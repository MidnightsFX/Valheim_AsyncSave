#if DEBUG
using System.Text;

namespace AsyncSave.Util
{
    /// Reports the first byte where two serializations diverge. Shared by both verify harnesses -
    /// the world one and the character one - which otherwise carried the same forty lines twice.
    /// Debug builds only, like everything that calls it.
    internal static class ByteDiff
    {
        public static void Report(string what, byte[] ours, int oursLen, byte[] vanilla, int vanillaLen)
        {
            int min = oursLen < vanillaLen ? oursLen : vanillaLen;
            int diff = -1;
            for (int i = 0; i < min; ++i)
            {
                if (ours[i] != vanilla[i]) { diff = i; break; }
            }

            if (diff < 0 && oursLen == vanillaLen)
            {
                Log.Info($"AsyncSave verify: {what} IDENTICAL ({oursLen} bytes).");
                return;
            }

            if (diff < 0)
            {
                Log.Error($"AsyncSave verify: {what} LENGTH MISMATCH - asyncsave={oursLen} " +
                          $"vanilla={vanillaLen}, common prefix of {min} bytes matches.");
                return;
            }

            Log.Error($"AsyncSave verify: {what} FIRST DIFFERENCE at offset {diff} " +
                      $"(asyncsave={oursLen} bytes, vanilla={vanillaLen} bytes)");
            Log.Error($"  asyncsave: {Dump(ours, oursLen, diff)}");
            Log.Error($"  vanilla:   {Dump(vanilla, vanillaLen, diff)}");
        }

        public static void Report(string what, byte[] ours, byte[] vanilla) =>
            Report(what, ours, ours.Length, vanilla, vanilla.Length);

        /// 32 bytes either side of the divergence, with the offending byte in brackets.
        private static string Dump(byte[] buf, int len, int center)
        {
            int from = center - 32; if (from < 0) from = 0;
            int to = center + 32; if (to > len) to = len;

            var sb = new StringBuilder((to - from) * 3 + 16);
            for (int i = from; i < to; ++i)
            {
                if (i == center) sb.Append('[');
                sb.Append(buf[i].ToString("x2"));
                if (i == center) sb.Append(']');
                sb.Append(' ');
            }
            return sb.ToString();
        }
    }
}
#endif
