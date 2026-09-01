using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects an issued release operation to a releaser exactly once and
    /// freezes the accepted receipt into an immutable release result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly two read-only fields: the release boundary
    /// and a private issuance gate. It holds no operation, receipt, result, or
    /// lifecycle evidence in any field and keeps no retry count, completion
    /// flag, or last result. It is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the fixed sequence exactly once per call:
    /// reject a null operation, reject an operation that is not currently
    /// releasable, hand the operation to the releaser exactly once, verify the
    /// returned receipt with the result's single correlation predicate, and
    /// return the result bound to this exact coordinator, releaser, operation,
    /// and receipt. A releaser or outcome disposal exception propagates
    /// unchanged and unwrapped; no result is produced and the operation is
    /// never modified, disposed, or destroyed. A partially released operation
    /// keeps the same instance and can be handed to the same coordinator again
    /// later.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator
    {
        private readonly ICaptureRunPublicationCaptureCompleteRecoveryReleaser _releaser;
        private readonly object _issuanceGate;

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser)
        {
            if (releaser == null)
            {
                throw new ArgumentNullException(nameof(releaser));
            }

            _releaser = releaser;
            _issuanceGate = new object();
        }

        internal ICaptureRunPublicationCaptureCompleteRecoveryReleaser Releaser => _releaser;

        /// <summary>
        /// Opaque proof minted only inside <see cref="Execute"/> after the
        /// releaser returned. It binds to this exact coordinator, to the
        /// coordinator's private issuance gate, and to the exact releaser,
        /// operation, and receipt of that single release, so the same
        /// coordinator's proof cannot be reused for a different release and a
        /// proof cannot be minted without the coordinator's private gate.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator _coordinator;
            private readonly object _gate;
            private readonly ICaptureRunPublicationCaptureCompleteRecoveryReleaser _releaser;
            private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation _operation;
            private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt _receipt;

            internal IssuanceProof(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator,
                object gate,
                ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser,
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation,
                CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt)
            {
                _coordinator = coordinator;
                _gate = gate;
                _releaser = releaser;
                _operation = operation;
                _receipt = receipt;
            }

            internal bool IsMintedFor(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator,
                object gate,
                ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser,
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation,
                CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt)
            {
                return coordinator != null
                    && gate != null
                    && releaser != null
                    && operation != null
                    && receipt != null
                    && ReferenceEquals(_coordinator, coordinator)
                    && ReferenceEquals(_gate, gate)
                    && ReferenceEquals(_releaser, releaser)
                    && ReferenceEquals(_operation, operation)
                    && ReferenceEquals(_receipt, receipt);
            }
        }

        internal bool IsMintedByThis(
            IssuanceProof proof,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt)
        {
            return proof != null
                && proof.IsMintedFor(this, _issuanceGate, _releaser, operation, receipt);
        }

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseResult Execute(
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.CanRelease)
            {
                throw new ArgumentException(
                    "Release operation must be currently releasable.",
                    nameof(operation));
            }

            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = _releaser.Release(operation);

            IssuanceProof proof = new IssuanceProof(this, _issuanceGate, _releaser, operation, receipt);

            if (!CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.IsCorrelated(this, proof, operation, receipt))
            {
                throw new InvalidOperationException(
                    "Releaser returned an invalid release receipt.");
            }

            return new CaptureRunPublicationCaptureCompleteRecoveryReleaseResult(this, proof, operation, receipt);
        }
    }
}
