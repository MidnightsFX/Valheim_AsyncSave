using System.IO;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Replace ZDOMan.SaveChunks when we have a ready snapshot: write each chunk file from the
    /// pre-serialized byte ranges the coroutine produced, applying the delete skip-list at write
    /// time. Skip vanilla's clone-based path.
    ///
    /// The bookkeeping around the write is vanilla's, deliberately: ChunkSaveMapping decides
    /// filenames from a per-chunk version counter, and ZDOMan.DeleteOldChunks /
    /// DeleteCurrentChunksAndRevert both depend on m_chunkSaveMappingCurrent having been built
    /// the same way. Only the bytes come from us.
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SaveChunks))]
    internal static class SaveChunksPatch
    {
        /// What the last write actually put on disk, for SaveWorldThreadPatch's summary line.
        /// Written on the save thread and read by it, in that order, within one save.
        /// SaveWorldThreadPatch clears these before the body runs so a save that fell through
        /// to vanilla can never report the previous snapshot's figures.
        internal static int LastKeptCount;
        internal static long LastZdoBytes;
        internal static int LastChunkCount;
        internal static bool LastWasSnapshot;

        internal static void ResetWriteStats()
        {
            LastKeptCount = 0;
            LastZdoBytes = 0;
            LastChunkCount = 0;
            LastWasSnapshot = false;
        }

        static bool Prefix(ZDOMan __instance, string path, FileHelpers.FileSource fileSource, ref bool __result)
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

            // Consumed - reset the ready flag up front so any early return below still leaves
            // the next save with a clean slate. Buffer/entries retain their capacity.
            SnapshotState.ReadyForWrite = false;

            __result = WriteAll(__instance, path, fileSource);
            return false;
        }

        private static bool WriteAll(ZDOMan zdoMan, string path, FileHelpers.FileSource fileSource)
        {
            var mapping = zdoMan.m_chunkSaveMapping.Clone();
            zdoMan.m_chunkSaveMappingCurrent = mapping;

            int chunkCount = SnapshotState.ChunkCount;
            var ranges = SnapshotState.ChunkRanges;

            // Bump every chunk's version first, exactly as vanilla does, so a chunk that
            // coarsened this save sees its finer neighbours' versions before any filename is
            // resolved. Doing it inline with the writes would name the first files from a
            // half-updated mapping.
            for (int slot = 0; slot < chunkCount; ++slot)
            {
                mapping.CreateOrUpdate(ranges[slot].Index);
            }

            var entries = SnapshotState.Entries;
            var removes = SnapshotState.PendingRemoves;
            var overflow = SnapshotState.Overflow;
            var buffer = SnapshotState.Buffer;
            bool anyRemoves = removes.Count > 0;

            int totalKept = 0;
            long totalBytes = 0;
            int overflowCursor = 0;
            bool ok = true;

            for (int slot = 0; slot < chunkCount; ++slot)
            {
                var range = ranges[slot];

                // The overflow list is sorted by (slot, entryIndex), so this slot's late
                // arrivals are the run starting at the cursor.
                int overflowStart = overflowCursor;
                while (overflowCursor < overflow.Count && (int)(overflow[overflowCursor] >> 32) == slot)
                {
                    ++overflowCursor;
                }
                int overflowEnd = overflowCursor;

                string filename = mapping.GetChunkFilename(range.Index);
                if (string.IsNullOrEmpty(filename))
                {
                    Log.Error($"AsyncSave: no filename for chunk {range.Index.Chunk} size " +
                              $"{range.Index.m_chunkSize}. Aborting the world save so the partial " +
                              "set of chunk files is rolled back rather than committed.");
                    ok = false;
                    break;
                }

                int kept = CountKept(entries, removes, anyRemoves, range, overflow, overflowStart, overflowEnd);

                long written;
                ok = WriteChunk(path + filename, fileSource, entries, removes, anyRemoves, buffer,
                                range, overflow, overflowStart, overflowEnd, kept, out written);

                mapping.Get(range.Index).m_numZDOs = kept;
                totalKept += kept;
                totalBytes += written;

                if (!ok) break;
            }

            LastKeptCount = totalKept;
            LastZdoBytes = totalBytes;
            LastChunkCount = chunkCount;
            LastWasSnapshot = true;

            if (SASettings.Logging.Value >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave wrote {totalKept} ZDOs into {chunkCount} chunk file(s) " +
                         $"({totalBytes} bytes, {removes.Count} skipped).");
            }

            return ok && mapping.Save(path, fileSource);
        }

        /// How many of a chunk's entries survive the delete skip-list.
        private static int CountKept(SnapshotEntry[] entries, System.Collections.Generic.HashSet<ZDOID> removes,
                                     bool anyRemoves, ChunkRange range,
                                     System.Collections.Generic.List<long> overflow, int ofStart, int ofEnd)
        {
            int total = range.Count + (ofEnd - ofStart);
            if (!anyRemoves) return total;

            int kept = 0;
            int end = range.Start + range.Count;
            for (int i = range.Start; i < end; ++i)
            {
                if (!removes.Contains(entries[i].Uid)) ++kept;
            }
            for (int i = ofStart; i < ofEnd; ++i)
            {
                if (!removes.Contains(entries[(int)overflow[i]].Uid)) ++kept;
            }
            return kept;
        }

        /// One .chunk file. Header is vanilla's - the world version as a short, then the ZDO
        /// count - followed by the records back to back, which is exactly what a sequence of
        /// ZDO.Save calls into the same ZPackage produces.
        private static bool WriteChunk(string fullPath, FileHelpers.FileSource fileSource,
                                       SnapshotEntry[] entries,
                                       System.Collections.Generic.HashSet<ZDOID> removes, bool anyRemoves,
                                       SnapshotBuffer buffer, ChunkRange range,
                                       System.Collections.Generic.List<long> overflow, int ofStart, int ofEnd,
                                       int kept, out long written)
        {
            written = 0;

            var save = SaveFileHelper.CreateZPackageForWriting(fullPath, fileSource);
            var pkg = save.Pkg;
            pkg.Write((short)WorldVersion.Current);
            pkg.Write(kept);

            // Straight to the underlying writer: ZPackage.Write(byte[]) prefixes a length, which
            // is not what a run of concatenated ZDO records wants.
            BinaryWriter raw = pkg.m_writer;

            // Entries are laid down back to back, so in the common case a whole run is one
            // contiguous range and goes out in a single stream write. Runs break only where a
            // ZDO was skipped, where reconciliation could not rewrite a mutated record in place,
            // or at a block boundary.
            var run = new RunWriter(raw, buffer);
            int end = range.Start + range.Count;
            for (int i = range.Start; i < end; ++i)
            {
                var e = entries[i];
                if (anyRemoves && removes.Contains(e.Uid)) { run.Break(); continue; }
                run.Add(e);
            }
            for (int i = ofStart; i < ofEnd; ++i)
            {
                var e = entries[(int)overflow[i]];
                if (anyRemoves && removes.Contains(e.Uid)) { run.Break(); continue; }
                run.Add(e);
            }
            run.Flush();
            written = run.Written;

            return save.Finish();
        }

        /// Coalesces adjacent entries into single stream writes.
        private struct RunWriter
        {
            private readonly BinaryWriter _out;
            private readonly SnapshotBuffer _buffer;
            private int _block;
            private int _offset;
            private int _length;
            public long Written;

            public RunWriter(BinaryWriter output, SnapshotBuffer buffer)
            {
                _out = output;
                _buffer = buffer;
                _block = 0;
                _offset = 0;
                _length = 0;
                Written = 0;
            }

            public void Add(SnapshotEntry e)
            {
                if (_length > 0 && e.Block == _block && e.Offset == _offset + _length)
                {
                    _length += e.Length;
                    return;
                }
                Flush();
                _block = e.Block;
                _offset = e.Offset;
                _length = e.Length;
            }

            public void Break() => Flush();

            public void Flush()
            {
                if (_length == 0) return;
                _out.Write(_buffer.BlockAt(_block), _offset, _length);
                Written += _length;
                _length = 0;
            }
        }
    }

    /// The world version stamped into every chunk file.
    ///
    /// Version.c_WorldVersion is a compile-time const, so referencing it directly would bake
    /// whatever value this DLL was built against into the chunk header and keep writing it after
    /// the game moved on - a file claiming to be an older format than it is, which sends
    /// ZDOMan.LoadChunks down its `newVersion` upgrade path on every load. Read the installed
    /// game's value instead, and fall back to the compiled one if the field is ever renamed.
    internal static class WorldVersion
    {
        public static readonly int Current = Resolve();

        private static int Resolve()
        {
            int compiled = (int)Version.c_WorldVersion;
            try
            {
                var field = AccessTools.DeclaredField(typeof(Version), nameof(Version.c_WorldVersion));
                if (field == null) return compiled;

                int live = (int)field.GetRawConstantValue();
                if (live != compiled)
                {
                    Log.Warn($"AsyncSave: the game's world save version is {live}, but this build was " +
                             $"compiled against {compiled}. Writing {live}, which is what vanilla writes. " +
                             "Update the mod if anything about the save format looks wrong.");
                }
                return live;
            }
            catch (System.Exception ex)
            {
                Log.Warn($"AsyncSave: could not read Version.c_WorldVersion ({ex.Message}); " +
                         $"using the compiled value {compiled}.");
                return compiled;
            }
        }
    }
}
