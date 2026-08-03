using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Snapshot;

namespace AsyncSave.Patches
{
    /// Prefix on ZNet.SaveWorldThread that blocks the background save thread
    /// until our main-thread snapshot coroutine signals completion.
    /// Returns false to skip the vanilla body if the snapshot was aborted.
    ///
    /// Also takes SaveIo.Gate for the whole body. ZNet.SaveWorldThread drives SaveSystem,
    /// FileHelpers and FileWriter, none of which are thread-safe, and our character-save worker
    /// touches the same statics - vanilla's safety story is that only one save thread exists at
    /// a time, so the two must not overlap. Game.UpdateSaving makes them overlap by default: it
    /// calls SavePlayerProfile and then ZNet.Save on the same frame.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorldThread))]
    internal static class SaveWorldThreadPatch
    {
        static bool Prefix(out bool __state)
        {
            Task<bool> t = SnapshotState.Task.Task;
            t.Wait();

            if (!t.Result)
            {
                // Snapshot aborted - vanilla body is skipped, so no gate is needed.
                __state = false;
                return false;
            }

            // Taken here rather than around the file write so it also covers vanilla's
            // SaveSystem.CheckMove / CreateBackup prologue.
            Monitor.Enter(SaveIo.Gate);
            __state = true;
            return true;
        }

        // A Finalizer rather than a Postfix: this runs on the exception path too, and leaking
        // the gate would deadlock every later character save.
        static Exception Finalizer(bool __state)
        {
            if (__state) Monitor.Exit(SaveIo.Gate);
            return null;
        }
    }
}
