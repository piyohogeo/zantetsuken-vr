using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completed PngJson artifact recovery execution step: the exact
    /// prepared step, the one receipt produced by executing it (or none for a
    /// routing or stop step), and the exact action plan validation token used
    /// for issuance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action-to-receipt correlation is exclusive:
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.PublishArtifact"/>
    /// holds a publish receipt only,
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex"/>
    /// holds a commit receipt only, and every routing or stop action holds
    /// neither. The single token-gated index-local factory verifies the
    /// prepared step, the receipt shape, the exact operation reference, and the
    /// receipt's exact issuer/operation/token binding before issuance; it never
    /// re-validates the whole plan or re-issues a token.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — neither the action plan,
    /// the decision, the snapshot, the lease, nor the canonical bytes — and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryCompletedStep
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryPreparedStep _preparedStep;
        private readonly PngJsonCapturePublicationArtifactPublishReceipt _publishReceipt;
        private readonly PngJsonCaptureRunCaptureIndexCommitReceipt _commitReceipt;
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken _token;

        private PngJsonCapturePublicationArtifactRecoveryCompletedStep(
            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep,
            PngJsonCapturePublicationArtifactPublishReceipt publishReceipt,
            PngJsonCaptureRunCaptureIndexCommitReceipt commitReceipt,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            _preparedStep = preparedStep;
            _publishReceipt = publishReceipt;
            _commitReceipt = commitReceipt;
            _token = token;
        }

        /// <summary>
        /// O(1) token-gated atomic factory: re-verifies only the prepared step
        /// and its action's exclusive receipt correlation through the supplied
        /// token, requiring each receipt to be issued by the exact backend for
        /// the exact operation and token. It never re-validates the plan,
        /// re-issues a token, or scans an entry.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryCompletedStep CreateIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            IPngJsonCapturePublicationArtifactPublisher publisher,
            IPngJsonCaptureRunCaptureIndexCommitter committer,
            PngJsonCapturePublicationArtifactPublishReceipt publishReceipt,
            PngJsonCaptureRunCaptureIndexCommitReceipt commitReceipt)
        {
            if (preparedStep == null)
            {
                throw new ArgumentNullException(nameof(preparedStep));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!preparedStep.IsValidIndexLocal(token))
            {
                throw new ArgumentException("Prepared step must be index-locally valid for the issued token.", nameof(preparedStep));
            }

            switch (preparedStep.Action)
            {
                case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    if (publisher == null)
                    {
                        throw new ArgumentNullException(nameof(publisher));
                    }

                    if (publishReceipt == null || commitReceipt != null)
                    {
                        throw new ArgumentException("A publish step requires exactly one publish receipt.", nameof(publishReceipt));
                    }

                    if (!publishReceipt.IsIssuedFor(publisher, preparedStep.PublishOperation, token))
                    {
                        throw new ArgumentException("Publish receipt must be issued by the publisher for the exact operation and token.", nameof(publishReceipt));
                    }

                    break;

                case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    if (committer == null)
                    {
                        throw new ArgumentNullException(nameof(committer));
                    }

                    if (publishReceipt != null || commitReceipt == null)
                    {
                        throw new ArgumentException("A commit step requires exactly one commit receipt.", nameof(commitReceipt));
                    }

                    if (!commitReceipt.IsIssuedFor(committer, preparedStep.CaptureIndexCommitOperation, token))
                    {
                        throw new ArgumentException("Commit receipt must be issued by the committer for the exact operation and token.", nameof(commitReceipt));
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
                        throw new ArgumentException("Routing and stop steps hold no receipt.", nameof(publishReceipt));
                    }

                    break;

                default:
                    throw new ArgumentException("Step action must be a defined artifact recovery action.", nameof(preparedStep));
            }

            return new PngJsonCapturePublicationArtifactRecoveryCompletedStep(
                preparedStep, publishReceipt, commitReceipt, token);
        }

        internal PngJsonCapturePublicationArtifactRecoveryPreparedStep PreparedStep => _preparedStep;

        internal PngJsonCapturePublicationArtifactPublishReceipt PublishReceipt => _publishReceipt;

        internal PngJsonCaptureRunCaptureIndexCommitReceipt CommitReceipt => _commitReceipt;

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _preparedStep.ActionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _preparedStep.Decision;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _preparedStep.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _preparedStep.AuthorityKind;

        internal int StepIndex => _preparedStep.StepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _preparedStep.Step;

        internal CaptureRunPublicationArtifactRecoveryAction Action => _preparedStep.Action;

        internal PngJsonCapturePublicationArtifactPublishOperation PublishOperation => _preparedStep.PublishOperation;

        internal PngJsonCaptureRunCaptureIndexCommitOperation CaptureIndexCommitOperation => _preparedStep.CaptureIndexCommitOperation;

        /// <summary>
        /// O(1), exception-safe index-local validity: re-verifies the exact
        /// held token identity, the prepared step, and its action's exclusive
        /// receipt shape and issuer/operation/token correlation. It never
        /// re-serializes a commit operation's canonical bytes, re-validates the
        /// whole plan, or re-issues a token.
        /// </summary>
        internal bool IsValidIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            try
            {
                if (_preparedStep == null || token == null || _token == null)
                {
                    return false;
                }

                if (!ReferenceEquals(token, _token))
                {
                    return false;
                }

                if (!_preparedStep.IsValidIndexLocal(token))
                {
                    return false;
                }

                switch (_preparedStep.Action)
                {
                    case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                        return _publishReceipt != null
                            && _commitReceipt == null
                            && _publishReceipt.IsIssuedFor(_publishReceipt.IssuedBy, _preparedStep.PublishOperation, _token);

                    case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                        return _publishReceipt == null
                            && _commitReceipt != null
                            && _commitReceipt.IsIssuedForIndexLocal(_commitReceipt.IssuedBy, _preparedStep.CaptureIndexCommitOperation, _token);

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
            catch (Exception)
            {
                return false;
            }
        }
    }
}
