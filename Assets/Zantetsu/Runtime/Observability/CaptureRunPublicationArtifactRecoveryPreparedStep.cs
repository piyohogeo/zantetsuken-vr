using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable prepared artifact recovery step: the action plan, the step
    /// index, and at most one of a publish operation or a capture index commit
    /// operation, according to the step's fixed action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action-to-operation correlation is exclusive:
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.PublishArtifact"/>
    /// holds a publish operation only,
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex"/>
    /// holds a capture index commit operation only, and every routing or stop
    /// action holds neither.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — neither the action plan,
    /// the decision, the snapshot, the lease, nor the canonical bytes — and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryPreparedStep
    {
        private readonly CaptureRunPublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly CaptureRunPublicationArtifactPublishOperation _publishOperation;
        private readonly CaptureRunCaptureIndexCommitOperation _captureIndexCommitOperation;

        internal CaptureRunPublicationArtifactRecoveryPreparedStep(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex,
            CaptureRunPublicationArtifactPublishOperation publishOperation,
            CaptureRunCaptureIndexCommitOperation captureIndexCommitOperation)
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
            if (step == null || !step.IsValid)
            {
                throw new ArgumentException("Step must be valid.", nameof(stepIndex));
            }

            if (!IsPreparedStepValid(actionPlan, token, stepIndex, publishOperation, captureIndexCommitOperation, false))
            {
                throw new ArgumentException("Prepared step must satisfy its action's exclusive operation correlation.", nameof(publishOperation));
            }

            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _publishOperation = publishOperation;
            _captureIndexCommitOperation = captureIndexCommitOperation;
        }

        internal CaptureRunPublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationArtifactRecoveryAction Action => Step.Action;

        internal CaptureRunPublicationArtifactPublishOperation PublishOperation => _publishOperation;

        internal CaptureRunCaptureIndexCommitOperation CaptureIndexCommitOperation => _captureIndexCommitOperation;

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
                try
                {
                    token = _actionPlan.AcquireValidationToken();
                }
                catch (InvalidOperationException)
                {
                    return false;
                }

                return IsValidIndexLocal(token);
            }
        }

        internal bool IsValidIndexLocal(CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return IsPreparedStepValid(_actionPlan, token, _stepIndex, _publishOperation, _captureIndexCommitOperation, true);
        }

        private static bool IsPreparedStepValid(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex,
            CaptureRunPublicationArtifactPublishOperation publishOperation,
            CaptureRunCaptureIndexCommitOperation captureIndexCommitOperation,
            bool fullCommitValidation)
        {
            if (actionPlan == null || token == null || !token.IsIssuedFor(actionPlan))
            {
                return false;
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid)
            {
                return false;
            }

            switch (step.Action)
            {
                case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    return publishOperation != null
                        && captureIndexCommitOperation == null
                        && ReferenceEquals(publishOperation.ActionPlan, actionPlan)
                        && publishOperation.StepIndex == stepIndex
                        && publishOperation.IsValidIndexLocal(token);

                case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    return publishOperation == null
                        && captureIndexCommitOperation != null
                        && ReferenceEquals(captureIndexCommitOperation.ActionPlan, actionPlan)
                        && captureIndexCommitOperation.StepIndex == stepIndex
                        && (fullCommitValidation
                            ? captureIndexCommitOperation.IsValidWithToken(token)
                            : captureIndexCommitOperation.IsValidIndexLocal(token));

                case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                    return publishOperation == null && captureIndexCommitOperation == null;

                default:
                    return false;
            }
        }
    }
}
