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
            if (!SnapshotState.InProgress && !SnapshotState.ReadyForWrite && !ZoneSnapshot.Ready) return;
            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = false;
            ZoneSnapshot.Reset();
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
            bool phaseTimings = SASettings.PhaseTimings.Value;
            var totalStopwatch = Stopwatch.StartNew();

            // GetUniqueHash mixes in ZDOExtraData.s_uniqueHashes, which increments per call
            // and is never reset, so regenerating twice yields different connection hashes.
            // The verify harness has to rewind it before running vanilla's pass.
            bool verify = SASettings.VerifyBytes.Value;
            int savedUniqueHashes = verify ? ZDOExtraData.s_uniqueHashes : 0;

            // Rebuild connection hash data from live connections. O(connections), unsliced.
            ZDOExtraData.RegenerateConnectionHashData();
            long connMs = totalStopwatch.ElapsedMilliseconds;

            int budgetMs = SASettings.SliceBudgetMs.Value;
            int maxPerSlice = SASettings.MaxZdosPerSlice.Value;
            if (budgetMs < 1) budgetMs = 1;
            if (maxPerSlice < 16) maxPerSlice = 16;

            if (verify)
            {
                // Byte-identity is only a well-defined property when nothing can mutate
                // mid-walk. Force the whole snapshot into one un-yielded slice: no yields
                // means no creates, destroys, mutations or sector migrations, so our entry
                // order is exactly ZDOMan.GetSaveClone order. This stutters hard on purpose.
                budgetMs = int.MaxValue;
                maxPerSlice = int.MaxValue;
            }

            var slice = SliceBudget.Create(budgetMs, maxPerSlice);
            int serialized = 0;

            // Main pass. For each sector we copy the live List<ZDO> refs into a pooled
            // scratch list atomically (no yield during the copy), then yield freely while
            // iterating the stable scratch. This is what protects us from the "ZDO removed
            // mid-yield shifts sector indices and we skip an element" data-loss bug.
            // Dedup is via SnapshotState.Visited - a ZDO that migrates sectors during a
            // yield may appear in both the old sector's scratch (already processed) and the
            // new sector's scratch (about to process); Visited.Add drops the duplicate.
            var scratch = SnapshotState.SectorScratch;
            var sectors = zdoMan.m_objectsBySector;
            for (int s = 0; s < sectors.Length; ++s)
            {
                var live = sectors[s];
                if (live == null || live.Count == 0) continue;

                // Atomic copy: no yields, no possible concurrent mutation.
                scratch.Clear();
                scratch.AddRange(live);

                int i = 0;
                while (i < scratch.Count)
                {
                    i = SerializeRange(scratch, i, ref slice, ref serialized);
                    if (i < scratch.Count)
                    {
                        slice.EndSlice();
                        yield return null;
                        slice.BeginSlice();
                    }
                }
            }
            long sectorMs = totalStopwatch.ElapsedMilliseconds;

            // Outside-sector map: flatten all values into scratch in one atomic pass so we
            // never hold a dictionary enumerator across a yield (would throw on mutation).
            scratch.Clear();
            foreach (var kv in zdoMan.m_objectsByOutsideSector)
            {
                var live = kv.Value;
                if (live == null) continue;
                scratch.AddRange(live);
            }
            {
                int i = 0;
                while (i < scratch.Count)
                {
                    i = SerializeRange(scratch, i, ref slice, ref serialized);
                    if (i < scratch.Count)
                    {
                        slice.EndSlice();
                        yield return null;
                        slice.BeginSlice();
                    }
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
                int i = 0;
                while (i < entries.Count)
                {
                    i = ReconcileRange(zdoMan, i, ref slice, ref reconciledMutations);
                    if (i < entries.Count)
                    {
                        slice.EndSlice();
                        yield return null;
                        slice.BeginSlice();
                    }
                }
            }

            // Append pending adds.
            {
                var pendingAdds = SnapshotState.PendingAdds;
                int i = 0;
                while (i < pendingAdds.Count)
                {
                    i = AppendAddsRange(zdoMan, i, ref slice, ref appendedAdds);
                    if (i < pendingAdds.Count)
                    {
                        slice.EndSlice();
                        yield return null;
                        slice.BeginSlice();
                    }
                }
            }

            long reconMs = totalStopwatch.ElapsedMilliseconds - reconStartMs;

            // Freeze nextUid AFTER the snapshot walk - covers any uids created during yields.
            SnapshotState.NextUid = zdoMan.m_nextUid;

            // Vanilla's ZoneSystem.PrepareSave rehashes every generated zone into a new
            // HashSet and copies every location instance into a new List - tens of ms and a
            // multi-megabyte allocation in a single frame. Capture into pooled arrays
            // instead. Atomic (no yields), same enumeration order, so the bytes match.
            long zoneStartMs = totalStopwatch.ElapsedMilliseconds;
            ZoneSnapshot.Capture(ZoneSystem.instance);
            long zoneMs = totalStopwatch.ElapsedMilliseconds - zoneStartMs;

            // Four field copies - nothing to optimise.
            RandEventSystem.instance.PrepareSave();

            // Reset the save-thread start clock so vanilla timing telemetry is meaningful.
            ZNet.instance.m_saveThreadStartTime = Time.realtimeSinceStartup;

            totalStopwatch.Stop();
            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = true;
            ZoneSnapshot.Ready = true;

            // Must run before the save thread is released - it serializes both ways into
            // memory and needs exclusive access to the snapshot and to vanilla's temps.
            if (verify) VerifyHarness.Compare(zdoMan, savedUniqueHashes);

            _current = null;
            tcs.TrySetResult(true);

            if (phaseTimings)
            {
                Log.Info($"AsyncSave phases: connHash={connMs}ms sectorWalk={sectorMs - connMs}ms " +
                         $"outsideSector={reconStartMs - sectorMs}ms reconcile+adds={reconMs}ms " +
                         $"zoneCapture={zoneMs}ms (zones={ZoneSnapshot.ZoneCount} locations={ZoneSnapshot.LocationCount}) " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms longestSlice={slice.LongestMs}ms");
            }

            int keptCount = SnapshotState.Entries.Count - SnapshotState.PendingRemoves.Count;
            if (log == SALogLevel.Simple)
            {
                Log.Info($"AsyncSave snapshot: {keptCount} ZDOs in {totalStopwatch.ElapsedMilliseconds} ms (longest slice: {slice.LongestMs} ms).");
            }
            else if (log == SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave snapshot: serialized={serialized} reconciled={reconciledMutations} adds={appendedAdds} removes={SnapshotState.PendingRemoves.Count} kept={keptCount} " +
                         $"buffer={SnapshotState.Buffer.Position} bytes cap={SnapshotState.Buffer.Data.Length} " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms recon={reconMs}ms longestSlice={slice.LongestMs}ms");
            }
        }

        // The three loops below live outside the iterator on purpose. Inside a C# iterator
        // every local becomes a field on the compiler-generated state machine, so each
        // access is a memory load instead of a register - measurable when the loop body is
        // this short and runs a hundred thousand times. Each returns the index to resume at;
        // the caller owns the yield, since yield return cannot cross a call boundary.

        private static int SerializeRange(System.Collections.Generic.List<ZDO> scratch, int from,
                                          ref SliceBudget slice, ref int serialized)
        {
            var visited = SnapshotState.Visited;
            var buffer = SnapshotState.Buffer;
            var entries = SnapshotState.Entries;
            int n = scratch.Count;
            int count = serialized;

            for (int i = from; i < n; ++i)
            {
                var zdo = scratch[i];
                if (!zdo.Persistent) continue;
                if (!visited.Add(zdo.m_uid)) continue;

                int offset = buffer.Position;
                int length = ZdoSerializer.Write(zdo, buffer);
                entries.Add(new SnapshotEntry
                {
                    Uid = zdo.m_uid,
                    Offset = offset,
                    Length = length,
                    DataRevision = zdo.DataRevision,
                });
                ++count;

                if (slice.Step()) { serialized = count; return i + 1; }
            }

            serialized = count;
            return n;
        }

        private static int ReconcileRange(ZDOMan zdoMan, int from, ref SliceBudget slice, ref int reconciled)
        {
            var entries = SnapshotState.Entries;
            var buffer = SnapshotState.Buffer;
            int n = entries.Count;
            int count = reconciled;

            for (int i = from; i < n; ++i)
            {
                var e = entries[i];
                var live = zdoMan.GetZDO(e.Uid);
                if (live == null)
                {
                    // If a remove happened after we serialized, the sector patch put it in
                    // PendingRemoves - we skip it at write time. Nothing to do here.
                    continue;
                }
                if (live.DataRevision == e.DataRevision) continue;

                // Rewrite in place when the new encoding cannot outgrow the old one. That
                // keeps entry offsets monotonic, which is what lets the write phase coalesce
                // the whole block into a single stream write, and stops the buffer's
                // high-water mark from creeping up over a long session.
                int tail = buffer.Position;
                buffer.Position = e.Offset;
                int newLength = ZdoSerializer.Write(live, buffer, e.Length);
                buffer.Position = tail;

                if (newLength < 0)
                {
                    // Did not fit - append at the tail and orphan the original bytes.
                    e.Offset = buffer.Position;
                    e.Length = ZdoSerializer.Write(live, buffer);
                }
                else
                {
                    e.Length = newLength;
                }

                e.DataRevision = live.DataRevision;
                entries[i] = e;
                ++count;

                if (slice.Step()) { reconciled = count; return i + 1; }
            }

            reconciled = count;
            return n;
        }

        private static int AppendAddsRange(ZDOMan zdoMan, int from, ref SliceBudget slice, ref int appended)
        {
            var pendingAdds = SnapshotState.PendingAdds;
            var removes = SnapshotState.PendingRemoves;
            var visited = SnapshotState.Visited;
            var buffer = SnapshotState.Buffer;
            var entries = SnapshotState.Entries;
            int n = pendingAdds.Count;
            int count = appended;

            for (int i = from; i < n; ++i)
            {
                var uid = pendingAdds[i];
                if (removes.Contains(uid)) continue;      // added then removed
                if (!visited.Add(uid)) continue;          // already serialized in the main pass
                var live = zdoMan.GetZDO(uid);
                if (live == null || !live.Persistent) continue;

                int offset = buffer.Position;
                int length = ZdoSerializer.Write(live, buffer);
                entries.Add(new SnapshotEntry
                {
                    Uid = uid,
                    Offset = offset,
                    Length = length,
                    DataRevision = live.DataRevision,
                });
                ++count;

                if (slice.Step()) { appended = count; return i + 1; }
            }

            appended = count;
            return n;
        }
    }
}
