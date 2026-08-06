using System.IO;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Replace ZDOMan.SaveAsync when we have a ready snapshot:
    /// write the pre-serialized ZDO byte block directly to disk, applying the
    /// delete skip-list at write time. Skip vanilla's clone-based path.
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SaveAsync))]
    internal static class SaveAsyncPatch
    {
        /// What the last write actually put on disk, for SaveWorldThreadPatch's summary line.
        /// Written on the save thread and read by it, in that order, within one save.
        /// SaveWorldThreadPatch clears these before the body runs so a save that fell through
        /// to vanilla can never report the previous snapshot's figures.
        internal static int LastKeptCount;
        internal static long LastZdoBytes;
        internal static bool LastWasSnapshot;

        internal static void ResetWriteStats()
        {
            LastKeptCount = 0;
            LastZdoBytes = 0;
            LastWasSnapshot = false;
        }

        static bool Prefix(BinaryWriter writer)
        {
            // The verify harness runs this method twice: once for us, once for vanilla.
#if DEBUG
            if (VerifyHarness.Bypass) return true;
#endif

            if (!SnapshotState.ReadyForWrite)
            {
                // No snapshot ready (sync save via vanilla PrepareSave path). Let vanilla run.
                return true;
            }

            long sessionId = SnapshotState.SessionID;
            uint nextUid = SnapshotState.NextUid;
            var entries = SnapshotState.Entries;
            int entryCount = SnapshotState.EntryCount;
            var removes = SnapshotState.PendingRemoves;
            var buffer = SnapshotState.Buffer;

            int keptCount = 0;
            if (removes.Count == 0)
            {
                keptCount = entryCount;
            }
            else
            {
                for (int i = 0; i < entryCount; ++i)
                {
                    if (!removes.Contains(entries[i].Uid)) ++keptCount;
                }
            }

            writer.Write(sessionId);
            writer.Write(nextUid);
            writer.Write(keptCount);

            // Entries are laid down back to back, so in the common case a whole buffer block is
            // one contiguous range and goes out in a single stream write. Runs break only where a
            // ZDO was skipped, where reconciliation could not rewrite a mutated record in place,
            // or at a block boundary - a few dozen extra writes even on a multi-gigabyte world.
            int runBlock = 0;
            int runOffset = 0;
            int runLength = 0;
            // Bytes actually handed to the writer. Not the same as Buffer.TotalBytes: that also
            // counts records the delete skip-list drops, the orphaned originals of any ZDO
            // reconciliation had to re-append because the new encoding outgrew the old one, and
            // the tail of each block abandoned when a record would not fit in it.
            long written = 0;
            for (int i = 0; i < entryCount; ++i)
            {
                var e = entries[i];
                if (removes.Count > 0 && removes.Contains(e.Uid))
                {
                    if (runLength > 0)
                    {
                        writer.Write(buffer.BlockAt(runBlock), runOffset, runLength);
                        written += runLength;
                        runLength = 0;
                    }
                    continue;
                }

                if (runLength > 0 && e.Block == runBlock && e.Offset == runOffset + runLength)
                {
                    runLength += e.Length;
                }
                else
                {
                    if (runLength > 0)
                    {
                        writer.Write(buffer.BlockAt(runBlock), runOffset, runLength);
                        written += runLength;
                    }
                    runBlock = e.Block;
                    runOffset = e.Offset;
                    runLength = e.Length;
                }
            }
            if (runLength > 0)
            {
                writer.Write(buffer.BlockAt(runBlock), runOffset, runLength);
                written += runLength;
            }

            LastKeptCount = keptCount;
            LastZdoBytes = written;
            LastWasSnapshot = true;

            if (SASettings.Logging.Value >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave wrote {keptCount} ZDOs ({written} bytes from " +
                         $"{buffer.BlockCount} block(s), {removes.Count} skipped).");
            }

            // Consumed - reset the ready flag; buffer/entries retain their capacity for next save.
            SnapshotState.ReadyForWrite = false;

            // Skip vanilla body.
            return false;
        }
    }
}
