using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Schedules a <see cref="CaptureFrameRecord"/> by registering it into a
    /// <see cref="CaptureFrameRecordRegistry"/> and then delegating its request
    /// to a <see cref="CaptureFrameRequestScheduler"/>, so that on success both
    /// sides exist and on failure no partial state is left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the success path the registry holds the record and the request queue
    /// holds the record's request, and a single queued trace event is recorded.
    /// A capture frame ID that is already registered is rejected with an
    /// <see cref="ArgumentException"/> (or the registry's
    /// <see cref="InvalidOperationException"/> when the same ID carries a
    /// different request) before any queue-capacity check, so duplicate
    /// correlation IDs are never recorded as backpressure.
    /// When the request queue is already full this type delegates to the
    /// request scheduler unchanged: the existing
    /// <see cref="CaptureFrameDropReason.RequestQueueFull"/> trace and the
    /// queue's rejected counter are the source of truth, and the registry is
    /// not touched.
    /// </para>
    /// <para>
    /// When the registry is full, a single
    /// <see cref="CaptureFrameDropReason.FrameRecordRegistryFull"/> drop trace
    /// is recorded and the request queue is left untouched.
    /// </para>
    /// <para>
    /// If the request scheduler throws or returns <c>false</c> after the record
    /// was registered, the record is rolled back with
    /// <see cref="CaptureFrameRecordRegistry.TryRemove"/>. The removed record
    /// must be the exact same instance that was registered; otherwise the
    /// registry/scheduler invariant is considered violated and an
    /// <see cref="InvalidOperationException"/> is thrown. On the normal
    /// exception path the original exception is rethrown unchanged after
    /// rollback.
    /// </para>
    /// <para>
    /// Does not own, dispose, or clear the request scheduler, the registry, the
    /// observer, or any record, and does not generate or mutate capture frame
    /// IDs. Does not log, perform file I/O, or use Unity static APIs.
    /// </para>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRecordScheduler
    {
        private readonly CaptureFrameRequestScheduler _requestScheduler;
        private readonly CaptureFrameRecordRegistry _recordRegistry;
        private readonly CaptureFrameTraceObserver _observer;

        public CaptureFrameRecordScheduler(
            CaptureFrameRequestScheduler requestScheduler,
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFrameTraceObserver observer)
        {
            if (requestScheduler == null)
            {
                throw new ArgumentNullException(nameof(requestScheduler));
            }

            if (recordRegistry == null)
            {
                throw new ArgumentNullException(nameof(recordRegistry));
            }

            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            _requestScheduler = requestScheduler;
            _recordRegistry = recordRegistry;
            _observer = observer;
        }

        /// <summary>
        /// Registers the record into the registry and schedules its request.
        /// Returns <c>true</c> only when both the record and its request were
        /// accepted.
        /// </summary>
        public bool TrySchedule(CaptureFrameRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            CaptureFrameRequest request = record.Request;

            // Reject a duplicate capture frame ID before the queue-capacity
            // check so the rejection is consistent regardless of queue state.
            // TryGet reuses the registry's full request matching contract:
            //   - true: same ID and identical request → duplicate.
            //   - false: no matching ID.
            //   - InvalidOperationException: same ID, different request.
            if (_recordRegistry.TryGet(request, out _))
            {
                throw new ArgumentException("A record with the same capture frame ID is already registered.", nameof(record));
            }

            // Request queue already full: delegate unchanged. The existing
            // RequestQueueFull trace and the queue's rejected counter are the
            // source of truth; the registry is not temporarily registered.
            if (_requestScheduler.Count >= _requestScheduler.Capacity)
            {
                return _requestScheduler.TrySchedule(request);
            }

            // Request queue has room: register the record first.
            if (!_recordRegistry.TryRegister(record))
            {
                // Registry full: record a FrameRecordRegistryFull drop only.
                _observer.RecordDropped(request.TraceContext, CaptureFrameDropReason.FrameRecordRegistryFull);
                return false;
            }

            bool scheduled;
            try
            {
                scheduled = _requestScheduler.TrySchedule(request);
            }
            catch
            {
                Rollback(record);
                throw;
            }

            if (!scheduled)
            {
                Rollback(record);
                return false;
            }

            return true;
        }

        private void Rollback(CaptureFrameRecord record)
        {
            if (!_recordRegistry.TryRemove(record.Request, out CaptureFrameRecord removed))
            {
                throw new InvalidOperationException("Rollback failed: the registered record could not be removed from the registry.");
            }

            if (!ReferenceEquals(removed, record))
            {
                throw new InvalidOperationException("Rollback failed: the registry returned a different record instance than the one that was registered.");
            }
        }
    }
}
