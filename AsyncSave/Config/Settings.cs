using BepInEx.Configuration;

namespace AsyncSave.Config
{
    internal enum LogLevel
    {
        Off,
        Simple,
        Detailed,
        /// Detailed plus per-phase millisecond timings on both the main thread and the save
        /// worker. Diagnostic; noisy.
        Debug,
    }

    /// How mutations that land during snapshot yields are re-serialized.
    internal enum ReconcileMode
    {
        /// Re-serialize only the ZDOs our revision hooks flagged. O(mutations).
        Dirty,
        /// Re-scan every entry against live DataRevision. O(world), and correct regardless of
        /// hook coverage.
        FullScan,
    }

    internal static class Settings
    {
        // 1 - General
        public static ConfigEntry<int> SaveIntervalSeconds;
        public static ConfigEntry<LogLevel> Logging;
        public static ConfigEntry<bool> DeadZdoPruning;
        public static ConfigEntry<int> DeadZdoTtlSeconds;

        // 2 - World Save (server side; a client never runs the world save at all)
        public static ConfigEntry<bool> WorldSaveEnabled;
        public static ConfigEntry<bool> AsyncManualSave;
        public static ConfigEntry<bool> AsyncFinalSave;
        public static ConfigEntry<int> SliceBudgetMs;
        public static ConfigEntry<bool> AdaptiveBudget;
        public static ConfigEntry<int> FrameHeadroomPercent;
        public static ConfigEntry<ReconcileMode> DeltaReconciliation;

        // 3 - Character Save (client side; also covers the server's own Developer.fch)
        public static ConfigEntry<bool> CharacterSaveEnabled;

#if DEBUG
        // 4 - Debug. Debug builds only: the verify harnesses serialize everything twice and are
        // for validating changes to the serializers, not for playing with.
        public static ConfigEntry<bool> VerifyBytes;
#endif

        /// Vanilla's Game.m_saveInterval initialiser. Used to tell "the player chose 1800" apart
        /// from "the player left it alone", so we only take the setting over when they meant it.
        private const int VanillaSaveInterval = 1800;

        // Runtime stand-downs, latched at startup when something makes a half of the mod unsafe
        // to run: a big-endian host, a failed transpile, an unrecognised .fch layout, a missing
        // revision hook.
        //
        // Deliberately not written back to the ConfigEntry. BepInEx persists Value assignments to
        // the .cfg, so doing that would let a one-off - a game update that renames a method, say -
        // silently and permanently rewrite a setting the player chose. Fixing the mod afterwards
        // would not put it back, and nothing ever told them it had changed. These flags live for
        // the process and the file on disk keeps saying what the player asked for.
        private static bool _worldSaveBlocked;
        private static bool _characterSaveBlocked;
        private static bool _reconcileForcedFullScan;

        public static void BlockWorldSave() => _worldSaveBlocked = true;
        public static void BlockCharacterSave() => _characterSaveBlocked = true;
        public static void ForceFullScanReconcile() => _reconcileForcedFullScan = true;

        /// Whether to slice the world save. Read this, never WorldSaveEnabled.Value.
        public static bool WorldSaveOn => WorldSaveEnabled.Value && !_worldSaveBlocked;

        /// Whether to run the async character save. Read this, never CharacterSaveEnabled.Value.
        public static bool CharacterSaveOn => CharacterSaveEnabled.Value && !_characterSaveBlocked;

        /// Effective reconciliation mode. Falls back to FullScan when the revision hooks are not
        /// attached, since that mode depends on nothing but DataRevision itself.
        public static ReconcileMode Reconcile =>
            _reconcileForcedFullScan ? ReconcileMode.FullScan : DeltaReconciliation.Value;

        /// True at Debug and above. Gates the per-phase timing lines.
        public static bool LogTimings => Logging.Value >= LogLevel.Debug;

        public static void Bind(ConfigFile cfg)
        {
            SaveIntervalSeconds = cfg.Bind(
                "1 - General", "Save interval (seconds)", VanillaSaveInterval,
                new ConfigDescription(
                    "How often the world auto-saves, in seconds. Vanilla default is 1800 (30 minutes). " +
                    "Values below 60 disable the pre-save warning message but the save itself still works. " +
                    "Applied immediately - no restart required. Left at 1800 this does not touch the " +
                    "interval at all, so another mod setting it still wins.",
                    new AcceptableValueRange<int>(10, 86400)));

            Logging = cfg.Bind(
                "1 - General", "Logging", LogLevel.Simple,
                "Amount of information written to the log per save. Debug adds per-phase timings.");

            DeadZdoPruning = cfg.Bind(
                "1 - General", "Prune dead ZDO records", true,
                "Expire the server's table of recently-destroyed ZDOs instead of letting it grow for " +
                "the whole session. Vanilla adds an entry for every destroyed ZDO and only ever clears " +
                "the table when a world loads, but reads it for one purpose: rejecting a create for a " +
                "ZDO that died while a client's packet was in flight. On a busy large world that is " +
                "millions of entries retained to answer a question about the last few seconds. " +
                "Server-side only; a client never fills this table.");

            DeadZdoTtlSeconds = cfg.Bind(
                "1 - General", "Dead ZDO record lifetime (seconds)", 60,
                new ConfigDescription(
                    "How long a destroyed ZDO stays in that table before being pruned. It only has to " +
                    "outlast a client's round-trip, so the default is already far more than needed. " +
                    "Raise it if you see destroyed objects reappearing on very high-latency clients.",
                    new AcceptableValueRange<int>(30, 3600)));

            // The two halves are independent on purpose: this DLL is meant to run unmodified on
            // a dedicated server, on a client, or on a host. Neither half syncs anything over
            // the network, so any mix of installed/not-installed works.
            WorldSaveEnabled = cfg.Bind(
                "2 - World Save", "Enabled", true,
                "Spread the world (ZDO) save across frames instead of stalling on it. " +
                "Only ever runs on the server or a host - a pure client ignores this. " +
                "Turning this off restores the vanilla save path completely. The mod also stands " +
                "this down by itself, for the session only, if it cannot patch ZNet.SaveWorld or " +
                "the host is big-endian - this setting is never rewritten on disk.");

            AsyncManualSave = cfg.Bind(
                "2 - World Save", "Async manual saves", true,
                "Use the async save path for the in-game menu's Save button (which vanilla only " +
                "takes in single-player, or on a host with no connected players). Turn this off to " +
                "have the Save button block until the world is on disk. The /save console command " +
                "is already asynchronous in vanilla and is unaffected either way. Shutdown, logout " +
                "and quit are not affected by this setting - see 'Async final save'.");

            AsyncFinalSave = cfg.Bind(
                "2 - World Save", "Async final save", false,
                "Take the world save made at shutdown - logout, quit, or stopping a server - through " +
                "this mod's serializer instead of vanilla's, which first deep-clones every object in " +
                "the world. From the menu's Logout and Quit, the logout or quit is held while an " +
                "async save runs flat out with the game still drawing frames, and goes ahead once it " +
                "has landed; a short final pass then catches whatever changed meanwhile. Any other " +
                "shutdown - a dedicated server being stopped, the window being closed - cannot be " +
                "held, so the save runs start to finish inside the shutdown, where vanilla's would " +
                "have. Off leaves the final save exactly as vanilla does it.");

            SliceBudgetMs = cfg.Bind(
                "2 - World Save", "Slice budget (ms)", 2,
                new ConfigDescription(
                    "Upper bound on the main-thread time budget per snapshot slice. Lower = smoother " +
                    "frames, longer total snapshot time. With the adaptive budget on this is a ceiling, " +
                    "not a target - a slice only ever gets whatever the frame had left over.",
                    new AcceptableValueRange<int>(1, 33)));

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

            DeltaReconciliation = cfg.Bind(
                "2 - World Save", "Reconcile mutations", ReconcileMode.Dirty,
                "Re-serialize any ZDO whose DataRevision changed during snapshot yields, so a save is " +
                "not one revision stale for mutations that land mid-snapshot. " +
                "Dirty re-serializes only what the revision hooks flagged and costs nothing on an idle " +
                "world. FullScan re-checks every ZDO against live state - correct regardless of hook " +
                "coverage, but it walks the whole world every save. The mod switches to FullScan by " +
                "itself, for the session only, if the revision hooks are not attached.");

            CharacterSaveEnabled = cfg.Bind(
                "3 - Character Save", "Enabled", true,
                "Move the character (.fch) save off the main thread: bulk-copy the minimap instead of " +
                "writing 8 million bools one at a time, then compress, hash and write on a worker. " +
                "Runs wherever there is a local player, and also covers a dedicated server's own " +
                "profile write. Quitting and logging out still save synchronously and in full before " +
                "the scene goes away; only the map step takes the faster path there. The mod stands " +
                "this down by itself, for the session only, if the .fch layout is not the one this " +
                "build was written against - this setting is never rewritten on disk.");

#if DEBUG
            VerifyBytes = cfg.Bind(
                "4 - Debug", "Verify save bytes", false,
                "Serialize the world and character saves both our way and vanilla's, then compare the " +
                "two byte streams and log the first difference. Very expensive and it stutters the " +
                "game - for validating changes to the serializers only.");
#endif

            // Push the interval into Game.m_saveInterval on bind and on any live change.
            ApplySaveInterval();
            SaveIntervalSeconds.SettingChanged += (_, __) => ApplySaveInterval();
        }

        /// Only when the player actually changed it. Writing the vanilla value back on every
        /// startup would silently win a race with any other mod that sets the interval, for a
        /// setting the player never touched.
        private static void ApplySaveInterval()
        {
            if (SaveIntervalSeconds.Value == VanillaSaveInterval) return;
            Game.m_saveInterval = SaveIntervalSeconds.Value;
        }
    }
}
