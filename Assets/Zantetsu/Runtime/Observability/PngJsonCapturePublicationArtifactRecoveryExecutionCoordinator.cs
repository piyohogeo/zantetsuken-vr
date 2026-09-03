using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Executes a prepared PngJson publication artifact recovery execution
    /// batch in ascending step order, contacting each backend at most once per
    /// step and verifying each returned receipt immediately after the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator holds exactly two readonly dependencies — the PngJson
    /// publisher and the PngJson capture index committer — and is not an
    /// <see cref="IDisposable"/>. <see cref="Execute"/> validates the batch
    /// once before any side effect, reuses the issued action plan validation
    /// token for every step and receipt check, and returns a result only after
    /// every step succeeded. Publish and commit actions contact their backend
    /// exactly once; routing and stop actions contact neither backend and
    /// produce no receipt.
    /// </para>
    /// <para>
    /// A backend exception propagates unchanged: the coordinator performs no
    /// retry, no rollback, no compensating deletion, no cleanup, and no
    /// automatic re-inspection, and it never disposes the lock owner, the
    /// publisher, or the committer. A null, foreign, different-operation,
    /// different-token, or invalid receipt throws
    /// <see cref="InvalidOperationException"/> and skips all remaining steps,
    /// returning no result.
    /// </para>
    /// <para>
    /// Because a publish or commit call may have partially succeeded on the
    /// filesystem before throwing, the caller must not blindly re-run the same
    /// batch; it must re-inspect under the held lock.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and never mutates the batch, plan,
    /// decision, snapshot, operations, or canonical bytes.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator
    {
        private readonly IPngJsonCapturePublicationArtifactPublisher _publisher;
        private readonly IPngJsonCaptureRunCaptureIndexCommitter _committer;

        internal PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(
            IPngJsonCapturePublicationArtifactPublisher publisher,
            IPngJsonCaptureRunCaptureIndexCommitter committer)
        {
            if (publisher == null)
            {
                throw new ArgumentNullException(nameof(publisher));
            }

            if (committer == null)
            {
                throw new ArgumentNullException(nameof(committer));
            }

            _publisher = publisher;
            _committer = committer;
        }

        internal IPngJsonCapturePublicationArtifactPublisher Publisher => _publisher;

        internal IPngJsonCaptureRunCaptureIndexCommitter Committer => _committer;

        /// <summary>
        /// Executes the batch once, in ascending step order, and returns an
        /// execution result only after every step succeeded. The action plan
        /// validation token is acquired exactly once, outside the step loop,
        /// and reused for every index-local re-check and receipt verification.
        /// </summary>
        internal PngJsonCapturePublicationArtifactRecoveryExecutionResult Execute(
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (!batch.TryValidate(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            int count = batch.Count;
            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] completedSteps =
                new PngJsonCapturePublicationArtifactRecoveryCompletedStep[count];

            for (int i = 0; i < count; i++)
            {
                PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep = batch.GetStep(i);

                // Re-confirm the step index-locally with the same token before
                // any backend contact, so a corrupted batch fails before side
                // effects on this step.
                if (preparedStep == null
                    || preparedStep.StepIndex != i
                    || !ReferenceEquals(preparedStep.ActionPlan, batch.ActionPlan)
                    || !preparedStep.IsValidIndexLocal(token))
                {
                    throw new InvalidOperationException("Prepared step correlation must remain intact.");
                }

                PngJsonCapturePublicationArtifactPublishReceipt publishReceipt = null;
                PngJsonCaptureRunCaptureIndexCommitReceipt commitReceipt = null;

                switch (preparedStep.Action)
                {
                    case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    {
                        PngJsonCapturePublicationArtifactPublishOperation operation = preparedStep.PublishOperation;
                        publishReceipt = _publisher.Publish(operation, token);

                        if (publishReceipt == null
                            || !publishReceipt.IsIssuedFor(_publisher, operation, token))
                        {
                            throw new InvalidOperationException("Publish receipt must be issued by this coordinator's publisher for the exact operation and token.");
                        }

                        break;
                    }

                    case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    {
                        PngJsonCaptureRunCaptureIndexCommitOperation operation = preparedStep.CaptureIndexCommitOperation;
                        commitReceipt = _committer.Commit(operation, token);

                        if (commitReceipt == null
                            || !commitReceipt.IsIssuedFor(_committer, operation, token))
                        {
                            throw new InvalidOperationException("Commit receipt must be issued by this coordinator's committer for the exact operation and token.");
                        }

                        break;
                    }
                }

                completedSteps[i] = PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    preparedStep, token, _publisher, _committer, publishReceipt, commitReceipt);
            }

            return PngJsonCapturePublicationArtifactRecoveryExecutionResult.Create(
                this, batch, completedSteps, token);
        }
    }
}
