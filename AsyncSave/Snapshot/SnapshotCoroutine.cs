using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Snapshot
{
    internal static class SnapshotCoroutine
    {
        private static Coroutine _current;

        /// Start a new snapshot. Returns the Task that SaveWorldThread should wait on.
        public static Task<bool> Start(ZDOMan zdoMan)
        {
            var tcs = new TaskCompletionSource<bool>();
            SnapshotState.Task = tcs;
            SnapshotState.ResetForNewSnapshot();
            SnapshotState.SessionID = zdoMan.m_sessionID;
            SnapshotState.InProgress = true;

            _current = ZNet.instance.StartCoroutine(Run(zdoMan, tcs));
            return tcs.Task;
        }

        /// Abort the running snapshot (called by sync-save fallback).
        public static void Abort(string reason)
        {
            if (!SnapshotState.InProgress && !SnapshotState.ReadyForWrite) return;
            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = false;
            if (_current != null)
            {
                ZNet.instance.StopCoroutine(_current);
                _current = null;
            }
            var tcs = SnapshotState.Task;
            if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            Log.Info($"AsyncSave snapshot aborted: {reason}");
        }

        private static IEnumerator Run(ZDOMan zdoMan, TaskCompletionSource<bool> tcs)
        {
            // One frame of settle time before we start walking; matches SmoothSave's shape
            // and lets the initial patch-triggered path finish stack unwind cleanly.
            yield return null;

            var log = SASettings.Logging.Value;
            var totalStopwatch = Stopwatch.StartNew();
            var sliceStopwatch = Stopwatch.StartNew();
            long longestSliceMs = 0;
            long sliceStartTicks = 0;

            // Rebuild connection hash data from live connections. Cheap - O(connections).
            ZDOExtraData.RegenerateConnectionHashData();

            int budgetMs = SASettings.SliceBudgetMs.Value;
            int maxPerSlice = SASettings.MaxZdosPerSlice.Value;
            if (budgetMs < 1) budgetMs = 1;
            if (maxPerSlice < 16) maxPerSlice = 16;

            int inSlice = 0;
            int serialized = 0;

            // Main pass. For each sector we copy the live List<ZDO> refs into a pooled
            // scratch list atomically (no yield during the copy), then yield freely while
            // iterating the stable scratch. This is what protects us from the "ZDO removed
            // mid-yield shifts sector indices and we skip an element" data-loss bug.
            // Dedup is via EntryIndex - a ZDO that migrates sectors during a yield may
            // appear in both the old sector's scratch (already processed) and the new
            // sector's scratch (about to process); the EntryIndex check drops the duplicate.
            var scratch = SnapshotState.SectorScratch;
            var sectors = zdoMan.m_objectsBySector;
            for (int s = 0; s < sectors.Length; ++s)
            {
                var live = sectors[s];
                if (live == null || live.Count == 0) continue;

                // Atomic copy: no yields, no possible concurrent mutation.
                scratch.Clear();
                for (int i = 0; i < live.Count; ++i) scratch.Add(live[i]);

                for (int i = 0; i < scratch.Count; ++i)
                {
                    var zdo = scratch[i];
                    if (!zdo.Persistent) continue;
                    if (SnapshotState.EntryIndex.ContainsKey(zdo.m_uid)) continue;
                    SerializeAndRecord(zdo);
                    ++serialized;
                    ++inSlice;

                    if (inSlice >= maxPerSlice || sliceStopwatch.ElapsedMilliseconds - sliceStartTicks >= budgetMs)
                    {
                        long elapsed = sliceStopwatch.ElapsedMilliseconds - sliceStartTicks;
                        if (elapsed > longestSliceMs) longestSliceMs = elapsed;
                        yield return null;
                        inSlice = 0;
                        sliceStartTicks = sliceStopwatch.ElapsedMilliseconds;
                    }
                }
            }

            // Outside-sector map: flatten all values into scratch in one atomic pass so we
            // never hold a dictionary enumerator across a yield (would throw on mutation).
            scratch.Clear();
            foreach (var kv in zdoMan.m_objectsByOutsideSector)
            {
                var live = kv.Value;
                if (live == null) continue;
                for (int i = 0; i < live.Count; ++i) scratch.Add(live[i]);
            }
            for (int i = 0; i < scratch.Count; ++i)
            {
                var zdo = scratch[i];
                if (!zdo.Persistent) continue;
                if (SnapshotState.EntryIndex.ContainsKey(zdo.m_uid)) continue;
                SerializeAndRecord(zdo);
                ++serialized;
                ++inSlice;
                if (inSlice >= maxPerSlice || sliceStopwatch.ElapsedMilliseconds - sliceStartTicks >= budgetMs)
                {
                    long elapsed = sliceStopwatch.ElapsedMilliseconds - sliceStartTicks;
                    if (elapsed > longestSliceMs) longestSliceMs = elapsed;
                    yield return null;
                    inSlice = 0;
                    sliceStartTicks = sliceStopwatch.ElapsedMilliseconds;
                }
            }
            scratch.Clear();

            // Reconciliation pass.
            long reconStartMs = totalStopwatch.ElapsedMilliseconds;
            int reconciledMutations = 0;
            int appendedAdds = 0;

            if (SASettings.DeltaReconciliation.Value)
            {
                var entries = SnapshotState.Entries;
                for (int i = 0; i < entries.Count; ++i)
                {
                    var e = entries[i];
                    var live = zdoMan.GetZDO(e.Uid);
                    if (live == null)
                    {
                        // If a remove happened after we serialized, RemoveFromSector patch
                        // put it in PendingRemoves - we'll skip it at write time. Nothing to do.
                        continue;
                    }
                    if (live.DataRevision != e.DataRevision)
                    {
                        // Rewrite at buffer tail; update the entry's offset/length/rev.
                        int newOffset = SnapshotState.Buffer.Position;
                        int newLength = ZdoSerializer.Write(live, SnapshotState.Buffer);
                        e.Offset = newOffset;
                        e.Length = newLength;
                        e.DataRevision = live.DataRevision;
                        entries[i] = e;
                        ++reconciledMutations;
                        ++inSlice;
                        if (inSlice >= maxPerSlice || sliceStopwatch.ElapsedMilliseconds - sliceStartTicks >= budgetMs)
                        {
                            long elapsed = sliceStopwatch.ElapsedMilliseconds - sliceStartTicks;
                            if (elapsed > longestSliceMs) longestSliceMs = elapsed;
                            yield return null;
                            inSlice = 0;
                            sliceStartTicks = sliceStopwatch.ElapsedMilliseconds;
                        }
                    }
                }
            }

            // Append pending adds.
            var pendingAdds = SnapshotState.PendingAdds;
            for (int i = 0; i < pendingAdds.Count; ++i)
            {
                var uid = pendingAdds[i];
                if (SnapshotState.EntryIndex.ContainsKey(uid)) continue; // already serialized in main pass
                if (SnapshotState.PendingRemoves.Contains(uid)) continue; // added then removed
                var live = zdoMan.GetZDO(uid);
                if (live == null || !live.Persistent) continue;
                SerializeAndRecord(live);
                ++appendedAdds;
                ++inSlice;
                if (inSlice >= maxPerSlice || sliceStopwatch.ElapsedMilliseconds - sliceStartTicks >= budgetMs)
                {
                    long elapsed = sliceStopwatch.ElapsedMilliseconds - sliceStartTicks;
                    if (elapsed > longestSliceMs) longestSliceMs = elapsed;
                    yield return null;
                    inSlice = 0;
                    sliceStartTicks = sliceStopwatch.ElapsedMilliseconds;
                }
            }

            long reconMs = totalStopwatch.ElapsedMilliseconds - reconStartMs;

            // Freeze nextUid AFTER the snapshot walk - covers any uids created during yields.
            SnapshotState.NextUid = zdoMan.m_nextUid;

            // Prepare small side-systems synchronously (they're cheap).
            ZoneSystem.instance.PrepareSave();
            RandEventSystem.instance.PrepareSave();

            // Reset the save-thread start clock so vanilla timing telemetry is meaningful.
            ZNet.instance.m_saveThreadStartTime = Time.realtimeSinceStartup;

            totalStopwatch.Stop();
            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = true;
            _current = null;
            tcs.TrySetResult(true);

            int keptCount = SnapshotState.Entries.Count - SnapshotState.PendingRemoves.Count;
            if (log == SALogLevel.Simple)
            {
                Log.Info($"AsyncSave snapshot: {keptCount} ZDOs in {totalStopwatch.ElapsedMilliseconds} ms (longest slice: {longestSliceMs} ms).");
            }
            else if (log == SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave snapshot: serialized={serialized} reconciled={reconciledMutations} adds={appendedAdds} removes={SnapshotState.PendingRemoves.Count} kept={keptCount} " +
                         $"buffer={SnapshotState.Buffer.Position} bytes cap={SnapshotState.Buffer.Data.Length} " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms recon={reconMs}ms longestSlice={longestSliceMs}ms");
            }
        }

        private static void SerializeAndRecord(ZDO zdo)
        {
            int offset = SnapshotState.Buffer.Position;
            int length = ZdoSerializer.Write(zdo, SnapshotState.Buffer);
            var entry = new SnapshotEntry
            {
                Uid = zdo.m_uid,
                Offset = offset,
                Length = length,
                DataRevision = zdo.DataRevision,
            };
            SnapshotState.EntryIndex[zdo.m_uid] = SnapshotState.Entries.Count;
            SnapshotState.Entries.Add(entry);
        }
    }
}
