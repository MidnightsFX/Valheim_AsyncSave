using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AsyncSave.Snapshot
{
    /// Per-frame time budget for the snapshot walk.
    ///
    /// The previous inline check called Stopwatch.ElapsedMilliseconds on every single ZDO:
    /// the object-count cap almost never trips first inside a 2 ms window, so the ||
    /// short-circuit never saved the clock read - and ElapsedMilliseconds is a
    /// QueryPerformanceCounter plus a 64-bit divide. This reads the raw timestamp and
    /// compares against a precomputed tick budget, and only every PollMask+1 objects.
    internal struct SliceBudget
    {
        /// Clock is read once per 32 objects. At the default 2 ms budget a slice covers
        /// thousands of objects, so the overshoot from polling coarsely is negligible while
        /// the saving is 32x.
        private const int PollMask = 31;

        private long _budgetTicks;
        private int _maxPerSlice;
        private int _inSlice;
        private long _sliceStart;
        private long _longestTicks;

        public static SliceBudget Create(int budgetMs, int maxPerSlice)
        {
            long ticks = (long)(Stopwatch.Frequency * (budgetMs / 1000.0));
            if (ticks < 1) ticks = 1;
            return new SliceBudget
            {
                _budgetTicks = ticks,
                _maxPerSlice = maxPerSlice,
                _inSlice = 0,
                _sliceStart = Stopwatch.GetTimestamp(),
                _longestTicks = 0,
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

        public void EndSlice()
        {
            long elapsed = Stopwatch.GetTimestamp() - _sliceStart;
            if (elapsed > _longestTicks) _longestTicks = elapsed;
        }

        public void BeginSlice()
        {
            _inSlice = 0;
            _sliceStart = Stopwatch.GetTimestamp();
        }

        public long LongestMs => _longestTicks * 1000L / Stopwatch.Frequency;
    }
}
