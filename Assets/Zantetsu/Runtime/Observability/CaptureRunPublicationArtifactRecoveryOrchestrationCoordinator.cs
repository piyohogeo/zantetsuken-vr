using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the capture run publication artifact recovery pipeline exactly
    /// once, under the held lock, in a fixed order: Inspection, Classification,
    /// Action Plan construction, Execution Batch construction, Recovery
    /// Execution, and finally the immutable Orchestration Result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly two read-only dependencies: one inspector
    /// (<see cref="ICaptureRunPublicationArtifactInspector"/>) and one recovery
    /// execution coordinator
    /// (<see cref="CaptureRunPublicationArtifactRecoveryExecutionCoordinator"/>).
    /// Both are required and rejected with an
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> names the
    /// offending parameter. It owns, mutates, and disposes nothing and is not
    /// an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the exact sequence once per call:
    /// <list type="number">
    /// <item>Reject a null operation with an
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>; reject an operation whose <c>IsValid</c> is false with
    /// an <see cref="ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any dependency is contacted.</item>
    /// <item><c>Inspect</c> the operation exactly once.</item>
    /// <item>Verify the returned snapshot is non-null, valid, issued by this
    /// coordinator's inspector, and holds the same operation; otherwise throw
    /// an <see cref="InvalidOperationException"/>.</item>
    /// <item><c>Classify</c> the verified snapshot into a decision.</item>
    /// <item>Build the action plan from the decision.</item>
    /// <item>Build the execution batch from the action plan.</item>
    /// <item><c>Execute</c> the batch exactly once.</item>
    /// <item>Verify the returned execution result is non-null, valid, issued by
    /// this coordinator's execution coordinator, and holds the same batch;
    /// otherwise throw an <see cref="InvalidOperationException"/>.</item>
    /// <item>Return an immutable orchestration result correlating this
    /// coordinator with that execution result.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The coordinator never retries, never rolls back, never deletes or
    /// repairs an artifact, never re-inspects, and never re-executes the same
    /// batch. A <c>ReinspectionRequired</c> outcome stops without contacting
    /// the inspector a second time; a <c>CaptureCompleteCleanupRequired</c>
    /// outcome stops without cleaning up or deleting the plan; a
    /// <c>OrphanedPreTrace</c>, <c>ArtifactSourceMissing</c>,
    /// <c>PublishedArtifactMissing</c>, or <c>RunRootCollision</c> outcome stops
    /// without contacting the publisher or committer. Exceptions thrown by the
    /// inspector, publisher, or committer propagate unchanged, with no
    /// compensating action. The coordinator never acquires or releases a lock
    /// and never disposes the lock, the dependencies, or anything it touches.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator
    {
        private readonly ICaptureRunPublicationArtifactInspector _inspector;
        private readonly CaptureRunPublicationArtifactRecoveryExecutionCoordinator _executionCoordinator;

        internal CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator(
            ICaptureRunPublicationArtifactInspector inspector,
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator executionCoordinator)
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

        internal ICaptureRunPublicationArtifactInspector Inspector => _inspector;

        internal CaptureRunPublicationArtifactRecoveryExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

        internal CaptureRunPublicationArtifactRecoveryOrchestrationResult Execute(
            CaptureRunPublicationArtifactInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Inspection operation must be valid.", nameof(operation));
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = _inspector.Inspect(operation);
            VerifySnapshot(snapshot, operation);

            CaptureRunPublicationArtifactRecoveryDecision decision =
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot);

            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan =
                CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(decision);

            CaptureRunPublicationArtifactRecoveryExecutionBatch batch =
                CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(actionPlan);

            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult =
                _executionCoordinator.Execute(batch);

            VerifyExecutionResult(executionResult, batch);

            return new CaptureRunPublicationArtifactRecoveryOrchestrationResult(this, executionResult);
        }

        private void VerifySnapshot(
            CaptureRunPublicationArtifactInspectionSnapshot snapshot,
            CaptureRunPublicationArtifactInspectionOperation operation)
        {
            if (snapshot == null
                || !snapshot.IsValid
                || !ReferenceEquals(snapshot.IssuedBy, _inspector)
                || !ReferenceEquals(snapshot.Operation, operation))
            {
                throw new InvalidOperationException(
                    "Inspection snapshot must be valid and issued for the inspection operation.");
            }
        }

        private void VerifyExecutionResult(
            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult,
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch)
        {
            if (executionResult == null
                || !executionResult.IsValid
                || !ReferenceEquals(executionResult.IssuedBy, _executionCoordinator)
                || !ReferenceEquals(executionResult.Batch, batch))
            {
                throw new InvalidOperationException(
                    "Execution result must be valid and issued for the execution batch.");
            }
        }
    }
}
