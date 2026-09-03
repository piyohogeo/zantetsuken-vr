using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Capability for a store that can reserve the whole-execution publication
    /// boundary atomically. A store that does not implement this cannot
    /// guarantee that a <c>Deferred</c> outcome leaves the file set unchanged,
    /// so the recovery coordinator must fail closed before any publication.
    /// </summary>
    internal interface ICaptureArtifactReservationStore
    {
        /// <summary>
        /// Reserves the publication resource for an entire execution, or
        /// returns <c>null</c> when no reservation is available. A successful
        /// reservation is bound to this store and is valid until released.
        /// </summary>
        CaptureArtifactPublishReservation TryReservePublish();

        /// <summary>
        /// Publishes one artifact under an active reservation previously minted
        /// by <see cref="TryReservePublish"/> on this exact store. Rejects a
        /// reservation from another store, another pool, or a stale/returned
        /// generation before any filesystem change.
        /// </summary>
        CaptureArtifactPublishReceipt PublishReserved(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactPublishReservation reservation);

        /// <summary>
        /// Releases a reservation previously minted by this store. Releasing a
        /// reservation from another store, or an already-released reservation,
        /// is a no-op.
        /// </summary>
        void ReleasePublishReservation(CaptureArtifactPublishReservation reservation);
    }
}
