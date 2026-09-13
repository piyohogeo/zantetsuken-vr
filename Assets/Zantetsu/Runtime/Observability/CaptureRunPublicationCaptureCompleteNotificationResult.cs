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
    /// operation, and the notification receipt — and has no public
    /// constructor. Every accessor forwards a value from the held operation
    /// graph: the cleanup orchestration result, cleanup execution result, root
    /// layout, lock lease, test run id, run initialization id, run manifest
    /// content SHA-256, capture index path, disposition, and status are all
    /// forwarded rather than duplicated.
    /// </para>
    /// <para>
    /// The constructor and <see cref="IsValid"/> share one exception-safe
    /// correlation predicate: the coordinator, proof, operation, and receipt
    /// are non-null, the proof was minted by this exact coordinator for this
    /// exact operation and receipt, and the receipt is issued for the
    /// coordinator's notifier and that exact operation. That last call is the
    /// one post-notification full validation — it settles the notifier, the
    /// operation identity, and the operation's whole current validity, so
    /// nothing here re-derives the status, the disposition, or the lease. Any
    /// forged, replaced, or released value converges to <c>false</c> without
    /// throwing.
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

        internal ICaptureRunPublicationCaptureCompleteNotifier Notifier => _issuedBy.Notifier;

        internal CaptureRunPublicationCaptureCompleteNotificationOperation Operation => _operation;

        internal CaptureRunPublicationCaptureCompleteNotificationReceipt Receipt => _receipt;

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _operation.CleanupResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _operation.ExecutionResult;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _operation.LockIdentityEvidence;

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

            // The proof must be minted by this exact coordinator for this exact
            // operation and receipt, so a result cannot be re-bound to another
            // coordinator, another notification, or a direct-minted receipt.
            if (!issuedBy.IsMintedByThis(proof, operation, receipt))
            {
                return false;
            }

            // The single post-notification full validation path: one call
            // settles the exact notifier, the exact operation, and that
            // operation's whole current validity, so nothing here re-derives
            // its status, disposition, or lease.
            return receipt.IsIssuedFor(issuedBy.Notifier, operation);
        }
    }
}
