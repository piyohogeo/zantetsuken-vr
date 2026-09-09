using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run CaptureComplete execution coordinator.
    /// It holds exactly one <see cref="INvencRunCaptureCompleter"/>, calls it
    /// exactly once per valid operation, and verifies the returned attempt
    /// result. It owns no thread or queue, performs no file, hash, thread, or
    /// task operation, and is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A completer exception propagates unchanged; the coordinator never
    /// repeats the call, never guesses another status, and never issues a
    /// result after a failure. A null, invalid, foreign, default, or corrupt
    /// attempt result is rejected with <see cref="InvalidOperationException"/>.
    /// This layer touches no process state, Registry, disposition, Publication
    /// Service, artifact store, filesystem, canonical bytes, hash, lease, or
    /// cleanup, and does not poison the process; linearizing with the Poison
    /// transition is the later Service integration's responsibility.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteExecutionCoordinator
    {
        private readonly INvencRunCaptureCompleter _completer;

        internal NvencRunCaptureCompleteExecutionCoordinator(
            INvencRunCaptureCompleter completer)
        {
            _completer = completer ?? throw new ArgumentNullException(nameof(completer));
        }

        internal INvencRunCaptureCompleter Completer => _completer;

        internal NvencRunCaptureCompleteAttemptResult Execute(
            NvencRunCaptureCompleteOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureCompleteAttemptResult attempt = _completer.Complete(operation);

            if (!IsValidAttempt(attempt, operation))
            {
                throw new InvalidOperationException(
                    "Completer returned a null, foreign, default, or corrupt attempt result.");
            }

            return attempt;
        }

        private bool IsValidAttempt(
            NvencRunCaptureCompleteAttemptResult attempt,
            NvencRunCaptureCompleteOperation operation)
        {
            if (attempt.IsNone || !attempt.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(attempt.Completer, _completer)
                || !ReferenceEquals(attempt.Operation, operation))
            {
                return false;
            }

            switch (attempt.Status)
            {
                case NvencRunCaptureCompleteStatus.Completed:
                {
                    NvencRunCaptureCompleteReceipt receipt = attempt.Receipt;
                    return receipt != null
                        && receipt.IsIssuedFor(_completer, operation);
                }

                case NvencRunCaptureCompleteStatus.Failed:
                    return attempt.Receipt == null;

                default:
                    return false;
            }
        }
    }
}
