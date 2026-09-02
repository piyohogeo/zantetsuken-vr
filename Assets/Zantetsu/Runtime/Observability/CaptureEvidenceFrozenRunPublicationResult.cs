using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable intermediate provenance of one persisted frozen-Run
    /// publication plan: the issuing coordinator, the exact freeze receipt, and
    /// the exact publication-plan write receipt, correlated through the exact
    /// store, registries, session, lock identity evidence, and canonical plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the issuing
    /// coordinator, the coordinator-minted issuance proof, the freeze receipt,
    /// and the publication-plan write receipt — and has no public constructor.
    /// Every accessor forwards a value from the held graph: the store, plan,
    /// draft registry, artifact registry, run session, root layout, lock
    /// identity evidence, run identity, run initialization id, run manifest
    /// content hash, publication plan path, and canonical byte count are all
    /// forwarded rather than duplicated. The proof is never exposed.
    /// </para>
    /// <para>
    /// <see cref="Create"/> performs a single O(1) exact-binding check that
    /// the proof was minted by this coordinator for the exact freeze receipt
    /// and write receipt, then assigns fields. The full freeze-graph validation
    /// happened once in the coordinator before persistence, and is re-run only
    /// by <see cref="IsValid"/> through one exception-safe correlation
    /// predicate. It re-checks the proof binding, the freeze receipt validity,
    /// the exact store and root layout, the lock identity evidence's live lock
    /// ownership, the drained registries, and the write receipt's exact store,
    /// plan, path, and byte count. Any forged, replaced, released, or corrupted
    /// value converges to <c>false</c> without throwing. Because this result
    /// proves the current freeze receipt and lock liveness, it becomes invalid
    /// once the ownership lease is released.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureEvidenceFrozenRunPublicationResult
    {
        private readonly CaptureEvidenceRunPublicationCoordinator _issuedBy;
        private readonly CaptureEvidenceRunPublicationCoordinator.IssuanceProof _proof;
        private readonly CaptureEvidenceRunFreezeReceipt _freezeReceipt;
        private readonly CapturePublicationPlanWriteReceipt _writeReceipt;

        /// <summary>
        /// Private assignment constructor: stores the already-validated graph
        /// without re-checking it. The only construction path is
        /// <see cref="Create"/>, which performs an O(1) exact-binding check
        /// before assigning.
        /// </summary>
        private CaptureEvidenceFrozenRunPublicationResult(
            CaptureEvidenceRunPublicationCoordinator issuedBy,
            CaptureEvidenceRunPublicationCoordinator.IssuanceProof proof,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CapturePublicationPlanWriteReceipt writeReceipt)
        {
            _issuedBy = issuedBy;
            _proof = proof;
            _freezeReceipt = freezeReceipt;
            _writeReceipt = writeReceipt;
        }

        /// <summary>
        /// Atomic factory: performs the single O(1) exact-binding check that
        /// the proof was minted by the issuing coordinator for the exact freeze
        /// receipt and write receipt, then assigns fields exactly once. The full
        /// freeze-graph validation already happened once in the coordinator, and
        /// is re-run only by <see cref="IsValid"/>.
        /// </summary>
        internal static CaptureEvidenceFrozenRunPublicationResult Create(
            CaptureEvidenceRunPublicationCoordinator issuedBy,
            CaptureEvidenceRunPublicationCoordinator.IssuanceProof proof,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CapturePublicationPlanWriteReceipt writeReceipt)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (proof == null)
            {
                throw new ArgumentNullException(nameof(proof));
            }

            if (freezeReceipt == null)
            {
                throw new ArgumentNullException(nameof(freezeReceipt));
            }

            if (writeReceipt == null)
            {
                throw new ArgumentNullException(nameof(writeReceipt));
            }

            if (!issuedBy.IsMintedByThis(proof, freezeReceipt, writeReceipt))
            {
                throw new InvalidOperationException(
                    "Frozen-Run publication proof is not minted by this coordinator for this evidence.");
            }

            return new CaptureEvidenceFrozenRunPublicationResult(issuedBy, proof, freezeReceipt, writeReceipt);
        }

        internal CaptureEvidenceRunPublicationCoordinator IssuedBy => _issuedBy;

        internal CaptureArtifactFileStore Store => _issuedBy.Store;

        internal CaptureEvidenceRunFreezeReceipt FreezeReceipt => _freezeReceipt;

        internal CapturePublicationPlanWriteReceipt PlanWriteReceipt => _writeReceipt;

        internal CapturePublicationPlan Plan => _writeReceipt.Plan;

        internal CaptureFrameDraftRegistry Drafts => _freezeReceipt.Drafts;

        internal CaptureArtifactRegistry Artifacts => _freezeReceipt.Artifacts;

        internal CaptureRunInitializationSession RunSession => _freezeReceipt.RunSession;

        internal CaptureRunRootLayout RootLayout => _freezeReceipt.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _freezeReceipt.LockIdentityEvidence;

        internal long TestRunId => _freezeReceipt.TestRunId;

        internal string RunInitializationId => _freezeReceipt.RunInitializationId;

        internal string RunManifestContentHash => _writeReceipt.Plan.RunManifestContentHash;

        internal string PublicationPlanPath => _issuedBy.Store.PublicationPlanPath;

        internal int CanonicalByteCount => _writeReceipt.ByteCount;

        /// <summary>
        /// Exception-safe recomputation of the full correlation from the
        /// currently held graph, without throwing. Any corrupted or replaced
        /// value converges to <c>false</c>.
        /// </summary>
        internal bool IsValid => IsCorrelated(_issuedBy, _proof, _freezeReceipt, _writeReceipt);

        private static bool IsCorrelated(
            CaptureEvidenceRunPublicationCoordinator issuedBy,
            CaptureEvidenceRunPublicationCoordinator.IssuanceProof proof,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CapturePublicationPlanWriteReceipt writeReceipt)
        {
            if (issuedBy == null || proof == null || freezeReceipt == null || writeReceipt == null)
            {
                return false;
            }

            if (!issuedBy.IsMintedByThis(proof, freezeReceipt, writeReceipt))
            {
                return false;
            }

            if (!freezeReceipt.IsValid)
            {
                return false;
            }

            CaptureArtifactFileStore store = issuedBy.Store;
            if (store == null)
            {
                return false;
            }

            if (!ReferenceEquals(freezeReceipt.RootLayout, store.RootLayout))
            {
                return false;
            }

            CaptureRunInitializationSession session = freezeReceipt.RunSession;
            CaptureRunLockIdentityEvidence lockIdentityEvidence = freezeReceipt.LockIdentityEvidence;
            if (session == null || lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                return false;
            }

            if (!session.IsValid
                || session.TestRunId != lockIdentityEvidence.TestRunId
                || !ReferenceEquals(session.RootLayout, lockIdentityEvidence.RootLayout))
            {
                return false;
            }

            CaptureFrameDraftRegistry drafts = freezeReceipt.Drafts;
            CaptureArtifactRegistry artifacts = freezeReceipt.Artifacts;
            if (drafts == null || artifacts == null)
            {
                return false;
            }

            if (artifacts.ReservedArtifactCount != 0)
            {
                return false;
            }

            if (!ReferenceEquals(writeReceipt.IssuedBy, store))
            {
                return false;
            }

            CapturePublicationPlan plan = writeReceipt.Plan;
            if (plan == null || !plan.IsValid)
            {
                return false;
            }

            if (!writeReceipt.IsIssuedFor(store, plan))
            {
                return false;
            }

            if (plan.TestRunId != freezeReceipt.TestRunId
                || plan.TestRunId != store.RootLayout.TestRunId)
            {
                return false;
            }

            if (!string.Equals(plan.RunInitializationId, freezeReceipt.RunInitializationId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsLowerHex(plan.RunManifestContentHash, 64))
            {
                return false;
            }

            if (!string.Equals(writeReceipt.AbsolutePath, store.PublicationPlanPath, StringComparison.Ordinal))
            {
                return false;
            }

            if (writeReceipt.ByteCount <= 0)
            {
                return false;
            }

            CaptureRunLockPathSet pathSet = lockIdentityEvidence.LockPathSet;
            if (pathSet == null || !ReferenceEquals(pathSet.RootLayout, store.RootLayout))
            {
                return false;
            }

            return true;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
