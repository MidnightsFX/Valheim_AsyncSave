using System;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Patches
{
    /// Take over PlayerProfile.SavePlayerToDisk when our orchestrator asked for it.
    ///
    /// The split: everything up to and including the ZPackage build stays on the main thread,
    /// byte-for-byte as vanilla writes it, because it walks six dictionaries that gameplay
    /// mutates every frame and mutates m_lastSaveLoad / m_knownWorlds / m_knownWorldKeys as a
    /// side effect. Only the finished byte[] crosses to the worker, which does the SHA512, the
    /// FileWriter, the fsync, ReplaceOldFile and the cache invalidation - measured at 219 ms on
    /// the main thread before this change.
    [HarmonyPatch(typeof(PlayerProfile), "SavePlayerToDisk")]
    internal static class SavePlayerToDiskPatch
    {
        static bool Prefix(PlayerProfile __instance, ref bool __result)
        {
            // Every path except our own orchestrator gets untouched vanilla behaviour -
            // notably menu character creation, which must be on disk before it returns.
            if (!ProfileSave.AsyncRequested) return true;

            __result = Run(__instance);
            return false;
        }

        private static bool Run(PlayerProfile profile)
        {
            if (profile.m_filename == null) return false;

            PlayerProfile.SavingStarted?.Invoke();

            DateTime now = DateTime.Now;

            // Stays on the main thread: it takes m_fileSource by ref and reads SaveSystem,
            // which the worker may be about to touch under SaveIo.Gate.
            bool moved = SaveSystem.CheckMove(profile.m_filename, SaveDataType.Character, ref profile.m_fileSource, now);
            if (profile.m_createBackupBeforeSaving && !moved)
            {
                SaveWithBackups save;
                if (SaveSystem.TryGetSaveByName(profile.m_filename, SaveDataType.Character, out save) && !save.IsDeleted)
                {
                    if (SaveSystem.CreateBackup(save.PrimaryFile, DateTime.Now, profile.m_fileSource))
                        Log.Info("Migrating character save from an old save format, created backup!");
                    else
                        Log.Error($"Failed to create backup of character save {profile.m_filename}!");
                }
                else
                {
                    Log.Error($"Failed to get character save {profile.m_filename} from save system, so a backup couldn't be created!");
                }
            }
            profile.m_createBackupBeforeSaving = false;

            string folder = PlayerProfile.GetCharacterFolderPath(profile.m_fileSource);
            string saveFile = $"{folder}{profile.m_filename}.fch";
            string oldFile = saveFile + ".old";
            string newFile = saveFile + ".new";
            if (!Directory.Exists(folder) && profile.m_fileSource != FileHelpers.FileSource.Cloud)
                Directory.CreateDirectory(folder);

            // Compares our build against a separate transcription of vanilla's, restoring the
            // side effects in between. Must run before the real build so the bytes we ship are
            // the ones that were checked.
            if (SASettings.VerifyBytes.Value) ProfileVerify.CompareProfilePackage(profile);

            byte[] array = BuildPackage(profile);

            var fileSource = profile.m_fileSource;
            string filename = profile.m_filename;

            SaveIo.Enqueue(() => WriteToDisk(array, filename, fileSource, saveFile, oldFile, newFile, now));
            return true;
        }

        /// Byte-for-byte reproduction of PlayerProfile.SavePlayerToDisk's ZPackage build.
        /// Main thread only. Note it deliberately keeps vanilla's side effects: m_lastSaveLoad
        /// is advanced and the known-world/known-key playtime counters are incremented here.
        internal static byte[] BuildPackage(PlayerProfile profile)
        {
            var pkg = new ZPackage();
            pkg.Write(ProfileFormat.ProfileVersion);

            pkg.Write(ProfileFormat.PlayerStatCount);
            for (int key = 0; key < ProfileFormat.PlayerStatCount; ++key)
                pkg.Write(profile.m_playerStats.m_stats[(PlayerStatType)key]);

            pkg.Write(profile.m_firstSpawn);

            pkg.Write(profile.m_worldData.Count);
            foreach (var kv in profile.m_worldData)
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

            pkg.Write(profile.m_playerName);
            pkg.Write(profile.m_playerID);
            pkg.Write(profile.m_startSeed);

            int totalSeconds = (int)(DateTime.Now - profile.m_lastSaveLoad).TotalSeconds;
            profile.m_lastSaveLoad = DateTime.Now;

            pkg.Write(profile.m_usedCheats);
            pkg.Write(new DateTimeOffset(profile.m_dateCreated).ToUnixTimeSeconds());

            if ((bool)(UnityEngine.Object)ZNet.instance && (bool)(UnityEngine.Object)ZoneSystem.instance && ZNet.World != null)
            {
                profile.m_knownWorlds.IncrementOrSet(ZNet.instance.GetWorldName(), totalSeconds);
                for (int key = 0; key < ProfileFormat.GlobalKeyCount; ++key)
                {
                    string value;
                    if (ZoneSystem.instance.GetGlobalKey((GlobalKeys)key, out value))
                        profile.m_knownWorldKeys.IncrementOrSet($"{((GlobalKeys)key).ToString().ToLower()} {value}", totalSeconds);
                    else
                        profile.m_knownWorldKeys.IncrementOrSet(((GlobalKeys)key).ToString().ToLower() + " default", totalSeconds);
                }
            }

            WriteStringFloatMap(pkg, profile.m_knownWorlds);
            WriteStringFloatMap(pkg, profile.m_knownWorldKeys);
            WriteStringFloatMap(pkg, profile.m_knownCommands);
            WriteStringFloatMap(pkg, profile.m_enemyStats);
            WriteStringFloatMap(pkg, profile.m_itemPickupStats);
            WriteStringFloatMap(pkg, profile.m_itemCraftStats);

            if (profile.m_playerData != null)
            {
                pkg.Write(true);
                pkg.Write(profile.m_playerData);
            }
            else
            {
                pkg.Write(false);
            }

            return pkg.GetArray();
        }

        private static void WriteStringFloatMap(ZPackage pkg, System.Collections.Generic.Dictionary<string, float> map)
        {
            pkg.Write(map.Count);
            foreach (var kv in map)
            {
                pkg.Write(kv.Key);
                pkg.Write(kv.Value);
            }
        }

        /// Worker thread, holding SaveIo.Gate.
        private static void WriteToDisk(byte[] array, string filename, FileHelpers.FileSource fileSource,
                                        string saveFile, string oldFile, string newFile, DateTime now)
        {
            // Whatever happens, the stage must advance - a wedged pipeline would silently stop
            // every future character save.
            try
            {
                var sw = SASettings.PhaseTimings.Value ? System.Diagnostics.Stopwatch.StartNew() : null;

                // Vanilla calls ZPackage.GenerateHash(), which re-runs GetArray() and copies the
                // whole payload again. We already hold the array.
                byte[] hash;
                using (var sha = SHA512.Create()) hash = sha.ComputeHash(array);

                var writer = new FileWriter(newFile, FileHelpers.FileHelperType.Binary, fileSource);
                writer.m_binary.Write(array.Length);
                writer.m_binary.Write(array);
                writer.m_binary.Write(hash.Length);
                writer.m_binary.Write(hash);
                writer.Finish();
                SaveSystem.InvalidateCache();

                if (writer.Status != FileWriter.WriterStatus.CloseSucceeded && fileSource == FileHelpers.FileSource.Cloud)
                {
                    string localPath = $"{PlayerProfile.GetCharacterFolderPath(FileHelpers.FileSource.Local)}{filename}_backup_cloud-{now:yyyyMMdd-HHmmss}.fch";
                    writer.DumpCloudWriteToLocalFile(localPath);
                    SaveSystem.InvalidateCache();
                    Log.Error($"Cloud save to location \"{saveFile}\" failed! Saved as local backup \"{localPath}\".");
                }
                else
                {
                    FileHelpers.ReplaceOldFile(saveFile, newFile, oldFile, fileSource);
                    SaveSystem.InvalidateCache();
                    ZNet.ConsiderAutoBackup(filename, SaveDataType.Character, now);
                }

                if (sw != null)
                    Log.Info($"AsyncSave timing: character write (worker) {sw.ElapsedMilliseconds} ms, {array.Length} bytes");
            }
            finally
            {
                ProfileSave.Stage = ProfileSaveStage.Done;
            }
        }
    }
}
