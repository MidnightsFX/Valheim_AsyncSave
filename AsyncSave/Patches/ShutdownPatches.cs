using HarmonyLib;
using AsyncSave.Profile;

namespace AsyncSave.Patches
{
    /// The must-complete paths.
    ///
    /// Game.Shutdown calls SavePlayerProfile(true) and then immediately tears down ZNetScene,
    /// ZNet and the scene itself; OnApplicationQuit only sleeps 2000 ms. Nothing here may be
    /// deferred to a later frame, because there will not be one - so we land any in-flight save
    /// and then let vanilla run the whole thing synchronously.
    internal static class ShutdownPatches
    {
        /// Shutdown latches ForceSync, but quitting to the main menu and then starting another
        /// world happens in the same process - without this the async path would stay disabled
        /// for the rest of the session.
        [HarmonyPatch(typeof(Game), "Awake")]
        internal static class GameAwake
        {
            static void Postfix()
            {
                ProfileSave.ForceSync = false;
                ProfileSave.Reset();
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
        internal static class GameShutdown
        {
            static void Prefix()
            {
                ProfileSave.DrainToDisk();
                ProfileSave.ForceSync = true;
            }
        }

        [HarmonyPatch(typeof(Game), "OnApplicationQuit")]
        internal static class GameOnApplicationQuit
        {
            static void Prefix()
            {
                ProfileSave.DrainToDisk();
                ProfileSave.ForceSync = true;
            }
        }
    }
}
