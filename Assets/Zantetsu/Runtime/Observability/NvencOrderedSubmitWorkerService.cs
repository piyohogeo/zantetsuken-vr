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
    /// <see cref="Dispose"/> is idempotent and is accepted only after the
    /// worker thread has physically stopped; it never force-stops a running
    /// worker and only releases the owned wait primitives.
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
        private readonly ManualResetEventSlim _settled = new ManualResetEventSlim(false);

        private Thread _workerThread;
        private volatile bool _drainRequested;
        private volatile bool _drainCompleted;
        private volatile bool _workerStopped;
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
            if (_workerThread != null)
            {
                return;
            }

            _workerThread = new Thread(Run)
            {
                IsBackground = true,
                Name = WorkerThreadName,
            };
            _workerThread.Start();
        }

        /// <summary>
        /// Coalescing, non-waiting notification that observable state changed.
        /// The caller never blocks; only the worker waits. Multiple calls may
        /// coalesce into a single wake and must not be interpreted as a count.
        /// </summary>
        internal void Notify()
        {
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
            if (!_processState.IsDraining)
            {
                return false;
            }

            _drainRequested = true;
            _signal.Set();
            return true;
        }

        /// <summary>
        /// Non-waiting physical stop confirmation: true once the worker thread
        /// has exited.
        /// </summary>
        internal bool IsStopped => _workerStopped;

        /// <summary>
        /// Instance-local observation signal that is set whenever the worker
        /// has drained all currently-processable work and is about to park, or
        /// when it stops. Production correctness never depends on this signal;
        /// it exists only so callers can observe worker quiescence without
        /// polling.
        /// </summary>
        internal ManualResetEventSlim Settled => _settled;

        /// <summary>
        /// Idempotent release of the owned wait primitives. Accepted only
        /// after the worker thread has physically stopped; a running worker is
        /// never force-stopped by disposal.
        /// </summary>
        public void Dispose()
        {
            if (!_workerStopped)
            {
                return;
            }

            _signal.Dispose();
            _settled.Dispose();
        }

        /// <summary>
        /// True only when the worker stopped because a requested drain was
        /// fully satisfied. False for poison and fatal stops.
        /// </summary>
        internal bool DrainCompleted => _drainCompleted;

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

                    _settled.Set();
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
                _settled.Set();
                _workerStopped = true;
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
