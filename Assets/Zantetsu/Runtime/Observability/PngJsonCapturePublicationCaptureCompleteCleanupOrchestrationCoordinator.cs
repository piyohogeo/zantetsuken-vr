using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects one PngJson capture publication artifact recovery result to the
    /// capture-complete cleanup execution pipeline exactly once in a fixed
    /// order: Cleanup Action Plan construction, Execution Batch construction,
    /// Cleanup Execution, full Execution Result validation, and finally the
    /// immutable Orchestration Result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly one read-only dependency: the cleanup
    /// execution coordinator
    /// (<see cref="PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator"/>).
    /// It is required and rejected with an <see cref="ArgumentNullException"/>.
    /// The coordinator owns, mutates, and disposes nothing, is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject, and never
    /// touches a filesystem, store, registry, or session ownership lease.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the exact sequence once per call:
    /// <list type="number">
    /// <item>Reject a null recovery result with an
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>recoveryResult</c>, before any dependency is contacted.</item>
    /// <item>Build the cleanup action plan from the recovery result exactly
    /// once.</item>
    /// <item>Build the execution batch from the action plan exactly once.</item>
    /// <item>Execute the batch exactly once through the held execution
    /// coordinator.</item>
    /// <item>Fully validate the returned execution result exactly once,
    /// acquiring its single validation token.</item>
    /// <item>Verify the exact correlation between this coordinator, the
    /// execution result, the token, the built batch, the built plan, and the
    /// input recovery result; otherwise throw an
    /// <see cref="InvalidOperationException"/>.</item>
    /// <item>Return an immutable orchestration result through the single
    /// issuance factory.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The coordinator never retries, never rolls back, never re-derives the
    /// action plan or batch, never notifies, and never acquires, transfers, or
    /// releases a session ownership lease. The execution result's single full
    /// validation re-verifies the current state of its completed steps and
    /// action plan with the already-held proof, without re-issuing a token.
    /// Exceptions thrown by the plan builder, batch builder, execution
    /// coordinator, or backend propagate unchanged, with no compensating
    /// action.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator _executionCoordinator;

        internal PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator executionCoordinator)
        {
            if (executionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(executionCoordinator));
            }

            _executionCoordinator = executionCoordinator;
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

        internal PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult Execute(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult recoveryResult)
        {
            if (recoveryResult == null)
            {
                throw new ArgumentNullException(nameof(recoveryResult));
            }

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.Create(recoveryResult);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch =
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.Create(actionPlan);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult executionResult =
                _executionCoordinator.Execute(batch);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token;
            VerifyExecutionResult(executionResult, batch, actionPlan, recoveryResult, out token);

            return PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult.Create(this, executionResult, token);
        }

        private void VerifyExecutionResult(
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult executionResult,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult recoveryResult,
            out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token)
        {
            if (executionResult == null
                || !executionResult.TryValidate(out token)
                || !ReferenceEquals(executionResult.IssuedBy, _executionCoordinator)
                || !ReferenceEquals(executionResult.Batch, batch)
                || !ReferenceEquals(executionResult.ActionPlan, actionPlan)
                || !ReferenceEquals(executionResult.OrchestrationResult, recoveryResult)
                || executionResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
            {
                throw new InvalidOperationException(
                    "Execution result must be valid, capture-complete-ready, and correlated with this coordinator and the built plan, batch, and recovery result.");
            }
        }
    }
}
