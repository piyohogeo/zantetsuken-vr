using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one orchestrated capture run publication artifact
    /// recovery pass: the coordinator that issued it and the execution result
    /// it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields and has no public
    /// constructor; the only way to build one is through
    /// <see cref="CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator.Execute"/>.
    /// Every accessor forwards a value from the correlated execution result
    /// graph. <see cref="IsValid"/> recomputes the full correlation without
    /// throwing, so a result whose nested values were forged, whose lease was
    /// released, or whose held values became otherwise invalid reports
    /// <c>false</c> instead of throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryOrchestrationResult
    {
        private readonly CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator _issuedBy;
        private readonly CaptureRunPublicationArtifactRecoveryExecutionResult _executionResult;

        internal CaptureRunPublicationArtifactRecoveryOrchestrationResult(
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator issuedBy,
            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult)
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
                throw new ArgumentException(
                    "Execution result must be correlated with the issuing orchestration coordinator.",
                    nameof(executionResult));
            }

            _issuedBy = issuedBy;
            _executionResult = executionResult;
        }

        internal CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator IssuedBy => _issuedBy;

        internal CaptureRunPublicationArtifactRecoveryExecutionResult ExecutionResult => _executionResult;

        internal CaptureRunPublicationArtifactInspectionSnapshot InspectionSnapshot =>
            _executionResult.Batch.ActionPlan.Decision.Snapshot;

        internal CaptureRunPublicationArtifactRecoveryDecision Decision =>
            _executionResult.Batch.ActionPlan.Decision;

        internal CaptureRunPublicationArtifactRecoveryActionPlan ActionPlan =>
            _executionResult.Batch.ActionPlan;

        internal CaptureRunPublicationArtifactRecoveryExecutionBatch Batch =>
            _executionResult.Batch;

        internal CaptureRunPublicationArtifactRecoveryExecutionStatus Status => _executionResult.Status;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _executionResult.Disposition;

        internal CaptureRunRootLayout RootLayout => _executionResult.RootLayout;

        internal CaptureRunLockLease LockLease => _executionResult.LockLease;

        internal long TestRunId => _executionResult.TestRunId;

        internal string RunInitializationId => _executionResult.RunInitializationId;

        internal bool IsValid => IsCorrelated(_issuedBy, _executionResult);

        private static bool IsCorrelated(
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator issuedBy,
            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult)
        {
            if (issuedBy == null || executionResult == null || !executionResult.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(executionResult.IssuedBy, issuedBy.ExecutionCoordinator))
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = executionResult.Batch;
            if (batch == null || !batch.IsValid)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryActionPlan plan = batch.ActionPlan;
            if (plan == null || !plan.IsValid)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryDecision decision = plan.Decision;
            if (decision == null || !decision.IsValid)
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null || !snapshot.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(snapshot.IssuedBy, issuedBy.Inspector))
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation operation = snapshot.Operation;
            if (operation == null || !operation.IsValid)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = operation.RootLayout;
            if (rootLayout == null
                || !ReferenceEquals(rootLayout, decision.RootLayout)
                || !ReferenceEquals(rootLayout, plan.RootLayout)
                || !ReferenceEquals(rootLayout, batch.RootLayout)
                || !ReferenceEquals(rootLayout, executionResult.RootLayout))
            {
                return false;
            }

            CaptureRunLockLease lockLease = operation.LockLease;
            if (lockLease == null
                || !ReferenceEquals(lockLease, batch.LockLease)
                || !ReferenceEquals(lockLease, executionResult.LockLease))
            {
                return false;
            }

            if (operation.TestRunId != executionResult.TestRunId
                || !string.Equals(
                    operation.RunInitializationId,
                    executionResult.RunInitializationId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return StatusMatchesDisposition(executionResult.Status, decision.Disposition);
        }

        private static bool StatusMatchesDisposition(
            CaptureRunPublicationArtifactRecoveryExecutionStatus status,
            CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            CaptureRunPublicationArtifactRecoveryExecutionStatus expected;
            switch (disposition)
            {
                case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired;
                    break;
                case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired;
                    break;
                case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace;
                    break;
                case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing;
                    break;
                case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing;
                    break;
                case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision;
                    break;
                default:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.None;
                    break;
            }

            return status == expected;
        }
    }
}
