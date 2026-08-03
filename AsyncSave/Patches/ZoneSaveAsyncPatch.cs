using System.IO;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;

namespace AsyncSave.Patches
{
    /// Replace ZoneSystem.SaveASync when we have a captured zone snapshot: write straight
    /// from ZoneSnapshot's pooled arrays instead of vanilla's m_temp*SaveClone collections.
    ///
    /// Byte layout mirrors ZoneSystem.SaveASync exactly (see the decompiled source, the
    /// numbered comments below map to it). Runs on the save thread.
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SaveASync))]
    internal static class ZoneSaveAsyncPatch
    {
        static bool Prefix(ZoneSystem __instance, BinaryWriter writer)
        {
            if (VerifyHarness.Bypass) return true;

            if (!ZoneSnapshot.Ready)
            {
                // Sync save or aborted snapshot - vanilla PrepareSave ran, so let it write.
                if (__instance.m_tempGeneratedZonesSaveClone == null)
                {
                    // Vanilla is about to NRE inside ZNet.SaveWorldThread's try/catch, which
                    // would surface as a generic "save failed". Name the real cause first.
                    Log.Error("AsyncSave: ZoneSystem.SaveASync reached with neither an AsyncSave " +
                              "capture nor vanilla's PrepareSave clones. World zone data will not be saved.");
                }
                return true;
            }

            // 1. generated zones
            var zones = ZoneSnapshot.Zones;
            int zoneCount = ZoneSnapshot.ZoneCount;
            writer.Write(zoneCount);
            for (int i = 0; i < zoneCount; ++i)
            {
                writer.Write(zones[i].x);
                writer.Write(zones[i].y);
            }

            // 2. fixed zero field, 3. location version (vanilla reads this live too)
            writer.Write(0);
            writer.Write(__instance.m_locationVersion);

            // 4. global keys, filtered exactly like vanilla's RemoveWhere: it *removes*
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
            writer.Write(keptCount);
            for (int i = 0; i < keptCount; ++i) writer.Write(keptKeys[i]);

            // 5. locations-generated flag, 6. location instances
            writer.Write(ZoneSnapshot.LocationsGenerated);
            var locations = ZoneSnapshot.Locations;
            int locationCount = ZoneSnapshot.LocationCount;
            writer.Write(locationCount);
            for (int i = 0; i < locationCount; ++i)
            {
                writer.Write(locations[i].m_location.m_prefabName);
                writer.Write(locations[i].m_position.x);
                writer.Write(locations[i].m_position.y);
                writer.Write(locations[i].m_position.z);
                writer.Write(locations[i].m_placed);
            }

            ZoneSnapshot.Ready = false;
            return false;
        }
    }
}
