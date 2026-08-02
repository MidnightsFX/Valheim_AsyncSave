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
        public static ConfigEntry<int> SliceBudgetMs;
        public static ConfigEntry<int> MaxZdosPerSlice;
        public static ConfigEntry<LogLevel> Logging;
        public static ConfigEntry<bool> DeltaReconciliation;

        public static void Bind(ConfigFile cfg)
        {
            SliceBudgetMs = cfg.Bind(
                "1 - General", "Slice budget (ms)", 2,
                "Wall-clock time budget per snapshot slice on the main thread. Lower = smoother frames, longer total snapshot time.");

            MaxZdosPerSlice = cfg.Bind(
                "1 - General", "Max ZDOs per slice", 5000,
                "Hard cap on ZDOs serialized per slice, regardless of time budget. Bounds worst-case slice duration when serialization is unusually fast.");

            Logging = cfg.Bind(
                "1 - General", "Logging", LogLevel.Simple,
                "Amount of information written to the log per save.");

            DeltaReconciliation = cfg.Bind(
                "2 - Advanced", "Reconcile mutations", true,
                "Re-serialize any ZDO whose DataRevision changed during snapshot yields. Disable only for benchmarking - saves will otherwise be one revision stale for mutations that happen mid-snapshot.");
        }
    }
}
