using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free factory that turns the single commit step of a PngJson
    /// artifact recovery action plan into an immutable Capture Index commit
    /// operation. It performs no filesystem work, no serialization, and no mode
    /// derivation, and mutates, owns, or disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The normal entry validates the whole plan once and issues a plan-bound
    /// validation token; the token-gated entry re-verifies only the targeted
    /// step. All full validation, mode derivation, and canonical serialization
    /// live in the operation and are never duplicated here.
    /// </para>
    /// </remarks>
    internal static class PngJsonCaptureRunCaptureIndexCommitOperationFactory
    {
        internal static PngJsonCaptureRunCaptureIndexCommitOperation Create(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            return PngJsonCaptureRunCaptureIndexCommitOperation.Create(actionPlan, stepIndex);
        }

        internal static PngJsonCaptureRunCaptureIndexCommitOperation CreateIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex)
        {
            return PngJsonCaptureRunCaptureIndexCommitOperation.CreateIndexLocal(actionPlan, token, stepIndex);
        }
    }
}
