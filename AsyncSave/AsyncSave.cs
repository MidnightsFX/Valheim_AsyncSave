using System.Reflection;
using BepInEx;
using HarmonyLib;
using AsyncSave.Patches;
using AsyncSave.Profile;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SAReconcile = AsyncSave.Config.ReconcileMode;

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

            VerifyRevisionHooks();

            Log.Info($"{ModName} {ModVersion} loaded.");
        }

        /// Dirty reconciliation is only correct while both ZDO revision hooks are live, and a
        /// missing hook is silent: PendingDirty simply stays empty, the reconcile pass finds
        /// nothing to do, and every mutation that lands mid-snapshot is saved one revision
        /// stale. There is no exception, no warning, and the phase timings look *better*.
        ///
        /// That is not hypothetical - ZdoRevisionPatches.cs sat in the tree but not in the
        /// csproj's Compile list, so it compiled away to nothing and Dirty mode silently meant
        /// Off. PatchAll cannot catch it: with the file absent there is no patch class to fail
        /// on. This is the check that would have caught it, so it asks the question from the
        /// other end - is the game method actually patched by us? - and falls back to the scan
        /// that depends on nothing but DataRevision itself.
        private void VerifyRevisionHooks()
        {
            string missing = null;
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDO), ZdoRevisionPatches.IncreaseDataRevisionName),
                           "ZDO." + ZdoRevisionPatches.IncreaseDataRevisionName, ref missing);
            NoteIfUnhooked(AccessTools.DeclaredMethod(typeof(ZDO), nameof(ZDO.Deserialize)),
                           "ZDO.Deserialize", ref missing);

            if (missing == null) return;

            var mode = SASettings.DeltaReconciliation.Value;
            if (mode == SAReconcile.Dirty || mode == SAReconcile.Verify)
            {
                SASettings.DeltaReconciliation.Value = SAReconcile.FullScan;
            }

            Log.Error($"AsyncSave: the ZDO revision hook on {missing} is not attached. Dirty " +
                      $"reconciliation cannot see mid-snapshot mutations without it and would " +
                      $"save them one revision stale, so 'Reconcile mutations' has been forced " +
                      $"to FullScan (correct, but it walks the whole world every save). This " +
                      $"means either the game renamed the method or the mod was built without " +
                      $"Patches/ZdoRevisionPatches.cs.");
        }

        /// Appends `label` to `missing` when the method is gone or carries no postfix of ours.
        /// Checks for *our* postfix specifically rather than for any patch at all: another mod
        /// patching the same method proves nothing about whether we did.
        private static void NoteIfUnhooked(MethodBase target, string label, ref string missing)
        {
            bool ours = false;
            var info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info != null)
            {
                foreach (var postfix in info.Postfixes)
                {
                    if (postfix.owner != ModGuid) continue;
                    ours = true;
                    break;
                }
            }

            if (ours) return;
            missing = missing == null ? label : missing + " and " + label;
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
