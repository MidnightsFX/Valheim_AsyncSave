using BepInEx.Configuration;

namespace AsyncSave.Config
{
    internal enum LogLevel
    {
        Off,
        Simple,
        Detailed,
    }

    /// How mutations that land during snapshot yields are re-serialized.
    internal enum ReconcileMode
    {
        /// No reconciliation. Mutations mid-snapshot are saved one revision stale.
        Off,
        /// Re-serialize only the ZDOs our revision hooks flagged. O(mutations).
        Dirty,
        /// Re-scan every entry against live DataRevision. O(world) - the pre-0.3 behaviour.
        FullScan,
        /// Dirty pass, then a full scan that reports anything the hooks missed. Diagnostic.
        Verify,
    }

    internal static class Settings
    {
        // 1 - General
        public static ConfigEntry<int> SaveIntervalSeconds;
        public static ConfigEntry<LogLevel> Logging;

        // 2 - World Save (server side; a client never runs the world save at all)
        public static ConfigEntry<bool> WorldSaveEnabled;
        public static ConfigEntry<bool> AsyncCommandSave;
        public static ConfigEntry<int> SliceBudgetMs;
        public static ConfigEntry<bool> AdaptiveBudget;
        public static ConfigEntry<int> FrameHeadroomPercent;
        public static ConfigEntry<int> MaxZdosPerSlice;
        public static ConfigEntry<ReconcileMode> DeltaReconciliation;

        // 3 - Character Save (client side; also covers the server's own Developer.fch)
        public static ConfigEntry<bool> CharacterSaveEnabled;

        // 4 - Debug
        public static ConfigEntry<bool> PhaseTimings;
        public static ConfigEntry<int> HitchThresholdMs;
        public static ConfigEntry<bool> VerifyBytes;

        public static void Bind(ConfigFile cfg)
        {
            SaveIntervalSeconds = cfg.Bind(
                "1 - General", "Save interval (seconds)", 1800,
                new ConfigDescription(
                    "How often the world auto-saves, in seconds. Vanilla default is 1800 (30 minutes). " +
                    "Values below 60 disable the pre-save warning message but the save itself still works. " +
                    "Applied immediately - no restart required.",
                    new AcceptableValueRange<int>(10, 86400)));

            Logging = cfg.Bind(
                "1 - General", "Logging", LogLevel.Simple,
                "Amount of information written to the log per save.");

            // The two halves are independent on purpose: this DLL is meant to run unmodified on
            // a dedicated server, on a client, or on a host. Neither half syncs anything over
            // the network, so any mix of installed/not-installed works.
            WorldSaveEnabled = cfg.Bind(
                "2 - World Save", "Enabled", true,
                "Spread the world (ZDO) save across frames instead of stalling on it. " +
                "Only ever runs on the server or a host - a pure client ignores this. " +
                "Forced off automatically if the ZNet.SaveWorld patch fails to apply.");

            AsyncCommandSave = cfg.Bind(
                "2 - World Save", "Async command saves", true,
                "Use the async save path for the in-game menu's Save button (which vanilla only " +
                "takes in single-player, or on a host with no connected players). The /save " +
                "console command already runs asynchronously in vanilla and is unaffected either " +
                "way. Turn this off to have the Save button block until the world is on disk. " +
                "Shutdown, logout and quit always save synchronously regardless of this setting.");

            SliceBudgetMs = cfg.Bind(
                "2 - World Save", "Slice budget (ms)", 2,
                "Upper bound on the main-thread time budget per snapshot slice. Lower = smoother " +
                "frames, longer total snapshot time. With the adaptive budget on this is a ceiling, " +
                "not a target - a slice only ever gets whatever the frame had left over.");

            AdaptiveBudget = cfg.Bind(
                "2 - World Save", "Adaptive budget", true,
                "Scale each slice to the frame time the game is not already using, instead of " +
                "spending the full slice budget unconditionally. On frames that have already " +
                "missed the target framerate the snapshot does almost nothing, so a save costs " +
                "little when the game is struggling - at the price of a longer save window. " +
                "Turn off for a fixed budget and the shortest possible save.");

            FrameHeadroomPercent = cfg.Bind(
                "2 - World Save", "Frame headroom (%)", 25,
                new ConfigDescription(
                    "How much longer a frame is allowed to take while a save runs, as a percentage " +
                    "of what that frame costs without us. 25 means a 4 ms frame may become 5 ms. " +
                    "This is what keeps the cost proportional: budgeting against a flat 60 FPS target " +
                    "authorises a 4x frame-time increase on a machine already running at 230 FPS. " +
                    "Lower = smoother but longer saves. Ignored when the adaptive budget is off.",
                    new AcceptableValueRange<int>(5, 200)));

            MaxZdosPerSlice = cfg.Bind(
                "2 - World Save", "Max ZDOs per slice", 50000,
                "Safety valve: hard cap on ZDOs examined per slice, regardless of the time budget. " +
                "Only meant to bound a slice if the clock misbehaves. Setting this low enough to " +
                "trip before the time budget (a ZDO costs a few hundred nanoseconds) just wastes " +
                "budget and stretches the save out.");

            DeltaReconciliation = cfg.Bind(
                "2 - World Save", "Reconcile mutations", ReconcileMode.Dirty,
                "Re-serialize any ZDO whose DataRevision changed during snapshot yields, so a save is " +
                "not one revision stale for mutations that land mid-snapshot. " +
                "Dirty re-serializes only what the revision hooks flagged and costs nothing on an idle " +
                "world. FullScan re-checks every ZDO against live state - correct regardless of hook " +
                "coverage, but it walks the whole world every save. Verify runs both and logs anything " +
                "Dirty missed; use it to validate the hooks, not in normal play. Off skips the pass.");

            CharacterSaveEnabled = cfg.Bind(
                "3 - Character Save", "Enabled", true,
                "Move the character (.fch) save off the main thread: bulk-copy the minimap instead of " +
                "writing 8 million bools one at a time, then compress, hash and write on a worker. " +
                "Runs wherever there is a local player, and also covers a dedicated server's own " +
                "profile write. Forced off automatically if the save format does not match what this " +
                "build supports. Quitting always uses the untouched vanilla path.");

            PhaseTimings = cfg.Bind(
                "4 - Debug", "Log phase timings", false,
                "Log per-phase millisecond timings for each save, on both the main thread and the " +
                "save worker. Diagnostic only.");

            HitchThresholdMs = cfg.Bind(
                "4 - Debug", "Hitch threshold (ms)", 16,
                new ConfigDescription(
                    "With phase timings on, record every main-thread block that ran longer than this " +
                    "and report the worst of them at the end of the save, with the phase it was in and " +
                    "whether a GC or a pool growth happened inside it. The single worst-block number on " +
                    "its own cannot tell one big stall apart from fifty medium ones.",
                    new AcceptableValueRange<int>(1, 10000)));

            VerifyBytes = cfg.Bind(
                "4 - Debug", "Verify save bytes", false,
                "Serialize the world and character saves both our way and vanilla's, then compare the " +
                "two byte streams and log the first difference. Very expensive and it stutters the " +
                "game - for validating changes to the serializers only.");

            // Push the interval into Game.m_saveInterval on bind and on any live change.
            ApplySaveInterval();
            SaveIntervalSeconds.SettingChanged += (_, __) => ApplySaveInterval();
        }

        private static void ApplySaveInterval()
        {
            Game.m_saveInterval = SaveIntervalSeconds.Value;
        }
    }
}
