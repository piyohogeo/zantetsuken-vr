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
    /// with no owned lease. An unknown state — a source exception, a failed
    /// sample return or transfer after a known-success copy, or a partial
    /// cleanup — poisons the process and propagates an exception, never
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
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencSubmitToOutputCreditPool _submitToOutputCredits;
        private readonly NvencFrameCompletionCreditPool _frameCompletionCredits;
        private readonly NvencOwnedAccessUnitBuffer _buffer;
        private readonly INvencOutputBitstreamSource _source;

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

        internal bool TryCollect(
            in NvencSubmitToOutputRecord record,
            out NvencSubmittedOutputCollectResult result)
        {
            result = default;

            // Poisoned: nothing progresses and the source is never contacted.
            if (_processState.IsPoisoned)
            {
                return false;
            }

            // Only the Submitted normal path reaches the source.
            if (record.Kind == NvencSubmitToOutputRecordKind.None)
            {
                return false;
            }

            if (record.Kind != NvencSubmitToOutputRecordKind.Submitted)
            {
                result = NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken);
                return true;
            }

            // Full correlation before any external contact.
            if (!record.IsValidFor(_workSlots, _sampleSlots, _submitToOutputCredits, _frameCompletionCredits))
            {
                result = NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken);
                return true;
            }

            // Reserve the fixed region. Poison, gate contention, or an exhausted
            // slot fail here before any external contact.
            if (!_buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease writeLease))
            {
                return false;
            }

            // The buffer authority synchronously calls the source with its fixed
            // storage. A source exception poisons and rethrows the same instance.
            bool copied;
            try
            {
                copied = _buffer.TryCopyCompletedOutput(writeLease, record.SampleSlot, _source);
            }
            catch
            {
                _processState.TryPoison();
                throw;
            }

            if (!copied)
            {
                return CompleteControlledFailure(record, writeLease, out result);
            }

            // Native ownership is safely resolved. Serialize the sample return
            // and the sink transfer as one short critical section.
            if (!_processState.TryBeginResourceResolution())
            {
                PoisonAndThrow("Submit-to-Output collector could not serialize the release step.");
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

            if (!_processState.TryBeginResourceResolution())
            {
                PoisonAndThrow("Submit-to-Output collector could not serialize the controlled-failure release.");
            }

            try
            {
                if (!_sampleSlots.TryReturn(record.SampleSlot))
                {
                    PoisonAndThrow("Encode Sample Slot return failed during controlled-failure release.");
                }

                if (!_buffer.CancelWrite(writeLease))
                {
                    PoisonAndThrow("Access Unit cancel failed during controlled-failure release.");
                }
            }
            finally
            {
                _processState.EndResourceResolution();
            }

            result = NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken);
            return true;
        }

        private void PoisonAndThrow(string message)
        {
            _processState.TryPoison();
            throw new InvalidOperationException(message);
        }
    }
}
