using System;

namespace AsyncSave.Snapshot
{
    /// One growable byte[] reused across saves. Grows monotonically to peak size,
    /// never shrinks - keeps the LOH allocation stable.
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

        /// Ensure at least additional bytes remain past Position.
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

        // Little-endian writers. BinaryWriter (used by ZPackage) is also little-endian
        // on all platforms Unity ships to; we bypass BinaryWriter entirely to avoid its
        // per-call virtuals and to write straight into our reusable byte[].

        public void WriteByte(byte v)
        {
            EnsureFree(1);
            Data[Position++] = v;
        }

        public void WriteSByte(sbyte v)
        {
            EnsureFree(1);
            Data[Position++] = unchecked((byte)v);
        }

        public void WriteBool(bool v)
        {
            EnsureFree(1);
            Data[Position++] = v ? (byte)1 : (byte)0;
        }

        public void WriteShort(short v)
        {
            EnsureFree(2);
            Data[Position++] = (byte)(v & 0xFF);
            Data[Position++] = (byte)((v >> 8) & 0xFF);
        }

        public void WriteUShort(ushort v)
        {
            EnsureFree(2);
            Data[Position++] = (byte)(v & 0xFF);
            Data[Position++] = (byte)((v >> 8) & 0xFF);
        }

        public void WriteInt(int v)
        {
            EnsureFree(4);
            Data[Position++] = (byte)(v & 0xFF);
            Data[Position++] = (byte)((v >> 8) & 0xFF);
            Data[Position++] = (byte)((v >> 16) & 0xFF);
            Data[Position++] = (byte)((v >> 24) & 0xFF);
        }

        public void WriteUInt(uint v)
        {
            EnsureFree(4);
            Data[Position++] = (byte)(v & 0xFF);
            Data[Position++] = (byte)((v >> 8) & 0xFF);
            Data[Position++] = (byte)((v >> 16) & 0xFF);
            Data[Position++] = (byte)((v >> 24) & 0xFF);
        }

        public void WriteLong(long v)
        {
            EnsureFree(8);
            Data[Position++] = (byte)(v & 0xFF);
            Data[Position++] = (byte)((v >> 8) & 0xFF);
            Data[Position++] = (byte)((v >> 16) & 0xFF);
            Data[Position++] = (byte)((v >> 24) & 0xFF);
            Data[Position++] = (byte)((v >> 32) & 0xFF);
            Data[Position++] = (byte)((v >> 40) & 0xFF);
            Data[Position++] = (byte)((v >> 48) & 0xFF);
            Data[Position++] = (byte)((v >> 56) & 0xFF);
        }

        public void WriteFloat(float v)
        {
            EnsureFree(4);
            var bits = new FloatUnion { F = v }.U;
            Data[Position++] = (byte)(bits & 0xFF);
            Data[Position++] = (byte)((bits >> 8) & 0xFF);
            Data[Position++] = (byte)((bits >> 16) & 0xFF);
            Data[Position++] = (byte)((bits >> 24) & 0xFF);
        }

        // BinaryWriter.Write(string) - 7-bit encoded length prefix + UTF-8 bytes.
        // Matches BinaryWriter's exact format so vanilla loader accepts it.
        public void WriteString(string s)
        {
            int byteCount = s.Length == 0 ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);
            // 7-bit length
            uint remaining = (uint)byteCount;
            while (remaining >= 0x80)
            {
                WriteByte((byte)(remaining | 0x80));
                remaining >>= 7;
            }
            WriteByte((byte)remaining);
            if (byteCount == 0) return;
            EnsureFree(byteCount);
            int written = System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, Data, Position);
            Position += written;
        }

        // BinaryWriter.Write(byte[]) writes raw bytes only.
        // ZPackage.Write(byte[]) writes int length prefix + raw bytes.
        public void WriteByteArrayWithLength(byte[] arr)
        {
            WriteInt(arr.Length);
            if (arr.Length == 0) return;
            EnsureFree(arr.Length);
            Buffer.BlockCopy(arr, 0, Data, Position, arr.Length);
            Position += arr.Length;
        }

        // ZPackage.WriteNumItems: 1 or 2 bytes depending on size (< 128).
        public void WriteNumItems(int numItems)
        {
            if (numItems < 128)
            {
                WriteByte((byte)numItems);
            }
            else
            {
                WriteByte((byte)((numItems >> 8) | 0x80));
                WriteByte((byte)numItems);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint U;
        }
    }
}
