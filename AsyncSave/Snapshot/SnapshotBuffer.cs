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
