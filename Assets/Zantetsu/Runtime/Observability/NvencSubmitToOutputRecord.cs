using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Exclusive Submit-to-Output record carried by the fixed SPSC
    /// Submit-to-Output Queue. A readonly value type that forwards the accepted
    /// work token and the two reserved slot leases; it stores no raw Input
    /// Surface, Output Buffer, Completion Event handle, sequence, timestamp, or
    /// sort key. The capture frame ID is read through the work token, not
    /// duplicated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two factories <see cref="CreateSubmitted"/> and
    /// <see cref="CreateFailedBeforeSubmit"/> issue records, and there is no
    /// constructor that accepts an arbitrary kind and reason. A record is valid
    /// only when its work token and both slot leases are valid and the kind and
    /// reason agree: Submitted pairs with <c>None</c>, and FailedBeforeSubmit
    /// pairs with a defined non-None reason.
    /// </para>
    /// <para>
    /// <see cref="IsValidFor"/> additionally requires that each slot lease is
    /// currently active in its exact pool. Foreign, returned, or stale leases
    /// make it false. Poison decisions are left to the later coordinator and
    /// are not mixed into this record.
    /// </para>
    /// </remarks>
    internal readonly struct NvencSubmitToOutputRecord
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencCaptureWorkSlotLease _workSlot;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;
        private readonly NvencSubmitToOutputRecordKind _kind;
        private readonly NvencFailedBeforeSubmitReason _reason;

        private NvencSubmitToOutputRecord(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencSubmitToOutputRecordKind kind,
            NvencFailedBeforeSubmitReason reason)
        {
            _workToken = workToken;
            _workSlot = workSlot;
            _sampleSlot = sampleSlot;
            _kind = kind;
            _reason = reason;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencCaptureWorkSlotLease WorkSlot => _workSlot;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal NvencSubmitToOutputRecordKind Kind => _kind;

        internal NvencFailedBeforeSubmitReason Reason => _reason;

        internal bool IsValid
        {
            get
            {
                if (!_workToken.IsValid || !_workSlot.IsValid || !_sampleSlot.IsValid)
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
            NvencEncodeSampleSlotPool sampleSlots)
        {
            return IsValid &&
                workSlots != null &&
                sampleSlots != null &&
                workSlots.IsActive(_workSlot) &&
                sampleSlots.IsActive(_sampleSlot);
        }

        internal static NvencSubmitToOutputRecord CreateSubmitted(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot)
        {
            ValidateInputs(workToken, workSlot, sampleSlot);

            return new NvencSubmitToOutputRecord(
                workToken,
                workSlot,
                sampleSlot,
                NvencSubmitToOutputRecordKind.Submitted,
                NvencFailedBeforeSubmitReason.None);
        }

        internal static NvencSubmitToOutputRecord CreateFailedBeforeSubmit(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencFailedBeforeSubmitReason reason)
        {
            ValidateInputs(workToken, workSlot, sampleSlot);

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
                NvencSubmitToOutputRecordKind.FailedBeforeSubmit,
                reason);
        }

        private static void ValidateInputs(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot)
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
