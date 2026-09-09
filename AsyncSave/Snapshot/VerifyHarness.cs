#if DEBUG
using System.Collections.Generic;
using System.IO;
using AsyncSave.Util;

namespace AsyncSave.Snapshot
{
    /// Debug-only proof that our serialization is byte-identical to vanilla's.
    ///
    /// Only meaningful when the snapshot ran in a single un-yielded slice: with no yields no
    /// ZDO can be created, destroyed, mutated or migrated mid-walk, so our per-chunk entry order
    /// is exactly ZDOMan.GetSaveClonePerChunk order and reconciliation is a no-op.
    /// SnapshotCoroutine forces that when Settings.VerifyBytes is on.
    ///
    /// The world save is no longer a single stream, so this runs in two halves. The ZDO half
    /// writes real chunk files into two scratch directories and diffs them file by file - which
    /// also proves our chunk *partition* matches vanilla's, since a disagreement shows up as a
    /// differing file name rather than differing bytes. The db2 half (zones, random events,
    /// persistent events) is still one stream and is compared in memory.
    ///
    /// Neither run promotes anything: SaveChunks only ever mutates m_chunkSaveMappingCurrent,
    /// which is a clone, and it is ZDOMan.DeleteOldChunks that makes it authoritative. So both
    /// passes start from the same m_chunkSaveMapping and resolve the same filenames, and the
    /// real save afterwards is unaffected.
    internal static class VerifyHarness
    {
        /// While set, our SaveChunks / ZoneSystem.Save prefixes stand down so vanilla's own
        /// serialization runs. Main thread only, and only inside Compare below - the save
        /// thread is still parked on the TCS at that point.
        public static bool Bypass;

        /// Serialize the world both ways and log the first difference.
        /// savedUniqueHashes must have been captured *before* the coroutine called
        /// ZDOExtraData.RegenerateConnectionHashData().
        public static void Compare(ZDOMan zdoMan, int savedUniqueHashes)
        {
            string root = Path.Combine(Path.GetTempPath(), "AsyncSaveVerify");
            string mineDir = Path.Combine(root, "mine") + Path.DirectorySeparatorChar;
            string vanillaDir = Path.Combine(root, "vanilla") + Path.DirectorySeparatorChar;

            try
            {
                FreshDirectory(mineDir);
                FreshDirectory(vanillaDir);

                var mineStream = new MemoryStream();
                bool mineOk = zdoMan.SaveChunks(mineDir, FileHelpers.FileSource.Local);   // our prefix
                using (var w = new BinaryWriter(mineStream, System.Text.Encoding.UTF8, true))
                {
                    ZoneSystem.instance.Save(w);                    // our prefix
                    RandEventSystem.instance.Save(w);               // unpatched
                    PersistentEventSystem.instance.Save(w);         // unpatched
                    w.Flush();
                }

                // ZDOExtraData.GetUniqueHash mixes in s_uniqueHashes, which increments per
                // call and is never reset - so a second RegenerateConnectionHashData would
                // produce different connection hashes and every connected ZDO would appear
                // to differ. Rewind the counter and the used-hash set first.
                ZDOExtraData.s_uniqueHashes = savedUniqueHashes;
                ZDOExtraData.s_usedHashes.Clear();

                // Rebuilds m_saveData - including the cloned ZDO lists our path never made -
                // and re-runs ZDOExtraData.PrepareSave, which is what vanilla's ZDO.Save reads.
                zdoMan.m_currentSaveState = ZDOMan.SaveState.NotSaving;
                zdoMan.PrepareSave();
                ZoneSystem.instance.PrepareSave();
                RandEventSystem.instance.PrepareSave();
                PersistentEventSystem.instance.PrepareSave();

                var vanillaStream = new MemoryStream();
                bool vanillaOk;
                Bypass = true;
                try
                {
                    vanillaOk = zdoMan.SaveChunks(vanillaDir, FileHelpers.FileSource.Local);
                    using (var w = new BinaryWriter(vanillaStream, System.Text.Encoding.UTF8, true))
                    {
                        ZoneSystem.instance.Save(w);
                        RandEventSystem.instance.Save(w);
                        PersistentEventSystem.instance.Save(w);
                        w.Flush();
                    }
                }
                finally
                {
                    Bypass = false;
                }

                if (!mineOk || !vanillaOk)
                {
                    Log.Error($"AsyncSave verify: SaveChunks reported failure (ours={mineOk} vanilla={vanillaOk}); " +
                              "the chunk comparison below may be incomplete.");
                }

                CompareDirectories(mineDir, vanillaDir);

                ByteDiff.Report("world db2", mineStream.GetBuffer(), (int)mineStream.Length,
                                vanillaStream.GetBuffer(), (int)vanillaStream.Length);
            }
            catch (System.Exception ex)
            {
                Log.Error($"AsyncSave verify harness failed: {ex}");
            }
            finally
            {
                // Our buffers were not disturbed - the zone key filter writes to
                // FilteredKeys, not to the capture - so the real write can still proceed.
                SnapshotState.ReadyForWrite = true;
                ZoneSnapshot.Ready = true;

                TryDelete(root);
            }
        }

        private static void CompareDirectories(string mineDir, string vanillaDir)
        {
            var mine = Index(mineDir);
            var vanilla = Index(vanillaDir);

            foreach (var kv in mine)
            {
                if (!vanilla.ContainsKey(kv.Key))
                {
                    Log.Error($"AsyncSave verify: we wrote chunk file \"{kv.Key}\" and vanilla did not. " +
                              "The chunk partition disagrees.");
                }
            }
            foreach (var kv in vanilla)
            {
                if (!mine.ContainsKey(kv.Key))
                {
                    Log.Error($"AsyncSave verify: vanilla wrote chunk file \"{kv.Key}\" and we did not. " +
                              "The chunk partition disagrees.");
                }
            }

            int compared = 0;
            foreach (var kv in mine)
            {
                string other;
                if (!vanilla.TryGetValue(kv.Key, out other)) continue;
                ByteDiff.Report("chunk " + kv.Key, File.ReadAllBytes(kv.Value), File.ReadAllBytes(other));
                ++compared;
            }

            Log.Info($"AsyncSave verify: compared {compared} chunk file(s) " +
                     $"({mine.Count} ours, {vanilla.Count} vanilla).");
        }

        private static Dictionary<string, string> Index(string dir)
        {
            var map = new Dictionary<string, string>();
            foreach (string path in Directory.GetFiles(dir))
            {
                map[Path.GetFileName(path)] = path;
            }
            return map;
        }

        private static void FreshDirectory(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (System.Exception ex)
            {
                Log.Warn($"AsyncSave verify: could not clean up \"{dir}\" ({ex.Message}).");
            }
        }
    }
}
#endif
