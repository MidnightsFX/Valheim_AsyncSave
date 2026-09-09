using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;
using Splatform;
using AsyncSave.Profile;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Patches
{
    /// Take over PlayerProfile.SavePlayerToDisk when our orchestrator asked for it.
    ///
    /// The split: everything up to and including the ZPackage build stays on the main thread,
    /// byte-for-byte as vanilla writes it, because it walks ninety-odd dictionaries that gameplay
    /// mutates every frame and mutates m_lastSaveLoad plus the per-difficulty known-world
    /// counters as a side effect. Only the finished byte[] crosses to the worker, which does the
    /// SHA512, the FileWriter, the fsync, ReplaceOldFile and the cache invalidation.
    ///
    /// The prologue - mount, PreSaveCloudChecksAndOperations, path setup - also stays on the main
    /// thread. It takes m_fileSource and m_createBackupBeforeSaving by ref and can copy the
    /// existing save to a backup, so it is neither cheap to reason about off-thread nor the part
    /// that costs frame time. The worker owns the unmount, exactly as ZNet's save thread does.
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

            bool mounted = false;
            if (FileHelpers.CloudStorageSupported)
            {
                mounted = FileHelpers.Mount(SaveDataAccess.ReadWrite);
                if (!mounted)
                {
                    // Vanilla throws here. Do the same rather than write a character file into
                    // an unmounted cloud path. ProfileSave.BuildAndQueueWrite catches it, sees
                    // nothing was queued and advances the stage itself, so the pipeline does not
                    // wedge - which is why nothing here touches Stage.
                    throw new InvalidOperationException("Failed to mount!");
                }
            }

            string saveFile, oldFile, newFile;
            byte[] array;
            try
            {
                bool saveExceededCloudQuota = false;
                SaveSystem.PreSaveCloudChecksAndOperations(profile.m_filename, SaveDataType.Character,
                                                           ref profile.m_fileSource,
                                                           ref profile.m_createBackupBeforeSaving,
                                                           ref saveExceededCloudQuota, 1);

                string folder = SaveSystem.GetCharacterFolderPath(profile.m_fileSource);
                saveFile = folder + profile.m_filename + ".fch";
                oldFile = saveFile + ".old";
                newFile = saveFile + ".new";
                if (!Directory.Exists(folder) && profile.m_fileSource.IsNotCloud())
                    Directory.CreateDirectory(folder);

                // Compares our build against a separate transcription of vanilla's, restoring the
                // side effects in between. Must run before the real build so the bytes we ship are
                // the ones that were checked.
#if DEBUG
                if (SASettings.VerifyBytes.Value) ProfileVerify.CompareProfilePackage(profile);
#endif

                array = BuildPackage(profile);
            }
            catch
            {
                // Nothing was queued, so the worker will never reach its own unmount.
                if (mounted) FileHelpers.Unmount();
                throw;
            }

            var fileSource = profile.m_fileSource;
            string filename = profile.m_filename;

            SaveIo.Enqueue(() => WriteToDisk(array, filename, fileSource, saveFile, oldFile, newFile, now, mounted));
            return true;
        }

        /// Byte-for-byte reproduction of PlayerProfile.SavePlayerToDisk's ZPackage build.
        /// Main thread only. Note it deliberately keeps vanilla's side effects: m_lastSaveLoad
        /// is advanced and the known-world/known-key playtime counters are incremented here.
        ///
        /// Those counters are now written *before* they are incremented - the per-difficulty
        /// stat blocks lead the file, and the increment happens near the end - so a playtime
        /// tick always lands in the following save. That is vanilla's ordering, not an
        /// oversight, and reversing it would change every byte after the first stat block.
        internal static byte[] BuildPackage(PlayerProfile profile)
        {
            var pkg = new ZPackage();
            pkg.Write(ProfileFormat.ProfileVersion);
            pkg.Write(ProfileFormat.PlayerStatCount);
            pkg.Write(ProfileFormat.DifficultySlots);

            var stats = profile.m_playerStats;
            for (int slot = 0; slot < ProfileFormat.DifficultySlots; ++slot)
            {
                var s = stats[slot];

                for (int key = 0; key < ProfileFormat.PlayerStatCount; ++key)
                    pkg.Write(s.m_stats[(PlayerStatType)key]);

                WriteStringFloatMap(pkg, s.m_knownWorlds);
                WriteStringFloatMap(pkg, s.m_knownWorldKeys);
                WriteStringFloatMap(pkg, s.m_knownCommands);

                pkg.Write(ProfileFormat.EnemyStatArrays);
                for (int k = 0; k < ProfileFormat.EnemyStatArrays; ++k)
                    WriteStringFloatMap(pkg, s.m_enemyStats[k]);

                WriteStringFloatMap(pkg, s.m_itemPickupStats);
                WriteStringFloatMap(pkg, s.m_itemCraftStats);
                WriteStringFloatMap(pkg, s.m_pickableStats);
                WriteStringFloatMap(pkg, s.m_foodEatenStats);
                WriteStringFloatMap(pkg, s.m_piecesPlacedStats);
            }

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
                // Every counter is kept twice: once in slot 0, the all-difficulties total, and
                // once in the slot for the difficulty this world is currently being played at.
                int difficulty = Achievements.GetCurrentAchievementDifficultyIndex();
                string worldName = ZNet.instance.GetWorldName();

                stats[0].m_knownWorlds.IncrementOrSet(worldName, totalSeconds);
                stats[difficulty].m_knownWorlds.IncrementOrSet(worldName, totalSeconds);

                for (int key = 0; key < ProfileFormat.GlobalKeyCount; ++key)
                {
                    string value;
                    string entry = ZoneSystem.instance.GetGlobalKey((GlobalKeys)key, out value)
                        ? ((GlobalKeys)key).ToString().ToLower() + " " + value
                        : ((GlobalKeys)key).ToString().ToLower() + " default";

                    stats[0].m_knownWorldKeys.IncrementOrSet(entry, totalSeconds);
                    stats[difficulty].m_knownWorldKeys.IncrementOrSet(entry, totalSeconds);
                }
            }

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

        private static void WriteStringFloatMap(ZPackage pkg, Dictionary<string, float> map)
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
                                        string saveFile, string oldFile, string newFile, DateTime now,
                                        bool mounted)
        {
            // Whatever happens, the stage must advance - a wedged pipeline would silently stop
            // every future character save.
            try
            {
                var sw = SASettings.LogTimings ? System.Diagnostics.Stopwatch.StartNew() : null;

                // Vanilla calls ZPackage.GenerateHash(), which re-runs GetArray() and copies the
                // whole payload again. We already hold the array.
                byte[] hash;
                using (var sha = SHA512.Create()) hash = sha.ComputeHash(array);

                var writer = new FileWriter(newFile, CloudStorageFileGrouping.SameFileEnding,
                                            FileHelpers.FileHelperType.Binary, fileSource);
                writer.m_binary.Write(array.Length);
                writer.m_binary.Write(array);
                writer.m_binary.Write(hash.Length);
                writer.m_binary.Write(hash);
                writer.Finish();
                SaveSystem.InvalidateCache(SaveDataType.Character);

                if (writer.Status != FileWriter.WriterStatus.CloseSucceeded && fileSource.IsCloud())
                {
                    string localPath = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local)
                                     + filename + "_backup_cloud-"
                                     + now.ToString(SaveSystem.s_defaultDateFormat) + ".fch";
                    writer.DumpCloudWriteToLocalFile(localPath);
                    SaveSystem.InvalidateCache(SaveDataType.Character);
                    Log.Error($"Cloud save to location \"{saveFile}\" failed! Saved as local backup " +
                              $"\"{localPath}\". Use the \"Manage saves\" menu to restore this backup.");
                }
                else
                {
                    FileHelpers.ReplaceOldFile(saveFile, newFile, oldFile,
                                               CloudStorageFileGrouping.SameFileEnding, fileSource);
                    SaveSystem.InvalidateCache(SaveDataType.Character);
                    ZNet.ConsiderAutoBackup(filename, SaveDataType.Character, now);
                }

                if (sw != null)
                    Log.Info($"AsyncSave timing: character write (worker) {sw.ElapsedMilliseconds} ms, {array.Length} bytes");
            }
            finally
            {
                if (mounted) FileHelpers.Unmount();

                // Not SavingFinished - that is raised by ProfileSave.Finish on the next main
                // thread pump. Its subscribers are Hud.ProfileSaveFinished and Menu.SaveFinished,
                // and the latter can load a scene; raising it from here would be a Unity call
                // off the main thread.
                ProfileSave.Stage = ProfileSaveStage.Done;
            }
        }
    }
}
