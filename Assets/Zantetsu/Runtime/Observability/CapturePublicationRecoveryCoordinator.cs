using System;

namespace Zantetsu.Observability
{
    /// <summary>Generic inspection and missing-artifact publication coordinator.</summary>
    internal sealed class CapturePublicationRecoveryCoordinator
    {
        private readonly ICaptureArtifactStore _store;

        internal CapturePublicationRecoveryCoordinator(ICaptureArtifactStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        internal CapturePublicationRecoverySnapshot Inspect(CapturePublicationPlan plan)
        {
            if (plan == null || !plan.IsValid) throw new ArgumentException("Plan must be valid.", nameof(plan));
            CaptureArtifactRecoveryObservation[] observations = new CaptureArtifactRecoveryObservation[plan.ArtifactCount];
            for (int i = 0; i < observations.Length; i++)
            {
                CaptureArtifactDescriptor descriptor = plan.GetArtifact(i);
                observations[i] = new CaptureArtifactRecoveryObservation(
                    descriptor,
                    _store.VerifyStaging(descriptor),
                    _store.Verify(descriptor));
            }
            return new CapturePublicationRecoverySnapshot(plan, observations);
        }

        internal CapturePublicationRecoverySnapshot InspectPersisted(
            ICapturePublicationPlanStore planStore,
            int maximumCanonicalByteCount)
        {
            if (planStore == null) throw new ArgumentNullException(nameof(planStore));
            CapturePublicationPlan plan = planStore.ReadOrRecoverPlan(maximumCanonicalByteCount);
            if (plan == null || !plan.IsValid) throw new InvalidOperationException("Plan store returned an invalid plan.");
            return Inspect(plan);
        }

        internal CapturePublicationRecoveryDisposition ExecuteMissing(CapturePublicationRecoverySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!snapshot.IsValid) throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            CapturePublicationRecoveryDisposition disposition = CapturePublicationRecoveryClassifier.Classify(snapshot);
            if (disposition != CapturePublicationRecoveryDisposition.PublishMissingArtifacts) return disposition;

            // The whole execution must hold one reservation before any
            // filesystem change, so a Deferred outcome can never surface after
            // an earlier artifact has already been published. A store without
            // the reservation capability cannot make that guarantee, so
            // publication is refused (Deferred) with zero changes.
            ICaptureArtifactReservationStore reservationStore = _store as ICaptureArtifactReservationStore;
            CaptureArtifactPublishReservation reservation = reservationStore?.TryReservePublish();
            if (reservation == null)
            {
                return CapturePublicationRecoveryDisposition.Deferred;
            }

            try
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    CaptureArtifactRecoveryObservation observation = snapshot.GetObservation(i);
                    if (observation.Final.Status == CaptureArtifactVerificationStatus.Absent)
                    {
                        CaptureArtifactPublishReceipt receipt = reservationStore.PublishReserved(observation.Descriptor, reservation);
                        if (receipt == null || !receipt.IsIssuedFor(_store, observation.Descriptor))
                            throw new InvalidOperationException("Store returned an invalid publish receipt.");
                    }
                }
                return CapturePublicationRecoveryDisposition.CaptureComplete;
            }
            finally
            {
                reservationStore.ReleasePublishReservation(reservation);
            }
        }
    }
}
