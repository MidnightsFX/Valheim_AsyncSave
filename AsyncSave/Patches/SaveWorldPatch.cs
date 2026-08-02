using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;

namespace AsyncSave.Patches
{
    /// Gate and re-route ZNet.SaveWorld:
    ///  - if snapshot in progress and sync arrives: abort snapshot, reset TCS to
    ///    completed-true so vanilla's fresh SaveWorldThread proceeds, then let vanilla run
    ///  - if snapshot in progress and async arrives: drop the duplicate save
    ///  - otherwise: transpile the async path to skip ZDOMan.PrepareSave and start our coroutine
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
    internal static class SaveWorldPatch
    {
        static bool Prefix(bool sync)
        {
            if (SnapshotState.Task.Task.IsCompleted && !SnapshotState.InProgress)
            {
                return true;
            }

            if (sync)
            {
                // Sync save (shutdown / manual) mid-async - abort our coroutine, then
                // reset TCS to completed-true so the vanilla SaveWorldThread runs.
                SnapshotCoroutine.Abort("preempted by sync save");
                var reset = new TaskCompletionSource<bool>();
                reset.SetResult(true);
                SnapshotState.Task = reset;
                return true;
            }

            // Async save already in progress - drop this duplicate.
            Log.Debug("AsyncSave: dropping duplicate async save request.");
            return false;
        }

        // Transpile:
        //   ...
        //   this.m_zdoMan.PrepareSave();
        //   ZoneSystem.instance.PrepareSave();
        //   ...
        // Into:
        //   ...
        //   if (!sync) { AsyncSaveStart(this.m_zdoMan); goto AfterPrepareSave; }
        //   this.m_zdoMan.PrepareSave();
        //   AfterPrepareSave:
        //   ZoneSystem.instance.PrepareSave();
        //   ...
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            MethodInfo prepareSave = AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.PrepareSave));
            MethodInfo customStart = AccessTools.DeclaredMethod(typeof(SaveWorldPatch), nameof(AsyncSaveStart));
            bool insertJumpTarget = false;
            Label doCustomLabel = il.DefineLabel();

            foreach (var ins in instructions)
            {
                if (insertJumpTarget)
                {
                    insertJumpTarget = false;
                    Label endLabel = il.DefineLabel();
                    yield return new CodeInstruction(OpCodes.Br, endLabel);
                    yield return new CodeInstruction(OpCodes.Call, customStart) { labels = { doCustomLabel } };
                    ins.labels.Add(endLabel);
                }

                if (ins.opcode == OpCodes.Callvirt && ins.OperandIs(prepareSave))
                {
                    // At this point, the stack has: ZDOMan_instance (already pushed).
                    // We consume that by calling AsyncSaveStart (static, one arg = ZDOMan).
                    // The 'sync' bool (arg1) determines whether to take the custom branch.
                    yield return new CodeInstruction(OpCodes.Ldarg_1); // sync
                    yield return new CodeInstruction(OpCodes.Brfalse, doCustomLabel);
                    insertJumpTarget = true;
                }

                yield return ins;
            }
        }

        // Called from transpiled ZNet.SaveWorld when !sync.
        // Consumes the ZDOMan instance that was already pushed onto the stack (would have
        // been the receiver for PrepareSave); we use it to start our coroutine.
        public static void AsyncSaveStart(ZDOMan zdoMan)
        {
            SnapshotCoroutine.Start(zdoMan);
        }
    }
}
