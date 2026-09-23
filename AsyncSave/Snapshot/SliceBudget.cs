using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AsyncSave.Snapshot
{
    /// Per-frame time budget for the snapshot walk.
    ///
    /// The clock is read from the raw timestamp and compared against a precomputed tick budget,
    /// and only every PollMask+1 objects. The obvious version - Stopwatch.ElapsedMilliseconds on
    /// every ZDO - is a QueryPerformanceCounter plus a 64-bit divide, per object, five million
    /// times over.
    ///
    /// Callers must charge Step() once per element *examined*, never once per element that
    /// produced output. Charging only the productive branch is what let the reconcile pass run
    /// 4.9 M dictionary lookups inside one frame while still reporting a 2 ms slice: it mutated
    /// 25 of 4.9 M entries, so it called Step() 25 times, and Step only reads the clock every
    /// 32nd call.
    internal struct SliceBudget
    {
        /// Clock is read once per 32 objects. At the default 2 ms budget a slice covers
        /// thousands of objects, so the overshoot from polling coarsely is negligible while
        /// the saving is 32x.
        private const int PollMask = 31;

        /// Weight of the newest frame sample in the base-frame-cost EMA.
        private const float Alpha = 0.25f;

        /// Floor on the adaptive budget. A pure clamp(target - frameCost, 0, max) sits at
        /// zero forever on a machine that never reaches its target framerate - and the save
        /// thread is parked on this coroutine, so a snapshot that never finishes is a save
        /// that never happens. The floor bounds the worst case instead: slow, but finite.
        private const float MinBudgetMs = 0.25f;

        private const float DefaultTargetFrameMs = 1000f / 60f;

        /// Safety valve: hard cap on objects examined per slice, whatever the clock says. Only
        /// meant to bound a slice if the timer misbehaves - a ZDO costs a few hundred nanoseconds,
        /// so at any sane budget the clock trips long before this. Was a config setting until it
        /// became clear that any value low enough to matter only wasted budget and stretched the
        /// save out.
        private const int MaxPerSlice = 50000;

        /// Set while a logout or quit is being held for the world save (see FinalSave). Nobody is
        /// playing through that save - the player asked to leave and is waiting on it - so frame
        /// pacing stops mattering and the only thing still worth protecting is the window's
        /// message pump. Each slice then gets RushBudgetMs, with the adaptive rule and the
        /// per-slice cap stood down: at a few hundred nanoseconds a ZDO the cap would trip at
        /// well under half the rush budget.
        ///
        /// Checked at the start of every slice, not just at Create, so it also hurries a walk
        /// that was already running at autosave pace when the logout arrived. Latches for the
        /// rest of that walk.
        public static bool Rush;

        /// Most of each frame goes to the walk while the window still pumps well over ten times a
        /// second - nowhere near the seconds Windows waits before calling a window unresponsive.
        private const float RushBudgetMs = 50f;

        private long _budgetTicks;
        private int _maxPerSlice;
        private bool _unsliced;
        private bool _rushed;
        private int _inSlice;
        private long _sliceStart;
        private long _worstTicks;
        private int _blocks;

        private bool _adaptive;
        private float _maxBudgetMs;
        private float _headroom;
        private float _capTargetMs;        // explicit fps cap / vsync interval, else 60 Hz
        private float _targetFrameMs;      // effective target after the proportional rule
        private float _baseFrameEmaMs;     // negative until the first sample
        private float _lastSliceMs;
        private float _lastBudgetMs;
        private float _ticksPerMs;

        /// `unsliced` forces the whole walk into one un-yielded block: byte-identity is only a
        /// well-defined property when nothing can mutate mid-walk, so the verify harness needs
        /// the budget - and the adaptive rule - to stand down entirely.
        public static SliceBudget Create(int budgetMs, bool adaptive, float headroom, bool unsliced)
        {
            // Double math on purpose: float would round the product into something meaningless.
            long ticks = unsliced ? long.MaxValue : (long)(Stopwatch.Frequency * (budgetMs / 1000.0));
            if (ticks < 1) ticks = 1;

            float capTarget = DetectTargetFrameMs();

            var slice = new SliceBudget
            {
                _budgetTicks = ticks,
                _maxPerSlice = unsliced ? int.MaxValue : MaxPerSlice,
                _unsliced = unsliced,
                _sliceStart = Stopwatch.GetTimestamp(),
                _adaptive = adaptive && !unsliced,
                _maxBudgetMs = budgetMs,
                _headroom = headroom,
                _capTargetMs = capTarget,
                _targetFrameMs = capTarget,
                _baseFrameEmaMs = -1f,
                _lastBudgetMs = budgetMs,
                _ticksPerMs = Stopwatch.Frequency / 1000f,
            };
            if (Rush) slice.ApplyRush();
            return slice;
        }

        /// Switch to the rush budget for the rest of this walk. An unsliced walk already has no
        /// budget to raise.
        private void ApplyRush()
        {
            if (_unsliced || _rushed) return;
            _rushed = true;
            _adaptive = false;
            _maxPerSlice = int.MaxValue;
            _budgetTicks = (long)(RushBudgetMs * _ticksPerMs);
            _lastBudgetMs = RushBudgetMs;
        }

        /// Count one processed object. Returns true when the current slice should end.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Step()
        {
            int n = ++_inSlice;
            if (n >= _maxPerSlice) return true;
            if ((n & PollMask) != 0) return false;
            return Stopwatch.GetTimestamp() - _sliceStart >= _budgetTicks;
        }

        /// Close the current main-thread block. Must be called before every yield *and* once
        /// when the coroutine finishes, so a phase that never yields still gets recorded.
        public void EndSlice()
        {
            long elapsed = Stopwatch.GetTimestamp() - _sliceStart;
            if (elapsed > _worstTicks) _worstTicks = elapsed;
            _lastSliceMs = elapsed / _ticksPerMs;
            ++_blocks;
        }

        public void BeginSlice()
        {
            if (Rush) ApplyRush();

            if (_adaptive)
            {
                // Time.unscaledDeltaTime, read from a coroutine that just resumed, is the
                // duration of the frame that ended - the one our previous slice ran in.
                // Subtract that slice back out so the EMA tracks what a frame costs *without*
                // us. Budgeting against the raw frame time makes the loop fight itself: it
                // backs off because of the very work it is trying to schedule, then ramps up
                // again the moment it stops, and oscillates instead of settling.
                float frameMs = Time.unscaledDeltaTime * 1000f;
                float baseMs = frameMs - _lastSliceMs;
                if (baseMs < 0f) baseMs = 0f;
                _baseFrameEmaMs = _baseFrameEmaMs < 0f
                    ? baseMs
                    : _baseFrameEmaMs + (baseMs - _baseFrameEmaMs) * Alpha;

                // The target is proportional to what a frame actually costs, not a flat 60 Hz.
                // A flat target is only a throttle on a machine that is already near it: at a
                // 4.3 ms base frame it authorises a 12.4 ms slice, i.e. a 4x frame-time
                // increase, and the only thing that ever held that back was the config
                // ceiling. Proportional keeps the cost felt-relative on every machine, and the
                // detected cap stays as an upper bound so we never authorise dropping below
                // the framerate the player asked for.
                float target = _baseFrameEmaMs * (1f + _headroom);
                if (target > _capTargetMs) target = _capTargetMs;
                _targetFrameMs = target;

                float budgetMs = target - _baseFrameEmaMs;
                if (budgetMs > _maxBudgetMs) budgetMs = _maxBudgetMs;
                if (budgetMs < MinBudgetMs) budgetMs = MinBudgetMs;

                _lastBudgetMs = budgetMs;
                _budgetTicks = (long)(budgetMs * _ticksPerMs);
                if (_budgetTicks < 1) _budgetTicks = 1;
            }

            _inSlice = 0;
            _sliceStart = Stopwatch.GetTimestamp();
        }

        /// Longest uninterrupted main-thread block, across every phase - including phases
        /// that never yielded. This is the number that matters for frame pacing.
        public float WorstBlockMs => _worstTicks / _ticksPerMs;

        /// Number of closed blocks, i.e. how many frames the coroutine spanned.
        public int Blocks => _blocks;

        /// Smoothed cost of a frame excluding our own slice. Diagnostic only.
        public float BaseFrameMs => _baseFrameEmaMs < 0f ? 0f : _baseFrameEmaMs;

        /// Frame time the adaptive budget is currently aiming at. Diagnostic only.
        public float TargetFrameMs => _targetFrameMs;

        /// The explicit fps cap / vsync interval the target is bounded by. Diagnostic only.
        public float CapTargetMs => _capTargetMs;

        /// Budget granted to the most recent slice. Diagnostic only.
        public float LastBudgetMs => _lastBudgetMs;

        /// The game's own FPS cap when it has one, otherwise the vsync interval,
        /// otherwise 60 Hz. This is a ceiling on the target, not the target itself -
        /// BeginSlice narrows it to something proportional to the measured frame cost.
        private static float DetectTargetFrameMs()
        {
            int fps = Application.targetFrameRate;
            if (fps > 0) return 1000f / fps;

            int vsync = QualitySettings.vSyncCount;
            if (vsync > 0)
            {
                double hz = Screen.currentResolution.refreshRateRatio.value;
                if (hz > 1.0) return (float)(1000.0 * vsync / hz);
            }

            return DefaultTargetFrameMs;
        }
    }
}
