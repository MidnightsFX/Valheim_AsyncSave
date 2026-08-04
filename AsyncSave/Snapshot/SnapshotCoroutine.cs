using System.Collections;
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

        /// Main-thread cost of the reset, measured on the frame that triggers the save.
        /// Everything here runs inside ZNet.SaveWorld, before the coroutine's first resume,
        /// so it is outside every stopwatch in Run - which is exactly how ~230 MB of
        /// collection memsets stayed invisible for as long as it did.
        private static float _triggerFrameMs;

        /// Rounds of dirty reconciliation before we give up and let the stragglers be one
        /// revision stale. Each round only sees mutations that landed during the previous
        /// one, so on anything short of a pathologically busy server this converges in two.
        private const int MaxDirtyRounds = 4;

        /// Start a new snapshot. Returns the Task that SaveWorldThread should wait on.
        public static Task<bool> Start(ZDOMan zdoMan)
        {
            long t0 = Stopwatch.GetTimestamp();

            var tcs = new TaskCompletionSource<bool>();
            SnapshotState.Task = tcs;
            SnapshotState.ResetForNewSnapshot();
            SnapshotState.SessionID = zdoMan.m_sessionID;
            SnapshotState.InProgress = true;

            _current = ZNet.instance.StartCoroutine(Run(zdoMan, tcs));

            _triggerFrameMs = (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;
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

            int budgetMs = SASettings.SliceBudgetMs.Value;
            int maxPerSlice = SASettings.MaxZdosPerSlice.Value;
            bool adaptive = SASettings.AdaptiveBudget.Value;
            float headroom = SASettings.FrameHeadroomPercent.Value / 100f;
            int hitchMs = phaseTimings ? SASettings.HitchThresholdMs.Value : 0;
            var reconMode = SASettings.DeltaReconciliation.Value;
            if (budgetMs < 1) budgetMs = 1;
            if (maxPerSlice < 16) maxPerSlice = 16;

            if (verify)
            {
                // Byte-identity is only a well-defined property when nothing can mutate
                // mid-walk. Force the whole snapshot into one un-yielded slice: no yields
                // means no creates, destroys, mutations or sector migrations, so our entry
                // order is exactly ZDOMan.GetSaveClone order. This stutters hard on purpose,
                // and the adaptive budget has to stand down or it would slice anyway.
                budgetMs = int.MaxValue;
                maxPerSlice = int.MaxValue;
                adaptive = false;
            }

            // Created before the connection-hash rebuild rather than after, so the worst-block
            // figure covers every millisecond this coroutine holds the main thread - the rebuild
            // shares a frame with the first slice, and a metric that excludes it would understate
            // what the player actually feels.
            var slice = SliceBudget.Create(budgetMs, maxPerSlice, adaptive, headroom, hitchMs);
            slice.SetPhase(SlicePhase.Presize);

            // Rebuild connection hash data from live connections. O(connections), unsliced:
            // it has to be atomic with respect to live connections.
            ZDOExtraData.RegenerateConnectionHashData();
            long connMs = totalStopwatch.ElapsedMilliseconds;

            int serialized = 0;

            // All three pools are empty right now, so each can be sized to the world in one
            // allocation instead of growing into it. Doing this here rather than in
            // ResetForNewSnapshot keeps the allocation off the frame that triggers the save.
            //
            // A frame between them, because together they are several hundred megabytes of
            // fresh LOH on a large world and one block holding all of it is exactly the kind
            // of spike the slice budget cannot see. Verify mode keeps its single un-yielded
            // slice - see above.
            int expectedZdos = zdoMan.m_objectsByID.Count;

            SnapshotState.Visited.EnsureCapacity(expectedZdos);
            if (!verify) { slice.EndSlice(); yield return null; slice.BeginSlice(); }

            SnapshotState.EnsureEntryCapacity(expectedZdos);
            if (!verify) { slice.EndSlice(); yield return null; slice.BeginSlice(); }

            // The buffer used to be the one pool that still grew by doubling, six times over,
            // allocating half a gigabyte and copying a quarter of one - all of it mid-slice,
            // where the budget cannot interrupt it.
            long wantBytes = (long)(expectedZdos * SnapshotState.BytesPerZdoEma * 1.15f);
            if (wantBytes > int.MaxValue - 64) wantBytes = int.MaxValue - 64;
            SnapshotState.Buffer.EnsureCapacityDiscard((int)wantBytes);
            if (!verify) { slice.EndSlice(); yield return null; slice.BeginSlice(); }

            long presizeMs = totalStopwatch.ElapsedMilliseconds - connMs;

            // Main pass. For each sector we copy the live List<ZDO> refs into a pooled
            // scratch list atomically (no yield during the copy), then yield freely while
            // iterating the stable scratch. This is what protects us from the "ZDO removed
            // mid-yield shifts sector indices and we skip an element" data-loss bug.
            // Dedup is via SnapshotState.Visited - a ZDO that migrates sectors during a
            // yield may appear in both the old sector's scratch (already processed) and the
            // new sector's scratch (about to process); Visited.Add drops the duplicate.
            slice.SetPhase(SlicePhase.SectorWalk);
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
            slice.SetPhase(SlicePhase.OutsideSector);
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
            slice.SetPhase(SlicePhase.Reconcile);
            long reconStartMs = totalStopwatch.ElapsedMilliseconds;
            int reconStartBlocks = slice.Blocks;
            int reconciledMutations = 0;
            int appendedAdds = 0;
            int dirtyExamined = 0;
            int missedByHooks = -1;
            bool dirtyCapHit = false;
            int catchUpExamined = 0;

            // Dirty pass: rewrite only what the revision hooks flagged. The old full scan
            // walked all 4.9 M entries with a dictionary lookup each to find 21 changes - 36%
            // of the whole save spent proving nothing had happened.
            if (reconMode == SAReconcile.Dirty || reconMode == SAReconcile.Verify)
            {
                var dirty = SnapshotState.PendingDirty;
                var dirtyScratch = SnapshotState.DirtyScratch;

                for (int round = 0; round < MaxDirtyRounds && dirty.Count > 0; ++round)
                {
                    // Atomic drain: the revision patches keep adding while we yield, and a
                    // live HashSet enumerator would throw the moment one did. Anything that
                    // arrives while this round runs is picked up by the next one.
                    dirtyScratch.Clear();
                    dirtyScratch.AddRange(dirty);
                    dirty.Clear();
                    dirtyExamined += dirtyScratch.Count;

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

                // Final catch-up drain: mutations that arrived during the last round
                // aren't a hook gap, they're just late. Process them once before
                // handing off to the full scan so Verify doesn't count them as missed.
                if (dirty.Count > 0)
                {
                    dirtyCapHit = true;
                    dirtyScratch.Clear();
                    dirtyScratch.AddRange(dirty);
                    dirty.Clear();
                    catchUpExamined = dirtyScratch.Count;
                    dirtyExamined += catchUpExamined;

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
                SnapshotState.DirtyScratch.Clear();
            }

            // Full scan: the pre-dirty behaviour, kept as the safety net. Under Verify it runs
            // *after* the dirty pass and anything it still finds is a hook the dirty path does
            // not cover - the one failure mode of this design, and silent without this check.
            if (reconMode == SAReconcile.FullScan || reconMode == SAReconcile.Verify)
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
                if (reconMode == SAReconcile.Verify) missedByHooks = reconciledMutations - before;
            }

            // Append pending adds.
            slice.SetPhase(SlicePhase.Adds);
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
            // +1 for the block still open here: reconcile's tail runs on through the zone
            // capture to the EndSlice below, and that stretch belongs to this phase.
            int reconBlocks = slice.Blocks - reconStartBlocks + 1;

            slice.SetPhase(SlicePhase.Tail);

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
            // without a grow. Must happen after the walk, before anything can reset Position.
            SnapshotState.RecordBytesPerZdo(SnapshotState.Buffer.Position, serialized);

            // Must run before the save thread is released - it serializes both ways into
            // memory and needs exclusive access to the snapshot and to vanilla's temps.
            if (verify) VerifyHarness.Compare(zdoMan, savedUniqueHashes);

            _current = null;
            tcs.TrySetResult(true);

            // dirtyExamined is the difference between "the hooks are incomplete" and "the hooks
            // are not there at all", and those want completely different fixes. Reporting only
            // the miss count sent a whole debugging session after a phantom game-version change
            // when the real answer was that the patch file had never been compiled.
            if (missedByHooks > 0 && dirtyExamined == 0)
            {
                Log.Error($"AsyncSave reconcile verify: the full scan found {missedByHooks} changed ZDOs " +
                          $"and the dirty pass examined 0. Not a coverage gap - the revision hooks are not " +
                          $"firing at all, so Dirty mode is currently equivalent to Off. Check for the hook " +
                          $"error logged at startup; until it is fixed, set 'Reconcile mutations' to FullScan.");
            }
            else if (missedByHooks > 0 && dirtyCapHit)
            {
                Log.Info($"AsyncSave reconcile verify: the full scan found {missedByHooks} changed ZDOs after " +
                         $"a dirty pass that examined {dirtyExamined} (including {catchUpExamined} catch-up). " +
                         $"The dirty pass hit its {MaxDirtyRounds}-round cap on a busy world; the full scan " +
                         $"already handled the stragglers. No action needed.");
            }
            else if (missedByHooks > 0)
            {
                Log.Error($"AsyncSave reconcile verify: the full scan still found {missedByHooks} changed " +
                          $"ZDOs after a dirty pass that examined {dirtyExamined}. The revision hooks likely " +
                          $"miss a mutation path on this game version. Set 'Reconcile mutations' to FullScan " +
                          $"until this is understood.");
            }
            else if (missedByHooks == 0 && dirtyExamined == 0)
            {
                // Both zero proves nothing: an idle world and a dead hook look identical here.
                Log.Info("AsyncSave reconcile verify: nothing mutated during the snapshot, so the dirty " +
                         "pass was not exercised. Re-run the check on a world with activity.");
            }
            else if (missedByHooks == 0)
            {
                Log.Info($"AsyncSave reconcile verify: dirty pass examined {dirtyExamined} ZDOs (including {catchUpExamined} catch-up) and covered " +
                         $"every mutation the full scan found.");
            }

            if (phaseTimings)
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

                // The worst-block scalar on its own cannot tell one big stall from fifty
                // medium ones, and it never says what the stall was.
                string hitches = slice.FormatHitches();
                if (hitches != null)
                {
                    Log.Info($"AsyncSave hitches: {slice.HitchCount} block(s) over " +
                             $"{SASettings.HitchThresholdMs.Value}ms, worst first:{hitches}");
                }
                else
                {
                    Log.Info($"AsyncSave hitches: none over {SASettings.HitchThresholdMs.Value}ms.");
                }
            }

            int keptCount = SnapshotState.EntryCount - SnapshotState.PendingRemoves.Count;
            if (log == SALogLevel.Simple)
            {
                Log.Info($"AsyncSave snapshot: {keptCount} ZDOs in {totalStopwatch.ElapsedMilliseconds} ms " +
                         $"over {slice.Blocks} frames (worst block: {slice.WorstBlockMs:0.00} ms).");
            }
            else if (log == SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave snapshot: serialized={serialized} reconciled={reconciledMutations} adds={appendedAdds} removes={SnapshotState.PendingRemoves.Count} kept={keptCount} " +
                         $"buffer={SnapshotState.Buffer.Position} bytes cap={SnapshotState.Buffer.Data.Length} " +
                         $"bytesPerZdo={SnapshotState.BytesPerZdoEma:0.0} " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms recon={reconMs}ms " +
                         $"blocks={slice.Blocks} worstBlock={slice.WorstBlockMs:0.00}ms");
            }
        }

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

        private static int SerializeRange(System.Collections.Generic.List<ZDO> scratch, int from,
                                          ref SliceBudget slice, ref int serialized)
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
                // entryCount is the slot this ZDO is about to occupy; handing it to the dedup
                // set is what lets dirty reconciliation find the record again by uid.
                if (!visited.Add(zdo.m_uid, entryCount)) continue;

                int offset = buffer.Position;
                int length = ZdoSerializer.Write(zdo, buffer);

                if (entryCount == entries.Length) entries = SnapshotState.GrowEntries(entryCount);
                entries[entryCount].Uid = zdo.m_uid;
                entries[entryCount].Offset = offset;
                entries[entryCount].Length = length;
                entries[entryCount].DataRevision = zdo.DataRevision;
                ++entryCount;
                ++count;
            }

            serialized = count;
            SnapshotState.EntryCount = entryCount;
            return n;
        }

        /// Dirty reconciliation: only the uids the revision hooks flagged. O(mutations).
        private static int ReconcileDirtyRange(ZDOMan zdoMan, System.Collections.Generic.List<ZDOID> dirty,
                                               int from, ref SliceBudget slice, ref int reconciled)
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

                // Never serialized in the main pass: either it was created after the walk
                // passed its sector (the adds pass picks it up with current data) or it is
                // not persistent. Either way there is no record here to rewrite.
                int idx;
                if (!visited.TryGetIndex(uid, out idx)) continue;

                var live = zdoMan.GetZDO(uid);
                if (live == null) continue;

                var e = entries[idx];
                if (live.DataRevision == e.DataRevision) continue;

                if (RewriteEntry(live, buffer, ref e)) ++count;
                entries[idx] = e;
            }

            reconciled = count;
            return n;
        }

        /// Full-scan reconciliation: re-check every entry against live state. O(world), and
        /// the reason the dirty path exists - but it depends on nothing except DataRevision
        /// itself, so it stays as the safety net and as Verify's reference.
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
                if (live == null)
                {
                    // If a remove happened after we serialized, the sector patch put it in
                    // PendingRemoves - we skip it at write time. Nothing to do here.
                    continue;
                }
                if (live.DataRevision == e.DataRevision) continue;

                if (RewriteEntry(live, buffer, ref e)) ++count;
                entries[i] = e;
            }

            reconciled = count;
            return n;
        }

        /// Re-encode one already-serialized ZDO in place where possible.
        ///
        /// Rewriting in place when the new encoding cannot outgrow the old one keeps entry
        /// offsets monotonic, which is what lets the write phase coalesce the whole block into
        /// a single stream write, and stops the buffer's high-water mark from creeping up over
        /// a long session.
        private static bool RewriteEntry(ZDO live, SnapshotBuffer buffer, ref SnapshotEntry e)
        {
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
            return true;
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
                if (removes.Contains(uid)) continue;             // added then removed
                var live = zdoMan.GetZDO(uid);
                if (live == null || !live.Persistent) continue;
                // Add last, not first: it publishes entryCount as this uid's entry index, so
                // it must not run on a path that then skips writing the entry - a stale index
                // would point dirty reconciliation at somebody else's record.
                if (!visited.Add(uid, entryCount)) continue;     // already serialized in the main pass

                int offset = buffer.Position;
                int length = ZdoSerializer.Write(live, buffer);

                if (entryCount == entries.Length) entries = SnapshotState.GrowEntries(entryCount);
                entries[entryCount].Uid = uid;
                entries[entryCount].Offset = offset;
                entries[entryCount].Length = length;
                entries[entryCount].DataRevision = live.DataRevision;
                ++entryCount;
                ++count;
            }

            appended = count;
            SnapshotState.EntryCount = entryCount;
            return n;
        }
    }
}
