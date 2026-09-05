using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single-producer submission admission boundary for the Phase 0.11 NVENC
    /// path. It serializes <see cref="TryAccept"/> into the fixed SPSC
    /// Submission Queue after atomically reserving one Work Slot, one Sample
    /// Slot, and one GPU Conversion Sync credit, transferring the caller's
    /// surface to the backend, and issuing the accepted work token. It owns
    /// none of its injected collaborators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acceptance follows a fixed order: the process state must be accepting,
    /// the queue must have capacity, one Work Slot and one Sample Slot must be
    /// rentable, and only then the short admission guard is acquired
    /// non-waiting. Inside that guard one GPU Conversion Sync credit is rented
    /// (serialized with the release coordinator's return on the same gate), the
    /// work token is issued from the backend owner and the work slot
    /// generation, the surface is transferred, the record is built, and it is
    /// enqueued exactly once, so the successful enqueue is the Accepted
    /// linearization point ordered before any later drain or poison. Capacity
    /// exhaustion of any kind is <c>Backpressured</c> and leaves the surface
    /// caller-owned with reservations released in reverse order (sync, then
    /// sample, then work). A stopped or poisoned process returns
    /// <c>NotAccepting</c> without transferring the surface.
    /// </para>
    /// <para>
    /// Under the strict single-producer contract a post-transfer enqueue failure
    /// is an internal invariant violation and is reported as an exception, not
    /// converted to backpressure. There is no rollback API that returns the
    /// surface to the caller and no dynamic queue growth. <see cref="TryDequeue"/>
    /// returns the queue's FIFO unchanged with no sorting, peeking, or skip.
    /// </para>
    /// <para>
    /// This type is not disposable, performs no wait, sleep, allocation, file
    /// I/O, or native call in the admission path, and never generates a new
    /// owner identity; the backend owner is fixed at construction.
    /// </para>
    /// </remarks>
    internal sealed class NvencSubmissionAdmissionCoordinator
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencGpuConversionSyncPool _syncSlots;
        private readonly NvencFixedSpscQueue<NvencSubmissionRecord> _submissionQueue;
        private readonly Guid _backendOwner;

        internal NvencSubmissionAdmissionCoordinator(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencFixedSpscQueue<NvencSubmissionRecord> submissionQueue,
            Guid backendOwner)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            if (workSlots == null)
            {
                throw new ArgumentNullException(nameof(workSlots));
            }

            if (sampleSlots == null)
            {
                throw new ArgumentNullException(nameof(sampleSlots));
            }

            if (syncSlots == null)
            {
                throw new ArgumentNullException(nameof(syncSlots));
            }

            if (submissionQueue == null)
            {
                throw new ArgumentNullException(nameof(submissionQueue));
            }

            if (backendOwner == Guid.Empty)
            {
                throw new ArgumentException("Backend owner must not be empty.", nameof(backendOwner));
            }

            _processState = processState;
            _workSlots = workSlots;
            _sampleSlots = sampleSlots;
            _syncSlots = syncSlots;
            _submissionQueue = submissionQueue;
            _backendOwner = backendOwner;
        }

        internal CaptureSubmitStatus TryAccept(
            CaptureFrameEnvelope frame,
            CaptureSurfaceLease surface,
            out CaptureFrameWorkToken workToken)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (!surface.IsCallerOwned)
            {
                throw new ArgumentException("Surface must be caller-owned.", nameof(surface));
            }

            if (frame.TestRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), frame.TestRunId, "Test run ID must be positive.");
            }

            if (frame.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), frame.CaptureFrameId, "Capture frame ID must be positive.");
            }

            workToken = default;

            if (!_processState.IsAccepting)
            {
                return CaptureSubmitStatus.NotAccepting;
            }

            if (!_submissionQueue.CanEnqueue)
            {
                return CaptureSubmitStatus.Backpressured;
            }

            if (!_workSlots.TryRent(out NvencCaptureWorkSlotLease workSlot))
            {
                return _processState.IsAccepting
                    ? CaptureSubmitStatus.Backpressured
                    : CaptureSubmitStatus.NotAccepting;
            }

            if (!_sampleSlots.TryRent(out NvencEncodeSampleSlotLease sampleSlot))
            {
                _workSlots.TryReturn(workSlot);
                return _processState.IsAccepting
                    ? CaptureSubmitStatus.Backpressured
                    : CaptureSubmitStatus.NotAccepting;
            }

            if (!_processState.TryBeginAdmission())
            {
                _sampleSlots.TryReturn(sampleSlot);
                _workSlots.TryReturn(workSlot);
                return CaptureSubmitStatus.NotAccepting;
            }

            CaptureFrameWorkToken token = default;
            bool syncRented = false;
            try
            {
                // The sync credit rent is inside the admission gate so it is
                // serialized with the release coordinator's TryReturn, which
                // runs on the same gate. Running is guaranteed while the gate
                // is held, so a failed rent here is capacity backpressure.
                if (_syncSlots.TryRent(out NvencGpuConversionSyncLease syncSlot))
                {
                    syncRented = true;
                    token = new CaptureFrameWorkToken(
                        _backendOwner, workSlot.SlotIndex, workSlot.Generation, frame.TestRunId, frame.CaptureFrameId);

                    surface.TransferToBackend(_backendOwner, token);

                    NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                        _backendOwner, token, workSlot, sampleSlot, syncSlot, surface, _workSlots, _sampleSlots, _syncSlots);

                    if (!_submissionQueue.TryEnqueue(record))
                    {
                        throw new InvalidOperationException("Submission queue rejected an accepted record; internal invariant violated.");
                    }
                }
            }
            finally
            {
                _processState.EndAdmission();
            }

            if (!syncRented)
            {
                // Sync credit capacity exhausted while Running: release the work
                // and sample reservations in reverse order after the gate is free.
                _sampleSlots.TryReturn(sampleSlot);
                _workSlots.TryReturn(workSlot);
                return CaptureSubmitStatus.Backpressured;
            }

            workToken = token;
            return CaptureSubmitStatus.Accepted;
        }

        internal bool TryDequeue(out NvencSubmissionRecord record)
        {
            return _submissionQueue.TryDequeue(out record);
        }
    }
}
