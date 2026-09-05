using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Accepted Work payload carried by the fixed SPSC Submission
    /// Queue toward the Submit Worker. A readonly value type holding the work
    /// token, the three reserved slot leases (work, sample, and GPU conversion
    /// sync), the two reserved capacity credits (Submit-to-Output and Frame
    /// Completion), and the caller-transferred surface. CaptureFrameId,
    /// TestRunId, slot index, and generation are read through the work token
    /// and leases, not duplicated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single factory <see cref="Create"/> issues a record only after the
    /// backend owner, work token, all three slot leases, both capacity credits,
    /// surface, and the exact pools are all valid and mutually consistent: the
    /// work token must correlate to the work slot lease (same slot index and
    /// generation), every lease and credit must be active in the given pools,
    /// and the surface must already be backend-owned by the exact owner and
    /// work token. The factory performs no side effect; it only observes the
    /// already-transferred state.
    /// </para>
    /// <para>
    /// <see cref="IsValidFor"/> re-checks the same shared predicate without
    /// throwing. The record neither transfers nor releases ownership and offers
    /// no dispose, release, or transfer API. Copying the record copies the
    /// surface reference but does not create a second ownership authority. The
    /// sample, sync, and credit slots are separate pools and lifecycles, so
    /// their indexes are not required to match the work slot or each other.
    /// </para>
    /// </remarks>
    internal readonly struct NvencSubmissionRecord
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencCaptureWorkSlotLease _workSlot;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;
        private readonly NvencGpuConversionSyncLease _syncSlot;
        private readonly NvencSubmitToOutputCreditLease _submitToOutputCredit;
        private readonly NvencFrameCompletionCreditLease _frameCompletionCredit;
        private readonly CaptureSurfaceLease _surface;

        private NvencSubmissionRecord(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencGpuConversionSyncLease syncSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit,
            CaptureSurfaceLease surface)
        {
            _workToken = workToken;
            _workSlot = workSlot;
            _sampleSlot = sampleSlot;
            _syncSlot = syncSlot;
            _submitToOutputCredit = submitToOutputCredit;
            _frameCompletionCredit = frameCompletionCredit;
            _surface = surface;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencCaptureWorkSlotLease WorkSlot => _workSlot;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal NvencGpuConversionSyncLease SyncSlot => _syncSlot;

        internal NvencSubmitToOutputCreditLease SubmitToOutputCredit => _submitToOutputCredit;

        internal NvencFrameCompletionCreditLease FrameCompletionCredit => _frameCompletionCredit;

        internal CaptureSurfaceLease Surface => _surface;

        internal bool IsValidFor(
            Guid backendOwner,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            return IsCurrentlyValid(
                backendOwner, _workToken, _workSlot, _sampleSlot, _syncSlot,
                _submitToOutputCredit, _frameCompletionCredit, _surface,
                workSlots, sampleSlots, syncSlots, submitToOutputCredits, frameCompletionCredits);
        }

        internal static NvencSubmissionRecord Create(
            Guid backendOwner,
            in CaptureFrameWorkToken workToken,
            in NvencCaptureWorkSlotLease workSlot,
            in NvencEncodeSampleSlotLease sampleSlot,
            in NvencGpuConversionSyncLease syncSlot,
            in NvencSubmitToOutputCreditLease submitToOutputCredit,
            in NvencFrameCompletionCreditLease frameCompletionCredit,
            CaptureSurfaceLease surface,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
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

            if (!IsCurrentlyValid(
                backendOwner, workToken, workSlot, sampleSlot, syncSlot,
                submitToOutputCredit, frameCompletionCredit, surface,
                workSlots, sampleSlots, syncSlots, submitToOutputCredits, frameCompletionCredits))
            {
                throw new ArgumentException("Submission record inputs must be valid and mutually consistent.");
            }

            return new NvencSubmissionRecord(
                workToken, workSlot, sampleSlot, syncSlot, submitToOutputCredit, frameCompletionCredit, surface);
        }

        private static bool IsCurrentlyValid(
            Guid backendOwner,
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencGpuConversionSyncLease syncSlot,
            NvencSubmitToOutputCreditLease submitToOutputCredit,
            NvencFrameCompletionCreditLease frameCompletionCredit,
            CaptureSurfaceLease surface,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            return backendOwner != Guid.Empty &&
                workToken.IsValid &&
                workSlot.IsValid &&
                sampleSlot.IsValid &&
                syncSlot.IsValid &&
                submitToOutputCredit.IsValid &&
                frameCompletionCredit.IsValid &&
                surface != null &&
                workToken.SlotIndex == workSlot.SlotIndex &&
                workToken.Generation == workSlot.Generation &&
                workSlots != null &&
                sampleSlots != null &&
                syncSlots != null &&
                submitToOutputCredits != null &&
                frameCompletionCredits != null &&
                workSlots.IsActive(workSlot) &&
                sampleSlots.IsActive(sampleSlot) &&
                syncSlots.IsActive(syncSlot) &&
                submitToOutputCredits.IsActive(submitToOutputCredit) &&
                frameCompletionCredits.IsActive(frameCompletionCredit) &&
                surface.IsOwnedBy(backendOwner, workToken);
        }
    }
}
