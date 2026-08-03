using System.Reflection;
using BepInEx;
using HarmonyLib;
using AsyncSave.Profile;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInIncompatibility("org.bepinex.plugins.smoothsave")]
    public class AsyncSavePlugin : BaseUnityPlugin
    {
        public const string ModGuid = "MidnightsFX.AsyncSave";
        public const string ModName = "AsyncSave";
        public const string ModVersion = "0.2.0";

        private Harmony _harmony;

        public void Awake()
        {
            Log.Source = Logger;
            SASettings.Bind(Config);

            // The ZDO serializer writes multi-byte values through raw pointers, so it inherits
            // host byte order, while vanilla's BinaryWriter is little-endian everywhere. No
            // Valheim target is big-endian, but refuse rather than corrupt saves if that changes.
            if (!System.BitConverter.IsLittleEndian)
            {
                SASettings.WorldSaveEnabled.Value = false;
                SASettings.CharacterSaveEnabled.Value = false;
                Log.Error("AsyncSave: big-endian runtime detected. Async save disabled - the " +
                          "snapshot serializer would produce an unloadable world file.");
            }

            // Utils.persistantDataPath is a field initializer on the same static class as
            // Utils.Compress. If a worker thread were ever the first to touch Utils, the type
            // initializer would run there and Application.persistentDataPath would throw.
            // Force the cctor now, on the main thread.
            _ = Utils.persistantDataPath;

            // The character save is hand-written against a specific .fch layout. If the game
            // updated underneath us, stand that half down rather than write a format we do not
            // understand.
            ProfileSave.FormatSupported = ProfileFormat.Verify();

            SaveIo.Start();

            _harmony = new Harmony(ModGuid);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Info($"{ModName} {ModVersion} loaded.");
        }

        /// Main-thread completion pump for the character-save pipeline. BaseUnityPlugin is
        /// already a MonoBehaviour and outlives scene loads, so this needs no patch of its own.
        public void Update()
        {
            ProfileSave.Pump();
        }

        public void OnDestroy()
        {
            ProfileSave.DrainToDisk();
            SaveIo.Stop();
            _harmony?.UnpatchSelf();
        }
    }
}
