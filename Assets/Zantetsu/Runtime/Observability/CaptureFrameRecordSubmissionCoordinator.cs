using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects <see cref="CaptureFrameRecordFactory.Create"/> and
    /// <see cref="CaptureFrameRecordScheduler.TrySchedule"/> in the correct
    /// order so a caller can neither swap the generated record nor forget to
    /// schedule it: every successful submit produces exactly one record and
    /// hands that same instance to the scheduler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation order is fixed: the record is created through the factory,
    /// the exact same instance is passed to the scheduler, and only when the
    /// scheduler accepts it is the record surfaced through
    /// <paramref name="acceptedRecord"/>.
    /// </para>
    /// <para>
    /// On success <paramref name="acceptedRecord"/> is the same instance the
    /// registry retains (<c>ReferenceEquals</c> true) and the request the queue
    /// receives is that record's request, byte-for-byte.
    /// </para>
    /// <para>
    /// Backpressure (queue full or registry full) returns <c>false</c> with
    /// <paramref name="acceptedRecord"/> left <c>null</c>; the drop trace and
    /// counter behavior are delegated to the scheduler unchanged. The capture
    /// frame ID issued by the factory is consumed and never reused.
    /// </para>
    /// <para>
    /// If the factory throws, the scheduler is never touched and the exception
    /// propagates unchanged. If the scheduler throws, its exception propagates
    /// unchanged and no rollback is performed here; registry rollback remains
    /// the scheduler's own responsibility.
    /// </para>
    /// <para>
    /// Owns, disposes, clears, and retains nothing: it holds no reference to a
    /// record, queue, registry, or logger beyond the injected factory and
    /// scheduler. It does not generate capture frame IDs (the factory does) and
    /// does not register, roll back, or trace (the scheduler does).
    /// </para>
    /// <para>
    /// Intended for the main thread only and <b>not</b> thread-safe. Not a
    /// MonoBehaviour, singleton, or <see cref="IDisposable"/>, and performs no
    /// Unity static API access, file I/O, logging, or additional trace.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRecordSubmissionCoordinator
    {
        private readonly CaptureFrameRecordFactory _recordFactory;
        private readonly CaptureFrameRecordScheduler _recordScheduler;

        public CaptureFrameRecordSubmissionCoordinator(
            CaptureFrameRecordFactory recordFactory,
            CaptureFrameRecordScheduler recordScheduler)
        {
            if (recordFactory == null)
            {
                throw new ArgumentNullException(nameof(recordFactory));
            }

            if (recordScheduler == null)
            {
                throw new ArgumentNullException(nameof(recordScheduler));
            }

            _recordFactory = recordFactory;
            _recordScheduler = recordScheduler;
        }

        public bool TrySubmit(
            long timestamp,
            long unityFrameId,
            long fixedStepId,
            int threadId,
            long openXRFrameId,
            long slashId,
            long frontEdgeId,
            long objectId,
            uint objectGeneration,
            long taskId,
            in CaptureFrameTiming timing,
            in CapturePoseSample headPose,
            in CapturePoseSample leftControllerPose,
            in CapturePoseSample rightControllerPose,
            int commitPathId,
            out CaptureFrameRecord acceptedRecord)
        {
            acceptedRecord = null;

            // 1. Create the record through the factory. If this throws, the
            // scheduler is never touched and the factory's existing ID
            // consumption contract applies unchanged.
            CaptureFrameRecord record = _recordFactory.Create(
                timestamp,
                unityFrameId,
                fixedStepId,
                threadId,
                openXRFrameId,
                slashId,
                frontEdgeId,
                objectId,
                objectGeneration,
                taskId,
                timing,
                headPose,
                leftControllerPose,
                rightControllerPose,
                commitPathId);

            // 2. Hand the exact same instance to the scheduler. Registration,
            // rollback, drop trace, and counters are all delegated unchanged.
            if (_recordScheduler.TrySchedule(record))
            {
                acceptedRecord = record;
                return true;
            }

            return false;
        }
    }
}
