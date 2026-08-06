using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// On shutdown, if a snapshot is in progress or ready-but-unwritten, force a
    /// sync save so world state hits disk. Mirrors SmoothSave.InterceptSaveThreadJoinOnStop.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.StopAll))]
    internal static class StopAllPatch
    {
        static void Prefix(ZNet __instance)
        {
            if (__instance.m_haveStoped) return;

            // Land any character save before the scene goes away.
            ProfileSave.DrainToDisk();

            if (!SnapshotState.InProgress && !SnapshotState.ReadyForWrite && !ZoneSnapshot.Ready) return;

            // Vanilla StopAll joins the save thread unconditionally right after this
            // (ZNet.cs:392), so whatever we start here has to be the vanilla sync path - an
            // async save would leave that Join waiting on a writer parked on a coroutine the
            // main thread can no longer run. ZNet.ShutdownWithoutSave (console suspend) reaches
            // StopAll without going through Game.Shutdown, so the latch is not already set.
            // Game.Awake clears it again if we come back to a world in the same process.
            ProfileSave.ForceSync = true;

            // SaveWorld(true) will invoke our SaveWorldPatch.Prefix which aborts the
            // pending snapshot and re-runs vanilla PrepareSave/SaveAsync synchronously.
            __instance.SaveWorld(true);
        }
    }
}
