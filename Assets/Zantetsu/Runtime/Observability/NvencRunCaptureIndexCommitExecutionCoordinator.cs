using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run capture index commit execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureIndexCommitter"/>, calls it exactly once per
    /// valid operation, and verifies the returned attempt result. It owns no
    /// thread or queue, performs no file, hash, thread, or task operation, and
    /// is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A committer exception propagates unchanged; the coordinator never
    /// repeats the commit, never guesses another status, and never issues a
    /// result after a failure. A null, invalid, foreign, default, or corrupt
    /// attempt result is rejected with <see cref="InvalidOperationException"/>.
    /// This layer touches no process state, Registry, disposition, Publication
    /// Service, or file, and does not poison the process; the future
    /// Publication Service and <see cref="NvencCaptureRunCoordinator"/> own
    /// thread and fatal policy.
    /// </remarks>
    internal sealed class NvencRunCaptureIndexCommitExecutionCoordinator
    {
        private readonly INvencRunCaptureIndexCommitter _committer;

        internal NvencRunCaptureIndexCommitExecutionCoordinator(
            INvencRunCaptureIndexCommitter committer)
        {
            _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        }

        internal INvencRunCaptureIndexCommitter Committer => _committer;

        internal NvencRunCaptureIndexCommitAttemptResult Execute(
            NvencRunCaptureIndexCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureIndexCommitAttemptResult attempt = _committer.Commit(operation);

            if (!IsValidAttempt(attempt, operation))
            {
                throw new InvalidOperationException(
                    "Committer returned a null, foreign, default, or corrupt attempt result.");
            }

            return attempt;
        }

        private bool IsValidAttempt(
            NvencRunCaptureIndexCommitAttemptResult attempt,
            NvencRunCaptureIndexCommitOperation operation)
        {
            if (attempt.IsNone || !attempt.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(attempt.Committer, _committer)
                || !ReferenceEquals(attempt.Operation, operation))
            {
                return false;
            }

            switch (attempt.Status)
            {
                case NvencRunCaptureIndexCommitStatus.Committed:
                {
                    NvencRunCaptureIndexCommitReceipt receipt = attempt.Receipt;
                    return receipt != null
                        && receipt.IsIssuedFor(_committer, operation);
                }

                case NvencRunCaptureIndexCommitStatus.Failed:
                    return attempt.Receipt == null;

                default:
                    return false;
            }
        }
    }
}
