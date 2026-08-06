using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace AsyncSave.Snapshot
{
    /// Single-instance mutable holder for the current snapshot. Reused across saves.
    internal static class SnapshotState
    {
        // Seed sizes. The pools grow into a world and stay grown for the session; ReleasePools
        // returns them to these when the world is unloaded.
        private const int SeedBufferBytes = 4 * 1024 * 1024;
        private const int SeedEntries = 65536;
        private const int SeedVisited = 65536;

        // Pooled - allocated once, grown as needed, cleared (not freed) each save.
        public static readonly SnapshotBuffer Buffer = new SnapshotBuffer(SeedBufferBytes);

        // A plain array rather than a List: List.Clear() memsets its backing store, and on a
        // five-million-ZDO world that is ~100 MB of stores on the trigger frame for a struct
        // that holds no references. Resetting is EntryCount = 0; nothing past the count is
        // ever read. Grown by GrowEntries and only handed back by ReleasePools.
        public static SnapshotEntry[] Entries = new SnapshotEntry[SeedEntries];
        public static int EntryCount;

        public static readonly List<ZDOID> PendingAdds = new List<ZDOID>(1024);
        public static readonly HashSet<ZDOID> PendingRemoves = new HashSet<ZDOID>(ZdoIdComparer.Instance);
        // ZDOs whose data changed after we serialized them, fed by ZdoRevisionPatches while
        // InProgress. Reconciliation walks this instead of re-checking the whole world: the
        // full scan cost 5.3 s of main thread on a 4.9 M-ZDO world to find 21 changes.
        public static readonly HashSet<ZDOID> PendingDirty = new HashSet<ZDOID>(ZdoIdComparer.Instance);
        // Dedup set for the walk. A ZDO that migrates sectors during a yield can appear in
        // both the old sector's scratch (already processed) and the new one's; Add returns
        // false on the second sighting. Not a Dictionary because the one probe that dedup needs
        // also returns the entry index, which is what dirty reconciliation looks records up by.
        public static readonly VisitedSet Visited = new VisitedSet(SeedVisited);
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
        public static TaskCompletionSource<bool> Task = CompletedSource();

        /// Rolling estimate of the encoded size of one ZDO, used to size the buffer up front.
        /// Seeded just above the ~52 bytes measured on a 4.9 M-ZDO world - close enough that
        /// the first save rarely has to add a block, low enough that the seed does not itself
        /// become the overshoot the presize exists to avoid. Survives across saves.
        public static float BytesPerZdoEma = 56f;

        /// Fold the last save's measurement into the estimate.
        public static void RecordBytesPerZdo(long bytes, int count)
        {
            if (count <= 0) return;
            float sample = (float)((double)bytes / count);
            BytesPerZdoEma += (sample - BytesPerZdoEma) * 0.5f;
        }

        /// Publish an already-completed gate, so a SaveWorldThread that is about to park on it
        /// proceeds straight down the untouched vanilla path. Used at boot, when the world-save
        /// half is switched off, and when a forced-sync save preempts a snapshot.
        public static void SignalNoSnapshot() => Task = CompletedSource();

        private static TaskCompletionSource<bool> CompletedSource()
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

        /// Hand the pools back when a world is unloaded.
        ///
        /// Growing and staying grown is the right policy across the saves of one session. It is
        /// the wrong policy across worlds: on a 48 M-ZDO world these three hold nearly five
        /// gigabytes between them, and without this the player carries all of it for the rest of
        /// the process because they once loaded a big save. Called from StopAllPatch, after the
        /// final save has been written.
        public static void ReleasePools()
        {
            Buffer.Release(SeedBufferBytes);
            Entries = new SnapshotEntry[SeedEntries];
            EntryCount = 0;
            Visited.Release(SeedVisited);
            ZoneSnapshot.ReleasePools();

            PendingAdds.Clear();
            PendingAdds.TrimExcess();
            PendingRemoves.Clear();
            PendingDirty.Clear();
            SectorScratch.Clear();
            SectorScratch.TrimExcess();
            DirtyScratch.Clear();
            DirtyScratch.TrimExcess();
        }

        /// Most entries one array can hold. SnapshotEntry is 24 bytes and a .NET object cannot
        /// exceed 2 GB, so this is the last size ceiling left in the snapshot - the buffer and
        /// the dedup table are both segmented and no longer have one. It works out around 89 M
        /// ZDOs, comfortably past the largest world anyone has reported. Segment Entries the same
        /// way as the other two if that ever stops being true.
        private const int MaxEntries = int.MaxValue / 24;

        /// Size the entry array for an expected ZDO count. Only valid while EntryCount is 0: it
        /// discards the old array rather than copying, so the first save on a large world pays one
        /// allocation instead of a doubling chain whose last step copies a hundred megabytes on a
        /// single frame. GrowEntries stays as the backstop for undercounts.
        ///
        /// Sized to the request, not rounded up to a power of two. `expected` is the whole
        /// m_objectsByID count and only persistent ZDOs get entries, so it already overshoots;
        /// rounding on top of that cost 460 MB of untouched array on a 48 M-ZDO world and brought
        /// the next doubling - 3.2 GB, unallocatable - within reach of a world half that size.
        public static void EnsureEntryCapacity(int expected)
        {
            if (expected <= Entries.Length) return;
            Entries = new SnapshotEntry[expected > MaxEntries ? MaxEntries : expected];
        }

        /// Grow the entry array, preserving the first `count` entries. Returns the new array so
        /// the caller can refresh its local copy of the reference.
        ///
        /// The save thread reads Entries after ReadyForWrite goes true; that flag is volatile and
        /// is written last, so the reference published here is visible by the time the writer can
        /// act on it.
        public static SnapshotEntry[] GrowEntries(int count)
        {
            // Half again rather than double: past a gigabyte, doubling is the difference between
            // an allocation that succeeds and one that cannot exist. EnsureEntryCapacity means
            // this normally never runs at all - it covers ZDOs created or migrated mid-snapshot.
            long want = (long)Entries.Length + (Entries.Length >> 1) + 1024;
            if (want > MaxEntries) want = MaxEntries;

            if (want <= count)
            {
                throw new System.InvalidOperationException(
                    $"AsyncSave: the snapshot needs more than {MaxEntries} ZDO entries, which is as " +
                    "many as one array can hold. Set 'World save' to false to use the vanilla save.");
            }

            var next = new SnapshotEntry[want];
            System.Array.Copy(Entries, next, count);
            Entries = next;
            return next;
        }
    }

    /// One serialized ZDO in the snapshot buffer. Struct for zero heap overhead; stored in a
    /// pooled array.
    ///
    /// Block plus Offset rather than one absolute position: the buffer is a list of blocks whose
    /// first one may be a different size from the rest, so there is no cheap absolute address.
    /// The pair is also what the write path wants - it hands (block, offset, length) straight to
    /// BinaryWriter.Write. Sits in 24 bytes, the same as before Block was added, because ZDOID is
    /// 6 bytes and the struct was already padded out to that.
    internal struct SnapshotEntry
    {
        public ZDOID Uid;
        public int Block;
        public int Offset;
        public int Length;
        public uint DataRevision;
    }

    /// ZDOID.GetHashCode() is GetUserID(UserKey).GetHashCode() ^ ID.GetHashCode(), and GetUserID
    /// is an indexer into a static List&lt;long&gt; - a bounds check and a deref on every hash. The
    /// snapshot walk hashes a ZDOID at least once per ZDO, so on a five-million-ZDO world that
    /// indirection is worth removing from our own collections. (Vanilla's ZDOExtraData dictionaries
    /// are left alone - they are shared global state and their cost is dominated by cache misses,
    /// not hash arithmetic.)
    ///
    /// UserKey must stay in the hash: every peer's ZDOID counter starts near 1, so IDs collide
    /// heavily across users on a multiplayer server.
    internal sealed class ZdoIdComparer : IEqualityComparer<ZDOID>
    {
        public static readonly ZdoIdComparer Instance = new ZdoIdComparer();

        private ZdoIdComparer() { }

        public bool Equals(ZDOID a, ZDOID b) => a == b;

        public int GetHashCode(ZDOID id) => Hash(id);

        /// Shared with VisitedSet, which cannot go through the comparer interface without paying
        /// for a virtual call on every probe. ID is sequential and 397 is odd, so consecutive ids
        /// land far apart and linear probing does not cluster.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Hash(ZDOID id) => unchecked((int)id.ID * 397) ^ id.UserKey;
    }
}
