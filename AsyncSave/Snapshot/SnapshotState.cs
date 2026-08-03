using System.Collections.Generic;
using System.Threading.Tasks;

namespace AsyncSave.Snapshot
{
    /// Single-instance mutable holder for the current snapshot. Reused across saves.
    internal static class SnapshotState
    {
        // Pooled - allocated once, grown as needed, cleared (not freed) each save.
        public static readonly SnapshotBuffer Buffer = new SnapshotBuffer(4 * 1024 * 1024);
        public static readonly List<SnapshotEntry> Entries = new List<SnapshotEntry>(65536);
        public static readonly List<ZDOID> PendingAdds = new List<ZDOID>(1024);
        public static readonly HashSet<ZDOID> PendingRemoves = new HashSet<ZDOID>(ZdoIdComparer.Instance);
        // Dedup set for the walk. A ZDO that migrates sectors during a yield can appear in
        // both the old sector's scratch (already processed) and the new one's; Add returns
        // false on the second sighting. HashSet.Add tests and inserts in a single probe,
        // which is why this is not a Dictionary - nothing ever needed the index value.
        public static readonly HashSet<ZDOID> Visited = new HashSet<ZDOID>(ZdoIdComparer.Instance);
        // Per-sector scratch reused across sectors: refs to persistent ZDOs, snapshotted
        // atomically at sector start so mid-sector yields cannot desync from live mutations.
        public static readonly List<ZDO> SectorScratch = new List<ZDO>(4096);

        /// True while the coroutine walks the world. Sector patches check this flag
        /// to append to PendingAdds / PendingRemoves; false = zero-cost pass-through.
        public static volatile bool InProgress;

        /// Set true at coroutine end (successful completion), false when SaveAsync
        /// consumes the snapshot or when Abort discards it. Guards the write path
        /// against reading partial / stale snapshot state.
        public static volatile bool ReadyForWrite;

        /// Session id captured at snapshot start (frozen for the writer).
        public static long SessionID;

        /// Next uid captured at snapshot start.
        public static uint NextUid;

        /// Signals SaveWorldThread to proceed. Set to a completed source at boot so
        /// the first vanilla save (if any) doesn't block.
        public static TaskCompletionSource<bool> Task = InitiallyCompleted();

        private static TaskCompletionSource<bool> InitiallyCompleted()
        {
            var t = new TaskCompletionSource<bool>();
            t.SetResult(true);
            return t;
        }

        /// Clear per-save state. Buffer and Entries retain their capacity.
        public static void ResetForNewSnapshot()
        {
            // Both ready flags must be cleared here, not just on consumption: if a save
            // thread ever dies before reaching ZDOMan.SaveAsync / ZoneSystem.SaveASync the
            // flag would otherwise survive into the next snapshot and expose a half-built
            // buffer to the writer.
            ReadyForWrite = false;
            ZoneSnapshot.Reset();

            Buffer.Reset();
            Entries.Clear();
            PendingAdds.Clear();
            PendingRemoves.Clear();
            Visited.Clear();
            SectorScratch.Clear();
        }
    }
}
