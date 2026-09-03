using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Whole-execution publication reservation. A store that supports atomic
    /// multi-artifact publication mints one reservation per execution; the
    /// reservation is bound to the issuing store and to that store's current
    /// verification-buffer generation, so it cannot be replayed against a
    /// different store, a foreign pool, or a returned/stale lease.
    /// </summary>
    internal sealed class CaptureArtifactPublishReservation
    {
        private readonly ICaptureArtifactReservationStore _store;
        private readonly CaptureArtifactVerificationBufferPool.Lease _lease;

        internal CaptureArtifactPublishReservation(
            ICaptureArtifactReservationStore store,
            CaptureArtifactVerificationBufferPool.Lease lease)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        internal ICaptureArtifactReservationStore Store => _store;

        internal CaptureArtifactVerificationBufferPool.Lease Lease => _lease;
    }
}
