using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// On shutdown, if a snapshot is in progress or ready-but-unwritten, force a
    /// sync save so world state hits disk. Mirrors SmoothSave.InterceptSaveThreadJoinOnStop.
    ///
    /// Also where the snapshot pools go back: this is the one point every world unload passes
    /// through, and it runs after the last save has been written.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.StopAll))]
    internal static class StopAllPatch
    {
        static void Prefix(ZNet __instance)
        {
            if (__instance.m_haveStoped) return;

            // Land any character save before the scene goes away.
            ProfileSave.DrainToDisk();

            if (SnapshotState.InProgress || SnapshotState.ReadyForWrite || ZoneSnapshot.Ready)
            {
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

        /// A postfix, not the tail of the prefix: the writer reads Entries and the snapshot buffer
        /// directly, and the one thing that guarantees no writer is still doing so is vanilla's own
        /// unconditional m_saveThread.Join() in the StopAll body (ZNet.cs:392). Releasing from the
        /// prefix would be racing it.
        static void Postfix()
        {
            SnapshotState.ReleasePools();
        }
    }
}
