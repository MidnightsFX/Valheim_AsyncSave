using System.IO;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;

namespace AsyncSave.Patches
{
    /// Replace ZoneSystem.Save when we have a captured zone snapshot: build the package from
    /// ZoneSnapshot's pooled arrays instead of vanilla's m_temp*SaveClone collections.
    ///
    /// Byte layout mirrors ZoneSystem.Save exactly (see the decompiled source; the numbered
    /// comments below map to it). Runs on the save thread, which is also where vanilla does the
    /// compression - the win here is the capture, which replaced two rehashing clones and a list
    /// copy on the main thread.
    ///
    /// Three things moved with the chunked save format and are easy to get wrong: generated
    /// zones are Vector2s rather than Vector2i, the fixed zero field that used to precede the
    /// location version is gone, and a location's prefab is stored as its stable hash rather
    /// than its name.
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Save))]
    internal static class ZoneSavePatch
    {
        static bool Prefix(ZoneSystem __instance, BinaryWriter binaryWriter)
        {
#if DEBUG
            if (VerifyHarness.Bypass) return true;
#endif

            if (!ZoneSnapshot.Ready)
            {
                // Sync save or aborted snapshot - vanilla PrepareSave ran, so let it write.
                if (__instance.m_tempGeneratedZonesSaveClone == null)
                {
                    // Vanilla is about to NRE inside ZNet.SaveWorldThread's try/catch, which
                    // would surface as a generic "save failed". Name the real cause first.
                    Log.Error("AsyncSave: ZoneSystem.Save reached with neither an AsyncSave " +
                              "capture nor vanilla's PrepareSave clones. World zone data will not be saved.");
                }
                return true;
            }

            var pkg = new ZPackage();

            // 1. generated zones
            var zones = ZoneSnapshot.Zones;
            int zoneCount = ZoneSnapshot.ZoneCount;
            pkg.Write(zoneCount);
            for (int i = 0; i < zoneCount; ++i) pkg.Write(zones[i]);

            // 2. location version (vanilla reads this live too)
            pkg.Write(__instance.m_locationVersion);

            // 3. global keys, filtered exactly like vanilla's RemoveWhere: it *removes*
            //    entries whose key resolves below NonServerOption, so we keep the rest.
            //    Compacting in place preserves relative order, as RemoveWhere does.
            var srcKeys = ZoneSnapshot.GlobalKeys;
            int srcCount = ZoneSnapshot.GlobalKeyCount;
            var keptKeys = ZoneSnapshot.FilteredKeys;
            int keptCount = 0;
            for (int i = 0; i < srcCount; ++i)
            {
                string key = srcKeys[i];
                GlobalKeys gk;
                ZoneSystem.GetKeyValue(key, out string _, out gk);
                if (gk >= GlobalKeys.NonServerOption) keptKeys[keptCount++] = key;
            }
            pkg.Write(keptCount);
            for (int i = 0; i < keptCount; ++i) pkg.Write(keptKeys[i]);

            // 4. locations-generated flag, 5. location instances
            pkg.Write(ZoneSnapshot.LocationsGenerated);
            var locations = ZoneSnapshot.Locations;
            int locationCount = ZoneSnapshot.LocationCount;
            pkg.Write(locationCount);
            for (int i = 0; i < locationCount; ++i)
            {
                pkg.Write(locations[i].m_location.m_prefabName.GetStableHashCode());
                pkg.Write(locations[i].m_position.x);
                pkg.Write(locations[i].m_position.y);
                pkg.Write(locations[i].m_position.z);
                pkg.Write(locations[i].m_placed);
            }

            byte[] compressed = pkg.GetCompressed();
            binaryWriter.Write(compressed.Length);
            binaryWriter.Write(compressed);

            ZoneSnapshot.Ready = false;
            return false;
        }
    }
}
