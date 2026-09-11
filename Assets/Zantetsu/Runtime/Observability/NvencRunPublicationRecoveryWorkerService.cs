using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The dedicated worker for one Phase 0.11 NVENC recovery: it runs the
    /// already-built <see cref="NvencRunPublicationRecoveryCoordinator"/> on a
    /// background thread, so the Main and Render threads never wait on that
    /// Run's filesystem work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One service drives one recovery. There is no request queue, no second
    /// Run, no thread pool, task, or timer, and no deadline: the thread starts
    /// on <see cref="Start"/>, calls the coordinator, and physically exits when
    /// that recovery is finished or has faulted.
    /// </para>
    /// <para>
    /// A returned terminal value is verified before it is published: it must be
    /// valid and must be the same value the coordinator itself retained, by
    /// each of the three release receipt references. Which disposition was
    /// taken is never re-decided here.
    /// </para>
    /// <para>
    /// An exception is classified by the lease's own state, never by its type
    /// or message. Only a lease that is no longer fully retained, is still
    /// releasable, and has not completed its release is a partial release;
    /// then the exact exception is retained and the worker parks in
    /// <see cref="NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry"/>
    /// until a retry is requested. It never retries on its own and never spins.
    /// Every other exception - an inspection, commit, or cleanup that failed
    /// with the lock still fully held, or a receipt that turned out unusable
    /// after the lease was fully released - retains the exact exception and
    /// stops the worker as Faulted. Neither case fabricates a terminal
    /// success.
    /// </para>
    /// <para>
    /// A requested retry re-enters the same coordinator once, which is what
    /// keeps the classification, the branch, and any cleanup it already ran
    /// from happening twice; one request is one attempt, and a second request
    /// while that attempt is in flight is refused. The service owns only its
    /// thread and its wait primitive: it owns and disposes neither the
    /// coordinator, the ownership lease, the entry, nor any branch
    /// collaborator, touches no process state, and adds no result wrapper,
    /// receipt, proof, token, nonce, or generation.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryWorkerService : IDisposable
    {
        internal const string WorkerThreadName = "Zantetsu.NvencRecoveryWorker";

        private readonly NvencRunPublicationRecoveryCoordinator _recovery;
        private readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);

        private const int LifecycleNotStarted = 0;
        private const int LifecycleStarting = 1;
        private const int LifecycleRunning = 2;
        private const int LifecycleDisposed = 3;

        private int _state = (int)NvencRunPublicationRecoveryWorkerState.NotStarted;
        private int _lifecycleState = LifecycleNotStarted;
        private Thread _workerThread;
        private volatile Exception _failure;
        private NvencRunPublicationRecoveryTerminalResult _terminalResult;

        internal NvencRunPublicationRecoveryWorkerService(
            NvencRunPublicationRecoveryCoordinator recovery)
        {
            // The outcome, the lease, and every correlation between them stay
            // the coordinator's and its entry's; nothing is inspected here.
            _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        }

        internal NvencRunPublicationRecoveryWorkerState State =>
            (NvencRunPublicationRecoveryWorkerState)Volatile.Read(ref _state);

        /// <summary>
        /// Non-waiting physical stop confirmation: true only once the worker
        /// thread has actually exited, or after disposal. A Completed or
        /// Faulted state alone is not a stop.
        /// </summary>
        internal bool IsStopped
        {
            get
            {
                int lifecycle = Volatile.Read(ref _lifecycleState);
                if (lifecycle == LifecycleDisposed)
                {
                    return true;
                }

                if (lifecycle != LifecycleRunning)
                {
                    // Not started, or still starting: there is no started
                    // thread to report as stopped.
                    return false;
                }

                Thread worker = Volatile.Read(ref _workerThread);
                return worker != null && !worker.IsAlive;
            }
        }

        /// <summary>
        /// Starts the one background thread, exactly once. The caller runs no
        /// part of the recovery and never waits for it. A start that failed
        /// leaves this service unstarted, so the same instance can be started
        /// again.
        /// </summary>
        internal void Start()
        {
            // Exactly-once start, mutually exclusive with disposal: only the
            // caller that flips NotStarted to Starting owns startup. Any other
            // caller - already starting, running, or disposed - observes no
            // side effect and runs no part of the recovery.
            if (Interlocked.CompareExchange(
                    ref _lifecycleState, LifecycleStarting, LifecycleNotStarted)
                != LifecycleNotStarted)
            {
                return;
            }

            try
            {
                Volatile.Write(ref _state, (int)NvencRunPublicationRecoveryWorkerState.Running);

                Thread thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = WorkerThreadName,
                };
                thread.Start();

                // Publish the thread and leave Starting only once the physical
                // thread exists, so a starting service is never reported as
                // stopped and a disposal can never release the signal under a
                // worker that is about to use it.
                Volatile.Write(ref _workerThread, thread);
                Volatile.Write(ref _lifecycleState, LifecycleRunning);
            }
            catch
            {
                // Nothing ran, so this service is unstarted again rather than
                // stuck in a state no thread will ever leave.
                Volatile.Write(ref _state, (int)NvencRunPublicationRecoveryWorkerState.NotStarted);
                Interlocked.CompareExchange(
                    ref _lifecycleState, LifecycleNotStarted, LifecycleStarting);
                throw;
            }
        }

        /// <summary>
        /// Non-waiting, at-most-once-per-attempt retry of the same recovery
        /// after a partial release. Accepted only while parked in
        /// <see cref="NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry"/>
        /// and only while the lease still shows that partial release. The
        /// retained failure is cleared before the worker is woken, and the
        /// entry, classification, and cleanup are not re-issued - that is the
        /// coordinator's retention.
        /// </summary>
        internal bool TryRequestReleaseRetry()
        {
            if (Volatile.Read(ref _lifecycleState) == LifecycleDisposed)
            {
                return false;
            }

            if (State != NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry)
            {
                return false;
            }

            if (!IsPartiallyReleased())
            {
                return false;
            }

            // One request is one attempt: a second one finds the state already
            // moved on.
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencRunPublicationRecoveryWorkerState.Running,
                    (int)NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry)
                != (int)NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry)
            {
                return false;
            }

            _failure = null;
            _signal.Set();
            return true;
        }

        /// <summary>
        /// Non-waiting terminal collection, at most once: only after the worker
        /// has physically stopped with a verified terminal result. Returns
        /// false and the default value in every other state, and never guesses
        /// a result.
        /// </summary>
        internal bool TryCollectTerminal(out NvencRunPublicationRecoveryTerminalResult result)
        {
            if (!IsStopped
                || Interlocked.CompareExchange(
                        ref _state,
                        (int)NvencRunPublicationRecoveryWorkerState.Collected,
                        (int)NvencRunPublicationRecoveryWorkerState.Completed)
                    != (int)NvencRunPublicationRecoveryWorkerState.Completed)
            {
                result = default;
                return false;
            }

            result = _terminalResult;
            return true;
        }

        /// <summary>
        /// Non-throwing diagnostic for the exception this worker retained,
        /// while it is parked for a release retry or has faulted. False once a
        /// retry has been accepted and false after a normal completion.
        /// </summary>
        internal bool TryGetFailure(out Exception failure)
        {
            NvencRunPublicationRecoveryWorkerState state = State;
            if (state != NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry
                && state != NvencRunPublicationRecoveryWorkerState.Faulted)
            {
                failure = null;
                return false;
            }

            failure = _failure;
            return failure != null;
        }

        /// <summary>
        /// Releases the owned wait primitive. Allowed only before the thread
        /// was started or after it has physically stopped - a worker parked for
        /// a release retry is alive and is never force-stopped. Idempotent.
        /// </summary>
        public void Dispose()
        {
            // NotStarted to Disposed must be a CAS so disposal is mutually
            // exclusive with Start's claim: exactly one of the two wins, and
            // Start never creates a thread after the signal is released.
            if (Interlocked.CompareExchange(
                    ref _lifecycleState, LifecycleDisposed, LifecycleNotStarted)
                == LifecycleNotStarted)
            {
                _signal.Dispose();
                return;
            }

            int lifecycle = Volatile.Read(ref _lifecycleState);
            if (lifecycle == LifecycleDisposed)
            {
                // Another disposer already finished; idempotent.
                return;
            }

            if (lifecycle == LifecycleStarting)
            {
                throw new InvalidOperationException(
                    "The recovery worker is starting; dispose is allowed only before Start or after the worker thread has physically stopped.");
            }

            Thread worker = Volatile.Read(ref _workerThread);
            if (worker == null || worker.IsAlive)
            {
                throw new InvalidOperationException(
                    "The recovery worker thread has not physically stopped; dispose is allowed only before it starts or after it exits.");
            }

            if (Interlocked.CompareExchange(
                    ref _lifecycleState, LifecycleDisposed, LifecycleRunning)
                == LifecycleRunning)
            {
                _signal.Dispose();
            }
        }

        private void Run()
        {
            while (true)
            {
                NvencRunPublicationRecoveryTerminalResult terminal;
                try
                {
                    terminal = _recovery.Execute();
                }
                catch (Exception ex)
                {
                    if (!IsPartiallyReleased())
                    {
                        // The lock is either still fully held or already fully
                        // released: there is no remaining release to retry, so
                        // this stops here with the exact exception.
                        _failure = ex;
                        Volatile.Write(
                            ref _state, (int)NvencRunPublicationRecoveryWorkerState.Faulted);
                        return;
                    }

                    _failure = ex;

                    // One park is one wait. Reset first, publish the parked
                    // state, then wait unconditionally: a retry accepted
                    // between those two sets the signal this park consumes,
                    // and a set from an earlier request cannot survive this
                    // park's reset to wake the next one.
                    _signal.Reset();
                    Volatile.Write(
                        ref _state,
                        (int)NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry);

                    _signal.Wait();

                    continue;
                }

                if (!terminal.IsValid || !IsTheCoordinatorsOwnTerminal(terminal))
                {
                    _failure = new InvalidOperationException(
                        "The recovery produced a terminal result this worker cannot publish as its own.");
                    Volatile.Write(
                        ref _state, (int)NvencRunPublicationRecoveryWorkerState.Faulted);
                    return;
                }

                _terminalResult = terminal;
                Volatile.Write(ref _state, (int)NvencRunPublicationRecoveryWorkerState.Completed);
                return;
            }
        }

        /// <summary>
        /// The one condition that makes another attempt possible, read from the
        /// lease alone: no longer fully retained, still releasable, and its
        /// release not complete.
        /// </summary>
        private bool IsPartiallyReleased()
        {
            CaptureRunInitializationSessionOwnershipLease lease = _recovery.OwnershipLease;

            return !lease.IsCreated && lease.CanRelease && !lease.IsReleaseComplete;
        }

        /// <summary>
        /// The value the coordinator itself retained, by each of the three
        /// release receipt references. A terminal result of some other attempt
        /// is never published as this Run's.
        /// </summary>
        private bool IsTheCoordinatorsOwnTerminal(
            NvencRunPublicationRecoveryTerminalResult terminal)
        {
            NvencRunPublicationRecoveryTerminalResult retained = _recovery.TerminalResult;

            return ReferenceEquals(
                    terminal.CaptureCompleteRelease, retained.CaptureCompleteRelease)
                && ReferenceEquals(terminal.IncompleteRelease, retained.IncompleteRelease)
                && ReferenceEquals(terminal.StopRelease, retained.StopRelease);
        }
    }
}
