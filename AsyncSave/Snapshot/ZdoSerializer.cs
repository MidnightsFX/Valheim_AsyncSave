using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace AsyncSave.Snapshot
{
    /// Replicates ZDO.Save's byte layout, reading live state (no clone, no snapshot dictionaries).
    /// Layout (must match ZDO.Save at DecompiledProject/assembly_valheim/ZDO.cs, `public void
    /// Save(ZPackage pkg)`):
    ///   ushort flags
    ///   [short x, short y]   if SmallPosition (0x2000), else [float x, y, z]
    ///   int prefab
    ///   [small rotation, 2 or 4 bytes]                              if Rotation (0x1000)
    ///   if any of flags 0x01..0x80 set, then:
    ///     [byte connectionType | int connectionHash]                if Connections (0x01)
    ///     [NumItems | (int key, float value)*]                      if Floats      (0x02)
    ///     [NumItems | (int key, Vector3 value)*]                    if Vec3        (0x04)
    ///     [NumItems | (int key, Quaternion value)*]                 if Quaternions (0x08)
    ///     [NumItems | (int key, int value)*]                        if Ints        (0x10)
    ///     [NumItems | (int key, long value)*]                       if Longs       (0x20)
    ///     [NumItems | (int key, string value)*]                     if Strings     (0x40)
    ///     [NumItems | (int key, byte[] value)*]                     if ByteArrays  (0x80)
    ///
    /// Three things changed with the chunked save format and are worth calling out, because each
    /// is a silent corruption if it drifts back:
    ///
    ///  - The sector field is gone. It used to sit between the flags and the position; ZDOMan
    ///    now recomputes it from the position on load.
    ///  - Position is packed to a Vector2s when it round-trips exactly and y is zero, which is
    ///    the common case for anything sitting on the ground. Rotation is packed by
    ///    ZPackage.WriteSmallRotation. Both decisions come from the game's own helpers
    ///    (Utils.SmallPosition / Utils.CloseToZero) rather than being reimplemented - they are
    ///    bit-exact float predicates and reimplementing them is how you lose a world.
    ///  - Extra data is filtered against ZDOExtraData.s_sessionOnly. Vanilla does this in
    ///    GetSaveData, after PrepareSave has cloned; we do it inline, which means the section
    ///    flag has to be decided on the *filtered* count, not the raw one.
    ///
    /// The whole record is sized up front and written through one pinned pointer, so there
    /// is exactly one capacity check and one Position store per ZDO rather than per field.
    /// Multi-byte stores go out in host byte order, which is why AsyncSavePlugin refuses to
    /// enable the mod on a big-endian runtime - vanilla's BinaryWriter is always little-endian.
    internal static unsafe class ZdoSerializer
    {
        /// Smallest a ZDO can encode to: flags, a packed position and the prefab, with no
        /// rotation, no connection and no extra data. SnapshotCoroutine uses it as the floor when
        /// deciding whether a chunk could possibly fit in one snapshot buffer.
        public const int MinBytesPerZdo = 10;

        private const int FlagConn     = 0x0001;
        private const int FlagAnyExtra = 0x00FF;
        private const int FlagRotation = 0x1000;
        private const int FlagSmallPos = 0x2000;

        // ZDO.DataFlags: Type = 3 (bits 0-1, used as a mask), Persistent = 4, Distant = 8.
        // The saved flag word wants Type at bits 10-11, Persistent at 0x100, Distant at 0x200.
        private const int DataFlagsTypeMask = 0x03;
        private const int DataFlagsPersistDistantMask = 0x0C;

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

            // Session-only keys are dropped from the save, so every count below is the count of
            // keys that survive the filter. s_sessionOnly is a couple of dozen entries and almost
            // never hits, but it has to be consulted per key: vanilla's GetSaveData does.
            var sessionOnly = ZDOExtraData.s_sessionOnly;
            bool anySession = sessionOnly.Count > 0;

            int nFloats  = Kept(floats,  sessionOnly, anySession);
            int nVec3s   = Kept(vec3s,   sessionOnly, anySession);
            int nQuats   = Kept(quats,   sessionOnly, anySession);
            int nInts    = Kept(ints,    sessionOnly, anySession);
            int nLongs   = Kept(longs,   sessionOnly, anySession);
            int nStrings = Kept(strings, sessionOnly, anySession);
            int nBytes   = Kept(bytes,   sessionOnly, anySession);
            bool hasConn = conn != null && conn.m_type != ZDOExtraData.ConnectionType.None;

            Vector3 rot = zdo.m_rotation;
            bool hasRot = !rot.CloseToZero();

            Vector3 pos = zdo.m_position;
            bool smallPos;
            Vector2s packedPos;
            {
                var sp = pos.SmallPosition();
                smallPos = sp.Item1;
                packedPos = sp.Item2;
            }

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
                      | (hasRot ? FlagRotation : 0)
                      | (smallPos ? FlagSmallPos : 0);

            // Fixed header: 2 flags + 4 or 12 position + 4 prefab. Rotation is budgeted at its
            // 4-byte worst case; the 2-byte form just leaves two bytes of the reservation unused.
            int need = 2 + (smallPos ? 4 : 12) + 4
                     + (hasRot  ? 4 : 0)
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
                var keys = strings.m_keys;
                var values = strings.m_values;
                int raw = strings.Count;
                for (int i = 0; i < raw; ++i)
                {
                    if (anySession && sessionOnly.Contains(keys[i])) continue;
                    string s = values[i];
                    if (s != null) need += s.Length * 3;
                }
            }
            if (nBytes > 0)
            {
                need += 2 + nBytes * 8;   // key(4) + length prefix(4)
                var keys = bytes.m_keys;
                var values = bytes.m_values;
                int raw = bytes.Count;
                for (int i = 0; i < raw; ++i)
                {
                    if (anySession && sessionOnly.Contains(keys[i])) continue;
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
                if (smallPos)
                {
                    *(short*)p = packedPos.x; *(short*)(p + 2) = packedPos.y; p += 4;
                }
                else
                {
                    *(float*)p = pos.x; *(float*)(p + 4) = pos.y; *(float*)(p + 8) = pos.z; p += 12;
                }
                *(int*)p = zdo.m_prefab; p += 4;
                if (hasRot) p = WriteSmallRotation(p, rot);

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
                        int raw = floats.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
                            *(int*)p = keys[i]; *(float*)(p + 4) = values[i]; p += 8;
                        }
                    }
                    if (nVec3s > 0)
                    {
                        p = WriteNumItems(p, nVec3s);
                        var keys = vec3s.m_keys; var values = vec3s.m_values;
                        int raw = vec3s.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
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
                        int raw = quats.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
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
                        int raw = ints.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
                            *(int*)p = keys[i]; *(int*)(p + 4) = values[i]; p += 8;
                        }
                    }
                    if (nLongs > 0)
                    {
                        p = WriteNumItems(p, nLongs);
                        var keys = longs.m_keys; var values = longs.m_values;
                        int raw = longs.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
                            *(int*)p = keys[i]; *(long*)(p + 4) = values[i]; p += 12;
                        }
                    }
                    if (nStrings > 0)
                    {
                        p = WriteNumItems(p, nStrings);
                        var keys = strings.m_keys; var values = strings.m_values;
                        int raw = strings.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
                            *(int*)p = keys[i]; p += 4;
                            p = WriteString(p, values[i] ?? string.Empty);
                        }
                    }
                    if (nBytes > 0)
                    {
                        p = WriteNumItems(p, nBytes);
                        var keys = bytes.m_keys; var values = bytes.m_values;
                        int raw = bytes.Count;
                        for (int i = 0; i < raw; ++i)
                        {
                            if (anySession && sessionOnly.Contains(keys[i])) continue;
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

        /// How many of a section's keys survive the session-only filter. Returns the raw count
        /// untouched when nothing is registered as session-only, which is the overwhelmingly
        /// common case and skips the per-key probe entirely.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Kept<T>(BinarySearchDictionary<int, T> d, HashSet<int> sessionOnly, bool anySession)
        {
            if (d == null) return 0;
            int n = d.Count;
            if (!anySession || n == 0) return n;

            var keys = d.m_keys;
            int kept = 0;
            for (int i = 0; i < n; ++i)
            {
                if (!sessionOnly.Contains(keys[i])) ++kept;
            }
            return kept;
        }

        /// ZPackage.WriteSmallRotation. Two forms: a single short when both pitch and roll are
        /// within half a degree of 0 or 360 (the flat case, which is most placed pieces), and
        /// otherwise three 10-bit fields packed into a uint and written as two ushorts, high
        /// half first. That high-half-first order is not a byte-order accident - vanilla writes
        /// two separate little-endian ushorts - so it has to be reproduced exactly.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* WriteSmallRotation(byte* p, Vector3 v3)
        {
            v3 *= 2f;
            uint x = (uint)v3.x;
            uint y = (uint)v3.y;
            uint z = (uint)v3.z;
            if ((x <= 1 || x >= 719) && (z <= 1 || z >= 719))
            {
                y |= 0x8000;
                *(ushort*)p = (ushort)y;
                return p + 2;
            }
            uint packed = x | (y << 10) | (z << 20);
            *(ushort*)p = (ushort)(packed >> 16);
            *(ushort*)(p + 2) = (ushort)packed;
            return p + 4;
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
