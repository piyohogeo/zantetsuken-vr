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
    /// while Running, Draining, or Run Abandoned: the Output Worker has no
    /// normal drain stop in this unit, and only an external Poison or a fatal
    /// processor exception stops it.
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

        private const int StateRunning = 0;
        private const int StateDisposed = 1;

        private Thread _workerThread;
        private int _lifecycleState = StateRunning;
        private volatile Exception _fatalFailure;
        private Action _settled;

        internal NvencOrderedOutputWorkerService(
            NvencCaptureProcessState processState,
            NvencOrderedOutputProcessor processor,
            NvencRunChunkContext runChunkContext,
            NvencOrderedSubmitWorkerService submitWorker)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _submitWorker = submitWorker ?? throw new ArgumentNullException(nameof(submitWorker));

            if (runChunkContext == null)
            {
                throw new ArgumentNullException(nameof(runChunkContext));
            }

            // Exact-reference correlation: the Submit Worker must be bound to
            // the same process state, and the processor's Sink must be the
            // exact Sink the Run chunk context finalizes, so the terminal can
            // never act on a foreign Run or a foreign Sink.
            if (!ReferenceEquals(submitWorker.ProcessState, _processState))
            {
                throw new ArgumentException(
                    "The Submit Worker must be bound to the same process state.", nameof(submitWorker));
            }

            if (!ReferenceEquals(processor.Sink, runChunkContext.Sink))
            {
                throw new ArgumentException(
                    "The Output Processor Sink must be the exact Sink of the Run chunk context.", nameof(runChunkContext));
            }

            // Bind the exact Run chunk context so a request can never point the
            // terminal at a foreign context.
            _terminalRequest = new NvencRunChunkTerminalRequest(runChunkContext);

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

            return _processState.IsPoisoned;
        }
    }
}
