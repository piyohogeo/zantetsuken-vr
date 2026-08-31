using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the capture run publication capture-complete cleanup pipeline
    /// exactly once, in a fixed order: Cleanup Action Plan construction, Cleanup
    /// Execution Batch construction, Cleanup Execution, and finally the
    /// immutable Cleanup Orchestration Result carrying
    /// <c>CaptureCompleteReady</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly one read-only dependency — the cleanup
    /// execution coordinator — and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject. It holds no builder, action plan,
    /// batch, or result in any field.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the exact sequence once per call:
    /// <list type="number">
    /// <item>Reject a null recovery result with an
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>recoveryResult</c>.</item>
    /// <item>Build the cleanup action plan from the recovery result exactly
    /// once. The pure builder and plan constructor are the single full
    /// validation boundary, so the coordinator does not re-validate the
    /// recovery result beforehand.</item>
    /// <item>Build the cleanup execution batch from the action plan exactly
    /// once.</item>
    /// <item>Execute the batch through the held execution coordinator exactly
    /// once.</item>
    /// <item>Fully verify the returned execution result: non-null, a single
    /// successful validation, issued by the held execution coordinator,
    /// bound to the same batch, action plan, and recovery result, and carrying
    /// <c>CaptureCompleteReady</c>.</item>
    /// <item>Construct the immutable orchestration result through its trusted
    /// constructor using the already acquired execution-result token.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Exceptions thrown by the plan builder, the batch builder, the execution
    /// coordinator, or the backend propagate unchanged and unwrapped. The
    /// coordinator performs no retry, rollback, compensating deletion, no
    /// automatic re-inspection, no draft registry release, no completion
    /// notification, and no OS lock release or lease transfer, and it never
    /// disposes the lease, any result, batch, or plan, and never contacts the
    /// filesystem, clock, randomness, or id sources directly.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator _executionCoordinator;

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator executionCoordinator)
        {
            if (executionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(executionCoordinator));
            }

            _executionCoordinator = executionCoordinator;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult Execute(
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recoveryResult)
        {
            if (recoveryResult == null)
            {
                throw new ArgumentNullException(nameof(recoveryResult));
            }

            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(recoveryResult);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch =
                CaptureRunPublicationCaptureCompleteCleanupExecutionBatchBuilder.Build(actionPlan);

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult =
                _executionCoordinator.Execute(batch);

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token =
                VerifyExecutionResult(executionResult, batch, actionPlan, recoveryResult);

            return new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(this, executionResult, token);
        }

        private CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken VerifyExecutionResult(
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recoveryResult)
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token;
            if (executionResult == null
                || !executionResult.TryValidate(out token)
                || !ReferenceEquals(executionResult.IssuedBy, _executionCoordinator)
                || !ReferenceEquals(executionResult.Batch, batch)
                || !ReferenceEquals(executionResult.ActionPlan, actionPlan)
                || !ReferenceEquals(executionResult.OrchestrationResult, recoveryResult)
                || executionResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
            {
                throw new InvalidOperationException(
                    "Execution result must be valid and correlated with the cleanup batch, plan, and recovery result.");
            }

            return token;
        }
    }
}
