using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one accepted capture-complete recovery owner
    /// release: the coordinator that issued it, the releaser it was routed
    /// through, the release operation it completed, and the receipt the
    /// releaser returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the issuing
    /// coordinator, the coordinator-bound issuance proof, the release
    /// operation, and the release receipt — and has no public constructor.
    /// Every accessor forwards a value from the held graph: the releaser,
    /// lifecycle evidence, notification result, open outcome, lock lease, root
    /// layout, test run id, run initialization id, run manifest content
    /// SHA-256, and capture index path are all forwarded rather than
    /// duplicated. The status is mapped from <see cref="IsValid"/>, never
    /// stored and never derived from the terminal state alone, so a corrupted
    /// proof, coordinator, or receipt converges to <c>None</c>.
    /// </para>
    /// <para>
    /// <see cref="Create"/> and <see cref="IsValid"/> share one exception-safe
    /// correlation predicate. It re-checks that the coordinator, proof,
    /// operation, and receipt are non-null, that the proof was minted by this
    /// exact coordinator for the exact releaser, operation, and receipt, that
    /// the receipt was issued by the coordinator's releaser and still proves
    /// the exact operation, that the operation's issuance proof is intact,
    /// that the exact outcome and lock lease are no longer created, and that
    /// every forwarded value matches between receipt and operation. Any
    /// forged, replaced, or released value converges to <c>false</c> without
    /// throwing. The upstream evidence, notification result, and operation are
    /// intentionally not re-validated here, because a completed release makes
    /// them invalid by design.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteRecoveryReleaseResult
    {
        private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof _proof;
        private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation _operation;
        private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt _receipt;

        /// <summary>
        /// Private assignment constructor: stores the already-validated graph
        /// without re-checking it. The only construction path is
        /// <see cref="Create"/>, which validates once before assigning.
        /// </summary>
        private CaptureRunPublicationCaptureCompleteRecoveryReleaseResult(
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt)
        {
            _issuedBy = issuedBy;
            _proof = proof;
            _operation = operation;
            _receipt = receipt;
        }

        /// <summary>
        /// Atomic validated factory: the single validation-and-assignment
        /// site. It rejects null structural arguments with
        /// <see cref="ArgumentNullException"/> and any uncorrelated graph —
        /// including a null, foreign, or unreleased receipt — with
        /// <see cref="InvalidOperationException"/>, then assigns fields exactly
        /// once.
        /// </summary>
        internal static CaptureRunPublicationCaptureCompleteRecoveryReleaseResult Create(
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt)
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

            if (!IsCorrelated(issuedBy, proof, operation, receipt))
            {
                throw new InvalidOperationException(
                    "Release receipt must be correlated with the issuing coordinator and operation.");
            }

            return new CaptureRunPublicationCaptureCompleteRecoveryReleaseResult(issuedBy, proof, operation, receipt);
        }

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator IssuedBy => _issuedBy;

        internal ICaptureRunPublicationCaptureCompleteRecoveryReleaser Releaser => _issuedBy.Releaser;

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation Operation => _operation;

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Receipt => _receipt;

        internal CaptureRunPublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _operation.LifecycleEvidence;

        internal CaptureRunPublicationCaptureCompleteNotificationResult NotificationResult => _operation.NotificationResult;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _operation.OpenOutcome;

        internal CaptureRunLockLease LockLease => _operation.LockLease;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus Status
            => IsValid
                ? CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.RecoveryOwnerReleased
                : CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None;

        /// <summary>
        /// Exception-safe recomputation of the full correlation from the
        /// currently held graph, without throwing. Any corrupted or replaced
        /// value converges to <c>false</c>.
        /// </summary>
        internal bool IsValid => IsCorrelated(_issuedBy, _proof, _operation, _receipt);

        internal static bool IsCorrelated(
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt)
        {
            if (coordinator == null || proof == null || operation == null || receipt == null)
            {
                return false;
            }

            // The proof must be minted by this exact coordinator for this exact
            // releaser, operation, and receipt, so a result cannot be re-bound
            // to another coordinator, another releaser, another operation, or a
            // direct-minted receipt.
            if (!coordinator.IsMintedByThis(proof, operation, receipt))
            {
                return false;
            }

            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = coordinator.Releaser;
            if (releaser == null)
            {
                return false;
            }

            if (!ReferenceEquals(receipt.IssuedBy, releaser)
                || !ReferenceEquals(receipt.Operation, operation))
            {
                return false;
            }

            // The single post-release full validation path: this re-checks the
            // receipt issuer, the operation identity, and the operation's
            // issuance proof and terminal state in one call.
            if (!receipt.IsIssuedFor(releaser, operation))
            {
                return false;
            }

            if (!operation.IsIssuanceProofIntact)
            {
                return false;
            }

            CaptureRunInitializationOpenOutcome openOutcome = operation.OpenOutcome;
            CaptureRunLockLease lockLease = operation.LockLease;
            if (openOutcome == null || lockLease == null || openOutcome.IsCreated || lockLease.IsCreated)
            {
                return false;
            }

            if (!ReferenceEquals(receipt.OpenOutcome, openOutcome)
                || !ReferenceEquals(receipt.LockLease, lockLease)
                || !ReferenceEquals(receipt.LifecycleEvidence, operation.LifecycleEvidence)
                || !ReferenceEquals(receipt.NotificationResult, operation.NotificationResult)
                || !ReferenceEquals(receipt.RootLayout, operation.RootLayout)
                || receipt.TestRunId != operation.TestRunId
                || !string.Equals(receipt.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(receipt.RunManifestContentSha256, operation.RunManifestContentSha256, StringComparison.Ordinal)
                || !string.Equals(receipt.CaptureIndexPath, operation.CaptureIndexPath, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
    }
}
