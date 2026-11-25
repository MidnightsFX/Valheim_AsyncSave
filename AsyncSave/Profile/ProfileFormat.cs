using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using AsyncSave.Util;

namespace AsyncSave.Profile
{
    /// The .fch layout constants we hand-write, plus a startup check that vanilla still agrees.
    ///
    /// SavePlayerToDiskPatch reproduces PlayerProfile.SavePlayerToDisk's serialization by hand.
    /// That is character data, so a silent format change in a game update is the one failure
    /// mode worth engineering against. Vanilla writes the version, the stat count and the
    /// difficulty-slot count as IL literals, and bounds its world-key loop with one, so we read
    /// its original instruction stream and confirm they are what we expect. On any mismatch the
    /// whole character-save half stands down and vanilla runs.
    internal static class ProfileFormat
    {
        public const int ProfileVersion = 47;
        public const int PlayerStatCount = 205;

        /// Stats are kept per achievement difficulty. Slot 0 is the all-difficulties total;
        /// the rest line up with DifficultyRequirement.
        public const int DifficultySlots = 10;

        /// PlayerStats.m_enemyStats is a fixed-length array of dictionaries, and its length is
        /// written into the file ahead of them.
        public const int EnemyStatArrays = 5;

        /// How many GlobalKeys values SavePlayerToDisk folds into m_knownWorldKeys. Not part of
        /// the byte layout - it drives a side effect - but wrong here means the playtime
        /// counters drift from vanilla's. Checked against vanilla's loop bound rather than taken
        /// from GlobalKeys.Count, which is compiled into this DLL and goes stale without a word
        /// when an update adds keys.
        public const int GlobalKeyCount = 56;

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

            var writeInt = AccessTools.DeclaredMethod(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(int) });
            if (writeInt == null)
            {
                Log.Error("AsyncSave: ZPackage.Write(int) not found. Async character save disabled.");
                return false;
            }

            // Minimap.MAPVERSION was a public const until the chunked-save update; the map
            // version is now an inline literal, so it has to be read the same way as the rest.
            var mapMethod = AccessTools.DeclaredMethod(typeof(Minimap), "GetMapData");
            if (mapMethod == null)
            {
                Log.Error("AsyncSave: Minimap.GetMapData not found. Async character save disabled.");
                return false;
            }

            var getGlobalKey = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.GetGlobalKey),
                                                          new[] { typeof(GlobalKeys), typeof(string).MakeByRefType() });
            if (getGlobalKey == null)
            {
                Log.Error("AsyncSave: ZoneSystem.GetGlobalKey(GlobalKeys, out string) not found. Async character save disabled.");
                return false;
            }

            List<CodeInstruction> profileBody, mapBody;
            if (!ReadBody(method, "PlayerProfile.SavePlayerToDisk", out profileBody)) return false;
            if (!ReadBody(mapMethod, "Minimap.GetMapData", out mapBody)) return false;

            int[] profileLiterals;
            if (!FirstWriteIntLiterals(profileBody, writeInt, 3, "PlayerProfile.SavePlayerToDisk", out profileLiterals)) return false;

            int[] mapLiterals;
            if (!FirstWriteIntLiterals(mapBody, writeInt, 1, "Minimap.GetMapData", out mapLiterals)) return false;

            int keyCount;
            if (!GlobalKeyLoopBound(profileBody, getGlobalKey, out keyCount)) return false;

            int version = profileLiterals[0];
            int statCount = profileLiterals[1];
            int slots = profileLiterals[2];
            int mapVersion = mapLiterals[0];

            if (version != ProfileVersion || statCount != PlayerStatCount || slots != DifficultySlots)
            {
                Log.Error("AsyncSave: character save format looks different from what this build supports " +
                          $"(found version={version} statCount={statCount} difficultySlots={slots}, expected " +
                          $"{ProfileVersion}/{PlayerStatCount}/{DifficultySlots}). Async character save " +
                          "disabled; vanilla character saving is untouched.");
                return false;
            }

            if (mapVersion != MapVersion)
            {
                Log.Error($"AsyncSave: Minimap.GetMapData writes map version {mapVersion}, expected " +
                          $"{MapVersion}. The map serialization format changed. Async character save disabled.");
                return false;
            }

            if (keyCount != GlobalKeyCount)
            {
                Log.Error($"AsyncSave: PlayerProfile.SavePlayerToDisk counts playtime for {keyCount} world keys, " +
                          $"expected {GlobalKeyCount}. Async character save disabled; vanilla character saving " +
                          "is untouched.");
                return false;
            }

            return true;
        }

        private static bool ReadBody(MethodBase method, string what, out List<CodeInstruction> body)
        {
            try
            {
                body = new List<CodeInstruction>(PatchProcessor.GetOriginalInstructions(method));
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Error($"AsyncSave: could not read {what} IL ({ex.Message}). " +
                          "Async character save disabled.");
                body = null;
                return false;
            }
        }

        /// The first `count` integer literals `body` passes to ZPackage.Write(int).
        ///
        /// Anchored on the call rather than on "the first int literals in the body", because
        /// the prologue pushes plenty of its own: SaveSystem.PreSaveCloudChecksAndOperations
        /// alone contributes an enum argument and a literal 1.
        private static bool FirstWriteIntLiterals(List<CodeInstruction> body, MethodInfo writeInt, int count,
                                                  string what, out int[] values)
        {
            values = null;

            var found = new int[count];
            int n = 0;
            for (int i = 1; i < body.Count && n < count; ++i)
            {
                if (!body[i].Calls(writeInt)) continue;
                int value;
                if (!TryGetInt32(body[i - 1], out value)) continue;
                found[n++] = value;
            }

            if (n < count)
            {
                Log.Error($"AsyncSave: expected {count} leading ZPackage.Write(int) literals in {what} " +
                          $"but found {n}. Async character save disabled.");
                return false;
            }

            values = found;
            return true;
        }

        /// The literal the world-key loop compares its index with. A for loop compiles its test
        /// to the bottom of the body, so it is the first `ldc.i4 N; blt` after the loop's
        /// GetGlobalKey call.
        private static bool GlobalKeyLoopBound(List<CodeInstruction> body, MethodInfo getGlobalKey, out int bound)
        {
            bound = 0;
            int call = body.FindIndex(ins => ins.Calls(getGlobalKey));
            for (int i = call + 1; call >= 0 && i + 1 < body.Count; ++i)
            {
                var next = body[i + 1].opcode;
                if ((next == OpCodes.Blt || next == OpCodes.Blt_S) && TryGetInt32(body[i], out bound)) return true;
            }

            Log.Error("AsyncSave: could not find the world-key loop in PlayerProfile.SavePlayerToDisk. " +
                      "Async character save disabled.");
            return false;
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
