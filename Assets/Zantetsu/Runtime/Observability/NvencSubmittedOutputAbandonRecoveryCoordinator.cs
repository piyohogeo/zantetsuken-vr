using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Run-abandoned recovery boundary for one Submitted Submit-to-Output
    /// record in the Phase 0.11 NVENC path. It safely recovers the already
    /// submitted output through the exact
    /// <see cref="NvencSubmittedOutputCollector"/> without appending it to any
    /// chunk, returns the Owned Access Unit to the exact buffer instead of
    /// handing it to the sink, and issues the exact
    /// <see cref="NvencRunAbandonedRecoveryResult"/> that a later Frame
    /// Completion publish requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This boundary never decides that a run becomes abandoned; the later
    /// Ordered Output Processor selects the abandoned branch and then hands one
    /// Submitted record here for resource recovery only. It holds no queue, no
    /// chunk appender, and no sink, and performs no chunk append, Frame
    /// Completion, Work Slot return, Submit-to-Output credit return, or Frame
    /// Completion credit return.
    /// </para>
    /// <para>
    /// The recovery order is fixed: reject a non-Submitted record before any
    /// dependency contact, hand the exact record to the collector exactly once,
    /// require the collector's work token to match, and then either follow the
    /// controlled-failure proof (sample slot already returned, exact recovery
    /// proof already minted by the collector cancel) or return the collector's
    /// owned Access Unit through the buffer. A successful owned return issues
    /// one recovery result and clears the single parked lease only after the
    /// result is built, so no window allows a re-issue; a busy gate keeps the
    /// single pending lease and retries only that return on the next call; a
    /// foreign, stale, or invalid lease or proof poisons without guessing a
    /// completion.
    /// </para>
    /// <para>
    /// This coordinator owns no thread and no native resource, holds exactly
    /// its four dependencies plus one single pending record and owned lease,
    /// and is not an <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencSubmittedOutputAbandonRecoveryCoordinator
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencSubmittedOutputCollector _collector;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencOwnedAccessUnitBuffer _buffer;

        private bool _pending;
        private NvencSubmitToOutputRecord _pendingRecord;
        private NvencOwnedAccessUnitLease _pendingOwnedLease;

        internal NvencSubmittedOutputAbandonRecoveryCoordinator(
            NvencCaptureProcessState processState,
            NvencSubmittedOutputCollector collector,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencOwnedAccessUnitBuffer buffer)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));
            _sampleSlots = sampleSlots ?? throw new ArgumentNullException(nameof(sampleSlots));
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        /// <summary>
        /// Recovers one Submitted record and issues the exact recovery result.
        /// Returns false without any change while the process is poisoned, the
        /// record is not Submitted, the collector is not yet terminal, or the
        /// owned Access Unit return is parked behind a busy gate; a mismatched
        /// collector token, a foreign, stale, or invalid lease or proof, or a
        /// broken collector shape poisons and throws without guessing.
        /// </summary>
        internal bool TryRecover(
            in NvencSubmitToOutputRecord record,
            out NvencRunAbandonedRecoveryResult result)
        {
            result = default;

            if (_pending)
            {
                return CompletePending(record, out result);
            }

            if (_processState.IsPoisoned)
            {
                return false;
            }

            // Reject a non-Submitted record before any dependency contact.
            if (record.Kind != NvencSubmitToOutputRecordKind.Submitted)
            {
                return false;
            }

            // Hand the exact record to the collector exactly once.
            if (!_collector.TryCollect(record, out NvencSubmittedOutputCollectResult collectorResult))
            {
                return false;
            }

            if (!collectorResult.WorkToken.IdenticalTo(record.WorkToken))
            {
                PoisonAndThrow("Abandon recovery collector work token does not match the record.");
            }

            if (collectorResult.IsSucceeded)
            {
                return CompleteOwnedReturn(record, collectorResult.OwnedLease, out result);
            }

            if (collectorResult.IsControlledFailure)
            {
                return CompleteControlledFailure(record, collectorResult.RecoveryProof, out result);
            }

            PoisonAndThrow("Abandon recovery collector result has no terminal shape.");
            return false;
        }

        private bool CompleteControlledFailure(
            in NvencSubmitToOutputRecord record,
            in NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof recoveryProof,
            out NvencRunAbandonedRecoveryResult result)
        {
            result = default;

            // The collector already returned the exact sample slot.
            if (_sampleSlots.IsActive(record.SampleSlot))
            {
                PoisonAndThrow("Abandon recovery sample slot is still active after controlled failure.");
            }

            result = BuildAndVerifyRecoveryResult(record, recoveryProof);
            return true;
        }

        private bool CompleteOwnedReturn(
            in NvencSubmitToOutputRecord record,
            in NvencOwnedAccessUnitLease ownedLease,
            out NvencRunAbandonedRecoveryResult result)
        {
            result = default;

            // Park the single owned lease before the return attempt so a busy
            // gate leaves it retained for the resume path.
            Park(record, ownedLease);

            NvencOwnedAccessUnitBoundaryStatus status = _buffer.TryReturnOwnedAccessUnit(
                ownedLease, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof);

            switch (status)
            {
                case NvencOwnedAccessUnitBoundaryStatus.Ready:
                    result = BuildAndVerifyRecoveryResult(record, proof);
                    ClearPending();
                    return true;

                case NvencOwnedAccessUnitBoundaryStatus.Busy:
                    return false;

                default:
                    PoisonAndThrow("Owned Access Unit return failed during abandon recovery.");
                    return false;
            }
        }

        private bool CompletePending(
            in NvencSubmitToOutputRecord record,
            out NvencRunAbandonedRecoveryResult result)
        {
            result = default;

            // After poison the parked lease is never guessed back.
            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (!MatchesPending(record))
            {
                PoisonAndThrow("Abandon recovery pending record mismatch.");
            }

            NvencSubmitToOutputRecord parkedRecord = _pendingRecord;
            NvencOwnedAccessUnitLease ownedLease = _pendingOwnedLease;

            NvencOwnedAccessUnitBoundaryStatus status = _buffer.TryReturnOwnedAccessUnit(
                ownedLease, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof);

            switch (status)
            {
                case NvencOwnedAccessUnitBoundaryStatus.Ready:
                    result = BuildAndVerifyRecoveryResult(parkedRecord, proof);
                    ClearPending();
                    return true;

                case NvencOwnedAccessUnitBoundaryStatus.Busy:
                    return false;

                default:
                    PoisonAndThrow("Owned Access Unit return failed during abandon recovery resume.");
                    return false;
            }
        }

        private NvencRunAbandonedRecoveryResult BuildAndVerifyRecoveryResult(
            in NvencSubmitToOutputRecord record,
            in NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof)
        {
            if (!_buffer.VerifyRecoveryProof(proof, record.WorkToken))
            {
                PoisonAndThrow("Abandon recovery proof is invalid.");
            }

            NvencRunAbandonedRecoveryResult built =
                NvencRunAbandonedRecoveryResult.Create(record, _sampleSlots, _buffer, proof);

            if (!built.Matches(record, _sampleSlots, _buffer))
            {
                PoisonAndThrow("Abandon recovery result does not match the record.");
            }

            return built;
        }

        private void Park(
            in NvencSubmitToOutputRecord record,
            in NvencOwnedAccessUnitLease ownedLease)
        {
            _pending = true;
            _pendingRecord = record;
            _pendingOwnedLease = ownedLease;
        }

        private void ClearPending()
        {
            _pending = false;
            _pendingRecord = default;
            _pendingOwnedLease = default;
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
