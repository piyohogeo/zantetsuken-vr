using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC recovery CaptureComplete cleanup execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureCompleteRecoveryCleaner"/>, calls it exactly
    /// once per valid operation, and returns the exact result it produced.
    /// </summary>
    /// <remarks>
    /// A cleaner exception propagates unchanged and is never turned into a
    /// Failed result; the coordinator never repeats the call and never
    /// fabricates a result or receipt. A default, invalid, or foreign result,
    /// an undefined status, a Cleaned result without its receipt, and a Failed
    /// result carrying one are all rejected with
    /// <see cref="InvalidOperationException"/>. This layer touches no
    /// filesystem, lock, Registry, disposition, or Service, owns no thread,
    /// queue, or task, and is not an <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator
    {
        private readonly INvencRunCaptureCompleteRecoveryCleaner _cleaner;

        internal NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
            INvencRunCaptureCompleteRecoveryCleaner cleaner)
        {
            _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        }

        internal INvencRunCaptureCompleteRecoveryCleaner Cleaner => _cleaner;

        internal NvencRunCaptureCompleteRecoveryCleanupAttemptResult Execute(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result = _cleaner.Clean(operation);

            if (!result.IsIssuedFor(_cleaner, operation))
            {
                throw new InvalidOperationException(
                    "Cleaner returned a default, foreign, or invalid cleanup result.");
            }

            if (result.IsCleaned)
            {
                NvencRunCaptureCompleteRecoveryCleanupReceipt receipt = result.Receipt;
                if (receipt == null || !receipt.IsIssuedFor(_cleaner, operation))
                {
                    throw new InvalidOperationException(
                        "A cleaned result must carry the receipt of this exact cleanup.");
                }
            }
            else if (result.Receipt != null)
            {
                throw new InvalidOperationException(
                    "A failed cleanup must not carry a receipt.");
            }

            return result;
        }
    }
}
