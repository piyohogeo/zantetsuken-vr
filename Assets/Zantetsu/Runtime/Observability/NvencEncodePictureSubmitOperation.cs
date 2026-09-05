using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, allocation-free encode-picture submit operation for one
    /// accepted Phase 0.11 work. It carries only the exact work token and the
    /// exact Work and Encode Sample slot leases; it holds no source surface,
    /// sync credit, completion evidence, output buffer, fence, query, command
    /// buffer, or native handle. It performs no lease release, encoder call, or
    /// queue operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single factory <see cref="Create"/> issues an operation only while
    /// the source <see cref="NvencSubmissionRecord"/> is valid for the exact
    /// Work and Sample pools: the work token must correlate to the work slot
    /// lease (same slot index and generation) and both leases must be active in
    /// the given pools. Because the operation does not hold the surface or the
    /// sync credit, it stays valid for Work and Sample even after those two are
    /// released on the Main Thread.
    /// </para>
    /// </remarks>
    internal readonly struct NvencEncodePictureSubmitOperation
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencCaptureWorkSlotLease _workSlot;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;

        private NvencEncodePictureSubmitOperation(
            CaptureFrameWorkToken workToken,
            NvencCaptureWorkSlotLease workSlot,
            NvencEncodeSampleSlotLease sampleSlot)
        {
            _workToken = workToken;
            _workSlot = workSlot;
            _sampleSlot = sampleSlot;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencCaptureWorkSlotLease WorkSlot => _workSlot;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal bool IsValidFor(
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots)
        {
            return _workToken.IsValid &&
                _workSlot.IsValid &&
                _sampleSlot.IsValid &&
                _workToken.SlotIndex == _workSlot.SlotIndex &&
                _workToken.Generation == _workSlot.Generation &&
                workSlots != null &&
                sampleSlots != null &&
                workSlots.IsActive(_workSlot) &&
                sampleSlots.IsActive(_sampleSlot);
        }

        internal static NvencEncodePictureSubmitOperation Create(
            in NvencSubmissionRecord record,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots)
        {
            if (workSlots == null)
            {
                throw new ArgumentNullException(nameof(workSlots));
            }

            if (sampleSlots == null)
            {
                throw new ArgumentNullException(nameof(sampleSlots));
            }

            if (!record.WorkToken.IsValid ||
                !record.WorkSlot.IsValid ||
                !record.SampleSlot.IsValid ||
                record.WorkToken.SlotIndex != record.WorkSlot.SlotIndex ||
                record.WorkToken.Generation != record.WorkSlot.Generation ||
                !workSlots.IsActive(record.WorkSlot) ||
                !sampleSlots.IsActive(record.SampleSlot))
            {
                throw new ArgumentException(
                    "The submission record must be valid for the exact Work and Sample pools.");
            }

            return new NvencEncodePictureSubmitOperation(
                record.WorkToken, record.WorkSlot, record.SampleSlot);
        }
    }
}
