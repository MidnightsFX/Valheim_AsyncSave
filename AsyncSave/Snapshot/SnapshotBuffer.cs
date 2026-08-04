using System;

namespace AsyncSave.Snapshot
{
    /// One growable byte[] reused across saves. Grows monotonically to peak size,
    /// never shrinks - keeps the LOH allocation stable.
    ///
    /// Deliberately has no per-primitive writers. ZdoSerializer sizes each ZDO up front,
    /// calls EnsureFree once, then writes the whole record through a pinned pointer: with
    /// per-primitive writers the bounds check, the Data/Position field reloads and the call
    /// overhead were costing more than the actual stores.
    internal sealed class SnapshotBuffer
    {
        public byte[] Data;
        public int Position;

        public SnapshotBuffer(int initialCapacity)
        {
            Data = new byte[initialCapacity];
            Position = 0;
        }

        public void Reset() => Position = 0;

        /// Size the buffer for a whole snapshot up front. Only valid while Position is 0: it
        /// discards the old array rather than copying it, which is the entire point.
        ///
        /// Growing organically into a 250 MB world is six doublings that allocate half a
        /// gigabyte and BlockCopy a quarter of one, every copy landing mid-slice where the
        /// time budget cannot see it - the last of them moves 128 MB inside a single frame.
        /// Same policy as VisitedSet.EnsureCapacity and SnapshotState.EnsureEntryCapacity;
        /// EnsureFree stays as the backstop for an undercount.
        public void EnsureCapacityDiscard(int bytes)
        {
            if (bytes <= Data.Length) return;
            if (Position != 0) return;   // caller error; growing here would lose the tail

            // Exactly the requested size, not the next power of two. The estimate already
            // carries its own headroom, and rounding a 360 MB request up to 512 MB would make
            // the presize allocate more than the doubling chain it exists to replace.
            Data = new byte[bytes];
        }

        /// Ensure at least `additional` bytes remain past Position.
        ///
        /// Callers must not rely on this preserving anything past Position: growth copies
        /// exactly Position bytes. In particular the in-place reconciliation path in
        /// SnapshotCoroutine rewinds Position into the middle of the buffer, and would lose
        /// the whole tail if a grow happened there. It cannot: that path passes a maxAllowed
        /// bound to ZdoSerializer.Write which rejects the rewrite before reaching here
        /// whenever the record could exceed the space it is overwriting.
        public void EnsureFree(int additional)
        {
            int need = Position + additional;
            if (need <= Data.Length) return;
            int cap = Data.Length;
            if (cap < 1024) cap = 1024;
            while (cap < need) cap *= 2;
            var next = new byte[cap];
            Buffer.BlockCopy(Data, 0, next, 0, Position);
            Data = next;
        }
    }
}
