using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using AsyncSave.Snapshot;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;
using SALogLevel = AsyncSave.Config.LogLevel;

namespace AsyncSave.Patches
{
    /// Warm the snapshot up while the world is still loading, so the first save of a session
    /// costs what every later one does.
    ///
    /// The pools grow into a world and stay grown, which makes every save after the first
    /// allocation-free - but the first one pays for all of it: a dedup table, an entry array and
    /// a byte buffer sized for the world, plus the zone and location arrays, each a fresh
    /// large-object allocation on a frame the player is playing through. The first save is also
    /// the first time any of this code runs, so the JIT compiles the walk - a very large iterator
    /// state machine and the serializer - inside that same first block. The session that prompted
    /// this had a 21 ms worst block on its first save and 2 ms on its second, same code, bigger
    /// chunk set.
    ///
    /// ZNet.LoadWorld is the server's load: ZoneSystem.Load and ZDOMan.LoadChunks have both run by
    /// the time it returns, so the counts are the real ones, and the loading screen is still up.
    /// A pure client never calls it and has no world save to warm. Everything here is best-effort:
    /// the pools grow on demand, so a warm-up that fails or undershoots costs the first save what
    /// it cost before, never more.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.LoadWorld))]
    internal static class WorldLoadPatch
    {
        /// Most ZDOs the pools are presized for. They hold roughly a hundred bytes per ZDO between
        /// them, so this is ~50 MB on a world at or above it - which such a world would have grown
        /// into on its first save anyway. Below it the first save never allocates; above it the
        /// first save grows from here as it did before, each allocation on its own frame.
        private const int MaxPrewarmZdos = 512 * 1024;

        /// The main-thread half of a save. The writer-thread half - SaveChunksPatch, ZoneSavePatch
        /// - compiles on the save thread, where nobody is drawing frames.
        private static readonly Type[] HotTypes =
        {
            typeof(SnapshotCoroutine),
            typeof(ZdoSerializer),
            typeof(VisitedSet),
            typeof(SnapshotBuffer),
            typeof(ChunkPartition),
            typeof(SliceBudget),
            typeof(ZoneSnapshot),
            typeof(SnapshotState),
        };

        static void Postfix(bool __result)
        {
            if (!__result || !SASettings.WorldSaveOn) return;

            try
            {
                Prewarm(ZDOMan.instance, ZoneSystem.instance);
            }
            catch (Exception ex)
            {
                Log.Warn("AsyncSave: could not pre-warm the snapshot; the first save will size itself " +
                         $"as before. {ex.Message}");
            }
        }

        private static void Prewarm(ZDOMan zdoMan, ZoneSystem zs)
        {
            if (zdoMan == null || zs == null) return;
            var sw = Stopwatch.StartNew();

            // Everything just loaded is persistent - non-persistent ZDOs are never written - so
            // this is what the first save would serialize if every chunk were dirty, and an upper
            // bound on what it will. Plus the walk's own headroom, so a request for exactly this
            // many does not land one past the presize (SnapshotCoroutine.EstimateZdoCount).
            int zdos = zdoMan.m_objectsByID.Count;
            int expected = (zdos > MaxPrewarmZdos ? MaxPrewarmZdos : zdos) + 1024;

            // The same three calls the walk makes on its first frames, with the same sizing.
            SnapshotState.Visited.EnsureCapacity(expected);
            SnapshotState.EnsureEntryCapacity(expected);
            long wantBytes = (long)(expected * (double)SnapshotState.BytesPerZdoEma * 1.15);
            SnapshotState.Buffer.EnsureCapacity(wantBytes);

            // Exact: the zone and location counts are known, and these arrays are small.
            ZoneSnapshot.Presize(zs.m_generatedZones.Count, zs.m_locationInstances.Count, zs.m_globalKeys.Count);

            long poolMs = sw.ElapsedMilliseconds;

            int failed;
            int compiled = CompileHotPaths(out failed);

            if (SASettings.Logging.Value >= SALogLevel.Detailed)
            {
                Log.Info($"AsyncSave: pre-warmed the snapshot for {zdos} ZDOs and {zs.m_locationInstances.Count} " +
                         $"locations in {sw.ElapsedMilliseconds} ms (pools {poolMs} ms; compiled {compiled} " +
                         $"methods, {failed} skipped).");
            }
        }

        /// Force the JIT over the save's main-thread code now rather than on the first save.
        ///
        /// PrepareMethod is what Harmony itself uses to get a method compiled before it detours
        /// it, so it is known to work on this runtime. The iterator matters most: the compiler
        /// lowers SnapshotCoroutine.Run into a nested state-machine class whose MoveNext is the
        /// largest method in the mod, and GetNestedTypes is how it is reached. Generic
        /// definitions are skipped - they have no code to compile until instantiated - and any
        /// other refusal is counted, not raised: a method the JIT declines here is simply
        /// compiled later, on first call, as it always was.
        private static int CompileHotPaths(out int failed)
        {
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance;
            int compiled = 0;
            failed = 0;

            foreach (var type in HotTypes)
            {
                CompileType(type, all, ref compiled, ref failed);
                foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    CompileType(nested, all, ref compiled, ref failed);
                }
            }
            return compiled;
        }

        private static void CompileType(Type type, BindingFlags flags, ref int compiled, ref int failed)
        {
            if (type.ContainsGenericParameters) return;

            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    ++compiled;
                }
                catch
                {
                    ++failed;
                }
            }
        }
    }
}
