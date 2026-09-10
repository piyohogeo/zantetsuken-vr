using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free classifier that turns one Phase 0.11 NVENC publication
    /// recovery snapshot into a single disposition and, only when the Run is
    /// recoverable, the authoritative plan that was already in the snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification is a fixed cascade. A finished plan next to the NVENC
    /// precommit temporary is a collision; so is a finished plan that is
    /// invalid, over the limit, from another Run, or not the fixed Phase 0.11
    /// graph. No finished plan is Incomplete, whether or not the temporary
    /// exists. A canonical plan is then decided by the verification of the one
    /// final chunk it declares: deferred verification defers, an exact match is
    /// recoverable, and an absent, mismatching, or invalid chunk is a
    /// collision.
    /// </para>
    /// <para>
    /// It performs no filesystem work, no codec, serialization, or hash
    /// computation, releases no lock, deletes nothing, and mutates, owns, or
    /// disposes nothing. It never restores or infers the previous process's
    /// <see cref="NvencRunEvidenceDisposition"/>. A contradictory snapshot -
    /// an undefined status, a canonical plan with no verification, or a
    /// verification of some other descriptor - resolves to a collision rather
    /// than to anything that would allow the Run to be published.
    /// </para>
    /// </remarks>
    internal static class NvencRunPublicationRecoveryClassifier
    {
        internal static NvencRunPublicationRecoveryDecision Classify(
            NvencRunPublicationRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            return new NvencRunPublicationRecoveryDecision(snapshot);
        }

        /// <summary>
        /// Pure computation shared with the decision constructor and its
        /// <c>IsValid</c> recomputation. Assumes the snapshot is valid.
        /// </summary>
        internal static NvencRunPublicationRecoveryDisposition ComputeDisposition(
            NvencRunPublicationRecoveryInspectionSnapshot snapshot,
            out CapturePublicationPlan authoritativePlan)
        {
            authoritativePlan = null;

            CaptureRunPublicationDocumentObservationStatus status = snapshot.PublicationPlanStatus;

            switch (status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                case CaptureRunPublicationDocumentObservationStatus.Invalid:
                case CaptureRunPublicationDocumentObservationStatus.LimitExceeded:
                    break;

                default:
                    // An undefined observation is never read as "nothing there".
                    return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
            }

            // 1. A finished plan and the NVENC precommit temporary at the same
            //    time: two writers disagree about this Run root.
            if (status != CaptureRunPublicationDocumentObservationStatus.Absent
                && snapshot.PrecommitTemporaryPresent)
            {
                return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
            }

            // 2. A finished plan that could not be read as this Run's plan.
            if (status == CaptureRunPublicationDocumentObservationStatus.Invalid
                || status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded)
            {
                return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
            }

            // 3. No finished plan at all: the previous process never reached the
            //    plan commit, whether or not its temporary is still there.
            if (status == CaptureRunPublicationDocumentObservationStatus.Absent)
            {
                return NvencRunPublicationRecoveryDisposition.Incomplete;
            }

            CapturePublicationPlan plan = snapshot.PublicationPlan;
            if (!NvencRunPublicationRecoveryPlanShape.IsFixedPhase011Plan(
                    plan, snapshot.TestRunId, snapshot.RunInitializationId))
            {
                return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
            }

            if (!snapshot.HasChunkVerificationResult)
            {
                // A canonical plan of this Run with no verification of its own
                // chunk decides nothing; it is not read as recoverable.
                return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
            }

            CaptureArtifactVerificationResult verification = snapshot.ChunkVerification;
            if (!verification.IsValid
                || !ReferenceEquals(verification.Descriptor, plan.GetArtifact(0)))
            {
                // A verification of some other artifact says nothing about the
                // chunk this plan declares.
                return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
            }

            // 4. The chunk could not be verified yet.
            if (verification.ExecutionDisposition
                == CaptureArtifactVerificationExecutionDisposition.Deferred)
            {
                return NvencRunPublicationRecoveryDisposition.Deferred;
            }

            // 5. The one declared final chunk is exactly what the plan says.
            if (verification.ExecutionDisposition
                    == CaptureArtifactVerificationExecutionDisposition.Completed
                && verification.Status == CaptureArtifactVerificationStatus.MatchesExpected)
            {
                authoritativePlan = plan;
                return NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired;
            }

            // 6. Absent, mismatching, or invalid.
            return NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision;
        }
    }
}
