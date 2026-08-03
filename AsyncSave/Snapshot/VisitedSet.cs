using System.Runtime.CompilerServices;

namespace AsyncSave.Snapshot
{
    /// Dedup set for the snapshot walk. Open addressing with linear probing over a single
    /// array, and a per-snapshot generation stamp in place of a clear.
    ///
    /// HashSet&lt;ZDOID&gt;.Clear() memsets both its slot and its bucket array. On a five-million-ZDO
    /// world that is ~120 MB of stores, on the frame that starts the save, for a key type that
    /// holds no references and therefore has nothing to release. Bumping a stamp makes every
    /// slot stale in O(1) instead. Folding the buckets into the slots also removes one
    /// dependent load per probe, which matters when the walk does five million of them.
    ///
    /// Dedup is not optional: a ZDO that appears twice in the save file makes ZDOMan.Load call
    /// Dictionary.Add with a duplicate key, which throws and takes the whole world with it.
    internal sealed class VisitedSet
    {
        private struct Slot
        {
            public uint Stamp;
            public ZDOID Key;
        }

        private Slot[] _slots;
        private int _mask;
        private int _count;
        private int _growAt;

        /// Current generation. Zero is reserved to mean "never written", so a freshly
        /// allocated array reads as empty without being touched.
        private uint _stamp;

        public VisitedSet(int capacity)
        {
            int cap = 16;
            while (cap < capacity) cap <<= 1;
            _slots = new Slot[cap];
            _mask = cap - 1;
            _growAt = cap - (cap >> 2);   // grow past 75% load
            _stamp = 1;
        }

        public int Count => _count;

        /// Invalidate every slot without touching memory.
        public void Clear()
        {
            _count = 0;
            if (++_stamp == 0)
            {
                // Wrapped - stale slots from generation 0xFFFFFFFF would read as live again.
                // Once every four billion saves; a real clear is fine here.
                System.Array.Clear(_slots, 0, _slots.Length);
                _stamp = 1;
            }
        }

        /// Size the table for an expected element count. Only valid while the set is empty:
        /// it discards the backing array instead of rehashing, which is the entire point.
        /// Growing organically from the seed capacity to five million keys is seven
        /// doublings, and the last of those rehashes three million live keys inside one
        /// frame; a fresh array of the right size costs an allocation of zero pages.
        public void EnsureCapacity(int expected)
        {
            int need = expected + (expected >> 1);   // stay clear of the 75% growth threshold
            if (need <= _slots.Length) return;

            int cap = _slots.Length;
            while (cap < need && cap < (1 << 30)) cap <<= 1;

            _slots = new Slot[cap];
            _mask = cap - 1;
            _growAt = cap - (cap >> 2);
            _count = 0;
            _stamp = 1;
        }

        /// Returns true if the uid was not already present.
        public bool Add(ZDOID uid)
        {
            uint stamp = _stamp;
            var slots = _slots;
            int i = Hash(uid) & _mask;
            while (true)
            {
                if (slots[i].Stamp != stamp)
                {
                    slots[i].Stamp = stamp;
                    slots[i].Key = uid;
                    if (++_count >= _growAt) Grow();
                    return true;
                }
                if (slots[i].Key == uid) return false;
                i = (i + 1) & _mask;
            }
        }

        /// Same mix as ZdoIdComparer: ZDOID.GetHashCode indexes a static List&lt;long&gt; to turn
        /// UserKey back into a user id, which is a bounds check and a deref we do not need.
        /// UserKey has to stay in the hash - every peer's ZDOID counter starts near 1, so raw
        /// IDs collide heavily across users on a multiplayer server. ID is sequential and 397
        /// is odd, so consecutive ids land far apart and linear probing does not cluster.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Hash(ZDOID uid) => unchecked((int)uid.ID * 397) ^ uid.UserKey;

        private void Grow()
        {
            var old = _slots;
            uint stamp = _stamp;
            int cap = old.Length << 1;
            int mask = cap - 1;
            var next = new Slot[cap];

            for (int j = 0; j < old.Length; ++j)
            {
                if (old[j].Stamp != stamp) continue;
                ZDOID key = old[j].Key;
                int i = Hash(key) & mask;
                while (next[i].Stamp == stamp) i = (i + 1) & mask;
                next[i].Stamp = stamp;
                next[i].Key = key;
            }

            _slots = next;
            _mask = mask;
            _growAt = cap - (cap >> 2);
        }
    }
}
