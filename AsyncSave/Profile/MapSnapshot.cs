using System.IO;
using System.IO.Compression;

namespace AsyncSave.Profile
{
    /// Main-thread capture of the minimap, plus the worker-side compression.
    ///
    /// Vanilla's Minimap.GetMapData writes m_explored and m_exploredOthers one bool at a time -
    /// at the shipped texture size of 2048 that is 8,388,608 virtual BinaryWriter.Write(bool)
    /// calls into a MemoryStream that starts empty and doubles ~24 times. Two Buffer.BlockCopy
    /// calls produce the identical byte sequence: a bool[] is one byte per element and
    /// BinaryWriter.Write(bool) emits 0/1. Minimap.SetMapData already reads these blocks back
    /// with ReadByteArray(m_explored.Length), so vanilla itself treats them as a raw byte block
    /// on load - the per-bool loop is only on the write side.
    ///
    /// Only the GZip runs on the worker. Everything here touches Unity-owned state and must be
    /// captured atomically on the main thread: m_pins is rebuilt every frame by
    /// UpdateDynamicPins, and PinData holds Sprite/RectTransform/GameObject references that must
    /// never cross a thread boundary.
    internal static class MapSnapshot
    {
        // Pooled, grown monotonically, never released - same policy as SnapshotBuffer.
        private static byte[] _payload = new byte[0];
        private static int _payloadLength;

        private static readonly MemoryStream _pinBlock = new MemoryStream(1024);

        /// Exactly what BinaryWriter uses by default, which is what ZPackage's writer is
        /// constructed with. Encoding.UTF8 would differ on malformed input (replacement
        /// instead of throw), and pin names are player-supplied text.
        private static readonly System.Text.UTF8Encoding PinEncoding = new System.Text.UTF8Encoding(false, true);

        /// True when Capture produced a payload that has not yet been consumed.
        public static bool HasCapture { get; private set; }

        public static void Reset()
        {
            HasCapture = false;
            _payloadLength = 0;
        }

        /// Main thread, no yields. Assembles the uncompressed map payload exactly as
        /// Minimap.GetMapData's inner ZPackage would.
        public static void Capture(Minimap map)
        {
            // Vanilla bounds BOTH loops by m_explored.Length. The arrays are allocated to the
            // same size in Minimap.Start, but mirror the original exactly.
            int n = map.m_explored.Length;

            // Pins first - the block is small and variable-length, so building it separately
            // lets the two big blocks land at fixed offsets with a straight memcpy.
            _pinBlock.SetLength(0);
            _pinBlock.Position = 0;
            using (var pinWriter = new BinaryWriter(_pinBlock, PinEncoding, true))
            {
                var pins = map.m_pins;
                int saved = 0;
                for (int i = 0; i < pins.Count; ++i)
                {
                    if (pins[i].m_save) ++saved;
                }
                pinWriter.Write(saved);

                for (int i = 0; i < pins.Count; ++i)
                {
                    var pin = pins[i];
                    if (!pin.m_save) continue;
                    pinWriter.Write(pin.m_name);
                    pinWriter.Write(pin.m_pos.x);
                    pinWriter.Write(pin.m_pos.y);
                    pinWriter.Write(pin.m_pos.z);
                    pinWriter.Write((int)pin.m_type);
                    pinWriter.Write(pin.m_checked);
                    pinWriter.Write(pin.m_ownerID);
                    // PlatformUserID is a struct of two strings - ToString is pure managed,
                    // but do it here anyway so nothing from the pin escapes to the worker.
                    pinWriter.Write(pin.m_author.ToString());
                }
                pinWriter.Flush();
            }
            int pinLength = (int)_pinBlock.Length;

            //  [int textureSize][n explored][n exploredOthers][int pinCount + pins][bool publicPos]
            int required = 4 + n + n + pinLength + 1;
            if (_payload.Length < required) _payload = new byte[required];
            var buf = _payload;

            int p = 0;
            WriteInt(buf, p, map.m_textureSize); p += 4;
            System.Buffer.BlockCopy(map.m_explored, 0, buf, p, n); p += n;
            System.Buffer.BlockCopy(map.m_exploredOthers, 0, buf, p, n); p += n;
            System.Buffer.BlockCopy(_pinBlock.GetBuffer(), 0, buf, p, pinLength); p += pinLength;
            buf[p++] = ZNet.instance.IsReferencePositionPublic() ? (byte)1 : (byte)0;

            _payloadLength = p;
            HasCapture = true;
        }

        /// Worker thread. Produces exactly what Minimap.GetMapData returns:
        /// the outer ZPackage is [int MAPVERSION][int compressedLength][gzip bytes], which is
        /// what ZPackage.WriteCompressed emits (length prefix, then the raw buffer).
        public static byte[] Compress()
        {
            byte[] compressed;
            using (var output = new MemoryStream())
            {
                // Must match Utils.Compress: GZip at CompressionLevel.Fastest. Calling
                // GZipStream directly avoids two more full copies of the ~8 MiB payload.
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                {
                    gzip.Write(_payload, 0, _payloadLength);
                }
                compressed = output.ToArray();
            }

            var result = new byte[8 + compressed.Length];
            WriteInt(result, 0, Minimap.MAPVERSION);
            WriteInt(result, 4, compressed.Length);
            System.Buffer.BlockCopy(compressed, 0, result, 8, compressed.Length);
            return result;
        }

#if DEBUG
        /// Uncompressed payload, for the verify harness only.
        public static byte[] DebugPayloadCopy()
        {
            var copy = new byte[_payloadLength];
            System.Buffer.BlockCopy(_payload, 0, copy, 0, _payloadLength);
            return copy;
        }
#endif

        private static void WriteInt(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }
    }
}
