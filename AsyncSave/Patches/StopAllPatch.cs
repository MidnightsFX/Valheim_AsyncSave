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
            // SaveWorld(true) will invoke our SaveWorldPatch.Prefix which aborts the
            // pending snapshot and re-runs vanilla PrepareSave/SaveAsync synchronously.
            __instance.SaveWorld(true);
        }
    }
}
