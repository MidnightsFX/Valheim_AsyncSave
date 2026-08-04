using HarmonyLib;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// Record ZDOs whose data changed during snapshot yields, so reconciliation can rewrite
    /// exactly those instead of re-checking the whole world.
    ///
    /// Zero cost when SnapshotState.InProgress is false, which is the same guard the sector
    /// patches use. That matters more here than there: these sit on paths that run every frame
    /// during normal play, not only while saving.
    ///
    /// Two hooks, because DataRevision moves two different ways:
    ///
    ///  - ZDO.IncreaseDataRevision - every local mutation. Private, so it is targeted by name.
    ///  - ZDO.Deserialize          - the network receive path. ZDOMan.RPC_ZDOData assigns
    ///                               `zdo.DataRevision = dataRevision` straight to the property
    ///                               (ZDOMan.cs:673) and never calls IncreaseDataRevision, so a
    ///                               hook on the latter alone would miss every change a client
    ///                               pushes to the host - which on a busy server is most of
    ///                               them. Deserialize is called once per updated ZDO on the
    ///                               line immediately after that assignment, and ZDOMan.cs:677
    ///                               is its only call site in the assembly.
    ///
    /// A ZDO created (rather than updated) mid-snapshot is already covered by
    /// CreateNewZdoPatch, which is the sole authoritative creation site for both cases.
    ///
    /// If a future game version grows a third way to bump a revision, the failure is silent:
    /// one mutation is saved a revision stale. That is what the Verify reconcile mode exists
    /// to catch - it runs the full scan alongside and reports anything these hooks missed.
    ///
    /// Both classes carry a Prepare that resolves the target itself and returns false when it
    /// is gone. Without it a rename would throw straight out of PatchAll and take the entire
    /// plugin down with it - a game update would cost the player the async character save and
    /// the sliced world save too, over a hook whose only job is to make reconciliation cheap.
    /// Skipping instead lets AsyncSavePlugin.VerifyRevisionHooks notice the gap and drop back
    /// to FullScan, which needs no hooks at all.
    internal static class ZdoRevisionPatches
    {
        [HarmonyPatch(typeof(ZDO), IncreaseDataRevisionName)]
        internal static class IncreaseDataRevisionPatch
        {
            [HarmonyPrepare]
            static bool Prepare()
            {
                return AccessTools.DeclaredMethod(typeof(ZDO), IncreaseDataRevisionName) != null;
            }

            [HarmonyPostfix]
            static void Postfix(ZDO __instance)
            {
                if (!SnapshotState.InProgress) return;
                SnapshotState.PendingDirty.Add(__instance.m_uid);
            }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        internal static class DeserializePatch
        {
            // nameof only proves the method existed in the assembly we compiled against; the
            // installed game can be a different build.
            [HarmonyPrepare]
            static bool Prepare()
            {
                return AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.Deserialize)) != null;
            }

            [HarmonyPostfix]
            static void Postfix(ZDO __instance)
            {
                if (!SnapshotState.InProgress) return;
                SnapshotState.PendingDirty.Add(__instance.m_uid);
            }
        }

        /// Private, so it cannot be reached through nameof. Shared by the patch attribute and
        /// the Prepare above so the two can never name different methods.
        internal const string IncreaseDataRevisionName = "IncreaseDataRevision";
    }
}
