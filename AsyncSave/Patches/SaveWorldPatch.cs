using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Patches
{
    /// Gate and re-route ZNet.SaveWorld:
    ///  - if snapshot in progress and sync arrives: abort snapshot, reset TCS to
    ///    completed-true so vanilla's fresh SaveWorldThread proceeds, then let vanilla run
    ///  - if snapshot in progress and async arrives: drop the duplicate save
    ///  - otherwise: transpile the async path to skip all three PrepareSave calls and start
    ///    our coroutine, which performs the equivalent work itself
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
    internal static class SaveWorldPatch
    {
        /// Set if the transpiler could not find the call sequence it needs. The method body
        /// is then left exactly as vanilla wrote it, and every other entry point in the mod
        /// stands down so we never half-apply.
        internal static bool TranspilerFailed;

        static bool Prefix(ZNet __instance, bool sync)
        {
            if (TranspilerFailed) return true;

            // ZNet.SaveWorld opens with `if (m_saveThread.IsAlive) m_saveThread.Join()` - an
            // unbounded main-thread block waiting on a background thread that is streaming a
            // quarter of a gigabyte to disk. The snapshot guards below only cover the window
            // where our coroutine is still walking; once it publishes, the writer is running
            // and vanilla would happily freeze the game on it. An autosave has no deadline, so
            // drop it and let the writer finish - the next one lands on the normal interval.
            //
            // The sync path deliberately falls through: a shutdown or manual save must
            // complete, and joining the previous writer first is the correct thing to do.
            if (!sync && __instance.m_saveThread != null && __instance.m_saveThread.IsAlive)
            {
                Log.Info("AsyncSave: previous world save is still writing to disk - skipping this autosave.");
                return false;
            }

            if (SnapshotState.Task.Task.IsCompleted && !SnapshotState.InProgress)
            {
                return true;
            }

            if (sync && ProfileSave.ForceSync)
            {
                // Shutdown save mid-async - abort our coroutine, then
                // reset TCS to completed-true so the vanilla SaveWorldThread runs.
                SnapshotCoroutine.Abort("preempted by shutdown save");
                var reset = new TaskCompletionSource<bool>();
                reset.SetResult(true);
                SnapshotState.Task = reset;
                return true;
            }

            // Command-invoked save (/save) arrives while async is in progress.
            if (ShouldForceSync(sync))
            {
                // Sync command save mid-async - abort and let vanilla run synchronously.
                SnapshotCoroutine.Abort("preempted by sync command save");
                var reset = new TaskCompletionSource<bool>();
                reset.SetResult(true);
                SnapshotState.Task = reset;
                return true;
            }

            // Autosave or async command save already in progress - drop this duplicate.
            Log.Debug("AsyncSave: dropping duplicate async save request.");
            return false;
        }

        // Vanilla ZNet.SaveWorld runs three PrepareSave calls back to back:
        //
        //   ldarg.0; ldfld m_zdoMan;             callvirt ZDOMan.PrepareSave()
        //   call ZoneSystem.get_instance();      callvirt ZoneSystem.PrepareSave()
        //   call RandEventSystem.get_instance(); callvirt RandEventSystem.PrepareSave()
        //
        // All three are unsliced main-thread work, and the coroutine performs the equivalent
        // of all three itself at the end of its walk. Bypassing them with one branch that
        // spans the whole run turns the trigger frame into a thread spawn and nothing else.
        //
        // One branch rather than three: the receiver for each call is pushed by the
        // instructions immediately preceding it, so three separate branches would strand
        // those get_instance calls. With a single branch the stack is trivially verifiable -
        // doCustom is only ever reached at depth 1 (the ZDOMan receiver, consumed by
        // AsyncSaveStart) and afterAll only ever at depth 0 from both sides.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo zdoPrepare = AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.PrepareSave));
            MethodInfo zonePrepare = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.PrepareSave));
            MethodInfo randPrepare = AccessTools.DeclaredMethod(typeof(RandEventSystem), nameof(RandEventSystem.PrepareSave));
            MethodInfo customStart = AccessTools.DeclaredMethod(typeof(SaveWorldPatch), nameof(AsyncSaveStart));

            int iZdo = zdoPrepare == null ? -1 : code.FindIndex(c => c.Calls(zdoPrepare));
            int iZone = (iZdo < 0 || zonePrepare == null) ? -1 : code.FindIndex(iZdo + 1, c => c.Calls(zonePrepare));
            int iRand = (iZone < 0 || randPrepare == null) ? -1 : code.FindIndex(iZone + 1, c => c.Calls(randPrepare));

            bool ok = iZdo >= 0 && iZone >= 0 && iRand >= 0 && iRand + 1 < code.Count;

            // Nothing may branch into the region we are about to bypass, or the jump would
            // land mid-region with the wrong stack depth.
            if (ok)
            {
                for (int i = iZdo; i <= iRand; ++i)
                {
                    if (code[i].labels.Count > 0) { ok = false; break; }
                }
            }

            if (!ok)
            {
                TranspilerFailed = true;
                Log.Error("AsyncSave: could not locate the ZDOMan/ZoneSystem/RandEventSystem PrepareSave " +
                          "sequence in ZNet.SaveWorld. Async save is disabled; the vanilla save path is " +
                          "left completely intact. This usually means the game updated.");
                return code;
            }

            Label doCustom = il.DefineLabel();
            Label afterAll = il.DefineLabel();

            // Tail insert first so iZdo stays valid.
            code[iRand + 1].labels.Add(afterAll);
            code.InsertRange(iRand + 1, new[]
            {
                new CodeInstruction(OpCodes.Br, afterAll),
                new CodeInstruction(OpCodes.Call, customStart) { labels = { doCustom } },
            });

            MethodInfo shouldForceSync = AccessTools.DeclaredMethod(typeof(SaveWorldPatch), nameof(ShouldForceSync));

            // Head insert: at iZdo the ZDOMan receiver is already on the stack.
            // ShouldForceSync(false) returns false (autosave, never forced).
            // ShouldForceSync(true) checks ForceSync and config. If not forced, we want async.
            code.InsertRange(iZdo, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_1),                    // sync
                new CodeInstruction(OpCodes.Call, shouldForceSync),     // -> bool
                new CodeInstruction(OpCodes.Brfalse, doCustom),         // !forced → async path
            });

            return code;
        }

        /// Determine whether this save should take the synchronous vanilla path.
        /// Autosaves are never forced. Shutdown saves are always forced. Command invokes
        /// honour the AsyncCommandSave config switch. Called from both Prefix and transpiled IL.
        public static bool ShouldForceSync(bool sync)
        {
            if (!sync) return false; // autosave -> never force
            if (ProfileSave.ForceSync) return true; // shutdown -> force
            return !SASettings.AsyncCommandSave.Value; // command -> honor config
        }

        // Called from transpiled ZNet.SaveWorld when the sync flag is not forcing vanilla path.
        // Consumes the ZDOMan instance that was already pushed onto the stack
        // (it would have been the receiver for PrepareSave).
        public static void AsyncSaveStart(ZDOMan zdoMan)
        {
            if (SASettings.WorldSaveEnabled.Value)
            {
                SnapshotCoroutine.Start(zdoMan);
                return;
            }

            // Kill switch: do exactly what the branch above skipped, then hand the save
            // thread a completed gate so it proceeds down the untouched vanilla path.
            zdoMan.PrepareSave();
            ZoneSystem.instance.PrepareSave();
            RandEventSystem.instance.PrepareSave();

            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = false;
            ZoneSnapshot.Reset();

            var done = new TaskCompletionSource<bool>();
            done.SetResult(true);
            SnapshotState.Task = done;
        }
    }
}
