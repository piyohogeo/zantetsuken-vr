using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one accepted PngJson capture-complete owner
    /// release: the coordinator that issued it, the releaser it was routed
    /// through, the release operation it completed, and the receipt the
    /// releaser returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type holds exactly four readonly references — the issuing
    /// coordinator, the coordinator-bound issuance proof, the release
    /// operation, and the release receipt — and has no public constructor.
    /// It duplicates no ownership lease, lock identity evidence, lifecycle
    /// evidence, or run identity as a field; every accessor forwards a value
    /// from the held operation graph. The status is derived from
    /// <see cref="IsValid"/>, never stored.
    /// </para>
    /// <para>
    /// The atomic factory <see cref="Create"/> and <see cref="IsValid"/> share
    /// one correlation predicate of three steps: the coordinator, proof,
    /// operation, and receipt are non-null; the proof was minted by this exact
    /// coordinator for the exact releaser, operation, and receipt; and the
    /// receipt is issued for the coordinator's releaser and that exact
    /// operation. That last call is the authority — it settles the exact
    /// releaser, the exact operation, the issuance binding, the completed
    /// release, and the non-releasable terminal state together, so none of
    /// those are re-derived here. Because the release makes the upstream
    /// evidence invalid by design, the predicate intentionally never requires
    /// <c>operation.IsValid</c>, the lifecycle evidence's validity, or the lock
    /// identity evidence's validity.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteReleaseResult
    {
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof _proof;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseOperation _operation;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseReceipt _receipt;

        private PngJsonCapturePublicationCaptureCompleteReleaseResult(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            _issuedBy = issuedBy;
            _proof = proof;
            _operation = operation;
            _receipt = receipt;
        }

        /// <summary>
        /// Atomic issuance gate used only by the coordinator, and the single
        /// place the correlation is verified: it null-checks every input and
        /// then runs the shared predicate, so an uncorrelated receipt yields no
        /// result.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteReleaseResult Create(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (proof == null)
            {
                throw new ArgumentNullException(nameof(proof));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (!IsCorrelated(issuedBy, proof, operation, receipt))
            {
                throw new InvalidOperationException(
                    "Release receipt must be correlated with the issuing coordinator and operation.");
            }

            return new PngJsonCapturePublicationCaptureCompleteReleaseResult(issuedBy, proof, operation, receipt);
        }

        internal PngJsonCapturePublicationCaptureCompleteReleaseCoordinator IssuedBy => _issuedBy;

        internal IPngJsonCapturePublicationCaptureCompleteReleaser Releaser => _issuedBy.Releaser;

        internal PngJsonCapturePublicationCaptureCompleteReleaseOperation Operation => _operation;

        internal PngJsonCapturePublicationCaptureCompleteReleaseReceipt Receipt => _receipt;

        internal PngJsonCapturePublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _operation.LifecycleEvidence;

        internal PngJsonCapturePublicationCaptureCompleteNotificationResult NotificationResult => _operation.NotificationResult;

        internal CaptureRunPublicationCaptureCompleteLifecycleOwnerKind Kind => _operation.Kind;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _operation.LockIdentityEvidence;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal bool IsReleaseComplete => _operation.IsReleaseComplete;

        internal PngJsonCapturePublicationCaptureCompleteReleaseStatus Status
            => IsValid
                ? PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased
                : PngJsonCapturePublicationCaptureCompleteReleaseStatus.None;

        /// <summary>
        /// Exception-safe recomputation of the full post-release correlation
        /// from the currently held graph, without throwing. Any corrupted or
        /// replaced value converges to <c>false</c>.
        /// </summary>
        internal bool IsValid => IsCorrelated(_issuedBy, _proof, _operation, _receipt);

        /// <summary>
        /// Exception-safe correlation predicate shared by the issuance factory
        /// and <see cref="IsValid"/>. The receipt's own
        /// <c>IsIssuedFor</c> is the authority for the exact releaser, the
        /// exact operation, the issuance binding, the completed release, and
        /// the non-releasable terminal state, so none of those are re-derived
        /// here. Any forged or replaced binding converges to <c>false</c>.
        /// </summary>
        private static bool IsCorrelated(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            if (issuedBy == null || proof == null || operation == null || receipt == null)
            {
                return false;
            }

            if (!issuedBy.IsMintedByThis(proof, operation, receipt))
            {
                return false;
            }

            return receipt.IsIssuedFor(issuedBy.Releaser, operation);
        }
    }
}
