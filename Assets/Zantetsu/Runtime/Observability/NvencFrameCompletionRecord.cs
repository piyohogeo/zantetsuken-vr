using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// NVENC-specific Frame Completion record carried by the fixed SPSC
    /// Completion Queue. A readonly value type that forwards the exact work
    /// token, the exact Work Slot lease, and the exact Frame Completion credit
    /// lease, together with a terminal status and a fixed NVENC
    /// failure/cancellation reason. It carries no produced-output count: the
    /// NVENC path always produces zero outputs, and no field can express a
    /// non-zero count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the three factories <see cref="CreateSucceeded"/>,
    /// <see cref="CreateFailed"/>, and <see cref="CreateCancelled"/> issue
    /// records, and there is no constructor that accepts an arbitrary status
    /// and reason. A record is valid only when its work token, work slot lease,
    /// and frame completion credit are valid, the work token correlates to the
    /// work slot lease (same slot index and generation), and the status and
    /// reason agree: Succeeded pairs with <c>None</c>, Failed pairs with a
    /// defined failure reason, and Cancelled pairs with a defined cancellation
    /// reason. <c>None</c> and undefined statuses are always invalid.
    /// </para>
    /// <para>
    /// The Submit-to-Output credit is not carried here: it is returned by the
    /// publish boundary before this record is enqueued. The Work Slot and Frame
    /// Completion credit stay active until the Main Thread collects this record
    /// and returns both.
    /// </para>
    /// </remarks>
    internal readonly struct NvencFrameCompletionRecord
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencCaptureWorkSlotLease _workSlot;
        private readonly NvencFrameCompletionCreditLease _frameCompletionCredit;
        private readonly CaptureFrameCompletionStatus _status;
        private readonly NvencFrameCompletionReason _reason;

        private NvencFrameCompletionRecord(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencFrameCompletionCreditLease frameCompletionCredit,
            CaptureFrameCompletionStatus status,
            NvencFrameCompletionReason reason)
        {
            _workToken = workToken;
            _workSlot = workSlot;
            _frameCompletionCredit = frameCompletionCredit;
            _status = status;
            _reason = reason;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencCaptureWorkSlotLease WorkSlot => _workSlot;

        internal NvencFrameCompletionCreditLease FrameCompletionCredit => _frameCompletionCredit;

        internal CaptureFrameCompletionStatus Status => _status;

        internal NvencFrameCompletionReason Reason => _reason;

        internal bool IsValid
        {
            get
            {
                if (!_workToken.IsValid || !_workSlot.IsValid || !_frameCompletionCredit.IsValid ||
                    !CorrelatesToWorkSlot(_workToken, _workSlot))
                {
                    return false;
                }

                switch (_status)
                {
                    case CaptureFrameCompletionStatus.Succeeded:
                        return _reason == NvencFrameCompletionReason.None;
                    case CaptureFrameCompletionStatus.Failed:
                        return IsFailureReason(_reason);
                    case CaptureFrameCompletionStatus.Cancelled:
                        return IsCancellationReason(_reason);
                    default:
                        return false;
                }
            }
        }

        internal static NvencFrameCompletionRecord CreateSucceeded(
            in CaptureFrameWorkToken workToken,
            in NvencCaptureWorkSlotLease workSlot,
            in NvencFrameCompletionCreditLease frameCompletionCredit)
        {
            ValidateCommon(workToken, workSlot, frameCompletionCredit);

            return new NvencFrameCompletionRecord(
                workToken,
                workSlot,
                frameCompletionCredit,
                CaptureFrameCompletionStatus.Succeeded,
                NvencFrameCompletionReason.None);
        }

        internal static NvencFrameCompletionRecord CreateFailed(
            in CaptureFrameWorkToken workToken,
            in NvencCaptureWorkSlotLease workSlot,
            in NvencFrameCompletionCreditLease frameCompletionCredit,
            NvencFrameCompletionReason reason)
        {
            ValidateCommon(workToken, workSlot, frameCompletionCredit);

            if (!IsFailureReason(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason,
                    "A Failed completion requires a defined failure reason.");
            }

            return new NvencFrameCompletionRecord(
                workToken,
                workSlot,
                frameCompletionCredit,
                CaptureFrameCompletionStatus.Failed,
                reason);
        }

        internal static NvencFrameCompletionRecord CreateCancelled(
            in CaptureFrameWorkToken workToken,
            in NvencCaptureWorkSlotLease workSlot,
            in NvencFrameCompletionCreditLease frameCompletionCredit,
            NvencFrameCompletionReason reason)
        {
            ValidateCommon(workToken, workSlot, frameCompletionCredit);

            if (!IsCancellationReason(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason,
                    "A Cancelled completion requires a defined cancellation reason.");
            }

            return new NvencFrameCompletionRecord(
                workToken,
                workSlot,
                frameCompletionCredit,
                CaptureFrameCompletionStatus.Cancelled,
                reason);
        }

        internal static bool IsFailureReason(NvencFrameCompletionReason reason)
        {
            switch (reason)
            {
                case NvencFrameCompletionReason.RunChunkControlledFailure:
                case NvencFrameCompletionReason.GpuConversionFailed:
                case NvencFrameCompletionReason.NvencSubmitFailed:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsCancellationReason(NvencFrameCompletionReason reason)
        {
            switch (reason)
            {
                case NvencFrameCompletionReason.CancelledBeforeSubmit:
                case NvencFrameCompletionReason.CancelledAfterRunAbandoned:
                case NvencFrameCompletionReason.DrainedBeforeSubmit:
                    return true;
                default:
                    return false;
            }
        }

        private static void ValidateCommon(
            in CaptureFrameWorkToken workToken,
            in NvencCaptureWorkSlotLease workSlot,
            in NvencFrameCompletionCreditLease frameCompletionCredit)
        {
            if (!workToken.IsValid)
            {
                throw new ArgumentException("Work token must be valid.", nameof(workToken));
            }

            if (!workSlot.IsValid)
            {
                throw new ArgumentException("Work slot lease must be valid.", nameof(workSlot));
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

        private static bool CorrelatesToWorkSlot(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot)
        {
            return workToken.SlotIndex == workSlot.SlotIndex &&
                workToken.Generation == workSlot.Generation;
        }
    }
}
