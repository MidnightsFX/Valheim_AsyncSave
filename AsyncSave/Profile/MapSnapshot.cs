using System.Collections;
using System.IO;
using System.IO.Compression;

namespace AsyncSave.Profile
{
    /// Main-thread capture of the minimap, plus the worker-side expansion and compression.
    ///
    /// Vanilla's Minimap.GetMapData writes m_explored and m_exploredOthers one bool at a time -
    /// at the shipped texture size of 2048 that is 8,388,608 virtual BinaryWriter.Write(bool)
    /// calls into a MemoryStream that starts empty and doubles ~24 times. The bytes it produces
    /// are just 0/1 per bit, and Minimap.SetMapData reads them back with
    /// ReadByteArray(m_explored.Length), so the whole thing is a byte-per-bit block in both
    /// directions - the per-bool loop is only on the write side.
    ///
    /// Since the chunked-save update those two are BitArrays rather than bool[], which is
    /// actually better for us: BitArray.CopyTo packs the whole thing into an int[] in one
    /// memcpy, so the main thread now copies 1 MB instead of 8 MB, and the byte-per-bit
    /// expansion moves to the worker alongside the GZip.
    ///
    /// Everything captured here touches Unity-owned state and must be captured atomically on the
    /// main thread: m_pins is rebuilt every frame by UpdateDynamicPins, and PinData holds
    /// Sprite/RectTransform/GameObject references that must never cross a thread boundary.
    internal static class MapSnapshot
    {
        // Pooled, grown monotonically, never released - same policy as SnapshotBuffer.
        private static byte[] _payload = new byte[0];
        private static int _payloadLength;

        // Packed bits straight out of BitArray.CopyTo, expanded on the worker.
        private static int[] _exploredBits = new int[0];
        private static int[] _exploredOthersBits = new int[0];
        private static int _bitCount;
        private static int _textureSize;
        private static bool _publicPosition;

        private static readonly MemoryStream _pinBlock = new MemoryStream(1024);
        private static int _pinLength;

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

        /// Main thread, no yields. Grabs everything the payload needs; the assembly itself
        /// happens on the worker in Compress.
        public static void Capture(Minimap map)
        {
            // Vanilla bounds BOTH loops by m_explored.Length. The arrays are allocated to the
            // same size in Minimap.Start, but mirror the original exactly.
            int n = map.m_explored.Length;
            _bitCount = n;
            _textureSize = map.m_textureSize;

            int words = (n + 31) / 32;
            if (_exploredBits.Length < words) _exploredBits = new int[words];
            if (_exploredOthersBits.Length < words) _exploredOthersBits = new int[words];
            map.m_explored.CopyTo(_exploredBits, 0);
            map.m_exploredOthers.CopyTo(_exploredOthersBits, 0);

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
            _pinLength = (int)_pinBlock.Length;

            _publicPosition = ZNet.instance.IsReferencePositionPublic();

            HasCapture = true;
        }

        /// Worker thread. Produces exactly what Minimap.GetMapData returns:
        /// the outer ZPackage is [int mapVersion][int compressedLength][gzip bytes], which is
        /// what ZPackage.WriteCompressed emits (length prefix, then the raw buffer).
        public static byte[] Compress()
        {
            BuildPayload();

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
            WriteInt(result, 0, ProfileFormat.MapVersion);
            WriteInt(result, 4, compressed.Length);
            System.Buffer.BlockCopy(compressed, 0, result, 8, compressed.Length);
            return result;
        }

        /// [int textureSize][n explored][n exploredOthers][int pinCount + pins][bool publicPos]
        private static void BuildPayload()
        {
            int n = _bitCount;
            int required = 4 + n + n + _pinLength + 1;
            if (_payload.Length < required) _payload = new byte[required];
            var buf = _payload;

            int p = 0;
            WriteInt(buf, p, _textureSize); p += 4;
            Expand(_exploredBits, buf, p, n); p += n;
            Expand(_exploredOthersBits, buf, p, n); p += n;
            System.Buffer.BlockCopy(_pinBlock.GetBuffer(), 0, buf, p, _pinLength); p += _pinLength;
            buf[p++] = _publicPosition ? (byte)1 : (byte)0;

            _payloadLength = p;
        }

        /// One byte per bit, which is what BinaryWriter.Write(bool) emits. Whole words are
        /// unrolled so the common case never touches the loop counter's division.
        private static void Expand(int[] bits, byte[] dest, int offset, int count)
        {
            int full = count >> 5;
            int at = offset;
            for (int w = 0; w < full; ++w)
            {
                int word = bits[w];
                for (int b = 0; b < 32; ++b)
                {
                    dest[at + b] = (byte)((word >> b) & 1);
                }
                at += 32;
            }

            int remaining = count & 31;
            if (remaining != 0)
            {
                int word = bits[full];
                for (int b = 0; b < remaining; ++b)
                {
                    dest[at + b] = (byte)((word >> b) & 1);
                }
            }
        }

#if DEBUG
        /// Uncompressed payload, for the verify harness only.
        public static byte[] DebugPayloadCopy()
        {
            BuildPayload();
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
