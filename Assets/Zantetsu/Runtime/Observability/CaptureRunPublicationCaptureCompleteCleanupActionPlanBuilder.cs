using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that validates a capture-complete cleanup
    /// orchestration result and delegates to the cleanup action plan
    /// constructor. It performs no filesystem work and mutates, owns, or
    /// disposes nothing.
    /// </summary>
    internal static class CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder
    {
        internal static CaptureRunPublicationCaptureCompleteCleanupActionPlan Build(
            CaptureRunPublicationArtifactRecoveryOrchestrationResult orchestrationResult)
        {
            if (orchestrationResult == null)
            {
                throw new ArgumentNullException(nameof(orchestrationResult));
            }

            if (!orchestrationResult.IsValid)
            {
                throw new ArgumentException("Orchestration result must be valid.", nameof(orchestrationResult));
            }

            return new CaptureRunPublicationCaptureCompleteCleanupActionPlan(orchestrationResult);
        }
    }
}
