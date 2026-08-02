using System.Threading.Tasks;
using HarmonyLib;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// Prefix on ZNet.SaveWorldThread that blocks the background save thread
    /// until our main-thread snapshot coroutine signals completion.
    /// Returns false to skip the vanilla body if the snapshot was aborted.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorldThread))]
    internal static class SaveWorldThreadPatch
    {
        static bool Prefix()
        {
            Task<bool> t = SnapshotState.Task.Task;
            t.Wait();
            return t.Result;
        }
    }
}
