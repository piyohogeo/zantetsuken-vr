using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable execution batch that materializes every step of an artifact
    /// recovery action plan into a prepared step before any side effect runs.
    /// The prepared step array is allocated exactly once at its exact length and
    /// is never exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor validates the whole action plan once and issues a single
    /// plan-bound validation token, then materializes each step in ascending
    /// index order using the index-local factory paths, keeping the whole
    /// construction O(n). <see cref="IsValid"/> recomputes the same
    /// correlations without throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — neither the action plan,
    /// the lease, the operations, nor the canonical bytes — and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryExecutionBatch
    {
        private readonly CaptureRunPublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly CaptureRunPublicationArtifactRecoveryPreparedStep[] _preparedSteps;

        internal CaptureRunPublicationArtifactRecoveryExecutionBatch(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan)
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

            if (!token.IsIssuedFor(actionPlan))
            {
                throw new ArgumentException("Token must be issued for this action plan.", nameof(actionPlan));
            }

            int count = checked(actionPlan.Count);

            CaptureRunPublicationArtifactRecoveryPreparedStep[] preparedSteps =
                new CaptureRunPublicationArtifactRecoveryPreparedStep[count];

            for (int i = 0; i < count; i++)
            {
                CaptureRunPublicationArtifactRecoveryStep step = actionPlan.GetStep(i);

                switch (step.Action)
                {
                    case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                        preparedSteps[i] = new CaptureRunPublicationArtifactRecoveryPreparedStep(
                            actionPlan,
                            token,
                            i,
                            CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(actionPlan, token, i),
                            null);
                        break;

                    case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                        preparedSteps[i] = new CaptureRunPublicationArtifactRecoveryPreparedStep(
                            actionPlan,
                            token,
                            i,
                            null,
                            CaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(actionPlan, token, i));
                        break;

                    default:
                        preparedSteps[i] = new CaptureRunPublicationArtifactRecoveryPreparedStep(
                            actionPlan, token, i, null, null);
                        break;
                }
            }

            _actionPlan = actionPlan;
            _preparedSteps = preparedSteps;
        }

        internal CaptureRunPublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal CaptureRunPublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _actionPlan.Disposition;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal CaptureRunLockLease LockLease => _actionPlan.Decision.Snapshot.Operation.LockLease;

        internal int Count => _preparedSteps.Length;

        internal CaptureRunPublicationArtifactRecoveryPreparedStep GetStep(int index)
        {
            if (index < 0 || index >= _preparedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the step count.");
            }

            return _preparedSteps[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || _preparedSteps == null)
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

                if (_preparedSteps.Length != _actionPlan.Count)
                {
                    return false;
                }

                for (int i = 0; i < _preparedSteps.Length; i++)
                {
                    CaptureRunPublicationArtifactRecoveryPreparedStep preparedStep = _preparedSteps[i];
                    if (preparedStep == null
                        || preparedStep.StepIndex != i
                        || !ReferenceEquals(preparedStep.ActionPlan, _actionPlan)
                        || !preparedStep.IsValidIndexLocal(token))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
