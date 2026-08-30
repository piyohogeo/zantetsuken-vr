using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that turns an artifact recovery action plan into
    /// a fully materialized execution batch. It performs no filesystem work and
    /// mutates, owns, or disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder only null-checks the plan and delegates to the batch
    /// constructor, which validates the whole plan once and materializes every
    /// step. It does not allocate the step array, generate operations, or
    /// re-derive the step sequence, and it never retries, falls back, or wraps
    /// exceptions.
    /// </para>
    /// </remarks>
    internal static class CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder
    {
        internal static CaptureRunPublicationArtifactRecoveryExecutionBatch Build(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            return new CaptureRunPublicationArtifactRecoveryExecutionBatch(actionPlan);
        }
    }
}
