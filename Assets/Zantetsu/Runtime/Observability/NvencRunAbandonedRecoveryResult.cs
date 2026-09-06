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
    /// <see cref="Create"/> is the only way to issue a valid result, and it is
    /// called only after the abandon recovery has resolved the sample slot and
    /// the owned Access Unit, so a Cancelled publish cannot proceed while the
    /// NVENC output is still unresolved. <see cref="Matches"/> re-checks the
    /// exact record and sample slot binding without throwing or allocating.
    /// </para>
    /// </remarks>
    internal readonly struct NvencRunAbandonedRecoveryResult
    {
        private readonly NvencSubmitToOutputRecord _record;
        private readonly NvencEncodeSampleSlotLease _sampleSlot;
        private readonly NvencOwnedAccessUnitLease _ownedAccessUnit;

        private NvencRunAbandonedRecoveryResult(
            NvencSubmitToOutputRecord record,
            NvencEncodeSampleSlotLease sampleSlot,
            NvencOwnedAccessUnitLease ownedAccessUnit)
        {
            _record = record;
            _sampleSlot = sampleSlot;
            _ownedAccessUnit = ownedAccessUnit;
        }

        internal NvencSubmitToOutputRecord Record => _record;

        internal NvencEncodeSampleSlotLease SampleSlot => _sampleSlot;

        /// <summary>
        /// The resolved Owned Access Unit: <c>default</c> when none was ever
        /// issued, or the exact returned lease when one was issued and returned.
        /// </summary>
        internal NvencOwnedAccessUnitLease OwnedAccessUnit => _ownedAccessUnit;

        internal bool IsValid =>
            _record.Kind == NvencSubmitToOutputRecordKind.Submitted &&
            _record.IsValid &&
            SampleSlotEquals(_sampleSlot, _record.SampleSlot);

        internal static NvencRunAbandonedRecoveryResult Create(
            in NvencSubmitToOutputRecord record,
            in NvencEncodeSampleSlotLease resolvedSampleSlot,
            in NvencOwnedAccessUnitLease resolvedOwnedAccessUnit)
        {
            if (record.Kind != NvencSubmitToOutputRecordKind.Submitted || !record.IsValid)
            {
                throw new ArgumentException(
                    "A recovery result requires a valid Submitted record.", nameof(record));
            }

            if (!SampleSlotEquals(resolvedSampleSlot, record.SampleSlot))
            {
                throw new ArgumentException(
                    "The resolved sample slot must match the record's sample slot.", nameof(resolvedSampleSlot));
            }

            if (resolvedOwnedAccessUnit.IsValid &&
                !resolvedOwnedAccessUnit.WorkToken.IdenticalTo(record.WorkToken))
            {
                throw new ArgumentException(
                    "The resolved Owned Access Unit must bind to the record's work token.", nameof(resolvedOwnedAccessUnit));
            }

            return new NvencRunAbandonedRecoveryResult(record, resolvedSampleSlot, resolvedOwnedAccessUnit);
        }

        internal bool Matches(in NvencSubmitToOutputRecord record)
        {
            return RecordEquals(_record, record) &&
                SampleSlotEquals(_sampleSlot, record.SampleSlot);
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
