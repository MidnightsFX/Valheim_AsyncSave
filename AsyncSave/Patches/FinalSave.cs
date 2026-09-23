using System;
using System.Diagnostics;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Runs the last world save of a session on the async path, and holds the logout or quit
    /// until it has landed.
    ///
    /// Vanilla's final save happens inside Game.Shutdown, after ZNetScene has been torn down and
    /// with no later frame to run a coroutine on, so ShutdownPatches has to leave it to vanilla -
    /// and on a large world that is the longest freeze of the session: ZDOExtraData.PrepareSave
    /// deep-clones every ZDO in the world, then the main thread Joins the writer. This gets in
    /// before shutdown starts instead. Logout (Game.ContinueLogout) and quit
    /// (Application.wantsToQuit) are held, the ordinary async save runs while the game keeps
    /// pumping frames, and once its writer has finished the held action is let through and
    /// shutdown runs exactly as vanilla wrote it.
    ///
    /// That leaves a window: the async save captured the world as of the end of its walk, and
    /// the game kept running while the writer wrote. Shutdown's own sync save still closes it.
    /// With the top-up armed, SaveWorldPatch sends that one through our serializer run to
    /// completion inside the call, so it only has to cover the chunks dirtied since the async
    /// save began, and skips the whole-world ZDOExtraData clone. The file on disk is then the
    /// world as it stood when shutdown began - the same guarantee vanilla's final save gives.
    ///
    /// Anything that reaches shutdown without passing through here - a quit that never raises
    /// wantsToQuit, the timeout below, the option being off - gets vanilla's final save, as
    /// before.
    internal static class FinalSave
    {
        private enum Held
        {
            None,
            Logout,
            Quit,
        }

        /// Give up holding after this long and let shutdown run. The save is not abandoned:
        /// shutdown's own save then takes the vanilla way, which aborts a walk still in progress
        /// or Joins a writer still writing. This is for a walk that has stopped advancing, not
        /// a slow disk - the writer's time is spent either way.
        private const double TimeoutSeconds = 300.0;

        /// How often the on-screen notice is re-shown. A center message fades after a few
        /// seconds, and a large world can take longer than that.
        private const double NoticeEverySeconds = 3.0;

        private static Held _held;
        private static readonly Stopwatch _clock = new Stopwatch();
        private static double _lastNotice;

        // Game.ContinueLogout's arguments, replayed on release.
        private static bool _save;
        private static bool _shouldExit;
        private static bool _changeToStartScene;

        /// True while we re-issue a held logout, so our own ContinueLogout prefix lets it by.
        private static bool _releasing;

        /// Set once a held quit has been released. Every quit request from then on goes
        /// straight through, including the one we just issued.
        private static bool _quitReleased;

        /// Shutdown's save is the top-up. Set on release, consumed by SaveWorldPatch.
        public static bool TopUpArmed { get; private set; }

        /// Stand the top-up down. `reason` is logged; null means it was consumed.
        public static void DisarmTopUp(string reason)
        {
            if (!TopUpArmed) return;
            TopUpArmed = false;
            if (reason != null)
            {
                Log.Warn($"AsyncSave: final save top-up stood down ({reason}); shutdown's save takes " +
                         "the vanilla path.");
            }
        }

        /// A new world, or the old one going away. Nothing carries over.
        public static void Reset()
        {
            _held = Held.None;
            _clock.Reset();
            _quitReleased = false;
            TopUpArmed = false;
            SliceBudget.Rush = false;
        }

        /// Game.Shutdown has begun. If we are still holding, something shut the game down around
        /// us - a quit that never raised wantsToQuit - and there is nothing left to release.
        /// The top-up stays unarmed, so that shutdown's save goes the vanilla way.
        public static void OnShutdownStarted()
        {
            if (_held == Held.None) return;
            Log.Warn("AsyncSave: the game shut down while the async final save was still running; " +
                     "vanilla's final save takes over.");
            Unhold();
        }

        /// Application.wantsToQuit handler. Returning false cancels the quit. Never throws: an
        /// exception here must not leave a game that cannot be quit.
        public static bool OnWantsToQuit()
        {
            try
            {
                if (_quitReleased) return true;

                if (_held != Held.None)
                {
                    // A stuck hold must not become a game that cannot be closed.
                    if (_clock.Elapsed.TotalSeconds > TimeoutSeconds) return true;

                    // Repeats are expected, not impatience - ServerCtrl re-issues Application.Quit
                    // every two seconds for as long as server_exit.drp exists - so none of them
                    // may cut the save short. A quit outranks a held logout, though: release as
                    // one.
                    if (_held == Held.Logout)
                    {
                        _held = Held.Quit;
                        Log.Info("AsyncSave: quit requested while holding a logout; the game will quit " +
                                 "once the final save lands.");
                    }
                    return false;
                }

                if (!CanRun()) return true;

                // Game.OnApplicationQuit asks this too, and quits without saving the world when it
                // fails. Holding the quit for a save vanilla would not have made helps nobody.
                if (!ZNet.instance.EnoughDiskSpaceAvailable(out bool _)) return true;

                Hold(Held.Quit);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"AsyncSave: async final save could not hold the quit, letting it through. {ex}");
                Unhold();
                return true;
            }
        }

        /// Main thread, once per frame, from AsyncSavePlugin.Update.
        public static void Pump()
        {
            if (_held == Held.None) return;

            bool timedOut = _clock.Elapsed.TotalSeconds > TimeoutSeconds;
            if (!timedOut && SaveInFlight())
            {
                ShowNotice();
                return;
            }

            if (timedOut)
            {
                Log.Error($"AsyncSave: the async final save did not finish within {TimeoutSeconds:0} s. " +
                          "Letting shutdown continue; its own save takes the vanilla path.");
            }
            else
            {
                TopUpArmed = true;
                if (SASettings.Logging.Value != SALogLevel.Off)
                {
                    Log.Info($"AsyncSave: async final save landed after {_clock.Elapsed.TotalSeconds:0.0} s; " +
                             "continuing shutdown.");
                }
            }

            Release();
        }

        /// Whether a final save is possible at all. Mirrors ZNet.Save's own reasons to skip, so
        /// we never hold a shutdown for a save that is not going to happen.
        private static bool CanRun()
        {
            if (!SASettings.AsyncFinalSave.Value || !SASettings.WorldSaveOn) return false;

            var znet = ZNet.instance;
            var game = Game.instance;
            if (znet == null || game == null) return false;
            if (game.IsShuttingDown() || znet.HaveStopped) return false;
            if (!znet.IsServer() || ZNet.World == null) return false;

            if (ZNet.m_loadError) return false;
            if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)) return false;
            if (ZoneSystem.instance == null || ZoneSystem.instance.SkipSaving()) return false;
            if (DungeonDB.instance == null || DungeonDB.instance.SkipSaving()) return false;
            return true;
        }

        /// The walk is still running, or a writer is still streaming a finished one. The writer
        /// thread is started in the same call as the walk and parks until the walk hands over, so
        /// it alone covers both phases; InProgress is belt and braces.
        private static bool SaveInFlight()
        {
            if (SnapshotState.InProgress) return true;

            var znet = ZNet.instance;
            if (znet == null) return false;
            var writer = znet.m_saveThread;
            return writer != null && writer.IsAlive;
        }

        private static void Hold(Held kind)
        {
            _held = kind;
            _clock.Restart();
            _lastNotice = double.NegativeInfinity;
            SliceBudget.Rush = true;

            // A save already in flight - an autosave mid-walk, or a writer still streaming a
            // finished one - serves as the bulk just as well. Starting another would only have
            // SaveWorldPatch drop it, and log it as a skipped autosave.
            bool joined = SaveInFlight();
            if (!joined) ZNet.instance.Save(sync: false);

            if (SASettings.Logging.Value != SALogLevel.Off)
            {
                Log.Info($"AsyncSave: holding {(kind == Held.Quit ? "quit" : "logout")} for the async final save" +
                         (joined ? ", joining the save already in progress." : "."));
            }

            // Close the confirmation it came from: left open, its Yes looks like it did nothing.
            var menu = Menu.instance;
            if (menu != null)
            {
                menu.m_logoutDialog.gameObject.SetActive(false);
                menu.m_quitDialog.gameObject.SetActive(false);
            }
            ShowNotice();
        }

        private static void Unhold()
        {
            _held = Held.None;
            SliceBudget.Rush = false;
        }

        /// Cosmetic and best-effort: a dedicated server has no Hud, and nothing depends on this
        /// landing - so nothing it throws may reach the hold.
        private static void ShowNotice()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastNotice < NoticeEverySeconds) return;
            _lastNotice = now;

            try
            {
                var hud = MessageHud.instance;
                if (hud != null) hud.ShowMessage(MessageHud.MessageType.Center, "Saving world...", log: false);
            }
            catch (Exception ex)
            {
                Log.Debug($"AsyncSave: could not show the saving notice. {ex.Message}");
            }
        }

        /// Let the held action through. Unheld first, so whatever the action throws - it is
        /// vanilla shutdown from here - can never leave us holding, or have us issue it twice.
        private static void Release()
        {
            var held = _held;
            Unhold();

            if (held == Held.Quit)
            {
                _quitReleased = true;
                UnityEngine.Application.Quit();
                return;
            }

            var game = Game.instance;
            if (game == null || game.IsShuttingDown())
            {
                DisarmTopUp(null);
                return;
            }

            _releasing = true;
            try
            {
                game.ContinueLogout(_save, _shouldExit, _changeToStartScene);
            }
            finally
            {
                _releasing = false;
                // Logout is synchronous: by now shutdown's save has either used the top-up or
                // was never going to run.
                DisarmTopUp(null);
            }
        }

        /// Logout's decision point: after the disk-space prompt has had its say, before
        /// Game.Shutdown. Game.Logout itself is too early - it can hand off to that prompt and
        /// come back here later, from the popup's callback.
        [HarmonyPatch(typeof(Game), nameof(Game.ContinueLogout))]
        internal static class GameContinueLogout
        {
            static bool Prefix(bool save, bool shouldExit, bool changeToStartScene)
            {
                if (_releasing) return true;

                try
                {
                    // Already holding: a repeat, or a logout behind a held quit that outranks it.
                    if (_held != Held.None) return false;

                    if (!save || !CanRun()) return true;

                    _save = save;
                    _shouldExit = shouldExit;
                    _changeToStartScene = changeToStartScene;
                    Hold(Held.Logout);
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error($"AsyncSave: async final save could not hold the logout, letting it through. {ex}");
                    Unhold();
                    return true;
                }
            }
        }
    }
}
