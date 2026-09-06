using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Release boundary for one FailedBeforeSubmit Submit-to-Output record in
    /// the Phase 0.11 NVENC path. It returns only the exact Encode Sample Slot
    /// and issues the exact <see cref="NvencFailedBeforeSubmitReleaseResult"/>
    /// that a later Frame Completion publish requires; it returns no Work Slot,
    /// Submit-to-Output credit, or Frame Completion credit, performs no Frame
    /// Completion, and never touches the Owned Access Unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The release runs inside one non-waiting resource-resolution gate in a
    /// fixed order: verify the record is the FailedBeforeSubmit variant, verify
    /// the exact correlation against every pool, return the Encode Sample Slot
    /// exactly once, issue the release result, and confirm the result matches
    /// before reporting success. No retryable intermediate state exists between
    /// the sample return and the result issue: an unexpected failure after the
    /// return poisons, so re-running the same record cannot double-return the
    /// sample slot. The Submit-to-Output credit becomes inactive only at Frame
    /// Completion publish, and the Work Slot and Frame Completion credit become
    /// inactive only at Main Thread collect, so the existing completion order
    /// is preserved unchanged.
    /// </para>
    /// <para>
    /// This coordinator spawns no asynchronous work and owns no native
    /// resource; it holds only its five exact dependencies and is not an
    /// <see cref="IDisposable"/>. A busy gate, a poisoned process, or a wrong
    /// record variant returns false without changing anything; a foreign,
    /// stale, or returned lease poisons without guessing a return.
    /// </para>
    /// </remarks>
    internal sealed class NvencFailedBeforeSubmitReleaseCoordinator
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencSubmitToOutputCreditPool _submitToOutputCredits;
        private readonly NvencFrameCompletionCreditPool _frameCompletionCredits;

        internal NvencFailedBeforeSubmitReleaseCoordinator(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _workSlots = workSlots ?? throw new ArgumentNullException(nameof(workSlots));
            _sampleSlots = sampleSlots ?? throw new ArgumentNullException(nameof(sampleSlots));
            _submitToOutputCredits = submitToOutputCredits ?? throw new ArgumentNullException(nameof(submitToOutputCredits));
            _frameCompletionCredits = frameCompletionCredits ?? throw new ArgumentNullException(nameof(frameCompletionCredits));
        }

        /// <summary>
        /// Releases the record's exact Encode Sample Slot and issues the exact
        /// release result. Returns false without any change while the process
        /// is poisoned, the record is not the FailedBeforeSubmit variant, or
        /// the resource-resolution gate is busy; a foreign, stale, or returned
        /// lease, a failed sample return, or a mismatched result poisons and
        /// throws without guessing a return.
        /// </summary>
        internal bool TryRelease(
            in NvencSubmitToOutputRecord record,
            out NvencFailedBeforeSubmitReleaseResult result)
        {
            result = default;

            // Poisoned: change nothing.
            if (_processState.IsPoisoned)
            {
                return false;
            }

            // Wrong variant: reject without contacting any pool.
            if (record.Kind != NvencSubmitToOutputRecordKind.FailedBeforeSubmit)
            {
                return false;
            }

            // Acquire the short resource-resolution gate without waiting; a
            // busy gate changes nothing and the caller may retry later.
            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // Exact correlation and active state against every pool before
                // any side effect. Foreign, stale, or already-returned leases
                // have unknown ownership and poison; no completion is guessed.
                if (!record.IsValidFor(_workSlots, _sampleSlots, _submitToOutputCredits, _frameCompletionCredits))
                {
                    PoisonAndThrow("FailedBeforeSubmit release correlation is broken.");
                }

                // Return the exact Encode Sample Slot exactly once.
                if (!_sampleSlots.TryReturn(record.SampleSlot))
                {
                    PoisonAndThrow("Encode Sample Slot return failed during FailedBeforeSubmit release.");
                }

                // Issue the release result after the sample return, then confirm
                // it matches before reporting success. An unexpected failure
                // here poisons instead of leaving a retryable half-release.
                NvencFailedBeforeSubmitReleaseResult releaseResult =
                    NvencFailedBeforeSubmitReleaseResult.Create(record, _sampleSlots);

                if (!releaseResult.Matches(record, _sampleSlots))
                {
                    PoisonAndThrow("FailedBeforeSubmit release result does not match the record.");
                }

                result = releaseResult;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private void PoisonAndThrow(string message)
        {
            _processState.TryPoison();
            throw new InvalidOperationException(message);
        }
    }
}
