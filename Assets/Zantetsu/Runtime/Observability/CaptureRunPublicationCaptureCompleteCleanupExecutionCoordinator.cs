using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Executes a prepared capture-complete cleanup execution batch in
    /// ascending step order and verifies each backend receipt immediately after
    /// the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator holds exactly one readonly dependency — the cleanup
    /// backend — and is not an <see cref="IDisposable"/>. <see cref="Execute"/>
    /// validates the batch once, then processes each prepared step once,
    /// verifies the returned receipt before proceeding, and returns a result
    /// only after every step succeeded. Each side-effecting action contacts the
    /// backend exactly once; the routing <c>CaptureCompleteReady</c> action
    /// contacts no backend and produces no receipt.
    /// </para>
    /// <para>
    /// A backend exception propagates unchanged: the coordinator performs no
    /// retry, no rollback, no compensating deletion, no cleanup, and no
    /// automatic re-inspection, and it never disposes the backend, batch, plan,
    /// result, or lock lease. A failed execution returns no result and skips
    /// all remaining steps. Because a backend call may have partially succeeded
    /// on the filesystem before throwing, the caller must not blindly re-run
    /// the same batch; it must re-inspect under the held lock and build a new
    /// decision, plan, and batch.
    /// </para>
    /// <para>
    /// A contract-violating receipt — null, issued by a foreign backend, bound
    /// to a different operation, or whose forwarded values disagree with the
    /// operation — throws <see cref="InvalidOperationException"/> and also
    /// skips the remaining steps.
    /// </para>
    /// <para>
    /// This coordinator does not release the draft registry, send the
    /// capture-complete notification, release or transfer the lock lease, or
    /// generate any session or outcome; it only returns the
    /// <c>CaptureCompleteReady</c> execution result.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator
    {
        private readonly ICaptureRunPublicationCaptureCompleteCleanupBackend _backend;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(
            ICaptureRunPublicationCaptureCompleteCleanupBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            _backend = backend;
        }

        internal ICaptureRunPublicationCaptureCompleteCleanupBackend Backend => _backend;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult Execute(
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (!batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken))
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = batchToken.ActionPlanToken;

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps =
                new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[batch.Count];

            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);

                CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = null;
                if (prepared.Action != CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    receipt = _backend.Execute(prepared.CleanupOperation);
                    VerifyReceipt(receipt, prepared, token);
                }

                completedSteps[i] = new CaptureRunPublicationCaptureCompleteCleanupCompletedStep(
                    prepared, receipt, token);
            }

            return new CaptureRunPublicationCaptureCompleteCleanupExecutionResult(this, batch, completedSteps, token);
        }

        private void VerifyReceipt(
            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt,
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            CaptureRunPublicationCaptureCompleteCleanupOperation operation = prepared.CleanupOperation;

            if (receipt == null
                || !ReferenceEquals(receipt.IssuedBy, _backend)
                || !ReferenceEquals(receipt.Operation, operation)
                || !receipt.IsIssuedForIndexLocal(_backend, operation, token))
            {
                throw new InvalidOperationException("Cleanup receipt must be issued by this backend for this cleanup operation.");
            }

            if (receipt.Action != prepared.Action
                || receipt.StepIndex != prepared.StepIndex
                || receipt.EntryIndex != operation.EntryIndex
                || receipt.ArtifactKind != operation.ArtifactKind
                || !string.Equals(receipt.TargetPath, operation.TargetPath, StringComparison.Ordinal)
                || !ReferenceEquals(receipt.ActionPlan, operation.ActionPlan)
                || !ReferenceEquals(receipt.RootLayout, operation.RootLayout)
                || !ReferenceEquals(receipt.LockLease, operation.LockLease)
                || receipt.TestRunId != operation.TestRunId
                || !string.Equals(receipt.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cleanup receipt must match the cleanup operation.");
            }
        }
    }
}
