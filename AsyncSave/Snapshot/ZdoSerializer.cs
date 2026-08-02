using System.Collections.Generic;
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
    internal static class ZdoSerializer
    {
        private const ushort FlagConn      = 0x0001;
        private const ushort FlagFloats    = 0x0002;
        private const ushort FlagVec3s     = 0x0004;
        private const ushort FlagQuats     = 0x0008;
        private const ushort FlagInts      = 0x0010;
        private const ushort FlagLongs     = 0x0020;
        private const ushort FlagStrings   = 0x0040;
        private const ushort FlagBytes     = 0x0080;
        private const ushort FlagAnyExtra  = 0x00FF;
        private const ushort FlagPersist   = 0x0100;
        private const ushort FlagDistant   = 0x0200;
        // Type occupies bits 10-11 (<< 10, mask 0x0C00 after masking with 3).
        private const int    TypeShift     = 10;
        private const ushort FlagRotation  = 0x1000;

        private static readonly Vector3 ZeroRotation = Quaternion.identity.eulerAngles;

        /// Serialize one ZDO into buf starting at buf.Position. Returns the byte length written.
        public static int Write(ZDO zdo, SnapshotBuffer buf)
        {
            int start = buf.Position;
            ZDOID uid = zdo.m_uid;

            // Compute flags from live extra data.
            BinarySearchDictionary<int, float> floats;
            BinarySearchDictionary<int, Vector3> vec3s;
            BinarySearchDictionary<int, Quaternion> quats;
            BinarySearchDictionary<int, int> ints;
            BinarySearchDictionary<int, long> longs;
            BinarySearchDictionary<int, string> strings;
            BinarySearchDictionary<int, byte[]> bytes;
            ZDOExtraData.s_floats.TryGetValue(uid, out floats);
            ZDOExtraData.s_vec3.TryGetValue(uid, out vec3s);
            ZDOExtraData.s_quats.TryGetValue(uid, out quats);
            ZDOExtraData.s_ints.TryGetValue(uid, out ints);
            ZDOExtraData.s_longs.TryGetValue(uid, out longs);
            ZDOExtraData.s_strings.TryGetValue(uid, out strings);
            ZDOExtraData.s_byteArrays.TryGetValue(uid, out bytes);
            ZDOConnectionHashData conn;
            ZDOExtraData.s_connectionsHashData.TryGetValue(uid, out conn);

            ushort flags = 0;
            if (conn != null && conn.m_type != ZDOExtraData.ConnectionType.None) flags |= FlagConn;
            if (floats != null && floats.Count > 0)    flags |= FlagFloats;
            if (vec3s != null && vec3s.Count > 0)      flags |= FlagVec3s;
            if (quats != null && quats.Count > 0)      flags |= FlagQuats;
            if (ints != null && ints.Count > 0)        flags |= FlagInts;
            if (longs != null && longs.Count > 0)      flags |= FlagLongs;
            if (strings != null && strings.Count > 0)  flags |= FlagStrings;
            if (bytes != null && bytes.Count > 0)      flags |= FlagBytes;
            if (zdo.Persistent) flags |= FlagPersist;
            if (zdo.Distant)    flags |= FlagDistant;
            flags |= (ushort)(((int)zdo.Type & 3) << TypeShift);
            Vector3 rot = zdo.m_rotation;
            bool hasRot = rot != ZeroRotation;
            if (hasRot) flags |= FlagRotation;

            buf.WriteUShort(flags);
            // Vector2s = 2 shorts
            buf.WriteShort(zdo.m_sector.x);
            buf.WriteShort(zdo.m_sector.y);
            // Vector3 position
            Vector3 pos = zdo.m_position;
            buf.WriteFloat(pos.x); buf.WriteFloat(pos.y); buf.WriteFloat(pos.z);
            buf.WriteInt(zdo.m_prefab);
            if (hasRot)
            {
                buf.WriteFloat(rot.x); buf.WriteFloat(rot.y); buf.WriteFloat(rot.z);
            }

            if ((flags & FlagAnyExtra) == 0)
            {
                return buf.Position - start;
            }

            if ((flags & FlagConn) != 0)
            {
                buf.WriteByte((byte)conn.m_type);
                buf.WriteInt(conn.m_hash);
            }

            if ((flags & FlagFloats) != 0)   WriteFloats(buf, floats);
            if ((flags & FlagVec3s) != 0)    WriteVec3s(buf, vec3s);
            if ((flags & FlagQuats) != 0)    WriteQuats(buf, quats);
            if ((flags & FlagInts) != 0)     WriteInts(buf, ints);
            if ((flags & FlagLongs) != 0)    WriteLongs(buf, longs);
            if ((flags & FlagStrings) != 0)  WriteStrings(buf, strings);
            if ((flags & FlagBytes) != 0)    WriteBytes(buf, bytes);

            return buf.Position - start;
        }

        private static void WriteFloats(SnapshotBuffer buf, BinarySearchDictionary<int, float> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                buf.WriteFloat(vals[i]);
            }
        }

        private static void WriteVec3s(SnapshotBuffer buf, BinarySearchDictionary<int, Vector3> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                Vector3 v = vals[i];
                buf.WriteFloat(v.x); buf.WriteFloat(v.y); buf.WriteFloat(v.z);
            }
        }

        private static void WriteQuats(SnapshotBuffer buf, BinarySearchDictionary<int, Quaternion> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                Quaternion q = vals[i];
                buf.WriteFloat(q.x); buf.WriteFloat(q.y); buf.WriteFloat(q.z); buf.WriteFloat(q.w);
            }
        }

        private static void WriteInts(SnapshotBuffer buf, BinarySearchDictionary<int, int> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                buf.WriteInt(vals[i]);
            }
        }

        private static void WriteLongs(SnapshotBuffer buf, BinarySearchDictionary<int, long> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                buf.WriteLong(vals[i]);
            }
        }

        private static void WriteStrings(SnapshotBuffer buf, BinarySearchDictionary<int, string> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                buf.WriteString(vals[i] ?? string.Empty);
            }
        }

        private static void WriteBytes(SnapshotBuffer buf, BinarySearchDictionary<int, byte[]> d)
        {
            int n = d.Count;
            buf.WriteNumItems(n);
            var keys = d.m_keys; var vals = d.m_values;
            for (int i = 0; i < n; ++i)
            {
                buf.WriteInt(keys[i]);
                buf.WriteByteArrayWithLength(vals[i] ?? System.Array.Empty<byte>());
            }
        }
    }
}
