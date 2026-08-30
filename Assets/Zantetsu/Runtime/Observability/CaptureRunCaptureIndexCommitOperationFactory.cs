using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free factory that turns the single commit step of an
    /// artifact recovery action plan into an immutable Capture Index commit
    /// operation. It performs no filesystem work and mutates, owns, or
    /// disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The normal entry validates the whole plan once and issues a plan-bound
    /// validation token; the token-gated entry re-verifies only the targeted
    /// step. The canonical bytes are serialized exactly once and their
    /// ownership is transferred to the operation only after it succeeds.
    /// </para>
    /// </remarks>
    internal static class CaptureRunCaptureIndexCommitOperationFactory
    {
        internal static CaptureRunCaptureIndexCommitOperation Create(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
            try
            {
                token = actionPlan.AcquireValidationToken();
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan), ex);
            }

            return CreateIndexLocal(actionPlan, token, stepIndex);
        }

        internal static CaptureRunCaptureIndexCommitOperation CreateIndexLocal(
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
            if (step == null || !step.IsValid
                || step.Action != CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex)
            {
                throw new ArgumentException("Step must be a valid commit capture index step.", nameof(stepIndex));
            }

            CaptureRunPublicationRecoveryDecision publicationDecision = actionPlan.Decision.PublicationDecision;
            if (publicationDecision == null)
            {
                throw new ArgumentException("Action plan must hold a publication decision.", nameof(actionPlan));
            }

            PngJsonCapturePublicationPlan authoritativePlan = publicationDecision.AuthoritativePlan;
            if (authoritativePlan == null)
            {
                throw new ArgumentException("Publication decision must hold an authoritative plan.", nameof(actionPlan));
            }

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken =
                CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken.Acquire(authoritativePlan);

            CaptureRunCaptureIndexCommitOperation operation = new CaptureRunCaptureIndexCommitOperation(
                actionPlan, token, stepIndex, ref bytesToken);

            if (bytesToken != null)
            {
                throw new InvalidOperationException("Canonical bytes token must be transferred to the operation.");
            }

            return operation;
        }
    }
}
