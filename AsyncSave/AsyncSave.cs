using System.Reflection;
using BepInEx;
using HarmonyLib;
using AsyncSave.Util;

namespace AsyncSave
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInIncompatibility("org.bepinex.plugins.smoothsave")]
    public class AsyncSavePlugin : BaseUnityPlugin
    {
        public const string ModGuid = "MidnightsFX.AsyncSave";
        public const string ModName = "AsyncSave";
        public const string ModVersion = "0.1.0";

        private Harmony _harmony;

        public void Awake()
        {
            Log.Source = Logger;
            AsyncSave.Config.Settings.Bind(Config);

            _harmony = new Harmony(ModGuid);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Info($"{ModName} {ModVersion} loaded.");
        }

        public void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
