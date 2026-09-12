using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single-producer submission admission boundary for the Phase 0.11 NVENC
    /// path. It serializes <see cref="TryAccept"/> into the fixed SPSC
    /// Submission Queue after atomically reserving one Work Slot, one Sample
    /// Slot, one GPU Conversion Sync credit, one Submit-to-Output capacity
    /// credit, and one Frame Completion capacity credit, transferring the
    /// caller's surface to the backend, and issuing the accepted work token.
    /// It owns none of its injected collaborators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acceptance follows a fixed order: the process state must be accepting,
    /// the queue must have capacity, and only then the short admission guard is
    /// acquired non-waiting. Inside that guard every downstream reservation is
    /// taken in a fixed order — Work Slot, Sample Slot, GPU Conversion Sync,
    /// Submit-to-Output credit, Frame Completion credit — so that Accepted work
    /// can never be admitted with more downstream demand than the pipeline can
    /// carry. On the first failed rent the already-reserved resources are
    /// released in strict reverse order while the admission gate is still
    /// held, and the call returns <c>Backpressured</c> with the surface still
    /// caller-owned and a default work token; a drain or poison transition
    /// therefore linearizes only after the rollback has completed. The work
    /// token is issued from the backend owner and the work slot generation, the
    /// surface is transferred, the record is built, and it is enqueued exactly
    /// once, so the successful enqueue is the Accepted linearization point
    /// ordered before any later drain or poison. Capacity exhaustion of any
    /// kind is <c>Backpressured</c>; a stopped, drained, or poisoned process
    /// (or a busy guard) returns <c>NotAccepting</c> without transferring the
    /// surface.
    /// </para>
    /// <para>
    /// The coordinator is bound to the exact <see cref="NvencRunChunkContext"/>
    /// for the same process state, so the Run's accepted Capture Frame Id
    /// sequence is the single source of truth. Before any reservation or
    /// surface transfer the frame's test run id must match the context and the
    /// context's side-effect-free admission predicate must accept the frame id;
    /// a duplicate, backward, over-capacity, frozen, or terminal frame is
    /// rejected as <c>NotAccepting</c> with no state change. After the
    /// successful enqueue, the frame id is registered into the context exactly
    /// once inside the same admission gate, before <c>EndAdmission</c>, so the
    /// Submit Worker — which must acquire the same gate before any submit side
    /// effect — can never begin an NVENC submit before the accepted registration
    /// has completed.
    /// </para>
    /// <para>
    /// Under the strict single-producer contract a post-transfer enqueue failure
    /// is an internal invariant violation and is reported as an exception, not
    /// converted to backpressure. Every already-reserved resource is released
    /// in reverse order before the exception propagates; if that cleanup itself
    /// throws, the cleanup failure is aggregated with the original exception.
    /// There is no rollback API that returns the surface to the caller and no
    /// dynamic queue growth. <see cref="TryDequeue"/> returns the queue's FIFO
    /// unchanged with no sorting, peeking, or skip.
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
        private readonly NvencSubmitToOutputCreditPool _submitToOutputCredits;
        private readonly NvencFrameCompletionCreditPool _frameCompletionCredits;
        private readonly NvencFixedSpscQueue<NvencSubmissionRecord> _submissionQueue;
        private readonly INvencGpuConversionCommandIssuer _conversionCommandIssuer;
        private readonly NvencRunChunkContext _context;
        private readonly Guid _backendOwner;

        internal NvencSubmissionAdmissionCoordinator(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits,
            NvencFixedSpscQueue<NvencSubmissionRecord> submissionQueue,
            INvencGpuConversionCommandIssuer conversionCommandIssuer,
            NvencRunChunkContext context,
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

            if (submitToOutputCredits == null)
            {
                throw new ArgumentNullException(nameof(submitToOutputCredits));
            }

            if (frameCompletionCredits == null)
            {
                throw new ArgumentNullException(nameof(frameCompletionCredits));
            }

            if (submissionQueue == null)
            {
                throw new ArgumentNullException(nameof(submissionQueue));
            }

            if (conversionCommandIssuer == null)
            {
                throw new ArgumentNullException(nameof(conversionCommandIssuer));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!ReferenceEquals(context.ProcessState, processState))
            {
                throw new ArgumentException(
                    "Run chunk context must be bound to the exact process state.", nameof(context));
            }

            if (backendOwner == Guid.Empty)
            {
                throw new ArgumentException("Backend owner must not be empty.", nameof(backendOwner));
            }

            _processState = processState;
            _workSlots = workSlots;
            _sampleSlots = sampleSlots;
            _syncSlots = syncSlots;
            _submitToOutputCredits = submitToOutputCredits;
            _frameCompletionCredits = frameCompletionCredits;
            _submissionQueue = submissionQueue;
            _conversionCommandIssuer = conversionCommandIssuer;
            _context = context;
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

            if (frame.TestRunId != _context.TestRunId)
            {
                throw new ArgumentException(
                    "Frame TestRunId does not match the Run chunk context.", nameof(frame));
            }

            workToken = default;

            // Side-effect-free context eligibility check before any reservation
            // or surface transfer: a duplicate, backward, over-capacity, frozen,
            // or terminal frame is rejected here with no state change.
            if (!_context.CanRecordAcceptedFrame(frame.CaptureFrameId))
            {
                return CaptureSubmitStatus.NotAccepting;
            }

            if (!_processState.IsAccepting)
            {
                return CaptureSubmitStatus.NotAccepting;
            }

            if (!_submissionQueue.CanEnqueue)
            {
                return CaptureSubmitStatus.Backpressured;
            }

            if (!_processState.TryBeginAdmission())
            {
                return CaptureSubmitStatus.NotAccepting;
            }

            CaptureFrameWorkToken token = default;
            NvencCaptureWorkSlotLease workSlot = default;
            NvencEncodeSampleSlotLease sampleSlot = default;
            NvencGpuConversionSyncLease syncSlot = default;
            NvencSubmitToOutputCreditLease submitToOutputCredit = default;
            NvencFrameCompletionCreditLease frameCompletionCredit = default;
            CaptureSubmitStatus status;

            try
            {
                // Reserve every downstream resource inside the admission gate so
                // Accepted work can never outrun the pipeline's capacity.
                // Running is guaranteed while the gate is held, so any failed
                // rent is capacity backpressure, and the short-circuit leaves
                // the later leases default (not rented).
                if (_workSlots.TryRent(out workSlot) &&
                    _sampleSlots.TryRent(out sampleSlot) &&
                    _syncSlots.TryRent(out syncSlot) &&
                    _submitToOutputCredits.TryRent(out submitToOutputCredit) &&
                    _frameCompletionCredits.TryRent(out frameCompletionCredit))
                {
                    token = new CaptureFrameWorkToken(
                        _backendOwner, workSlot.SlotIndex, workSlot.Generation, frame.TestRunId, frame.CaptureFrameId);

                    surface.TransferToBackend(_backendOwner, token);

                    NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                        _backendOwner, token, workSlot, sampleSlot, syncSlot,
                        submitToOutputCredit, frameCompletionCredit, surface,
                        _workSlots, _sampleSlots, _syncSlots, _submitToOutputCredits, _frameCompletionCredits);

                    // Issue the conversion exactly once, after the surface is
                    // the backend's and before the record can be dequeued. A
                    // render callback that runs the instant this returns finds
                    // a surface that is already transferred, and a worker can
                    // never take a record whose conversion was not issued.
                    bool issued;
                    try
                    {
                        issued = _conversionCommandIssuer.TryIssue(record);
                    }
                    catch (Exception)
                    {
                        // Whether a command exists - and so whether the GPU is
                        // reading the source - cannot be established. Nothing
                        // is enqueued, recorded, returned, or fabricated, and
                        // the same failure propagates.
                        _processState.TryPoison();
                        throw;
                    }

                    if (!issued)
                    {
                        // Known: no command exists and none will, so the source
                        // is not being read. Everything goes back in reverse,
                        // the queue and the context are untouched, and the
                        // process is not poisoned - but this is an invariant
                        // violation rather than an ordinary submit status.
                        RollbackAfterTransferAndThrow(
                            new InvalidOperationException(
                                "GPU conversion command issuer refused an accepted record; internal invariant violated."),
                            workSlot, sampleSlot, syncSlot,
                            submitToOutputCredit, frameCompletionCredit, surface, token);
                    }

                    try
                    {
                        if (!_submissionQueue.TryEnqueue(record))
                        {
                            throw new InvalidOperationException("Submission queue rejected an accepted record; internal invariant violated.");
                        }
                    }
                    catch (Exception)
                    {
                        // The conversion is already issued, so the render
                        // callback may be reading the source at this moment.
                        // Nothing is rolled back and nothing is fabricated:
                        // the surface and every reservation stay held, the
                        // process is poisoned, and the invariant violation
                        // propagates.
                        _processState.TryPoison();
                        throw;
                    }

                    // Register the accepted frame id exactly once, inside the
                    // same admission gate and only after the successful
                    // enqueue. The pre-admission predicate already verified
                    // this would succeed; a false here is an internal
                    // invariant violation and is not rolled back, dequeued, or
                    // surface-returned on guess — only the process is poisoned.
                    if (!_context.TryRecordAcceptedFrame(frame.CaptureFrameId))
                    {
                        _processState.TryPoison();
                        throw new InvalidOperationException(
                            "Run chunk context rejected the accepted frame after enqueue; internal invariant violated.");
                    }

                    status = CaptureSubmitStatus.Accepted;
                }
                else
                {
                    // Capacity exhausted while Running: release the reservations
                    // taken so far in strict reverse order while still holding
                    // the admission gate, so a drain or poison transition
                    // linearizes only after the rollback has completed. Default
                    // (unrented) leases are rejected by TryReturn and harmless.
                    RollbackReservations(workSlot, sampleSlot, syncSlot, submitToOutputCredit, frameCompletionCredit);
                    status = CaptureSubmitStatus.Backpressured;
                }
            }
            finally
            {
                _processState.EndAdmission();
            }

            workToken = token;
            return status;
        }

        internal bool TryDequeue(out NvencSubmissionRecord record)
        {
            return _submissionQueue.TryDequeue(out record);
        }

        private void RollbackReservations(
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencGpuConversionSyncLease syncSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit)
        {
            _frameCompletionCredits.TryReturn(frameCompletionCredit);
            _submitToOutputCredits.TryReturn(submitToOutputCredit);
            _syncSlots.TryReturn(syncSlot);
            _sampleSlots.TryReturn(sampleSlot);
            _workSlots.TryReturn(workSlot);
        }

        private void RollbackAfterTransferAndThrow(
            Exception original,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencGpuConversionSyncLease syncSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit,
            CaptureSurfaceLease surface,
            in CaptureFrameWorkToken token)
        {
            Exception cleanupFailure = null;
            try
            {
                RollbackReservations(workSlot, sampleSlot, syncSlot, submitToOutputCredit, frameCompletionCredit);
                surface.ReleaseFromBackend(_backendOwner, token);
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            if (cleanupFailure == null)
            {
                ExceptionDispatchInfo.Capture(original).Throw();
            }

            throw new AggregateException(original, cleanupFailure);
        }
    }
}
