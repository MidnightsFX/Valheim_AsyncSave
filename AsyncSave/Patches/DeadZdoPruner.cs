using System.Collections.Generic;
using HarmonyLib;
using AsyncSave.Util;
using SASettings = AsyncSave.Config.Settings;

namespace AsyncSave.Patches
{
    /// Vanilla defect: ZDOMan.m_deadZDOs (ZDOMan.cs:24) gains an entry for every ZDO the server
    /// destroys (HandleDestroyedZDO, :556) and is cleared *only* by ZDOMan.Load (:288). Nothing
    /// prunes it while the server runs.
    ///
    /// It is read at exactly one site - ZDOMan.cs:685, inside RPC_ZDOData: when incoming client
    /// data would *create* a ZDO whose uid is already dead, the server destroys it again. That is
    /// an anti-resurrection guard for packets that were already in flight when the ZDO died, so
    /// an entry only has to survive a client's round-trip, not the session.
    ///
    /// On a large server the difference is enormous. This mod's own snapshot telemetry reports
    /// 62k-175k removes per ~13-minute window on a 5.2 M-ZDO world, which is on the order of
    /// 2-3 M entries over a five-hour session: 100 MB+ of dictionary that is rehashed as it grows
    /// and walked by every gen-2 GC, to answer a question about the last few seconds.
    ///
    /// The value stored is already ZNet.GetTime().Ticks, so everything needed to expire an entry
    /// is present - vanilla just never looks at it.
    ///
    /// Approach: rather than periodically scanning the dictionary (which is the very O(n) walk
    /// this is meant to remove, plus a multi-megabyte key copy per pass), mirror each insert into
    /// a queue. Inserts happen in time order, so the queue is sorted by construction and pruning
    /// is O(expired) with no scan at all: look at the front, drop it if it has aged out, stop at
    /// the first entry that has not.
    ///
    /// Client cost is zero - m_deadZDOs is server-only, and Record mirrors that check.
    internal static class DeadZdoPruner
    {
        private struct Entry
        {
            public ZDOID Uid;
            public long Ticks;
        }

        /// Mirror of m_deadZDOs insertion order. Bounded above by the dictionary it prunes: one
        /// entry per insert, and both drain together.
        private static readonly Queue<Entry> _queue = new Queue<Entry>();

        /// Ceiling on removals per frame. A destroy costs one enqueue and an expiry costs one
        /// dequeue plus one dictionary remove, so even the observed 175k-per-13-minute peak is
        /// ~225/second - three orders of magnitude under this cap at 60 fps. The cap exists to
        /// bound the catch-up burst after a long stall, not the steady state.
        private const int MaxRemovalsPerPump = 2000;

        /// Reduced ceiling while a snapshot walk holds the frame budget. Still an order of
        /// magnitude above the steady-state rate, so the dictionary keeps draining through the
        /// save window rather than growing across the busiest 13 minutes of every 30.
        private const int MaxRemovalsDuringSnapshot = 256;

        /// Guards the one-shot warning when the clock runs backwards.
        private static bool _warnedBackwards;

        public static void Record(ZDOID uid, long ticks)
        {
            _queue.Enqueue(new Entry { Uid = uid, Ticks = ticks });
        }

        public static void Clear()
        {
            _queue.Clear();
            _warnedBackwards = false;
        }

        /// Drop dead-ZDO records that have aged past the TTL. Called once per frame from
        /// AsyncSavePlugin.Update.
        public static void Pump()
        {
            if (_queue.Count == 0) return;
            if (!SASettings.DeadZdoPruning.Value) return;

            ZDOMan zdoMan = ZDOMan.instance;
            ZNet zNet = ZNet.instance;
            if (zdoMan == null || zNet == null) return;

            long now = zNet.GetTime().Ticks;
            long ttlTicks = SASettings.DeadZdoTtlSeconds.Value * System.TimeSpan.TicksPerSecond;

            var dead = zdoMan.m_deadZDOs;
            int budget = Snapshot.SnapshotState.InProgress
                ? MaxRemovalsDuringSnapshot
                : MaxRemovalsPerPump;

            int removed = 0;
            int examined = 0;

            while (_queue.Count > 0 && examined < budget)
            {
                Entry head = _queue.Peek();
                long age = now - head.Ticks;

                // Net time is world time, and the console (and sleeping) can move it. A negative
                // age means it went backwards; leave everything queued and let it catch up rather
                // than expiring records early. Failing this way is exactly vanilla's behaviour -
                // the dictionary simply keeps growing until the clock recovers.
                if (age < 0)
                {
                    if (!_warnedBackwards)
                    {
                        _warnedBackwards = true;
                        Log.Warn("AsyncSave: network time moved backwards, so dead-ZDO pruning is " +
                                 "paused until it catches up. Harmless - this is vanilla behaviour - " +
                                 "but the dead-ZDO table will grow in the meantime.");
                    }
                    return;
                }

                // The queue is in time order, so the first unexpired entry ends the pass.
                if (age <= ttlTicks) break;

                _queue.Dequeue();
                ++examined;

                // A uid destroyed twice is enqueued twice, and HandleDestroyedZDO's indexer
                // assignment keeps only the newer timestamp. Removing on the stale record would
                // drop the guard early, so only the record that still matches the dictionary may
                // remove it; the newer one is further down the queue and will do the job.
                long current;
                if (!dead.TryGetValue(head.Uid, out current) || current != head.Ticks) continue;

                dead.Remove(head.Uid);
                ++removed;
            }

            if (removed > 0 && SASettings.LogTimings)
            {
                Log.Debug($"AsyncSave dead-ZDO prune: removed={removed} remaining={dead.Count} " +
                          $"queued={_queue.Count} ttl={SASettings.DeadZdoTtlSeconds.Value}s");
            }
        }

        /// Mirrors every insert into m_deadZDOs. Runs unconditionally rather than behind the
        /// config toggle: if the mirror were skipped while pruning was off, turning it on later
        /// would prune against a queue that had missed everything before the switch, and the
        /// dictionary would never drain. Only Pump reads the toggle.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.HandleDestroyedZDO))]
        internal static class HandleDestroyedZdoPrunerPatch
        {
            [HarmonyPostfix]
            static void Postfix(ZDOMan __instance, ZDOID uid)
            {
                // Vanilla only records on the server (ZDOMan.cs:553). Read the stored value back
                // rather than recomputing GetTime().Ticks, so the queued timestamp is exactly the
                // one the dictionary holds and the staleness check above is a reliable equality.
                long ticks;
                if (!__instance.m_deadZDOs.TryGetValue(uid, out ticks)) return;

                Record(uid, ticks);
            }
        }

        /// ZDOMan.Load clears m_deadZDOs outright (:288); the mirror has to go with it or the
        /// queue would carry uids from the previous world.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
        internal static class ZdoManLoadPrunerPatch
        {
            [HarmonyPostfix]
            static void Postfix() => Clear();
        }

        /// ShutDown does not clear m_deadZDOs in vanilla, but it tears the world down, so holding
        /// the mirror past it serves no purpose and would survive into the next Load.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ShutDown))]
        internal static class ZdoManShutDownPrunerPatch
        {
            [HarmonyPostfix]
            static void Postfix() => Clear();
        }
    }
}
