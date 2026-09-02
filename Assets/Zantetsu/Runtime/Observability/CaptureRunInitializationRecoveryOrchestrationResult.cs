using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one completed recovery orchestration: the
    /// orchestrating coordinator and the execution result it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holds only two references and forwards every other value — snapshot,
    /// decision, action plan, batch, status, root layout, lock lease, test run
    /// id, and run initialization id — straight from the execution result's
    /// graph. No value is copied. <see cref="IsValid"/> recomputes the full
    /// correlation from the held values without throwing.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryOrchestrationResult
    {
        private readonly CaptureRunInitializationRecoveryOrchestrationCoordinator _issuedBy;
        private readonly CaptureRunInitializationRecoveryExecutionResult _executionResult;

        internal CaptureRunInitializationRecoveryOrchestrationResult(
            CaptureRunInitializationRecoveryOrchestrationCoordinator issuedBy,
            CaptureRunInitializationRecoveryExecutionResult executionResult)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (executionResult == null)
            {
                throw new ArgumentNullException(nameof(executionResult));
            }

            if (!IsCorrelated(issuedBy, executionResult))
            {
                throw new ArgumentException("Execution result must be fully correlated with the issuing coordinator.", nameof(executionResult));
            }

            _issuedBy = issuedBy;
            _executionResult = executionResult;
        }

        internal CaptureRunInitializationRecoveryOrchestrationCoordinator IssuedBy => _issuedBy;

        internal CaptureRunInitializationRecoveryExecutionResult ExecutionResult => _executionResult;

        internal CaptureRunInitializationRecoveryInspectionSnapshot Snapshot => _executionResult.Batch.ActionPlan.Decision.Snapshot;

        internal CaptureRunInitializationRecoveryDecision Decision => _executionResult.Batch.ActionPlan.Decision;

        internal CaptureRunInitializationRecoveryActionPlan ActionPlan => _executionResult.Batch.ActionPlan;

        internal CaptureRunInitializationRecoveryExecutionBatch Batch => _executionResult.Batch;

        internal CaptureRunInitializationRecoveryExecutionStatus Status => _executionResult.Status;

        internal CaptureRunInitializationRecoveryDisposition Disposition => _executionResult.Batch.ActionPlan.Decision.Disposition;

        internal CaptureRunRootLayout RootLayout => _executionResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _executionResult.LockIdentityEvidence;

        internal long TestRunId => _executionResult.TestRunId;

        internal string RunInitializationId => _executionResult.RunInitializationId;

        internal bool IsValid => IsCorrelated(_issuedBy, _executionResult);

        private static bool IsCorrelated(
            CaptureRunInitializationRecoveryOrchestrationCoordinator issuedBy,
            CaptureRunInitializationRecoveryExecutionResult executionResult)
        {
            if (issuedBy == null || executionResult == null || !executionResult.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(executionResult.IssuedBy, issuedBy.ExecutionCoordinator))
            {
                return false;
            }

            CaptureRunInitializationRecoveryExecutionBatch batch = executionResult.Batch;
            if (batch == null || !batch.IsValid)
            {
                return false;
            }

            CaptureRunInitializationRecoveryActionPlan plan = batch.ActionPlan;
            if (plan == null || !plan.IsValid)
            {
                return false;
            }

            CaptureRunInitializationRecoveryDecision decision = plan.Decision;
            if (decision == null || !decision.IsValid)
            {
                return false;
            }

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null || !snapshot.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(snapshot.IssuedBy, issuedBy.Inspector))
            {
                return false;
            }

            CaptureRunInitializationRecoveryInspectionOperation operation = snapshot.Operation;
            if (operation == null || !operation.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(operation.RootLayout, batch.RootLayout)
                || !ReferenceEquals(operation.RootLayout, executionResult.RootLayout)
                || !ReferenceEquals(operation.LockIdentityEvidence, batch.LockIdentityEvidence)
                || !ReferenceEquals(operation.LockIdentityEvidence, executionResult.LockIdentityEvidence))
            {
                return false;
            }

            if (!StatusMatchesDisposition(executionResult.Status, decision.Disposition))
            {
                return false;
            }

            return true;
        }

        private static bool StatusMatchesDisposition(
            CaptureRunInitializationRecoveryExecutionStatus status,
            CaptureRunInitializationRecoveryDisposition disposition)
        {
            CaptureRunInitializationRecoveryExecutionStatus expected;
            switch (disposition)
            {
                case CaptureRunInitializationRecoveryDisposition.StartFresh:
                case CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh:
                    expected = CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired;
                    break;

                case CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization:
                case CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers:
                case CaptureRunInitializationRecoveryDisposition.AlreadyInitialized:
                    expected = CaptureRunInitializationRecoveryExecutionStatus.InitializationReady;
                    break;

                case CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery:
                    expected = CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired;
                    break;

                case CaptureRunInitializationRecoveryDisposition.RunRootCollision:
                    expected = CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision;
                    break;

                default:
                    expected = CaptureRunInitializationRecoveryExecutionStatus.None;
                    break;
            }

            return status == expected;
        }
    }
}
