using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC recovery CaptureComplete execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureCompleteRecoveryCompleter"/>, calls it exactly
    /// once per valid operation, and returns the exact receipt it produced.
    /// </summary>
    /// <remarks>
    /// A completer exception propagates unchanged; the coordinator never
    /// repeats the call, never fabricates a receipt, and never turns a failure
    /// into a success. A null receipt, one that is invalid, and one issued for
    /// another completer or another operation are rejected with
    /// <see cref="InvalidOperationException"/>. Both authorities - an already
    /// authoritative final Capture Index and a recovery-committed one - go
    /// through this same call, and which one it was stays inside the operation.
    /// This layer touches no process state, Poison, filesystem, Registry,
    /// disposition, Service, or lock, owns no thread, queue, or task, and is
    /// not an <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryExecutionCoordinator
    {
        private readonly INvencRunCaptureCompleteRecoveryCompleter _completer;

        internal NvencRunCaptureCompleteRecoveryExecutionCoordinator(
            INvencRunCaptureCompleteRecoveryCompleter completer)
        {
            _completer = completer ?? throw new ArgumentNullException(nameof(completer));
        }

        internal INvencRunCaptureCompleteRecoveryCompleter Completer => _completer;

        internal NvencRunCaptureCompleteRecoveryReceipt Execute(
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureCompleteRecoveryReceipt receipt = _completer.Complete(operation);

            if (receipt == null || !receipt.IsIssuedFor(_completer, operation))
            {
                throw new InvalidOperationException(
                    "Completer returned a null, foreign, or invalid CaptureComplete receipt.");
            }

            return receipt;
        }
    }
}
