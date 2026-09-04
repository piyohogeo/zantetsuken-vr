using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless builder that converts one PngJson capture-complete
    /// cleanup action plan into an execution batch. It holds no fields and
    /// performs no filesystem work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder rejects a null plan and otherwise delegates exactly once to
    /// the batch's atomic factory, which owns the prepared-step array and
    /// performs the single full validation, allocation, and step walk.
    /// </para>
    /// </remarks>
    internal static class PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatchBuilder
    {
        internal static PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch Build(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            return PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.Create(actionPlan);
        }
    }
}
