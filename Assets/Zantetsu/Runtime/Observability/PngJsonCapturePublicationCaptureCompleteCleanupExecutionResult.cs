using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a completed PngJson capture-complete cleanup
    /// execution: the coordinator that issued it, the batch it executed, the
    /// completed steps in order, and the exact action plan token the execution
    /// used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completed-step array is defensively copied at construction and never
    /// exposed. <see cref="IsValid"/> recomputes the full correlation — count,
    /// order, prepared-step identity, receipt issuers, and receipt operations —
    /// from the held values with the held action plan token, without re-issuing
    /// any token and without throwing, including after the lock lease has been
    /// released or a nested value was forged. The result owns and disposes
    /// nothing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch _batch;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] _completedSteps;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken _actionPlanToken;

        /// <summary>
        /// Proof that this exact execution result instance and its completed
        /// step sequence were fully validated. The token is bound to the result,
        /// coordinator, batch, completed-step array, and held action plan token
        /// by reference, so batch replacement, completed-step array replacement
        /// or reordering, receipt replacement, and action plan token replacement
        /// after issuance all fail closed.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult _result;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator _issuedBy;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch _batch;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] _completedStepsArray;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken _actionPlanToken;

            private ValidationToken(
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result,
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch,
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] completedStepsArray,
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken actionPlanToken)
            {
                _result = result;
                _issuedBy = issuedBy;
                _batch = batch;
                _completedStepsArray = completedStepsArray;
                _actionPlanToken = actionPlanToken;
            }

            /// <summary>
            /// Single atomic validated mint: fully validates the result exactly
            /// once and only then binds the exact completed-step array and held
            /// action plan token. The private constructor keeps the token
            /// unfabricable, and the token never leaves this method for a result
            /// that failed validation. The proof array and the held action plan
            /// token are never exposed.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result,
                out ValidationToken token)
            {
                token = null;
                if (result == null)
                {
                    return false;
                }

                if (!result.IsFullyValid())
                {
                    return false;
                }

                token = new ValidationToken(
                    result,
                    result._issuedBy,
                    result._batch,
                    result._completedSteps,
                    result._actionPlanToken);
                return true;
            }

            /// <summary>
            /// O(1), exception-safe binding-only check that this token was
            /// minted for the given result and still binds to its exact
            /// coordinator, batch, completed-step array, and held action plan
            /// token, without walking the step elements. Never throws.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result)
            {
                return result != null
                    && _actionPlanToken != null
                    && ReferenceEquals(_result, result)
                    && ReferenceEquals(_issuedBy, result._issuedBy)
                    && ReferenceEquals(_batch, result._batch)
                    && result._completedSteps != null
                    && ReferenceEquals(_completedStepsArray, result._completedSteps)
                    && ReferenceEquals(_actionPlanToken, result._actionPlanToken);
            }
        }

        /// <summary>
        /// Single token-gated atomic factory: verifies the exact token binding,
        /// the completed-step array length, the ascending order, the exact
        /// prepared-step identity, the completed-step index-local validity, and
        /// the receipt issuer correlation, and defensively copies the array in
        /// the same ascending loop. No plan re-validation, token re-issuance,
        /// entry scan, or filesystem access happens.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult Create(
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch,
            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] completedSteps,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
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

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!token.IsIssuedFor(batch.ActionPlan))
            {
                throw new ArgumentException(
                    "Action plan token must be issued for the batch's action plan.",
                    nameof(token));
            }

            if (completedSteps.Length != batch.Count)
            {
                throw new ArgumentException(
                    "Completed step count must match the batch step count.",
                    nameof(completedSteps));
            }

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] copy =
                new PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[completedSteps.Length];

            for (int i = 0; i < completedSteps.Length; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed = completedSteps[i];
                if (completed == null)
                {
                    throw new ArgumentException(
                        "Completed step array must not contain null elements.",
                        nameof(completedSteps));
                }

                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                if (prepared == null
                    || !ReferenceEquals(completed.PreparedStep, prepared)
                    || completed.StepIndex != i)
                {
                    throw new ArgumentException(
                        "Completed step must match the batch step at its index.",
                        nameof(completedSteps));
                }

                if (!completed.IsValidIndexLocal(token))
                {
                    throw new ArgumentException(
                        "Completed step must be index-locally valid for the token.",
                        nameof(completedSteps));
                }

                PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = completed.CleanupReceipt;
                if (prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    if (receipt != null)
                    {
                        throw new ArgumentException(
                            "CaptureCompleteReady completed step must hold no receipt.",
                            nameof(completedSteps));
                    }
                }
                else if (receipt == null || !ReferenceEquals(receipt.IssuedBy, issuedBy.Backend))
                {
                    throw new ArgumentException(
                        "Side-effecting completed step must hold a receipt issued by the coordinator's backend.",
                        nameof(completedSteps));
                }

                copy[i] = completed;
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult(issuedBy, batch, copy, token);
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult(
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch,
            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] completedSteps,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken actionPlanToken)
        {
            _issuedBy = issuedBy;
            _batch = batch;
            _completedSteps = completedSteps;
            _actionPlanToken = actionPlanToken;
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch Batch => _batch;

        internal PngJsonCapturePublicationCaptureCompleteCleanupActionPlan ActionPlan => _batch.ActionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _batch.OrchestrationResult;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _batch.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _batch.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _batch.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _batch.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _batch.LockIdentityEvidence;

        internal long TestRunId => _batch.TestRunId;

        internal string RunInitializationId => _batch.RunInitializationId;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status =>
            CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady;

        internal int Count => _completedSteps.Length;

        internal PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep GetCompletedStep(int index)
        {
            if (index < 0 || index >= _completedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Completed step index out of range.");
            }

            return _completedSteps[index];
        }

        /// <summary>
        /// Fully validates this execution result exactly once and returns a
        /// result validation token bound to this exact instance, its issuing
        /// coordinator, its batch, and its completed-step sequence. Never
        /// re-issues an action plan token.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        /// <summary>
        /// Fully re-validates this result exactly once with the held action
        /// plan token, without re-issuing any token. A nulled, shortened,
        /// reordered, or element-swapped completed-step array, a swapped
        /// receipt, or a released owner converges to false without throwing.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return IsFullyValid();
            }
        }

        /// <summary>
        /// Exception-safe token-gated full validation: first requires the token
        /// to still bind to this exact result, then fully re-validates the
        /// current completed-step and receipt sequence. Never throws.
        /// </summary>
        internal bool IsValidWithToken(ValidationToken token)
        {
            if (token == null || !token.IsIssuedFor(this))
            {
                return false;
            }

            return IsFullyValid();
        }

        private bool IsFullyValid()
        {
            return IsCorrelated(_issuedBy, _batch, _completedSteps, _actionPlanToken);
        }

        private static bool IsCorrelated(
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch,
            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] completedSteps,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            try
            {
                if (issuedBy == null || batch == null || completedSteps == null || token == null)
                {
                    return false;
                }

                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = batch.ActionPlan;
                if (actionPlan == null || !token.IsIssuedFor(actionPlan) || !actionPlan.IsValid)
                {
                    return false;
                }

                if (completedSteps.Length != batch.Count)
                {
                    return false;
                }

                for (int i = 0; i < completedSteps.Length; i++)
                {
                    PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed = completedSteps[i];
                    if (completed == null)
                    {
                        return false;
                    }

                    PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                    if (prepared == null
                        || !ReferenceEquals(completed.PreparedStep, prepared)
                        || completed.StepIndex != i)
                    {
                        return false;
                    }

                    if (!completed.IsValidIndexLocal(token))
                    {
                        return false;
                    }

                    PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = completed.CleanupReceipt;
                    if (prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                    {
                        if (receipt != null)
                        {
                            return false;
                        }
                    }
                    else if (receipt == null || !ReferenceEquals(receipt.IssuedBy, issuedBy.Backend))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
