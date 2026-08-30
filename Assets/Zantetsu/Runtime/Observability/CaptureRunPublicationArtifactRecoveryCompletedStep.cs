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
    /// the caller's already-issued plan validation token for index-local, O(1)
    /// checks and never re-validates the whole plan or re-serializes canonical
    /// bytes. <see cref="IsValid"/> recomputes the correlation from the held
    /// values — including the plan's lease liveness — without throwing.
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

            switch (preparedStep.Action)
            {
                case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    if (publishReceipt == null)
                    {
                        throw new ArgumentException("Publish action requires a publish receipt.", nameof(publishReceipt));
                    }

                    if (commitReceipt != null)
                    {
                        throw new ArgumentException("Publish action must not hold a commit receipt.", nameof(publishReceipt));
                    }

                    if (preparedStep.PublishOperation == null)
                    {
                        throw new ArgumentException("Publish action requires a publish operation.", nameof(preparedStep));
                    }

                    if (!ReferenceEquals(publishReceipt.Operation, preparedStep.PublishOperation))
                    {
                        throw new ArgumentException("Publish receipt must match the prepared publish operation.", nameof(publishReceipt));
                    }

                    if (publishReceipt.IssuedBy == null)
                    {
                        throw new ArgumentException("Publish receipt must be valid.", nameof(publishReceipt));
                    }

                    if (!preparedStep.PublishOperation.IsValidIndexLocal(token))
                    {
                        throw new ArgumentException("Publish operation must be valid.", nameof(preparedStep));
                    }

                    break;

                case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    if (commitReceipt == null)
                    {
                        throw new ArgumentException("Commit action requires a commit receipt.", nameof(commitReceipt));
                    }

                    if (publishReceipt != null)
                    {
                        throw new ArgumentException("Commit action must not hold a publish receipt.", nameof(commitReceipt));
                    }

                    if (preparedStep.CaptureIndexCommitOperation == null)
                    {
                        throw new ArgumentException("Commit action requires a commit operation.", nameof(preparedStep));
                    }

                    if (!ReferenceEquals(commitReceipt.Operation, preparedStep.CaptureIndexCommitOperation))
                    {
                        throw new ArgumentException("Commit receipt must match the prepared commit operation.", nameof(commitReceipt));
                    }

                    if (commitReceipt.IssuedBy == null)
                    {
                        throw new ArgumentException("Commit receipt must be valid.", nameof(commitReceipt));
                    }

                    if (!preparedStep.CaptureIndexCommitOperation.IsValidIndexLocal(token))
                    {
                        throw new ArgumentException("Commit operation must be valid.", nameof(preparedStep));
                    }

                    break;

                case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                    if (publishReceipt != null || commitReceipt != null)
                    {
                        throw new ArgumentException("Routing or stop step must not hold a receipt.", nameof(publishReceipt));
                    }

                    break;

                default:
                    throw new ArgumentException("Prepared step action must be a defined recovery action.", nameof(preparedStep));
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
                if (actionPlan == null || !actionPlan.IsDecisionLeaseLive())
                {
                    return false;
                }

                switch (_preparedStep.Action)
                {
                    case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                        return _publishReceipt != null
                            && _commitReceipt == null
                            && _preparedStep.PublishOperation != null
                            && _publishReceipt.IssuedBy != null
                            && ReferenceEquals(_publishReceipt.Operation, _preparedStep.PublishOperation);

                    case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                        return _publishReceipt == null
                            && _commitReceipt != null
                            && _preparedStep.CaptureIndexCommitOperation != null
                            && _commitReceipt.IssuedBy != null
                            && ReferenceEquals(_commitReceipt.Operation, _preparedStep.CaptureIndexCommitOperation);

                    case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                    case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                    case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                    case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                    case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                    case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                        return _publishReceipt == null && _commitReceipt == null;

                    default:
                        return false;
                }
            }
        }
    }
}
