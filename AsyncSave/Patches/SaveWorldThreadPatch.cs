using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Timings for one run of ZNet.SaveWorldThread, all measured on the save thread itself.
    internal struct WriteTiming
    {
        /// The gate is held and must be released by the finalizer.
        public bool GateHeld;
        /// Ticks the save thread spent parked on the snapshot TCS.
        public long WaitTicks;
        /// Ticks spent waiting for the character worker to let go of SaveIo.Gate.
        public long GateTicks;
        /// Timestamp at which the vanilla body started.
        public long BodyStart;
    }

    /// Prefix on ZNet.SaveWorldThread that blocks the background save thread
    /// until our main-thread snapshot coroutine signals completion.
    /// Returns false to skip the vanilla body if the snapshot was aborted.
    ///
    /// Also takes SaveIo.Gate for the whole body. ZNet.SaveWorldThread drives SaveSystem,
    /// FileHelpers and FileWriter, none of which are thread-safe, and our character-save worker
    /// touches the same statics - vanilla's safety story is that only one save thread exists at
    /// a time, so the two must not overlap. Game.UpdateSaving makes them overlap by default: it
    /// calls SavePlayerProfile and then ZNet.Save on the same frame.
    ///
    /// This is also where the write itself gets timed. Everything from here to the finalizer is
    /// the actual cost of putting the world on disk - the snapshot coroutine's own log line only
    /// covers the main-thread walk that produced the bytes.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorldThread))]
    internal static class SaveWorldThreadPatch
    {
        static bool Prefix(out WriteTiming __state)
        {
            __state = default(WriteTiming);

            Task<bool> t = SnapshotState.Task.Task;
            long t0 = Stopwatch.GetTimestamp();
            t.Wait();
            __state.WaitTicks = Stopwatch.GetTimestamp() - t0;

            if (!t.Result)
            {
                // Snapshot aborted - vanilla body is skipped, so no gate is needed.
                return false;
            }

            // Taken here rather than around the file write so it also covers vanilla's
            // ConsiderAutoBackup / PreSaveCloudChecksAndOperations prologue, which touches the
            // same SaveSystem statics the character worker does.
            long t1 = Stopwatch.GetTimestamp();
            Monitor.Enter(SaveIo.Gate);
            __state.GateTicks = Stopwatch.GetTimestamp() - t1;
            __state.GateHeld = true;

            // Cleared before the body, not after: on a forced-sync save vanilla's own
            // ZDOMan.SaveChunks runs and our prefix never sets them, and stale figures from the
            // previous async save would be indistinguishable from real ones.
            SaveChunksPatch.ResetWriteStats();

            __state.BodyStart = Stopwatch.GetTimestamp();
            return true;
        }

        // A Finalizer rather than a Postfix: this runs on the exception path too, and leaking
        // the gate would deadlock every later character save.
        static Exception Finalizer(WriteTiming __state)
        {
            if (!__state.GateHeld) return null;

            Monitor.Exit(SaveIo.Gate);
            LogWrite(__state);
            return null;
        }

        /// One consolidated line per write. Emitted from the save thread, as the character-save
        /// worker's timings already are - BepInEx's ManualLogSource is safe to call off the main
        /// thread, and buffering this to the next frame would only decouple it from the write it
        /// describes.
        private static void LogWrite(WriteTiming state)
        {
            var log = SASettings.Logging.Value;
            if (log == SALogLevel.Off) return;

            float perMs = 1000f / Stopwatch.Frequency;
            float writeMs = (Stopwatch.GetTimestamp() - state.BodyStart) * perMs;
            float waitMs = state.WaitTicks * perMs;
            float gateMs = state.GateTicks * perMs;

            string what = SaveChunksPatch.LastWasSnapshot
                ? $"{SaveChunksPatch.LastKeptCount} ZDOs, {SaveChunksPatch.LastZdoBytes / (1024f * 1024f):0.0} MB " +
                  $"across {SaveChunksPatch.LastChunkCount} chunk file(s)"
                : "vanilla path (no snapshot)";

            if (log >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave world write: {writeMs:0} ms - {what} " +
                         $"(writer parked {waitMs:0} ms on the snapshot, {gateMs:0} ms on the character-save gate).");
            }
            else
            {
                Log.Info($"AsyncSave world write: {writeMs:0} ms - {what} " +
                         $"(writer parked {waitMs:0} ms waiting for the snapshot).");
            }
        }
    }
}
