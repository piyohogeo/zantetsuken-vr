using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the PngJson capture publication artifact recovery pipeline
    /// exactly once in a fixed order: Inspection, Classification, Action Plan
    /// construction, Execution Batch construction, Recovery Execution, and
    /// finally the immutable Orchestration Result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly two read-only dependencies: one inspector
    /// (<see cref="IPngJsonCapturePublicationArtifactInspector"/>) and one
    /// recovery execution coordinator
    /// (<see cref="PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator"/>).
    /// Both are required and rejected with an
    /// <see cref="ArgumentNullException"/>. It owns, mutates, and disposes
    /// nothing and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject, and never touches a filesystem, store, registry, or
    /// session ownership lease.
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
    /// coordinator with that execution result and the proof from its single
    /// full validation.</item>
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
    /// and never disposes anything it touches.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator
    {
        private readonly IPngJsonCapturePublicationArtifactInspector _inspector;
        private readonly PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator _executionCoordinator;

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(
            IPngJsonCapturePublicationArtifactInspector inspector,
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator executionCoordinator)
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

        internal IPngJsonCapturePublicationArtifactInspector Inspector => _inspector;

        internal PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationResult Execute(
            PngJsonCapturePublicationArtifactInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Inspection operation must be valid.", nameof(operation));
            }

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = _inspector.Inspect(operation);
            VerifySnapshot(snapshot, operation);

            PngJsonCapturePublicationArtifactRecoveryDecision decision =
                PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(snapshot);

            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan =
                PngJsonCapturePublicationArtifactRecoveryActionPlanBuilder.Build(decision);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch =
                PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder.Build(actionPlan);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult =
                _executionCoordinator.Execute(batch);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken token;
            VerifyExecutionResult(executionResult, batch, out token);

            return PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(this, executionResult, token);
        }

        private void VerifySnapshot(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            PngJsonCapturePublicationArtifactInspectionOperation operation)
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
            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult,
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            out PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken token)
        {
            if (executionResult == null
                || !executionResult.TryValidate(out token)
                || !ReferenceEquals(executionResult.IssuedBy, _executionCoordinator)
                || !ReferenceEquals(executionResult.Batch, batch))
            {
                throw new InvalidOperationException(
                    "Execution result must be valid and issued for the execution batch.");
            }
        }
    }
}
