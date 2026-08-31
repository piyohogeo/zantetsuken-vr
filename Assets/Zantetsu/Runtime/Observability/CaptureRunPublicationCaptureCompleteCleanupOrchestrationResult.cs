using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one orchestrated capture run publication
    /// capture-complete cleanup pass: the coordinator that issued it and the
    /// cleanup execution result it produced, carrying
    /// <c>CaptureCompleteReady</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields and has no public
    /// constructor; the only way to build one is through
    /// <see cref="CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator.Execute"/>
    /// or its internal trusted constructor. Every accessor forwards a value
    /// from the correlated execution result graph; the action plan, batch,
    /// artifact recovery orchestration result, status, disposition, root
    /// layout, lock lease, test run id, and run initialization id are all
    /// forwarded rather than duplicated.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the full correlation without throwing,
    /// so a result whose nested values were forged, whose lease was released,
    /// whose token was corrupted, or whose held values became otherwise invalid
    /// reports <c>false</c> instead of throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionResult _executionResult;

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (executionResult == null)
            {
                throw new ArgumentNullException(nameof(executionResult));
            }

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token;
            if (!executionResult.TryValidate(out token)
                || !IsCorrelated(issuedBy, executionResult, token))
            {
                throw new ArgumentException(
                    "Execution result must be correlated with the issuing orchestration coordinator.",
                    nameof(executionResult));
            }

            _issuedBy = issuedBy;
            _executionResult = executionResult;
        }

        /// <summary>
        /// Atomic construction path used by the coordinator: fully validates
        /// the execution result once, verifies the expected batch, action plan,
        /// and recovery result references, re-verifies the correlation
        /// index-locally, and constructs the result — all without ever exposing
        /// the validation token, so no two-step token handoff can re-introduce
        /// a TOCTOU gap.
        /// </summary>
        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch expectedBatch,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan expectedActionPlan,
            CaptureRunPublicationArtifactRecoveryOrchestrationResult expectedRecoveryResult)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (executionResult == null)
            {
                throw new ArgumentNullException(nameof(executionResult));
            }

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token;
            if (!executionResult.TryValidate(out token)
                || !ReferenceEquals(executionResult.IssuedBy, issuedBy.ExecutionCoordinator)
                || !ReferenceEquals(executionResult.Batch, expectedBatch)
                || !ReferenceEquals(executionResult.ActionPlan, expectedActionPlan)
                || !ReferenceEquals(executionResult.OrchestrationResult, expectedRecoveryResult)
                || executionResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady
                || !IsCorrelated(issuedBy, executionResult, token))
            {
                throw new InvalidOperationException(
                    "Execution result must be valid and correlated with the cleanup batch, plan, and recovery result.");
            }

            _issuedBy = issuedBy;
            _executionResult = executionResult;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _executionResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionBatch Batch => _executionResult.Batch;

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan ActionPlan => _executionResult.ActionPlan;

        internal CaptureRunPublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _executionResult.OrchestrationResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _executionResult.Status;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _executionResult.OrchestrationResult.Disposition;

        internal CaptureRunRootLayout RootLayout => _executionResult.RootLayout;

        internal CaptureRunLockLease LockLease => _executionResult.LockLease;

        internal long TestRunId => _executionResult.TestRunId;

        internal string RunInitializationId => _executionResult.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                if (_executionResult == null)
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token;
                if (!_executionResult.TryValidate(out token))
                {
                    return false;
                }

                return IsCorrelated(_issuedBy, _executionResult, token);
            }
        }

        /// <summary>
        /// Exception-safe correlation shared by both constructors and
        /// <see cref="IsValid"/>. It re-verifies the token binding, the issuing
        /// coordinator identity, the execution-result-to-batch-to-action-plan-to
        /// recovery-result reference chain, the shared root layout and lock
        /// lease, the test run id and run initialization id, the
        /// <c>CaptureCompleteReady</c> status, and the accepted disposition,
        /// returning <c>false</c> for any forged or corrupted value without
        /// throwing.
        /// </summary>
        private static bool IsCorrelated(
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult,
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token)
        {
            if (issuedBy == null || executionResult == null || token == null)
            {
                return false;
            }

            if (!token.IsIssuedForExactBindings(executionResult))
            {
                return false;
            }

            if (!ReferenceEquals(executionResult.IssuedBy, issuedBy.ExecutionCoordinator))
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = executionResult.Batch;
            if (batch == null)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan = batch.ActionPlan;
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken actionPlanToken = token.ActionPlanToken;
            if (actionPlan == null || actionPlanToken == null || !actionPlanToken.IsIssuedFor(actionPlan))
            {
                return false;
            }

            if (!ReferenceEquals(executionResult.ActionPlan, actionPlan))
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = actionPlan.OrchestrationResult;
            if (recovery == null || !ReferenceEquals(executionResult.OrchestrationResult, recovery))
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = executionResult.RootLayout;
            if (rootLayout == null
                || !ReferenceEquals(rootLayout, batch.RootLayout)
                || !ReferenceEquals(rootLayout, actionPlan.RootLayout))
            {
                return false;
            }

            CaptureRunLockLease lockLease = executionResult.LockLease;
            if (lockLease == null
                || !ReferenceEquals(lockLease, batch.LockLease)
                || !ReferenceEquals(lockLease, actionPlan.LockLease))
            {
                return false;
            }

            if (executionResult.TestRunId != recovery.TestRunId
                || !string.Equals(
                    executionResult.RunInitializationId,
                    recovery.RunInitializationId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (executionResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
            {
                return false;
            }

            return IsAcceptedDisposition(recovery.Disposition);
        }

        private static bool IsAcceptedDisposition(CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            return disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex
                || disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
        }
    }
}
