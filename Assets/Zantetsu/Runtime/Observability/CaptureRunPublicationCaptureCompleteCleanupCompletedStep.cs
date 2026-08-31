using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completed step of a capture-complete cleanup execution: the
    /// prepared step plus the one cleanup receipt produced by executing it, or
    /// no receipt for the routing <c>CaptureCompleteReady</c> step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one receipt is required for each of the eight side-effecting
    /// cleanup actions; the routing <c>CaptureCompleteReady</c> step holds
    /// none. Each receipt's operation must be the same reference as the
    /// prepared step's cleanup operation. The constructor uses the caller's
    /// already-issued plan validation token for index-local checks and never
    /// re-validates the whole plan; the receipt issuer is correlated to the
    /// issuing coordinator's backend by the execution result, which is the
    /// only holder of that dependency. <see cref="IsValid"/> recomputes the
    /// correlation from the held values — including the plan's lease liveness —
    /// without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupCompletedStep
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupPreparedStep _preparedStep;
        private readonly CaptureRunPublicationCaptureCompleteCleanupReceipt _cleanupReceipt;

        internal CaptureRunPublicationCaptureCompleteCleanupCompletedStep(
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep preparedStep,
            CaptureRunPublicationCaptureCompleteCleanupReceipt cleanupReceipt,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (preparedStep == null)
            {
                throw new ArgumentNullException(nameof(preparedStep));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!token.IsIssuedFor(preparedStep.ActionPlan))
            {
                throw new ArgumentException("Token must be issued for the prepared step's action plan.", nameof(token));
            }

            if (!IsCorrelatedIndexLocal(preparedStep, cleanupReceipt, token))
            {
                throw new ArgumentException("Completed step must satisfy its action's receipt and operation correlation.", nameof(preparedStep));
            }

            _preparedStep = preparedStep;
            _cleanupReceipt = cleanupReceipt;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupPreparedStep PreparedStep => _preparedStep;

        internal CaptureRunPublicationCaptureCompleteCleanupReceipt CleanupReceipt => _cleanupReceipt;

        internal bool IsValid
        {
            get
            {
                if (_preparedStep == null)
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan = _preparedStep.ActionPlan;
                if (actionPlan == null)
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
                try
                {
                    token = actionPlan.AcquireValidationToken();
                }
                catch (InvalidOperationException)
                {
                    return false;
                }

                return IsValidIndexLocal(token);
            }
        }

        /// <summary>
        /// Token-gated, exception-safe validity: re-verifies the whole prepared
        /// step index-locally and then confirms the receipt shape and operation
        /// correlation. Never throws.
        /// </summary>
        internal bool IsValidIndexLocal(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            return IsCorrelatedIndexLocal(_preparedStep, _cleanupReceipt, token);
        }

        private static bool IsCorrelatedIndexLocal(
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep preparedStep,
            CaptureRunPublicationCaptureCompleteCleanupReceipt cleanupReceipt,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (preparedStep == null || token == null)
            {
                return false;
            }

            if (!preparedStep.IsValidIndexLocal(token))
            {
                return false;
            }

            switch (preparedStep.Action)
            {
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                    return cleanupReceipt != null
                        && cleanupReceipt.IssuedBy != null
                        && ReferenceEquals(cleanupReceipt.Operation, preparedStep.CleanupOperation);

                case CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady:
                    return cleanupReceipt == null;

                default:
                    return false;
            }
        }
    }
}
