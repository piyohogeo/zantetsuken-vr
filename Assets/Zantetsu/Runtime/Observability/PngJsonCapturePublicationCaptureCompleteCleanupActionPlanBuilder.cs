using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that null-checks a PngJson capture-complete
    /// cleanup orchestration result and delegates to the cleanup action plan
    /// factory, which is the single full-validation path. It performs no
    /// result re-validation, no action recomputation, no array copy, no
    /// filesystem work, and mutates, owns, or disposes nothing.
    /// </summary>
    internal static class PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder
    {
        internal static PngJsonCapturePublicationCaptureCompleteCleanupActionPlan Build(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult orchestrationResult)
        {
            if (orchestrationResult == null)
            {
                throw new ArgumentNullException(nameof(orchestrationResult));
            }

            // Delegate the full validation to the plan factory, which is the
            // single validation path; validating the result here would walk
            // the artifact and receipt graph twice on the success path.
            return PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.Create(orchestrationResult);
        }
    }
}
