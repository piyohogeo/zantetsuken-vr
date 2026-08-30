using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Executes a prepared publication artifact recovery execution batch in
    /// ascending step order and verifies each backend receipt immediately after
    /// the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator holds exactly two readonly dependencies — the publisher
    /// and the capture index committer — and is not an <see cref="IDisposable"/>.
    /// <see cref="Execute"/> processes each prepared step once, verifies the
    /// returned receipt before proceeding, and returns a result only after every
    /// step succeeded. Publish and commit actions contact their backend exactly
    /// once; routing and stop actions contact neither backend and produce no
    /// receipt.
    /// </para>
    /// <para>
    /// A backend exception propagates unchanged: the coordinator performs no
    /// retry, no rollback, no compensating deletion, no cleanup, and no
    /// automatic re-inspection, and it never disposes the lock lease, the
    /// publisher, or the committer. It never mutates the batch, plan, decision,
    /// snapshot, operation, or canonical bytes. A failed execution returns no
    /// result and skips all remaining steps.
    /// </para>
    /// <para>
    /// Because a publish or commit call may have partially succeeded on the
    /// filesystem before throwing, the caller must not blindly re-run the same
    /// batch; it must re-inspect under the held lock.
    /// </para>
    /// <para>
    /// A contract-violating receipt — null, issued by a foreign backend, bound
    /// to a different operation, or whose forwarded values disagree with the
    /// operation — throws <see cref="InvalidOperationException"/> and also
    /// skips the remaining steps.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryExecutionCoordinator
    {
        private readonly ICaptureRunPublicationArtifactPublisher _publisher;
        private readonly ICaptureRunCaptureIndexCommitter _captureIndexCommitter;

        internal CaptureRunPublicationArtifactRecoveryExecutionCoordinator(
            ICaptureRunPublicationArtifactPublisher publisher,
            ICaptureRunCaptureIndexCommitter captureIndexCommitter)
        {
            if (publisher == null)
            {
                throw new ArgumentNullException(nameof(publisher));
            }

            if (captureIndexCommitter == null)
            {
                throw new ArgumentNullException(nameof(captureIndexCommitter));
            }

            _publisher = publisher;
            _captureIndexCommitter = captureIndexCommitter;
        }

        internal ICaptureRunPublicationArtifactPublisher Publisher => _publisher;

        internal ICaptureRunCaptureIndexCommitter CaptureIndexCommitter => _captureIndexCommitter;

        internal CaptureRunPublicationArtifactRecoveryExecutionResult Execute(
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (!batch.IsValid)
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = batch.ActionPlan;
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
            try
            {
                token = actionPlan.AcquireValidationToken();
            }
            catch (InvalidOperationException)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(batch));
            }

            CaptureRunPublicationArtifactRecoveryCompletedStep[] completedSteps =
                new CaptureRunPublicationArtifactRecoveryCompletedStep[batch.Count];

            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunPublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(i);

                CaptureRunPublicationArtifactPublishReceipt publishReceipt = null;
                CaptureRunCaptureIndexCommitReceipt commitReceipt = null;

                switch (prepared.Action)
                {
                    case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                        publishReceipt = _publisher.Publish(prepared.PublishOperation);
                        VerifyPublishReceipt(publishReceipt, prepared.PublishOperation, token);
                        break;

                    case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                        commitReceipt = _captureIndexCommitter.Commit(prepared.CaptureIndexCommitOperation);
                        VerifyCommitReceipt(commitReceipt, prepared.CaptureIndexCommitOperation, token);
                        break;
                }

                completedSteps[i] = new CaptureRunPublicationArtifactRecoveryCompletedStep(
                    prepared, publishReceipt, commitReceipt, token);
            }

            return new CaptureRunPublicationArtifactRecoveryExecutionResult(this, batch, completedSteps);
        }

        private void VerifyPublishReceipt(
            CaptureRunPublicationArtifactPublishReceipt receipt,
            CaptureRunPublicationArtifactPublishOperation operation,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (receipt == null
                || !ReferenceEquals(receipt.IssuedBy, _publisher)
                || !ReferenceEquals(receipt.Operation, operation)
                || !operation.IsValidIndexLocal(token))
            {
                throw new InvalidOperationException("Publish receipt must be issued by this publisher for this publish operation.");
            }

            if (receipt.EntryIndex != operation.EntryIndex
                || receipt.ArtifactKind != operation.ArtifactKind
                || receipt.CaptureFrameId != operation.CaptureFrameId
                || !string.Equals(receipt.SourcePath, operation.SourcePath, StringComparison.Ordinal)
                || !string.Equals(receipt.DestinationPath, operation.DestinationPath, StringComparison.Ordinal)
                || receipt.ExpectedByteCount != operation.ExpectedByteCount
                || !string.Equals(receipt.ExpectedContentSha256, operation.ExpectedContentSha256, StringComparison.Ordinal)
                || !ReferenceEquals(receipt.RootLayout, operation.RootLayout)
                || receipt.TestRunId != operation.TestRunId
                || !string.Equals(receipt.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Publish receipt must match the publish operation.");
            }
        }

        private void VerifyCommitReceipt(
            CaptureRunCaptureIndexCommitReceipt receipt,
            CaptureRunCaptureIndexCommitOperation operation,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (receipt == null
                || !ReferenceEquals(receipt.IssuedBy, _captureIndexCommitter)
                || !ReferenceEquals(receipt.Operation, operation)
                || !operation.IsValidIndexLocal(token))
            {
                throw new InvalidOperationException("Commit receipt must be issued by this committer for this commit operation.");
            }

            if (receipt.Mode != operation.Mode
                || !string.Equals(receipt.TemporaryPath, operation.TemporaryPath, StringComparison.Ordinal)
                || !string.Equals(receipt.FinalPath, operation.FinalPath, StringComparison.Ordinal)
                || receipt.ByteCount != operation.ByteCount
                || !ReferenceEquals(receipt.ActionPlan, operation.ActionPlan)
                || !ReferenceEquals(receipt.RootLayout, operation.RootLayout)
                || receipt.TestRunId != operation.TestRunId
                || !string.Equals(receipt.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Commit receipt must match the commit operation.");
            }
        }
    }
}
