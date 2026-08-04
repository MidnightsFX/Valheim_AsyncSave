using System.Diagnostics;
using HarmonyLib;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Patches
{
    /// Measure-only patches on the vanilla player-profile save. Game.UpdateSaving calls
    /// SavePlayerProfile on the frame *before* ZNet.Save, so any cost here lands right where
    /// the world save appears to start - but none of it is AsyncSave's to schedule.
    ///
    /// Minimap.GetMapData serializes m_explored and m_exploredOthers one bool at a time
    /// through ZPackage (2 x textureSize^2 BinaryWriter calls), Deflate-compresses the
    /// result, and PlayerProfile.Save then writes the .fch synchronously. These numbers
    /// decide whether absorbing that work is worth doing at all.
    ///
    /// Nothing here changes behaviour; every patch is a stopwatch around an unmodified call.
    internal static class ProfileTimingPatches
    {
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SaveMapData))]
        internal static class MinimapSaveMapData
        {
            static void Prefix(out Stopwatch __state)
            {
                __state = SASettings.PhaseTimings.Value ? Stopwatch.StartNew() : null;
            }

            static void Postfix(Stopwatch __state)
            {
                if (__state == null) return;
                Log.Info($"AsyncSave timing: Minimap.SaveMapData {__state.ElapsedMilliseconds} ms");
            }
        }

        [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.Save))]
        internal static class PlayerProfileSave
        {
            static void Prefix(out Stopwatch __state)
            {
                __state = SASettings.PhaseTimings.Value ? Stopwatch.StartNew() : null;
            }

            static void Postfix(Stopwatch __state)
            {
                if (__state == null) return;
                Log.Info($"AsyncSave timing: PlayerProfile.Save {__state.ElapsedMilliseconds} ms");
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
        internal static class GameSavePlayerProfile
        {
            /// Priority.First plus __runOriginal, because SavePlayerProfilePatch prefixes the
            /// same method and returns false on the async path. Harmony skips later prefixes
            /// once one does that unless they opt in by taking __runOriginal - which is how
            /// this reported "TOTAL 0 ms" on a frame where the capture alone measured 9 ms.
            /// The parameter is unused; taking it is the opt-in.
            [HarmonyPriority(Priority.First)]
            static void Prefix(out Stopwatch __state, bool __runOriginal)
            {
                __state = SASettings.PhaseTimings.Value ? Stopwatch.StartNew() : null;
            }

            static void Postfix(Stopwatch __state)
            {
                if (__state == null) return;
                Log.Info($"AsyncSave timing: Game.SavePlayerProfile TOTAL {__state.ElapsedMilliseconds} ms " +
                         "(main thread, frame before the world save)");
            }
        }
    }
}
