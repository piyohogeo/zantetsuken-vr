using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A non-owning lease over one render target slot in a
    /// <see cref="CaptureFrameRenderTargetPool"/>. A value type with no
    /// reference-type fields; copying a lease is safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default</c> is an invalid lease. The owner pool identity and slot
    /// generation are held as value-type fields and are not exposed publicly, so
    /// a lease cannot collide with a same-index, same-generation slot of a
    /// different pool, and re-renting a slot makes every earlier copy of its
    /// lease stale.
    /// </para>
    /// </remarks>
    public readonly struct CaptureFrameRenderTargetLease
    {
        private readonly Guid _ownerToken;
        private readonly int _slotIndex;
        private readonly long _generation;

        public int SlotIndex => _slotIndex;

        public bool IsValid => _ownerToken != Guid.Empty;

        internal Guid OwnerToken => _ownerToken;

        internal long Generation => _generation;

        internal CaptureFrameRenderTargetLease(Guid ownerToken, int slotIndex, long generation)
        {
            _ownerToken = ownerToken;
            _slotIndex = slotIndex;
            _generation = generation;
        }
    }
}
