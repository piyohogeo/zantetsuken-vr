using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that null-checks a capture-complete cleanup
    /// orchestration result and delegates to the cleanup action plan
    /// constructor, which is the single full-validation path. It performs no
    /// filesystem work and mutates, owns, or disposes nothing.
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

            // Delegate the full validation to the plan constructor, which is
            // the single validation path; validating the result here would
            // walk the artifact and receipt graph twice on the success path.
            return new CaptureRunPublicationCaptureCompleteCleanupActionPlan(orchestrationResult);
        }
    }
}
