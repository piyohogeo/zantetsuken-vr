using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, fixed action plan derived from an artifact recovery decision
    /// before any side effect runs. It owns exactly one step array computed by
    /// its constructor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor recomputes the single expected step sequence from the
    /// decision, so no external caller can hand in a contradicting step list.
    /// The step array is allocated exactly once at its exact length and filled
    /// in ascending entry order. <see cref="IsValid"/> recomputes the decision
    /// once and compares the held steps in one linear pass without throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryActionPlan
    {
        private readonly CaptureRunPublicationArtifactRecoveryDecision _decision;
        private readonly CaptureRunPublicationArtifactRecoveryStep[] _steps;

        internal CaptureRunPublicationArtifactRecoveryActionPlan(
            CaptureRunPublicationArtifactRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException("Decision must be valid.", nameof(decision));
            }

            CaptureRunPublicationArtifactRecoveryStep[] steps;
            CaptureRunPublicationArtifactRecoveryDisposition disposition = decision.Disposition;

            switch (disposition)
            {
                case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                    steps = new[]
                    {
                        new CaptureRunPublicationArtifactRecoveryStep(
                            CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace, -1, CaptureRunPublicationArtifactKind.None)
                    };
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                    steps = BuildPublishSteps(decision);
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                    steps = new[]
                    {
                        new CaptureRunPublicationArtifactRecoveryStep(
                            CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex, -1, CaptureRunPublicationArtifactKind.None)
                    };
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                    steps = new[]
                    {
                        new CaptureRunPublicationArtifactRecoveryStep(
                            CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup, -1, CaptureRunPublicationArtifactKind.None)
                    };
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                    steps = new[]
                    {
                        new CaptureRunPublicationArtifactRecoveryStep(
                            CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing, -1, CaptureRunPublicationArtifactKind.None)
                    };
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                    steps = new[]
                    {
                        new CaptureRunPublicationArtifactRecoveryStep(
                            CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing, -1, CaptureRunPublicationArtifactKind.None)
                    };
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                    steps = new[]
                    {
                        new CaptureRunPublicationArtifactRecoveryStep(
                            CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision, -1, CaptureRunPublicationArtifactKind.None)
                    };
                    break;

                default:
                    throw new ArgumentException("Disposition must be a defined artifact recovery disposition.", nameof(decision));
            }

            _decision = decision;
            _steps = steps;
        }

        internal CaptureRunPublicationArtifactRecoveryDecision Decision => _decision;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _decision.Disposition;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _decision.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        internal int Count => _steps.Length;

        internal CaptureRunPublicationArtifactRecoveryStep GetStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the step count.");
            }

            return _steps[index];
        }

        /// <summary>
        /// Proof that this action plan was fully validated. Only this plan can
        /// mint tokens, so callers cannot substitute a cheap check for the
        /// full <see cref="IsValid"/> pass.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly CaptureRunPublicationArtifactRecoveryActionPlan _plan;

            private ValidationToken(CaptureRunPublicationArtifactRecoveryActionPlan plan)
            {
                _plan = plan;
            }

            internal bool IsIssuedFor(CaptureRunPublicationArtifactRecoveryActionPlan plan)
            {
                return plan != null && ReferenceEquals(_plan, plan) && plan.IsDecisionLeaseLive();
            }

            internal static ValidationToken Acquire(CaptureRunPublicationArtifactRecoveryActionPlan plan)
            {
                if (plan == null)
                {
                    throw new ArgumentNullException(nameof(plan));
                }

                if (!plan.IsValid)
                {
                    throw new InvalidOperationException("Action plan must be fully valid before issuing a validation token.");
                }

                return new ValidationToken(plan);
            }

            /// <summary>
            /// Non-validating mint reachable only from the single combined plan
            /// validation: the caller has already proven this action plan valid
            /// in the same pass, so this must never re-walk the plan. No
            /// caller-facing API bypasses <see cref="Acquire"/>.
            /// </summary>
            internal static ValidationToken AcquireFromValidatedPlan(CaptureRunPublicationArtifactRecoveryActionPlan plan)
            {
                return new ValidationToken(plan);
            }
        }

        internal ValidationToken AcquireValidationToken()
        {
            return ValidationToken.Acquire(this);
        }

        internal bool IsDecisionLeaseLive()
        {
            if (_decision == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = _decision.Snapshot;
            if (snapshot == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation operation = snapshot.Operation;
            if (operation == null)
            {
                return false;
            }

            CaptureRunLockLease lease = operation.LockLease;
            return lease != null && lease.IsCreated;
        }

        /// <summary>
        /// O(1), exception-safe check that the index-local core structure this
        /// plan exposes — the step array and the decision graph — is present,
        /// so a stale validation token cannot navigate a partially corrupted
        /// plan.
        /// </summary>
        internal bool IsIndexLocalStructureIntact()
        {
            return _steps != null
                && _decision != null
                && _decision.Snapshot != null
                && _decision.Snapshot.IsIndexLocalStructureIntact();
        }

        internal bool IsValid
        {
            get
            {
                if (_decision == null || !_decision.IsValid || _steps == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryDisposition disposition = _decision.Disposition;
                CaptureRunPublicationArtifactInspectionSnapshot snapshot = _decision.Snapshot;
                int entryCount = snapshot.Count;

                switch (disposition)
                {
                    case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                        return _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace, -1, CaptureRunPublicationArtifactKind.None);

                    case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                        return MatchesPublishSteps(snapshot, entryCount);

                    case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                        return _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex, -1, CaptureRunPublicationArtifactKind.None);

                    case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                        return _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup, -1, CaptureRunPublicationArtifactKind.None);

                    case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                        return _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing, -1, CaptureRunPublicationArtifactKind.None);

                    case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                        return _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing, -1, CaptureRunPublicationArtifactKind.None);

                    case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                        return _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision, -1, CaptureRunPublicationArtifactKind.None);

                    default:
                        return false;
                }
            }
        }

        private static CaptureRunPublicationArtifactRecoveryStep[] BuildPublishSteps(
            CaptureRunPublicationArtifactRecoveryDecision decision)
        {
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = decision.Snapshot;
            int entryCount = snapshot.Count;

            int publishableCount = 0;
            for (int i = 0; i < entryCount; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);
                if (IsPublishable(observation.FinalPngStatus, observation.StagingPngStatus))
                {
                    publishableCount++;
                }

                if (IsPublishable(observation.FinalSidecarStatus, observation.StagingSidecarStatus))
                {
                    publishableCount++;
                }
            }

            if (publishableCount == 0)
            {
                throw new ArgumentException("Decision must require at least one publishable artifact.", nameof(decision));
            }

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                new CaptureRunPublicationArtifactRecoveryStep[checked(publishableCount + 1)];

            int stepIndex = 0;
            for (int i = 0; i < entryCount; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

                if (IsPublishable(observation.FinalPngStatus, observation.StagingPngStatus))
                {
                    steps[stepIndex] = new CaptureRunPublicationArtifactRecoveryStep(
                        CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Png);
                    stepIndex++;
                }

                if (IsPublishable(observation.FinalSidecarStatus, observation.StagingSidecarStatus))
                {
                    steps[stepIndex] = new CaptureRunPublicationArtifactRecoveryStep(
                        CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Sidecar);
                    stepIndex++;
                }
            }

            steps[stepIndex] = new CaptureRunPublicationArtifactRecoveryStep(
                CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts, -1, CaptureRunPublicationArtifactKind.None);

            return steps;
        }

        private static bool IsPublishable(
            CaptureRunPublicationEvidenceStatus finalStatus,
            CaptureRunPublicationEvidenceStatus stagingStatus)
        {
            return finalStatus == CaptureRunPublicationEvidenceStatus.Absent
                && stagingStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected;
        }

        private bool MatchesPublishSteps(
            CaptureRunPublicationArtifactInspectionSnapshot snapshot,
            int entryCount)
        {
            int stepIndex = 0;

            for (int i = 0; i < entryCount; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

                if (IsPublishable(observation.FinalPngStatus, observation.StagingPngStatus))
                {
                    if (stepIndex >= _steps.Length
                        || !StepMatches(_steps[stepIndex], CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Png))
                    {
                        return false;
                    }

                    stepIndex++;
                }

                if (IsPublishable(observation.FinalSidecarStatus, observation.StagingSidecarStatus))
                {
                    if (stepIndex >= _steps.Length
                        || !StepMatches(_steps[stepIndex], CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Sidecar))
                    {
                        return false;
                    }

                    stepIndex++;
                }
            }

            if (stepIndex >= _steps.Length
                || !StepMatches(_steps[stepIndex], CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts, -1, CaptureRunPublicationArtifactKind.None))
            {
                return false;
            }

            stepIndex++;

            return stepIndex == _steps.Length;
        }

        private static bool StepMatches(
            CaptureRunPublicationArtifactRecoveryStep step,
            CaptureRunPublicationArtifactRecoveryAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind artifactKind)
        {
            return step != null && step.Matches(action, entryIndex, artifactKind);
        }
    }
}
