using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free value that fixes the exact Fresh ownership
    /// chain from a frozen-Run publication result through the PNG-compatible
    /// plan binding to the publication path set, ready to seed the Fresh
    /// artifact inspection path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the plan binding
    /// and the publication path set — and has no public or internal
    /// constructor. It duplicates no descriptor, entry, identifier, path, hash,
    /// or session; every accessor forwards from the held graph. The only
    /// construction path is <see cref="Create"/>, which validates the binding
    /// once, correlates the exact references, builds the path set once, and
    /// assigns the fields through the private assignment constructor, so no
    /// path set, legacy plan, or frozen result can be injected from outside.
    /// </para>
    /// <para>
    /// This seed does not claim that any <c>publication.plan</c> bytes were
    /// observed as a PngJson document, that publication document inspection or
    /// artifact inspection has completed, that artifacts exist, match, or were
    /// published, that a capture index exists, or that cleanup or notification
    /// completed. It only proves that the frozen result's persisted generic
    /// plan and the strictly derived PNG-compatible plan can be handed to
    /// artifact inspection under the same Fresh ownership.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCaptureFrozenRunArtifactInspectionSeed
    {
        private readonly PngJsonCaptureFrozenRunPublicationPlanBinding _planBinding;
        private readonly CaptureRunPublicationPathSet _publicationPaths;

        private PngJsonCaptureFrozenRunArtifactInspectionSeed(
            PngJsonCaptureFrozenRunPublicationPlanBinding planBinding,
            CaptureRunPublicationPathSet publicationPaths)
        {
            _planBinding = planBinding;
            _publicationPaths = publicationPaths;
        }

        /// <summary>
        /// Atomic validated factory: the single validation-and-assignment site.
        /// It validates the binding once as the sole full-plan boundary, then
        /// confirms only reference, value, and O(1) ownership correlation,
        /// builds the publication path set once, and assigns fields.
        /// </summary>
        internal static PngJsonCaptureFrozenRunArtifactInspectionSeed Create(
            PngJsonCaptureFrozenRunPublicationPlanBinding planBinding)
        {
            if (planBinding == null)
            {
                throw new ArgumentNullException(nameof(planBinding));
            }

            if (!planBinding.IsValid)
            {
                throw new ArgumentException("Plan binding must remain valid.", nameof(planBinding));
            }

            CaptureEvidenceFrozenRunPublicationResult frozen = planBinding.FrozenPublicationResult;
            if (frozen == null)
            {
                throw new ArgumentException("Plan binding must hold a frozen publication result.", nameof(planBinding));
            }

            CapturePublicationPlan genericPlan = planBinding.GenericPlan;
            PngJsonCapturePublicationPlan legacyPlan = planBinding.LegacyPlan;
            if (genericPlan == null || legacyPlan == null)
            {
                throw new ArgumentException("Plan binding must hold both plans.", nameof(planBinding));
            }

            if (!ReferenceEquals(frozen.Plan, genericPlan)
                || !ReferenceEquals(planBinding.LegacyPlan, legacyPlan))
            {
                throw new ArgumentException("Plan binding must hold the exact frozen and legacy plans.", nameof(planBinding));
            }

            CaptureRunRootLayout rootLayout = planBinding.RootLayout;
            if (rootLayout == null || !rootLayout.IsValid)
            {
                throw new ArgumentException("Plan binding must hold a valid root layout.", nameof(planBinding));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = planBinding.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Plan binding must hold live lock identity evidence.", nameof(planBinding));
            }

            CaptureEvidenceRunFreezeReceipt freezeReceipt = planBinding.FreezeReceipt;
            CaptureRunInitializationSession session = planBinding.RunSession;
            if (freezeReceipt == null || session == null || !session.IsValid
                || session.TestRunId != lockIdentityEvidence.TestRunId
                || !ReferenceEquals(session.RootLayout, lockIdentityEvidence.RootLayout))
            {
                throw new ArgumentException("Plan binding must hold a live session bound to the exact lock identity evidence.", nameof(planBinding));
            }

            if (planBinding.TestRunId != frozen.TestRunId
                || planBinding.TestRunId != genericPlan.TestRunId
                || planBinding.TestRunId != legacyPlan.TestRunId
                || planBinding.TestRunId != rootLayout.TestRunId)
            {
                throw new ArgumentException("Test run ID must match across the seed graph.", nameof(planBinding));
            }

            if (!string.Equals(planBinding.RunInitializationId, frozen.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(planBinding.RunInitializationId, genericPlan.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(planBinding.RunInitializationId, legacyPlan.RunInitializationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Run initialization ID must match across the seed graph.", nameof(planBinding));
            }

            if (!string.Equals(planBinding.RunManifestContentHash, genericPlan.RunManifestContentHash, StringComparison.Ordinal)
                || !string.Equals(planBinding.RunManifestContentHash, legacyPlan.RunManifestContentSha256, StringComparison.Ordinal)
                || !string.Equals(frozen.RunManifestContentHash, genericPlan.RunManifestContentHash, StringComparison.Ordinal))
            {
                throw new ArgumentException("Manifest hash must match across the seed graph.", nameof(planBinding));
            }

            CaptureRunPublicationPathSet publicationPaths = new CaptureRunPublicationPathSet(rootLayout);
            if (!publicationPaths.IsValid || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
            {
                throw new ArgumentException("Publication paths must be valid for the root layout.", nameof(planBinding));
            }

            if (!string.Equals(publicationPaths.PublicationPlanPath, frozen.PublicationPlanPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Publication plan path must match the frozen result.", nameof(planBinding));
            }

            return new PngJsonCaptureFrozenRunArtifactInspectionSeed(planBinding, publicationPaths);
        }

        internal PngJsonCaptureFrozenRunPublicationPlanBinding PlanBinding => _planBinding;

        internal CaptureEvidenceFrozenRunPublicationResult FrozenPublicationResult => _planBinding.FrozenPublicationResult;

        internal CapturePublicationPlan GenericPlan => _planBinding.GenericPlan;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _planBinding.LegacyPlan;

        internal CaptureEvidenceRunFreezeReceipt FreezeReceipt => _planBinding.FreezeReceipt;

        internal CaptureFrameDraftRegistry Drafts => _planBinding.Drafts;

        internal CaptureArtifactRegistry Artifacts => _planBinding.Artifacts;

        internal CaptureRunInitializationSession RunSession => _planBinding.RunSession;

        internal CaptureRunRootLayout RootLayout => _planBinding.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _planBinding.LockIdentityEvidence;

        internal long TestRunId => _planBinding.TestRunId;

        internal string RunInitializationId => _planBinding.RunInitializationId;

        internal string RunManifestContentSha256 => _planBinding.RunManifestContentHash;

        internal CaptureRunPublicationPathSet PublicationPaths => _publicationPaths;

        internal CaptureRunPublicationRecoveryDisposition Disposition =>
            CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative;

        internal bool IsValid
        {
            get
            {
                PngJsonCaptureFrozenRunPublicationPlanBinding planBinding = _planBinding;
                if (planBinding == null || !planBinding.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationPathSet publicationPaths = _publicationPaths;
                if (publicationPaths == null || !publicationPaths.IsValid)
                {
                    return false;
                }

                CaptureEvidenceFrozenRunPublicationResult frozen = planBinding.FrozenPublicationResult;
                CapturePublicationPlan genericPlan = planBinding.GenericPlan;
                PngJsonCapturePublicationPlan legacyPlan = planBinding.LegacyPlan;
                if (frozen == null || genericPlan == null || legacyPlan == null)
                {
                    return false;
                }

                if (!ReferenceEquals(frozen.Plan, genericPlan)
                    || !ReferenceEquals(planBinding.LegacyPlan, legacyPlan))
                {
                    return false;
                }

                CaptureRunRootLayout rootLayout = planBinding.RootLayout;
                if (rootLayout == null || !rootLayout.IsValid)
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = planBinding.LockIdentityEvidence;
                CaptureRunInitializationSession session = planBinding.RunSession;
                CaptureEvidenceRunFreezeReceipt freezeReceipt = planBinding.FreezeReceipt;
                if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid
                    || session == null || !session.IsValid
                    || freezeReceipt == null
                    || session.TestRunId != lockIdentityEvidence.TestRunId
                    || !ReferenceEquals(session.RootLayout, lockIdentityEvidence.RootLayout))
                {
                    return false;
                }

                if (planBinding.TestRunId != frozen.TestRunId
                    || planBinding.TestRunId != genericPlan.TestRunId
                    || planBinding.TestRunId != legacyPlan.TestRunId
                    || planBinding.TestRunId != rootLayout.TestRunId)
                {
                    return false;
                }

                if (!string.Equals(planBinding.RunInitializationId, frozen.RunInitializationId, StringComparison.Ordinal)
                    || !string.Equals(planBinding.RunInitializationId, genericPlan.RunInitializationId, StringComparison.Ordinal)
                    || !string.Equals(planBinding.RunInitializationId, legacyPlan.RunInitializationId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!string.Equals(planBinding.RunManifestContentHash, genericPlan.RunManifestContentHash, StringComparison.Ordinal)
                    || !string.Equals(planBinding.RunManifestContentHash, legacyPlan.RunManifestContentSha256, StringComparison.Ordinal)
                    || !string.Equals(frozen.RunManifestContentHash, genericPlan.RunManifestContentHash, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!ReferenceEquals(publicationPaths.RootLayout, rootLayout))
                {
                    return false;
                }

                if (!string.Equals(publicationPaths.PublicationPlanPath, frozen.PublicationPlanPath, StringComparison.Ordinal))
                {
                    return false;
                }

                return true;
            }
        }
    }
}
