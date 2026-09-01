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
    /// entry access and reads only that reference. A token, structure, or
    /// entry-proof mismatch fails closed without producing a disposition, and
    /// only a legitimate classification of the exact snapshot the token proved
    /// yields a disposition.
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
        /// recomputation. Returns <c>false</c> without a disposition when the
        /// token binding, snapshot structure, lease liveness, or any issued
        /// entry proof is no longer intact, and returns <c>true</c> with a
        /// disposition only for a legitimate classification of the exact
        /// snapshot the token proved. Never throws.
        /// </summary>
        internal static bool TryComputeDisposition(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token,
            out CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            disposition = CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;

            if (snapshot == null || token == null)
            {
                return false;
            }

            if (!token.IsIssuedForExactBindings(snapshot))
            {
                return false;
            }

            CaptureRunPublicationEvidenceStatus traceStatus;
            PngJsonCapturePublicationArtifactInspectionAuthority authority;
            PngJsonCapturePublicationArtifactInspectionAuthorityKind authorityKind;
            CaptureRunPublicationRecoveryDisposition publication;
            int count;

            try
            {
                traceStatus = snapshot.TraceManifestStatus;

                PngJsonCapturePublicationArtifactInspectionOperation operation = snapshot.Operation;
                authority = operation.Authority;
                if (authority == null)
                {
                    return false;
                }

                CaptureRunLockLease lease = authority.LockLease;
                if (lease == null || !lease.IsCreated)
                {
                    return false;
                }

                authorityKind = authority.Kind;
                publication = authority.Disposition;
                count = snapshot.Count;
            }
            catch (Exception)
            {
                disposition = CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                return false;
            }

            if (traceStatus != CaptureRunPublicationEvidenceStatus.Absent
                && traceStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
            {
                return true;
            }

            if (traceStatus == CaptureRunPublicationEvidenceStatus.Absent)
            {
                if (authorityKind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision)
                {
                    if (!TryGetCaptureIndexTemporaryAbsent(authority, out bool captureIndexTemporaryAbsent))
                    {
                        return false;
                    }

                    if (!captureIndexTemporaryAbsent)
                    {
                        return true;
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                    {
                        return false;
                    }

                    if (HasArtifactAnomaly(observation)
                        || observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.Absent
                        || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.Absent)
                    {
                        return true;
                    }
                }

                disposition = publication == CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                    ? CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace
                    : CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;
                return true;
            }

            if (publication == CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative)
            {
                bool sourceMissing = false;
                bool publishable = false;

                for (int i = 0; i < count; i++)
                {
                    if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                    {
                        return false;
                    }

                    if (HasArtifactAnomaly(observation))
                    {
                        return true;
                    }

                    ClassifyPlanArtifact(
                        observation.FinalPngStatus, observation.StagingPngStatus, ref sourceMissing, ref publishable);
                    ClassifyPlanArtifact(
                        observation.FinalSidecarStatus, observation.StagingSidecarStatus, ref sourceMissing, ref publishable);
                }

                if (sourceMissing)
                {
                    disposition = CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing;
                    return true;
                }

                if (publishable)
                {
                    disposition = CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;
                    return true;
                }

                disposition = CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;
                return true;
            }

            if (publication == CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                if (authorityKind != PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision)
                {
                    return true;
                }

                bool anyFinalMissing = false;

                for (int i = 0; i < count; i++)
                {
                    if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                    {
                        return false;
                    }

                    if (HasArtifactAnomaly(observation))
                    {
                        return true;
                    }

                    if (observation.FinalPngStatus == CaptureRunPublicationEvidenceStatus.Absent
                        || observation.FinalSidecarStatus == CaptureRunPublicationEvidenceStatus.Absent)
                    {
                        anyFinalMissing = true;
                    }
                }

                disposition = anyFinalMissing
                    ? CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing
                    : CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
                return true;
            }

            return true;
        }

        private static bool TryGetCaptureIndexTemporaryAbsent(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            out bool isAbsent)
        {
            isAbsent = false;

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
            if (captureIndexTemporary == null)
            {
                return false;
            }

            isAbsent = captureIndexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Absent;
            return true;
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
