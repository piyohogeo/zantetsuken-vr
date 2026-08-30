using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that validates an artifact recovery decision
    /// and delegates to the action plan constructor. It performs no filesystem
    /// work and mutates, owns, or disposes nothing.
    /// </summary>
    internal static class CaptureRunPublicationArtifactRecoveryActionPlanBuilder
    {
        internal static CaptureRunPublicationArtifactRecoveryActionPlan Build(
            CaptureRunPublicationArtifactRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException("Decision must be valid.", nameof(decision));
            }

            return new CaptureRunPublicationArtifactRecoveryActionPlan(decision);
        }
    }
}
