using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A non-owning, allocation-free Sink-side reservation of the single fixed
    /// Access Unit region in an <see cref="NvencOwnedAccessUnitBuffer"/>. It
    /// carries only the issuing buffer identity, the generation, and the exact
    /// bound <see cref="CaptureFrameWorkToken"/>; it holds no storage
    /// reference, surface, handle, or OS resource.
    /// </summary>
    /// <remarks>
    /// <c>default</c> is invalid. Every field is a private readonly value, so a
    /// copied lease cannot gain authority, and the buffer's generation check
    /// makes any earlier copy stale after a release. The value type holds no
    /// reference-type fields and copies without allocation.
    /// </remarks>
    internal readonly struct NvencOwnedAccessUnitLease
    {
        private readonly Guid _ownerToken;
        private readonly long _generation;
        private readonly CaptureFrameWorkToken _workToken;

        internal NvencOwnedAccessUnitLease(Guid ownerToken, long generation, in CaptureFrameWorkToken workToken)
        {
            _ownerToken = ownerToken;
            _generation = generation;
            _workToken = workToken;
        }

        internal Guid OwnerToken => _ownerToken;

        internal long Generation => _generation;

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal bool IsValid => _ownerToken != Guid.Empty && _generation > 0 && _workToken.IsValid;
    }
}
