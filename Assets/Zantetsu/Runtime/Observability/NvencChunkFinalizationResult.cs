using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Phase 0.11 Run chunk finalization result: the exact
    /// coordinator, its minted issuance proof, the exact operation, and the
    /// exact receipt. It owns no writer, buffer, lease, stream, hash state,
    /// token, or byte array, performs no file, hash, thread, or task
    /// operation, and is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Create"/> checks only the proof's exact binding and the
    /// receipt's reference correlation in O(1); the coordinator has already
    /// fully validated the receipt. <see cref="IsValid"/> re-verifies the same
    /// graph without throwing and stays true after a post-finalization poison.
    /// </remarks>
    internal sealed class NvencChunkFinalizationResult
    {
        private readonly NvencRunChunkFinalizationCoordinator _coordinator;
        private readonly NvencRunChunkFinalizationCoordinator.IssuanceProof _proof;
        private readonly NvencRunChunkFinalizationOperation _operation;
        private readonly NvencRunChunkFinalizationReceipt _receipt;

        private NvencChunkFinalizationResult(
            NvencRunChunkFinalizationCoordinator coordinator,
            NvencRunChunkFinalizationCoordinator.IssuanceProof proof,
            NvencRunChunkFinalizationOperation operation,
            NvencRunChunkFinalizationReceipt receipt)
        {
            _coordinator = coordinator;
            _proof = proof;
            _operation = operation;
            _receipt = receipt;
        }

        internal static NvencChunkFinalizationResult Create(
            NvencRunChunkFinalizationCoordinator coordinator,
            NvencRunChunkFinalizationCoordinator.IssuanceProof proof,
            NvencRunChunkFinalizationOperation operation,
            NvencRunChunkFinalizationReceipt receipt)
        {
            if (coordinator == null)
            {
                throw new ArgumentNullException(nameof(coordinator));
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

            if (!proof.IsMintedFor(coordinator, coordinator.Finalizer, operation, receipt))
            {
                throw new ArgumentException(
                    "Proof must be minted for the coordinator, operation, and receipt.", nameof(proof));
            }

            if (!ReferenceEquals(receipt.IssuedBy, coordinator.Finalizer) ||
                !ReferenceEquals(receipt.Operation, operation))
            {
                throw new ArgumentException(
                    "Receipt must correlate to the coordinator and operation.", nameof(receipt));
            }

            return new NvencChunkFinalizationResult(coordinator, proof, operation, receipt);
        }

        internal NvencRunChunkFinalizationCoordinator Coordinator => _coordinator;

        internal INvencRunChunkFinalizer Finalizer => _coordinator.Finalizer;

        internal NvencRunChunkFinalizationCoordinator.IssuanceProof Proof => _proof;

        internal NvencRunChunkFinalizationOperation Operation => _operation;

        internal NvencRunChunkFinalizationReceipt Receipt => _receipt;

        internal CaptureArtifactDescriptor Descriptor => _receipt.Descriptor;

        internal CaptureArtifactFrameRelation FrameRelation => _receipt.FrameRelation;

        internal NvencRunChunkSink Sink => _receipt.Sink;

        internal NvencRunChunkSinkFinalizationEvidence Evidence => _receipt.Evidence;

        internal string ArtifactId => _receipt.ArtifactId;

        internal CaptureArtifactKind ArtifactKind => _receipt.ArtifactKind;

        internal string FormatId => _receipt.FormatId;

        internal int FormatVersion => _receipt.FormatVersion;

        internal string StagingRelativePath => _receipt.StagingRelativePath;

        internal string FinalRelativePath => _receipt.FinalRelativePath;

        internal long ByteLength => _receipt.ByteLength;

        internal string ContentHash => _receipt.ContentHash;

        internal long AppendedCount => _receipt.AppendedCount;

        internal long LastFrameId => _receipt.LastFrameId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _coordinator != null
                        && _proof != null
                        && _operation != null
                        && _receipt != null
                        && _proof.IsMintedFor(_coordinator, _coordinator.Finalizer, _operation, _receipt)
                        && _receipt.IsIssuedFor(_coordinator.Finalizer, _operation);
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is ArgumentOutOfRangeException
                    || ex is InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }
}
