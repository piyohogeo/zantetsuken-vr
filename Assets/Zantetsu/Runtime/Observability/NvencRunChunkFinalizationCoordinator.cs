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

            if (receipt == null || !receipt.IsIssuedFor(_finalizer, operation))
            {
                throw new InvalidOperationException("Finalizer returned an invalid receipt.");
            }

            return NvencChunkFinalizationResult.Create(this, operation, receipt);
        }
    }
}
