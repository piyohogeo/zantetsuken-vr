using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects an issued PngJson capture-complete release operation to the
    /// exact releaser exactly once and freezes the accepted receipt into an
    /// immutable release result.
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
    /// returned receipt is issued by this releaser for the exact operation,
    /// mint the coordinator-bound issuance proof, and hand everything to the
    /// result's atomic factory. A releaser exception propagates unchanged and
    /// unwrapped; no proof or result is produced and the operation is never
    /// modified, disposed, or destroyed. A partially released operation keeps
    /// the same instance and can be handed to the same coordinator again
    /// later.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteReleaseCoordinator
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteReleaser _releaser;
        private readonly object _issuanceGate;

        internal PngJsonCapturePublicationCaptureCompleteReleaseCoordinator(
            IPngJsonCapturePublicationCaptureCompleteReleaser releaser)
        {
            if (releaser == null)
            {
                throw new ArgumentNullException(nameof(releaser));
            }

            _releaser = releaser;
            _issuanceGate = new object();
        }

        internal IPngJsonCapturePublicationCaptureCompleteReleaser Releaser => _releaser;

        /// <summary>
        /// Opaque proof minted only inside <see cref="Execute"/> after the
        /// returned receipt was fully verified. It binds to this exact
        /// coordinator, to the coordinator's private issuance gate, and to the
        /// exact releaser, operation, and receipt of that single release, so
        /// the same coordinator's proof cannot be reused for a different
        /// release and a proof cannot be minted without the coordinator's
        /// private gate.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly PngJsonCapturePublicationCaptureCompleteReleaseCoordinator _coordinator;
            private readonly object _gate;
            private readonly IPngJsonCapturePublicationCaptureCompleteReleaser _releaser;
            private readonly PngJsonCapturePublicationCaptureCompleteReleaseOperation _operation;
            private readonly PngJsonCapturePublicationCaptureCompleteReleaseReceipt _receipt;

            private IssuanceProof(
                PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator,
                object gate,
                IPngJsonCapturePublicationCaptureCompleteReleaser releaser,
                PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
                PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
            {
                _coordinator = coordinator;
                _gate = gate;
                _releaser = releaser;
                _operation = operation;
                _receipt = receipt;
            }

            /// <summary>
            /// Atomic mint used only by the coordinator after the receipt was
            /// verified. The constructor is private, so a proof can only exist
            /// for a release routed through this exact coordinator.
            /// </summary>
            internal static IssuanceProof Mint(
                PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator,
                object gate,
                IPngJsonCapturePublicationCaptureCompleteReleaser releaser,
                PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
                PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
            {
                return new IssuanceProof(coordinator, gate, releaser, operation, receipt);
            }

            internal bool IsMintedFor(
                PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator,
                object gate,
                IPngJsonCapturePublicationCaptureCompleteReleaser releaser,
                PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
                PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
            {
                try
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
                catch (Exception)
                {
                    return false;
                }
            }
        }

        internal bool IsMintedByThis(
            IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            return proof != null
                && proof.IsMintedFor(this, _issuanceGate, _releaser, operation, receipt);
        }

        internal PngJsonCapturePublicationCaptureCompleteReleaseResult Execute(
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation)
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

            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = _releaser.Release(operation);

            if (receipt == null
                || !ReferenceEquals(receipt.IssuedBy, _releaser)
                || !ReferenceEquals(receipt.Operation, operation)
                || !receipt.IsIssuedFor(_releaser, operation))
            {
                throw new InvalidOperationException(
                    "Release receipt must be issued for this releaser and operation.");
            }

            IssuanceProof proof = IssuanceProof.Mint(this, _issuanceGate, _releaser, operation, receipt);

            return PngJsonCapturePublicationCaptureCompleteReleaseResult.Create(this, proof, operation, receipt);
        }
    }
}
