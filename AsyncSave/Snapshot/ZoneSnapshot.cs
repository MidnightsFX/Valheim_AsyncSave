using UnityEngine;

namespace AsyncSave.Snapshot
{
    /// Allocation-free stand-in for ZoneSystem.PrepareSave's three clones.
    ///
    /// Vanilla builds a new HashSet&lt;Vector2i&gt; and a new List&lt;LocationInstance&gt; on every
    /// save. On a mature world that is a full rehash of every generated zone plus a
    /// multi-megabyte copy of every location instance, all on the main thread in one frame.
    /// We copy into pooled arrays instead: HashSet/Dictionary CopyTo are linear slot walks
    /// with no hashing and, once the arrays have grown to world size, no allocation.
    ///
    /// Enumeration order is preserved, which is what keeps the output byte-identical:
    /// vanilla's clone is built by sequential Add over the source's slot order, and CopyTo
    /// walks those same slots in the same order.
    ///
    /// Arrays grow monotonically within a session and are handed back by ReleasePools when
    /// the world unloads - same policy as the other snapshot pools.
    internal static class ZoneSnapshot
    {
        public static Vector2i[] Zones = new Vector2i[16384];
        public static int ZoneCount;

        public static ZoneSystem.LocationInstance[] Locations = new ZoneSystem.LocationInstance[8192];
        public static int LocationCount;

        public static string[] GlobalKeys = new string[64];
        public static int GlobalKeyCount;

        /// Scratch for the save-thread equivalent of vanilla's RemoveWhere filter. Kept
        /// separate from GlobalKeys so the capture stays intact and the write is repeatable
        /// (the verify harness runs the write twice).
        public static string[] FilteredKeys = new string[64];

        public static bool LocationsGenerated;

        /// Deliberately separate from SnapshotState.ReadyForWrite. ZoneSystem.SaveASync runs
        /// after ZDOMan.SaveAsync (ZNet.SaveWorldThread), and that one clears its own flag on
        /// the way out - sharing a flag would drop the zone write into the vanilla path with
        /// null m_temp*SaveClone fields and NRE inside vanilla's try/catch.
        public static volatile bool Ready;

        /// Main thread, no yields - the copy must be atomic with respect to zone generation.
        public static void Capture(ZoneSystem zs)
        {
            var zones = zs.m_generatedZones;
            Grow(ref Zones, zones.Count);
            zones.CopyTo(Zones, 0);
            ZoneCount = zones.Count;

            // LocationInstance is a struct holding a reference to a shared ZoneLocation, so
            // this copy aliases the same ZoneLocation objects the save thread will read
            // m_prefabName from. Vanilla's List clone aliases them identically - no new
            // cross-thread exposure.
            var locations = zs.m_locationInstances;
            Grow(ref Locations, locations.Count);
            locations.Values.CopyTo(Locations, 0);
            LocationCount = locations.Count;

            var keys = zs.m_globalKeys;
            Grow(ref GlobalKeys, keys.Count);
            keys.CopyTo(GlobalKeys, 0);
            GlobalKeyCount = keys.Count;
            Grow(ref FilteredKeys, keys.Count);

            LocationsGenerated = zs.LocationsGenerated;
        }

        public static void Reset()
        {
            Ready = false;
        }

        /// Drop the arrays back to seed size when a world unloads - see SnapshotState.ReleasePools.
        public static void ReleasePools()
        {
            Ready = false;
            Zones = new Vector2i[16384];
            ZoneCount = 0;
            Locations = new ZoneSystem.LocationInstance[8192];
            LocationCount = 0;
            GlobalKeys = new string[64];
            GlobalKeyCount = 0;
            FilteredKeys = new string[64];
        }

        private static void Grow<T>(ref T[] arr, int need)
        {
            if (arr.Length >= need) return;
            int cap = arr.Length < 16 ? 16 : arr.Length;
            while (cap < need) cap *= 2;
            arr = new T[cap];
        }
    }
}
