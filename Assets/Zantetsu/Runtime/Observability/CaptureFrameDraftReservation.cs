using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, value-type receipt for a reserved capture frame draft
    /// admission slot. The implementation identifiers (owner identity,
    /// reservation generation, and pending slot index) are not exposed outside
    /// this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Committing or cancelling any single copy invalidates every other copy of
    /// the same reservation, which is then rejected as stale by the registry.
    /// The owner identity is a <see cref="Guid"/> so registries cannot collide.
    /// </para>
    /// <para>
    /// The struct holds only value-type fields and performs no allocation.
    /// </para>
    /// </remarks>
    internal readonly struct CaptureFrameDraftReservation
    {
        internal readonly Guid OwnerId;

        internal readonly long Generation;

        internal readonly int PendingSlotIndex;

        internal CaptureFrameDraftReservation(Guid ownerId, long generation, int pendingSlotIndex)
        {
            OwnerId = ownerId;
            Generation = generation;
            PendingSlotIndex = pendingSlotIndex;
        }

        internal bool IsValid =>
            OwnerId != Guid.Empty &&
            Generation > 0 &&
            PendingSlotIndex >= 0;
    }
}
