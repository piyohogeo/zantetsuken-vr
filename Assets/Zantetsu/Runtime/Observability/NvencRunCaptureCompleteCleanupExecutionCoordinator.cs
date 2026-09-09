using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run CaptureComplete cleanup execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureCompleteCleaner"/>, calls it exactly once per
    /// valid operation, and verifies the returned attempt result. It owns no
    /// thread or queue, performs no file, hash, thread, or task operation, and
    /// is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A cleaner exception propagates unchanged; the coordinator never repeats
    /// the call, never guesses another status, never converts a failure into a
    /// Failed result, and never issues a result after one. A null, invalid,
    /// foreign, default, or corrupt attempt result is rejected with
    /// <see cref="InvalidOperationException"/>. This layer touches no process
    /// state, Poison, Registry, disposition, Publication Service, lease, or
    /// file.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteCleanupExecutionCoordinator
    {
        private readonly INvencRunCaptureCompleteCleaner _cleaner;

        internal NvencRunCaptureCompleteCleanupExecutionCoordinator(
            INvencRunCaptureCompleteCleaner cleaner)
        {
            _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        }

        internal INvencRunCaptureCompleteCleaner Cleaner => _cleaner;

        internal NvencRunCaptureCompleteCleanupAttemptResult Execute(
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureCompleteCleanupAttemptResult attempt = _cleaner.Clean(operation);

            if (!IsValidAttempt(attempt, operation))
            {
                throw new InvalidOperationException(
                    "Cleaner returned a null, foreign, default, or corrupt attempt result.");
            }

            return attempt;
        }

        private bool IsValidAttempt(
            NvencRunCaptureCompleteCleanupAttemptResult attempt,
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            if (attempt.IsNone || !attempt.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(attempt.Cleaner, _cleaner)
                || !ReferenceEquals(attempt.Operation, operation))
            {
                return false;
            }

            switch (attempt.Status)
            {
                case NvencRunCaptureCompleteCleanupStatus.Cleaned:
                {
                    NvencRunCaptureCompleteCleanupReceipt receipt = attempt.Receipt;
                    return receipt != null
                        && receipt.IsIssuedFor(_cleaner, operation);
                }

                case NvencRunCaptureCompleteCleanupStatus.Failed:
                    return attempt.Receipt == null;

                default:
                    return false;
            }
        }
    }
}
