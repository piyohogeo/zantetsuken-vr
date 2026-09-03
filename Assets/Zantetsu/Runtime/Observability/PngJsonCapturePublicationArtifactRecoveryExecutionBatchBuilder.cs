using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that turns a PngJson artifact recovery action
    /// plan into a fully materialized execution batch. It performs no
    /// filesystem work and mutates, owns, or disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder only null-checks the plan and delegates to the batch's
    /// atomic factory, which validates the whole plan once and materializes
    /// every step. It does not allocate the step array, generate operations,
    /// serialize, or derive a mode, and it never retries, falls back, or wraps
    /// exceptions.
    /// </para>
    /// </remarks>
    internal static class PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder
    {
        internal static PngJsonCapturePublicationArtifactRecoveryExecutionBatch Build(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            return PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(actionPlan);
        }
    }
}
