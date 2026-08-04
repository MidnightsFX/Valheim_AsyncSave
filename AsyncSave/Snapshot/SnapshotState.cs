using System.Collections.Generic;
using System.Threading.Tasks;

namespace AsyncSave.Snapshot
{
    /// Single-instance mutable holder for the current snapshot. Reused across saves.
    internal static class SnapshotState
    {
        // Pooled - allocated once, grown as needed, cleared (not freed) each save.
        public static readonly SnapshotBuffer Buffer = new SnapshotBuffer(4 * 1024 * 1024);

        // A plain array rather than a List: List.Clear() memsets its backing store, and on a
        // five-million-ZDO world that is ~100 MB of stores on the trigger frame for a struct
        // that holds no references. Resetting is EntryCount = 0; nothing past the count is
        // ever read. Grown by GrowEntries, never shrunk - same policy as SnapshotBuffer.
        public static SnapshotEntry[] Entries = new SnapshotEntry[65536];
        public static int EntryCount;

        public static readonly List<ZDOID> PendingAdds = new List<ZDOID>(1024);
        public static readonly HashSet<ZDOID> PendingRemoves = new HashSet<ZDOID>(ZdoIdComparer.Instance);
        // ZDOs whose data changed after we serialized them, fed by ZdoRevisionPatches while
        // InProgress. Reconciliation walks this instead of re-checking the whole world: the
        // full scan cost 5.3 s of main thread on a 4.9 M-ZDO world to find 21 changes.
        public static readonly HashSet<ZDOID> PendingDirty = new HashSet<ZDOID>(ZdoIdComparer.Instance);
        // Dedup set for the walk. A ZDO that migrates sectors during a yield can appear in
        // both the old sector's scratch (already processed) and the new one's; Add returns
        // false on the second sighting. Add tests and inserts in a single probe, which is why
        // this is not a Dictionary - nothing ever needed the index value.
        public static readonly VisitedSet Visited = new VisitedSet(65536);
        // Per-sector scratch reused across sectors: refs to persistent ZDOs, snapshotted
        // atomically at sector start so mid-sector yields cannot desync from live mutations.
        public static readonly List<ZDO> SectorScratch = new List<ZDO>(4096);
        // Drain target for PendingDirty, same reason as SectorScratch: the revision patches
        // keep adding while reconciliation yields, and a live HashSet enumerator throws the
        // moment one does.
        public static readonly List<ZDOID> DirtyScratch = new List<ZDOID>(1024);

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

        /// Rolling estimate of the encoded size of one ZDO, used to size the buffer up front.
        /// Seeded just above the ~52 bytes measured on a 4.9 M-ZDO world - close enough that
        /// the first save rarely has to grow, low enough that the seed does not itself become
        /// the overshoot the presize exists to avoid. Survives across saves; the buffer never
        /// shrinks, so one generous allocation beats a doubling chain every time.
        public static float BytesPerZdoEma = 56f;

        /// Fold the last save's measurement into the estimate.
        public static void RecordBytesPerZdo(int bytes, int count)
        {
            if (count <= 0) return;
            float sample = (float)bytes / count;
            BytesPerZdoEma += (sample - BytesPerZdoEma) * 0.5f;
        }

        /// Current capacity of each pool. Sampled by SliceBudget on both sides of a block so a
        /// hitch can be attributed to a growth that happened inside it.
        public static void CurrentCaps(out int buffer, out int entries, out int visited)
        {
            buffer = Buffer.Data.Length;
            entries = Entries.Length;
            visited = Visited.Capacity;
        }

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

            // Everything below is O(1) or bounded by the number of mid-snapshot mutations.
            // It runs synchronously on the frame that triggers the save, so nothing here may
            // be proportional to world size.
            Buffer.Reset();
            EntryCount = 0;
            PendingAdds.Clear();
            PendingRemoves.Clear();
            PendingDirty.Clear();
            Visited.Clear();
            SectorScratch.Clear();
            DirtyScratch.Clear();
        }

        /// Size the entry array for an expected ZDO count. Only valid while EntryCount is 0:
        /// it discards the old array rather than copying, so the first save on a large world
        /// pays one allocation instead of a doubling chain whose last step copies a hundred
        /// megabytes on a single frame. GrowEntries stays as the backstop for undercounts.
        public static void EnsureEntryCapacity(int expected)
        {
            if (expected <= Entries.Length) return;
            int cap = Entries.Length;
            while (cap < expected && cap < (1 << 30)) cap <<= 1;
            Entries = new SnapshotEntry[cap];
        }

        /// Double the entry array, preserving the first `count` entries. Returns the new array
        /// so the caller can refresh its local copy of the reference.
        ///
        /// The save thread reads Entries after ReadyForWrite goes true; that flag is volatile
        /// and is written last, so the reference published here is visible by the time the
        /// writer can act on it.
        public static SnapshotEntry[] GrowEntries(int count)
        {
            var next = new SnapshotEntry[Entries.Length * 2];
            System.Array.Copy(Entries, next, count);
            Entries = next;
            return next;
        }
    }
}
