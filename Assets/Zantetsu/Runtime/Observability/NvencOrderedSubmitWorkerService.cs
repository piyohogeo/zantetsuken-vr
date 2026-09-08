using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single dedicated Submit Worker for the Phase 0.11 NVENC path. It drives
    /// the threadless <see cref="NvencOrderedSubmitProcessor"/> continuously,
    /// waking only on a coalescing notification so it never busy-spins and the
    /// Main/Render threads never wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One background thread repeatedly calls
    /// <see cref="NvencOrderedSubmitProcessor.TryProcessNext"/> while it reports
    /// progress, then parks on a single manual-reset notification primitive.
    /// Notifications are coalescing state-change hints, not counts: any
    /// producer calls <see cref="Notify"/> without waiting after accepted work
    /// is enqueued, source completion or the Main Thread release handoff
    /// advances, Submit-to-Output capacity is freed, or the process state
    /// changes. The worker resets the signal, re-checks for progress, and only
    /// then waits, so a notification that lands just before the wait is never
    /// lost.
    /// </para>
    /// <para>
    /// A <c>false</c> return from the processor is never reinterpreted here:
    /// an empty Submission Queue, incomplete head source evidence, an
    /// unapplied release handoff, a full Submit-to-Output Queue, and a busy
    /// gate all simply park the worker until the next notification. The worker
    /// never peeks, skips, sorts, or reorders records.
    /// </para>
    /// <para>
    /// A processor exception is a fatal invariant violation: the exact
    /// exception is retained exactly once, the process is poisoned, and the
    /// worker stops without fabricating a Submitted or FailedBeforeSubmit
    /// record or touching later work. A controlled <c>TrySubmit == false</c>
    /// is not fatal; the processor emits a FailedBeforeSubmit record and the
    /// worker continues with the next work.
    /// </para>
    /// <para>
    /// <see cref="BeginDrain"/> is non-waiting and idempotent, is accepted
    /// only while the process is already draining, and must be called after
    /// the process-wide drain. After it the worker keeps processing every
    /// already-accepted record and the held current work, and stops exactly
    /// when the Submission Queue is empty and the processor holds no current
    /// work. Worker stop does not drain the Submit-to-Output Queue, complete
    /// frames, finalize chunks, or join the backend. Poison stop and normal
    /// drain completion are distinguishable through
    /// <see cref="DrainCompleted"/> and <see cref="TryGetFailure"/>.
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> is idempotent and is accepted before the worker
    /// is started or after the worker thread has physically stopped; it is
    /// rejected only while the worker is starting or running, and never
    /// force-stops a running worker. After disposal <see cref="Start"/>,
    /// <see cref="Notify"/>, and <see cref="BeginDrain"/> are rejected before
    /// any side effect.
    /// </para>
    /// <para>
    /// This type owns only its own thread and signals; it owns none of its
    /// injected collaborators, never creates a second worker, and uses no
    /// task, thread pool, timer, sleep, busy loop, queue copy, LINQ, or
    /// static/global hook.
    /// </para>
    /// </remarks>
    internal sealed class NvencOrderedSubmitWorkerService : IDisposable
    {
        internal const string WorkerThreadName = "Zantetsu.NvencSubmitWorker";

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencOrderedSubmitProcessor _processor;
        private readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);
        private Action _settled;

        private const int StateNotStarted = 0;
        private const int StateStarting = 1;
        private const int StateRunning = 2;
        private const int StateDisposed = 3;

        private Thread _workerThread;
        private int _lifecycleState;
        private volatile bool _drainRequested;
        private volatile bool _drainCompleted;
        private volatile Exception _fatalFailure;

        internal NvencOrderedSubmitWorkerService(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitProcessor processor)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            if (processor == null)
            {
                throw new ArgumentNullException(nameof(processor));
            }

            _processState = processState;
            _processor = processor;
        }

        /// <summary>
        /// Starts the single background worker thread exactly once. The thread
        /// is background, uses a fixed name, and never waits with a timeout.
        /// </summary>
        internal void Start()
        {
            // Exactly-once start: only the caller that flips NotStarted to
            // Starting owns startup; any other caller (already starting,
            // running, or disposed) observes no side effect.
            if (Interlocked.CompareExchange(ref _lifecycleState, StateStarting, StateNotStarted)
                != StateNotStarted)
            {
                return;
            }

            try
            {
                Thread thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = WorkerThreadName,
                };
                thread.Start();

                // Publish the thread and flip to Running only after the
                // physical thread exists, so Starting is never reported as
                // stopped and only a started thread feeds the physical check.
                Volatile.Write(ref _workerThread, thread);
                Volatile.Write(ref _lifecycleState, StateRunning);
            }
            catch
            {
                // Startup failed (for example Thread.Start threw); release the
                // startup right so a later Start can retry.
                Interlocked.CompareExchange(ref _lifecycleState, StateNotStarted, StateStarting);
                throw;
            }
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
                throw new ObjectDisposedException(nameof(NvencOrderedSubmitWorkerService));
            }

            _signal.Set();
        }

        /// <summary>
        /// Non-waiting, idempotent worker drain request. Accepted only while
        /// the process is already draining; returns false without any state
        /// change while Running or Poisoned. The worker keeps processing
        /// accepted work and stops only once nothing remains to process.
        /// </summary>
        internal bool BeginDrain()
        {
            if (Volatile.Read(ref _lifecycleState) == StateDisposed)
            {
                throw new ObjectDisposedException(nameof(NvencOrderedSubmitWorkerService));
            }

            if (!_processState.IsDraining)
            {
                return false;
            }

            _drainRequested = true;
            _signal.Set();
            return true;
        }

        /// <summary>
        /// Non-waiting physical stop confirmation: true only once the worker
        /// thread has physically exited (or after disposal). False while the
        /// worker is starting or running, so only a started thread feeds the
        /// physical <see cref="Thread.IsAlive"/> check and the stop
        /// notification is never reported as stopped mid-delivery.
        /// </summary>
        internal bool IsStopped
        {
            get
            {
                int state = Volatile.Read(ref _lifecycleState);
                if (state == StateDisposed)
                {
                    return true;
                }

                if (state != StateRunning)
                {
                    return false;
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
        /// observers from running; the worker swallows the exception. The worker
        /// keeps ownership of its signal object, so callers can subscribe and
        /// unsubscribe but can never reset, set, or dispose it.
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
        /// Releases the owned wait primitive. Allowed before the worker is
        /// started or after it has physically stopped; rejected with
        /// <see cref="InvalidOperationException"/> only while the worker is
        /// starting or running, and never force-stops a running worker.
        /// Idempotent.
        /// </summary>
        public void Dispose()
        {
            // NotStarted → Disposed must be a CAS so disposal is mutually
            // exclusive with Start's NotStarted → Starting claim: exactly one
            // of the two wins, and Start never creates a thread after the
            // signal is released.
            if (Interlocked.CompareExchange(ref _lifecycleState, StateDisposed, StateNotStarted)
                == StateNotStarted)
            {
                _signal.Dispose();
                return;
            }

            int state = Volatile.Read(ref _lifecycleState);
            if (state == StateDisposed)
            {
                return; // another disposer already finished; idempotent
            }

            if (state == StateStarting)
            {
                throw new InvalidOperationException(
                    "The Submit Worker is starting; dispose is allowed only before Start or after the worker thread has physically stopped.");
            }

            // state == StateRunning: only physical stop permits disposal.
            Thread worker = Volatile.Read(ref _workerThread);
            if (worker == null || worker.IsAlive)
            {
                throw new InvalidOperationException(
                    "The Submit Worker thread has not physically stopped; dispose is allowed only after the worker thread has exited.");
            }

            // Running → Disposed (physically stopped). CAS so concurrent
            // disposals agree on a single transition and a single release.
            if (Interlocked.CompareExchange(ref _lifecycleState, StateDisposed, StateRunning) == StateRunning)
            {
                _signal.Dispose();
            }
        }

        /// <summary>
        /// True only when the worker stopped because a requested drain was
        /// fully satisfied. False for poison and fatal stops.
        /// </summary>
        internal bool DrainCompleted => _drainCompleted;

        /// <summary>
        /// The exact process state this worker is bound to, for exact-reference
        /// correlation by the Output Worker.
        /// </summary>
        internal NvencCaptureProcessState ProcessState => _processState;

        /// <summary>
        /// O(1) correlation predicate: true only when this worker and its
        /// internal Submit Processor are bound to the exact process state and
        /// emit into the exact Submit-to-Output Queue, without exposing the
        /// queue.
        /// </summary>
        internal bool IsCorrelatedWith(
            NvencCaptureProcessState processState,
            NvencFixedSpscQueue<NvencSubmitToOutputRecord> queue)
        {
            return ReferenceEquals(_processState, processState) &&
                _processor.IsCorrelatedWith(processState, queue);
        }

        /// <summary>
        /// O(1) resource correlation predicate: true only when this worker's
        /// internal Submit Processor and source release coordinator are bound
        /// to the exact Work, Sample, GPU Conversion Sync, Submit-to-Output
        /// credit, and Frame Completion credit pools, without exposing them.
        /// </summary>
        internal bool IsCorrelatedWithResources(
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            return _processor.IsCorrelatedWithResources(
                workSlots, sampleSlots, syncSlots, submitToOutputCredits, frameCompletionCredits);
        }

        /// <summary>
        /// Non-throwing diagnostic for the first fatal processor exception, if
        /// any. Returns false when the worker stopped without a fatal failure.
        /// </summary>
        internal bool TryGetFailure(out Exception failure)
        {
            failure = Volatile.Read(ref _fatalFailure);
            return failure != null;
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    // Process until no progress or until a stop condition.
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

                    // Park on the coalescing signal. Reset first and re-check
                    // once, so a notification that landed between the last
                    // failed TryProcessNext and Reset is observed instead of
                    // lost.
                    _signal.Reset();

                    if (TryStop())
                    {
                        return;
                    }

                    if (TryProcessNextSafely())
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
                // After this point the worker never touches its internal signal
                // again. The stop notification is raised last, but IsStopped
                // stays false until the thread physically exits, so a subscriber
                // must join the thread before disposing.
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
                // Invoke the snapshot once without materializing an invocation
                // list: no per-park managed allocation. A single observer is
                // the supported shape, and subscription-time delegate
                // combination happens before the worker thread starts.
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

            if (_drainRequested && !_processor.HasPendingWork)
            {
                _drainCompleted = true;
                return true;
            }

            return false;
        }
    }
}
