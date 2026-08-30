using System;

namespace Zantetsu.Observability
{
    internal static class CapturePublicationRecoveryClassifier
    {
        internal static CapturePublicationRecoveryDisposition Classify(CapturePublicationRecoverySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!snapshot.IsValid) throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            bool missingFinal = false;
            bool missingSource = false;
            for (int i = 0; i < snapshot.Count; i++)
            {
                CaptureArtifactRecoveryObservation observation = snapshot.GetObservation(i);
                CaptureArtifactVerificationStatus staging = observation.Staging.Status;
                CaptureArtifactVerificationStatus final = observation.Final.Status;
                if (staging == CaptureArtifactVerificationStatus.Invalid
                    || staging == CaptureArtifactVerificationStatus.Mismatch
                    || final == CaptureArtifactVerificationStatus.Invalid
                    || final == CaptureArtifactVerificationStatus.Mismatch
                    || staging == CaptureArtifactVerificationStatus.None
                    || final == CaptureArtifactVerificationStatus.None)
                {
                    return CapturePublicationRecoveryDisposition.RunRootCollision;
                }

                if (final == CaptureArtifactVerificationStatus.Absent)
                {
                    missingFinal = true;
                    if (staging != CaptureArtifactVerificationStatus.MatchesExpected) missingSource = true;
                }
            }

            if (missingSource) return CapturePublicationRecoveryDisposition.ArtifactSourceMissing;
            if (missingFinal) return CapturePublicationRecoveryDisposition.PublishMissingArtifacts;
            return CapturePublicationRecoveryDisposition.CaptureComplete;
        }
    }
}
