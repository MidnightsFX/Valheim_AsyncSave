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
    /// We keep the Unity-bound capture here and hand the rest to SaveIo.
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

            // Steam-bound, stays on the main thread exactly as vanilla has it.
            if (profile.m_fileSource == FileHelpers.FileSource.Cloud)
            {
                ulong needed = 1048576UL;
                if (FileHelpers.FileExistsCloud(profile.GetPath()))
                    needed += FileHelpers.GetFileSize(profile.GetPath(), FileHelpers.FileSource.Cloud);
                if (FileHelpers.OperationExceedsCloudCapacity(needed * 3UL))
                {
                    string cloudPath = profile.GetPath();
                    profile.m_fileSource = FileHelpers.FileSource.Local;
                    string localPath = profile.GetPath();
                    if (FileHelpers.FileExistsCloud(cloudPath))
                        FileHelpers.FileCopyOutFromCloud(cloudPath, localPath, true);
                    SaveSystem.InvalidateCache();
                    Log.Warn("The character save operation may exceed the cloud save quota and it has therefore been moved to local storage!");
                }
            }

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
}
