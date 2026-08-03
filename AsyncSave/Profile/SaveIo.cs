using System;
using System.Collections.Generic;
using System.Threading;
using AsyncSave.Util;

namespace AsyncSave.Profile
{
    /// One background worker for character-save work, plus the lock that keeps it from ever
    /// running beside ZNet.SaveWorldThread.
    ///
    /// Why the lock matters: SaveSystem.s_saveCollections, SaveCollection's list/dictionary and
    /// FileHelpers' mount refcount (m_depotReferenceCounter, incremented without Interlocked)
    /// are all unsynchronized statics. Iron Gate drives every one of them from SaveWorldThread
    /// already - including Steam cloud writes - so background IO is sanctioned, but they only
    /// ever run one save thread at a time (ZNet.SaveWorld joins the previous one before
    /// starting the next). We join that invariant rather than running beside it.
    internal static class SaveIo
    {
        /// Held for the duration of any job that touches SaveSystem / FileHelpers / FileWriter.
        /// SaveWorldThreadPatch takes this too, so the two writers are mutually exclusive.
        public static readonly object Gate = new object();

        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static readonly object _queueLock = new object();
        private static Thread _worker;
        private static bool _running;
        private static int _pending;

        /// Jobs queued but not yet finished. Read from the main thread to decide whether a
        /// drain is needed.
        public static bool Busy => Volatile.Read(ref _pending) > 0;

        public static void Start()
        {
            if (_worker != null) return;
            _running = true;
            _worker = new Thread(WorkerLoop)
            {
                Name = "AsyncSave.SaveIo",
                IsBackground = true,
            };
            _worker.Start();
        }

        public static void Stop()
        {
            if (_worker == null) return;
            Drain();
            lock (_queueLock)
            {
                _running = false;
                Monitor.PulseAll(_queueLock);
            }
            _worker.Join(5000);
            _worker = null;
        }

        /// Queue work. The job runs while holding Gate.
        public static void Enqueue(Action job)
        {
            Interlocked.Increment(ref _pending);
            lock (_queueLock)
            {
                _queue.Enqueue(job);
                Monitor.Pulse(_queueLock);
            }
        }

        /// Block the calling (main) thread until every queued job has finished. Used only on
        /// the must-complete paths - shutdown, quit to menu - where the scene is about to be
        /// torn down underneath us.
        public static void Drain()
        {
            while (Volatile.Read(ref _pending) > 0)
            {
                Thread.Sleep(1);
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                Action job;
                lock (_queueLock)
                {
                    while (_running && _queue.Count == 0) Monitor.Wait(_queueLock);
                    if (!_running && _queue.Count == 0) return;
                    job = _queue.Dequeue();
                }

                try
                {
                    lock (Gate)
                    {
                        job();
                    }
                }
                catch (Exception ex)
                {
                    // Never let a worker exception kill the thread - the next save would
                    // silently never run.
                    Log.Error($"AsyncSave: character save job failed: {ex}");
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
    }
}
