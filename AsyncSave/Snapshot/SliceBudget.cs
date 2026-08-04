using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace AsyncSave.Snapshot
{
    /// Which part of the walk a main-thread block belonged to. Recorded per hitch so a spike
    /// can be attributed to a phase instead of just to "the save".
    internal enum SlicePhase : byte
    {
        Presize,
        SectorWalk,
        OutsideSector,
        Reconcile,
        Adds,
        Tail,
    }

    /// One main-thread block that ran past the hitch threshold.
    internal struct HitchRecord
    {
        public int Block;
        public SlicePhase Phase;
        public float Ms;
        public int Gen0;
        public int Gen1;
        public int Gen2;
        public int BufferCapBefore;
        public int BufferCapAfter;
        public int EntriesCapBefore;
        public int EntriesCapAfter;
        public int VisitedCapBefore;
        public int VisitedCapAfter;
    }

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

        /// How many of the worst blocks are kept. Insertion is O(HitchSlots) but only runs
        /// for blocks that already exceeded the threshold, which should be a handful per save.
        private const int HitchSlots = 8;

        private long _budgetTicks;
        private int _maxPerSlice;
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

        private SlicePhase _phase;
        private long _hitchTicks;          // 0 disables hitch tracking
        private HitchRecord[] _hitches;
        private int _hitchesKept;
        private int _hitchTotal;
        private int _gen0Before, _gen1Before, _gen2Before;
        private int _bufCapBefore, _entCapBefore, _visCapBefore;

        public static SliceBudget Create(int budgetMs, int maxPerSlice, bool adaptive,
                                         float headroom, int hitchMs)
        {
            // Double math on purpose: verify mode passes int.MaxValue, and float would round
            // the product into something meaningless.
            long ticks = (long)(Stopwatch.Frequency * (budgetMs / 1000.0));
            if (ticks < 1) ticks = 1;

            float capTarget = DetectTargetFrameMs();

            var b = new SliceBudget
            {
                _budgetTicks = ticks,
                _maxPerSlice = maxPerSlice,
                _inSlice = 0,
                _sliceStart = Stopwatch.GetTimestamp(),
                _worstTicks = 0,
                _blocks = 0,
                _adaptive = adaptive,
                _maxBudgetMs = budgetMs,
                _headroom = headroom,
                _capTargetMs = capTarget,
                _targetFrameMs = capTarget,
                _baseFrameEmaMs = -1f,
                _lastSliceMs = 0f,
                _lastBudgetMs = budgetMs,
                _ticksPerMs = Stopwatch.Frequency / 1000f,
                _phase = SlicePhase.Presize,
                _hitchTicks = 0,
                _hitches = null,
                _hitchesKept = 0,
                _hitchTotal = 0,
            };

            if (hitchMs > 0)
            {
                b._hitchTicks = (long)(Stopwatch.Frequency * (hitchMs / 1000.0));
                if (b._hitchTicks < 1) b._hitchTicks = 1;
                b._hitches = new HitchRecord[HitchSlots];
                b.SampleBlockStart();
            }

            return b;
        }

        /// Tag the blocks that follow. Recorded on every hitch so a spike names its phase.
        public void SetPhase(SlicePhase phase) => _phase = phase;

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

            if (_hitchTicks != 0 && elapsed >= _hitchTicks) RecordHitch(elapsed);
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
            if (_hitchTicks != 0) SampleBlockStart();
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

        /// How many blocks ran past the hitch threshold.
        public int HitchCount => _hitchTotal;

        /// One line per kept hitch, worst first. Empty when hitch tracking is off or clean.
        public string FormatHitches()
        {
            if (_hitches == null || _hitchesKept == 0) return null;

            var sb = new StringBuilder();
            for (int i = 0; i < _hitchesKept; ++i)
            {
                var h = _hitches[i];
                sb.Append("\n  ").Append(h.Ms.ToString("0.00")).Append("ms block#").Append(h.Block)
                  .Append(' ').Append(h.Phase.ToString());

                if (h.Gen0 != 0 || h.Gen1 != 0 || h.Gen2 != 0)
                    sb.Append(" gc=").Append(h.Gen0).Append('/').Append(h.Gen1).Append('/').Append(h.Gen2);

                if (h.BufferCapBefore != h.BufferCapAfter)
                    sb.Append(" bufferGrew=").Append(h.BufferCapBefore).Append("->").Append(h.BufferCapAfter);
                if (h.EntriesCapBefore != h.EntriesCapAfter)
                    sb.Append(" entriesGrew=").Append(h.EntriesCapBefore).Append("->").Append(h.EntriesCapAfter);
                if (h.VisitedCapBefore != h.VisitedCapAfter)
                    sb.Append(" visitedGrew=").Append(h.VisitedCapBefore).Append("->").Append(h.VisitedCapAfter);

                // Nothing grew and no collection ran: the block was our own work overrunning
                // the budget, which points at Step() granularity or one very expensive element.
                if (h.Gen0 == 0 && h.Gen1 == 0 && h.Gen2 == 0 &&
                    h.BufferCapBefore == h.BufferCapAfter &&
                    h.EntriesCapBefore == h.EntriesCapAfter &&
                    h.VisitedCapBefore == h.VisitedCapAfter)
                    sb.Append(" (no gc, no growth)");
            }
            return sb.ToString();
        }

        private void SampleBlockStart()
        {
            _gen0Before = GC.CollectionCount(0);
            _gen1Before = GC.CollectionCount(1);
            _gen2Before = GC.CollectionCount(2);
            SnapshotState.CurrentCaps(out _bufCapBefore, out _entCapBefore, out _visCapBefore);
        }

        private void RecordHitch(long elapsed)
        {
            ++_hitchTotal;

            var rec = new HitchRecord
            {
                Block = _blocks,
                Phase = _phase,
                Ms = elapsed / _ticksPerMs,
                Gen0 = GC.CollectionCount(0) - _gen0Before,
                Gen1 = GC.CollectionCount(1) - _gen1Before,
                Gen2 = GC.CollectionCount(2) - _gen2Before,
                BufferCapBefore = _bufCapBefore,
                EntriesCapBefore = _entCapBefore,
                VisitedCapBefore = _visCapBefore,
            };
            SnapshotState.CurrentCaps(out rec.BufferCapAfter, out rec.EntriesCapAfter, out rec.VisitedCapAfter);

            // Keep the worst HitchSlots, sorted descending. A ring of the *first* N would fill
            // up with the startup allocations and then never see the spike we are hunting.
            if (_hitchesKept < HitchSlots) ++_hitchesKept;
            else if (rec.Ms <= _hitches[HitchSlots - 1].Ms) return;

            int i = _hitchesKept - 1;
            while (i > 0 && _hitches[i - 1].Ms < rec.Ms)
            {
                _hitches[i] = _hitches[i - 1];
                --i;
            }
            _hitches[i] = rec;
        }

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
