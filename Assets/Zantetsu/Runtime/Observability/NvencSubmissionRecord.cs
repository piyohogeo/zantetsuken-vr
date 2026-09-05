using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Accepted Work payload carried by the fixed SPSC Submission
    /// Queue toward the Submit Worker. A readonly value type holding only the
    /// work token, the two reserved slot leases, and the caller-transferred
    /// surface. CaptureFrameId, TestRunId, slot index, and generation are read
    /// through the work token and leases, not duplicated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single factory <see cref="Create"/> issues a record only after the
    /// backend owner, work token, both slot leases, surface, and the exact
    /// pools are all valid and mutually consistent: the work token must
    /// correlate to the work slot lease (same slot index and generation), both
    /// leases must be active in the given pools, and the surface must already
    /// be backend-owned by the exact owner and work token. The factory performs
    /// no side effect; it only observes the already-transferred state.
    /// </para>
    /// <para>
    /// <see cref="IsValidFor"/> re-checks the same shared predicate without
    /// throwing. The record neither transfers nor releases ownership and offers
    /// no dispose, release, or transfer API. Copying the record copies the
    /// surface reference but does not create a second ownership authority. The
    /// sample slot is a separate pool and lifecycle, so its index is not
    /// required to match the work slot.
    /// </para>
    /// </remarks>
    internal readonly struct NvencSubmissionRecord
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencCaptureWorkSlotLease _workSlot;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;
        private readonly CaptureSurfaceLease _surface;

        private NvencSubmissionRecord(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            CaptureSurfaceLease surface)
        {
            _workToken = workToken;
            _workSlot = workSlot;
            _sampleSlot = sampleSlot;
            _surface = surface;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencCaptureWorkSlotLease WorkSlot => _workSlot;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal CaptureSurfaceLease Surface => _surface;

        internal bool IsValidFor(
            Guid backendOwner,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots)
        {
            return IsCurrentlyValid(
                backendOwner, _workToken, _workSlot, _sampleSlot, _surface, workSlots, sampleSlots);
        }

        internal static NvencSubmissionRecord Create(
            Guid backendOwner,
            in CaptureFrameWorkToken workToken,
            in NvencCaptureWorkSlotLease workSlot,
            in NvencEncodeSampleSlotLease sampleSlot,
            CaptureSurfaceLease surface,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots)
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

            if (!IsCurrentlyValid(backendOwner, workToken, workSlot, sampleSlot, surface, workSlots, sampleSlots))
            {
                throw new ArgumentException("Submission record inputs must be valid and mutually consistent.");
            }

            return new NvencSubmissionRecord(workToken, workSlot, sampleSlot, surface);
        }

        private static bool IsCurrentlyValid(
            Guid backendOwner,
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot,
            CaptureSurfaceLease surface,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots)
        {
            return backendOwner != Guid.Empty &&
                workToken.IsValid &&
                workSlot.IsValid &&
                sampleSlot.IsValid &&
                surface != null &&
                workToken.SlotIndex == workSlot.SlotIndex &&
                workToken.Generation == workSlot.Generation &&
                workSlots != null &&
                sampleSlots != null &&
                workSlots.IsActive(workSlot) &&
                sampleSlots.IsActive(sampleSlot) &&
                surface.IsOwnedBy(backendOwner, workToken);
        }
    }
}
