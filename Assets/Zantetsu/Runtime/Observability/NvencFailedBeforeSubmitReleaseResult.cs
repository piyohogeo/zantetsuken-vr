using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free, immutable proof that one FailedBeforeSubmit
    /// Submit-to-Output record's Encode Sample Slot has been returned by the
    /// failed-before-submit release processing. It binds the exact record and
    /// the exact returned sample slot; it transfers no ownership and holds no
    /// pool, handle, or native resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default</c> is invalid and matches nothing. The single factory
    /// <see cref="Create"/> verifies against the exact sample slot pool that
    /// the record's Encode Sample Slot is no longer active, so a release
    /// result can only be issued after the release processing has returned
    /// the exact sample slot, and a Frame Completion publish cannot proceed
    /// without a completed sample slot return. <see cref="Matches"/>
    /// re-checks the exact record and sample slot binding without throwing or
    /// allocating.
    /// </para>
    /// </remarks>
    internal readonly struct NvencFailedBeforeSubmitReleaseResult
    {
        private readonly NvencSubmitToOutputRecord _record;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;

        private NvencFailedBeforeSubmitReleaseResult(
            NvencSubmitToOutputRecord record,
            NvencEncodeSampleSlotLease sampleSlot)
        {
            _record = record;
            _sampleSlot = sampleSlot;
        }

        internal NvencSubmitToOutputRecord Record => _record;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal bool IsValid =>
            _record.Kind == NvencSubmitToOutputRecordKind.FailedBeforeSubmit &&
            _record.IsValid &&
            SampleSlotEquals(_sampleSlot, _record.SampleSlot);

        internal static NvencFailedBeforeSubmitReleaseResult Create(
            in NvencSubmitToOutputRecord record,
            NvencEncodeSampleSlotPool sampleSlots)
        {
            if (record.Kind != NvencSubmitToOutputRecordKind.FailedBeforeSubmit || !record.IsValid)
            {
                throw new ArgumentException(
                    "A release result requires a valid FailedBeforeSubmit record.", nameof(record));
            }

            if (sampleSlots == null)
            {
                throw new ArgumentNullException(nameof(sampleSlots));
            }

            // The exact sample slot must already be returned in its pool; a
            // still-active sample slot proves the release did not complete.
            if (sampleSlots.IsActive(record.SampleSlot))
            {
                throw new InvalidOperationException(
                    "The record's Encode Sample Slot is still active; it must be returned before the release result is issued.");
            }

            return new NvencFailedBeforeSubmitReleaseResult(record, record.SampleSlot);
        }

        internal bool Matches(in NvencSubmitToOutputRecord record, NvencEncodeSampleSlotPool sampleSlots)
        {
            return RecordEquals(_record, record) &&
                SampleSlotEquals(_sampleSlot, record.SampleSlot) &&
                sampleSlots != null &&
                !sampleSlots.IsActive(record.SampleSlot);
        }

        private static bool SampleSlotEquals(in NvencEncodeSampleSlotLease a, in NvencEncodeSampleSlotLease b)
        {
            return a.OwnerToken == b.OwnerToken &&
                a.SlotIndex == b.SlotIndex &&
                a.Generation == b.Generation;
        }

        private static bool RecordEquals(in NvencSubmitToOutputRecord a, in NvencSubmitToOutputRecord b)
        {
            return a.WorkToken.IdenticalTo(b.WorkToken) &&
                a.WorkSlot.OwnerToken == b.WorkSlot.OwnerToken &&
                a.WorkSlot.SlotIndex == b.WorkSlot.SlotIndex &&
                a.WorkSlot.Generation == b.WorkSlot.Generation &&
                a.SampleSlot.OwnerToken == b.SampleSlot.OwnerToken &&
                a.SampleSlot.SlotIndex == b.SampleSlot.SlotIndex &&
                a.SampleSlot.Generation == b.SampleSlot.Generation &&
                a.SubmitToOutputCredit.OwnerToken == b.SubmitToOutputCredit.OwnerToken &&
                a.SubmitToOutputCredit.SlotIndex == b.SubmitToOutputCredit.SlotIndex &&
                a.SubmitToOutputCredit.Generation == b.SubmitToOutputCredit.Generation &&
                a.FrameCompletionCredit.OwnerToken == b.FrameCompletionCredit.OwnerToken &&
                a.FrameCompletionCredit.SlotIndex == b.FrameCompletionCredit.SlotIndex &&
                a.FrameCompletionCredit.Generation == b.FrameCompletionCredit.Generation &&
                a.Kind == b.Kind &&
                a.Reason == b.Reason;
        }
    }
}
