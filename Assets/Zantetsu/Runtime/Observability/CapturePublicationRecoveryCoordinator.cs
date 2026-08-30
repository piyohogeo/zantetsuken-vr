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

        internal CapturePublicationRecoveryDisposition ExecuteMissing(CapturePublicationRecoverySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!snapshot.IsValid) throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            CapturePublicationRecoveryDisposition disposition = CapturePublicationRecoveryClassifier.Classify(snapshot);
            if (disposition != CapturePublicationRecoveryDisposition.PublishMissingArtifacts) return disposition;
            for (int i = 0; i < snapshot.Count; i++)
            {
                CaptureArtifactRecoveryObservation observation = snapshot.GetObservation(i);
                if (observation.Final.Status == CaptureArtifactVerificationStatus.Absent)
                {
                    CaptureArtifactPublishReceipt receipt = _store.Publish(observation.Descriptor);
                    if (receipt == null || !receipt.IsIssuedFor(_store, observation.Descriptor))
                        throw new InvalidOperationException("Store returned an invalid publish receipt.");
                }
            }
            return CapturePublicationRecoveryDisposition.CaptureComplete;
        }
    }
}
