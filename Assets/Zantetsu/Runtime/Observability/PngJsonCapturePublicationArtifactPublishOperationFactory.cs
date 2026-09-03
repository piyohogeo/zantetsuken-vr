using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free factory that turns one publish step of a PngJson
    /// artifact recovery action plan into an immutable publication operation.
    /// It performs no filesystem work and mutates, owns, or disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> validates the whole plan once, issues a plan-bound
    /// validation token, and then delegates to <see cref="CreateIndexLocal"/>.
    /// The token-gated index-local entry is O(1) per step and is safe for batch
    /// materialization without quadratic behavior.
    /// </para>
    /// </remarks>
    internal static class PngJsonCapturePublicationArtifactPublishOperationFactory
    {
        internal static PngJsonCapturePublicationArtifactPublishOperation Create(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            return PngJsonCapturePublicationArtifactPublishOperation.Create(actionPlan, stepIndex);
        }

        internal static PngJsonCapturePublicationArtifactPublishOperation CreateIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex)
        {
            return PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, stepIndex);
        }
    }
}
