using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects <see cref="CaptureFrameRecordFactory.Create"/> and the
    /// lease-aware <see cref="CaptureFrameRenderTargetRecordScheduler"/> so a
    /// caller can submit a rented render target lease together with the capture
    /// inputs and receive the accepted record only when both the lease and the
    /// record were registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On entry the lease is owned by the caller. On success the lease's
    /// ownership transfers to the lease registry and
    /// <paramref name="acceptedRecord"/> is the exact instance the record
    /// registry retains (<c>ReferenceEquals</c> true). On backpressure the
    /// result is <c>false</c> with <paramref name="acceptedRecord"/> left
    /// <c>null</c>; the lease has been rolled back and returns to the caller.
    /// On a factory exception the scheduler is never touched and the lease
    /// remains with the caller. On a scheduler exception the exception
    /// propagates unchanged and the lease returns to the caller only when the
    /// scheduler's rollback succeeded.
    /// </para>
    /// <para>
    /// The capture frame ID issued by the factory is consumed even when the
    /// record is rejected or the factory fails after issuance, and is never
    /// reused by this coordinator. All matching, rollback, drop-trace, and
    /// counter behavior is delegated to the factory and the scheduler; none of
    /// it is re-implemented here.
    /// </para>
    /// <para>
    /// This coordinator never calls <c>CaptureFrameRenderTargetPool.Return</c>,
    /// never clears the queue or the registries, and retains no record, lease,
    /// pool, queue, registry, or logger. It is intended for the main thread
    /// only and is <b>not</b> thread-safe; it is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or singleton, and performs no
    /// trace, logging, file I/O, Unity static API access, time lookup, or ID
    /// generation.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetRecordSubmissionCoordinator
    {
        private readonly CaptureFrameRecordFactory _recordFactory;
        private readonly CaptureFrameRenderTargetRecordScheduler _recordScheduler;

        public CaptureFrameRenderTargetRecordSubmissionCoordinator(
            CaptureFrameRecordFactory recordFactory,
            CaptureFrameRenderTargetRecordScheduler recordScheduler)
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

        /// <summary>
        /// Creates a record from the capture inputs and schedules it together
        /// with the lease. On success <paramref name="acceptedRecord"/> is the
        /// generated record; on <c>false</c> it is <c>null</c>.
        /// </summary>
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
            in CaptureFrameRenderTargetLease lease,
            out CaptureFrameRecord acceptedRecord)
        {
            acceptedRecord = null;

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

            if (_recordScheduler.TrySchedule(record, lease))
            {
                acceptedRecord = record;
                return true;
            }

            return false;
        }
    }
}
