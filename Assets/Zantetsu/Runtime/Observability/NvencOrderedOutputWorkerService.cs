using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single dedicated Output Worker for the Phase 0.11 NVENC path. It drives
    /// the threadless <see cref="NvencOrderedOutputProcessor"/> continuously,
    /// waking only on a coalescing notification so it never busy-spins and the
    /// Main/Render threads never wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor starts exactly one background thread that repeatedly
    /// calls <see cref="NvencOrderedOutputProcessor.TryProcessNext"/> while it
    /// reports progress, then parks on a single manual-reset notification
    /// primitive. Notifications are coalescing state-change hints, not counts:
    /// any producer calls <see cref="Notify"/> without waiting after a
    /// Submit-to-Output record is enqueued, a downstream gate is released, a
    /// Frame Completion is collected, or the process state changes. The worker
    /// resets the signal, re-checks for progress, and only then waits, so a
    /// notification that lands just before the wait is never lost.
    /// </para>
    /// <para>
    /// A <c>false</c> return from the processor is never reinterpreted here: an
    /// empty Submit-to-Output Queue, an incomplete collector or sink, an
    /// unapplied release or recovery, and a busy gate all simply park the
    /// worker until the next notification. The worker never peeks, skips,
    /// sorts, or reorders records, and it does not stop when the queue empties
    /// while Running, Draining, or Run Abandoned. The worker stops only from
    /// an external Poison, a fatal processor exception, or a completed normal
    /// teardown request: after the collected terminal has converged and a
    /// teardown request has been accepted, the worker runs the injected
    /// teardown exactly once on its own thread, verifies the receipt, publishes
    /// the normal-stop evidence, and then physically exits.
    /// </para>
    /// <para>
    /// A processor exception is a fatal invariant violation: the exact
    /// exception is retained exactly once, the process is poisoned, and the
    /// worker stops without fabricating a Completion or touching later work. An
    /// external Poison stop is distinguishable from a fatal stop through
    /// <see cref="TryGetFailure"/>. <see cref="Dispose"/> releases only the
    /// owned wait primitive and is allowed only after the worker has physically
    /// stopped; it is idempotent and never force-stops a running worker.
    /// </para>
    /// <para>
    /// This type owns only its own thread and signal; it owns none of its
    /// injected collaborators, never creates a second worker, and uses no
    /// thread pool, timer, sleep, busy loop, queue copy, LINQ, or static/global
    /// hook.
    /// </para>
    /// </remarks>
    internal sealed class NvencOrderedOutputWorkerService : IDisposable
    {
        internal const string WorkerThreadName = "Zantetsu.NvencOutputWorker";

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencOrderedOutputProcessor _processor;
        private readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);
        private readonly NvencRunChunkTerminalRequest _terminalRequest;
        private readonly NvencOrderedSubmitWorkerService _submitWorker;
        private readonly NvencRunChunkContext _runChunkContext;
        private readonly INvencOutputWorkerTeardown _teardown;

        private const int StateRunning = 0;
        private const int StateDisposed = 1;

        private Thread _workerThread;
        private int _lifecycleState = StateRunning;
        private volatile Exception _fatalFailure;
        private volatile bool _teardownRequested;
        private volatile bool _teardownCompleted;
        private Action _settled;

        internal NvencOrderedOutputWorkerService(
            NvencCaptureProcessState processState,
            NvencOrderedOutputProcessor processor,
            NvencRunChunkContext runChunkContext,
            NvencOrderedSubmitWorkerService submitWorker,
            INvencOutputWorkerTeardown teardown)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _submitWorker = submitWorker ?? throw new ArgumentNullException(nameof(submitWorker));
            _teardown = teardown ?? throw new ArgumentNullException(nameof(teardown));

            if (runChunkContext == null)
            {
                throw new ArgumentNullException(nameof(runChunkContext));
            }

            // Exact-reference correlation: the Submit Worker must be bound to
            // the same process state, and the processor's Sink must be the
            // exact Sink the Run chunk context finalizes, so the terminal can
            // never act on a foreign Run or a foreign Sink.
            // Single O(1) correlation predicate: the exact process state, the
            // Submit-to-Output Queue, and the Run chunk Sink must all agree
            // across the Submit Worker, its internal Submit Processor, the
            // Output Processor, and the context. A foreign internal Submit
            // Processor or Output Processor process state is rejected here
            // before any side effect.
            if (!processor.IsCorrelatedWith(_processState, submitWorker, runChunkContext))
            {
                throw new ArgumentException(
                    "The Output Processor must share the exact process state and Submit-to-Output Queue with the Submit Worker, and the exact Sink with the Run chunk context.",
                    nameof(processor));
            }

            // Bind the exact Run chunk context so a request can never point the
            // terminal at a foreign context.
            _terminalRequest = new NvencRunChunkTerminalRequest(runChunkContext);
            _runChunkContext = runChunkContext;

            // Start the single dedicated background thread in the constructor
            // with a fixed name, then publish it so IsStopped only ever observes
            // a started thread.
            Thread thread = new Thread(Run)
            {
                IsBackground = true,
                Name = WorkerThreadName,
            };
            thread.Start();
            Volatile.Write(ref _workerThread, thread);
        }

        /// <summary>
        /// O(1) correlation predicate: true only when this worker is bound to
        /// the exact process state, Submit Worker, and Run chunk context,
        /// without exposing the internal Queue, Sink, or gates.
        /// </summary>
        internal bool IsCorrelatedWith(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitWorkerService submitWorker,
            NvencRunChunkContext runChunkContext)
        {
            return ReferenceEquals(_processState, processState) &&
                ReferenceEquals(_submitWorker, submitWorker) &&
                ReferenceEquals(_runChunkContext, runChunkContext);
        }

        /// <summary>
        /// Coalescing, non-waiting notification that observable state changed.
        /// The caller never blocks; only the worker waits. Multiple calls may
        /// coalesce into a single wake and must not be interpreted as a count.
        /// </summary>
        internal void Notify()
        {
            if (Volatile.Read(ref _lifecycleState) == StateDisposed)
            {
                throw new ObjectDisposedException(nameof(NvencOrderedOutputWorkerService));
            }

            _signal.Set();
        }

        /// <summary>
        /// Non-waiting, exclusive Finalize request entry for the exact Run
        /// chunk context bound at construction. Accepted only while the process
        /// is Draining and the exact Submit Worker's monotonic DrainCompleted
        /// evidence is published, never while Running, Poisoned, before the
        /// Submit Worker completed its drain, already requested, or after the
        /// terminal is Completed or Collected. The acceptance is serialized
        /// with the Poison transition on the shared process-state gate. On
        /// success the worker is notified and the caller thread never touches
        /// the context.
        /// </summary>
        internal bool TryRequestFinalize()
        {
            // Acquire the existing process-state gate without waiting, then
            // check Draining, the exact Submit Worker drain, and the exclusive
            // acceptance as one critical section: a poison either linearizes
            // first (false, no change) or waits behind this acceptance.
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (!_processState.IsDraining || !_submitWorker.DrainCompleted)
                {
                    return false;
                }

                if (!_terminalRequest.TryAcceptFinalize())
                {
                    return false;
                }

                Notify();
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Non-waiting, exclusive Abandon request entry for the exact Run
        /// chunk context bound at construction. Accepted only while the process
        /// is Draining and the exact Submit Worker's monotonic DrainCompleted
        /// evidence is published, never while Running, Poisoned, before the
        /// Submit Worker completed its drain, already requested, or after the
        /// terminal is Completed or Collected. The acceptance is serialized
        /// with the Poison transition on the shared process-state gate. On
        /// success the worker is notified and the caller thread never touches
        /// the context.
        /// </summary>
        internal bool TryRequestAbandon()
        {
            // Acquire the existing process-state gate without waiting, then
            // check Draining, the exact Submit Worker drain, and the exclusive
            // acceptance as one critical section: a poison either linearizes
            // first (false, no change) or waits behind this acceptance.
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (!_processState.IsDraining || !_submitWorker.DrainCompleted)
                {
                    return false;
                }

                if (!_terminalRequest.TryAcceptAbandon())
                {
                    return false;
                }

                Notify();
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Non-waiting terminal collection. Returns false while the terminal
        /// is unfinished, already collected, or poisoned with no published
        /// outcome, and never guesses a result. A Finalized outcome carries the
        /// exact result; an Abandoned outcome carries no result.
        /// </summary>
        internal bool TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome)
        {
            return _terminalRequest.TryCollect(out outcome);
        }

        /// <summary>
        /// Non-waiting, at-most-once normal teardown request for the exact
        /// Run chunk context bound at construction. Accepted only while the
        /// process is Draining, the exact Submit Worker's monotonic
        /// DrainCompleted evidence is published, the Output Processor holds no
        /// pending or current record, and the bound terminal request is
        /// Collected; never while Running, Poisoned, before the terminal was
        /// collected, already requested, or after the worker stopped. The
        /// acceptance is serialized with the Poison transition on the shared
        /// process-state gate. On success the worker is notified.
        /// </summary>
        internal bool TryRequestTeardown()
        {
            // Serialize the acceptance with the Poison transition on the
            // shared short gate: a poison either linearizes first (false, no
            // change) or waits behind this acceptance.
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (!_processState.IsDraining || !_submitWorker.DrainCompleted)
                {
                    return false;
                }

                if (_processor.HasPendingWork)
                {
                    return false;
                }

                if (_terminalRequest.State != NvencRunChunkTerminalRequestState.Collected)
                {
                    return false;
                }

                if (!TryAcceptTeardownRequest())
                {
                    return false;
                }

                Notify();
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Normal-stop evidence: true only while the worker has completed the
        /// injected teardown exactly once, verified the success receipt, and
        /// the process is neither Poisoned nor carrying a fatal failure. An
        /// external Poison or fatal stop — even one that linearized during
        /// the teardown call — is never reported as normal completion, and
        /// the property is false until the teardown has actually completed.
        /// </summary>
        internal bool TeardownCompleted =>
            Volatile.Read(ref _teardownCompleted)
            && Volatile.Read(ref _fatalFailure) == null
            && !_processState.IsPoisoned;

        /// <summary>
        /// Non-waiting physical stop confirmation: true only once the worker
        /// thread has physically exited (or after disposal). False while the
        /// worker is running, so a stop notification is never reported as
        /// stopped mid-delivery.
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
        /// Instance-local, best-effort observation notification raised whenever
        /// the worker has drained all currently-processable work and is about
        /// to park, or when it stops. Production correctness never depends on
        /// subscribers. The handler snapshot is invoked exactly once per raise
        /// with no per-park allocation, so a throwing observer can prevent later
        /// observers from running; the worker swallows the exception.
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
        /// Non-throwing diagnostic for the first fatal processor exception, if
        /// any. Returns false when the worker stopped from an external Poison
        /// without a fatal failure.
        /// </summary>
        internal bool TryGetFailure(out Exception failure)
        {
            failure = Volatile.Read(ref _fatalFailure);
            return failure != null;
        }

        /// <summary>
        /// Releases the owned wait primitive. Allowed only after the worker has
        /// physically stopped, rejected with <see cref="InvalidOperationException"/>
        /// while the worker is running, and never force-stops a running worker.
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
                    "The Output Worker thread has not physically stopped; dispose is allowed only after the worker thread has exited.");
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
                    bool progressed;
                    do
                    {
                        progressed = TryProcessNextSafely();

                        if (TryStop())
                        {
                            return;
                        }
                    }
                    while (progressed);

                    // Only after the processor has used up all currently
                    // processable work may a terminal request be advanced, so
                    // the terminal never overtakes a held current or queued
                    // record.
                    if (!_processor.HasPendingWork && TryProcessTerminalSafely())
                    {
                        continue;
                    }

                    if (TryExecuteTeardownSafely())
                    {
                        continue;
                    }

                    if (TryStop())
                    {
                        return;
                    }

                    // Park on the coalescing signal. Reset first and re-check
                    // once, so a notification that landed between the last failed
                    // TryProcessNext and Reset is observed instead of lost.
                    _signal.Reset();

                    if (TryStop())
                    {
                        return;
                    }

                    if (TryProcessNextSafely())
                    {
                        continue;
                    }

                    if (!_processor.HasPendingWork && TryProcessTerminalSafely())
                    {
                        continue;
                    }

                    if (TryExecuteTeardownSafely())
                    {
                        continue;
                    }

                    if (TryStop())
                    {
                        return;
                    }

                    RaiseSettled();
                    _signal.Wait();
                }
            }
            catch (Exception ex)
            {
                // An unexpected worker-side failure (for example a disposed
                // signal) is still fatal: record it, poison, and stop without
                // letting the exception escape the thread.
                RecordFatalFailure(ex);
                _processState.TryPoison();
            }
            finally
            {
                RaiseSettled();
            }
        }

        private bool TryProcessNextSafely()
        {
            try
            {
                return _processor.TryProcessNext();
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }
        }

        private bool TryProcessTerminalSafely()
        {
            if (_processState.IsPoisoned)
            {
                return false;
            }

            try
            {
                return _terminalRequest.TryAdvance();
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }
        }

        private bool TryExecuteTeardownSafely()
        {
            if (!Volatile.Read(ref _teardownRequested) || Volatile.Read(ref _teardownCompleted))
            {
                return false;
            }

            // A Poison that linearized first wins: the worker must never
            // contact the teardown after an external Poison or a fatal stop.
            if (_processState.IsPoisoned)
            {
                return false;
            }

            // Run the injected teardown exactly once on the worker thread,
            // outside the gate, so a slow teardown never blocks a concurrent
            // Poison transition.
            NvencOutputWorkerTeardownReceipt receipt;
            try
            {
                receipt = _teardown.TearDown();
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }

            // Order the post-teardown Receipt verification and the normal-stop
            // evidence publication with the Poison transition on the shared
            // process-state gate. A Poison that linearized during TearDown()
            // either fails this acquisition (poisoned) or is observed by the
            // re-check below, so no normal evidence is published.
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    return false;
                }

                // Verify immediately: a null, foreign, or invalid receipt is
                // the first exact failure; it poisons and publishes no
                // normal-stop evidence.
                if (receipt == null || !receipt.IsIssuedFor(_teardown))
                {
                    throw new InvalidOperationException(
                        "Output Worker teardown returned a null, foreign, or invalid receipt.");
                }

                // Publish the normal-stop evidence with release semantics only
                // after the receipt is verified and the non-poisoned state is
                // confirmed under the gate.
                Volatile.Write(ref _teardownCompleted, true);
                return true;
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        private bool TryAcceptTeardownRequest()
        {
            // The acceptance is serialized with the Poison transition on the
            // shared process-state gate held by the caller, so a plain
            // check-and-set is atomic here; the volatile write publishes it to
            // the worker thread.
            if (Volatile.Read(ref _teardownRequested))
            {
                return false;
            }

            Volatile.Write(ref _teardownRequested, true);
            return true;
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
                // An observer failure must never become the worker's fatal
                // failure; observation is best-effort.
            }
        }

        private bool TryStop()
        {
            if (Volatile.Read(ref _fatalFailure) != null)
            {
                return true;
            }

            if (_processState.IsPoisoned)
            {
                return true;
            }

            // Normal teardown completion is the only non-poison, non-fatal
            // stop: the worker exits after the normal-stop evidence is
            // published.
            return Volatile.Read(ref _teardownCompleted);
        }
    }
}
