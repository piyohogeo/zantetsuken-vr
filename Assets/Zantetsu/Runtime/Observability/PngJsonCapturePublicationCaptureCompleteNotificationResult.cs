using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one accepted PngJson capture-complete notification:
    /// the coordinator that issued it, the coordinator-bound issuance proof,
    /// the notification operation it sent, and the receipt the notifier
    /// returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the issuing
    /// coordinator, the issuance proof, the notification operation, and the
    /// notification receipt — and has no public constructor. It holds no
    /// cleanup result, notification identity, lease, or token as a duplicate
    /// field; every accessor forwards a value from the held operation graph.
    /// </para>
    /// <para>
    /// The atomic factory and <see cref="IsValid"/> share one exception-safe
    /// correlation predicate. It re-checks that the coordinator, proof,
    /// operation, and receipt are non-null, that the proof was minted by the
    /// exact coordinator for the exact operation and receipt, that the receipt
    /// was issued by the coordinator's notifier, and that the receipt still
    /// proves that exact operation through the single <c>IsIssuedFor</c> path —
    /// which itself performs the operation's full current-state validation.
    /// The result therefore re-derives no plan, path set, manifest, status, or
    /// disposition of its own; that single post-notification validation is not
    /// duplicated.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteNotificationResult
    {
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof _proof;
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationOperation _operation;
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationReceipt _receipt;

        private PngJsonCapturePublicationCaptureCompleteNotificationResult(
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
        {
            _issuedBy = issuedBy;
            _proof = proof;
            _operation = operation;
            _receipt = receipt;
        }

        /// <summary>
        /// Atomic issuance gate used only by the coordinator after it already
        /// fully verified the receipt: null-checks every input and then runs
        /// the O(1) exact-binding predicate only — proof binding, coordinator
        /// notifier, receipt issuer, and receipt operation reference identity.
        /// It never re-runs <c>receipt.IsIssuedFor</c> or the operation's full
        /// validation on the success path.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteNotificationResult Create(
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
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

            if (!IsBoundO1(issuedBy, proof, operation, receipt))
            {
                throw new ArgumentException(
                    "Notification receipt must be correlated with the issuing coordinator and operation.",
                    nameof(receipt));
            }

            return new PngJsonCapturePublicationCaptureCompleteNotificationResult(issuedBy, proof, operation, receipt);
        }

        internal PngJsonCapturePublicationCaptureCompleteNotificationCoordinator IssuedBy => _issuedBy;

        internal IPngJsonCapturePublicationCaptureCompleteNotifier Notifier => _issuedBy.Notifier;

        internal PngJsonCapturePublicationCaptureCompleteNotificationOperation Operation => _operation;

        internal PngJsonCapturePublicationCaptureCompleteNotificationReceipt Receipt => _receipt;

        internal PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _operation.CleanupResult;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _operation.ExecutionResult;

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
        /// currently held graph, without throwing. It first runs the O(1)
        /// exact-binding predicate and then calls <c>receipt.IsIssuedFor</c>
        /// exactly once, which performs the operation's full current-state
        /// validation. Any corrupted or replaced value converges to
        /// <c>false</c>.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return IsFullyValid(_issuedBy, _proof, _operation, _receipt);
            }
        }

        /// <summary>
        /// O(1), exception-safe exact-binding predicate used by the issuance
        /// factory: non-null checks, proof binding, coordinator notifier, and
        /// the receipt issuer and operation reference identity only. It never
        /// calls <c>receipt.IsIssuedFor</c> or <c>operation.IsValid</c>, so the
        /// coordinator's immediate full receipt verification is not repeated on
        /// the success path.
        /// </summary>
        private static bool IsBoundO1(
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
        {
            try
            {
                if (issuedBy == null || proof == null || operation == null || receipt == null)
                {
                    return false;
                }

                // The proof must be minted by this exact coordinator for this
                // exact operation and receipt, so a result cannot be re-bound to
                // another coordinator, another notification, or a direct-minted
                // receipt.
                if (!issuedBy.IsMintedByThis(proof, operation, receipt))
                {
                    return false;
                }

                IPngJsonCapturePublicationCaptureCompleteNotifier notifier = issuedBy.Notifier;
                if (notifier == null)
                {
                    return false;
                }

                return ReferenceEquals(receipt.IssuedBy, notifier)
                    && ReferenceEquals(receipt.Operation, operation);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Exception-safe full validation predicate: runs the O(1) exact-binding
        /// predicate and then calls <c>receipt.IsIssuedFor</c> exactly once for
        /// the operation's full current-state validation.
        /// </summary>
        private static bool IsFullyValid(
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
        {
            try
            {
                if (!IsBoundO1(issuedBy, proof, operation, receipt))
                {
                    return false;
                }

                return receipt.IsIssuedFor(issuedBy.Notifier, operation);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
