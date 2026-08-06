using HarmonyLib;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// Track ZDOs that appear in a sector during snapshot yields, and ZDOs that are destroyed.
    /// Zero cost when SnapshotState.InProgress is false.

    /// The coroutine walks m_objectsBySector from index 0 upward and yields between sectors, so
    /// the sector a ZDO lives in decides whether the walk has already been past it. Every way a
    /// ZDO can end up in a sector funnels through ZDOMan.AddToSector - ZDO.Initialize on creation,
    /// ZDO.SetSector on every move - which makes this one hook the complete set of ZDOs the walk
    /// might otherwise miss:
    ///
    ///  - created mid-snapshot: never in any scratch copy;
    ///  - migrated *backwards*, from a sector the walk has not reached into one it already
    ///    finished: absent from both scratch copies. This is data loss - the ZDO simply is not in
    ///    the save file - and it hits exactly the objects that move, which are the ones a player
    ///    notices missing: carts, ships, tamed creatures, dropped items.
    ///
    /// Forward migration is the harmless case and needs no help: the ZDO appears in both the old
    /// sector's scratch (already processed) and the new sector's (about to be), and Visited.Add
    /// drops the second sighting.
    ///
    /// An earlier version hooked ZDOMan.CreateNewZDO instead, on the grounds that AddToSector also
    /// fires for migration and would "bloat PendingAdds with false positives". Migration is the
    /// case that matters, and the false positives are free: AppendAddsRange re-checks removes,
    /// liveness, persistence and Visited, so a uid that was already serialized costs one hash
    /// probe and is dropped.
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector))]
    internal static class AddToSectorPatch
    {
        [HarmonyPostfix]
        static void Postfix(ZDO zdo)
        {
            if (!SnapshotState.InProgress || zdo == null) return;
            SnapshotState.PendingAdds.Add(zdo.m_uid);
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
