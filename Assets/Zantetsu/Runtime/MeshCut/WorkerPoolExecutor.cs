using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// One of the two always-on thread pools of DESIGN 4.3, as the shared dispatch uses it: a fixed number of
    /// dedicated OS threads at a lowered priority, and one bounded queue in front of them.
    /// <para>
    /// **What a worker does.** It waits when the queue is empty — no spinning, no polling — and when work arrives it
    /// begins it and goes on to the next one without any further push from the main thread. A work's
    /// <see cref="IDispatchWork.Begin"/> therefore runs **off** the main thread and is the numeric body itself: the
    /// Burst direct call route, reading input nothing else writes and writing scratch and output no other work
    /// shares, and touching no Unity scene object. Whatever a first call has to prepare is prepared on the main
    /// thread before any worker exists — see the <c>prepareOnThisThread</c> argument — and a worker never schedules
    /// or completes a Unity job.
    /// </para>
    /// <para>
    /// **Bounded, everywhere.** <see cref="Capacity"/> bounds what this pool holds at once: queued, running, and
    /// finished but not yet taken back by the main thread. It is a bound and not merely the queue's starting size —
    /// the queue is never allowed past it. When the main thread stops collecting, the finished work piles up against
    /// that same bound and the pool then accepts nothing new, which is what keeps a main thread that has stopped
    /// collecting from growing this pool without end.
    /// </para>
    /// <para>
    /// **Failure and ownership.** A worker never lets an exception escape: it catches it, and the main thread
    /// collects that work once with the exception as its failure. Nothing is re-run and nothing is sent to another
    /// destination. Work that finished keeps everything it holds until the main thread collects it.
    /// </para>
    /// <para>
    /// **A pool that could not start is not a pool.** If a worker cannot be started, the workers that did start are
    /// stopped and joined and the construction fails, so no half-started pool is ever handed out. If a worker
    /// **loop** ends the way it never should, the pool is faulted from then on: <see cref="IsFaulted"/> says so,
    /// <see cref="CanAccept"/> turns false and <see cref="Submit"/> refuses, because work queued behind a worker
    /// that is gone would simply sit there. Finishing a startup attempt and running normally are reported
    /// separately (<see cref="StartupFinishedCount"/> against <see cref="IsFaulted"/>), and nothing here restarts a
    /// worker or moves its work to the other pool.
    /// </para>
    /// <para>
    /// **Threads.** <see cref="Submit"/>, <see cref="TryTakeFinished"/>, <see cref="CanAccept"/> and
    /// <see cref="StopAndConfirm"/> are the main thread's. The counters meant for observation are safe to read from
    /// the main thread at any time; they are observations, not guarantees about what a worker is doing at that
    /// instant.
    /// </para>
    /// </summary>
    public sealed class WorkerPoolExecutor : IWorkExecutor, IDisposable
    {
        /// <summary>DESIGN 4.3's provisional G: the Geometry pool's always-on worker count.</summary>
        public const int DefaultGeometryWorkerCount = 8;

        /// <summary>DESIGN 4.3's provisional B: the Background pool's always-on worker count.</summary>
        public const int DefaultBackgroundWorkerCount = 2;

        /// <summary>What one worker did, for observation from the main thread.</summary>
        public sealed class WorkerObservation
        {
            internal WorkerObservation(string name, ThreadPriority requestedPriority)
            {
                this.name = name;
                this.requestedPriority = requestedPriority;
            }

            /// <summary>The thread's name, as this pool gave it.</summary>
            public readonly string name;

            /// <summary>The priority this pool asked for.</summary>
            public readonly ThreadPriority requestedPriority;

            /// <summary>The priority read back on the worker itself right after it was set.</summary>
            public ThreadPriority ObservedPriority => _observedPriority;

            /// <summary>The worker's own managed thread id, which is not the main thread's.</summary>
            public int ManagedThreadId => _managedThreadId;

            /// <summary>How many works this worker has begun and finished, successfully or not.</summary>
            public int Executed => Volatile.Read(ref _executed);

            /// <summary>The last exception a work of this worker threw, if any. The work carries its own copy.</summary>
            public Exception LastWorkFailure => _lastWorkFailure;

            /// <summary>An exception that ended the worker loop itself, which should not happen.</summary>
            public Exception FatalFailure => _fatalFailure;

            /// <summary>Whether this worker finished its startup attempt at all, succeeding or throwing.</summary>
            public bool StartupFinished => _startupFinished != 0;

            /// <summary>
            /// Whether that attempt succeeded. A worker whose startup finished but did not succeed is not running:
            /// the two are deliberately separate.
            /// </summary>
            public bool StartupSucceeded => _startupSucceeded;

            private volatile ThreadPriority _observedPriority;
            private volatile int _managedThreadId;
            private int _executed;
            private int _startupFinished;
            private volatile bool _startupSucceeded;
            private volatile Exception _lastWorkFailure;
            private volatile Exception _fatalFailure;

            /// <summary>Records the end of the startup attempt, once. False when it had already been recorded.</summary>
            internal bool NoteStartupFinishedOnce(bool succeeded)
            {
                if (Interlocked.CompareExchange(ref _startupFinished, 1, 0) != 0)
                {
                    return false;
                }

                _startupSucceeded = succeeded;
                return true;
            }

            internal void NoteStarted(int managedThreadId, ThreadPriority observedPriority)
            {
                _managedThreadId = managedThreadId;
                _observedPriority = observedPriority;
            }

            internal void NoteExecuted(Exception workFailure)
            {
                if (workFailure != null)
                {
                    _lastWorkFailure = workFailure;
                }

                Interlocked.Increment(ref _executed);
            }

            internal void NoteFatal(Exception fatal)
            {
                _fatalFailure = fatal;
            }
        }

        private struct Ended
        {
            public IDispatchWork work;
            public WorkCompletion completion;
        }

        private readonly object _queueGate = new object();
        private readonly object _endedGate = new object();
        private readonly Queue<IDispatchWork> _queue;
        private readonly Queue<Ended> _ended = new Queue<Ended>();
        private readonly Thread[] _threads;
        private readonly WorkerObservation[] _workers;
        private readonly ThreadPriority _priority;
        private readonly ManualResetEventSlim _allWorkersFinishedStartup = new ManualResetEventSlim(false);

        private readonly Action<Thread> _startWorker;
        private readonly Action _onWorkerStarting;

        private int _held;
        private int _running;
        private int _startupFinishedCount;
        private int _startedThreadCount;
        private bool _accepting = true;
        private bool _stopping;
        private bool _workersStopped;
        private bool _synchronisationReleased;
        private volatile bool _faulted;
        private volatile Exception _faultFailure;

        /// <summary>How long <see cref="Dispose"/> gives an unconfirmed stop. Shortened by tests only.</summary>
        internal int disposeStopTimeoutMilliseconds = 2000;

        /// <param name="destination">Which of the two pools this is; the Unity Job destination is not a pool.</param>
        /// <param name="workerCount">How many always-on threads. Fixed: nothing here grows or shrinks it.</param>
        /// <param name="priority">The OS priority each worker asks for on itself, once, as it starts.</param>
        /// <param name="capacity">The bound on queued, running and finished-not-taken work together.</param>
        /// <param name="prepareOnThisThread">
        /// Whatever a first call has to prepare — a Burst direct call's first invocation above all — run **here, on
        /// the calling thread**, which is the main thread, and finished before any worker thread exists. Optional
        /// only because a pool may be given work that needs no preparation.
        /// </param>
        public WorkerPoolExecutor(
            WorkDestination destination,
            int workerCount,
            ThreadPriority priority,
            int capacity,
            Action prepareOnThisThread = null)
            : this(destination, workerCount, priority, capacity, prepareOnThisThread, null, null)
        {
        }

        /// <summary>
        /// The same, with the two seams a test needs to make a start fail: <paramref name="startWorker"/> stands in
        /// for <c>Thread.Start</c>, and <paramref name="onWorkerStarting"/> runs at the very top of a worker, before
        /// it asks for its priority. Null for either is the ordinary path. Nothing in the product passes these.
        /// </summary>
        internal WorkerPoolExecutor(
            WorkDestination destination,
            int workerCount,
            ThreadPriority priority,
            int capacity,
            Action prepareOnThisThread,
            Action<Thread> startWorker,
            Action onWorkerStarting)
        {
            if (destination != WorkDestination.GeometryPool && destination != WorkDestination.BackgroundPool)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(destination), destination, "a pool is the Geometry or the Background destination");
            }

            if (workerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(workerCount), "must be positive");
            }

            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "must be positive");
            }

            Destination = destination;
            Capacity = capacity;
            _priority = priority;
            _queue = new Queue<IDispatchWork>(capacity);
            _startWorker = startWorker ?? (thread => thread.Start());
            _onWorkerStarting = onWorkerStarting;

            // Before any worker exists, so that no worker can be the first caller of anything this prepares.
            prepareOnThisThread?.Invoke();

            _workers = new WorkerObservation[workerCount];
            _threads = new Thread[workerCount];
            try
            {
                for (int i = 0; i < workerCount; i++)
                {
                    string name = destination + "-" + i;
                    _workers[i] = new WorkerObservation(name, priority);
                    int index = i;
                    _threads[i] = new Thread(() => WorkerLoop(index), 1024 * 1024)
                    {
                        Name = name,

                        // Only about what happens to the thread if the process goes down; nothing to do with
                        // priority.
                        IsBackground = true,
                    };
                    _startWorker(_threads[i]);
                    _startedThreadCount = i + 1;
                }
            }
            catch (Exception failure)
            {
                // A pool that could not start all of its workers is no pool. The caller never receives this object
                // and no work was ever submitted to it, so the workers that did start are told to stop and are then
                // **waited for** — every one of them, with no timeout, because there is nothing for them to be busy
                // with and nobody left to hand them back to. Only once they have all ended does the failure leave.
                // Nothing restarts anything, and no other pool takes this one's place.
                Fault(failure);
                StopStartedWorkersAndJoin();
                throw;
            }
        }

        // The construction-failure path only: notify the stop, then confirm every started worker has ended. This is
        // not a frame path and not the normal stop, and it is the one place a join is unbounded.
        private void StopStartedWorkersAndJoin()
        {
            lock (_queueGate)
            {
                _accepting = false;
                _stopping = true;
                Monitor.PulseAll(_queueGate);
            }

            for (int i = 0; i < _startedThreadCount; i++)
            {
                _threads[i].Join();
            }

            _workersStopped = true;
            ReleaseSynchronisationIfStopped();
        }

        /// <summary>The Geometry pool of DESIGN 4.3: BelowNormal, <see cref="DefaultGeometryWorkerCount"/> threads.</summary>
        public static WorkerPoolExecutor GeometryPool(int capacity, Action prepareOnThisThread = null)
        {
            return new WorkerPoolExecutor(
                WorkDestination.GeometryPool,
                DefaultGeometryWorkerCount,
                ThreadPriority.BelowNormal,
                capacity,
                prepareOnThisThread);
        }

        /// <summary>The Background pool of DESIGN 4.3: Lowest, <see cref="DefaultBackgroundWorkerCount"/> threads.</summary>
        public static WorkerPoolExecutor BackgroundPool(int capacity, Action prepareOnThisThread = null)
        {
            return new WorkerPoolExecutor(
                WorkDestination.BackgroundPool,
                DefaultBackgroundWorkerCount,
                ThreadPriority.Lowest,
                capacity,
                prepareOnThisThread);
        }

        public WorkDestination Destination { get; }

        public int Capacity { get; }

        public int Held => Volatile.Read(ref _held);

        public bool CanAccept => _accepting && !_stopping && !_faulted && Held < Capacity;

        /// <summary>How many works are in the queue, waiting for a worker.</summary>
        public int QueuedCount
        {
            get
            {
                lock (_queueGate)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>How many works a worker is inside right now. An observation, not a promise.</summary>
        public int RunningCount => Volatile.Read(ref _running);

        /// <summary>How many works have ended and are waiting for the main thread to take them back.</summary>
        public int EndedNotTakenCount
        {
            get
            {
                lock (_endedGate)
                {
                    return _ended.Count;
                }
            }
        }

        /// <summary>Whether every worker has stopped, which a normal stop confirms.</summary>
        public bool WorkersStopped => _workersStopped;

        /// <summary>
        /// Whether this pool's own synchronisation has been released, which happens only once a stop has been
        /// confirmed. False after a stop that timed out: the resources are still there, and a later confirmed stop
        /// releases them.
        /// </summary>
        public bool SynchronisationReleased => _synchronisationReleased;

        /// <summary>
        /// Whether a worker failed to start, or a worker loop ended in a way it never should. A faulted pool accepts
        /// no new work — work already queued would otherwise sit there with nobody to run it — and nothing here
        /// restarts a worker or sends its work to another pool.
        /// </summary>
        public bool IsFaulted => _faulted;

        /// <summary>The first failure behind <see cref="IsFaulted"/>, or null.</summary>
        public Exception FaultFailure => _faultFailure;

        /// <summary>What each worker did: its priority readback, its thread id and its counts.</summary>
        public IReadOnlyList<WorkerObservation> Workers => _workers;

        /// <summary>
        /// How many workers have finished their **startup attempt**, whether it succeeded or threw. Not the same as
        /// how many are running normally: see <see cref="IsFaulted"/> and
        /// <see cref="WorkerObservation.StartupSucceeded"/>.
        /// </summary>
        public int StartupFinishedCount => Volatile.Read(ref _startupFinishedCount);

        /// <summary>
        /// Waits until every worker has finished its startup attempt — asked for its priority, or thrown trying.
        /// Each worker reports that once. True says the attempts are over, **not** that they succeeded, so a caller
        /// that cares checks <see cref="IsFaulted"/> as well. The ordinary path needs neither, because work can be
        /// queued before a worker is ready.
        /// </summary>
        public bool WaitUntilStartupFinished(int timeoutMilliseconds)
        {
            return _allWorkersFinishedStartup.Wait(timeoutMilliseconds);
        }

        /// <summary>
        /// Takes the work into the queue, or refuses it. Every reason to refuse — closed, stopping, faulted, full —
        /// is read under the **same lock** that puts the work in the queue, and a worker that falls over sets the
        /// fault under that same lock, so the two can only happen in one order or the other: a work is either
        /// accepted by a pool that was still running, or refused and left untouched with its caller. Nothing is
        /// begun here, so nothing the work does can throw out of this.
        /// <para>
        /// **Acceptance is where ownership passes, and a worker may start at once.** The moment this returns true
        /// the work is in the queue, and a waiting worker can pick it up and begin it before this call has even
        /// returned to its caller — certainly before <see cref="BeginAccepted"/> is called or any record of the
        /// submission is written elsewhere. That is deliberate: what an accepted work is promised is that this pool
        /// owns it and will hand it back through <see cref="TryTakeFinished"/> exactly once, not that it will wait
        /// to be told to start.
        /// </para>
        /// </summary>
        public bool TryAccept(IDispatchWork work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            lock (_queueGate)
            {
                if (!_accepting || _stopping || _faulted || _held >= Capacity)
                {
                    return false;
                }

                _queue.Enqueue(work);
                _held++;
                Monitor.Pulse(_queueGate);
                return true;
            }
        }

        /// <summary>
        /// Nothing: a pool's own worker begins the work it accepted, off the main thread, and may well have begun or
        /// even finished it before this is called. Here only to answer the same contract the Unity Job destination
        /// does, where beginning really is the main thread's step.
        /// </summary>
        public void BeginAccepted(IDispatchWork work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }
        }

        public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
        {
            lock (_endedGate)
            {
                if (_ended.Count == 0)
                {
                    work = null;
                    completion = default;
                    return false;
                }

                Ended ended = _ended.Dequeue();
                work = ended.work;
                completion = ended.completion;
            }

            // Only now does this pool hold one less, which is what frees room for new work.
            lock (_queueGate)
            {
                _held--;
            }

            return true;
        }

        public void CloseForNewWork()
        {
            lock (_queueGate)
            {
                _accepting = false;
            }
        }

        /// <summary>
        /// The normal stop. New work is refused, whatever has not begun is turned into one
        /// <see cref="WorkOutcome.Cancelled"/> completion each for the main thread to take exactly once, and then
        /// this waits for the work that is already running to finish with what it holds and for every worker to
        /// stop. Returns whether every worker was confirmed stopped within the timeout; nothing is declared free
        /// before that. Never part of an ordinary frame.
        /// </summary>
        public bool StopAndConfirm(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "must not be negative");
            }

            var unstarted = new List<IDispatchWork>();
            lock (_queueGate)
            {
                _accepting = false;
                _stopping = true;
                while (_queue.Count > 0)
                {
                    unstarted.Add(_queue.Dequeue());
                }

                // Every worker wakes up, sees an empty queue and a stop, and leaves.
                Monitor.PulseAll(_queueGate);
            }

            // Cancelled outside the queue's lock: the main thread still takes each of these exactly once, through
            // the one path every ended work comes back on.
            if (unstarted.Count > 0)
            {
                lock (_endedGate)
                {
                    for (int i = 0; i < unstarted.Count; i++)
                    {
                        _ended.Enqueue(new Ended { work = unstarted[i], completion = WorkCompletion.Cancelled });
                    }
                }
            }

            bool stopped = true;
            var elapsed = Stopwatch.StartNew();

            // Only the threads that really were started: joining one that never started is not a thing to do.
            for (int i = 0; i < _startedThreadCount; i++)
            {
                long remaining = timeoutMilliseconds - elapsed.ElapsedMilliseconds;
                if (remaining < 0)
                {
                    remaining = 0;
                }

                if (!_threads[i].Join((int)remaining))
                {
                    stopped = false;
                }
            }

            _workersStopped = stopped;
            ReleaseSynchronisationIfStopped();
            return stopped;
        }

        /// <summary>
        /// The last resort, not the normal path: a stop the caller never confirmed. It closes the pool and waits
        /// briefly for the workers, and whatever ended work was never collected stays uncollected — which is why a
        /// normal stop goes through <see cref="SharedWorkDispatcher.Shutdown"/> instead.
        /// <para>
        /// If the stop is still not confirmed when this returns, this pool's own synchronisation is **kept**, not
        /// released: a worker that is still on its way could otherwise reach for something that is already gone.
        /// <see cref="WorkersStopped"/> and <see cref="SynchronisationReleased"/> are how the caller sees that the
        /// stop did not finish, and calling <see cref="StopAndConfirm"/> again later confirms it and releases the
        /// rest. Nothing here is fixed by giving the stop more time.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (!_workersStopped)
            {
                StopAndConfirm(disposeStopTimeoutMilliseconds);
            }

            ReleaseSynchronisationIfStopped();
        }

        // Released once, and only once every worker is known to have stopped: until then a worker may still be on
        // its way to reporting that its startup finished.
        private void ReleaseSynchronisationIfStopped()
        {
            if (!_workersStopped || _synchronisationReleased)
            {
                return;
            }

            _synchronisationReleased = true;
            _allWorkersFinishedStartup.Dispose();
        }

        private void WorkerLoop(int index)
        {
            WorkerObservation observation = _workers[index];
            try
            {
                _onWorkerStarting?.Invoke();
                Thread current = Thread.CurrentThread;
                current.Priority = _priority;
                observation.NoteStarted(current.ManagedThreadId, current.Priority);
                NoteStartupFinished(observation, null);

                while (true)
                {
                    IDispatchWork work;
                    lock (_queueGate)
                    {
                        // Empty means wait, not spin. A stop wakes this the same way new work does.
                        while (_queue.Count == 0 && !_stopping)
                        {
                            Monitor.Wait(_queueGate);
                        }

                        if (_queue.Count == 0)
                        {
                            return;
                        }

                        work = _queue.Dequeue();
                    }

                    WorkCompletion completion;
                    Exception failure = null;
                    Interlocked.Increment(ref _running);
                    try
                    {
                        work.Begin();
                        completion = WorkCompletion.Finished;
                    }
                    catch (Exception thrown)
                    {
                        // A worker never lets this escape: it travels to the main thread as the work's failure.
                        failure = thrown;
                        completion = WorkCompletion.Failed(thrown);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _running);
                    }

                    observation.NoteExecuted(failure);

                    // Published only here, after the work's own writes are over: the main thread cannot see this
                    // work as ended any earlier, and the lock is what carries those writes across.
                    lock (_endedGate)
                    {
                        _ended.Enqueue(new Ended { work = work, completion = completion });
                    }
                }
            }
            catch (Exception fatal)
            {
                // A worker loop must not end this way. The pool is faulted from here on, so nothing new is accepted
                // and queued work cannot pile up behind a worker that is gone. Nothing restarts it.
                observation.NoteFatal(fatal);
                NoteStartupFinished(observation, fatal);
            }
        }

        // The pool can run nothing from here on. Set under the queue's lock, which is the lock that decides whether
        // a work is accepted, so accepting and falling over cannot interleave.
        private void Fault(Exception failure)
        {
            lock (_queueGate)
            {
                _faultFailure = _faultFailure ?? failure;
                _faulted = true;
            }
        }

        // Each worker reports the end of its startup attempt exactly once, whether it got through it or threw on the
        // way. Reporting it is not the same as running normally: a failure faults the pool at the same time.
        private void NoteStartupFinished(WorkerObservation observation, Exception failure)
        {
            if (failure != null)
            {
                Fault(failure);
            }

            if (!observation.NoteStartupFinishedOnce(failure == null))
            {
                return;
            }

            if (Interlocked.Increment(ref _startupFinishedCount) >= _threads.Length)
            {
                // Safe without a guard: this pool's synchronisation is released only after every worker has been
                // joined, so no worker can still be here to reach for it.
                _allWorkersFinishedStartup.Set();
            }
        }
    }
}
