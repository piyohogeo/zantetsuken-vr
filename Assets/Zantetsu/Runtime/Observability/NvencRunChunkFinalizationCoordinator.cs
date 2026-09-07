using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 Run chunk finalization coordinator. It holds
    /// exactly one <see cref="INvencRunChunkFinalizer"/>, calls it exactly once
    /// per valid operation, verifies the returned receipt, mints a
    /// coordinator-specific issuance proof, and issues a
    /// <see cref="NvencChunkFinalizationResult"/>. It owns no thread or queue,
    /// performs no file, hash, thread, or task operation, and is not an
    /// <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A finalizer exception propagates unchanged; the coordinator never
    /// repeats the finalize, never rebuilds the operation, and never issues a
    /// result after a failure.
    /// </remarks>
    internal sealed class NvencRunChunkFinalizationCoordinator
    {
        private static readonly object IssuanceGate = new object();

        private readonly INvencRunChunkFinalizer _finalizer;

        internal NvencRunChunkFinalizationCoordinator(INvencRunChunkFinalizer finalizer)
        {
            _finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));
        }

        internal INvencRunChunkFinalizer Finalizer => _finalizer;

        internal NvencChunkFinalizationResult Execute(
            NvencRunChunkFinalizationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunChunkFinalizationReceipt receipt = _finalizer.FinalizeChunk(operation);

            if (receipt == null ||
                !ReferenceEquals(receipt.IssuedBy, _finalizer) ||
                !ReferenceEquals(receipt.Operation, operation) ||
                !receipt.IsIssuedFor(_finalizer, operation))
            {
                throw new InvalidOperationException("Finalizer returned an invalid receipt.");
            }

            IssuanceProof proof = IssuanceProof.Mint(IssuanceGate, this, _finalizer, operation, receipt);
            return NvencChunkFinalizationResult.Create(this, proof, operation, receipt);
        }

        /// <summary>
        /// Coordinator-specific issuance proof. It binds the exact coordinator,
        /// the private issuance gate, the exact finalizer, operation, and
        /// receipt. Its constructor is private and only the coordinator calls
        /// the mint path; only the O(1) exception-safe
        /// <see cref="IsMintedFor"/> is exposed.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly NvencRunChunkFinalizationCoordinator _coordinator;
            private readonly object _issuanceGate;
            private readonly INvencRunChunkFinalizer _finalizer;
            private readonly NvencRunChunkFinalizationOperation _operation;
            private readonly NvencRunChunkFinalizationReceipt _receipt;

            private IssuanceProof(
                NvencRunChunkFinalizationCoordinator coordinator,
                object issuanceGate,
                INvencRunChunkFinalizer finalizer,
                NvencRunChunkFinalizationOperation operation,
                NvencRunChunkFinalizationReceipt receipt)
            {
                _coordinator = coordinator;
                _issuanceGate = issuanceGate;
                _finalizer = finalizer;
                _operation = operation;
                _receipt = receipt;
            }

            internal static IssuanceProof Mint(
                object issuanceGate,
                NvencRunChunkFinalizationCoordinator coordinator,
                INvencRunChunkFinalizer finalizer,
                NvencRunChunkFinalizationOperation operation,
                NvencRunChunkFinalizationReceipt receipt)
            {
                if (!ReferenceEquals(issuanceGate, NvencRunChunkFinalizationCoordinator.IssuanceGate))
                {
                    throw new ArgumentException(
                        "Issuance gate does not match.", nameof(issuanceGate));
                }

                return new IssuanceProof(
                    coordinator,
                    issuanceGate,
                    finalizer,
                    operation,
                    receipt);
            }

            internal bool IsMintedFor(
                NvencRunChunkFinalizationCoordinator coordinator,
                INvencRunChunkFinalizer finalizer,
                NvencRunChunkFinalizationOperation operation,
                NvencRunChunkFinalizationReceipt receipt)
            {
                return coordinator != null
                    && finalizer != null
                    && operation != null
                    && receipt != null
                    && ReferenceEquals(_coordinator, coordinator)
                    && ReferenceEquals(_issuanceGate, NvencRunChunkFinalizationCoordinator.IssuanceGate)
                    && ReferenceEquals(_finalizer, finalizer)
                    && ReferenceEquals(_operation, operation)
                    && ReferenceEquals(_receipt, receipt);
            }
        }
    }
}
