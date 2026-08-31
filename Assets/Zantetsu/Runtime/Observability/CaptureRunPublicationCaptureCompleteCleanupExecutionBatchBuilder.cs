using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless builder that converts one capture-complete cleanup
    /// action plan into an execution batch. It holds no fields and performs no
    /// filesystem work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder rejects a null plan and otherwise delegates directly to the
    /// <see cref="CaptureRunPublicationCaptureCompleteCleanupExecutionBatch"/>
    /// constructor, which owns the prepared-step array and performs the single
    /// full validation, path set construction, allocation, and step walk.
    /// </para>
    /// </remarks>
    internal static class CaptureRunPublicationCaptureCompleteCleanupExecutionBatchBuilder
    {
        internal static CaptureRunPublicationCaptureCompleteCleanupExecutionBatch Build(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            return new CaptureRunPublicationCaptureCompleteCleanupExecutionBatch(actionPlan);
        }
    }
}
