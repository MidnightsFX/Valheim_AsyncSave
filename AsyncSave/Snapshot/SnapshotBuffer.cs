using System;
using System.Collections.Generic;

namespace AsyncSave.Snapshot
{
    /// The snapshot's byte store: a list of large blocks, reused across saves. Blocks grow in
    /// number to the peak the session has needed and are never returned until the world unloads.
    ///
    /// Blocks rather than one array, because one array cannot hold a big world. A .NET array is
    /// capped at 2 GB per object and Mono has no large-array escape hatch, so at the ~54 bytes a
    /// ZDO encodes to in practice a single buffer runs out somewhere under 40 M ZDOs - and the old
    /// code did not run out gracefully: the doubling loop overflowed int and either spun or raised
    /// a negative array length from inside a pinned-pointer write. Blocks also allocate better,
    /// since forty 64 MB requests are forty untouched zero-page mappings where one 2.6 GB request
    /// is a single allocation that has to succeed outright.
    ///
    /// A record never straddles a block, because ZdoSerializer writes each one through a single
    /// pinned pointer. When the current block cannot fit the next record the buffer rolls to the
    /// next one and abandons the tail - at most one record's worth, a few dozen bytes.
    ///
    /// Deliberately has no per-primitive writers. ZdoSerializer sizes each ZDO up front, calls
    /// EnsureFree once, then writes the whole record through that pointer: with per-primitive
    /// writers the bounds check, the field reloads and the call overhead were costing more than
    /// the actual stores.
    internal sealed class SnapshotBuffer
    {
        /// Size of every block after the first. 64 MB is small enough that the LOH is not asked
        /// for anything awkward and large enough that even a 2.6 GB world needs only ~40 of them.
        public const int BlockBytes = 64 * 1024 * 1024;

        /// The block being written and the offset within it. Callers do not compute an absolute
        /// position from these - a record's home is the (Block, Offset) pair, which is what
        /// SnapshotEntry stores, because the first block may be a different size from the rest.
        public byte[] Data;
        public int Position;
        public int Block;

        private readonly List<byte[]> _blocks = new List<byte[]>();

        /// Bytes written in blocks before the current one. Kept incrementally because block
        /// lengths and the bytes actually used in them are not the same thing once a roll has
        /// abandoned a tail.
        private long _committed;

        public SnapshotBuffer(int initialCapacity)
        {
            _blocks.Add(new byte[initialCapacity]);
            Data = _blocks[0];
        }

        /// Total bytes written this snapshot, across every block.
        public long TotalBytes => _committed + Position;

        /// Bytes currently allocated across every block. Diagnostic only.
        public long Capacity
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _blocks.Count; ++i) total += _blocks[i].Length;
                return total;
            }
        }

        public int BlockCount => _blocks.Count;

        /// The backing array for a block, for the write path.
        public byte[] BlockAt(int index) => _blocks[index];

        public void Reset()
        {
            Block = 0;
            Position = 0;
            _committed = 0;
            Data = _blocks[0];
        }

        /// Drop back to a single small block. Called when a world is unloaded: growing and staying
        /// grown is the right policy across the saves of one session, but holding gigabytes for
        /// the rest of the process because the player once visited a big world is not.
        public void Release(int capacity)
        {
            _blocks.Clear();
            _blocks.Add(new byte[capacity]);
            Reset();
        }

        /// Size the store for a whole snapshot up front. Only valid before the walk starts: it
        /// replaces block 0 rather than preserving it.
        ///
        /// Growing organically into a 250 MB world used to be six doublings that allocated half a
        /// gigabyte and BlockCopy'd a quarter of one, every copy landing mid-slice where the time
        /// budget cannot see it. Blocks removed the copy - a roll allocates and moves nothing -
        /// but allocating them up front still keeps the allocations off the slices.
        ///
        /// A world that fits in one block gets exactly one right-sized block, so a small world
        /// holds no more than it did before blocks existed.
        public void EnsureCapacity(long bytes)
        {
            if (Position != 0 || Block != 0) return;   // caller error; would strand written bytes

            if (bytes <= BlockBytes)
            {
                if (_blocks[0].Length < bytes) _blocks[0] = new byte[(int)bytes];
            }
            else
            {
                if (_blocks[0].Length < BlockBytes) _blocks[0] = new byte[BlockBytes];
                int want = (int)((bytes + BlockBytes - 1) / BlockBytes);
                while (_blocks.Count < want) _blocks.Add(new byte[BlockBytes]);
            }

            Data = _blocks[0];
        }

        /// Ensure `additional` bytes are free at Position, rolling to the next block if they are
        /// not. Callers must read Block and Position *after* this, never before: a roll moves both.
        ///
        /// Nothing past Position survives a roll, and the in-place reconciliation path in
        /// SnapshotCoroutine depends on never triggering one. It cannot: that path passes the old
        /// record's length as maxAllowed to ZdoSerializer.Write, which rejects the rewrite before
        /// reaching here whenever the new encoding could exceed the space it is overwriting - so
        /// the bytes it needs are always still inside the block it is already in.
        public void EnsureFree(int additional)
        {
            if ((long)Position + additional <= Data.Length) return;

            _committed += Position;
            ++Block;

            // A single record larger than a block gets a block of its own. Nothing in Valheim's
            // ZDO data comes close, but the alternative is a record that cannot be written at all.
            int want = additional > BlockBytes ? additional : BlockBytes;
            if (Block < _blocks.Count)
            {
                if (_blocks[Block].Length < want) _blocks[Block] = new byte[want];
            }
            else
            {
                _blocks.Add(new byte[want]);
            }

            Data = _blocks[Block];
            Position = 0;
        }

        /// Move the write cursor onto an existing record so it can be overwritten in place.
        /// Always paired with a second call restoring the cursor that was there - see RewriteEntry.
        /// Does not touch _committed, so TotalBytes is only meaningful once the cursor is back.
        public void Seek(int block, int offset)
        {
            Block = block;
            Position = offset;
            Data = _blocks[block];
        }
    }
}
