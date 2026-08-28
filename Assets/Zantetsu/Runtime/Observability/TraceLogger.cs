using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Unity.Collections;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Collects <see cref="TraceEvent"/> values from jobs and from the main
    /// thread into a persistent <see cref="NativeQueue{T}"/>, and drains them
    /// into a fixed-capacity <see cref="TraceRingBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Not a MonoBehaviour and not a singleton. This class owns its own
    /// <see cref="NativeQueue{T}"/>; the caller is responsible for completing
    /// all scheduled jobs that write through <see cref="JobWriter"/> before
    /// calling <see cref="Dispose"/>. <see cref="Dispose"/> does not complete
    /// or dispose any other native container or job.
    /// </remarks>
    public sealed class TraceLogger : IDisposable
    {
        private NativeQueue<TraceEvent> _queue;
        private NativeArray<int> _sealGate;
        private readonly TraceRingBuffer _history;
        private readonly long _testRunId;
        private readonly int _mainThreadId;
        private TraceRunSealReceipt _issuedSealReceipt;
        private bool _disposed;

        /// <summary>
        /// Creates a legacy logger without a capture run seal gate.
        /// </summary>
        public TraceLogger(int historyCapacity)
        {
            if (historyCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(historyCapacity), historyCapacity, "History capacity must be greater than zero.");
            }

            _queue = new NativeQueue<TraceEvent>(Allocator.Persistent);
            _history = new TraceRingBuffer(historyCapacity);
            _sealGate = default;
            _testRunId = 0;
            _mainThreadId = 0;
            _disposed = false;
        }

        /// <summary>
        /// Creates a capture run logger bound to a single
        /// <paramref name="testRunId"/> with an atomic seal gate. The binding
        /// is fixed for the lifetime of the logger and cannot be re-bound.
        /// </summary>
        internal TraceLogger(int historyCapacity, long testRunId)
        {
            if (historyCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(historyCapacity), historyCapacity, "History capacity must be greater than zero.");
            }

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            _history = new TraceRingBuffer(historyCapacity);

            Exception creationFailure = null;
            try
            {
                _queue = new NativeQueue<TraceEvent>(Allocator.Persistent);
                _sealGate = TraceRunSealGate.Create();
            }
            catch (Exception ex)
            {
                creationFailure = ex;
            }

            if (creationFailure != null)
            {
                Exception cleanupFailure = DisposeOwnedContainersForCleanup();
                if (cleanupFailure == null)
                {
                    ExceptionDispatchInfo.Capture(creationFailure).Throw();
                }
                else
                {
                    throw new AggregateException(creationFailure, cleanupFailure);
                }
            }

            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _testRunId = testRunId;
            _disposed = false;
        }

        /// <summary>The bound test run ID, or 0 for a legacy logger.</summary>
        internal long TestRunId => _testRunId;

        /// <summary>Whether this logger is bound to a capture run.</summary>
        internal bool IsCaptureRun => _testRunId != 0;

        /// <summary>Whether the current thread is the thread that constructed this logger.</summary>
        internal bool IsOnConstructingThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// The exact seal receipt this logger issued, or null before the seal
        /// completes. Used to reject forged receipts and value copies.
        /// </summary>
        internal TraceRunSealReceipt IssuedSealReceipt => _issuedSealReceipt;

        /// <summary>
        /// The current seal state of the capture run, or
        /// <see cref="TraceRunSealState.Open"/> for a legacy logger.
        /// </summary>
        internal TraceRunSealState SealState
        {
            get
            {
                ThrowIfDisposed();
                return IsCaptureRun
                    ? (TraceRunSealState)TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotSealState)
                    : TraceRunSealState.Open;
            }
        }

        /// <summary>Whether the backing native queue currently holds no events.</summary>
        internal bool IsQueueEmpty
        {
            get
            {
                ThrowIfDisposed();
                return _queue.IsEmpty();
            }
        }

        /// <summary>Whether the backing native queue is created (and not disposed).</summary>
        public bool IsCreated => _queue.IsCreated;

        /// <summary>Fixed number of history events the ring buffer can hold.</summary>
        public int HistoryCapacity
        {
            get
            {
                ThrowIfDisposed();
                return _history.Capacity;
            }
        }

        /// <summary>Number of events currently held in the drained history.</summary>
        public int HistoryCount
        {
            get
            {
                ThrowIfDisposed();
                return _history.Count;
            }
        }

        /// <summary>Total number of events ever drained into the history, including overwritten ones.</summary>
        public long TotalWritten
        {
            get
            {
                ThrowIfDisposed();
                return _history.TotalWritten;
            }
        }

        /// <summary>Number of oldest history events discarded due to capacity overflow.</summary>
        public long OverwrittenCount
        {
            get
            {
                ThrowIfDisposed();
                return _history.OverwrittenCount;
            }
        }

        /// <summary>
        /// Returns a parallel writer for enqueueing events from jobs. Obtaining
        /// a writer does not create a new queue. The relative order of events
        /// enqueued in parallel from jobs is not guaranteed; downstream
        /// analysis must rely on Timestamp or ID fields rather than enqueue
        /// order.
        /// </summary>
        public NativeQueue<TraceEvent>.ParallelWriter JobWriter
        {
            get
            {
                ThrowIfDisposed();

                if (IsCaptureRun)
                {
                    throw new InvalidOperationException("A capture run logger does not expose the raw job writer; use CaptureRunWriter instead.");
                }

                return _queue.AsParallelWriter();
            }
        }

        /// <summary>
        /// Returns a seal-aware writer for a capture run logger. The writer
        /// shares the logger's seal gate and the bound test run ID.
        /// </summary>
        internal SealableTraceWriter CaptureRunWriter
        {
            get
            {
                ThrowIfDisposed();

                if (!IsCaptureRun)
                {
                    throw new InvalidOperationException("A legacy logger does not expose a capture run writer.");
                }

                return new SealableTraceWriter(_queue.AsParallelWriter(), _sealGate, _testRunId);
            }
        }

        /// <summary>
        /// Number of enqueue failures accounted while the run was still mutable
        /// (open processing or sealing before the failure cutoff). Non-negative
        /// and saturating at <see cref="int.MaxValue"/>.
        /// </summary>
        internal int TraceEnqueueFailureCount
        {
            get
            {
                ThrowIfDisposed();
                return IsCaptureRun ? TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotMutableFailures) : 0;
            }
        }

        /// <summary>
        /// Number of enqueue failures fixed into the sealed count when the seal
        /// completed. Non-negative and saturating at
        /// <see cref="int.MaxValue"/>; never changes after the seal.
        /// </summary>
        internal int SealedTraceEnqueueFailureCount
        {
            get
            {
                ThrowIfDisposed();
                return IsCaptureRun ? TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotSealedFailures) : 0;
            }
        }

        /// <summary>
        /// Number of enqueue attempts observed after the failure cutoff closed
        /// or after the run was sealed. Non-negative and saturating at
        /// <see cref="int.MaxValue"/>.
        /// </summary>
        internal int PostSealTraceEnqueueAttemptCount
        {
            get
            {
                ThrowIfDisposed();
                return IsCaptureRun ? TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotPostSealAttempts) : 0;
            }
        }

        /// <summary>Enqueues an event from the main thread.</summary>
        public void Enqueue(in TraceEvent traceEvent)
        {
            ThrowIfDisposed();

            if (!IsCaptureRun)
            {
                _queue.Enqueue(traceEvent);
                return;
            }

            if (traceEvent.TestRunId != _testRunId)
            {
                throw new ArgumentException("The trace event's TestRunId must match the bound capture run.", nameof(traceEvent));
            }

            TraceRunSealGate.Increment(_sealGate, TraceRunSealGate.SlotActiveWriters);

            try
            {
                int sealState = TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotSealState);
                if (sealState != (int)TraceRunSealState.Open)
                {
                    TraceRunSealGate.RecordRejection(_sealGate, sealState);
                    return;
                }

                bool enqueued = false;
                try
                {
                    _queue.Enqueue(traceEvent);
                    enqueued = true;
                }
                finally
                {
                    if (!enqueued)
                    {
                        TraceRunSealGate.RecordMutableFailure(_sealGate);
                    }
                }
            }
            finally
            {
                TraceRunSealGate.Decrement(_sealGate, TraceRunSealGate.SlotActiveWriters);
            }
        }

        /// <summary>
        /// Drains every queued event into the history ring buffer and returns
        /// the number of events processed by this call.
        /// </summary>
        public int Drain()
        {
            ThrowIfDisposed();
            using (ZantetsuProfilerMarkers.TraceDrain.Auto())
            {
                int drained = 0;
                while (_queue.TryDequeue(out TraceEvent traceEvent))
                {
                    _history.Write(traceEvent);
                    drained++;
                }

                return drained;
            }
        }

        /// <summary>
        /// Drains every queued event into the history ring buffer while also
        /// writing, in drain order, up to <paramref name="maximumCapturedCount"/>
        /// of those events into <paramref name="capture"/>. Returns the total
        /// number of events drained from the queue; <paramref name="capturedCount"/>
        /// reports how many were duplicated into the capture.
        /// </summary>
        internal int Drain(TraceRingBuffer capture, int maximumCapturedCount, out int capturedCount)
        {
            ThrowIfDisposed();

            if (capture == null)
            {
                throw new ArgumentNullException(nameof(capture));
            }

            using (ZantetsuProfilerMarkers.TraceDrain.Auto())
            {
                int drained = 0;
                int captured = 0;
                while (_queue.TryDequeue(out TraceEvent traceEvent))
                {
                    _history.Write(traceEvent);
                    drained++;

                    if (captured < maximumCapturedCount)
                    {
                        capture.Write(traceEvent);
                        captured++;
                    }
                }

                capturedCount = captured;
                return drained;
            }
        }

        /// <summary>
        /// Seals a capture run and drains every remaining queued event through
        /// <paramref name="recorder"/>. Must be called from the main thread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The operation validates its inputs before touching the gate, so any
        /// pre-validation failure leaves the seal state, queue, recorder, and
        /// counters unchanged. After the seal state moves to
        /// <see cref="TraceRunSealState.Sealing"/> it never rolls back to
        /// <see cref="TraceRunSealState.Open"/>: a post-CAS failure fails
        /// closed and is not translated into another exception type.
        /// </para>
        /// <para>
        /// The final drain reuses the recorder's normal post-roll FIFO path
        /// (its <see cref="TraceFlightRecorder.NormalPostRollCapacity"/>,
        /// overflow accounting, and terminal-reserve logic); the logger never
        /// reimplements ring writes here.
        /// </para>
        /// </remarks>
        internal TraceRunSealReceipt SealAndDrainRunForFreeze(long testRunId, TraceFlightRecorder recorder)
        {
            // 1. Only a capture run logger can be sealed.
            ThrowIfDisposed();
            if (!IsCaptureRun)
            {
                throw new InvalidOperationException("Only a capture run logger can be sealed.");
            }

            // 2. The supplied run ID must match the bound run.
            if (testRunId <= 0 || testRunId != _testRunId)
            {
                throw new ArgumentException("The test run ID must match the bound capture run.", nameof(testRunId));
            }

            // 3. A recorder is required.
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            // 4. The recorder must reference this logger.
            if (!ReferenceEquals(recorder.Logger, this))
            {
                throw new ArgumentException("The recorder must reference this logger.", nameof(recorder));
            }

            // 5. The recorder must be reserve-configured and CapturingPostRoll.
            if (recorder.FreezeTerminalTraceReserve <= 0 || recorder.State != TraceFlightRecorderState.CapturingPostRoll)
            {
                throw new InvalidOperationException("The recorder must be reserve-configured and CapturingPostRoll.");
            }

            // 6. The seal must run on the thread that constructed the capture
            // logger. Off-thread calls are rejected before the CAS with no side
            // effects.
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException("The seal must be performed on the thread that constructed the capture logger.");
            }

            // 7. Atomically move Open -> Sealing.
            int original = TraceRunSealGate.CompareExchange(_sealGate, TraceRunSealGate.SlotSealState, (int)TraceRunSealState.Sealing, (int)TraceRunSealState.Open);
            if (original != (int)TraceRunSealState.Open)
            {
                throw new InvalidOperationException("The capture run is not in the Open state.");
            }

            // 8. Wait for every in-flight writer to exit its active section.
            SpinWait spinWait = default;
            while (TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotActiveWriters) != 0)
            {
                spinWait.SpinOnce();
            }

            // 9. Final drain through the recorder's normal post-roll path.
            int drained = recorder.Drain();

            // 10. Re-check that the queue is empty.
            if (_queue.Count != 0)
            {
                throw new InvalidOperationException("The run queue is not empty after the final drain.");
            }

            // 11. Close the mutable failure cutoff, then fix the sealed count
            // once. Re-waiting after the cutoff closes makes the cutoff and the
            // accounting destination atomic: any rejection still in flight
            // before the close is counted in the mutable count before it is
            // fixed.
            TraceRunSealGate.CompareExchange(_sealGate, TraceRunSealGate.SlotCutoffClosed, 1, 0);

            spinWait = default;
            while (TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotActiveWriters) != 0)
            {
                spinWait.SpinOnce();
            }

            int sealedFailures = TraceRunSealGate.Read(_sealGate, TraceRunSealGate.SlotMutableFailures);
            TraceRunSealGate.CompareExchange(_sealGate, TraceRunSealGate.SlotSealedFailures, sealedFailures, 0);

            // 12. Pre-build the seal receipt BEFORE publishing Sealed, so that
            // publishing Sealed is the last observable mutation and nothing can
            // allocate, validate, or throw after it.
            TraceRunSealReceipt receipt = new TraceRunSealReceipt(
                this,
                recorder,
                _testRunId,
                drained,
                recorder.CapturedPostRollCount,
                recorder.TraceCaptureOverflowCount,
                sealedFailures);

            // Record the exact issued instance so BeginFreezeTerminalAppend can
            // reject forged receipts and value copies.
            _issuedSealReceipt = receipt;

            // 13. Publish Sealed last.
            TraceRunSealGate.CompareExchange(_sealGate, TraceRunSealGate.SlotSealState, (int)TraceRunSealState.Sealed, (int)TraceRunSealState.Sealing);

            // 14. Return the pre-built receipt.
            return receipt;
        }

        /// <summary>
        /// Returns the history event at the given chronological index, where 0
        /// is the oldest stored event.
        /// </summary>
        public TraceEvent GetHistoryEvent(int chronologicalIndex)
        {
            ThrowIfDisposed();
            return _history[chronologicalIndex];
        }

        /// <summary>
        /// Copies the history events, oldest first, into
        /// <paramref name="destination"/> starting at
        /// <paramref name="destinationIndex"/>.
        /// </summary>
        public void CopyHistoryTo(TraceEvent[] destination, int destinationIndex)
        {
            ThrowIfDisposed();
            _history.CopyTo(destination, destinationIndex);
        }

        /// <summary>Clears the history ring buffer and its counters. The queue is left untouched.</summary>
        public void ClearHistory()
        {
            ThrowIfDisposed();
            _history.Clear();
        }

        /// <summary>
        /// Disposes the owned native queue and seal gate. Calling it more than
        /// once is safe. The disposed flag is set only after every owned native
        /// container has been released successfully; if any cleanup fails, all
        /// containers are still attempted and the aggregated exception is
        /// rethrown so the call can be retried.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Exception failure = DisposeOwnedContainersForCleanup();
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            _disposed = true;
        }

        /// <summary>
        /// Disposes every owned native container independently, attempting all
        /// of them even when one fails, and aggregates any cleanup failures.
        /// Returns null when all containers were released successfully.
        /// </summary>
        private Exception DisposeOwnedContainersForCleanup()
        {
            Exception first = null;

            if (_sealGate.IsCreated)
            {
                try
                {
                    _sealGate.Dispose();
                }
                catch (Exception ex)
                {
                    first = ex;
                }
            }

            if (_queue.IsCreated)
            {
                try
                {
                    _queue.Dispose();
                }
                catch (Exception ex)
                {
                    first = first == null ? ex : new AggregateException(first, ex);
                }
            }

            return first;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TraceLogger));
            }
        }
    }
}
