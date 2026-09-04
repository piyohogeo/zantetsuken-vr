using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Executes a PngJson capture-complete cleanup batch against one backend,
    /// contacting the backend exactly once per side-effecting step in ascending
    /// order and verifying each returned receipt before issuing a completed
    /// step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly one field: the backend. <see cref="Execute"/>
    /// validates the batch exactly once outside the loop, re-confirms each
    /// prepared step index-locally immediately before backend contact, calls
    /// the backend exactly once per side-effecting step, and verifies the
    /// returned receipt before materializing the completed step. The
    /// <c>CaptureCompleteReady</c> routing step never contacts the backend and
    /// carries no receipt.
    /// </para>
    /// <para>
    /// A backend exception propagates unchanged and stops the loop, so no
    /// subsequent step is contacted and no partial result is returned. A null,
    /// foreign-issuer, other-operation, other-token, or invalid receipt throws
    /// <see cref="InvalidOperationException"/> immediately.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteCleanupBackend _backend;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            _backend = backend;
        }

        internal IPngJsonCapturePublicationCaptureCompleteCleanupBackend Backend => _backend;

        /// <summary>
        /// Executes the batch: validates it exactly once to acquire the action
        /// plan token, then walks every prepared step in ascending order,
        /// re-confirming each step index-locally before backend contact, calling
        /// the backend exactly once per side-effecting step, and verifying each
        /// receipt before issuing the completed step. The result is published
        /// only after every step succeeds.
        /// </summary>
        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult Execute(
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (!batch.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
            {
                throw new ArgumentException(
                    "Execution batch must be a valid capture-complete cleanup batch.",
                    nameof(batch));
            }

            int count = batch.Count;
            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] completedSteps =
                new PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[count];

            for (int i = 0; i < count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                if (prepared == null
                    || prepared.StepIndex != i
                    || !prepared.IsValidIndexLocal(token))
                {
                    throw new InvalidOperationException(
                        "Prepared step at index " + i + " is no longer the exact validated step.");
                }

                PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = null;
                if (prepared.Action != CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    receipt = _backend.Execute(prepared.CleanupOperation, token);

                    if (receipt == null || !receipt.IsIssuedFor(_backend, prepared.CleanupOperation, token))
                    {
                        throw new InvalidOperationException(
                            "Cleanup receipt must be issued by this backend for the prepared step's exact operation and token.");
                    }
                }

                completedSteps[i] = PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(
                    _backend, prepared, receipt, token);
            }

            return PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.Create(
                this, batch, completedSteps, token);
        }
    }
}
