using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one accepted capture-complete notification: the
    /// coordinator that issued it, the notification operation it sent, and the
    /// receipt the notifier returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the issuing
    /// coordinator, the coordinator-bound issuance proof, the notification
    /// operation, and the notification receipt — and has no public constructor. Every accessor forwards a value from the
    /// held operation graph: the cleanup orchestration result, cleanup
    /// execution result, root layout, lock lease, test run id, run
    /// initialization id, run manifest content SHA-256, capture index path,
    /// disposition, and status are all forwarded rather than duplicated.
    /// </para>
    /// <para>
    /// The constructor and <see cref="IsValid"/> share one exception-safe
    /// correlation predicate. It re-checks that the coordinator, operation, and
    /// receipt are non-null, that the receipt was issued by the coordinator's
    /// notifier, that the receipt and operation reference the same operation,
    /// that the receipt still proves that exact operation through the single
    /// <c>IsIssuedFor</c> path (the one post-notification full validation),
    /// that every forwarded value matches between operation and receipt, that
    /// the status is <c>CaptureCompleteReady</c>, that the disposition is
    /// accepted, and that the lease is still live. Any forged, replaced, or
    /// released value converges to <c>false</c> without throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteNotificationResult
    {
        private readonly CaptureRunPublicationCaptureCompleteNotificationCoordinator _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof _proof;
        private readonly CaptureRunPublicationCaptureCompleteNotificationOperation _operation;
        private readonly CaptureRunPublicationCaptureCompleteNotificationReceipt _receipt;

        internal CaptureRunPublicationCaptureCompleteNotificationResult(
            CaptureRunPublicationCaptureCompleteNotificationCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation,
            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt)
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
                throw new ArgumentException(
                    "Notification receipt must be correlated with the issuing coordinator and operation.",
                    nameof(receipt));
            }

            _issuedBy = issuedBy;
            _proof = proof;
            _operation = operation;
            _receipt = receipt;
        }

        internal CaptureRunPublicationCaptureCompleteNotificationCoordinator IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof Proof => _proof;

        internal ICaptureRunPublicationCaptureCompleteNotifier Notifier => _issuedBy.Notifier;

        internal CaptureRunPublicationCaptureCompleteNotificationOperation Operation => _operation;

        internal CaptureRunPublicationCaptureCompleteNotificationReceipt Receipt => _receipt;

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _operation.CleanupResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _operation.ExecutionResult;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockLease LockLease => _operation.LockLease;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _operation.Disposition;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _operation.Status;

        /// <summary>
        /// Exception-safe recomputation of the full correlation from the
        /// currently held graph, without throwing. Any corrupted or replaced
        /// value converges to <c>false</c>.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return IsCorrelated(_issuedBy, _proof, _operation, _receipt);
            }
        }

        private static bool IsCorrelated(
            CaptureRunPublicationCaptureCompleteNotificationCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation,
            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt)
        {
            if (issuedBy == null || proof == null || operation == null || receipt == null)
            {
                return false;
            }

            // The proof must be minted for this exact coordinator, so a result
            // cannot be re-bound to a different coordinator that shares the
            // same notifier.
            if (!proof.IsMintedFor(issuedBy))
            {
                return false;
            }

            ICaptureRunPublicationCaptureCompleteNotifier notifier = issuedBy.Notifier;
            if (notifier == null)
            {
                return false;
            }

            if (!ReferenceEquals(receipt.IssuedBy, notifier)
                || !ReferenceEquals(receipt.Operation, operation))
            {
                return false;
            }

            // The single post-notification full validation path: this re-checks
            // the receipt issuer, the operation identity, and the operation's
            // full validity in one call, so the operation is never fully
            // validated a second time elsewhere in this predicate.
            if (!receipt.IsIssuedFor(notifier, operation))
            {
                return false;
            }

            if (!ReferenceEquals(operation.RootLayout, receipt.RootLayout)
                || !ReferenceEquals(operation.LockLease, receipt.LockLease))
            {
                return false;
            }

            if (operation.TestRunId != receipt.TestRunId
                || !string.Equals(operation.RunInitializationId, receipt.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(operation.RunManifestContentSha256, receipt.RunManifestContentSha256, StringComparison.Ordinal)
                || !string.Equals(operation.CaptureIndexPath, receipt.CaptureIndexPath, StringComparison.Ordinal)
                || operation.Disposition != receipt.Disposition
                || operation.Status != receipt.Status)
            {
                return false;
            }

            if (operation.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
            {
                return false;
            }

            if (!IsAcceptedDisposition(operation.Disposition))
            {
                return false;
            }

            CaptureRunLockLease lockLease = operation.LockLease;
            if (lockLease == null || !lockLease.IsCreated)
            {
                return false;
            }

            return true;
        }

        private static bool IsAcceptedDisposition(CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            return disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex
                || disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
        }
    }
}
