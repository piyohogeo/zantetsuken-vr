using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free classifier that turns an artifact inspection snapshot
    /// into a single recovery disposition. It performs no filesystem work, no
    /// codec, serialization, or hash computation, and mutates, owns, or
    /// disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification is a fixed priority cascade: content-anomaly collision,
    /// then trace absence, then per-entry artifact publication state. Entry
    /// statuses are read in one linear pass per branch and never re-validated
    /// per entry.
    /// </para>
    /// </remarks>
    internal static class CaptureRunPublicationArtifactRecoveryClassifier
    {
        internal static CaptureRunPublicationArtifactRecoveryDecision Classify(
            CaptureRunPublicationArtifactInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            return new CaptureRunPublicationArtifactRecoveryDecision(snapshot);
        }

        /// <summary>
        /// Pure computation shared with the decision constructor and its
        /// <c>IsValid</c> recomputation. Assumes the snapshot is valid.
        /// </summary>
        internal static CaptureRunPublicationArtifactRecoveryDisposition ComputeDisposition(
            CaptureRunPublicationArtifactInspectionSnapshot snapshot)
        {
            CaptureRunPublicationEvidenceStatus traceStatus = snapshot.TraceManifestStatus;

            if (!IsCleanTrace(traceStatus))
            {
                return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
            }

            CaptureRunPublicationRecoveryDisposition publication = snapshot.Decision.Disposition;
            int count = snapshot.Count;

            if (traceStatus == CaptureRunPublicationEvidenceStatus.Absent)
            {
                if (!IsAbsentCaptureIndexTemporary(snapshot))
                {
                    return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                }

                for (int i = 0; i < count; i++)
                {
                    CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

                    if (HasArtifactAnomaly(observation))
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }

                    if (observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.Absent
                        || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.Absent)
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }
                }

                return publication == CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                    ? CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace
                    : CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
            }

            if (publication == CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative)
            {
                bool sourceMissing = false;
                bool publishable = false;

                for (int i = 0; i < count; i++)
                {
                    CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

                    if (HasArtifactAnomaly(observation))
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }

                    ClassifyPlanArtifact(
                        observation.FinalPngStatus, observation.StagingPngStatus, ref sourceMissing, ref publishable);
                    ClassifyPlanArtifact(
                        observation.FinalSidecarStatus, observation.StagingSidecarStatus, ref sourceMissing, ref publishable);
                }

                if (sourceMissing)
                {
                    return CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing;
                }

                if (publishable)
                {
                    return CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;
                }

                return CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;
            }

            if (publication == CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                bool anyFinalMissing = false;

                for (int i = 0; i < count; i++)
                {
                    CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

                    if (HasArtifactAnomaly(observation))
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }

                    if (observation.FinalPngStatus == CaptureRunPublicationEvidenceStatus.Absent
                        || observation.FinalSidecarStatus == CaptureRunPublicationEvidenceStatus.Absent)
                    {
                        anyFinalMissing = true;
                    }
                }

                return anyFinalMissing
                    ? CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing
                    : CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
            }

            return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
        }

        private static bool IsCleanTrace(CaptureRunPublicationEvidenceStatus status)
        {
            return status == CaptureRunPublicationEvidenceStatus.Absent
                || status == CaptureRunPublicationEvidenceStatus.MatchesExpected;
        }

        private static bool IsAbsentCaptureIndexTemporary(CaptureRunPublicationArtifactInspectionSnapshot snapshot)
        {
            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = snapshot.Decision.Snapshot;
            return publicationSnapshot.CaptureIndexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Absent;
        }

        private static bool HasArtifactAnomaly(CaptureRunPublicationArtifactEntryObservation observation)
        {
            return !IsCleanArtifactStatus(observation.StagingPngStatus)
                || !IsCleanArtifactStatus(observation.StagingSidecarStatus)
                || !IsCleanArtifactStatus(observation.FinalPngStatus)
                || !IsCleanArtifactStatus(observation.FinalSidecarStatus);
        }

        private static bool IsCleanArtifactStatus(CaptureRunPublicationEvidenceStatus status)
        {
            return status == CaptureRunPublicationEvidenceStatus.Absent
                || status == CaptureRunPublicationEvidenceStatus.MatchesExpected;
        }

        private static void ClassifyPlanArtifact(
            CaptureRunPublicationEvidenceStatus finalStatus,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            ref bool sourceMissing,
            ref bool publishable)
        {
            if (finalStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected)
            {
                return;
            }

            if (stagingStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected)
            {
                publishable = true;
            }
            else
            {
                sourceMissing = true;
            }
        }
    }
}
