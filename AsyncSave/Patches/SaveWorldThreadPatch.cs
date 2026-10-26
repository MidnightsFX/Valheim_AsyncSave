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
        /// A backup check cheaper than this is left out of the Simple line. On local disk the
        /// check is a directory listing, a few milliseconds nobody needs to read about. On cloud
        /// storage it re-queries the platform for every save file the account holds and is the
        /// largest fixed cost of a save - hundreds of milliseconds - which is exactly when the
        /// line should say where the time went.
        private const float BackupCheckNoteMs = 100f;

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
            // previous async save would be indistinguishable from real ones. The backup figure
            // is cleared for the same reason - a save whose backup check threw would otherwise
            // report the previous save's.
            SaveChunksPatch.ResetWriteStats();
            AutoBackupPatch.ResetWorldStats();

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
        ///
        /// The headline figure is everything vanilla's body did, and that is more than writing
        /// our bytes: the backup check and any backup it decides to make run inside it, and on
        /// cloud storage those dwarf the write (see AutoBackupPatch). The line breaks that part
        /// out and names the storage, so a seven-second "write" of one chunk file reads as what
        /// it was rather than as a slow writer.
        private static void LogWrite(WriteTiming state)
        {
            var log = SASettings.Logging.Value;
            if (log == SALogLevel.Off) return;

            float perMs = 1000f / Stopwatch.Frequency;
            float writeMs = (Stopwatch.GetTimestamp() - state.BodyStart) * perMs;
            float waitMs = state.WaitTicks * perMs;
            float gateMs = state.GateTicks * perMs;
            float backupMs = AutoBackupPatch.WorldTicks * perMs;
            bool backupWritten = AutoBackupPatch.WorldBackupWritten;

            string what = SaveChunksPatch.LastWasSnapshot
                ? $"{SaveChunksPatch.LastKeptCount} ZDOs, {SaveChunksPatch.LastZdoBytes / (1024f * 1024f):0.0} MB " +
                  $"across {SaveChunksPatch.LastChunkCount} chunk file(s)"
                : "vanilla path (no snapshot)";

            // Read after the body, not before it: PreSaveCloudChecksAndOperations takes the
            // source by ref and moves a world off cloud storage mid-save when the quota is short,
            // and the line should name where the files actually went.
            string where = DescribeSource(ZNet.m_world);

            if (log >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave world write: {writeMs:0} ms - {what}, to {where} " +
                         $"(vanilla backup check {backupMs:0} ms, {(backupWritten ? "backup written" : "no backup written")}; " +
                         $"writer parked {waitMs:0} ms on the snapshot, {gateMs:0} ms on the character-save gate).");
            }
            else
            {
                string backup = backupWritten
                    ? $"{backupMs:0} ms of that was vanilla's world auto-backup; "
                    : backupMs >= BackupCheckNoteMs
                        ? $"{backupMs:0} ms of that was vanilla's backup check; "
                        : "";
                Log.Info($"AsyncSave world write: {writeMs:0} ms - {what}, to {where} " +
                         $"({backup}writer parked {waitMs:0} ms waiting for the snapshot).");
            }
        }

        /// Where the world's files live, in the player's terms. "Cloud" is whichever platform
        /// store the build runs against - Steam Cloud on Steam - and the line does not guess
        /// which; what matters for reading it is that every file there is one synchronous
        /// platform call.
        private static string DescribeSource(World world)
        {
            if (world == null) return "an unknown location";
            switch (world.m_fileSource)
            {
                case FileHelpers.FileSource.Cloud: return "cloud storage";
                case FileHelpers.FileSource.Local: return "local disk";
                case FileHelpers.FileSource.Legacy: return "local disk (legacy folder)";
                default: return world.m_fileSource.ToString();
            }
        }
    }

    /// Times vanilla's auto-backup decision for the world so the write line can account for it.
    ///
    /// ZNet.ConsiderAutoBackup runs inside SaveWorldThread, in the window SaveWorldThreadPatch
    /// times, and it is not merely a check: it re-enumerates every save file the account holds
    /// - on cloud storage, one platform query per file - and when a backup is due it copies the
    /// whole world through the same storage, every chunk file included, not just the chunks this
    /// save is writing. On a cloud world with 92 chunk files that copy was 6.9 s of a 7.3 s
    /// "world write" whose actual chunk write took 57 ms. Without this the line blamed our writer.
    ///
    /// World saves only. The character save calls the same method - from our worker, under the
    /// same gate - and the per-type filter keeps it out of the world figure. Plain statics are
    /// safe here because ZNet.SaveWorld joins the previous save thread before starting the next,
    /// so there is never more than one world body in flight, and SaveWorldThreadPatch resets
    /// these before each one.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.ConsiderAutoBackup))]
    internal static class AutoBackupPatch
    {
        /// Ticks the last world save's body spent inside ConsiderAutoBackup, check and copy.
        public static long WorldTicks;
        /// Whether that call actually wrote a backup, as opposed to deciding not to.
        public static bool WorldBackupWritten;

        public static void ResetWorldStats()
        {
            WorldTicks = 0;
            WorldBackupWritten = false;
        }

        static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        static void Postfix(SaveDataType dataType, bool __result, long __state)
        {
            if (dataType != SaveDataType.World) return;
            WorldTicks = Stopwatch.GetTimestamp() - __state;
            WorldBackupWritten = __result;
        }
    }
}
