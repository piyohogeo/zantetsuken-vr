using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free factory that turns one publish step of an artifact
    /// recovery action plan into an immutable publication operation. It
    /// performs no filesystem work and mutates, owns, or disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The normal entry validates the whole plan once, issues a plan-bound
    /// validation token, then re-verifies only the targeted step. The
    /// token-gated index-local entry is O(1) per step and is safe for batch
    /// materialization without quadratic behavior.
    /// </para>
    /// </remarks>
    internal static class CaptureRunPublicationArtifactPublishOperationFactory
    {
        internal static CaptureRunPublicationArtifactPublishOperation Create(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.IsValid)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan));
            }

            return CreateIndexLocal(actionPlan, actionPlan.AcquireValidationToken(), stepIndex);
        }

        internal static CaptureRunPublicationArtifactPublishOperation CreateIndexLocal(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!token.IsIssuedFor(actionPlan))
            {
                throw new ArgumentException("Token must be issued for this action plan.", nameof(token));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index must be within the step count.");
            }

            CaptureRunPublicationArtifactRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid || step.Action != CaptureRunPublicationArtifactRecoveryAction.PublishArtifact)
            {
                throw new ArgumentException("Step must be a valid publish artifact step.", nameof(stepIndex));
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = actionPlan.Decision.Snapshot;
            CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(step.EntryIndex);
            CaptureRunPublicationArtifactPathSet artifactPaths = observation.ArtifactPaths;

            return new CaptureRunPublicationArtifactPublishOperation(actionPlan, token, stepIndex, artifactPaths);
        }
    }
}
