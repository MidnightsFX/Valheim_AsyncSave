using System.Collections.Generic;

namespace AsyncSave.Snapshot
{
    /// Decides which chunks this save writes and at what granularity - the half of
    /// ZDOMan.GetSaveClonePerChunk that does not clone anything.
    ///
    /// Vanilla lays the world out as a 64x64 grid of chunks, each covering 8x8 zones, then
    /// coarsens: wherever a 2x2 block of chunks holds fewer than 100k ZDOs between them and no
    /// finer chunk has already been written to disk for that area, the four collapse into one
    /// bigger chunk. Three coarsening rounds give chunk sizes 0 (8x8 zones) through 3 (64x64).
    /// Of the resulting chunks, those in ZDOMan's dirty set are written, and so is any chunk the
    /// chunk save mapping has no file for yet; everything else keeps the .chunk file it already
    /// has on disk.
    ///
    /// This is a literal transcription of DecideChunkSize and the chunk-selection half of
    /// AddObjectsPerChunk (DecompiledProject/assembly_valheim/ZDOMan.cs). It has to stay literal:
    /// the partition decides chunk *filenames*, so a version of this that disagrees with vanilla
    /// by one chunk writes a file the game will not read back, and the ZDOs in it are gone. The
    /// magic numbers below are vanilla's, deliberately un-refactored so the two can be diffed.
    ///
    /// The cost is a 262144-iteration counting pass over the sector array - array indexing and a
    /// List.Count each, a couple of milliseconds even on a large world - plus a few thousand
    /// integer operations. That is why this runs in one unsliced step: slicing it would mean
    /// holding the partition open across frames while sectors mutate underneath it, which is a
    /// much worse problem than the milliseconds it would save.
    internal static class ChunkPartition
    {
        // One entry per chunk at each size. 64x64, 32x32, 16x16, 8x8.
        private static readonly int[] s_count0 = new int[4096];
        private static readonly int[] s_count1 = new int[1024];
        private static readonly int[] s_count2 = new int[256];
        private static readonly int[] s_count3 = new int[64];

        /// Vanilla's c_MaxNumberOfZDOsPerBiggerChunks: a 2x2 block only collapses if the four
        /// together stay under this.
        private const int MaxZdosPerBiggerChunk = 100000;

        /// The sentinel vanilla writes into a coarse slot to mean "cannot collapse here".
        private const int Blocked = -100000000;

        /// Fill `selected` with the chunks this save must write, in vanilla's order (coarsest
        /// granularity last), and report how many chunk files the world has in total - which is
        /// what SaveData.m_numFiles feeds to the cloud-quota check, not the number we write.
        ///
        /// `selectedZdoCount` is how many ZDOs live in the selected chunks, used to presize the
        /// snapshot pools. It counts every ZDO in those sectors, including the non-persistent and
        /// portal-prefab ones the walk will skip, so it always over-reserves slightly - which is
        /// the right direction for a pool that gets reused.
        ///
        /// The portal chunk is not included; it is special-cased by the caller because its
        /// contents come from m_portalObjects rather than from the sector array.
        public static void Compute(ZDOMan zdoMan, List<ZoneSystem.ChunkIndex> selected,
                                   out int totalChunkFiles, out int selectedZdoCount)
        {
            selected.Clear();

            var sectors = zdoMan.m_objectsBySector;
            var count0 = s_count0;
            var count1 = s_count1;
            var count2 = s_count2;
            var count3 = s_count3;

            System.Array.Clear(count0, 0, count0.Length);
            System.Array.Clear(count1, 0, count1.Length);
            System.Array.Clear(count2, 0, count2.Length);
            System.Array.Clear(count3, 0, count3.Length);

            // ZDOs per finest chunk. The world is 512x512 zones; each chunk is 8x8 of them.
            int chunk = 0;
            for (uint zy = 0u; zy < 512u; zy += 8u)
            {
                for (uint zx = 0u; zx < 512u; zx += 8u)
                {
                    int total = 0;
                    for (uint dy = 0u; dy < 8u; ++dy)
                    {
                        for (uint dx = 0u; dx < 8u; ++dx)
                        {
                            var list = sectors[ZoneSystem.IndicesToIndex(zx + dx, zy + dy).Sector];
                            if (list != null) total += list.Count;
                        }
                    }
                    count0[chunk] = total;
                    ++chunk;
                }
            }

            Coarsen(zdoMan, 64u, 1, count0, count1);
            Coarsen(zdoMan, 32u, 2, count1, count2);
            Coarsen(zdoMan, 16u, 3, count2, count3);

            totalChunkFiles = NonEmpty(count0) + NonEmpty(count1) + NonEmpty(count2) + NonEmpty(count3);

            var dirty = zdoMan.m_dirtyChunks[0];
            var mapping = zdoMan.m_chunkSaveMapping;
            long zdos = 0;
            zdos += Select(64, 0, count0, dirty, mapping, selected);
            zdos += Select(32, 1, count1, dirty, mapping, selected);
            zdos += Select(16, 2, count2, dirty, mapping, selected);
            zdos += Select(8, 3, count3, dirty, mapping, selected);
            selectedZdoCount = zdos > int.MaxValue ? int.MaxValue : (int)zdos;
        }

        /// ZDOMan.DecideChunkSize. Collapses 2x2 blocks of `fine` into single slots of `coarse`,
        /// zeroing the four fine slots it consumed.
        ///
        /// Two things block a collapse. The first is that any of the four finer chunks already
        /// exists on disk - written by an earlier save at the finer size - because the game has
        /// no way to merge an existing file into a bigger one. The second is the ZDO ceiling, and
        /// the `num9 != 0 && num7 != 0` guard, which leaves the first row and column of every
        /// coarsening round permanently at the finer size.
        private static void Coarsen(ZDOMan zdoMan, uint size, byte chunkSize, int[] fine, int[] coarse)
        {
            byte finerSize = (byte)(chunkSize - 1);
            uint stride = 64u / size;
            var mapping = zdoMan.m_chunkSaveMapping;

            int slot = 0;
            if (mapping != null)
            {
                for (uint y = 0u; y < size; y += 2u)
                {
                    for (uint x = 0u; x < size; x += 2u)
                    {
                        uint cx = x * stride;
                        uint cy = y * stride;
                        if (mapping.GetNumZDOs(cx, cy, finerSize)
                          + mapping.GetNumZDOs(cx + stride, cy, finerSize)
                          + mapping.GetNumZDOs(cx, cy + stride, finerSize)
                          + mapping.GetNumZDOs(cx + stride, cy + stride, finerSize) > 0)
                        {
                            coarse[slot] = Blocked;
                        }
                        ++slot;
                    }
                }
            }

            slot = 0;
            for (uint y = 0u; y < size; y += 2u)
            {
                uint rowBase = y * size;
                for (uint x = 0u; x < size; x += 2u)
                {
                    if (coarse[slot] >= 0)
                    {
                        int sum = fine[rowBase + x]
                                + fine[1 + rowBase + x]
                                + fine[size + rowBase + x]
                                + fine[size + 1 + rowBase + x];
                        if (x != 0u && y != 0u && sum > 0 && sum < MaxZdosPerBiggerChunk)
                        {
                            coarse[slot] = sum;
                            fine[rowBase + x] = 0;
                            fine[rowBase + x + 1] = 0;
                            fine[rowBase + size + x] = 0;
                            fine[rowBase + size + x + 1] = 0;
                        }
                        else
                        {
                            coarse[slot] = Blocked;
                        }
                    }
                    ++slot;
                }
            }
        }

        /// The chunk-selection half of ZDOMan.AddObjectsPerChunk: every non-empty slot that is
        /// dirty or has no file in the chunk save mapping. Vanilla derives the chunk's x/y from
        /// the slot index the same way. Returns the ZDO count across the chunks it selected.
        ///
        /// The mapping half is in Valheim 1.0.17 and not in 1.0.14. Before it, a populated chunk
        /// that was not dirty and had never been written was skipped, and its ZDOs reached no
        /// file at all. A null mapping counts as "nothing on disk", as it does in Coarsen.
        private static long Select(int size, byte chunkSize, int[] counts,
                                   HashSet<ZoneSystem.ChunkIndex> dirty,
                                   ChunkSaveMapping mapping,
                                   List<ZoneSystem.ChunkIndex> selected)
        {
            int stride = 64 / size;
            long zdos = 0;
            for (int i = 0; i < counts.Length; ++i)
            {
                if (counts[i] <= 0) continue;

                uint x = (uint)(i % size * stride);
                uint y = (uint)(i / size * stride);
                var index = ZoneSystem.ChunkIndexFromXY(x, y, chunkSize);
                if (!dirty.Contains(index) && mapping != null && mapping.Chunks.ContainsKey(index)) continue;

                selected.Add(index);
                zdos += counts[i];
            }
            return zdos;
        }

        /// Vanilla counts a chunk file per non-empty slot. Its Aggregate spelling reads as a
        /// fold over `(acc > 0 ? 1 : 0) + x` with the accumulator and element swapped, which
        /// evaluates to the same thing as this for every array it is given here.
        private static int NonEmpty(int[] counts)
        {
            int n = 0;
            for (int i = 0; i < counts.Length; ++i)
            {
                if (counts[i] > 0) ++n;
            }
            return n;
        }

        /// The zone rectangle a chunk covers: origin zone plus 8 * 2^size zones on each axis.
        public static void ZoneBounds(ZoneSystem.ChunkIndex index, out int x0, out int y0, out int span)
        {
            var origin = ZoneSystem.GetZoneFromChunk(index);
            x0 = origin.x;
            y0 = origin.y;
            span = 8 * (1 << index.m_chunkSize);
        }
    }
}
