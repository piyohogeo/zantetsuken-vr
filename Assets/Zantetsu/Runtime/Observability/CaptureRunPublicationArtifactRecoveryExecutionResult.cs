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

        /// <summary>
        /// Proof that this exact execution result instance was fully validated.
        /// The token is bound to the result by reference and carries the action
        /// plan validation token acquired during that validation, so a caller
        /// can re-check index-local correlation without re-validating the plan.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly CaptureRunPublicationArtifactRecoveryExecutionResult _result;
            private readonly CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken _actionPlanToken;

            private ValidationToken(
                CaptureRunPublicationArtifactRecoveryExecutionResult result,
                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken actionPlanToken)
            {
                _result = result;
                _actionPlanToken = actionPlanToken;
            }

            internal CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken ActionPlanToken => _actionPlanToken;

            internal static ValidationToken Acquire(
                CaptureRunPublicationArtifactRecoveryExecutionResult result,
                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken actionPlanToken)
            {
                if (result == null)
                {
                    throw new ArgumentNullException(nameof(result));
                }

                if (actionPlanToken == null)
                {
                    throw new ArgumentNullException(nameof(actionPlanToken));
                }

                return new ValidationToken(result, actionPlanToken);
            }

            /// <summary>
            /// Combined-proof mint: the caller has already fully validated the
            /// cleanup plan in one pass, which proved this execution result
            /// (its batch, action plan, and completed-step receipts). This
            /// mints the result token from that already-validated state without
            /// re-walking the Artifact/Receipt graph a second time.
            /// </summary>
            internal static bool TryAcquireFromValidatedResult(
                CaptureRunPublicationArtifactRecoveryExecutionResult result,
                out ValidationToken token)
            {
                token = null;
                if (result == null || result.Batch == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = result.Batch.ActionPlan;
                if (actionPlan == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken actionPlanToken =
                    CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken.AcquireFromValidatedPlan(actionPlan);

                token = new ValidationToken(result, actionPlanToken);
                return true;
            }

            /// <summary>
            /// Reports whether this token was issued for the given execution
            /// result and whether that result's index-local structure and step
            /// correlations are still intact. Never throws.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunPublicationArtifactRecoveryExecutionResult result)
            {
                return result != null
                    && ReferenceEquals(_result, result)
                    && result.IsIndexLocalIntact(_actionPlanToken);
            }
        }

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

        internal bool IsValid => TryValidate(out _);

        /// <summary>
        /// Fully validates this execution result exactly once and returns an
        /// execution-result validation token bound to this exact instance, so a
        /// caller can reuse it for index-local checks without re-validating the
        /// batch or the plan a second time.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            token = null;

            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken actionPlanToken;
            if (!TryAcquireToken(_batch, out actionPlanToken))
            {
                return false;
            }

            if (!IsCorrelated(_issuedBy, _batch, _completedSteps, actionPlanToken))
            {
                return false;
            }

            token = ValidationToken.Acquire(this, actionPlanToken);
            return true;
        }

        /// <summary>
        /// O(n), exception-safe re-check that the completed-step array and its
        /// receipts are still correlated to this result's batch and issuer,
        /// using an already acquired action plan token.
        /// </summary>
        internal bool IsIndexLocalIntact(
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken actionPlanToken)
        {
            return IsStepsIndexLocalCorrelated(_issuedBy, _batch, _completedSteps, actionPlanToken);
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

            return IsStepsIndexLocalCorrelated(issuedBy, batch, completedSteps, token);
        }

        private static bool IsStepsIndexLocalCorrelated(
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator issuedBy,
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch,
            CaptureRunPublicationArtifactRecoveryCompletedStep[] completedSteps,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (issuedBy == null || batch == null || completedSteps == null || token == null
                || !batch.IsIndexLocalStructureIntact())
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
