using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Exclusive Submit-to-Output record carried by the fixed SPSC
    /// Submit-to-Output Queue. A readonly value type that forwards the accepted
    /// work token, the two reserved slot leases, and the two reserved capacity
    /// credits (Submit-to-Output and Frame Completion); it stores no raw Input
    /// Surface, Output Buffer, Completion Event handle, sequence, timestamp, or
    /// sort key. The capture frame ID is read through the work token, not
    /// duplicated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two factories <see cref="CreateSubmitted"/> and
    /// <see cref="CreateFailedBeforeSubmit"/> issue records, and there is no
    /// constructor that accepts an arbitrary kind and reason. A record is valid
    /// only when its work token, both slot leases, and both capacity credits
    /// are valid, the work token correlates to the work slot lease (same slot
    /// index and generation), and the kind and reason agree: Submitted pairs
    /// with <c>None</c>, and FailedBeforeSubmit pairs with a defined non-None
    /// reason.
    /// </para>
    /// <para>
    /// <see cref="IsValidFor"/> additionally requires that each slot lease and
    /// capacity credit is currently active in its exact pool. Foreign,
    /// returned, or stale leases make it false. Poison decisions are left to
    /// the later coordinator and are not mixed into this record.
    /// </para>
    /// </remarks>
    internal readonly struct NvencSubmitToOutputRecord
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencCaptureWorkSlotLease _workSlot;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;
        private readonly NvencSubmitToOutputCreditLease _submitToOutputCredit;
        private readonly NvencFrameCompletionCreditLease _frameCompletionCredit;
        private readonly NvencSubmitToOutputRecordKind _kind;
        private readonly NvencFailedBeforeSubmitReason _reason;

        private NvencSubmitToOutputRecord(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit,
            NvencSubmitToOutputRecordKind kind,
            NvencFailedBeforeSubmitReason reason)
        {
            _workToken = workToken;
            _workSlot = workSlot;
            _sampleSlot = sampleSlot;
            _submitToOutputCredit = submitToOutputCredit;
            _frameCompletionCredit = frameCompletionCredit;
            _kind = kind;
            _reason = reason;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencCaptureWorkSlotLease WorkSlot => _workSlot;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal NvencSubmitToOutputCreditLease SubmitToOutputCredit => _submitToOutputCredit;

        internal NvencFrameCompletionCreditLease FrameCompletionCredit => _frameCompletionCredit;

        internal NvencSubmitToOutputRecordKind Kind => _kind;

        internal NvencFailedBeforeSubmitReason Reason => _reason;

        internal bool IsValid
        {
            get
            {
                if (!_workToken.IsValid || !_workSlot.IsValid || !_sampleSlot.IsValid ||
                    !_submitToOutputCredit.IsValid || !_frameCompletionCredit.IsValid ||
                    !CorrelatesToWorkSlot(_workToken, _workSlot))
                {
                    return false;
                }

                switch (_kind)
                {
                    case NvencSubmitToOutputRecordKind.Submitted:
                        return _reason == NvencFailedBeforeSubmitReason.None;
                    case NvencSubmitToOutputRecordKind.FailedBeforeSubmit:
                        return IsDefinedNonNoneReason(_reason);
                    default:
                        return false;
                }
            }
        }

        internal bool IsValidFor(
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            return IsValid &&
                workSlots != null &&
                sampleSlots != null &&
                submitToOutputCredits != null &&
                frameCompletionCredits != null &&
                workSlots.IsActive(_workSlot) &&
                sampleSlots.IsActive(_sampleSlot) &&
                submitToOutputCredits.IsActive(_submitToOutputCredit) &&
                frameCompletionCredits.IsActive(_frameCompletionCredit);
        }

        internal static NvencSubmitToOutputRecord CreateSubmitted(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit)
        {
            ValidateInputs(workToken, workSlot, sampleSlot, submitToOutputCredit, frameCompletionCredit);

            return new NvencSubmitToOutputRecord(
                workToken,
                workSlot,
                sampleSlot,
                submitToOutputCredit,
                frameCompletionCredit,
                NvencSubmitToOutputRecordKind.Submitted,
                NvencFailedBeforeSubmitReason.None);
        }

        internal static NvencSubmitToOutputRecord CreateFailedBeforeSubmit(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit,
            NvencFailedBeforeSubmitReason reason)
        {
            ValidateInputs(workToken, workSlot, sampleSlot, submitToOutputCredit, frameCompletionCredit);

            if (reason == NvencFailedBeforeSubmitReason.None)
            {
                throw new ArgumentException("A failure record requires a non-None reason.", nameof(reason));
            }

            if (!IsDefinedNonNoneReason(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Reason must be a defined FailedBeforeSubmit reason.");
            }

            return new NvencSubmitToOutputRecord(
                workToken,
                workSlot,
                sampleSlot,
                submitToOutputCredit,
                frameCompletionCredit,
                NvencSubmitToOutputRecordKind.FailedBeforeSubmit,
                reason);
        }

        private static void ValidateInputs(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit)
        {
            if (!workToken.IsValid)
            {
                throw new ArgumentException("Work token must be valid.", nameof(workToken));
            }

            if (!workSlot.IsValid)
            {
                throw new ArgumentException("Work slot lease must be valid.", nameof(workSlot));
            }

            if (!sampleSlot.IsValid)
            {
                throw new ArgumentException("Sample slot lease must be valid.", nameof(sampleSlot));
            }

            if (!submitToOutputCredit.IsValid)
            {
                throw new ArgumentException("Submit-to-Output credit must be valid.", nameof(submitToOutputCredit));
            }

            if (!frameCompletionCredit.IsValid)
            {
                throw new ArgumentException("Frame Completion credit must be valid.", nameof(frameCompletionCredit));
            }

            if (!CorrelatesToWorkSlot(workToken, workSlot))
            {
                throw new ArgumentException("Work token must correlate to the work slot lease.", nameof(workToken));
            }
        }

        private static bool CorrelatesToWorkSlot(CaptureFrameWorkToken workToken, NvencCaptureWorkSlotLease workSlot)
        {
            return workToken.SlotIndex == workSlot.SlotIndex &&
                workToken.Generation == workSlot.Generation;
        }

        private static bool IsDefinedNonNoneReason(NvencFailedBeforeSubmitReason reason)
        {
            switch (reason)
            {
                case NvencFailedBeforeSubmitReason.GpuConversionFailed:
                case NvencFailedBeforeSubmitReason.NvencSubmitFailed:
                case NvencFailedBeforeSubmitReason.CancelledBeforeSubmit:
                case NvencFailedBeforeSubmitReason.CancelledAfterRunAbandoned:
                case NvencFailedBeforeSubmitReason.DrainedBeforeSubmit:
                    return true;
                default:
                    return false;
            }
        }
    }
}
