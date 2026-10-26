using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Patches
{
    /// Replace Game.SavePlayerProfile with a capture-then-pipeline version.
    ///
    /// Vanilla does, all on one frame: SavePlayerData, Minimap.SaveMapData (8.4 M per-bool
    /// writes then an 8 MiB GZip), SaveLogoutPoint, a Steam cloud-capacity check, and
    /// PlayerProfile.Save (SHA512 + File.Create + fsync). Measured at 336 ms.
    ///
    /// We keep the Unity-bound capture here and hand the rest to SaveIo. The synchronous saves -
    /// shutdown has latched ProfileSave.ForceSync - fall through to vanilla's body untouched, and
    /// SaveMapDataSyncPatch below takes over the one step of it worth taking.
    [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
    internal static class SavePlayerProfilePatch
    {
        static bool Prefix(Game __instance, bool setLogoutPoint)
        {
            if (!ProfileSave.Enabled) return true;

            var profile = __instance.m_playerProfile;
            if (profile == null) return true;

            // A save is already in flight - drop this one rather than stalling the main thread
            // waiting for it, the same way the world save drops duplicate requests. The pending
            // save lands within a frame or two and persists effectively the same state. Callers
            // that overlap in practice: a sleep save landing on top of the 30-minute autosave,
            // or the server's SavePlayerProfile broadcast arriving during one.
            if (ProfileSave.Active)
            {
                __instance.m_saveTimer = 0f;
                // Cheap and main-thread-only; do it now so the request's intent is not lost.
                if (setLogoutPoint && (bool)(UnityEngine.Object)Player.m_localPlayer)
                    profile.SaveLogoutPoint();
                Log.Debug("AsyncSave: dropping duplicate character save request.");
                return false;
            }

            var stopwatch = SASettings.LogTimings ? System.Diagnostics.Stopwatch.StartNew() : null;

            __instance.m_saveTimer = 0f;

            bool captured = false;
            if ((bool)(UnityEngine.Object)Player.m_localPlayer)
            {
                profile.SavePlayerData(Player.m_localPlayer);

                // Null on a dedicated server, which still has a real PlayerProfile ("Developer")
                // and still writes it every autosave.
                var map = Minimap.instance;
                if ((bool)(UnityEngine.Object)map)
                {
                    MapSnapshot.Capture(map);
                    captured = true;
#if DEBUG
                    if (SASettings.VerifyBytes.Value) ProfileVerify.CompareMapPayload(map);
#endif
                }

                if (setLogoutPoint) profile.SaveLogoutPoint();
            }

            // Steam-bound, stays on the main thread exactly as vanilla has it - a transcription of
            // the quota check in the current Game.SavePlayerProfile. That overload takes the file
            // source and the file itself, so it decides on its own whether the cloud is involved
            // and counts the file slot as well as the bytes; it replaced a hand-rolled size sum
            // that only looked at bytes. Only the character cache is invalidated, as vanilla does.
            if (FileHelpers.OperationExceedsCloudCapacity(profile.m_fileSource, 1048576UL, 1, profile.GetPath().ToSingleArray()))
            {
                string cloudPath = profile.GetPath();
                profile.m_fileSource = FileHelpers.FileSource.Local;
                string localPath = profile.GetPath();
                if (FileHelpers.FileExistsCloud(cloudPath))
                    FileHelpers.FileCopyOutFromCloud(cloudPath, localPath, deleteOnCloud: true);
                SaveSystem.InvalidateCache(SaveDataType.Character);
                Log.Warn("The character save operation may exceed the cloud save quota and it has therefore been moved to local storage!");
            }

            // Vanilla pushes achievement progress to the platform here, just before writing the
            // character. It reads stats the save itself is about to persist, and it is a platform
            // call, so it stays on the main thread at the same point in the sequence. Without it
            // every autosave skipped the sync and progress only reached the platform at quit.
            Splatform.PlatformManager.DistributionPlatform?.AchievementManager?.Synchronize();

            ProfileSave.Profile = profile;
            ProfileSave.HadMapCapture = captured;

            if (captured)
            {
                ProfileSave.Stage = ProfileSaveStage.Compressing;
                SaveIo.Enqueue(() =>
                {
                    // The stage advance must happen even if the compress throws, or the
                    // pipeline wedges and no further character save ever runs.
                    try
                    {
                        var sw = SASettings.LogTimings ? System.Diagnostics.Stopwatch.StartNew() : null;
                        ProfileSave.MapData = MapSnapshot.Compress();
                        if (sw != null)
                            Log.Info($"AsyncSave timing: map compress (worker) {sw.ElapsedMilliseconds} ms, {ProfileSave.MapData.Length} bytes");
                    }
                    finally
                    {
                        ProfileSave.Stage = ProfileSaveStage.ReadyToBuild;
                    }
                });
            }
            else
            {
                ProfileSave.Stage = ProfileSaveStage.ReadyToBuild;
            }

            if (stopwatch != null)
                Log.Info($"AsyncSave timing: character capture (main thread) {stopwatch.ElapsedMilliseconds} ms");

            return false;
        }
    }

    /// The synchronous character saves - quit, logout, a server stopping - still run vanilla's
    /// Game.SavePlayerProfile start to finish on the main thread, so the last save before the
    /// scene goes away is never in flight. The one step taken over here is the map.
    ///
    /// Minimap.SaveMapData builds the map payload with 8.4 M BinaryWriter.Write(bool) calls into a
    /// MemoryStream that doubles two dozen times on the way, then gzips it. MapSnapshot produces
    /// the identical bytes from two bulk copies and one unrolled expansion, and compresses with the
    /// same call vanilla makes, so this is the same output for a fraction of the time - still on
    /// the main thread, because on these paths there is no later frame to hand it to. Everything
    /// around it - SavePlayerData, the loading-screen flag, the cloud mount and quota check, the
    /// achievement sync, PlayerProfile.Save - is vanilla's, untouched.
    ///
    /// SaveMapData has one caller, Game.SavePlayerProfile, and SavePlayerProfilePatch skips
    /// vanilla's body entirely on the async path, so this is only ever reached on a sync save. The
    /// buffers are the worker's too, hence the ProfileSave.Active guard: a capture must never land
    /// on top of a compress still in flight. Shutdown's DrainToDisk already rules that out; the
    /// guard turns a surprise into vanilla's path rather than a race.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.SaveMapData))]
    internal static class SaveMapDataSyncPatch
    {
        static bool Prefix(Minimap __instance)
        {
            if (!SASettings.CharacterSaveOn || ProfileSave.Active) return true;

            var game = Game.instance;
            var profile = game == null ? null : game.GetPlayerProfile();
            if (profile == null) return true;

            var stopwatch = SASettings.LogTimings ? System.Diagnostics.Stopwatch.StartNew() : null;

            byte[] data;
            try
            {
                MapSnapshot.Capture(__instance);
#if DEBUG
                if (SASettings.VerifyBytes.Value) ProfileVerify.CompareMapPayload(__instance);
#endif
                data = MapSnapshot.Compress();
            }
            catch (System.Exception ex)
            {
                // This is the last save of the session; losing it to our own bug is the one
                // outcome that must not happen. Vanilla rebuilds the payload from live data.
                MapSnapshot.Reset();
                Log.Error($"AsyncSave: bulk map capture failed on the sync save, using vanilla's. {ex}");
                return true;
            }
            finally
            {
                MapSnapshot.Reset();
            }

            // Exactly vanilla's SaveMapData, with our bytes in place of GetMapData().
            profile.SetMapData(data);

            if (stopwatch != null)
                Log.Info($"AsyncSave timing: sync map capture + compress (main thread) {stopwatch.ElapsedMilliseconds} ms, {data.Length} bytes");

            return false;
        }
    }
}
