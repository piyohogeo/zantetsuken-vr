using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single-producer submission admission boundary for the Phase 0.11 NVENC
    /// path. It serializes <see cref="TryAccept"/> into the fixed SPSC
    /// Submission Queue after atomically reserving one Work Slot and one Sample
    /// Slot, transferring the caller's surface to the backend, and issuing the
    /// accepted work token. It owns none of its injected collaborators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acceptance follows a fixed order: the process state must be accepting,
    /// the queue must have capacity, one Work Slot and one Sample Slot must be
    /// rentable, the process state is re-checked, the work token is issued from
    /// the backend owner and the work slot generation, the surface is
    /// transferred, the record is built, and it is enqueued exactly once. The
    /// final atomic admission re-check is the Accepted linearization point; once
    /// it succeeds, the transfer and enqueue cannot fail. Capacity
    /// exhaustion of any kind is <c>Backpressured</c> and leaves the surface
    /// caller-owned with reservations released in reverse order. A stopped or
    /// poisoned process returns <c>NotAccepting</c> without transferring the
    /// surface.
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
        private readonly NvencFixedSpscQueue<NvencSubmissionRecord> _submissionQueue;
        private readonly Guid _backendOwner;

        internal NvencSubmissionAdmissionCoordinator(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
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

            if (!_processState.TryAdmit())
            {
                _sampleSlots.TryReturn(sampleSlot);
                _workSlots.TryReturn(workSlot);
                return CaptureSubmitStatus.NotAccepting;
            }

            CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                _backendOwner, workSlot.SlotIndex, workSlot.Generation, frame.TestRunId, frame.CaptureFrameId);

            surface.TransferToBackend(_backendOwner, token);

            NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                _backendOwner, token, workSlot, sampleSlot, surface, _workSlots, _sampleSlots);

            if (!_submissionQueue.TryEnqueue(record))
            {
                throw new InvalidOperationException("Submission queue rejected an accepted record; internal invariant violated.");
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
