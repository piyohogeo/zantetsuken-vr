using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completed step of a publication artifact recovery execution:
    /// the prepared step plus the one receipt produced by executing it, or no
    /// receipt for a routing or stop step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one receipt is required for a publish or commit action; a
    /// routing or stop step holds none. Each receipt's operation must be the
    /// same reference as the prepared step's operation. The constructor uses
    /// the caller's already-issued plan validation token for index-local checks
    /// and never re-validates the whole plan; a commit step additionally
    /// re-verifies the held canonical bytes against the authoritative plan.
    /// <see cref="IsValid"/> recomputes the correlation from the held values —
    /// including the plan's lease liveness — without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryCompletedStep
    {
        private readonly CaptureRunPublicationArtifactRecoveryPreparedStep _preparedStep;
        private readonly CaptureRunPublicationArtifactPublishReceipt _publishReceipt;
        private readonly CaptureRunCaptureIndexCommitReceipt _commitReceipt;

        internal CaptureRunPublicationArtifactRecoveryCompletedStep(
            CaptureRunPublicationArtifactRecoveryPreparedStep preparedStep,
            CaptureRunPublicationArtifactPublishReceipt publishReceipt,
            CaptureRunCaptureIndexCommitReceipt commitReceipt,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
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

            if (!IsCorrelatedIndexLocal(preparedStep, publishReceipt, commitReceipt, token))
            {
                throw new ArgumentException("Completed step must satisfy its action's receipt and operation correlation.", nameof(preparedStep));
            }

            _preparedStep = preparedStep;
            _publishReceipt = publishReceipt;
            _commitReceipt = commitReceipt;
        }

        internal CaptureRunPublicationArtifactRecoveryPreparedStep PreparedStep => _preparedStep;

        internal CaptureRunPublicationArtifactPublishReceipt PublishReceipt => _publishReceipt;

        internal CaptureRunCaptureIndexCommitReceipt CommitReceipt => _commitReceipt;

        internal bool IsValid
        {
            get
            {
                if (_preparedStep == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = _preparedStep.ActionPlan;
                if (actionPlan == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
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
        /// step index-locally — for a commit step this also re-verifies the held
        /// canonical bytes against the authoritative plan — and then confirms the
        /// receipt shape and operation correlation. Never throws.
        /// </summary>
        internal bool IsValidIndexLocal(
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return IsCorrelatedIndexLocal(_preparedStep, _publishReceipt, _commitReceipt, token);
        }

        private static bool IsCorrelatedIndexLocal(
            CaptureRunPublicationArtifactRecoveryPreparedStep preparedStep,
            CaptureRunPublicationArtifactPublishReceipt publishReceipt,
            CaptureRunCaptureIndexCommitReceipt commitReceipt,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
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
                case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    return publishReceipt != null
                        && commitReceipt == null
                        && publishReceipt.IssuedBy != null
                        && ReferenceEquals(publishReceipt.Operation, preparedStep.PublishOperation);

                case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    return publishReceipt == null
                        && commitReceipt != null
                        && commitReceipt.IssuedBy != null
                        && ReferenceEquals(commitReceipt.Operation, preparedStep.CaptureIndexCommitOperation);

                case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                    return publishReceipt == null && commitReceipt == null;

                default:
                    return false;
            }
        }
    }
}
