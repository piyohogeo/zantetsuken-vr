using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the recovery pipeline exactly once under the held lock:
    /// Inspection, Classification, Action Plan, Execution Batch, Recovery
    /// Execution, and finally the Orchestration Result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holds exactly two readonly dependencies — the inspector and the
    /// execution coordinator — and is not an <see cref="IDisposable"/>.
    /// <see cref="Execute"/> inspects once, classifies, plans, batches,
    /// executes once, and verifies the snapshot and the execution result
    /// immediately before proceeding.
    /// </para>
    /// <para>
    /// No retry, rollback, compensating deletion, or automatic re-inspection
    /// is performed. The lock lease is owned by the operation and is never
    /// disposed here; neither the inspector, the execution coordinator, nor
    /// its backends are disposed. Inspector and backend exceptions propagate
    /// unchanged. A start-fresh result does not invoke the bootstrap, issue an
    /// initialization ID, or re-acquire the lock; an initialization-ready
    /// result does not convert into an existing session; a publication result
    /// does not call publication recovery; a collision result returns a
    /// backend-free stop outcome only.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryOrchestrationCoordinator
    {
        private readonly ICaptureRunInitializationRecoveryInspector _inspector;
        private readonly CaptureRunInitializationRecoveryExecutionCoordinator _executionCoordinator;

        internal CaptureRunInitializationRecoveryOrchestrationCoordinator(
            ICaptureRunInitializationRecoveryInspector inspector,
            CaptureRunInitializationRecoveryExecutionCoordinator executionCoordinator)
        {
            if (inspector == null)
            {
                throw new ArgumentNullException(nameof(inspector));
            }

            if (executionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(executionCoordinator));
            }

            _inspector = inspector;
            _executionCoordinator = executionCoordinator;
        }

        internal ICaptureRunInitializationRecoveryInspector Inspector => _inspector;

        internal CaptureRunInitializationRecoveryExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

        internal CaptureRunInitializationRecoveryOrchestrationResult Execute(
            CaptureRunInitializationRecoveryInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Inspection operation must be valid.", nameof(operation));
            }

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = _inspector.Inspect(operation);
            VerifySnapshot(snapshot, operation);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(snapshot);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(decision);
            CaptureRunInitializationRecoveryExecutionBatch batch = CaptureRunInitializationRecoveryExecutionBatchBuilder.Build(plan);

            CaptureRunInitializationRecoveryExecutionResult executionResult = _executionCoordinator.Execute(batch);
            VerifyExecutionResult(executionResult, batch);

            return new CaptureRunInitializationRecoveryOrchestrationResult(this, executionResult);
        }

        private void VerifySnapshot(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot,
            CaptureRunInitializationRecoveryInspectionOperation operation)
        {
            if (snapshot == null
                || !snapshot.IsValid
                || !ReferenceEquals(snapshot.IssuedBy, _inspector)
                || !ReferenceEquals(snapshot.Operation, operation))
            {
                throw new InvalidOperationException("Inspection snapshot must be valid and issued for the inspection operation.");
            }
        }

        private void VerifyExecutionResult(
            CaptureRunInitializationRecoveryExecutionResult executionResult,
            CaptureRunInitializationRecoveryExecutionBatch batch)
        {
            if (executionResult == null
                || !executionResult.IsValid
                || !ReferenceEquals(executionResult.IssuedBy, _executionCoordinator)
                || !ReferenceEquals(executionResult.Batch, batch))
            {
                throw new InvalidOperationException("Execution result must be valid and issued for the execution batch.");
            }
        }
    }
}
