using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A non-owning, allocation-free reservation of one fixed NVENC Encode
    /// Sample Slot in a <see cref="NvencEncodeSampleSlotPool"/>. This logical
    /// lease carries only the issuing pool correlation, slot index, and
    /// generation; it holds no Texture, NVENC handle, Completion Event, or other
    /// native resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default</c> is an invalid lease. The owner correlation and generation
    /// are private value-type fields with no setters, so a lease cannot collide
    /// with a same-index, same-generation slot of another pool, and re-renting a
    /// slot makes every earlier copy of its lease stale. The value type holds no
    /// reference-type fields and copies without allocation.
    /// </para>
    /// </remarks>
    internal readonly struct NvencEncodeSampleSlotLease
    {
        private readonly Guid _ownerToken;
        private readonly int _slotIndex;
        private readonly long _generation;

        internal NvencEncodeSampleSlotLease(Guid ownerToken, int slotIndex, long generation)
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
