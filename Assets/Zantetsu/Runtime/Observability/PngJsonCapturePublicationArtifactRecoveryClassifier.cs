using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free classifier that turns a completed shared publication
    /// artifact inspection snapshot into a single recovery disposition using
    /// one fixed rule table for both the Recovery and Fresh authorities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Classify"/> validates the snapshot once through its
    /// validation token, classifies once in a single linear pass with the same
    /// token, and constructs the decision without re-validating. The
    /// classification loop obtains each entry once through the token's issued
    /// entry access and reads only that reference, so a caller that mutates a
    /// snapshot during classification fails closed to
    /// <see cref="CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision"/>.
    /// </para>
    /// <para>
    /// This type holds no fields and performs no filesystem work, no codec,
    /// serialization, or hash computation, no LINQ or collection allocation,
    /// no notification, registry, or draft contact, and no lock acquisition or
    /// release.
    /// </para>
    /// </remarks>
    internal static class PngJsonCapturePublicationArtifactRecoveryClassifier
    {
        /// <summary>
        /// Validated entry point: null-checks the snapshot, performs the single
        /// full validation, classifies once with the issued token, and returns
        /// the decision only after classification completes.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryDecision Classify(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.TryValidate(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token))
            {
                throw new ArgumentException("Snapshot must be fully valid.", nameof(snapshot));
            }

            return PngJsonCapturePublicationArtifactRecoveryDecision.Create(snapshot, token);
        }

        /// <summary>
        /// Pure token-gated linear classification shared with the decision
        /// constructor and its <see cref="PngJsonCapturePublicationArtifactRecoveryDecision.IsValid"/>
        /// recomputation. Assumes the snapshot was validated and the token was
        /// issued for it; each entry is obtained once through the token in
        /// O(1) and never re-validated per entry beyond the token's issued
        /// entry re-correlation.
        /// </summary>
        internal static CaptureRunPublicationArtifactRecoveryDisposition ComputeDisposition(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token)
        {
            if (!token.IsIssuedForExactBindings(snapshot))
            {
                return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
            }

            CaptureRunPublicationEvidenceStatus traceStatus = snapshot.TraceManifestStatus;

            if (traceStatus != CaptureRunPublicationEvidenceStatus.Absent
                && traceStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
            {
                return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority authority = snapshot.Authority;
            PngJsonCapturePublicationArtifactInspectionAuthorityKind authorityKind = snapshot.AuthorityKind;
            CaptureRunPublicationRecoveryDisposition publication = authority.Disposition;
            int count = snapshot.Count;

            if (traceStatus == CaptureRunPublicationEvidenceStatus.Absent)
            {
                if (authorityKind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision
                    && !HasAbsentCaptureIndexTemporary(authority))
                {
                    return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                }

                for (int i = 0; i < count; i++)
                {
                    if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }

                    if (HasArtifactAnomaly(observation)
                        || observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.Absent
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
                    if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }

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
                if (authorityKind != PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision)
                {
                    return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                }

                bool anyFinalMissing = false;

                for (int i = 0; i < count; i++)
                {
                    if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                    {
                        return CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                    }

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

        private static bool HasAbsentCaptureIndexTemporary(PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            if (authority == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryDecision decision = authority.RecoveryDecision;
            if (decision == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null)
            {
                return false;
            }

            CaptureRunPublicationDocumentObservation captureIndexTemporary = snapshot.CaptureIndexTemporary;
            return captureIndexTemporary != null
                && captureIndexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Absent;
        }

        private static bool HasArtifactAnomaly(PngJsonCapturePublicationArtifactEntryObservation observation)
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
