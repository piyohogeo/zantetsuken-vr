using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects a validated capture-complete cleanup orchestration result to a
    /// completion notifier exactly once and freezes the accepted receipt into
    /// an immutable notification result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly one read-only dependency — the completion
    /// notifier — and holds no operation, receipt, result, or cleanup result in
    /// any field. It is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the fixed sequence exactly once per call:
    /// reject a null cleanup result, build the notification operation exactly
    /// once without re-validating the cleanup result beforehand, notify exactly
    /// once, and construct the immutable notification result whose constructor
    /// immediately correlates the receipt. Notifier exceptions propagate
    /// unchanged and unwrapped. The coordinator performs no retry, no
    /// re-cleanup, no re-inspection, no rollback, no disposal, no lease
    /// release, and no draft registry or filesystem contact.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteNotificationCoordinator
    {
        private readonly ICaptureRunPublicationCaptureCompleteNotifier _notifier;
        private readonly object _issuanceGate;

        internal CaptureRunPublicationCaptureCompleteNotificationCoordinator(
            ICaptureRunPublicationCaptureCompleteNotifier notifier)
        {
            if (notifier == null)
            {
                throw new ArgumentNullException(nameof(notifier));
            }

            _notifier = notifier;
            _issuanceGate = new object();
        }

        internal ICaptureRunPublicationCaptureCompleteNotifier Notifier => _notifier;

        /// <summary>
        /// Opaque proof minted only inside <see cref="Execute"/> after the
        /// notifier returned. It binds to this exact coordinator, to the
        /// coordinator's private issuance gate, and to the exact operation and
        /// receipt of that single notification, so the same coordinator's
        /// proof cannot be reused for a different notification and a proof
        /// cannot be minted without the coordinator's private gate.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly CaptureRunPublicationCaptureCompleteNotificationCoordinator _coordinator;
            private readonly object _gate;
            private readonly CaptureRunPublicationCaptureCompleteNotificationOperation _operation;
            private readonly CaptureRunPublicationCaptureCompleteNotificationReceipt _receipt;

            internal IssuanceProof(
                CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator,
                object gate,
                CaptureRunPublicationCaptureCompleteNotificationOperation operation,
                CaptureRunPublicationCaptureCompleteNotificationReceipt receipt)
            {
                _coordinator = coordinator;
                _gate = gate;
                _operation = operation;
                _receipt = receipt;
            }

            internal bool IsMintedFor(
                CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator,
                object gate,
                CaptureRunPublicationCaptureCompleteNotificationOperation operation,
                CaptureRunPublicationCaptureCompleteNotificationReceipt receipt)
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

        internal bool IsMintedByThis(
            IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation,
            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt)
        {
            return proof != null && proof.IsMintedFor(this, _issuanceGate, operation, receipt);
        }

        internal CaptureRunPublicationCaptureCompleteNotificationResult Execute(
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            if (cleanupResult == null)
            {
                throw new ArgumentNullException(nameof(cleanupResult));
            }

            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanupResult);

            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt = _notifier.Notify(operation);

            IssuanceProof proof = MintProof(operation, receipt);

            return new CaptureRunPublicationCaptureCompleteNotificationResult(this, proof, operation, receipt);
        }

        private IssuanceProof MintProof(
            CaptureRunPublicationCaptureCompleteNotificationOperation operation,
            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt)
        {
            return new IssuanceProof(this, _issuanceGate, operation, receipt);
        }
    }
}
