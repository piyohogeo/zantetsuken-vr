using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that null-checks a shared PngJson artifact
    /// recovery decision and delegates to the action plan's atomic factory. It
    /// performs no filesystem work and does not re-validate the decision or
    /// re-derive the step sequence.
    /// </summary>
    internal static class PngJsonCapturePublicationArtifactRecoveryActionPlanBuilder
    {
        internal static PngJsonCapturePublicationArtifactRecoveryActionPlan Build(
            PngJsonCapturePublicationArtifactRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            return PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision);
        }
    }
}
