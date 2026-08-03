using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using AsyncSave.Util;

namespace AsyncSave.Profile
{
    /// The .fch layout constants we hand-write, plus a startup check that vanilla still agrees.
    ///
    /// SavePlayerToDiskPatch reproduces PlayerProfile.SavePlayerToDisk's serialization by hand.
    /// That is character data, so a silent format change in a game update is the one failure
    /// mode worth engineering against. Vanilla writes the version and the stat count as IL
    /// literals, so we read its original instruction stream and confirm they are what we
    /// expect. On any mismatch the whole character-save half stands down and vanilla runs.
    internal static class ProfileFormat
    {
        public const int ProfileVersion = 43;
        public const int PlayerStatCount = 105;
        public const int GlobalKeyCount = 43;
        public const int MapVersion = 8;

        /// Returns false if the character-save half must not engage.
        public static bool Verify()
        {
            var method = AccessTools.DeclaredMethod(typeof(PlayerProfile), "SavePlayerToDisk");
            if (method == null)
            {
                Log.Error("AsyncSave: PlayerProfile.SavePlayerToDisk not found. Async character save disabled.");
                return false;
            }

            if (Minimap.MAPVERSION != MapVersion)
            {
                Log.Error($"AsyncSave: Minimap.MAPVERSION is {Minimap.MAPVERSION}, expected {MapVersion}. " +
                          "The map serialization format changed. Async character save disabled.");
                return false;
            }

            List<CodeInstruction> body;
            try
            {
                body = new List<CodeInstruction>(PatchProcessor.GetOriginalInstructions(method));
            }
            catch (System.Exception ex)
            {
                Log.Error($"AsyncSave: could not read PlayerProfile.SavePlayerToDisk IL ({ex.Message}). " +
                          "Async character save disabled.");
                return false;
            }

            // The first two ZPackage.Write(int) calls in the method are
            //   zpackage.Write(43); zpackage.Write(105);
            // Anchor on the call, not on "the first int literals" - SaveSystem.CheckMove runs
            // first and pushes its own enum arguments and defaults (1, 0, 0, ...).
            var writeInt = AccessTools.DeclaredMethod(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(int) });
            if (writeInt == null)
            {
                Log.Error("AsyncSave: ZPackage.Write(int) not found. Async character save disabled.");
                return false;
            }

            int version = -1;
            int statCount = -1;
            for (int i = 1; i < body.Count; ++i)
            {
                if (!body[i].Calls(writeInt)) continue;
                int value;
                if (!TryGetInt32(body[i - 1], out value)) continue;
                if (version < 0) { version = value; continue; }
                statCount = value;
                break;
            }

            if (version != ProfileVersion || statCount != PlayerStatCount)
            {
                Log.Error($"AsyncSave: character save format looks different from what this build supports " +
                          $"(found version={version} statCount={statCount}, expected {ProfileVersion}/{PlayerStatCount}). " +
                          "Async character save disabled; vanilla character saving is untouched.");
                return false;
            }

            return true;
        }

        private static bool TryGetInt32(CodeInstruction ins, out int value)
        {
            value = 0;
            var op = ins.opcode;
            if (op == OpCodes.Ldc_I4) { value = (int)ins.operand; return true; }
            if (op == OpCodes.Ldc_I4_S) { value = System.Convert.ToInt32(ins.operand); return true; }
            if (op == OpCodes.Ldc_I4_0) { value = 0; return true; }
            if (op == OpCodes.Ldc_I4_1) { value = 1; return true; }
            if (op == OpCodes.Ldc_I4_2) { value = 2; return true; }
            if (op == OpCodes.Ldc_I4_3) { value = 3; return true; }
            if (op == OpCodes.Ldc_I4_4) { value = 4; return true; }
            if (op == OpCodes.Ldc_I4_5) { value = 5; return true; }
            if (op == OpCodes.Ldc_I4_6) { value = 6; return true; }
            if (op == OpCodes.Ldc_I4_7) { value = 7; return true; }
            if (op == OpCodes.Ldc_I4_8) { value = 8; return true; }
            return false;
        }
    }
}
