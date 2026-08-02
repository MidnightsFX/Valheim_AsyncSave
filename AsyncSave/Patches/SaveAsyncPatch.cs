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
            if (!SnapshotState.ReadyForWrite)
            {
                // No snapshot ready (sync save via vanilla PrepareSave path). Let vanilla run.
                return true;
            }

            long sessionId = SnapshotState.SessionID;
            uint nextUid = SnapshotState.NextUid;
            var entries = SnapshotState.Entries;
            var removes = SnapshotState.PendingRemoves;
            byte[] buf = SnapshotState.Buffer.Data;

            int keptCount = 0;
            if (removes.Count == 0)
            {
                keptCount = entries.Count;
            }
            else
            {
                for (int i = 0; i < entries.Count; ++i)
                {
                    if (!removes.Contains(entries[i].Uid)) ++keptCount;
                }
            }

            writer.Write(sessionId);
            writer.Write(nextUid);
            writer.Write(keptCount);

            for (int i = 0; i < entries.Count; ++i)
            {
                var e = entries[i];
                if (removes.Count > 0 && removes.Contains(e.Uid)) continue;
                writer.Write(buf, e.Offset, e.Length);
            }

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
