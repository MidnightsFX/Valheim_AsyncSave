using BepInEx.Configuration;

namespace AsyncSave.Config
{
    internal enum LogLevel
    {
        Off,
        Simple,
        Detailed,
    }

    internal static class Settings
    {
        // 1 - General
        public static ConfigEntry<int> SaveIntervalSeconds;
        public static ConfigEntry<LogLevel> Logging;

        // 2 - World Save (server side; a client never runs the world save at all)
        public static ConfigEntry<bool> WorldSaveEnabled;
        public static ConfigEntry<int> SliceBudgetMs;
        public static ConfigEntry<bool> AdaptiveBudget;
        public static ConfigEntry<int> MaxZdosPerSlice;
        public static ConfigEntry<bool> DeltaReconciliation;

        // 3 - Character Save (client side; also covers the server's own Developer.fch)
        public static ConfigEntry<bool> CharacterSaveEnabled;

        // 4 - Debug
        public static ConfigEntry<bool> PhaseTimings;
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

            MaxZdosPerSlice = cfg.Bind(
                "2 - World Save", "Max ZDOs per slice", 50000,
                "Safety valve: hard cap on ZDOs examined per slice, regardless of the time budget. " +
                "Only meant to bound a slice if the clock misbehaves. Setting this low enough to " +
                "trip before the time budget (a ZDO costs a few hundred nanoseconds) just wastes " +
                "budget and stretches the save out.");

            DeltaReconciliation = cfg.Bind(
                "2 - World Save", "Reconcile mutations", true,
                "Re-serialize any ZDO whose DataRevision changed during snapshot yields. Disable only for benchmarking - saves will otherwise be one revision stale for mutations that happen mid-snapshot.");

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
