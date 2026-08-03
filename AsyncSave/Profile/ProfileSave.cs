using System.Threading;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Profile
{
    internal enum ProfileSaveStage
    {
        Idle = 0,
        Compressing = 1,   // worker is gzipping the map payload
        ReadyToBuild = 2,  // main thread must SetMapData then build the .fch package
        Writing = 3,       // worker is hashing and writing to disk
        Done = 4,          // main thread must raise SavingFinished
    }

    /// Drives the character save across the main thread and SaveIo's worker.
    ///
    /// Two hops, because of two hard constraints:
    ///  - the map payload is Unity-owned, so it must be captured on the main thread, but the
    ///    GZip over ~8 MiB is pure CPU and belongs on the worker;
    ///  - the .fch ZPackage build iterates six dictionaries that gameplay mutates every frame
    ///    (kills, pickups, crafts, and GetWorldData which inserts on a read) and mutates
    ///    m_lastSaveLoad / m_knownWorlds / m_knownWorldKeys itself, so it must be on the main
    ///    thread - only the finished byte[] may cross to the worker.
    internal static class ProfileSave
    {
        private static int _stage;

        /// Set only while our orchestrated path is calling PlayerProfile.Save(). The
        /// SavePlayerToDisk prefix checks this and otherwise defers to vanilla, so menu
        /// character creation (FejdStartup.OnNewCharacterDone, which must be complete before it
        /// re-enumerates saves from disk) never touches our code.
        public static bool AsyncRequested;

        /// Set while shutting down: the whole save runs vanilla and synchronous.
        public static bool ForceSync;

        /// Cleared if the IL version guard fails - see ProfileFormat.
        public static bool FormatSupported = true;

        public static PlayerProfile Profile;
        public static byte[] MapData;
        public static bool HadMapCapture;

        public static ProfileSaveStage Stage
        {
            get => (ProfileSaveStage)Volatile.Read(ref _stage);
            set => Volatile.Write(ref _stage, (int)value);
        }

        public static bool Active => Stage != ProfileSaveStage.Idle;

        public static bool Enabled =>
            SASettings.CharacterSaveEnabled.Value && FormatSupported && !ForceSync;

        public static void Reset()
        {
            Stage = ProfileSaveStage.Idle;
            Profile = null;
            MapData = null;
            HadMapCapture = false;
            MapSnapshot.Reset();
        }

        /// Main thread. Called once per frame from AsyncSavePlugin.Update.
        public static void Pump()
        {
            switch (Stage)
            {
                case ProfileSaveStage.ReadyToBuild:
                    BuildAndQueueWrite();
                    break;

                case ProfileSaveStage.Done:
                    Finish();
                    break;
            }
        }

        private static void BuildAndQueueWrite()
        {
            var profile = Profile;
            if (profile == null) { Reset(); return; }

            if (HadMapCapture && MapData != null)
            {
                // Touches ZNet.instance.GetWorldUID() - main thread only.
                profile.SetMapData(MapData);
                MapData = null;
            }

            Stage = ProfileSaveStage.Writing;

            // Re-enters PlayerProfile.SavePlayerToDisk, which our prefix takes over: it builds
            // the ZPackage here on the main thread and queues the hash + file write.
            AsyncRequested = true;
            bool queued;
            try
            {
                queued = profile.Save();
            }
            catch (System.Exception ex)
            {
                Log.Error($"AsyncSave: character save failed during package build: {ex}");
                queued = false;
            }
            finally
            {
                AsyncRequested = false;
            }

            // Save() can bail before enqueueing anything (no filename). Without this the stage
            // would sit at Writing forever and every later character save would be skipped.
            if (!queued) Stage = ProfileSaveStage.Done;
        }

        private static void Finish()
        {
            Reset();
            // Vanilla raises this at the end of SavePlayerToDisk. It must land on the main
            // thread: Menu.SaveFinished can call SceneManager.LoadScene and toggle UI objects.
            PlayerProfile.SavingFinished?.Invoke();
        }

        /// Block until any in-flight character save has fully landed on disk. Used on the
        /// must-complete paths, where the scene is about to be torn down.
        public static void DrainToDisk()
        {
            if (!Active) { SaveIo.Drain(); return; }

            // The pipeline needs a main-thread hop between the two worker stages, so step it
            // manually rather than waiting on the pump that is about to stop running.
            for (int guard = 0; guard < 10000 && Active; ++guard)
            {
                SaveIo.Drain();
                if (Stage == ProfileSaveStage.ReadyToBuild) BuildAndQueueWrite();
                else if (Stage == ProfileSaveStage.Done) { Reset(); break; }
                else if (!SaveIo.Busy && Stage == ProfileSaveStage.Writing) break;
            }
            SaveIo.Drain();
            Reset();
        }
    }
}
