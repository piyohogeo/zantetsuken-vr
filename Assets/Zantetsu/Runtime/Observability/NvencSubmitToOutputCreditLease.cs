using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A non-owning, allocation-free reservation of one fixed Submit-to-Output
    /// capacity credit in a <see cref="NvencSubmitToOutputCreditPool"/>. It
    /// carries only the issuing pool correlation, slot index, and generation; it
    /// holds no queue, buffer, surface, handle, or other native resource.
    /// </summary>
    /// <remarks>
    /// <c>default</c> is invalid. The owner correlation and generation are
    /// private value-type fields with no setters, so a lease cannot collide with
    /// a same-index, same-generation slot of another pool, and re-renting a slot
    /// makes every earlier copy of its lease stale.
    /// </remarks>
    internal readonly struct NvencSubmitToOutputCreditLease
    {
        private readonly Guid _ownerToken;
        private readonly int _slotIndex;
        private readonly long _generation;

        internal NvencSubmitToOutputCreditLease(Guid ownerToken, int slotIndex, long generation)
        {
            _ownerToken = ownerToken;
            _slotIndex = slotIndex;
            _generation = generation;
        }

        internal int SlotIndex => _slotIndex;

        internal long Generation => _generation;

        internal Guid OwnerToken => _ownerToken;

        internal bool IsValid => _ownerToken != Guid.Empty;
    }
}
