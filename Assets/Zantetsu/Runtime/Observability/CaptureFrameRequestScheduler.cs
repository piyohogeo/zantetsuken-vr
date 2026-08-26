using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Schedules capture frame requests through a
    /// <see cref="CaptureFrameRequestQueue"/> while recording lifecycle trace
    /// events via a <see cref="CaptureFrameTraceObserver"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Does not own, dispose, drain, or clear the queue or the observer, and
    /// does not generate or mutate capture frame IDs. The caller issues IDs via
    /// <see cref="CaptureFrameIdSequence"/> and passes requests whose
    /// <see cref="CaptureFrameTraceContext"/> already carries the assigned ID.
    /// </para>
    /// <para>
    /// Main-thread only. Because the queue is also main-thread only, it is
    /// assumed that the queue's full/available state cannot change between the
    /// trace recording and the actual enqueue on the queue.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRequestScheduler
    {
        private readonly CaptureFrameRequestQueue _queue;
        private readonly CaptureFrameTraceObserver _observer;

        public CaptureFrameRequestScheduler(
            CaptureFrameRequestQueue queue,
            CaptureFrameTraceObserver observer)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            _queue = queue;
            _observer = observer;
        }

        public int Capacity => _queue.Capacity;

        public int Count => _queue.Count;

        public long TotalAccepted => _queue.TotalAccepted;

        public long TotalRejected => _queue.TotalRejected;

        /// <summary>
        /// Schedules a valid request. When a slot is available, records a queued
        /// event and enqueues the request, returning true. When the queue is
        /// full, records a dropped event with <see cref="CaptureFrameDropReason.RequestQueueFull"/>,
        /// routes the request through the queue's normal full rejection path so
        /// its rejected counter increments, and returns false without
        /// overwriting any existing request.
        /// </summary>
        public bool TrySchedule(in CaptureFrameRequest request)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (_queue.Count < _queue.Capacity)
            {
                _observer.RecordQueued(request.TraceContext);
                _queue.TryEnqueue(request);
                return true;
            }

            _observer.RecordDropped(request.TraceContext, CaptureFrameDropReason.RequestQueueFull);
            _queue.TryEnqueue(request);
            return false;
        }
    }
}
