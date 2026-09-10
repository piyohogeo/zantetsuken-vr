using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the Phase 0.11 NVENC Capture Index recovery commit exactly
    /// once: from a classification that requires a commit it issues one commit
    /// operation, runs the configured commit execution coordinator once, and
    /// returns the exact receipt of that known success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds exactly one readonly dependency, the commit execution
    /// coordinator, and is not an <see cref="IDisposable"/>. It owns no thread,
    /// queue, task, lease, filesystem, or buffer, keeps no operation, receipt,
    /// or decision in fields, and adds no result wrapper, issuer, receipt,
    /// status, token, nonce, or generation of its own.
    /// </para>
    /// <para>
    /// Only a null input is rejected here; the detailed admission is the
    /// existing <see cref="NvencRunCaptureIndexRecoveryCommitOperation"/>
    /// constructor's, so an invalid decision, a CaptureComplete or collision
    /// disposition, a decision whose OS lock is gone, and an undefined commit
    /// mode are all refused before the committer is contacted. The execution
    /// coordinator already requires the receipt to be issued for that exact
    /// committer and operation, so that correlation is not reimplemented here.
    /// Inspector, committer, and execution coordinator exceptions propagate by
    /// the same reference; nothing is retried, re-issued, re-committed,
    /// re-inspected, or guessed.
    /// </para>
    /// <para>
    /// The decision and its snapshot stay the immutable record of the earlier
    /// inspection even after a successful commit: a changed filesystem is never
    /// written back into them as a new observation status. Nor is a second
    /// Execute with the same decision made to succeed - the final index now
    /// exists, so the production committer refuses it - and no idempotency
    /// latch or commit history is kept to pretend otherwise.
    /// </para>
    /// <para>
    /// This unit stops at a successful commit. It does not join the
    /// CaptureComplete branch, re-inspect anything, clean up, remove markers or
    /// staging, or release the OS lock.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator
    {
        private readonly NvencRunCaptureIndexRecoveryCommitExecutionCoordinator _commitExecution;

        internal NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
            NvencRunCaptureIndexRecoveryCommitExecutionCoordinator commitExecution)
        {
            _commitExecution = commitExecution
                ?? throw new ArgumentNullException(nameof(commitExecution));
        }

        internal NvencRunCaptureIndexRecoveryCommitExecutionCoordinator CommitExecution =>
            _commitExecution;

        internal NvencRunCaptureIndexRecoveryCommitReceipt Execute(
            NvencRunCaptureIndexRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            // Issuance is the whole admission check, and it has no side effect.
            NvencRunCaptureIndexRecoveryCommitOperation operation =
                new NvencRunCaptureIndexRecoveryCommitOperation(decision);

            // Exactly one commit attempt, and the receipt it produced.
            return _commitExecution.Execute(operation);
        }
    }
}
