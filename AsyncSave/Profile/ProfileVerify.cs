#if DEBUG
using System;
using AsyncSave.Patches;
using AsyncSave.Util;

namespace AsyncSave.Profile
{
    /// Debug-only proof that the hand-written character serializers match vanilla byte for byte.
    ///
    /// This is the safety net for the two places AsyncSave reproduces a save format by hand:
    /// the minimap payload (where 8.4 M per-bool writes became two memcpys) and the .fch
    /// ZPackage. Both are character data, so run this before trusting the async path on a
    /// character you care about.
    internal static class ProfileVerify
    {
        /// Compare our uncompressed map payload against vanilla's inner ZPackage.
        /// Must be called on the main thread, straight after MapSnapshot.Capture.
        public static void CompareMapPayload(Minimap map)
        {
            try
            {
                var ours = MapSnapshot.DebugPayloadCopy();

                // Rebuild vanilla's inner package - the same content GetMapData compresses.
                var pkg = new ZPackage();
                pkg.Write(map.m_textureSize);
                for (int i = 0; i < map.m_explored.Length; ++i) pkg.Write(map.m_explored[i]);
                for (int i = 0; i < map.m_explored.Length; ++i) pkg.Write(map.m_exploredOthers[i]);
                int saved = 0;
                foreach (var pin in map.m_pins)
                {
                    if (pin.m_save) ++saved;
                }
                pkg.Write(saved);
                foreach (var pin in map.m_pins)
                {
                    if (!pin.m_save) continue;
                    pkg.Write(pin.m_name);
                    pkg.Write(pin.m_pos);
                    pkg.Write((int)pin.m_type);
                    pkg.Write(pin.m_checked);
                    pkg.Write(pin.m_ownerID);
                    pkg.Write(pin.m_author.ToString());
                }
                pkg.Write(ZNet.instance.IsReferencePositionPublic());

                ByteDiff.Report("map payload", ours, pkg.GetArray());
            }
            catch (Exception ex)
            {
                Log.Error($"AsyncSave verify (map payload) failed: {ex}");
            }
        }

        /// Compare our .fch package against vanilla's.
        ///
        /// Order matters: vanilla's build mutates m_lastSaveLoad and increments the
        /// known-world / known-key playtime counters, so the two passes cannot produce
        /// identical bytes unless that state is restored between them.
        public static void CompareProfilePackage(PlayerProfile profile)
        {
            try
            {
                DateTime savedLastSaveLoad = profile.m_lastSaveLoad;
                var savedKnownWorlds = new System.Collections.Generic.Dictionary<string, float>(profile.m_knownWorlds);
                var savedKnownWorldKeys = new System.Collections.Generic.Dictionary<string, float>(profile.m_knownWorldKeys);

                byte[] ours = SavePlayerToDiskPatch.BuildPackage(profile);

                // Rewind the side effects so vanilla's pass starts from the same state.
                profile.m_lastSaveLoad = savedLastSaveLoad;
                profile.m_knownWorlds.Clear();
                foreach (var kv in savedKnownWorlds) profile.m_knownWorlds[kv.Key] = kv.Value;
                profile.m_knownWorldKeys.Clear();
                foreach (var kv in savedKnownWorldKeys) profile.m_knownWorldKeys[kv.Key] = kv.Value;

                byte[] vanilla = VanillaPackage(profile);

                ByteDiff.Report(".fch package", ours, vanilla);
            }
            catch (Exception ex)
            {
                Log.Error($"AsyncSave verify (.fch package) failed: {ex}");
            }
        }

        /// A literal transcription of PlayerProfile.SavePlayerToDisk's ZPackage build, kept
        /// separate from the production copy on purpose - if both were the same code this
        /// would prove nothing. Only reachable from the debug toggle.
        private static byte[] VanillaPackage(PlayerProfile p)
        {
            var pkg = new ZPackage();
            pkg.Write(43);
            pkg.Write(105);
            for (int key = 0; key < 105; ++key) pkg.Write(p.m_playerStats.m_stats[(PlayerStatType)key]);
            pkg.Write(p.m_firstSpawn);
            pkg.Write(p.m_worldData.Count);
            foreach (var kv in p.m_worldData)
            {
                pkg.Write(kv.Key);
                pkg.Write(kv.Value.m_haveCustomSpawnPoint);
                pkg.Write(kv.Value.m_spawnPoint);
                pkg.Write(kv.Value.m_haveLogoutPoint);
                pkg.Write(kv.Value.m_logoutPoint);
                pkg.Write(kv.Value.m_haveDeathPoint);
                pkg.Write(kv.Value.m_deathPoint);
                pkg.Write(kv.Value.m_homePoint);
                pkg.Write(kv.Value.m_mapData != null);
                if (kv.Value.m_mapData != null) pkg.Write(kv.Value.m_mapData);
            }
            pkg.Write(p.m_playerName);
            pkg.Write(p.m_playerID);
            pkg.Write(p.m_startSeed);
            int totalSeconds = (int)(DateTime.Now - p.m_lastSaveLoad).TotalSeconds;
            p.m_lastSaveLoad = DateTime.Now;
            pkg.Write(p.m_usedCheats);
            pkg.Write(new DateTimeOffset(p.m_dateCreated).ToUnixTimeSeconds());
            if ((bool)(UnityEngine.Object)ZNet.instance && (bool)(UnityEngine.Object)ZoneSystem.instance && ZNet.World != null)
            {
                p.m_knownWorlds.IncrementOrSet(ZNet.instance.GetWorldName(), totalSeconds);
                for (int key = 0; key < 43; ++key)
                {
                    string value;
                    if (ZoneSystem.instance.GetGlobalKey((GlobalKeys)key, out value))
                        p.m_knownWorldKeys.IncrementOrSet($"{((GlobalKeys)key).ToString().ToLower()} {value}", totalSeconds);
                    else
                        p.m_knownWorldKeys.IncrementOrSet(((GlobalKeys)key).ToString().ToLower() + " default", totalSeconds);
                }
            }
            WriteMap(pkg, p.m_knownWorlds);
            WriteMap(pkg, p.m_knownWorldKeys);
            WriteMap(pkg, p.m_knownCommands);
            WriteMap(pkg, p.m_enemyStats);
            WriteMap(pkg, p.m_itemPickupStats);
            WriteMap(pkg, p.m_itemCraftStats);
            if (p.m_playerData != null) { pkg.Write(true); pkg.Write(p.m_playerData); }
            else pkg.Write(false);
            return pkg.GetArray();
        }

        private static void WriteMap(ZPackage pkg, System.Collections.Generic.Dictionary<string, float> map)
        {
            pkg.Write(map.Count);
            foreach (var kv in map) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
        }

    }
}
#endif
