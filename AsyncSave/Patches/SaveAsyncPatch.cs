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
        static bool Prefix(BinaryWriter writer)
        {
            // The verify harness runs this method twice: once for us, once for vanilla.
            if (VerifyHarness.Bypass) return true;

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
            byte[] buf = SnapshotState.Buffer.Data;

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

            // Entries are laid down back to back, so in the common case the whole block is
            // one contiguous range and this collapses to a single stream write. Runs break
            // only where a ZDO was skipped or where reconciliation could not rewrite a
            // mutated record in place.
            int runOffset = 0;
            int runLength = 0;
            for (int i = 0; i < entryCount; ++i)
            {
                var e = entries[i];
                if (removes.Count > 0 && removes.Contains(e.Uid))
                {
                    if (runLength > 0) { writer.Write(buf, runOffset, runLength); runLength = 0; }
                    continue;
                }

                if (runLength > 0 && e.Offset == runOffset + runLength)
                {
                    runLength += e.Length;
                }
                else
                {
                    if (runLength > 0) writer.Write(buf, runOffset, runLength);
                    runOffset = e.Offset;
                    runLength = e.Length;
                }
            }
            if (runLength > 0) writer.Write(buf, runOffset, runLength);

            if (SASettings.Logging.Value == SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave wrote {keptCount} ZDOs ({SnapshotState.Buffer.Position} bytes buffered, {removes.Count} skipped).");
            }

            // Consumed - reset the ready flag; buffer/entries retain their capacity for next save.
            SnapshotState.ReadyForWrite = false;

            // Skip vanilla body.
            return false;
        }
    }
}
