using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Per-invocation timing state for ZNet.SaveWorld. A field would not do: StopAllPatch
    /// calls SaveWorld from inside ZNet.StopAll, so two invocations can be live at once.
    internal struct SaveWorldTiming
    {
        /// The original method body ran, i.e. the prefix returned true.
        public bool Ran;
        /// The sync flag as the caller passed it, before any downgrade.
        public bool WasSync;
        /// The sync flag the original body actually saw.
        public bool Sync;
        /// The final-save top-up: our snapshot, run to completion, then vanilla's Join.
        public bool TopUp;
        public long Start;
    }

    /// Gate and re-route ZNet.SaveWorld:
    ///  - if the save must complete (shutdown, or the manual save button with async manual
    ///    saves turned off): let vanilla run untouched, aborting any snapshot first
    ///  - if it is shutdown's save straight after an async final save (FinalSave's top-up):
    ///    keep the sync flag, and run our snapshot to completion inside the call
    ///  - if it need not complete but the caller asked for sync: downgrade the sync flag so
    ///    vanilla skips its save-thread Join, then take the async branch
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

        /// Main-thread blocks shorter than this are only reported at Detailed. An async save
        /// costs a fraction of a millisecond here; anything near this figure means the snapshot
        /// branch did not get taken.
        private const float MainThreadLogThresholdMs = 50f;

        /// How soon an autosave dropped because the previous writer was still busy comes back.
        private const float RetryAfterSeconds = 60f;

        static bool Prefix(ZNet __instance, ref bool sync, out SaveWorldTiming __state)
        {
            __state = new SaveWorldTiming
            {
                Ran = false,
                WasSync = sync,
                Sync = sync,
                Start = Stopwatch.GetTimestamp(),
            };

            if (TranspilerFailed)
            {
                __state.Ran = true;
                return true;
            }

            // The top-up partitions against ZDOMan's settled dirty set, so it is only valid with
            // no snapshot in flight. The one way to arrive with one is a save that slipped into the
            // gap between the async final save landing and the quit going through - an admin's
            // /save, say. That save gets the ordinary shutdown treatment instead: abort the
            // snapshot and let vanilla do the whole thing. Disarmed before anything below reads
            // ShouldForceSync, so this call and the transpiled body still agree.
            if (sync && FinalSave.TopUpArmed && (SnapshotState.InProgress || !SnapshotState.Task.Task.IsCompleted))
            {
                FinalSave.DisarmTopUp("a snapshot was already in flight when the shutdown save arrived");
            }

            // Vanilla's tail is `if (!sync) return; m_saveThread.Join();`. On the async path
            // that writer thread is parked in SaveWorldThreadPatch waiting on a TCS only the
            // main-thread snapshot coroutine can complete - and the main thread would be sitting
            // in that very Join. A permanent deadlock, and the reason the manual save button
            // froze the game. If we are taking the async branch, take vanilla's async tail with
            // it. What still guarantees the bytes land is the shutdown side: ForceSync makes
            // Game.Shutdown / OnApplicationQuit run the whole save the vanilla way, and
            // StopAllPatch forces one for any snapshot that is still pending.
            //
            // Written through `ref` so the transpiled body reads the same value we decided on
            // here - ShouldForceSync is called from both, and they must never disagree.
            //
            // The top-up is the one save that takes our branch and keeps its flag. It can:
            // AsyncSaveStart finishes the whole snapshot before the writer thread even exists,
            // so the Join has nothing to wait on but the write.
            bool topUp = sync && FinalSave.TopUpArmed && !ShouldForceSync(sync);
            if (sync && !ShouldForceSync(sync) && !topUp) sync = false;

            // ZNet.SaveWorld opens with `if (m_saveThread.IsAlive) m_saveThread.Join()` - an
            // unbounded main-thread block waiting on a background thread that is streaming a
            // quarter of a gigabyte to disk. The snapshot guards below only cover the window
            // where our coroutine is still walking; once it publishes, the writer is running
            // and vanilla would happily freeze the game on it. An autosave has no deadline, so
            // drop it and let the writer finish - the next one lands on the normal interval.
            //
            // Tested against wasSync, not the downgraded flag: a save the player asked for
            // should wait out the previous writer rather than be silently dropped, and that
            // wait is bounded by whatever is left of the write.
            if (!__state.WasSync && __instance.m_saveThread != null && __instance.m_saveThread.IsAlive)
            {
                // ZNet.Save already zeroed m_saveTimer on the way in, so simply returning would
                // put the next attempt a whole interval away - half an hour by default, and
                // every time on a disk slow enough to still be writing. Wind the timer forward to
                // just short of the interval instead, so this retries in a minute. Clamped at
                // zero so a configured interval shorter than the retry is not made longer by it.
                float retryIn = Game.m_saveInterval - RetryAfterSeconds;
                if (retryIn < 0f) retryIn = 0f;
                if (Game.instance != null) Game.instance.m_saveTimer = retryIn;

                Log.Info("AsyncSave: previous world save is still writing to disk - skipping this " +
                         $"autosave, retrying in {Game.m_saveInterval - retryIn:0} s.");
                return false;
            }

            if (SnapshotState.Task.Task.IsCompleted && !SnapshotState.InProgress)
            {
                __state.Ran = true;
                __state.Sync = sync;
                __state.TopUp = topUp;
                return true;
            }

            // A save that must complete arrives mid-snapshot. After the downgrade above - and with
            // any top-up disarmed at the top, since this path never has one - ShouldForceSync is
            // true exactly when sync survived it, so shutdown saves and config-disabled manual
            // saves share one branch: abort our coroutine, then reset the TCS to completed-true
            // so the fresh vanilla SaveWorldThread proceeds.
            if (ShouldForceSync(sync))
            {
                SnapshotCoroutine.Abort(ProfileSave.ForceSync
                    ? "preempted by shutdown save"
                    : "preempted by sync manual save");
                SnapshotState.SignalNoSnapshot();
                __state.Ran = true;
                __state.Sync = sync;
                return true;
            }

            // Autosave or async manual save already in progress - drop this duplicate.
            Log.Debug("AsyncSave: dropping duplicate async save request.");
            return false;
        }

        /// Harmony runs postfixes even when a prefix skipped the original, hence the Ran flag.
        ///
        /// The interval measured here spans the whole of ZNet.SaveWorld, so on the sync path it
        /// covers vanilla's three PrepareSave calls *and* the save-thread Join - exactly the
        /// stretch the player experiences as a freeze.
        static void Postfix(SaveWorldTiming __state)
        {
            if (!__state.Ran) return;

            var log = SASettings.Logging.Value;
            if (log == SALogLevel.Off) return;

            float ms = (Stopwatch.GetTimestamp() - __state.Start) * 1000f / Stopwatch.Frequency;
            bool notable = __state.WasSync || ms >= MainThreadLogThresholdMs;
            if (log == SALogLevel.Simple && !notable) return;

            if (__state.TopUp)
            {
                Log.Info($"AsyncSave: ZNet.SaveWorld blocked the main thread for {ms:0.0} ms " +
                         "(final save top-up: snapshot run to completion + save thread Join).");
            }
            else if (__state.Sync)
            {
                Log.Info($"AsyncSave: ZNet.SaveWorld blocked the main thread for {ms:0.0} ms " +
                         "(sync path: vanilla PrepareSave + save thread Join).");
            }
            else if (__state.WasSync)
            {
                Log.Info($"AsyncSave: ZNet.SaveWorld returned in {ms:0.0} ms " +
                         "(manual save, downgraded to the async path - no Join).");
            }
            else
            {
                Log.Info($"AsyncSave: ZNet.SaveWorld returned in {ms:0.0} ms (async path).");
            }
        }

        // Vanilla ZNet.SaveWorld runs four PrepareSave calls back to back:
        //
        //   ldarg.0; ldfld m_zdoMan;                    callvirt ZDOMan.PrepareSave()
        //   call ZoneSystem.get_instance();             callvirt ZoneSystem.PrepareSave()
        //   call RandEventSystem.get_instance();        callvirt RandEventSystem.PrepareSave()
        //   call PersistentEventSystem.get_instance();  callvirt PersistentEventSystem.PrepareSave()
        //
        // All four are unsliced main-thread work, and the coroutine performs the equivalent
        // of all four itself at the end of its walk. Bypassing them with one branch that
        // spans the whole run turns the trigger frame into a thread spawn and nothing else.
        //
        // One branch rather than four: the receiver for each call is pushed by the
        // instructions immediately preceding it, so separate branches would strand
        // those get_instance calls. With a single branch the stack is trivially verifiable -
        // doCustom is only ever reached at depth 1 (the ZDOMan receiver, which AsyncSaveStart
        // consumes along with the sync flag pushed after it) and afterAll only ever at depth 0
        // from both sides.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo zdoPrepare = AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.PrepareSave));
            MethodInfo zonePrepare = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.PrepareSave));
            MethodInfo randPrepare = AccessTools.DeclaredMethod(typeof(RandEventSystem), nameof(RandEventSystem.PrepareSave));
            MethodInfo persistPrepare = AccessTools.DeclaredMethod(typeof(PersistentEventSystem), nameof(PersistentEventSystem.PrepareSave));
            MethodInfo customStart = AccessTools.DeclaredMethod(typeof(SaveWorldPatch), nameof(AsyncSaveStart));

            int iZdo = zdoPrepare == null ? -1 : code.FindIndex(c => c.Calls(zdoPrepare));
            int iZone = (iZdo < 0 || zonePrepare == null) ? -1 : code.FindIndex(iZdo + 1, c => c.Calls(zonePrepare));
            int iRand = (iZone < 0 || randPrepare == null) ? -1 : code.FindIndex(iZone + 1, c => c.Calls(randPrepare));
            int iLast = (iRand < 0 || persistPrepare == null) ? -1 : code.FindIndex(iRand + 1, c => c.Calls(persistPrepare));

            bool ok = iZdo >= 0 && iZone >= 0 && iRand >= 0 && iLast >= 0 && iLast + 1 < code.Count;

            // Nothing may branch into the region we are about to bypass, or the jump would
            // land mid-region with the wrong stack depth.
            if (ok)
            {
                for (int i = iZdo; i <= iLast; ++i)
                {
                    if (code[i].labels.Count > 0) { ok = false; break; }
                }
            }

            if (!ok)
            {
                // Both flags, because they answer different questions. TranspilerFailed says the
                // method body is exactly as vanilla wrote it, so Prefix must get out of the way
                // entirely. BlockWorldSave says the world-save half is standing down, which is
                // what makes ShouldForceSync stop downgrading the manual save.
                TranspilerFailed = true;
                SASettings.BlockWorldSave();
                Log.Error("AsyncSave: could not locate the ZDOMan/ZoneSystem/RandEventSystem/" +
                          "PersistentEventSystem PrepareSave sequence in ZNet.SaveWorld. Async save is " +
                          "disabled; the vanilla save path is left completely intact. This usually means " +
                          "the game updated.");
                return code;
            }

            Label doCustom = il.DefineLabel();
            Label afterAll = il.DefineLabel();

            // Tail insert first so iZdo stays valid.
            code[iLast + 1].labels.Add(afterAll);
            code.InsertRange(iLast + 1, new[]
            {
                new CodeInstruction(OpCodes.Br, afterAll),
                new CodeInstruction(OpCodes.Ldarg_1) { labels = { doCustom } },   // sync
                new CodeInstruction(OpCodes.Call, customStart),
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
        /// Autosaves are never forced. Shutdown saves are always forced, except the final-save
        /// top-up, which takes our path run to completion. The only other caller that passes sync
        /// is Menu.OnManualSave, which honours the AsyncManualSave config switch. Called from both
        /// Prefix and transpiled IL, which must agree.
        public static bool ShouldForceSync(bool sync)
        {
            if (!sync) return false; // autosave -> never force

            // With the world save off there is no coroutine to wait on, so there is nothing to
            // gain by dropping vanilla's save-thread Join - and a lot to lose: the kill switch is
            // documented as restoring the vanilla path completely, and a Save button that returns
            // before the bytes are on disk is not that. Checked here rather than in AsyncSaveStart
            // because the sync downgrade in Prefix happens before the branch that reaches it.
            if (!SASettings.WorldSaveOn) return true;

            if (FinalSave.TopUpArmed) return false; // shutdown after an async final save -> top-up
            if (ProfileSave.ForceSync) return true; // shutdown -> force

            return !SASettings.AsyncManualSave.Value; // manual save -> honour config
        }

        // Called from transpiled ZNet.SaveWorld when the sync flag is not forcing vanilla path.
        // Consumes the ZDOMan instance that was already pushed onto the stack
        // (it would have been the receiver for PrepareSave), and the sync flag as Prefix left it.
        public static void AsyncSaveStart(ZDOMan zdoMan, bool sync)
        {
            if (SASettings.WorldSaveOn)
            {
                // Still set here means vanilla's tail is about to Join the writer this call
                // starts, so the snapshot has to be finished before we return: a coroutine would
                // park that writer on a walk the blocked main thread can never advance - the
                // manual-save deadlock again. Prefix downgrades every other sync save that
                // reaches this branch; only the final-save top-up keeps the flag.
                if (sync)
                {
                    FinalSave.DisarmTopUp(null);
                    SnapshotCoroutine.RunToCompletion(zdoMan);
                }
                else
                {
                    SnapshotCoroutine.Start(zdoMan);
                }
                return;
            }

            // Kill switch: do exactly what the branch above skipped, then hand the save
            // thread a completed gate so it proceeds down the untouched vanilla path.
            zdoMan.PrepareSave();
            ZoneSystem.instance.PrepareSave();
            RandEventSystem.instance.PrepareSave();
            PersistentEventSystem.instance.PrepareSave();

            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = false;
            ZoneSnapshot.Reset();
            SnapshotState.SignalNoSnapshot();
        }
    }
}
