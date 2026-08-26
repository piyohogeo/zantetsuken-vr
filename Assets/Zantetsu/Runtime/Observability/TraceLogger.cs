using System;
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
        private readonly TraceRingBuffer _history;
        private bool _disposed;

        public TraceLogger(int historyCapacity)
        {
            if (historyCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(historyCapacity), historyCapacity, "History capacity must be greater than zero.");
            }

            _queue = new NativeQueue<TraceEvent>(Allocator.Persistent);
            _history = new TraceRingBuffer(historyCapacity);
            _disposed = false;
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
                return _queue.AsParallelWriter();
            }
        }

        /// <summary>Enqueues an event from the main thread.</summary>
        public void Enqueue(in TraceEvent traceEvent)
        {
            ThrowIfDisposed();
            _queue.Enqueue(traceEvent);
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
        /// Disposes the owned native queue. Calling it more than once is safe.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_queue.IsCreated)
            {
                _queue.Dispose();
            }
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
