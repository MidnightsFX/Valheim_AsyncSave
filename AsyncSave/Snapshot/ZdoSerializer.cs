using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace AsyncSave.Snapshot
{
    /// Replicates ZDO.Save's byte layout, reading live state (no clone, no snapshot dictionaries).
    /// Layout (must match ZDO.Save at DecompiledProject/assembly_valheim/ZDO.cs:636-685):
    ///   ushort flags | Vector2s sector | Vector3 position | int prefab
    ///   [Vector3 rotation]                                          if flag 0x1000
    ///   if any of flags 0x01..0x80 set, then:
    ///     [byte connectionType | int connectionHash]                if flag 0x01
    ///     [NumItems | (int key, float value)*]                      if flag 0x02
    ///     [NumItems | (int key, Vector3 value)*]                    if flag 0x04
    ///     [NumItems | (int key, Quaternion value)*]                 if flag 0x08
    ///     [NumItems | (int key, int value)*]                        if flag 0x10
    ///     [NumItems | (int key, long value)*]                       if flag 0x20
    ///     [NumItems | (int key, string value)*]                     if flag 0x40
    ///     [NumItems | (int key, byte[] value)*]                     if flag 0x80
    ///
    /// The whole record is sized up front and written through one pinned pointer, so there
    /// is exactly one capacity check and one Position store per ZDO rather than per field.
    /// Multi-byte stores go out in host byte order, which is why AsyncSavePlugin refuses to
    /// enable the mod on a big-endian runtime - vanilla's BinaryWriter is always little-endian.
    internal static unsafe class ZdoSerializer
    {
        /// Smallest a ZDO can encode to: the fixed header alone, with no rotation, no connection
        /// and no extra data. SnapshotCoroutine uses it as the floor when deciding whether a world
        /// could possibly fit in one snapshot buffer.
        public const int MinBytesPerZdo = 22;

        private const int FlagConn     = 0x0001;
        private const int FlagAnyExtra = 0x00FF;
        private const int FlagRotation = 0x1000;

        // ZDO.DataFlags: Type = 3 (bits 0-1, used as a mask), Persistent = 4, Distant = 8.
        // The saved flag word wants Type at bits 10-11, Persistent at 0x100, Distant at 0x200.
        private const int DataFlagsTypeMask = 0x03;
        private const int DataFlagsPersistDistantMask = 0x0C;

        // Unity's Vector3 == is a squared-magnitude compare against kEpsilon squared. Written
        // as !(sq < eps) rather than (sq >= eps) so a NaN component takes the has-rotation
        // branch, exactly as vanilla's `m_rotation != Quaternion.identity.eulerAngles` does.
        private const float UnityEpsilonSq = 9.99999944E-11f;

        /// Serialize one ZDO at the buffer's write cursor. Returns the byte length written, or -1
        /// if the record's worst-case size exceeds maxAllowed - in which case nothing is written
        /// and buf is untouched.
        ///
        /// `block` and `offset` report where the record actually landed, which is not always where
        /// the cursor was on entry: reserving space can roll the buffer to the next block. Callers
        /// must record these rather than sampling buf.Position beforehand.
        public static int Write(ZDO zdo, SnapshotBuffer buf, out int block, out int offset,
                                int maxAllowed = int.MaxValue)
        {
            block = buf.Block;
            offset = buf.Position;
            ZDOID uid = zdo.m_uid;

            BinarySearchDictionary<int, float> floats;
            BinarySearchDictionary<int, Vector3> vec3s;
            BinarySearchDictionary<int, Quaternion> quats;
            BinarySearchDictionary<int, int> ints;
            BinarySearchDictionary<int, long> longs;
            BinarySearchDictionary<int, string> strings;
            BinarySearchDictionary<int, byte[]> bytes;
            ZDOConnectionHashData conn;
            ZDOExtraData.s_floats.TryGetValue(uid, out floats);
            ZDOExtraData.s_vec3.TryGetValue(uid, out vec3s);
            ZDOExtraData.s_quats.TryGetValue(uid, out quats);
            ZDOExtraData.s_ints.TryGetValue(uid, out ints);
            ZDOExtraData.s_longs.TryGetValue(uid, out longs);
            ZDOExtraData.s_strings.TryGetValue(uid, out strings);
            ZDOExtraData.s_byteArrays.TryGetValue(uid, out bytes);
            ZDOExtraData.s_connectionsHashData.TryGetValue(uid, out conn);

            int nFloats  = floats  != null ? floats.Count  : 0;
            int nVec3s   = vec3s   != null ? vec3s.Count   : 0;
            int nQuats   = quats   != null ? quats.Count   : 0;
            int nInts    = ints    != null ? ints.Count    : 0;
            int nLongs   = longs   != null ? longs.Count   : 0;
            int nStrings = strings != null ? strings.Count : 0;
            int nBytes   = bytes   != null ? bytes.Count   : 0;
            bool hasConn = conn != null && conn.m_type != ZDOExtraData.ConnectionType.None;

            Vector3 rot = zdo.m_rotation;
            float rotSq = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z;
            bool hasRot = !(rotSq < UnityEpsilonSq);

            // One read of m_dataFlags replaces three property getters over the same byte.
            int dataFlags = (int)zdo.m_dataFlags;

            int flags = (hasConn ? FlagConn : 0)
                      | (nFloats  > 0 ? 0x0002 : 0)
                      | (nVec3s   > 0 ? 0x0004 : 0)
                      | (nQuats   > 0 ? 0x0008 : 0)
                      | (nInts    > 0 ? 0x0010 : 0)
                      | (nLongs   > 0 ? 0x0020 : 0)
                      | (nStrings > 0 ? 0x0040 : 0)
                      | (nBytes   > 0 ? 0x0080 : 0)
                      | ((dataFlags & DataFlagsPersistDistantMask) << 6)   // 4 -> 0x100, 8 -> 0x200
                      | ((dataFlags & DataFlagsTypeMask) << 10)
                      | (hasRot ? FlagRotation : 0);

            // Fixed header: 2 flags + 4 sector + 12 position + 4 prefab.
            int need = MinBytesPerZdo
                     + (hasRot  ? 12 : 0)
                     + (hasConn ? 5 : 0)
                     + (nFloats > 0 ? 2 + nFloats * 8  : 0)   // key + float
                     + (nVec3s  > 0 ? 2 + nVec3s  * 16 : 0)   // key + 3 floats
                     + (nQuats  > 0 ? 2 + nQuats  * 20 : 0)   // key + 4 floats
                     + (nInts   > 0 ? 2 + nInts   * 8  : 0)
                     + (nLongs  > 0 ? 2 + nLongs  * 12 : 0);

            if (nStrings > 0)
            {
                // key(4) + worst-case 7-bit length prefix(5) + UTF-8 bytes. UTF-8 never
                // exceeds 3 bytes per char within the BMP, and a surrogate pair is 4 bytes
                // for 2 chars, so 3 * Length bounds every string.
                need += 2 + nStrings * 9;
                var values = strings.m_values;
                for (int i = 0; i < nStrings; ++i)
                {
                    string s = values[i];
                    if (s != null) need += s.Length * 3;
                }
            }
            if (nBytes > 0)
            {
                need += 2 + nBytes * 8;   // key(4) + length prefix(4)
                var values = bytes.m_values;
                for (int i = 0; i < nBytes; ++i)
                {
                    byte[] a = values[i];
                    if (a != null) need += a.Length;
                }
            }

            if (need > maxAllowed) return -1;

            // May roll to the next block, which moves both the array and the offset - hence the
            // out params, read back here rather than sampled by the caller before the call.
            buf.EnsureFree(need);
            byte[] data = buf.Data;
            int start = buf.Position;
            block = buf.Block;
            offset = start;

            fixed (byte* origin = data)
            {
                byte* p = origin + start;

                *(ushort*)p = (ushort)flags; p += 2;
                Vector2s sector = zdo.m_sector;
                *(short*)p = sector.x; p += 2;
                *(short*)p = sector.y; p += 2;
                Vector3 pos = zdo.m_position;
                *(float*)p = pos.x; *(float*)(p + 4) = pos.y; *(float*)(p + 8) = pos.z; p += 12;
                *(int*)p = zdo.m_prefab; p += 4;
                if (hasRot)
                {
                    *(float*)p = rot.x; *(float*)(p + 4) = rot.y; *(float*)(p + 8) = rot.z; p += 12;
                }

                if ((flags & FlagAnyExtra) != 0)
                {
                    if (hasConn)
                    {
                        *p = (byte)conn.m_type; ++p;
                        *(int*)p = conn.m_hash; p += 4;
                    }

                    if (nFloats > 0)
                    {
                        p = WriteNumItems(p, nFloats);
                        var keys = floats.m_keys; var values = floats.m_values;
                        for (int i = 0; i < nFloats; ++i)
                        {
                            *(int*)p = keys[i]; *(float*)(p + 4) = values[i]; p += 8;
                        }
                    }
                    if (nVec3s > 0)
                    {
                        p = WriteNumItems(p, nVec3s);
                        var keys = vec3s.m_keys; var values = vec3s.m_values;
                        for (int i = 0; i < nVec3s; ++i)
                        {
                            *(int*)p = keys[i];
                            Vector3 v = values[i];
                            *(float*)(p + 4) = v.x; *(float*)(p + 8) = v.y; *(float*)(p + 12) = v.z;
                            p += 16;
                        }
                    }
                    if (nQuats > 0)
                    {
                        p = WriteNumItems(p, nQuats);
                        var keys = quats.m_keys; var values = quats.m_values;
                        for (int i = 0; i < nQuats; ++i)
                        {
                            *(int*)p = keys[i];
                            Quaternion q = values[i];
                            *(float*)(p + 4) = q.x; *(float*)(p + 8) = q.y;
                            *(float*)(p + 12) = q.z; *(float*)(p + 16) = q.w;
                            p += 20;
                        }
                    }
                    if (nInts > 0)
                    {
                        p = WriteNumItems(p, nInts);
                        var keys = ints.m_keys; var values = ints.m_values;
                        for (int i = 0; i < nInts; ++i)
                        {
                            *(int*)p = keys[i]; *(int*)(p + 4) = values[i]; p += 8;
                        }
                    }
                    if (nLongs > 0)
                    {
                        p = WriteNumItems(p, nLongs);
                        var keys = longs.m_keys; var values = longs.m_values;
                        for (int i = 0; i < nLongs; ++i)
                        {
                            *(int*)p = keys[i]; *(long*)(p + 4) = values[i]; p += 12;
                        }
                    }
                    if (nStrings > 0)
                    {
                        p = WriteNumItems(p, nStrings);
                        var keys = strings.m_keys; var values = strings.m_values;
                        for (int i = 0; i < nStrings; ++i)
                        {
                            *(int*)p = keys[i]; p += 4;
                            p = WriteString(p, values[i] ?? string.Empty);
                        }
                    }
                    if (nBytes > 0)
                    {
                        p = WriteNumItems(p, nBytes);
                        var keys = bytes.m_keys; var values = bytes.m_values;
                        for (int i = 0; i < nBytes; ++i)
                        {
                            *(int*)p = keys[i]; p += 4;
                            byte[] a = values[i];
                            int len = a != null ? a.Length : 0;
                            *(int*)p = len; p += 4;
                            for (int b = 0; b < len; ++b) p[b] = a[b];
                            p += len;
                        }
                    }
                }

                int written = (int)(p - (origin + start));
#if DEBUG
                if (written > need)
                {
                    throw new System.InvalidOperationException(
                        $"ZdoSerializer wrote {written} bytes but reserved {need} - buffer overrun.");
                }
#endif
                buf.Position = start + written;
                return written;
            }
        }

        /// ZPackage.WriteNumItems: 1 byte below 128, otherwise 2.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* WriteNumItems(byte* p, int numItems)
        {
            if (numItems < 128) { *p = (byte)numItems; return p + 1; }
            p[0] = (byte)((numItems >> 8) | 0x80);
            p[1] = (byte)numItems;
            return p + 2;
        }

        /// BinaryWriter.Write(string): 7-bit encoded byte-count prefix, then UTF-8 bytes.
        private static byte* WriteString(byte* p, string s)
        {
            int len = s.Length;
            if (len == 0) { *p = 0; return p + 1; }

            fixed (char* src = s)
            {
                // Fast path: below 128 chars, all-ASCII means byteCount == len and the 7-bit
                // prefix is the single byte len - so one pass writes prefix and payload with
                // no Encoding call at all. Anything written here is fully overwritten by the
                // slow path below, since 1 + byteCount >= 1 + (chars scanned).
                if (len < 128)
                {
                    byte* q = p + 1;
                    int i = 0;
                    while (i < len)
                    {
                        char c = src[i];
                        if (c >= 0x80) break;
                        q[i] = (byte)c;
                        ++i;
                    }
                    if (i == len)
                    {
                        *p = (byte)len;
                        return p + 1 + len;
                    }
                }

                int byteCount = Encoding.UTF8.GetByteCount(src, len);
                uint remaining = (uint)byteCount;
                while (remaining >= 0x80)
                {
                    *p++ = (byte)(remaining | 0x80);
                    remaining >>= 7;
                }
                *p++ = (byte)remaining;
                p += Encoding.UTF8.GetBytes(src, len, p, byteCount);
                return p;
            }
        }
    }
}
