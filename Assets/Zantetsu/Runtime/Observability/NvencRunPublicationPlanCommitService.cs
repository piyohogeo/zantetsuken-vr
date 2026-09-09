using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Monotonic state of the Phase 0.11 Publication Plan Commit Service: a
    /// fixed single-request-slot, one-shot boundary. <see cref="Accepting"/>
    /// advances to <see cref="Queued"/> on a successful
    /// <see cref="NvencRunPublicationPlanCommitService.TrySubmit"/>,
    /// <see cref="Queued"/> advances to <see cref="Executing"/> when the Worker
    /// takes the exact operation, <see cref="Executing"/> advances to
    /// <see cref="Completed"/> on a verified normal return,
    /// <see cref="Completed"/> advances to <see cref="Collected"/> on a
    /// successful <see cref="NvencRunPublicationPlanCommitService.TryCollect"/>.
    /// <see cref="Collected"/> is the normal terminal: re-submission and
    /// re-collection are rejected. A never-submitted Service advances from
    /// <see cref="Accepting"/> to <see cref="StoppedWithoutRequest"/> on a
    /// normal, non-poisoning stop. A coordinator/committer exception, a
    /// null/foreign/corrupt Execution Result, or a preceding external Poison
    /// advances directly to <see cref="Poisoned"/> without publishing a normal
    /// terminal.
    /// </summary>
    internal enum NvencRunPublicationPlanCommitServiceState
    {
        Accepting = 0,
        Queued = 1,
        Executing = 2,
        Completed = 3,
        Collected = 4,
        Poisoned = 5,
        StoppedWithoutRequest = 6,
    }

    /// <summary>
    /// Phase 0.11 Publication Plan Commit Service: a fixed single-request-slot,
    /// one-shot boundary that separates the publication plan commit execution
    /// from the Main/Render threads so a future dedicated Publication Worker
    /// can perform the plan I/O exactly once. It owns exactly one dedicated
    /// Worker thread with a fixed name, exactly one wake primitive, and one
    /// request slot; it holds no queue, list, dictionary, task, thread pool,
    /// timer, periodic poll, busy spin, or second worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Worker runs the injected Execution Coordinator exactly once per
    /// accepted operation, verifies the returned Execution Result, publishes it
    /// for a single non-blocking collection, and then physically stops. The
    /// Committed, FailedBeforeRename, and CommitOutcomeUnknown statuses are all
    /// published as normal Service terminals without reclassification.
    /// </para>
    /// <para>
    /// A coordinator/committer exception, a null/foreign/corrupt Execution
    /// Result, or a Poison that linearized during execution is fatal: the exact
    /// first exception is retained, the process is poisoned, no normal terminal
    /// is published, and the Worker stops without retrying, cleaning up,
    /// guessing file state, or re-running the commit. TrySubmit never waits,
    /// linearizes the Poison check, the Accepting check, the operation validity,
    /// and the exact process-state correlation on the shared process-state gate,
    /// and notifies the Worker only after the slot is claimed; a foreign
    /// process, an invalid operation, or a second submission is rejected with
    /// no side effect and never contacts the coordinator. An early or spurious
    /// notification while Accepting only re-parks the Worker, so a later
    /// submission still converges, and the Queued-to-Executing claim is
    /// linearized with the Poison transition on the same gate so a Poison that
    /// linearized first never contacts the coordinator. This type owns only
    /// its own thread and signal and never touches a Session Lease, the
    /// Run's local registry slot, or any evidence disposition.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationPlanCommitService : IDisposable
    {
        internal const string WorkerThreadName = "Zantetsu.NvencPublicationWorker";

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencRunPublicationPlanCommitExecutionCoordinator _coordinator;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private const int StateRunning = 0;
        private const int StateDisposed = 1;

        private Thread _workerThread;
        private int _lifecycleState = StateRunning;
        private int _state = (int)NvencRunPublicationPlanCommitServiceState.Accepting;
        private NvencRunPublicationPlanCommitOperation _operation;
        private NvencRunPublicationPlanCommitExecutionResult _result;
        private volatile Exception _fatalFailure;
        private Action _settled;

        internal NvencRunPublicationPlanCommitService(
            NvencCaptureProcessState processState,
            NvencRunPublicationPlanCommitExecutionCoordinator coordinator)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

            Thread thread = new Thread(Run)
            {
                IsBackground = true,
                Name = WorkerThreadName,
            };
            thread.Start();
            Volatile.Write(ref _workerThread, thread);
        }

        internal NvencRunPublicationPlanCommitServiceState State =>
            (NvencRunPublicationPlanCommitServiceState)Volatile.Read(ref _state);

        /// <summary>
        /// Minimal O(1) exact-process-state correlation for the Run Coordinator:
        /// true only when this Service is bound to the exact supplied process
        /// state. ReferenceEquals only; it never exposes the Execution
        /// Coordinator or the Committer.
        /// </summary>
        internal bool IsBoundToProcessState(NvencCaptureProcessState processState)
        {
            return processState != null
                && ReferenceEquals(_processState, processState);
        }

        /// <summary>
        /// Non-waiting, exclusive submission of exactly one valid publication
        /// plan commit operation. A null operation throws
        /// <see cref="ArgumentNullException"/>. A foreign process state, an
        /// invalid operation, or any second submission is rejected with no side
        /// effect and without contacting the coordinator. Acceptance is
        /// linearized with the Poison transition on the shared process-state
        /// gate, and the Worker is notified only after the slot is claimed.
        /// </summary>
        internal bool TrySubmit(NvencRunPublicationPlanCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (Volatile.Read(ref _state) != (int)NvencRunPublicationPlanCommitServiceState.Accepting
                    || !operation.IsValid
                    || !operation.IsBoundToProcessState(_processState))
                {
                    return false;
                }

                _operation = operation;
                Volatile.Write(ref _state, (int)NvencRunPublicationPlanCommitServiceState.Queued);
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            Notify();
            return true;
        }

        /// <summary>
        /// Non-waiting, one-time normal stop of a never-submitted Service: the
        /// Accepting state advances to
        /// <see cref="NvencRunPublicationPlanCommitServiceState.StoppedWithoutRequest"/>
        /// without poisoning the process, linearized with submission on the
        /// shared process-state gate. The Worker is then notified and exits, so
        /// an unused Service for an Incomplete Run releases its Worker and wait
        /// handle while the process stays Draining. A Service that already
        /// accepted a submission, is already stopped, or observes a Poison is
        /// not changed and returns false.
        /// </summary>
        internal bool TryStopWithoutRequest()
        {
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            bool stopped;
            try
            {
                if (_processState.IsPoisoned)
                {
                    return false;
                }

                stopped = Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencRunPublicationPlanCommitServiceState.StoppedWithoutRequest,
                    (int)NvencRunPublicationPlanCommitServiceState.Accepting)
                    == (int)NvencRunPublicationPlanCommitServiceState.Accepting;
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            if (stopped)
            {
                Notify();
            }

            return stopped;
        }

        /// <summary>
        /// Non-waiting, at-most-once collection of the exact Execution Result.
        /// Returns the result only while the Service is
        /// <see cref="NvencRunPublicationPlanCommitServiceState.Completed"/>;
        /// a fatal or Poisoned Service never yields a result. Collection is
        /// linearized with the Poison transition on the shared process-state
        /// gate, which also serializes concurrent collectors, so exactly one
        /// caller succeeds. On success the internal operation and result
        /// references are cleared before
        /// <see cref="NvencRunPublicationPlanCommitServiceState.Collected"/> is
        /// published, so the request slot is empty before the terminal becomes
        /// observable and a second collection returns false with a null result.
        /// </summary>
        internal bool TryCollect(out NvencRunPublicationPlanCommitExecutionResult result)
        {
            result = null;

            // Linearize collection with the Poison transition and serialize
            // concurrent collectors on the shared non-waiting gate: a Poison
            // that linearized first yields no normal result.
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned
                    || Volatile.Read(ref _state) != (int)NvencRunPublicationPlanCommitServiceState.Completed)
                {
                    return false;
                }

                // Empty the slot before the terminal becomes observable: the
                // exact result is published by the Worker before Completed, so
                // it is visible here; both references are cleared and only then
                // is Collected released.
                result = _result;
                _operation = null;
                _result = null;
                Volatile.Write(ref _state, (int)NvencRunPublicationPlanCommitServiceState.Collected);
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Non-throwing diagnostic for the first fatal exception, if any.
        /// Returns false when the Worker stopped from an external Poison
        /// without a fatal failure.
        /// </summary>
        internal bool TryGetFailure(out Exception failure)
        {
            failure = Volatile.Read(ref _fatalFailure);
            return failure != null;
        }

        /// <summary>
        /// Non-waiting physical stop evidence: true only once the Worker thread
        /// has physically exited (or after disposal), false while the Worker is
        /// running.
        /// </summary>
        internal bool IsStopped
        {
            get
            {
                if (Volatile.Read(ref _lifecycleState) == StateDisposed)
                {
                    return true;
                }

                Thread worker = Volatile.Read(ref _workerThread);
                return worker != null && !worker.IsAlive;
            }
        }

        /// <summary>
        /// Coalescing, non-waiting notification that observable state changed.
        /// The caller never blocks; only the Worker waits. Production
        /// correctness never depends on the Worker being notified more than
        /// once.
        /// </summary>
        internal void Notify()
        {
            if (Volatile.Read(ref _lifecycleState) == StateDisposed)
            {
                throw new ObjectDisposedException(nameof(NvencRunPublicationPlanCommitService));
            }

            _signal.Set();
        }

        /// <summary>
        /// Instance-local, best-effort completion notification raised whenever
        /// the Worker reaches a terminal state. Production correctness never
        /// depends on subscribers; tests subscribe before submission and use a
        /// ManualResetEventSlim watchdog, never a sleep or short negative wait.
        /// </summary>
        internal event Action Settled
        {
            add
            {
                Action snapshot;
                Action updated;
                do
                {
                    snapshot = Volatile.Read(ref _settled);
                    updated = snapshot + value;
                }
                while (Interlocked.CompareExchange(ref _settled, updated, snapshot) != snapshot);
            }
            remove
            {
                Action snapshot;
                Action updated;
                do
                {
                    snapshot = Volatile.Read(ref _settled);
                    updated = snapshot - value;
                }
                while (Interlocked.CompareExchange(ref _settled, updated, snapshot) != snapshot);
            }
        }

        /// <summary>
        /// Releases the owned wait primitive. Allowed only after the Worker has
        /// physically stopped, rejected with <see cref="InvalidOperationException"/>
        /// while the Worker is running, and never force-stops a running Worker.
        /// Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Volatile.Read(ref _lifecycleState) == StateDisposed)
            {
                return;
            }

            Thread worker = Volatile.Read(ref _workerThread);
            if (worker == null || worker.IsAlive)
            {
                throw new InvalidOperationException(
                    "The Publication Worker thread has not physically stopped; dispose is allowed only after the worker thread has exited.");
            }

            if (Interlocked.CompareExchange(ref _lifecycleState, StateDisposed, StateRunning) == StateRunning)
            {
                _signal.Dispose();
            }
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    // Park until a notification arrives. The auto-reset event
                    // consumes exactly one notification per wait, so a signaled
                    // event never causes a busy spin and an early notification
                    // is simply re-parked.
                    _signal.WaitOne();

                    // Fast-path Poison check: an external Poison never contacts
                    // the coordinator.
                    if (_processState.IsPoisoned)
                    {
                        EnterFailedWithoutResult();
                        return;
                    }

                    int state = Volatile.Read(ref _state);
                    if (state == (int)NvencRunPublicationPlanCommitServiceState.Accepting)
                    {
                        // An early or spurious notification with no queued
                        // request: re-park instead of terminating, so a later
                        // submission still converges.
                        continue;
                    }

                    if (state != (int)NvencRunPublicationPlanCommitServiceState.Queued)
                    {
                        return;
                    }

                    // Claim Queued -> Executing inside the shared process-state
                    // gate, blocking only until the short gate is free, so the
                    // claim is ordered with a concurrent Poison: a Poison that
                    // linearized first fails this entry without contacting the
                    // coordinator, while a successful claim makes this attempt
                    // in-flight.
                    if (!_processState.TryBeginSettlement())
                    {
                        EnterFailedWithoutResult();
                        return;
                    }

                    try
                    {
                        if (_processState.IsPoisoned)
                        {
                            EnterFailedWithoutResult();
                            return;
                        }

                        if (Volatile.Read(ref _state) != (int)NvencRunPublicationPlanCommitServiceState.Queued)
                        {
                            return;
                        }

                        Volatile.Write(ref _state, (int)NvencRunPublicationPlanCommitServiceState.Executing);
                    }
                    finally
                    {
                        _processState.EndSettlement();
                    }

                    // Step 3: run the Execution Coordinator exactly once, outside
                    // any gate, so a slow commit never blocks a concurrent Poison
                    // transition. Once claimed, this is an in-flight attempt; a
                    // Poison that lands during Execute is settled below.
                    NvencRunPublicationPlanCommitExecutionResult result;
                    try
                    {
                        result = _coordinator.Execute(_operation);
                    }
                    catch (Exception ex)
                    {
                        RecordFatalFailure(ex);
                        _processState.TryPoison();
                        return;
                    }

                    // Step 4: settle on the shared process-state gate so the
                    // result verification and Completed publication are
                    // serialized with a concurrent Poison transition. A Poison
                    // that linearized during Execute fails this entry without
                    // publishing a normal terminal.
                    if (!_processState.TryBeginSettlement())
                    {
                        EnterFailedWithoutResult();
                        return;
                    }

                    try
                    {
                        if (_processState.IsPoisoned)
                        {
                            EnterFailedWithoutResult();
                            return;
                        }

                        // Step 5: verify the result is non-null, valid, and bound
                        // to the exact Execution Coordinator and the exact
                        // operation.
                        if (result == null
                            || !result.IsValid
                            || !ReferenceEquals(result.IssuedBy, _coordinator)
                            || !ReferenceEquals(result.Attempt.Operation, _operation))
                        {
                            throw new InvalidOperationException(
                                "The Publication Plan commit Execution Coordinator returned a null, foreign, or corrupt result.");
                        }

                        // Step 6: hold the result first, then publish Completed.
                        _result = result;
                        Volatile.Write(ref _state, (int)NvencRunPublicationPlanCommitServiceState.Completed);
                    }
                    catch (Exception ex)
                    {
                        RecordFatalFailure(ex);
                        _processState.TryPoison();
                        return;
                    }
                    finally
                    {
                        _processState.EndSettlement();
                    }

                    // Step 7: one-shot — the Worker physically stops after
                    // publishing.
                    return;
                }
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
            }
            finally
            {
                RaiseSettled();
            }
        }

        private void EnterFailedWithoutResult()
        {
            Volatile.Write(ref _state, (int)NvencRunPublicationPlanCommitServiceState.Poisoned);
        }

        private void RecordFatalFailure(Exception failure)
        {
            Interlocked.CompareExchange(ref _fatalFailure, failure, null);
        }

        private void RaiseSettled()
        {
            Action handler = Volatile.Read(ref _settled);
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch
            {
                // An observer failure must never become the Worker's fatal
                // failure; observation is best-effort.
            }
        }
    }
}
