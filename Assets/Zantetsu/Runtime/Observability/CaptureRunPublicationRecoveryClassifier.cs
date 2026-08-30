using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free classifier that turns a publication recovery
    /// inspection snapshot into a single disposition and, when authoritative,
    /// the authoritative plan. It performs no filesystem work, no codec,
    /// serialization, or hash computation, and mutates, owns, or disposes
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification is a fixed priority cascade: root collision facts,
    /// document limit and invalid facts, then cross-document plan agreement.
    /// Canonical plans are compared by their held values only, never by
    /// re-serializing or re-hashing. Corrupted nested values are treated as
    /// disagreement and never throw.
    /// </para>
    /// </remarks>
    internal static class CaptureRunPublicationRecoveryClassifier
    {
        internal static CaptureRunPublicationRecoveryDecision Classify(
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            return new CaptureRunPublicationRecoveryDecision(snapshot);
        }

        /// <summary>
        /// Pure computation shared with the decision constructor and its
        /// <c>IsValid</c> recomputation. Assumes the snapshot is valid.
        /// </summary>
        internal static CaptureRunPublicationRecoveryDisposition ComputeDisposition(
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot,
            out CapturePublicationPlan authoritativePlan)
        {
            authoritativePlan = null;

            if (snapshot.StagingRootEntryLimitExceeded || snapshot.FinalRootEntryLimitExceeded
                || snapshot.StagingHasUnexpectedEntries || snapshot.FinalHasUnexpectedEntries
                || snapshot.StagingFramesStatus == CaptureRunPublicationFramesObservationStatus.Invalid
                || snapshot.FinalFramesStatus == CaptureRunPublicationFramesObservationStatus.Invalid)
            {
                return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
            }

            CaptureRunPublicationDocumentObservation planTemporary = snapshot.PublicationPlanTemporary;
            CaptureRunPublicationDocumentObservation plan = snapshot.PublicationPlan;
            CaptureRunPublicationDocumentObservation indexTemporary = snapshot.CaptureIndexTemporary;
            CaptureRunPublicationDocumentObservation index = snapshot.CaptureIndex;

            if (planTemporary.Status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded
                || plan.Status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded
                || indexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded
                || index.Status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded)
            {
                return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
            }

            if (plan.Status == CaptureRunPublicationDocumentObservationStatus.Invalid
                || index.Status == CaptureRunPublicationDocumentObservationStatus.Invalid)
            {
                return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;

            if (!CanonicalMatchesOperation(planTemporary, operation)
                || !CanonicalMatchesOperation(plan, operation)
                || !CanonicalMatchesOperation(indexTemporary, operation)
                || !CanonicalMatchesOperation(index, operation))
            {
                return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
            }

            if (index.Status == CaptureRunPublicationDocumentObservationStatus.Canonical)
            {
                CapturePublicationPlan indexPlan = index.Plan;

                if (plan.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                    && !PlansEqual(plan.Plan, indexPlan))
                {
                    return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
                }

                if (planTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                    && !PlansEqual(planTemporary.Plan, indexPlan))
                {
                    return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
                }

                if (indexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                    && !PlansEqual(indexTemporary.Plan, indexPlan))
                {
                    return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
                }

                authoritativePlan = indexPlan;
                return CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative;
            }

            if (plan.Status == CaptureRunPublicationDocumentObservationStatus.Canonical)
            {
                CapturePublicationPlan planValue = plan.Plan;

                if (planTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                    && !PlansEqual(planTemporary.Plan, planValue))
                {
                    return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
                }

                if (indexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                    && !PlansEqual(indexTemporary.Plan, planValue))
                {
                    return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
                }

                authoritativePlan = planValue;
                return CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative;
            }

            if (planTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                && indexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.Canonical
                && !PlansEqual(planTemporary.Plan, indexTemporary.Plan))
            {
                return CaptureRunPublicationRecoveryDisposition.RunRootCollision;
            }

            return CaptureRunPublicationRecoveryDisposition.NoAuthoritativeDocument;
        }

        private static bool CanonicalMatchesOperation(
            CaptureRunPublicationDocumentObservation observation,
            CaptureRunPublicationRecoveryInspectionOperation operation)
        {
            if (observation.Status != CaptureRunPublicationDocumentObservationStatus.Canonical)
            {
                return true;
            }

            CapturePublicationPlan plan = observation.Plan;
            if (plan == null || !plan.IsValid)
            {
                return false;
            }

            return plan.TestRunId == operation.TestRunId
                && string.Equals(plan.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal);
        }

        internal static bool PlansEqual(CapturePublicationPlan left, CapturePublicationPlan right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || !left.IsValid || !right.IsValid)
            {
                return false;
            }

            if (left.SchemaVersion != right.SchemaVersion
                || left.TestRunId != right.TestRunId
                || !string.Equals(left.RunInitializationId, right.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(left.RunManifestContentSha256, right.RunManifestContentSha256, StringComparison.Ordinal)
                || left.EntryCount != right.EntryCount)
            {
                return false;
            }

            for (int i = 0; i < left.EntryCount; i++)
            {
                if (!EntriesEqual(left.GetEntry(i), right.GetEntry(i)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool EntriesEqual(CapturePublicationPlanEntry left, CapturePublicationPlanEntry right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || !left.IsValid || !right.IsValid)
            {
                return false;
            }

            return left.CaptureFrameId == right.CaptureFrameId
                && string.Equals(left.PngStagingRelativePath, right.PngStagingRelativePath, StringComparison.Ordinal)
                && string.Equals(left.SidecarStagingRelativePath, right.SidecarStagingRelativePath, StringComparison.Ordinal)
                && string.Equals(left.PngFinalRelativePath, right.PngFinalRelativePath, StringComparison.Ordinal)
                && string.Equals(left.SidecarFinalRelativePath, right.SidecarFinalRelativePath, StringComparison.Ordinal)
                && left.PngByteLength == right.PngByteLength
                && left.SidecarByteLength == right.SidecarByteLength
                && string.Equals(left.PngContentSha256, right.PngContentSha256, StringComparison.Ordinal)
                && string.Equals(left.SidecarContentSha256, right.SidecarContentSha256, StringComparison.Ordinal);
        }
    }
}
