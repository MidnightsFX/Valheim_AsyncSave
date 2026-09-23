using System;
using System.Diagnostics;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Takes the last world save of a session off vanilla's path.
    ///
    /// Vanilla's final save happens inside Game.Shutdown, and on a large world it is the longest
    /// freeze of the session: ZDOExtraData.PrepareSave deep-clones every ZDO in the world, then
    /// the main thread Joins the writer. With the option on, that save - whatever triggered the
    /// shutdown - goes through our serializer instead (OwnsShutdownSave; SaveWorldPatch routes
    /// it). There is no later frame to spread it over by then, so it runs to completion inside
    /// the call. That alone is what a dedicated server gets: its stop arrives through Unity's
    /// quit, which cannot be held (see below), and nobody is watching its frames anyway.
    ///
    /// The menu's Logout (Game.ContinueLogout) and Quit (Menu.QuitGame) go one better, because
    /// they can be held before shutdown starts. The ordinary async save runs while the game keeps
    /// pumping frames, and once its writer has finished the held action is replayed. Shutdown's
    /// own save then only has the chunks dirtied since that async save began, so the block the
    /// player sees shrinks to the last few seconds of changes. The file on disk is the world as it
    /// stood when shutdown began either way - the same guarantee vanilla's final save gives.
    ///
    /// Quit is held at the menu, before Application.Quit is ever called, and never through
    /// Application.wantsToQuit. Once Unity's quit has started, cancelling it there does not stop
    /// OnApplicationQuit on this Unity version: Game shut the world down anyway, the quit stayed
    /// cancelled, and the process was left running a dead game on a black screen. So nothing here
    /// may ever cancel a quit Unity has begun.
    internal static class FinalSave
    {
        private enum Held
        {
            None,
            Logout,
            Quit,
        }

        /// Give up holding after this long and let shutdown run. The save is not abandoned:
        /// shutdown's own save aborts a walk still in progress and redoes it in one block, or
        /// Joins a writer still writing. This is for a walk that has stopped advancing, not a
        /// slow disk - the writer's time is spent either way.
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

        /// True while we replay a held action, so our own prefixes let it by.
        private static bool _releasing;

        /// A sync save now is shutdown's save, and ours to take. Game.Shutdown raises
        /// IsShuttingDown before it saves anything, and every way the game stops - logout, quit,
        /// a server told to stop, the window closing - passes through there. Read by
        /// SaveWorldPatch from both its prefix and the transpiled body, so it must not change
        /// between the two within one save; nothing here does.
        public static bool OwnsShutdownSave
        {
            get
            {
                if (!SASettings.AsyncFinalSave.Value) return false;
                var game = Game.instance;
                return game != null && game.IsShuttingDown();
            }
        }

        /// A new world, or the old one going away. Nothing carries over.
        public static void Reset()
        {
            _held = Held.None;
            _clock.Reset();
            SliceBudget.Rush = false;
        }

        /// Game.Shutdown has begun. If we are still holding, the game was shut down around us -
        /// the window closed, most likely - and there is nothing left to release. Shutdown's own
        /// save still takes our path, it just has the whole of it to do in one block.
        public static void OnShutdownStarted()
        {
            if (_held == Held.None) return;
            Log.Warn("AsyncSave: the game shut down while the async final save was still running; " +
                     "the shutdown save will finish it in one pass.");
            Unhold();
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
                          "Letting shutdown continue; its own save will redo it in one pass.");
            }
            else if (SASettings.Logging.Value != SALogLevel.Off)
            {
                Log.Info($"AsyncSave: async final save landed after {_clock.Elapsed.TotalSeconds:0.0} s; " +
                         "continuing shutdown.");
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

        /// Cosmetic and best-effort: nothing depends on this landing, so nothing it throws may
        /// reach the hold.
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

        /// Replay the held action. Unheld first, so whatever the action throws - it is vanilla
        /// shutdown from here - can never leave us holding, or have us replay it twice.
        private static void Release()
        {
            var held = _held;
            Unhold();

            if (held == Held.Quit)
            {
                // Through Menu.QuitGame rather than straight to Application.Quit, so anything else
                // patched onto the menu's quit still sees it.
                var menu = Menu.instance;
                _releasing = true;
                try
                {
                    if (menu != null) menu.QuitGame();
                    else UnityEngine.Application.Quit();
                }
                finally
                {
                    _releasing = false;
                }
                return;
            }

            var game = Game.instance;
            if (game == null || game.IsShuttingDown()) return;

            _releasing = true;
            try
            {
                game.ContinueLogout(_save, _shouldExit, _changeToStartScene);
            }
            finally
            {
                _releasing = false;
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

        /// The menu's Quit, after its disk-space and cloud-storage prompts have had their say,
        /// and before Application.Quit - the last point at which holding is still safe.
        [HarmonyPatch(typeof(Menu), nameof(Menu.QuitGame))]
        internal static class MenuQuitGame
        {
            static bool Prefix()
            {
                if (_releasing) return true;

                try
                {
                    if (_held != Held.None)
                    {
                        // A quit outranks a held logout: replay as a quit instead.
                        if (_held == Held.Logout)
                        {
                            _held = Held.Quit;
                            Log.Info("AsyncSave: quit requested while holding a logout; the game will " +
                                     "quit once the final save lands.");
                        }
                        return false;
                    }

                    if (!CanRun()) return true;

                    // Game.OnApplicationQuit asks this too, and quits without saving the world
                    // when it fails. Holding the quit for a save vanilla would not have made
                    // helps nobody.
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
        }
    }
}
