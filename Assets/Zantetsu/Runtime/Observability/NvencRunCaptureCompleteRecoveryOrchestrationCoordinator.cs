using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Joins the two healthy Capture Index recovery branches and runs the
    /// recovery CaptureComplete exactly once: a Run that still needs a commit
    /// commits first, a Run whose final index is already authoritative goes
    /// straight through, and a colliding Run is refused without touching
    /// either collaborator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds exactly two readonly dependencies, the Capture Index commit
    /// orchestration and the CaptureComplete execution coordinator, and is not
    /// an <see cref="IDisposable"/>. It owns no thread, queue, task, lease,
    /// filesystem, or buffer, keeps no decision, operation, or receipt in
    /// fields, and adds no result wrapper, status, receipt, issuer, token,
    /// nonce, or generation of its own.
    /// </para>
    /// <para>
    /// Each branch is built from its own authority. A commit's own success
    /// receipt becomes the CaptureComplete operation, never replaced by another
    /// receipt and never reconstructed from the decision alone; an already
    /// authoritative final index is passed on as itself, with no invented
    /// commit receipt. Only a null decision and a collision are rejected here,
    /// and the detailed admission - an invalid decision, one whose OS lock is
    /// gone, an undefined commit mode - is the existing operation factories'
    /// and the commit orchestration's, so it is not duplicated.
    /// </para>
    /// <para>
    /// Exceptions from the commit and from CaptureComplete propagate by the
    /// same reference; no commit, completion, or operation issuance is
    /// repeated, no disposition is guessed, and there is no retry latch or
    /// partial-progress result. In particular, when the Capture Index commit
    /// succeeded and CaptureComplete then failed, the commit is not retried
    /// inside this call: a final index may now exist on disk, so the caller
    /// re-inspects under the lock it still holds and proceeds next time as an
    /// already authoritative index. The input decision and its snapshot stay
    /// the record of the earlier inspection throughout.
    /// </para>
    /// <para>
    /// This unit stops at the CaptureComplete receipt: no cleanup, no staging,
    /// temporary, or marker removal, and no lock release.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOrchestrationCoordinator
    {
        private readonly NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator
            _captureIndexCommitOrchestration;

        private readonly NvencRunCaptureCompleteRecoveryExecutionCoordinator
            _captureCompleteExecution;

        internal NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
            NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator captureIndexCommitOrchestration,
            NvencRunCaptureCompleteRecoveryExecutionCoordinator captureCompleteExecution)
        {
            _captureIndexCommitOrchestration = captureIndexCommitOrchestration
                ?? throw new ArgumentNullException(nameof(captureIndexCommitOrchestration));
            _captureCompleteExecution = captureCompleteExecution
                ?? throw new ArgumentNullException(nameof(captureCompleteExecution));
        }

        internal NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator
            CaptureIndexCommitOrchestration => _captureIndexCommitOrchestration;

        internal NvencRunCaptureCompleteRecoveryExecutionCoordinator CaptureCompleteExecution =>
            _captureCompleteExecution;

        internal NvencRunCaptureCompleteRecoveryReceipt Execute(
            NvencRunCaptureIndexRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            NvencRunCaptureCompleteRecoveryOperation operation;
            switch (decision.Disposition)
            {
                case NvencRunCaptureIndexRecoveryDisposition.CommitRequired:
                    // The commit's own receipt is the authority for this
                    // branch; nothing else may stand in for it.
                    operation = NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(
                        _captureIndexCommitOrchestration.Execute(decision));
                    break;

                case NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired:
                    // Nothing was committed, so there is no commit receipt to
                    // carry and none is invented.
                    operation = NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                        decision);
                    break;

                default:
                    throw new ArgumentException(
                        "Only a committable or already authoritative Capture Index may reach CaptureComplete.",
                        nameof(decision));
            }

            return _captureCompleteExecution.Execute(operation);
        }
    }
}
