using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a completed capture-complete cleanup execution: the
    /// coordinator that issued it, the batch it executed, and the completed
    /// steps in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completed-step array is defensively copied at construction and never
    /// exposed. <see cref="IsValid"/> recomputes the full correlation — count,
    /// order, prepared-step identity, receipt issuers, and receipt operations —
    /// from the held values without throwing, including after the lock lease
    /// has been released or a nested value was forged. The result owns and
    /// disposes nothing, including the lock lease.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupExecutionResult
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionBatch _batch;
        private readonly CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] _completedSteps;

        /// <summary>
        /// Proof that this exact execution result instance and its completed
        /// step sequence were fully validated. The token is bound to the result
        /// by reference, binds to the exact completed-step array and element
        /// references, and carries the exact batch validation token acquired
        /// during that validation, so batch action plan replacement, array
        /// replacement, and reordering after issuance all fail closed.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionResult _result;
            private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken _batchToken;
            private readonly CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] _issuedStepsArray;
            private readonly CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] _issuedSteps;

            private ValidationToken(
                CaptureRunPublicationCaptureCompleteCleanupExecutionResult result,
                CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken,
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] issuedStepsArray,
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] issuedSteps)
            {
                _result = result;
                _batchToken = batchToken;
                _issuedStepsArray = issuedStepsArray;
                _issuedSteps = issuedSteps;
            }

            internal CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken ActionPlanToken => _batchToken.ActionPlanToken;

            /// <summary>
            /// Single atomic validated mint: fully validates the result exactly
            /// once and only then captures the defensive reference snapshot of
            /// the issued completed-step sequence. The private constructor keeps
            /// the token unfabricable, and the token never leaves this method
            /// for a result that failed validation.
            /// </summary>
            internal static bool TryAcquire(
                CaptureRunPublicationCaptureCompleteCleanupExecutionResult result,
                out ValidationToken token)
            {
                token = null;
                if (result == null)
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken;
                if (!TryAcquireToken(result._batch, out batchToken))
                {
                    return false;
                }

                if (!IsCorrelated(result._issuedBy, result._batch, result._completedSteps, batchToken))
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps = result._completedSteps;
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] issued =
                    new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[steps.Length];
                for (int i = 0; i < steps.Length; i++)
                {
                    issued[i] = steps[i];
                }

                token = new ValidationToken(result, batchToken, steps, issued);
                return true;
            }

            /// <summary>
            /// Exception-safe check that this token was minted for the given
            /// result, that the result still holds the exact completed-step
            /// array with the same element references, and that the completed
            /// steps are still fully correlated to the batch and issuing
            /// coordinator. Never throws and never exposes the completed-step
            /// array.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunPublicationCaptureCompleteCleanupExecutionResult result)
            {
                if (result == null || !ReferenceEquals(_result, result) || result._completedSteps == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_issuedStepsArray, result._completedSteps))
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] issued = _issuedSteps;
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] current = result._completedSteps;
                if (issued == null || issued.Length != current.Length)
                {
                    return false;
                }

                for (int i = 0; i < issued.Length; i++)
                {
                    CaptureRunPublicationCaptureCompleteCleanupCompletedStep step = current[i];
                    if (step == null || !ReferenceEquals(step, issued[i]))
                    {
                        return false;
                    }
                }

                return result.IsIndexLocalIntact(_batchToken);
            }
        }

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps)
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

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken;
            if (!TryAcquireToken(batch, out batchToken))
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            if (!IsCorrelated(issuedBy, batch, completedSteps, batchToken))
            {
                throw new ArgumentException("Completed steps must be fully correlated with the issuing coordinator and batch.", nameof(completedSteps));
            }

            _issuedBy = issuedBy;
            _batch = batch;
            _completedSteps = Copy(completedSteps);
        }

        /// <summary>
        /// Token-gated construction path used by a coordinator that has already
        /// acquired the exact batch validation token once. It confirms the
        /// token still binds to the exact batch and its prepared-step array and
        /// re-verifies the completed-step correlation index-locally, never
        /// re-running the full plan validation.
        /// </summary>
        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken)
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

            if (batchToken == null)
            {
                throw new ArgumentNullException(nameof(batchToken));
            }

            if (!batchToken.IsIssuedFor(batch))
            {
                throw new ArgumentException("Token must be issued for the exact execution batch.", nameof(batchToken));
            }

            if (!IsCorrelated(issuedBy, batch, completedSteps, batchToken))
            {
                throw new ArgumentException("Completed steps must be fully correlated with the issuing coordinator and batch.", nameof(completedSteps));
            }

            _issuedBy = issuedBy;
            _batch = batch;
            _completedSteps = Copy(completedSteps);
        }

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionBatch Batch => _batch;

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan ActionPlan => _batch.ActionPlan;

        internal CaptureRunPublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _batch.OrchestrationResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status =>
            CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady;

        internal CaptureRunRootLayout RootLayout => _batch.RootLayout;

        internal CaptureRunLockLease LockLease => _batch.LockLease;

        internal long TestRunId => _batch.TestRunId;

        internal string RunInitializationId => _batch.RunInitializationId;

        internal int Count => _completedSteps.Length;

        internal CaptureRunPublicationCaptureCompleteCleanupCompletedStep GetStep(int index)
        {
            if (index < 0 || index >= _completedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Completed step index out of range.");
            }

            return _completedSteps[index];
        }

        internal bool IsValid => TryValidate(out _);

        /// <summary>
        /// Fully validates this execution result exactly once and returns a
        /// result validation token bound to this exact instance and its
        /// completed-step sequence.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        /// <summary>
        /// O(n), exception-safe re-check that the completed-step array and its
        /// receipts are still correlated to this result's batch and issuer,
        /// using an already acquired exact batch validation token.
        /// </summary>
        internal bool IsIndexLocalIntact(CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken)
        {
            return IsStepsIndexLocalCorrelated(_issuedBy, _batch, _completedSteps, batchToken);
        }

        private static bool IsCorrelated(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken)
        {
            if (issuedBy == null || batch == null || completedSteps == null || batchToken == null)
            {
                return false;
            }

            return IsStepsIndexLocalCorrelated(issuedBy, batch, completedSteps, batchToken);
        }

        private static bool IsStepsIndexLocalCorrelated(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken)
        {
            if (issuedBy == null || batch == null || completedSteps == null || batchToken == null
                || !batchToken.IsIssuedFor(batch))
            {
                return false;
            }

            if (completedSteps.Length != batch.Count)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken actionPlanToken = batchToken.ActionPlanToken;

            for (int i = 0; i < completedSteps.Length; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep completed = completedSteps[i];
                if (completed == null)
                {
                    return false;
                }

                if (!batchToken.TryGetIssuedStep(batch, i, out CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared))
                {
                    return false;
                }

                if (!ReferenceEquals(completed.PreparedStep, prepared))
                {
                    return false;
                }

                if (!completed.IsValidIndexLocal(actionPlanToken))
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = completed.CleanupReceipt;
                if (receipt != null && !ReferenceEquals(receipt.IssuedBy, issuedBy.Backend))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAcquireToken(
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken)
        {
            batchToken = null;

            if (batch == null)
            {
                return false;
            }

            if (!batch.TryValidate(out batchToken))
            {
                return false;
            }

            return batchToken != null;
        }

        private static CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] Copy(
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps)
        {
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] copy =
                new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                copy[i] = steps[i];
            }

            return copy;
        }
    }
}
