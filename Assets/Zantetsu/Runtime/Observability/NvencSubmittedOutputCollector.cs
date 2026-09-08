using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the deferred Access Unit content access to the single normal
    /// Submitted path of Phase 0.11. For one Submitted Submit-to-Output record
    /// it verifies the full correlation, reserves the fixed Access Unit region,
    /// retrieves the bitstream through the injected output source, returns the
    /// Encode Sample Slot, and transfers the recorded content to SinkOwned,
    /// issuing an exact owned Access Unit lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The success order is fixed: full record correlation, region reservation,
    /// source copy, then — after the native ownership is safely resolved — a
    /// short resource-resolution gate that returns the exact Encode Sample Slot
    /// and transfers the recorded content to SinkOwned before issuing the
    /// success result. The sample slot is returned after the copy and before
    /// the sink lease.
    /// </para>
    /// <para>
    /// A controllable source failure returns the sample slot exactly once,
    /// cancels the region back to Free, and issues a ControlledFailure result
    /// with no owned lease. A transient resource-resolution gate contention
    /// (before the copy, on the copy commit, or after the source has returned)
    /// parks the single pending record, write lease, and resume stage, to be
    /// completed on the next attempt without re-contacting the source. The
    /// parked record is matched exactly against the next incoming record, and
    /// a mismatch poisons as an order/ownership break. An unknown state — a
    /// source exception,
    /// a failed sample return or transfer after a known-success copy, or a
    /// partial cleanup — poisons the process and propagates an exception, never
    /// guessing a release of the sample slot or the region.
    /// </para>
    /// <para>
    /// Only <see cref="NvencSubmitToOutputRecordKind.Submitted"/> records reach
    /// the source; FailedBeforeSubmit, None, and undefined kinds are rejected
    /// before any external contact. This type owns no thread, queue, or native
    /// resource and performs no run-time managed allocation.
    /// </para>
    /// </remarks>
    internal sealed class NvencSubmittedOutputCollector
    {
        private enum NvencCollectorPendingStage
        {
            CopyNotStarted,
            CopyPending,
            Success,
            ControlledFailure,
        }

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencSubmitToOutputCreditPool _submitToOutputCredits;
        private readonly NvencFrameCompletionCreditPool _frameCompletionCredits;
        private readonly NvencOwnedAccessUnitBuffer _buffer;
        private readonly INvencOutputBitstreamSource _source;

        private bool _pending;
        private NvencSubmitToOutputRecord _pendingRecord;
        private NvencAccessUnitWriteLease _pendingWriteLease;
        private NvencCollectorPendingStage _pendingStage;
        private NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyProof _pendingProof;

        internal NvencSubmittedOutputCollector(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits,
            NvencOwnedAccessUnitBuffer buffer,
            INvencOutputBitstreamSource source)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _workSlots = workSlots ?? throw new ArgumentNullException(nameof(workSlots));
            _sampleSlots = sampleSlots ?? throw new ArgumentNullException(nameof(sampleSlots));
            _submitToOutputCredits = submitToOutputCredits ?? throw new ArgumentNullException(nameof(submitToOutputCredits));
            _frameCompletionCredits = frameCompletionCredits ?? throw new ArgumentNullException(nameof(frameCompletionCredits));
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>
        /// O(1) resource correlation predicate: true only when this collector is
        /// bound to the exact Work, Sample, Submit-to-Output credit, Frame
        /// Completion credit pools and the exact Owned Access Unit buffer,
        /// without exposing them.
        /// </summary>
        internal bool IsCorrelatedWithResources(
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits,
            NvencOwnedAccessUnitBuffer buffer)
        {
            return ReferenceEquals(_workSlots, workSlots)
                && ReferenceEquals(_sampleSlots, sampleSlots)
                && ReferenceEquals(_submitToOutputCredits, submitToOutputCredits)
                && ReferenceEquals(_frameCompletionCredits, frameCompletionCredits)
                && ReferenceEquals(_buffer, buffer);
        }

        internal bool TryCollect(
            in NvencSubmitToOutputRecord record,
            out NvencSubmittedOutputCollectResult result)
        {
            result = default;

            // Complete a parked step before accepting new work. The parked
            // record is matched exactly against the argument first.
            if (_pending)
            {
                return CompletePending(record, out result);
            }

            // Poisoned: nothing progresses and the source is never contacted.
            if (_processState.IsPoisoned)
            {
                return false;
            }

            // Reject non-Submitted records with a default result and no resource
            // touch; the caller routes them to their dedicated release path.
            if (record.Kind != NvencSubmitToOutputRecordKind.Submitted)
            {
                return false;
            }

            // A Submitted record whose current correlation is broken is an
            // unknown ownership state: poison rather than fabricating a
            // controlled failure that would leak its held resources.
            if (!record.IsValidFor(_workSlots, _sampleSlots, _submitToOutputCredits, _frameCompletionCredits))
            {
                PoisonAndThrow("Submitted record correlation is broken.");
            }

            // Reserve the fixed region. Poison, gate contention, or an exhausted
            // slot fail here before any external contact.
            if (!_buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease writeLease))
            {
                return false;
            }

            return CompleteCopy(record, writeLease, out result);
        }

        /// <summary>
        /// Runs or resumes the source copy for an already-reserved region and
        /// dispatches the buffer's outcome. A source exception poisons and
        /// rethrows the same instance.
        /// </summary>
        private bool CompleteCopy(
            in NvencSubmitToOutputRecord record,
            in NvencAccessUnitWriteLease writeLease,
            out NvencSubmittedOutputCollectResult result)
        {
            result = default;

            NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus status;
            NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyProof proof;
            try
            {
                status = _buffer.TryCopyCompletedOutput(writeLease, record.SampleSlot, _source, out proof);
            }
            catch
            {
                _processState.TryPoison();
                throw;
            }

            switch (status)
            {
                case NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus.Committed:
                    return CompleteSuccess(record, writeLease, out result);

                case NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus.Pending:
                    Park(record, writeLease, NvencCollectorPendingStage.CopyPending, proof);
                    return false;

                case NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus.Rejected:
                    if (_processState.IsPoisoned)
                    {
                        PoisonAndThrow("Submit-to-Output collector was poisoned during the copy.");
                    }

                    return CompleteControlledFailure(record, writeLease, out result);

                default:
                    if (_processState.IsPoisoned)
                    {
                        PoisonAndThrow("Submit-to-Output collector was poisoned before the copy.");
                    }

                    Park(record, writeLease, NvencCollectorPendingStage.CopyNotStarted, default);
                    return false;
            }
        }

        private bool CompleteSuccess(
            in NvencSubmitToOutputRecord record,
            in NvencAccessUnitWriteLease writeLease,
            out NvencSubmittedOutputCollectResult result)
        {
            result = default;

            if (_processState.IsPoisoned)
            {
                PoisonAndThrow("Submit-to-Output collector was poisoned before the sink transfer.");
            }

            // A busy gate here is ordinary contention, not unknown ownership:
            // park and retry later without re-contacting the source.
            if (!_processState.TryBeginResourceResolution())
            {
                Park(record, writeLease, NvencCollectorPendingStage.Success, default);
                return false;
            }

            try
            {
                // Return the exact Encode Sample Slot before issuing the sink lease.
                if (!_sampleSlots.TryReturn(record.SampleSlot))
                {
                    PoisonAndThrow("Encode Sample Slot return failed after a known-success copy.");
                }

                // Transfer the recorded content to SinkOwned (no caller length).
                if (!_buffer.TryTransferToSink(writeLease, out NvencOwnedAccessUnitLease ownedLease))
                {
                    PoisonAndThrow("Access Unit sink transfer failed after a known-success copy.");
                }

                result = NvencSubmittedOutputCollectResult.Succeeded(record.WorkToken, ownedLease);
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private bool CompleteControlledFailure(
            in NvencSubmitToOutputRecord record,
            in NvencAccessUnitWriteLease writeLease,
            out NvencSubmittedOutputCollectResult result)
        {
            result = default;

            if (_processState.IsPoisoned)
            {
                PoisonAndThrow("Submit-to-Output collector was poisoned before the controlled release.");
            }

            if (!_processState.TryBeginResourceResolution())
            {
                Park(record, writeLease, NvencCollectorPendingStage.ControlledFailure, default);
                return false;
            }

            NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof recoveryProof;
            try
            {
                if (!_sampleSlots.TryReturn(record.SampleSlot))
                {
                    PoisonAndThrow("Encode Sample Slot return failed during controlled-failure release.");
                }

                if (!_buffer.TryCancelCollectorReservation(writeLease, out recoveryProof))
                {
                    PoisonAndThrow("Access Unit cancel failed during controlled-failure release.");
                }
            }
            finally
            {
                _processState.EndResourceResolution();
            }

            result = NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken, recoveryProof);
            return true;
        }

        private bool CompletePending(
            in NvencSubmitToOutputRecord record,
            out NvencSubmittedOutputCollectResult result)
        {
            result = default;

            // The parked record must match the incoming record exactly; any
            // mismatch means the FIFO order or ownership is broken.
            if (!MatchesPending(record))
            {
                PoisonAndThrow("Submit-to-Output collector pending record mismatch.");
            }

            NvencSubmitToOutputRecord parkedRecord = _pendingRecord;

            // Re-verify the parked record's leases and credits are still active
            // before any external contact or sample return; a lease returned or
            // reused while parked is an ownership break, not a controlled path.
            if (!parkedRecord.IsValidFor(_workSlots, _sampleSlots, _submitToOutputCredits, _frameCompletionCredits))
            {
                PoisonAndThrow("Submit-to-Output collector pending record correlation is broken.");
            }

            NvencAccessUnitWriteLease writeLease = _pendingWriteLease;
            NvencCollectorPendingStage stage = _pendingStage;

            bool terminal;
            switch (stage)
            {
                case NvencCollectorPendingStage.CopyNotStarted:
                    terminal = CompleteCopy(parkedRecord, writeLease, out result);
                    break;

                case NvencCollectorPendingStage.CopyPending:
                    if (!_buffer.TryCommitCopiedContent(writeLease, _pendingProof))
                    {
                        if (_processState.IsPoisoned)
                        {
                            PoisonAndThrow("Submit-to-Output collector was poisoned before the pending copy commit.");
                        }

                        return false;
                    }

                    terminal = CompleteSuccess(parkedRecord, writeLease, out result);
                    break;

                case NvencCollectorPendingStage.Success:
                    terminal = CompleteSuccess(parkedRecord, writeLease, out result);
                    break;

                default:
                    terminal = CompleteControlledFailure(parkedRecord, writeLease, out result);
                    break;
            }

            if (terminal)
            {
                ClearPending();
            }

            return terminal;
        }

        private void Park(
            in NvencSubmitToOutputRecord record,
            in NvencAccessUnitWriteLease writeLease,
            NvencCollectorPendingStage stage,
            in NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyProof proof)
        {
            _pending = true;
            _pendingRecord = record;
            _pendingWriteLease = writeLease;
            _pendingStage = stage;
            _pendingProof = proof;
        }

        private void ClearPending()
        {
            _pending = false;
            _pendingRecord = default;
            _pendingWriteLease = default;
            _pendingStage = NvencCollectorPendingStage.CopyNotStarted;
            _pendingProof = default;
        }

        private bool MatchesPending(in NvencSubmitToOutputRecord record)
        {
            NvencSubmitToOutputRecord parked = _pendingRecord;

            return parked.WorkToken.IdenticalTo(record.WorkToken) &&
                WorkSlotEquals(parked.WorkSlot, record.WorkSlot) &&
                SampleSlotEquals(parked.SampleSlot, record.SampleSlot) &&
                SubmitCreditEquals(parked.SubmitToOutputCredit, record.SubmitToOutputCredit) &&
                FrameCreditEquals(parked.FrameCompletionCredit, record.FrameCompletionCredit) &&
                parked.Kind == record.Kind &&
                parked.Reason == record.Reason;
        }

        private static bool WorkSlotEquals(in NvencCaptureWorkSlotLease a, in NvencCaptureWorkSlotLease b)
        {
            return a.OwnerToken == b.OwnerToken && a.SlotIndex == b.SlotIndex && a.Generation == b.Generation;
        }

        private static bool SampleSlotEquals(in NvencEncodeSampleSlotLease a, in NvencEncodeSampleSlotLease b)
        {
            return a.OwnerToken == b.OwnerToken && a.SlotIndex == b.SlotIndex && a.Generation == b.Generation;
        }

        private static bool SubmitCreditEquals(in NvencSubmitToOutputCreditLease a, in NvencSubmitToOutputCreditLease b)
        {
            return a.OwnerToken == b.OwnerToken && a.SlotIndex == b.SlotIndex && a.Generation == b.Generation;
        }

        private static bool FrameCreditEquals(in NvencFrameCompletionCreditLease a, in NvencFrameCompletionCreditLease b)
        {
            return a.OwnerToken == b.OwnerToken && a.SlotIndex == b.SlotIndex && a.Generation == b.Generation;
        }

        private void PoisonAndThrow(string message)
        {
            _processState.TryPoison();
            throw new InvalidOperationException(message);
        }
    }
}
