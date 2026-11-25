using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Splatform;
using AsyncSave.Util;

namespace AsyncSave.Patches
{
    /// Splatform.Steam.SteamCloud.WriteFile allocates a 100 MiB staging buffer on every call,
    /// whatever the file's size, copies the data into it and hands Steam that buffer in 100 MiB
    /// chunks. Every Steam Cloud write in a save pays for it - the .fch three times, each chunk
    /// file, .db2, .fwl2, .chunks, .ok, and every file the auto-backup copies - so one autosave of
    /// a cloud world can allocate gigabytes. In game, the eight writes of a small save took
    /// 330-380 ms with it and about 145 ms with the buffer sized to the file.
    ///
    /// On our worker threads it also stalls the main thread. Each allocation that large makes
    /// Mono's Boehm collector do a share of collection work proportional to its size while it
    /// holds the global allocation lock, and the main thread blocks on its next allocation until
    /// that finishes: 160-190 ms frames in ValheimMonitor on an 80k-ZDO cloud world, and the
    /// worst frame of those eight writes went from 77-95 ms to 37 ms with this patch.
    ///
    /// The buffer is only ever read up to the current chunk's length, which is never more than
    /// the data's length, so sizing it at min(data.Length, chunk size) sends Steam exactly the
    /// same bytes in exactly the same calls.
    ///
    /// Targeted by name: AsyncSave does not reference Splatform.Steam, and it may not be loaded
    /// yet when PatchAll runs, which Type.GetType with an assembly name takes care of.
    [HarmonyPatch]
    internal static class SteamCloudWritePatch
    {
        private const int ChunkBytes = 104857600;

        static MethodBase TargetMethod() => Find();

        [HarmonyPrepare]
        static bool Prepare()
        {
            if (Find() != null) return true;
            Log.Warn("AsyncSave: Splatform.Steam.SteamCloud.WriteFile not found; Steam Cloud writes keep the game's 100 MB buffer.");
            return false;
        }

        private static MethodBase Find()
        {
            var type = Type.GetType("Splatform.Steam.SteamCloud, Splatform.Steam", throwOnError: false);
            return type == null
                ? null
                : AccessTools.DeclaredMethod(type, "WriteFile",
                    new[] { typeof(string), typeof(byte[]), typeof(CloudStorageFileGrouping), typeof(bool) });
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            int constant = code.FindIndex(c => c.opcode == OpCodes.Ldc_I4 && c.operand is int v && v == ChunkBytes);
            int alloc = constant < 0 ? -1 : code.FindIndex(constant, c => c.opcode == OpCodes.Newarr && c.operand as Type == typeof(byte));
            int dataArg = Array.FindIndex(original.GetParameters(), p => p.ParameterType == typeof(byte[]));

            if (alloc < 0 || dataArg < 0)
            {
                Log.Warn("AsyncSave: SteamCloud.WriteFile no longer has the expected 100 MB buffer; leaving it as the game wrote it.");
                return code;
            }

            // newarr takes the length off the stack: turn `chunk` into `Math.Min(chunk, data.Length)`.
            code.InsertRange(alloc, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_S, (byte)(dataArg + (original.IsStatic ? 0 : 1))),
                new CodeInstruction(OpCodes.Ldlen),
                new CodeInstruction(OpCodes.Conv_I4),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Math), nameof(Math.Min), new[] { typeof(int), typeof(int) })),
            });
            return code;
        }
    }
}
