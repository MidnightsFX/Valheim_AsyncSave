using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;
using SAReconcile = AsyncSave.Config.ReconcileMode;

namespace AsyncSave.Snapshot
{
    internal static class SnapshotCoroutine
    {
        private static Coroutine _current;

        /// Main-thread cost of the reset, measured on the frame that triggers the save. It runs
        /// inside ZNet.SaveWorld, before the coroutine's first resume, so it falls outside every
        /// stopwatch in Run - which is how ~230 MB of collection memsets stayed invisible.
        private static float _triggerFrameMs;

        /// Rounds the reconcile and adds passes will chase work that arrived while they ran,
        /// before giving up on the stragglers. Each round only sees what landed during the
        /// previous one, so on anything short of a pathologically busy server this converges
        /// in two.
        private const int MaxCatchUpRounds = 4;

        /// Start a new snapshot. Returns the Task that SaveWorldThread should wait on.
        public static Task<bool> Start(ZDOMan zdoMan)
        {
            long t0 = Stopwatch.GetTimestamp();

            var tcs = new TaskCompletionSource<bool>();
            SnapshotState.Task = tcs;
            SnapshotState.ResetForNewSnapshot();
            SnapshotState.SessionID = zdoMan.m_sessionID;
            SnapshotState.InProgress = true;

            _current = ZNet.instance.StartCoroutine(Guarded(Run(zdoMan, tcs), tcs));

            _triggerFrameMs = (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;
            return tcs.Task;
        }

        /// Runs the walk so that it fails closed.
        ///
        /// ZNet.SaveWorldThread is a *foreground* thread (ZNet.cs:1203) parked on our completion
        /// source with no deadline - deliberately, because a legitimate snapshot on a very large
        /// world takes minutes and any wall-clock timeout would abandon the healthy saves that
        /// need this mod most. That makes it our job never to leave the source uncompleted. If we
        /// did, the writer would park forever, the process could not exit, InProgress would stay
        /// set so every later save was dropped as a duplicate, and shutdown would deadlock on
        /// ZNet.StopAll's Join - all from one unhandled exception anywhere in the walk.
        ///
        /// yield return cannot sit inside try/catch, but it is legal inside try/finally, and an
        /// exception thrown in the walk unwinds through this finally on its way out of MoveNext,
        /// before Unity's coroutine runner ever sees it. Iterator disposal - Unity tearing the
        /// coroutine down because ZNet's GameObject went away - lands here too.
        private static IEnumerator Guarded(IEnumerator walk, TaskCompletionSource<bool> tcs)
        {
            bool completed = false;
            try
            {
                while (walk.MoveNext()) yield return walk.Current;
                completed = true;
            }
            finally
            {
                // Guarded on still being the live snapshot. Unity does not promise to dispose a
                // stopped iterator promptly, and a disposal that arrived after the next snapshot
                // had started would otherwise reach in and clear its InProgress flag mid-walk.
                if (!completed && ReferenceEquals(SnapshotState.Task, tcs))
                {
                    SnapshotState.InProgress = false;
                    SnapshotState.ReadyForWrite = false;
                    ZoneSnapshot.Reset();
                    _current = null;

                    // False means Abort already completed it - a deliberate stop, which logs its
                    // own reason. True means we are the first to know, so nobody else will report
                    // it. Releasing the writer with false makes it skip the vanilla body: this
                    // save is lost, the next one starts from a clean snapshot.
                    if (tcs.TrySetResult(false))
                    {
                        Log.Error("AsyncSave: the snapshot coroutine did not finish, so this world " +
                                  "save was skipped. Look for the exception logged just above. The " +
                                  "next save starts from a clean snapshot.");
                    }
                }
            }
        }

        /// Abort the running snapshot (called by sync-save fallback).
        public static void Abort(string reason)
        {
            if (!SnapshotState.InProgress && !SnapshotState.ReadyForWrite && !ZoneSnapshot.Ready) return;
            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = false;
            ZoneSnapshot.Reset();

            // Completed before StopCoroutine, not after: it releases the parked writer a moment
            // sooner, and it means Guarded's finally sees an already-completed source and stays
            // quiet whether or not Unity disposes the iterator on our way out.
            var tcs = SnapshotState.Task;
            if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);

            if (_current != null)
            {
                ZNet.instance.StopCoroutine(_current);
                _current = null;
            }
            Log.Info($"AsyncSave snapshot aborted: {reason}");
        }

        private static IEnumerator Run(ZDOMan zdoMan, TaskCompletionSource<bool> tcs)
        {
            // One frame of settle time before we start walking; matches SmoothSave's shape
            // and lets the initial patch-triggered path finish stack unwind cleanly.
            yield return null;

            var log = SASettings.Logging.Value;
            bool timings = SASettings.LogTimings;
            var totalStopwatch = Stopwatch.StartNew();

            // Byte-identity is only a well-defined property when nothing can mutate mid-walk, so
            // the verify harness forces the whole snapshot into one un-yielded slice: no yields
            // means no creates, destroys, mutations or sector migrations, and our entry order is
            // exactly ZDOMan.GetSaveClone order. It stutters hard on purpose.
            //
            // Not a const in Release, so the branches below stay live-but-false rather than
            // unreachable. The JIT folds them away; the compiler would only warn about them.
#if DEBUG
            // GetUniqueHash mixes in ZDOExtraData.s_uniqueHashes, which increments per call and is
            // never reset, so regenerating twice yields different connection hashes. The harness
            // has to rewind it before running vanilla's pass.
            bool verify = SASettings.VerifyBytes.Value;
            int savedUniqueHashes = verify ? ZDOExtraData.s_uniqueHashes : 0;
#else
            bool verify = false;
#endif

            int budgetMs = SASettings.SliceBudgetMs.Value;
            bool adaptive = SASettings.AdaptiveBudget.Value;
            float headroom = SASettings.FrameHeadroomPercent.Value / 100f;
            var reconMode = SASettings.Reconcile;

            // Created before the connection-hash rebuild rather than after, so the worst-block
            // figure covers every millisecond this coroutine holds the main thread - the rebuild
            // shares a frame with the first slice, and a metric that excludes it would understate
            // what the player actually feels.
            var slice = SliceBudget.Create(budgetMs, adaptive, headroom, verify);

            // Rebuild connection hash data from live connections. O(connections), unsliced:
            // it has to be atomic with respect to live connections.
            ZDOExtraData.RegenerateConnectionHashData();
            long connMs = totalStopwatch.ElapsedMilliseconds;

            int serialized = 0;

            // All three pools are empty right now, so each can be sized to the world in one
            // allocation instead of growing into it. Doing this here rather than in
            // ResetForNewSnapshot keeps the allocation off the frame that triggers the save.
            //
            // A frame between them, because together they are gigabytes of fresh LOH on a large
            // world and one block holding all of it is exactly the kind of spike the slice budget
            // cannot see. Verify mode keeps its single un-yielded slice - see above.
            int expectedZdos = zdoMan.m_objectsByID.Count;

            SnapshotState.Visited.EnsureCapacity(expectedZdos);
            if (!verify) { slice.EndSlice(); yield return null; slice.BeginSlice(); }

            SnapshotState.EnsureEntryCapacity(expectedZdos);
            if (!verify) { slice.EndSlice(); yield return null; slice.BeginSlice(); }

            // 15% over the measured density, so the walk normally never has to add a block. double
            // rather than float: at 48 M ZDOs the product is past where float keeps whole numbers.
            long wantBytes = (long)(expectedZdos * (double)SnapshotState.BytesPerZdoEma * 1.15);
            SnapshotState.Buffer.EnsureCapacity(wantBytes);
            if (!verify) { slice.EndSlice(); yield return null; slice.BeginSlice(); }

            long presizeMs = totalStopwatch.ElapsedMilliseconds - connMs;

            // Main pass. For each sector we copy the live List<ZDO> refs into a pooled
            // scratch list atomically (no yield during the copy), then yield freely while
            // iterating the stable scratch. This is what protects us from the "ZDO removed
            // mid-yield shifts sector indices and we skip an element" data-loss bug.
            //
            // Sector migration during a yield cuts both ways, and the two directions are
            // handled at opposite ends. Forward - into a sector we have not reached - puts the
            // ZDO in two scratch copies, and Visited.Add drops the second sighting. Backward -
            // into a sector we already finished - would put it in neither, so AddToSectorPatch
            // records it in PendingAdds and the adds pass below picks it up.
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
            int sectorBlocks = slice.Blocks;

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
            int reconStartBlocks = slice.Blocks;
            int reconciledMutations = 0;
            int appendedAdds = 0;
            int unexaminedAdds = 0;
            int dirtyExamined = 0;
            int catchUpExamined = 0;

            // Dirty pass: rewrite only what the revision hooks flagged. The full scan below walks
            // all 4.9 M entries with a dictionary lookup each to find 21 changes - 36% of the whole
            // save spent proving nothing had happened.
            if (reconMode == SAReconcile.Dirty || verify)
            {
                var dirty = SnapshotState.PendingDirty;
                var dirtyScratch = SnapshotState.DirtyScratch;

                // One extra round past the cap: mutations that arrive during the last round are
                // not a hook gap, just late, and processing them keeps the verify pass from
                // counting them as missed.
                for (int round = 0; round <= MaxCatchUpRounds && dirty.Count > 0; ++round)
                {
                    // Atomic drain: the revision patches keep adding while we yield, and a
                    // live HashSet enumerator would throw the moment one did. Anything that
                    // arrives while this round runs is picked up by the next one.
                    dirtyScratch.Clear();
                    dirtyScratch.AddRange(dirty);
                    dirty.Clear();
                    dirtyExamined += dirtyScratch.Count;
                    if (round == MaxCatchUpRounds) catchUpExamined = dirtyScratch.Count;

                    int i = 0;
                    while (i < dirtyScratch.Count)
                    {
                        i = ReconcileDirtyRange(zdoMan, dirtyScratch, i, ref slice, ref reconciledMutations);
                        if (i < dirtyScratch.Count)
                        {
                            slice.EndSlice();
                            yield return null;
                            slice.BeginSlice();
                        }
                    }
                }
                dirtyScratch.Clear();
            }

            // Full scan: re-check every entry against live state. Depends on nothing but
            // DataRevision itself, which is why it stays as the fallback when the revision hooks
            // are not attached - and as the verify pass's reference for what the hooks missed.
            int missedByHooks = -1;
            if (reconMode == SAReconcile.FullScan || verify)
            {
                int before = reconciledMutations;
                int n = SnapshotState.EntryCount;
                int i = 0;
                while (i < n)
                {
                    i = ReconcileRange(zdoMan, i, ref slice, ref reconciledMutations);
                    if (i < n)
                    {
                        slice.EndSlice();
                        yield return null;
                        slice.BeginSlice();
                    }
                }
                if (verify) missedByHooks = reconciledMutations - before;
            }

            // Append pending adds: everything AddToSectorPatch saw enter a sector while we
            // walked - created mid-snapshot, or migrated backwards past the walk.
            //
            // The list keeps growing while this runs, because a ZDO can migrate during the adds
            // pass itself, so each round takes a fresh high-water mark. Rounds are capped for
            // the same reason the dirty pass caps them: a slice serializes thousands of entries
            // while even a busy server crosses a few hundred sector boundaries a frame, so this
            // converges in one or two - but the save has to finish on a world where it doesn't.
            // Anything left over is overwhelmingly forward migrations that were already
            // serialized and would have been dropped by Visited.Add anyway.
            {
                var pendingAdds = SnapshotState.PendingAdds;
                int i = 0;
                for (int round = 0; round < MaxCatchUpRounds && i < pendingAdds.Count; ++round)
                {
                    int limit = pendingAdds.Count;
                    while (i < limit)
                    {
                        i = AppendAddsRange(zdoMan, i, limit, ref slice, ref appendedAdds);
                        if (i < limit)
                        {
                            slice.EndSlice();
                            yield return null;
                            slice.BeginSlice();
                        }
                    }
                }
                unexaminedAdds = pendingAdds.Count - i;
            }

            long reconMs = totalStopwatch.ElapsedMilliseconds - reconStartMs;
            // +1 for the block still open here: reconcile's tail runs on through the zone
            // capture to the EndSlice below, and that stretch belongs to this phase.
            int reconBlocks = slice.Blocks - reconStartBlocks + 1;

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

            // Close the trailing block. Without this the stretch from the last yield to here -
            // the tail of reconcile, the whole adds pass, and the zone capture - is never
            // measured, and neither is any phase that finished without yielding at all.
            slice.EndSlice();

            totalStopwatch.Stop();
            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = true;
            ZoneSnapshot.Ready = true;

            // Feed the measured encoding density back so the next save's buffer presize lands
            // without adding a block. Must happen after the walk, before anything resets the
            // buffer cursor.
            SnapshotState.RecordBytesPerZdo(SnapshotState.Buffer.TotalBytes, serialized);

#if DEBUG
            // Must run before the save thread is released - it serializes both ways into
            // memory and needs exclusive access to the snapshot and to vanilla's temps.
            if (verify)
            {
                VerifyHarness.Compare(zdoMan, savedUniqueHashes);
                ReportHookCoverage(missedByHooks, dirtyExamined, catchUpExamined);
            }
#endif

            _current = null;
            tcs.TrySetResult(true);

            // Almost always zero, and almost always harmless when it isn't - a forward migration
            // that was already serialized looks the same here as one we never saw. Reported
            // anyway, because the harmful case (a backward migration we ran out of rounds for)
            // is otherwise indistinguishable from a clean save.
            if (unexaminedAdds > 0)
            {
                Log.Warn($"AsyncSave: {unexaminedAdds} sector entries arrived faster than the adds pass " +
                         $"could drain them and were left unexamined after {MaxCatchUpRounds} rounds. Most " +
                         "will be objects already in this save; a few may be one save behind. Raise the " +
                         "slice budget if this recurs.");
            }

            if (timings)
            {
                // Block counts per phase, not just totals: a phase whose wall clock is large
                // but whose block count is 1 is a freeze, not a slice, and that distinction is
                // the whole point of this log line.
                Log.Info($"AsyncSave phases: connHash={connMs}ms presize={presizeMs}ms " +
                         $"sectorWalk={sectorMs - connMs - presizeMs}ms/{sectorBlocks}blocks " +
                         $"outsideSector={reconStartMs - sectorMs}ms " +
                         $"reconcile+adds={reconMs}ms/{reconBlocks}blocks (mode={reconMode} dirty={dirtyExamined} catchUp={catchUpExamined}) " +
                         $"zoneCapture={zoneMs}ms (zones={ZoneSnapshot.ZoneCount} locations={ZoneSnapshot.LocationCount}) " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms triggerFrame={_triggerFrameMs:0.0}ms " +
                         $"worstBlock={slice.WorstBlockMs:0.00}ms blocks={slice.Blocks} " +
                         $"target={slice.TargetFrameMs:0.0}ms cap={slice.CapTargetMs:0.0}ms " +
                         $"budget={slice.LastBudgetMs:0.00}ms headroom={SASettings.FrameHeadroomPercent.Value}% " +
                         $"baseFrame={slice.BaseFrameMs:0.0}ms");
            }

            int keptCount = SnapshotState.EntryCount - SnapshotState.PendingRemoves.Count;
            if (log == SALogLevel.Simple)
            {
                Log.Info($"AsyncSave snapshot: {keptCount} ZDOs in {totalStopwatch.ElapsedMilliseconds} ms " +
                         $"over {slice.Blocks} frames (worst block: {slice.WorstBlockMs:0.00} ms).");
            }
            else if (log >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave snapshot: serialized={serialized} reconciled={reconciledMutations} " +
                         $"adds={appendedAdds} removes={SnapshotState.PendingRemoves.Count} kept={keptCount} " +
                         $"buffer={SnapshotState.Buffer.TotalBytes} bytes cap={SnapshotState.Buffer.Capacity} " +
                         $"bufBlocks={SnapshotState.Buffer.BlockCount} " +
                         $"bytesPerZdo={SnapshotState.BytesPerZdoEma:0.0} " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms recon={reconMs}ms " +
                         $"blocks={slice.Blocks} worstBlock={slice.WorstBlockMs:0.00}ms");
            }
        }

#if DEBUG
        /// Verify mode only: what the full scan still found after the dirty pass had run.
        ///
        /// dirtyExamined separates "the hooks are incomplete" from "the hooks are not there at
        /// all", and those want completely different fixes. Reporting only the miss count once
        /// sent a whole debugging session after a phantom game-version change when the real
        /// answer was that the patch file had never been compiled.
        private static void ReportHookCoverage(int missed, int dirtyExamined, int catchUpExamined)
        {
            if (missed > 0 && dirtyExamined == 0)
            {
                Log.Error($"AsyncSave reconcile verify: the full scan found {missed} changed ZDOs and the " +
                          "dirty pass examined 0. Not a coverage gap - the revision hooks are not firing at " +
                          "all. Check for the hook error logged at startup; until it is fixed, set " +
                          "'Reconcile mutations' to FullScan.");
            }
            else if (missed > 0)
            {
                Log.Error($"AsyncSave reconcile verify: the full scan still found {missed} changed ZDOs " +
                          $"after a dirty pass that examined {dirtyExamined} ({catchUpExamined} of them in " +
                          "the final catch-up round). The revision hooks likely miss a mutation path on this " +
                          "game version. Set 'Reconcile mutations' to FullScan until this is understood.");
            }
            else if (dirtyExamined == 0)
            {
                // Both zero proves nothing: an idle world and a dead hook look identical here.
                Log.Info("AsyncSave reconcile verify: nothing mutated during the snapshot, so the dirty " +
                         "pass was not exercised. Re-run the check on a world with activity.");
            }
            else
            {
                Log.Info($"AsyncSave reconcile verify: dirty pass examined {dirtyExamined} ZDOs and covered " +
                         "every mutation the full scan found.");
            }
        }
#endif

        // The loops below live outside the iterator on purpose. Inside a C# iterator every
        // local becomes a field on the compiler-generated state machine, so each access is a
        // memory load instead of a register - measurable when the loop body is this short and
        // runs a hundred thousand times. Each returns the index to resume at; the caller owns
        // the yield, since yield return cannot cross a call boundary.
        //
        // slice.Step() is charged at the top of every loop, before any `continue`, so the
        // budget pays for work *performed* rather than work that happened to produce output.
        // It used to sit at the bottom past several continues, which meant a pass that
        // skipped almost everything it looked at - reconcile skips ~99.999% - never consulted
        // the clock and ran to completion inside a single frame.
        //
        // Because the charge happens first, the resume index is `i`, not `i + 1`: the element
        // at i has not been touched yet. Getting that wrong drops or duplicates a ZDO, and a
        // duplicate is fatal on load (ZDOMan.Load feeds them to Dictionary.Add).

        /// Serialize one ZDO into the next free entry slot. False means it was already in the
        /// snapshot and nothing was written.
        ///
        /// The dedup add comes first because it is also the write of this uid's entry index, and
        /// the two must agree: publishing entryCount and then not filling that slot would point
        /// dirty reconciliation at somebody else's record. Every caller therefore does its own
        /// skip checks - persistence, liveness, pending removal - before calling in.
        private static bool AppendEntry(ZDO live, VisitedSet visited, SnapshotBuffer buffer,
                                        ref SnapshotEntry[] entries, ref int entryCount)
        {
            if (!visited.Add(live.m_uid, entryCount)) return false;

            int block, offset;
            int length = ZdoSerializer.Write(live, buffer, out block, out offset);

            if (entryCount == entries.Length) entries = SnapshotState.GrowEntries(entryCount);
            entries[entryCount].Uid = live.m_uid;
            entries[entryCount].Block = block;
            entries[entryCount].Offset = offset;
            entries[entryCount].Length = length;
            entries[entryCount].DataRevision = live.DataRevision;
            ++entryCount;
            return true;
        }

        private static int SerializeRange(List<ZDO> scratch, int from, ref SliceBudget slice,
                                          ref int serialized)
        {
            var visited = SnapshotState.Visited;
            var buffer = SnapshotState.Buffer;
            var entries = SnapshotState.Entries;
            int n = scratch.Count;
            int count = serialized;
            int entryCount = SnapshotState.EntryCount;

            for (int i = from; i < n; ++i)
            {
                if (slice.Step())
                {
                    serialized = count;
                    SnapshotState.EntryCount = entryCount;
                    return i;
                }

                var zdo = scratch[i];
                if (!zdo.Persistent) continue;
                if (AppendEntry(zdo, visited, buffer, ref entries, ref entryCount)) ++count;
            }

            serialized = count;
            SnapshotState.EntryCount = entryCount;
            return n;
        }

        private static int AppendAddsRange(ZDOMan zdoMan, int from, int n, ref SliceBudget slice,
                                           ref int appended)
        {
            var pendingAdds = SnapshotState.PendingAdds;
            var removes = SnapshotState.PendingRemoves;
            var visited = SnapshotState.Visited;
            var buffer = SnapshotState.Buffer;
            var entries = SnapshotState.Entries;
            int count = appended;
            int entryCount = SnapshotState.EntryCount;

            for (int i = from; i < n; ++i)
            {
                if (slice.Step())
                {
                    appended = count;
                    SnapshotState.EntryCount = entryCount;
                    return i;
                }

                var uid = pendingAdds[i];
                if (removes.Contains(uid)) continue;            // added then removed
                var live = zdoMan.GetZDO(uid);
                if (live == null || !live.Persistent) continue;
                if (AppendEntry(live, visited, buffer, ref entries, ref entryCount)) ++count;
            }

            appended = count;
            SnapshotState.EntryCount = entryCount;
            return n;
        }

        /// Dirty reconciliation: only the uids the revision hooks flagged. O(mutations).
        private static int ReconcileDirtyRange(ZDOMan zdoMan, List<ZDOID> dirty, int from,
                                               ref SliceBudget slice, ref int reconciled)
        {
            var entries = SnapshotState.Entries;
            var buffer = SnapshotState.Buffer;
            var visited = SnapshotState.Visited;
            var removes = SnapshotState.PendingRemoves;
            int n = dirty.Count;
            int count = reconciled;

            for (int i = from; i < n; ++i)
            {
                if (slice.Step()) { reconciled = count; return i; }

                ZDOID uid = dirty[i];

                // Destroyed after we serialized it - the write phase skips it entirely.
                if (removes.Count > 0 && removes.Contains(uid)) continue;

                // Never serialized in the main pass: it is not persistent, or it was created
                // or migrated backwards after the walk passed its sector. In the latter cases
                // AddToSectorPatch queued it and the adds pass serializes it with current
                // data. Either way there is no record here to rewrite.
                int idx;
                if (!visited.TryGetIndex(uid, out idx)) continue;

                var live = zdoMan.GetZDO(uid);
                if (live == null) continue;

                var e = entries[idx];
                if (live.DataRevision == e.DataRevision) continue;

                RewriteEntry(live, buffer, ref e);
                entries[idx] = e;
                ++count;
            }

            reconciled = count;
            return n;
        }

        /// Full-scan reconciliation: re-check every entry against live state. O(world), and the
        /// reason the dirty path exists - but it depends on nothing except DataRevision itself,
        /// so it stays as the fallback and as the verify pass's reference.
        private static int ReconcileRange(ZDOMan zdoMan, int from, ref SliceBudget slice, ref int reconciled)
        {
            var entries = SnapshotState.Entries;
            var buffer = SnapshotState.Buffer;
            int n = SnapshotState.EntryCount;
            int count = reconciled;

            for (int i = from; i < n; ++i)
            {
                if (slice.Step()) { reconciled = count; return i; }

                var e = entries[i];
                var live = zdoMan.GetZDO(e.Uid);
                // Removed after we serialized it: the sector patch put it in PendingRemoves and
                // the write phase skips it. Nothing to do here.
                if (live == null) continue;
                if (live.DataRevision == e.DataRevision) continue;

                RewriteEntry(live, buffer, ref e);
                entries[i] = e;
                ++count;
            }

            reconciled = count;
            return n;
        }

        /// Re-encode one already-serialized ZDO in place where possible.
        ///
        /// Rewriting in place when the new encoding cannot outgrow the old one keeps entry offsets
        /// monotonic within their block, which is what lets the write phase coalesce a whole block
        /// into a single stream write, and stops the buffer's high-water mark from creeping up
        /// over a long session.
        private static void RewriteEntry(ZDO live, SnapshotBuffer buffer, ref SnapshotEntry e)
        {
            // Rewind onto the record's own slot, rewrite, put the cursor back. This cannot roll to
            // another block: passing e.Length as maxAllowed makes Write refuse before reserving
            // anything whenever the new encoding could outgrow the bytes it is replacing.
            int tailBlock = buffer.Block;
            int tailOffset = buffer.Position;
            buffer.Seek(e.Block, e.Offset);
            int block, offset;
            int newLength = ZdoSerializer.Write(live, buffer, out block, out offset, e.Length);
            buffer.Seek(tailBlock, tailOffset);

            if (newLength < 0)
            {
                // Did not fit - append at the tail and orphan the original bytes.
                e.Length = ZdoSerializer.Write(live, buffer, out block, out offset);
                e.Block = block;
                e.Offset = offset;
            }
            else
            {
                e.Length = newLength;
            }

            e.DataRevision = live.DataRevision;
        }
    }
}
