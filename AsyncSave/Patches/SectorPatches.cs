using HarmonyLib;
using UnityEngine;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// Track ZDO creation and destruction during snapshot yields.
    /// Zero cost when SnapshotState.InProgress is false.
    ///
    /// We hook the true creation and destruction sites, not AddToSector/RemoveFromSector,
    /// because those are also called during sector migration (every position change) and
    /// would bloat pendingAdds with false positives.

    [HarmonyPatch]
    internal static class CreateNewZdoPatch
    {
        // Targets the private CreateNewZDO(ZDOID, Vector3, int) - the sole authoritative
        // creation site (both for new local ZDOs and for network-received ZDOs).
        [HarmonyTargetMethod]
        static System.Reflection.MethodInfo Target()
        {
            return AccessTools.DeclaredMethod(
                typeof(ZDOMan),
                nameof(ZDOMan.CreateNewZDO),
                new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });
        }

        [HarmonyPostfix]
        static void Postfix(ZDO __result)
        {
            if (!SnapshotState.InProgress || __result == null) return;
            SnapshotState.PendingAdds.Add(__result.m_uid);
        }
    }

    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
    internal static class HandleDestroyedZdoPatch
    {
        [HarmonyPostfix]
        static void Postfix(ZDOID uid)
        {
            if (!SnapshotState.InProgress) return;
            SnapshotState.PendingRemoves.Add(uid);
        }
    }
}
