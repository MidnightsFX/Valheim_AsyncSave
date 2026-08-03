using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AsyncSave.Snapshot
{
    /// Per-frame time budget for the snapshot walk.
    ///
    /// The previous inline check called Stopwatch.ElapsedMilliseconds on every single ZDO:
    /// the object-count cap almost never trips first inside a 2 ms window, so the ||
    /// short-circuit never saved the clock read - and ElapsedMilliseconds is a
    /// QueryPerformanceCounter plus a 64-bit divide. This reads the raw timestamp and
    /// compares against a precomputed tick budget, and only every PollMask+1 objects.
    ///
    /// Callers must charge Step() once per element *examined*, never once per element that
    /// produced output. Charging only the productive branch is what let the reconcile pass
    /// run 4.9 M dictionary lookups inside one frame while still reporting a 2 ms slice: it
    /// mutated 25 of 4.9 M entries, so it called Step() 25 times, and Step only reads the
    /// clock every 32nd call.
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

        private long _budgetTicks;
        private int _maxPerSlice;
        private int _inSlice;
        private long _sliceStart;
        private long _worstTicks;
        private int _blocks;

        private bool _adaptive;
        private float _maxBudgetMs;
        private float _targetFrameMs;
        private float _baseFrameEmaMs;   // negative until the first sample
        private float _lastSliceMs;
        private float _ticksPerMs;

        public static SliceBudget Create(int budgetMs, int maxPerSlice, bool adaptive)
        {
            // Double math on purpose: verify mode passes int.MaxValue, and float would round
            // the product into something meaningless.
            long ticks = (long)(Stopwatch.Frequency * (budgetMs / 1000.0));
            if (ticks < 1) ticks = 1;

            return new SliceBudget
            {
                _budgetTicks = ticks,
                _maxPerSlice = maxPerSlice,
                _inSlice = 0,
                _sliceStart = Stopwatch.GetTimestamp(),
                _worstTicks = 0,
                _blocks = 0,
                _adaptive = adaptive,
                _maxBudgetMs = budgetMs,
                _targetFrameMs = DetectTargetFrameMs(),
                _baseFrameEmaMs = -1f,
                _lastSliceMs = 0f,
                _ticksPerMs = Stopwatch.Frequency / 1000f,
            };
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

                float budgetMs = _targetFrameMs - _baseFrameEmaMs;
                if (budgetMs > _maxBudgetMs) budgetMs = _maxBudgetMs;
                if (budgetMs < MinBudgetMs) budgetMs = MinBudgetMs;

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

        /// Frame time the adaptive budget is aiming at. Diagnostic only.
        public float TargetFrameMs => _targetFrameMs;

        /// The game's own FPS cap when it has one, otherwise the vsync interval,
        /// otherwise 60 Hz.
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
