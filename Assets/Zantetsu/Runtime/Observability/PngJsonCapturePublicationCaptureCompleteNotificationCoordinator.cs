using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects a PngJson capture-complete cleanup orchestration result to a
    /// notification operation, notifies exactly once, verifies the returned
    /// receipt immediately, and freezes the accepted receipt into an immutable
    /// notification result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly one read-only dependency — the notifier —
    /// plus one private issuance authority, and holds no operation, receipt,
    /// result, or cleanup result in any field. It is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the fixed sequence exactly once per call:
    /// reject a null cleanup result, build the notification operation exactly
    /// once through the operation factory (the single input validation
    /// boundary), notify exactly once, verify the returned receipt immediately,
    /// mint the coordinator-bound issuance proof only after that verification
    /// succeeds, and issue the immutable notification result. Notifier
    /// exceptions propagate unchanged and unwrapped. The coordinator performs
    /// no retry, no re-notification, no operation rebuild, no re-cleanup, no
    /// rollback or compensation, no disposal, no lease release, and no draft
    /// registry or filesystem contact.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteNotificationCoordinator
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteNotifier _notifier;
        private readonly object _issuanceGate;

        internal PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(
            IPngJsonCapturePublicationCaptureCompleteNotifier notifier)
        {
            if (notifier == null)
            {
                throw new ArgumentNullException(nameof(notifier));
            }

            _notifier = notifier;
            _issuanceGate = new object();
        }

        internal IPngJsonCapturePublicationCaptureCompleteNotifier Notifier => _notifier;

        /// <summary>
        /// Opaque proof minted only inside <see cref="Execute"/> after the
        /// notifier returned and its receipt passed verification. It binds to
        /// this exact coordinator, to the coordinator's private issuance
        /// authority, and to the exact operation and receipt of that single
        /// notification, so the proof cannot be reused for a different
        /// notification and cannot be minted without the coordinator's private
        /// authority. The constructor is private; the coordinator is the only
        /// minting site.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly PngJsonCapturePublicationCaptureCompleteNotificationCoordinator _coordinator;
            private readonly object _gate;
            private readonly PngJsonCapturePublicationCaptureCompleteNotificationOperation _operation;
            private readonly PngJsonCapturePublicationCaptureCompleteNotificationReceipt _receipt;

            private IssuanceProof(
                PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator,
                object gate,
                PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
            {
                _coordinator = coordinator;
                _gate = gate;
                _operation = operation;
                _receipt = receipt;
            }

            /// <summary>
            /// Single minting path: only the containing coordinator invokes
            /// this with its private issuance authority, so a valid proof
            /// cannot be created without that authority. The constructor is
            /// private.
            /// </summary>
            internal static IssuanceProof Mint(
                PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator,
                object gate,
                PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
            {
                return new IssuanceProof(coordinator, gate, operation, receipt);
            }

            internal bool IsMintedFor(
                PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator,
                object gate,
                PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
            {
                return coordinator != null
                    && gate != null
                    && operation != null
                    && receipt != null
                    && ReferenceEquals(_coordinator, coordinator)
                    && ReferenceEquals(_gate, gate)
                    && ReferenceEquals(_operation, operation)
                    && ReferenceEquals(_receipt, receipt);
            }
        }

        /// <summary>
        /// O(1), exception-safe check that this proof was minted by this exact
        /// coordinator with its private issuance authority for the exact
        /// operation and receipt. A foreign coordinator, another operation, or
        /// another receipt fails.
        /// </summary>
        internal bool IsMintedByThis(
            IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation,
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt)
        {
            return proof != null && proof.IsMintedFor(this, _issuanceGate, operation, receipt);
        }

        internal PngJsonCapturePublicationCaptureCompleteNotificationResult Execute(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            if (cleanupResult == null)
            {
                throw new ArgumentNullException(nameof(cleanupResult));
            }

            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                PngJsonCapturePublicationCaptureCompleteNotificationOperationFactory.Build(cleanupResult);

            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = _notifier.Notify(operation);

            VerifyReceipt(receipt, operation);

            IssuanceProof proof = IssuanceProof.Mint(this, _issuanceGate, operation, receipt);

            return PngJsonCapturePublicationCaptureCompleteNotificationResult.Create(this, proof, operation, receipt);
        }

        private void VerifyReceipt(
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt,
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
        {
            if (receipt == null
                || !ReferenceEquals(receipt.IssuedBy, _notifier)
                || !ReferenceEquals(receipt.Operation, operation)
                || !receipt.IsIssuedFor(_notifier, operation))
            {
                throw new InvalidOperationException(
                    "Notification receipt must be issued by this coordinator's notifier for the notification operation.");
            }
        }
    }
}
