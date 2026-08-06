#if DEBUG
using System.IO;
using AsyncSave.Util;

namespace AsyncSave.Snapshot
{
    /// Debug-only proof that our serialization is byte-identical to vanilla's.
    ///
    /// Only meaningful when the snapshot ran in a single un-yielded slice: with no yields no
    /// ZDO can be created, destroyed, mutated or migrated mid-walk, so our entry order is
    /// exactly ZDOMan.GetSaveClone order and reconciliation is a no-op. SnapshotCoroutine
    /// forces that when Settings.VerifyBytes is on.
    internal static class VerifyHarness
    {
        /// While set, our SaveAsync / SaveASync prefixes stand down so vanilla's own
        /// serialization runs. Main thread only, and only inside Compare below - the save
        /// thread is still parked on the TCS at that point.
        public static bool Bypass;

        /// Serialize the world both ways and log the first difference.
        /// savedUniqueHashes must have been captured *before* the coroutine called
        /// ZDOExtraData.RegenerateConnectionHashData().
        public static void Compare(ZDOMan zdoMan, int savedUniqueHashes)
        {
            try
            {
                var mine = new MemoryStream();
                using (var w = new BinaryWriter(mine, System.Text.Encoding.UTF8, true))
                {
                    zdoMan.SaveAsync(w);                        // our prefix
                    ZoneSystem.instance.SaveASync(w);           // our prefix
                    RandEventSystem.instance.SaveAsync(w);      // unpatched
                    w.Flush();
                }

                // ZDOExtraData.GetUniqueHash mixes in s_uniqueHashes, which increments per
                // call and is never reset - so a second RegenerateConnectionHashData would
                // produce different connection hashes and every connected ZDO would appear
                // to differ. Rewind the counter and the used-hash set first.
                ZDOExtraData.s_uniqueHashes = savedUniqueHashes;
                ZDOExtraData.s_usedHashes.Clear();

                zdoMan.PrepareSave();
                ZoneSystem.instance.PrepareSave();
                RandEventSystem.instance.PrepareSave();

                var vanilla = new MemoryStream();
                Bypass = true;
                try
                {
                    using (var w = new BinaryWriter(vanilla, System.Text.Encoding.UTF8, true))
                    {
                        zdoMan.SaveAsync(w);
                        ZoneSystem.instance.SaveASync(w);
                        RandEventSystem.instance.SaveAsync(w);
                        w.Flush();
                    }
                }
                finally
                {
                    Bypass = false;
                }

                ByteDiff.Report("world save", mine.GetBuffer(), (int)mine.Length,
                                vanilla.GetBuffer(), (int)vanilla.Length);
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
            }
        }

    }
}
#endif
