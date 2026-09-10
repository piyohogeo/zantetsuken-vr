using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Capture Index recovery commit execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureIndexRecoveryCommitter"/>, calls it exactly
    /// once per valid operation, and returns the exact receipt it produced.
    /// </summary>
    /// <remarks>
    /// A committer exception propagates unchanged; the coordinator never
    /// repeats the call, never fabricates a receipt, and never turns a failure
    /// into a success. A null receipt, one that is invalid, and one issued for
    /// another committer or another operation are rejected with
    /// <see cref="InvalidOperationException"/>. This layer touches no file,
    /// process state, Poison, Registry, disposition, service, or lock, owns no
    /// thread, queue, or task, and is not an <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryCommitExecutionCoordinator
    {
        private readonly INvencRunCaptureIndexRecoveryCommitter _committer;

        internal NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
            INvencRunCaptureIndexRecoveryCommitter committer)
        {
            _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        }

        internal INvencRunCaptureIndexRecoveryCommitter Committer => _committer;

        internal NvencRunCaptureIndexRecoveryCommitReceipt Execute(
            NvencRunCaptureIndexRecoveryCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureIndexRecoveryCommitReceipt receipt = _committer.Commit(operation);

            if (receipt == null || !receipt.IsIssuedFor(_committer, operation))
            {
                throw new InvalidOperationException(
                    "Committer returned a null, foreign, or invalid commit receipt.");
            }

            return receipt;
        }
    }
}
