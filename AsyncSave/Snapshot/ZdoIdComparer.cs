using System.Collections.Generic;

namespace AsyncSave.Snapshot
{
    /// ZDOID.GetHashCode() is GetUserID(UserKey).GetHashCode() ^ ID.GetHashCode(), and
    /// GetUserID is an indexer into a static List&lt;long&gt; - a bounds check and a deref on
    /// every hash. The snapshot walk hashes a ZDOID at least once per ZDO, so on a
    /// hundred-thousand-ZDO world that indirection is worth removing from our own
    /// collections. (Vanilla's ZDOExtraData dictionaries are left alone - they are shared
    /// global state and their cost is dominated by cache misses, not hash arithmetic.)
    ///
    /// UserKey must stay in the hash: every peer's ZDOID counter starts near 1, so IDs
    /// collide heavily across users on a multiplayer server.
    internal sealed class ZdoIdComparer : IEqualityComparer<ZDOID>
    {
        public static readonly ZdoIdComparer Instance = new ZdoIdComparer();

        private ZdoIdComparer() { }

        public bool Equals(ZDOID a, ZDOID b) => a == b;

        public int GetHashCode(ZDOID id) => unchecked((int)id.ID * 397) ^ id.UserKey;
    }
}
