using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free, immutable proof that one Submitted Submit-to-Output
    /// record abandoned by a run abort has had its output safely recovered:
    /// its Encode Sample Slot has been returned and its Owned Access Unit is
    /// non-residual (either never issued, or issued and returned). It binds the
    /// exact record and the exact resolved sample slot; it transfers no
    /// ownership and holds no pool, buffer, handle, or native resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default</c> is invalid and matches nothing. The single factory
    /// <see cref="Create"/> verifies against the exact sample slot pool and
    /// the exact Owned Access Unit buffer that the sample slot is no longer
    /// active and that the supplied recovery proof was minted by that buffer
    /// for this exact work token, so a recovery result can only be issued
    /// after the abandoned output was safely recovered.
    /// <see cref="Matches"/> re-checks the exact record, sample slot, and
    /// recovery proof binding without throwing or allocating.
    /// </para>
    /// </remarks>
    internal readonly struct NvencRunAbandonedRecoveryResult
    {
        private readonly NvencSubmitToOutputRecord _record;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;
        private readonly NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof _recoveryProof;

        private NvencRunAbandonedRecoveryResult(
            NvencSubmitToOutputRecord record,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof recoveryProof)
        {
            _record = record;
            _sampleSlot = sampleSlot;
            _recoveryProof = recoveryProof;
        }

        internal NvencSubmitToOutputRecord Record => _record;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        internal bool IsValid =>
            _record.Kind == NvencSubmitToOutputRecordKind.Submitted &&
            _record.IsValid &&
            SampleSlotEquals(_sampleSlot, _record.SampleSlot);

        internal static NvencRunAbandonedRecoveryResult Create(
            in NvencSubmitToOutputRecord record,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencOwnedAccessUnitBuffer buffer,
            in NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof recoveryProof)
        {
            if (record.Kind != NvencSubmitToOutputRecordKind.Submitted || !record.IsValid)
            {
                throw new ArgumentException(
                    "A recovery result requires a valid Submitted record.", nameof(record));
            }

            if (sampleSlots == null)
            {
                throw new ArgumentNullException(nameof(sampleSlots));
            }

            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (sampleSlots.IsActive(record.SampleSlot))
            {
                throw new InvalidOperationException(
                    "The record's Encode Sample Slot is still active; it must be resolved before the recovery result is issued.");
            }

            // The recovery proof must be minted by this exact buffer for this
            // exact work token; a default, foreign, or forged proof is rejected.
            if (!buffer.VerifyRecoveryProof(recoveryProof, record.WorkToken))
            {
                throw new ArgumentException(
                    "The recovery proof must be minted by the exact buffer for this record's work token.",
                    nameof(recoveryProof));
            }

            return new NvencRunAbandonedRecoveryResult(record, record.SampleSlot, recoveryProof);
        }

        internal bool Matches(
            in NvencSubmitToOutputRecord record,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencOwnedAccessUnitBuffer buffer)
        {
            return RecordEquals(_record, record) &&
                SampleSlotEquals(_sampleSlot, record.SampleSlot) &&
                sampleSlots != null &&
                buffer != null &&
                !sampleSlots.IsActive(record.SampleSlot) &&
                buffer.VerifyRecoveryProof(_recoveryProof, record.WorkToken);
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
