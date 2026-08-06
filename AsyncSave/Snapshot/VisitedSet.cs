using System.Runtime.CompilerServices;

namespace AsyncSave.Snapshot
{
    /// Dedup set for the snapshot walk. Open addressing with linear probing, and a per-snapshot
    /// generation stamp in place of a clear.
    ///
    /// HashSet&lt;ZDOID&gt;.Clear() memsets both its slot and its bucket array. On a five-million-ZDO
    /// world that is ~120 MB of stores, on the frame that starts the save, for a key type that
    /// holds no references and therefore has nothing to release. Bumping a stamp makes every slot
    /// stale in O(1) instead. Folding the buckets into the slots also removes one dependent load
    /// per probe, which matters when the walk does five million of them.
    ///
    /// Dedup is not optional: a ZDO that appears twice in the save file makes ZDOMan.Load call
    /// Dictionary.Add with a duplicate key, which throws and takes the whole world with it.
    ///
    /// Each slot also carries the index of the entry the uid was written to. That is what lets
    /// dirty reconciliation find the record for a mutated ZDO without a second uid-&gt;index map:
    /// the probe that already had to happen for dedup answers both questions.
    ///
    /// The table is split into fixed-size segments rather than living in one array, for the same
    /// reason SnapshotBuffer is: a slot is 16 bytes, so a 48 M-ZDO world wants 2^27 of them and
    /// that is 2 GB in a single object - exactly the limit Mono will not allocate past. A segment
    /// is 4 Mi slots, i.e. 64 MB, matching the buffer's block size. Small tables live entirely in
    /// one short segment and take the same code path, since for any index below the segment size
    /// the shift is zero and the mask is the identity.
    internal sealed class VisitedSet
    {
        private struct Slot
        {
            public uint Stamp;
            public ZDOID Key;
            public int Index;
        }

        /// 4 Mi slots per segment: 64 MB at 16 bytes each. Power of two so the split is a shift
        /// and a mask rather than a division on every probe.
        private const int SegShift = 22;
        private const int SegSlots = 1 << SegShift;
        private const int SegMask = SegSlots - 1;

        /// Largest table we will build. 2^30 slots is 16 GB and 800 M ZDOs at the load factor
        /// below; the real point is that _mask stays a positive int.
        private const int MaxSlots = 1 << 30;

        private Slot[][] _segs;
        private int _mask;
        private int _count;
        private int _growAt;

        /// Current generation. Zero is reserved to mean "never written", so a freshly allocated
        /// array reads as empty without being touched.
        private uint _stamp;

        public VisitedSet(int capacity)
        {
            Release(capacity);
        }

        /// Build the table at `capacity` slots, discarding whatever was there. Used to construct
        /// the set, to presize it for a world, and - via SnapshotState.ReleasePools - to hand a
        /// world-sized table back when the world unloads.
        public void Release(int capacity)
        {
            int cap = 16;
            while (cap < capacity && cap < MaxSlots) cap <<= 1;
            Allocate(cap);
        }

        /// Live keys this generation. Nothing in the save path reads it - it exists so a test can
        /// assert the set neither dropped a key nor counted one twice, which is the failure mode
        /// that would put a duplicate ZDO in the file and make the world unloadable.
        public int Count => _count;

        /// Backing slot count.
        public int Capacity => _mask + 1;

        /// Invalidate every slot without touching memory.
        public void Clear()
        {
            _count = 0;
            if (++_stamp == 0)
            {
                // Wrapped - stale slots from generation 0xFFFFFFFF would read as live again.
                // Once every four billion saves; a real clear is fine here.
                for (int s = 0; s < _segs.Length; ++s) System.Array.Clear(_segs[s], 0, _segs[s].Length);
                _stamp = 1;
            }
        }

        /// Size the table for an expected element count. Only valid while the set is empty: it
        /// discards the backing segments instead of rehashing, which is the entire point. Growing
        /// organically from the seed capacity to five million keys is seven doublings, and the
        /// last of those rehashes three million live keys inside one frame; fresh segments of the
        /// right size cost an allocation of zero pages.
        public void EnsureCapacity(int expected)
        {
            // Sized so `expected` sits exactly at the 75% growth threshold - cap >= expected / 0.75
            // - rather than at some looser multiple. Because the result is then rounded up to a
            // power of two, a factor chosen with any slack to spare pushes straight past the next
            // boundary: 1.5x on a 48 M-ZDO world asks for 72 M slots, gets 134 M, and holds 2 GB
            // at 36% load. 4/3 asks for 64 M, gets 67 M, and holds half that at 72%.
            //
            // `expected` is the whole m_objectsByID count while only persistent ZDOs are ever
            // added, so there is real margin underneath this in practice. If a world does exceed
            // it - creations during a long walk - Grow still handles it correctly, and now that
            // the table is segmented that path allocates rather than failing outright.
            long need = (long)expected * 4 / 3 + 1024;
            if (need <= _mask + 1) return;

            long cap = _mask + 1;
            while (cap < need && cap < MaxSlots) cap <<= 1;
            Allocate((int)cap);
        }

        /// Record that `uid` was serialized to entry `index`. Returns true if the uid was not
        /// already present; false means it was a duplicate sighting and `index` is discarded.
        public bool Add(ZDOID uid, int index)
        {
            uint stamp = _stamp;
            int i = Hash(uid) & _mask;
            while (true)
            {
                var seg = _segs[i >> SegShift];
                int j = i & SegMask;
                if (seg[j].Stamp != stamp)
                {
                    seg[j].Stamp = stamp;
                    seg[j].Key = uid;
                    seg[j].Index = index;
                    if (++_count >= _growAt) Grow();
                    return true;
                }
                if (seg[j].Key == uid) return false;
                i = (i + 1) & _mask;
            }
        }

        /// Look up the entry index a uid was serialized to. False if it was never serialized -
        /// which is the normal answer for a ZDO created after the walk passed its sector.
        public bool TryGetIndex(ZDOID uid, out int index)
        {
            uint stamp = _stamp;
            int i = Hash(uid) & _mask;
            while (true)
            {
                var seg = _segs[i >> SegShift];
                int j = i & SegMask;
                if (seg[j].Stamp != stamp) { index = -1; return false; }
                if (seg[j].Key == uid) { index = seg[j].Index; return true; }
                i = (i + 1) & _mask;
            }
        }

        /// See ZdoIdComparer.Hash: ZDOID.GetHashCode indexes a static List&lt;long&gt; to turn
        /// UserKey back into a user id, which is a bounds check and a deref we do not need on
        /// every one of five million probes.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Hash(ZDOID uid) => ZdoIdComparer.Hash(uid);

        /// Lay out `cap` slots as segments. A table at or below one segment gets a single short
        /// segment, so a small world allocates no more than it did before segmenting.
        private void Allocate(int cap)
        {
            if (cap <= SegSlots)
            {
                _segs = new Slot[1][];
                _segs[0] = new Slot[cap];
            }
            else
            {
                _segs = new Slot[cap / SegSlots][];
                for (int s = 0; s < _segs.Length; ++s) _segs[s] = new Slot[SegSlots];
            }

            _mask = cap - 1;
            _growAt = cap - (cap >> 2);   // grow past 75% load
            _count = 0;
            _stamp = 1;
        }

        private void Grow()
        {
            int cap = _mask + 1;
            if (cap >= MaxSlots) return;   // nothing left to grow into; probes get slower, not wrong

            var old = _segs;
            uint stamp = _stamp;
            Allocate(cap << 1);

            // Allocate reset the stamp and the count; put them back before reinserting, so the
            // rehashed entries land live rather than being read as stale on the next probe.
            _stamp = stamp;
            int mask = _mask;
            var segs = _segs;
            int live = 0;

            for (int s = 0; s < old.Length; ++s)
            {
                var oldSeg = old[s];
                for (int k = 0; k < oldSeg.Length; ++k)
                {
                    if (oldSeg[k].Stamp != stamp) continue;
                    ZDOID key = oldSeg[k].Key;
                    int i = Hash(key) & mask;
                    while (segs[i >> SegShift][i & SegMask].Stamp == stamp) i = (i + 1) & mask;
                    var seg = segs[i >> SegShift];
                    int j = i & SegMask;
                    seg[j].Stamp = stamp;
                    seg[j].Key = key;
                    seg[j].Index = oldSeg[k].Index;
                    ++live;
                }
            }

            _count = live;
        }
    }
}
