using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a completed publication artifact recovery execution:
    /// the coordinator that issued it, the batch it executed, and the completed
    /// steps in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completed-step array is defensively copied at construction and never
    /// exposed. <see cref="IsValid"/> recomputes the full correlation — count,
    /// order, prepared-step identity, receipt issuers, and receipt operations —
    /// from the held values without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryExecutionResult
    {
        private readonly CaptureRunPublicationArtifactRecoveryExecutionCoordinator _issuedBy;
        private readonly CaptureRunPublicationArtifactRecoveryExecutionBatch _batch;
        private readonly CaptureRunPublicationArtifactRecoveryCompletedStep[] _completedSteps;

        internal CaptureRunPublicationArtifactRecoveryExecutionResult(
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator issuedBy,
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch,
            CaptureRunPublicationArtifactRecoveryCompletedStep[] completedSteps)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (completedSteps == null)
            {
                throw new ArgumentNullException(nameof(completedSteps));
            }

            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
            if (!TryAcquireToken(batch, out token))
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            if (!IsCorrelated(issuedBy, batch, completedSteps, token))
            {
                throw new ArgumentException("Completed steps must be fully correlated with the issuing coordinator and batch.", nameof(completedSteps));
            }

            _issuedBy = issuedBy;
            _batch = batch;
            _completedSteps = Copy(completedSteps);
        }

        internal CaptureRunPublicationArtifactRecoveryExecutionCoordinator IssuedBy => _issuedBy;

        internal CaptureRunPublicationArtifactRecoveryExecutionBatch Batch => _batch;

        internal CaptureRunPublicationArtifactRecoveryExecutionStatus Status => StatusFromDisposition(_batch.Disposition);

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _batch.Disposition;

        internal CaptureRunRootLayout RootLayout => _batch.RootLayout;

        internal CaptureRunLockLease LockLease => _batch.LockLease;

        internal long TestRunId => _batch.TestRunId;

        internal string RunInitializationId => _batch.RunInitializationId;

        internal int Count => _completedSteps.Length;

        internal CaptureRunPublicationArtifactRecoveryCompletedStep GetCompletedStep(int index)
        {
            if (index < 0 || index >= _completedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Completed step index out of range.");
            }

            return _completedSteps[index];
        }

        internal bool IsValid
        {
            get
            {
                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
                if (!TryAcquireToken(_batch, out token))
                {
                    return false;
                }

                return IsCorrelated(_issuedBy, _batch, _completedSteps, token);
            }
        }

        private static bool IsCorrelated(
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator issuedBy,
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch,
            CaptureRunPublicationArtifactRecoveryCompletedStep[] completedSteps,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (issuedBy == null || batch == null || completedSteps == null || token == null)
            {
                return false;
            }

            if (StatusFromDisposition(batch.Disposition) == CaptureRunPublicationArtifactRecoveryExecutionStatus.None)
            {
                return false;
            }

            if (completedSteps.Length != batch.Count)
            {
                return false;
            }

            for (int i = 0; i < completedSteps.Length; i++)
            {
                CaptureRunPublicationArtifactRecoveryCompletedStep completed = completedSteps[i];
                if (completed == null)
                {
                    return false;
                }

                if (!ReferenceEquals(completed.PreparedStep, batch.GetStep(i)))
                {
                    return false;
                }

                if (!completed.IsValidIndexLocal(token))
                {
                    return false;
                }

                switch (completed.PreparedStep.Action)
                {
                    case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                        if (!ReferenceEquals(completed.PublishReceipt.IssuedBy, issuedBy.Publisher))
                        {
                            return false;
                        }

                        break;

                    case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                        if (!ReferenceEquals(completed.CommitReceipt.IssuedBy, issuedBy.CaptureIndexCommitter))
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }

        private static bool TryAcquireToken(
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch,
            out CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            token = null;

            if (batch == null)
            {
                return false;
            }

            return batch.TryValidate(out token);
        }

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus StatusFromDisposition(
            CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            switch (disposition)
            {
                case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired;

                case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired;

                case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace;

                case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing;

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing;

                case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision;

                default:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.None;
            }
        }

        private static CaptureRunPublicationArtifactRecoveryCompletedStep[] Copy(
            CaptureRunPublicationArtifactRecoveryCompletedStep[] steps)
        {
            CaptureRunPublicationArtifactRecoveryCompletedStep[] copy =
                new CaptureRunPublicationArtifactRecoveryCompletedStep[steps.Length];
            Array.Copy(steps, copy, steps.Length);
            return copy;
        }
    }
}
