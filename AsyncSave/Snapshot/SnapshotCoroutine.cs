using System;
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
    /// The main-thread half of an async world save: everything ZNet.SaveWorld's four PrepareSave
    /// calls would have done, spread over frames.
    ///
    /// Since the chunked save format, vanilla only clones the ZDOs in chunks it is about to
    /// rewrite, so the walk below is bounded by what actually changed rather than by world size.
    /// What is *not* bounded is ZDOExtraData.PrepareSave, which deep-clones all eight extra-data
    /// dictionaries for every ZDO in the world on every save - one BinarySearchDictionary
    /// allocation per ZDO, dirty or not. Pre-serializing straight from live data is what lets us
    /// skip it, and on a large world it is now the single biggest thing this mod removes from the
    /// main thread.
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

        /// Portal prefabs, rebuilt per save from Game.instance.PortalPrefabHash. Vanilla asks
        /// that List for Contains once per ZDO, which is a linear scan inside the hottest loop
        /// of the save; the set has the same membership and answers in one probe.
        private static readonly HashSet<int> _portalPrefabs = new HashSet<int>();

        /// Start a new snapshot. Returns the Task that SaveWorldThread should wait on.
        public static Task<bool> Start(ZDOMan zdoMan)
        {
            long t0 = Stopwatch.GetTimestamp();

            var tcs = new TaskCompletionSource<bool>();
            SnapshotState.Task = tcs;
            SnapshotState.ResetForNewSnapshot();
            SnapshotState.InProgress = true;

            _current = ZNet.instance.StartCoroutine(Guarded(Run(zdoMan, tcs), tcs));

            _triggerFrameMs = (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;
            return tcs.Task;
        }

        /// Runs the walk so that it fails closed.
        ///
        /// ZNet.SaveWorldThread is a *foreground* thread parked on our completion source, so an
        /// exception that escaped the iterator would hang the process on exit rather than merely
        /// losing a save.
        private static IEnumerator Guarded(IEnumerator walk, TaskCompletionSource<bool> tcs)
        {
            while (true)
            {
                object step;
                try
                {
                    if (!walk.MoveNext()) break;
                    step = walk.Current;
                }
                catch (Exception ex)
                {
                    Log.Error($"AsyncSave: snapshot walk threw, falling back to the vanilla save path. {ex}");
                    SnapshotState.InProgress = false;
                    SnapshotState.ReadyForWrite = false;
                    ZoneSnapshot.Reset();
                    RestoreVanillaPrepare(ZDOMan.instance);
                    tcs.TrySetResult(true);
                    _current = null;
                    yield break;
                }
                yield return step;
            }

            // Normal completion still guarantees the gate is released, even if Run returned
            // without setting it - a parked foreground thread is not something to leave to luck.
            if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
        }

        /// The walk failed or was preempted after it had already taken over from vanilla's
        /// PrepareSave. Run the four calls we skipped so the save thread finds the state it
        /// expects, and drop our half-built snapshot.
        ///
        /// Order matters: ZDOMan.PrepareSave overwrites m_saveData and re-runs BeginSave, which
        /// is exactly what we want - it discards our partition and rebuilds vanilla's.
        private static void RestoreVanillaPrepare(ZDOMan zdoMan)
        {
            try
            {
                RollbackSaveState(zdoMan);
                zdoMan.PrepareSave();
                ZoneSystem.instance.PrepareSave();
                RandEventSystem.instance.PrepareSave();
                PersistentEventSystem.instance.PrepareSave();
            }
            catch (Exception ex)
            {
                Log.Error($"AsyncSave: could not hand the save back to vanilla's PrepareSave. {ex}");
            }
        }

        /// Undo the ZDOMan.BeginSave our walk performed, exactly as vanilla's own failed-save
        /// transition does (ZDOMan.UpdateSaveState with saveState false).
        ///
        /// This is not bookkeeping tidiness. Starting the walk flips m_currentSaveState, which
        /// re-points the DirtyChunks and DirtyPortalObjects properties at slot 1, so everything
        /// that changed while we walked was recorded as "dirty for the next save". If we hand
        /// back to vanilla without merging slot 1 into slot 0 and clearing the flag, vanilla's
        /// replacement PrepareSave reads a dirty set that is missing every one of those changes,
        /// and reads the *wrong slot* for DirtyPortalObjects - so a shutdown save that preempts
        /// a snapshot could silently drop the portal chunk and every chunk touched during the
        /// walk.
        private static void RollbackSaveState(ZDOMan zdoMan)
        {
            if (zdoMan == null) return;
            if (zdoMan.m_currentSaveState == ZDOMan.SaveState.NotSaving) return;

            var dirty = zdoMan.m_dirtyChunks;
            dirty[0].UnionWith(dirty[1]);
            dirty[1].Clear();

            var portals = zdoMan.m_dirtyPortalObjects;
            portals[0] |= portals[1];
            portals[1] = false;

            zdoMan.m_currentSaveState = ZDOMan.SaveState.NotSaving;
            zdoMan.m_endSave = null;
        }

        /// Stand the snapshot down completely and give the world back to vanilla.
        ///
        /// "Completely" matters: the caller is either shutting down or running a save that must
        /// finish, and in both cases vanilla is about to do the whole job itself. Leaving
        /// ReadyForWrite set would have the write path put *our* chunk set on disk while
        /// ZDOMan.DeleteOldChunks worked from vanilla's, which is how an orphaned chunk file
        /// outlives the save that replaced it.
        public static void Abort(string reason)
        {
            bool pending = SnapshotState.InProgress
                        || SnapshotState.ReadyForWrite
                        || ZoneSnapshot.Ready
                        || !SnapshotState.Task.Task.IsCompleted;
            if (!pending) return;

            SnapshotState.InProgress = false;
            SnapshotState.ReadyForWrite = false;
            ZoneSnapshot.Reset();
            RollbackSaveState(ZDOMan.instance);

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
            // One frame of settle time before we start walking; lets the initial patch-triggered
            // path finish stack unwind cleanly.
            yield return null;

            var log = SASettings.Logging.Value;
            bool timings = SASettings.LogTimings;
            var totalStopwatch = Stopwatch.StartNew();

            // Byte-identity is only a well-defined property when nothing can mutate mid-walk, so
            // the verify harness forces the whole snapshot into one un-yielded slice: no yields
            // means no creates, destroys, mutations or sector migrations.
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

            // Decide the chunk partition and which chunks this save writes. Unsliced and
            // deliberately so: it reads the whole sector array to count ZDOs per chunk, and
            // holding that open across frames while sectors mutate would produce a partition
            // that describes no state the world was ever in. A couple of milliseconds.
            var selected = SnapshotState.Selected;
            int totalChunkFiles;
            int selectedZdos;
            ChunkPartition.Compute(zdoMan, selected, out totalChunkFiles, out selectedZdos);

            // Read before BeginSave flips the save state, exactly where vanilla reads it.
            bool portalsDirty = zdoMan.m_dirtyPortalObjects[(int)zdoMan.m_currentSaveState];
            bool writePortalChunk = portalsDirty && zdoMan.m_portalObjects.Count > 0;

            SnapshotState.TotalChunkFiles = totalChunkFiles + (writePortalChunk ? 1 : 0);

            // Stand in for ZDOMan.PrepareSave's bookkeeping. m_saveData has to exist and has to
            // list every chunk we write: SaveCleanup, DeleteOldChunks and
            // DeleteCurrentChunksAndRevert all iterate it, and the last two use it to work out
            // which files to delete. The ZDO lists stay empty - our bytes are in the snapshot
            // buffer, and SaveCleanup only ever calls Reset() on whatever it finds there.
            var saveData = new ZDOMan.SaveData();
            var objectsByChunk = saveData.m_objectsByChunk;
            for (int i = 0; i < selected.Count; ++i)
            {
                objectsByChunk.Add(new Tuple<ZoneSystem.ChunkIndex, List<ZDO>>(selected[i], new List<ZDO>()));
            }
            if (writePortalChunk)
            {
                objectsByChunk.Add(new Tuple<ZoneSystem.ChunkIndex, List<ZDO>>(ZoneSystem.ChunkPortal, new List<ZDO>()));
            }
            // Vanilla's "+ 4" is the four non-chunk files a world save touches: .fwl2, .db2,
            // .chunks and .ok. PreSaveCloudChecksAndOperations sizes the cloud reservation on it.
            saveData.m_numFiles = SnapshotState.TotalChunkFiles + 4;
            saveData.m_dirtyFiles = objectsByChunk.Count + 4;
            zdoMan.m_saveData = saveData;

            // ZDOMan.BeginSave. From here on, anything that dirties a chunk lands in slot 1 and
            // is picked up by the *next* save - which is what makes it safe for us to keep
            // walking across frames. Must come after the partition read slot 0, as it does in
            // vanilla.
            zdoMan.m_currentSaveState = ZDOMan.SaveState.CurrentlySaving;

            int serialized = 0;

            // Pools are empty right now, so each can be sized in one allocation instead of
            // growing into it. Doing this here rather than in ResetForNewSnapshot keeps the
            // allocation off the frame that triggers the save.
            //
            // A frame between them, because together they can be gigabytes of fresh LOH and one
            // block holding all of it is exactly the kind of spike the slice budget cannot see.
            // Verify mode keeps its single un-yielded slice - see above.
            int expectedZdos = EstimateZdoCount(zdoMan, selectedZdos, writePortalChunk);

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

            _portalPrefabs.Clear();
            var portalList = Game.instance.PortalPrefabHash;
            for (int i = 0; i < portalList.Count; ++i) _portalPrefabs.Add(portalList[i]);

            // Main pass, chunk by chunk. Within a chunk we walk its zones' sectors, copying each
            // live List<ZDO> into a pooled scratch atomically (no yield during the copy) and then
            // yielding freely while iterating the stable scratch. That is what protects us from
            // the "ZDO removed mid-yield shifts sector indices and we skip an element" data-loss
            // bug.
            //
            // Sector migration during a yield cuts both ways. Forward - into a sector or chunk we
            // have not reached - puts the ZDO in two scratch copies, and Visited.Add drops the
            // second sighting; that dedup is load-critical, since ZDOMan.LoadChunks feeds every
            // ZDO to a Dictionary.Add that throws on duplicates. Backward - into ground we already
            // covered - would put it in neither, so AddToSectorPatch records it in PendingAdds and
            // the adds pass below picks it up and files it against the right chunk.
            var scratch = SnapshotState.SectorScratch;
            var sectors = zdoMan.m_objectsBySector;
            for (int c = 0; c < selected.Count; ++c)
            {
                int slot = SnapshotState.BeginChunk(selected[c]);

                int x0, y0, span;
                ChunkPartition.ZoneBounds(selected[c], out x0, out y0, out span);

                for (int zy = y0; zy < y0 + span; ++zy)
                {
                    for (int zx = x0; zx < x0 + span; ++zx)
                    {
                        var live = sectors[ZoneSystem.SectorToIndex(zx, zy).Sector];
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
                }

                SnapshotState.EndChunk(slot);
            }
            long chunkMs = totalStopwatch.ElapsedMilliseconds;
            int chunkBlocks = slice.Blocks;

            // Portal chunk. Portals live in their own file rather than in the chunk that covers
            // them, because ConnectPortals has to run over all of them at load. Flattened into
            // scratch in one atomic pass so we never hold a dictionary enumerator across a yield.
            int portalSlot = -1;
            if (writePortalChunk)
            {
                portalSlot = SnapshotState.BeginChunk(ZoneSystem.ChunkPortal);

                scratch.Clear();
                foreach (var kv in zdoMan.m_portalObjects)
                {
                    var live = kv.Value;
                    if (live == null) continue;
                    scratch.AddRange(live);
                }

                int i = 0;
                while (i < scratch.Count)
                {
                    i = SerializePortalRange(scratch, i, ref slice, ref serialized);
                    if (i < scratch.Count)
                    {
                        slice.EndSlice();
                        yield return null;
                        slice.BeginSlice();
                    }
                }

                SnapshotState.EndChunk(portalSlot);
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
            // every entry with a dictionary lookup each to find a handful of changes.
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
            // walked - created mid-snapshot, or migrated backwards past the walk. Each one is
            // filed against the chunk that will hold it; anything landing in a chunk this save
            // is not writing is skipped, because ZDOMan has already marked that chunk dirty for
            // the next save. That is precisely vanilla's own behaviour for anything that moves
            // after PrepareSave, so it loses nothing this format was promising to keep.
            //
            // The list keeps growing while this runs, because a ZDO can migrate during the adds
            // pass itself, so each round takes a fresh high-water mark. Rounds are capped for
            // the same reason the dirty pass caps them.
            {
                var pendingAdds = SnapshotState.PendingAdds;
                int i = 0;
                for (int round = 0; round < MaxCatchUpRounds && i < pendingAdds.Count; ++round)
                {
                    int limit = pendingAdds.Count;
                    while (i < limit)
                    {
                        i = AppendAddsRange(zdoMan, i, limit, portalSlot, ref slice, ref appendedAdds);
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

            // Group the late arrivals by chunk so the write path can merge them with one cursor
            // instead of rescanning the list per chunk. Packed slot-then-index, so the default
            // long ordering is already the order the writer wants.
            SnapshotState.Overflow.Sort();

            long reconMs = totalStopwatch.ElapsedMilliseconds - reconStartMs;
            // +1 for the block still open here: reconcile's tail runs on through the zone
            // capture to the EndSlice below, and that stretch belongs to this phase.
            int reconBlocks = slice.Blocks - reconStartBlocks + 1;

            // Vanilla's ZoneSystem.PrepareSave rehashes every generated zone into a new
            // HashSet and copies every location instance into a new List - tens of ms and a
            // multi-megabyte allocation in a single frame. Capture into pooled arrays
            // instead. Atomic (no yields), same enumeration order, so the bytes match.
            long zoneStartMs = totalStopwatch.ElapsedMilliseconds;
            ZoneSnapshot.Capture(ZoneSystem.instance);
            long zoneMs = totalStopwatch.ElapsedMilliseconds - zoneStartMs;

            // A handful of field copies each - nothing to slice.
            RandEventSystem.instance.PrepareSave();
            PersistentEventSystem.instance.PrepareSave();

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
                         $"chunkWalk={chunkMs - connMs - presizeMs}ms/{chunkBlocks}blocks " +
                         $"portals={reconStartMs - chunkMs}ms " +
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
                Log.Info($"AsyncSave snapshot: {keptCount} ZDOs across {SnapshotState.ChunkCount} dirty " +
                         $"chunk(s) of {SnapshotState.TotalChunkFiles} in {totalStopwatch.ElapsedMilliseconds} ms " +
                         $"over {slice.Blocks} frames (worst block: {slice.WorstBlockMs:0.00} ms).");
            }
            else if (log >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave snapshot: serialized={serialized} reconciled={reconciledMutations} " +
                         $"adds={appendedAdds} removes={SnapshotState.PendingRemoves.Count} kept={keptCount} " +
                         $"chunks={SnapshotState.ChunkCount}/{SnapshotState.TotalChunkFiles} " +
                         $"overflow={SnapshotState.Overflow.Count} " +
                         $"buffer={SnapshotState.Buffer.TotalBytes} bytes cap={SnapshotState.Buffer.Capacity} " +
                         $"bufBlocks={SnapshotState.Buffer.BlockCount} " +
                         $"bytesPerZdo={SnapshotState.BytesPerZdoEma:0.0} " +
                         $"total={totalStopwatch.ElapsedMilliseconds}ms recon={reconMs}ms " +
                         $"blocks={slice.Blocks} worstBlock={slice.WorstBlockMs:0.00}ms");
            }
        }

        /// How many ZDOs this save will serialize, for presizing the pools.
        ///
        /// The partition already counted every sector in the selected chunks, so this is close
        /// to exact rather than an estimate, and it is bounded by what changed rather than by
        /// world size - which is the whole point of the chunked format. An idle world with two
        /// dirty chunks now reserves two chunks' worth of buffer instead of a gigabyte.
        private static int EstimateZdoCount(ZDOMan zdoMan, int selectedZdos, bool portals)
        {
            long total = selectedZdos;
            if (portals)
            {
                foreach (var kv in zdoMan.m_portalObjects)
                {
                    if (kv.Value != null) total += kv.Value.Count;
                }
            }

            // Headroom for what arrives during the walk.
            total += 1024;
            return total > int.MaxValue ? int.MaxValue : (int)total;
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
        // duplicate is fatal on load (ZDOMan.LoadChunks feeds them to Dictionary.Add).

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

        /// One sector's worth of a normal chunk. Portal-prefab ZDOs are excluded here and
        /// collected into the portal chunk instead, exactly as ZDOMan.AddObjectsPerChunk does.
        private static int SerializeRange(List<ZDO> scratch, int from, ref SliceBudget slice,
                                          ref int serialized)
        {
            var visited = SnapshotState.Visited;
            var buffer = SnapshotState.Buffer;
            var entries = SnapshotState.Entries;
            var portals = _portalPrefabs;
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
                if (portals.Contains(zdo.GetPrefab())) continue;
                if (AppendEntry(zdo, visited, buffer, ref entries, ref entryCount)) ++count;
            }

            serialized = count;
            SnapshotState.EntryCount = entryCount;
            return n;
        }

        /// The portal chunk. Vanilla clones every portal object without checking Persistent,
        /// so neither do we.
        private static int SerializePortalRange(List<ZDO> scratch, int from, ref SliceBudget slice,
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

                if (AppendEntry(scratch[i], visited, buffer, ref entries, ref entryCount)) ++count;
            }

            serialized = count;
            SnapshotState.EntryCount = entryCount;
            return n;
        }

        private static int AppendAddsRange(ZDOMan zdoMan, int from, int n, int portalSlot,
                                           ref SliceBudget slice, ref int appended)
        {
            var pendingAdds = SnapshotState.PendingAdds;
            var removes = SnapshotState.PendingRemoves;
            var visited = SnapshotState.Visited;
            var buffer = SnapshotState.Buffer;
            var entries = SnapshotState.Entries;
            var portals = _portalPrefabs;
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
                if (live == null) continue;

                // Which file is this going into? A portal belongs to the portal chunk if we are
                // writing one; anything else goes to whichever selected chunk covers its sector.
                int slot;
                if (portals.Contains(live.GetPrefab()))
                {
                    slot = portalSlot;
                }
                else
                {
                    if (!live.Persistent) continue;
                    slot = SnapshotState.SlotForSector(live.GetSectorIndex());
                }
                if (slot < 0) continue;   // chunk not part of this save; already dirty for the next

                int at = entryCount;
                if (AppendEntry(live, visited, buffer, ref entries, ref entryCount))
                {
                    SnapshotState.AddOverflow(slot, at);
                    ++count;
                }
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

                // Never serialized in the main pass: it is not persistent, it lives in a chunk
                // this save is not writing, or it was created or migrated backwards after the
                // walk passed its sector. In the last case AddToSectorPatch queued it and the
                // adds pass serializes it with current data. Either way there is no record here
                // to rewrite.
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

        /// Full-scan reconciliation: re-check every entry against live state. O(snapshot), and the
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
        /// monotonic within their block, which is what lets the write phase coalesce a whole run
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
