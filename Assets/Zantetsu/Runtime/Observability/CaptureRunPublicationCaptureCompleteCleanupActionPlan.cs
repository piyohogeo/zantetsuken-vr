using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free Capture Run publication capture-complete
    /// cleanup action plan: the fixed ordered sequence of filesystem cleanup
    /// steps that must run after a
    /// <see cref="CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired"/>
    /// orchestration result, derived from the result before any side effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor recomputes the single expected step sequence from the
    /// orchestration result, so no external caller can hand in a contradicting
    /// step list. It owns exactly two reference fields — the orchestration
    /// result and the exact-length step array — allocates the step array
    /// exactly once at its exact length, fills it directly in fixed order, and
    /// never exposes it. <see cref="IsValid"/> recomputes the same expected
    /// sequence from the current result graph and compares the held steps in
    /// one linear pass without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, no cleanup backend call, no
    /// draft registry release, no Capture-Complete notification, no OS lock
    /// acquisition, release, or disposal, no retry, rollback, or
    /// re-inspection, no canonical byte re-serialization, and no PNG/sidecar
    /// content or hash recomputation. It mutates, owns, and disposes nothing —
    /// neither the result, the plan, the snapshot, the receipts, nor the
    /// lease — and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupActionPlan
    {
        private readonly CaptureRunPublicationArtifactRecoveryOrchestrationResult _orchestrationResult;
        private readonly CaptureRunPublicationCaptureCompleteCleanupStep[] _steps;

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan(
            CaptureRunPublicationArtifactRecoveryOrchestrationResult orchestrationResult)
        {
            if (orchestrationResult == null)
            {
                throw new ArgumentNullException(nameof(orchestrationResult));
            }

            CaptureRunPublicationCaptureCompleteCleanupStep[] steps = ComputeSteps(orchestrationResult);
            if (steps == null)
            {
                throw new ArgumentException(
                    "Orchestration result must be a valid capture-complete cleanup result.",
                    nameof(orchestrationResult));
            }

            _orchestrationResult = orchestrationResult;
            _steps = steps;
        }

        internal CaptureRunPublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _orchestrationResult;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _orchestrationResult.Decision.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _orchestrationResult.RootLayout;

        internal CaptureRunLockLease LockLease => _orchestrationResult.LockLease;

        internal long TestRunId => _orchestrationResult.TestRunId;

        internal string RunInitializationId => _orchestrationResult.RunInitializationId;

        internal int Count => _steps.Length;

        internal CaptureRunPublicationCaptureCompleteCleanupStep GetStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the step count.");
            }

            return _steps[index];
        }

        /// <summary>
        /// Recomputes the expected step sequence from the current result graph
        /// and compares the held steps without throwing. Any forged nested
        /// value, corrupted step array, reordered step, corrupted observation,
        /// or released lease makes the plan invalid.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                CaptureRunPublicationCaptureCompleteCleanupStep[] expected = ComputeSteps(_orchestrationResult);
                if (expected == null || _steps == null || expected.Length != _steps.Length)
                {
                    return false;
                }

                for (int i = 0; i < _steps.Length; i++)
                {
                    CaptureRunPublicationCaptureCompleteCleanupStep held = _steps[i];
                    CaptureRunPublicationCaptureCompleteCleanupStep exp = expected[i];
                    if (held == null || !held.Matches(exp.Action, exp.EntryIndex, exp.ArtifactKind))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Fully validates the result and derives the exact expected cleanup
        /// step sequence, or returns null on any violation. This is the single
        /// shared computation used by both the constructor and
        /// <see cref="IsValid"/>.
        /// </summary>
        private static CaptureRunPublicationCaptureCompleteCleanupStep[] ComputeSteps(
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result)
        {
            if (result == null || !result.IsValid)
            {
                return null;
            }

            if (result.Status != CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired)
            {
                return null;
            }

            CaptureRunPublicationArtifactRecoveryDisposition disposition = result.Disposition;
            bool commitRoute;
            if (disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex)
            {
                commitRoute = true;
            }
            else if (disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete)
            {
                commitRoute = false;
            }
            else
            {
                return null;
            }

            PngJsonCapturePublicationPlan authoritativePlan = result.Decision.AuthoritativePlan;
            if (authoritativePlan == null || !authoritativePlan.IsValid)
            {
                return null;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = result.InspectionSnapshot;
            if (snapshot == null || !snapshot.IsValid)
            {
                return null;
            }

            CaptureRunPublicationArtifactInspectionOperation operation = snapshot.Operation;
            if (operation == null || !operation.IsValid)
            {
                return null;
            }

            CaptureRunRootLayout rootLayout = result.RootLayout;
            if (rootLayout == null || !ReferenceEquals(rootLayout, operation.RootLayout))
            {
                return null;
            }

            CaptureRunLockLease lockLease = result.LockLease;
            if (lockLease == null || !lockLease.IsCreated || !ReferenceEquals(lockLease, operation.LockLease))
            {
                return null;
            }

            if (result.TestRunId != operation.TestRunId
                || !string.Equals(result.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                return null;
            }

            CaptureRunPublicationRecoveryDecision publicationDecision = snapshot.Decision;
            if (publicationDecision == null)
            {
                return null;
            }

            if (commitRoute
                ? publicationDecision.Disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                : publicationDecision.Disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = publicationDecision.Snapshot;
            if (publicationSnapshot == null || !publicationSnapshot.IsValid)
            {
                return null;
            }

            CaptureRunPublicationDocumentObservation publicationPlanTemporary = publicationSnapshot.PublicationPlanTemporary;
            CaptureRunPublicationDocumentObservation publicationPlan = publicationSnapshot.PublicationPlan;
            CaptureRunPublicationDocumentObservation captureIndexTemporary = publicationSnapshot.CaptureIndexTemporary;
            CaptureRunPublicationDocumentObservation captureIndex = publicationSnapshot.CaptureIndex;

            if (publicationPlanTemporary == null || !publicationPlanTemporary.IsValid
                || publicationPlan == null || !publicationPlan.IsValid
                || captureIndexTemporary == null || !captureIndexTemporary.IsValid
                || captureIndex == null || !captureIndex.IsValid)
            {
                return null;
            }

            // Canonical Capture Index proof.
            if (commitRoute)
            {
                if (!HasValidCommitReceipt(result))
                {
                    return null;
                }
            }
            else
            {
                if (captureIndex.Status != CaptureRunPublicationDocumentObservationStatus.Canonical
                    || !CaptureRunPublicationRecoveryClassifier.PlansEqual(captureIndex.Plan, authoritativePlan))
                {
                    return null;
                }
            }

            int entryCount = authoritativePlan.EntryCount;
            if (snapshot.Count != entryCount)
            {
                return null;
            }

            // Temporary document steps (fail closed on any anomaly).
            bool deletePublicationPlanTemporary;
            switch (publicationPlanTemporary.Status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                    deletePublicationPlanTemporary = false;
                    break;

                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                    if (!CaptureRunPublicationRecoveryClassifier.PlansEqual(publicationPlanTemporary.Plan, authoritativePlan))
                    {
                        return null;
                    }

                    deletePublicationPlanTemporary = true;
                    break;

                default:
                    return null;
            }

            bool deleteCaptureIndexTemporary = false;
            if (commitRoute)
            {
                // The committer receipt guarantees the temporary index is
                // already absent on success; never derive a delete step from
                // the pre-commit temporary state.
                if (captureIndexTemporary.Status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded)
                {
                    return null;
                }
            }
            else
            {
                switch (captureIndexTemporary.Status)
                {
                    case CaptureRunPublicationDocumentObservationStatus.Absent:
                        break;

                    case CaptureRunPublicationDocumentObservationStatus.Canonical:
                        if (!CaptureRunPublicationRecoveryClassifier.PlansEqual(captureIndexTemporary.Plan, authoritativePlan))
                        {
                            return null;
                        }

                        deleteCaptureIndexTemporary = true;
                        break;

                    default:
                        return null;
                }
            }

            // Staging root cleanup document states.
            bool removeStagingFramesRoot;
            switch (publicationSnapshot.StagingFramesStatus)
            {
                case CaptureRunPublicationFramesObservationStatus.Absent:
                    removeStagingFramesRoot = false;
                    break;

                case CaptureRunPublicationFramesObservationStatus.Directory:
                    removeStagingFramesRoot = true;
                    break;

                default:
                    return null;
            }

            bool deletePublicationPlan;
            switch (publicationPlan.Status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                    deletePublicationPlan = false;
                    break;

                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                    if (!CaptureRunPublicationRecoveryClassifier.PlansEqual(publicationPlan.Plan, authoritativePlan))
                    {
                        return null;
                    }

                    deletePublicationPlan = true;
                    break;

                default:
                    return null;
            }

            // Per-entry validation and staging step count.
            int fixedStepCount = 0;
            if (deletePublicationPlanTemporary)
            {
                fixedStepCount++;
            }

            if (deleteCaptureIndexTemporary)
            {
                fixedStepCount++;
            }

            if (removeStagingFramesRoot)
            {
                fixedStepCount++;
            }

            if (deletePublicationPlan)
            {
                fixedStepCount++;
            }

            // Four unconditional tail steps.
            fixedStepCount += 4;

            int stagingStepCount = 0;
            for (int i = 0; i < entryCount; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);
                if (observation == null)
                {
                    return null;
                }

                if (observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                    || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    return null;
                }

                if (observation.StagingPngStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    stagingStepCount++;
                }
                else if (observation.StagingPngStatus != CaptureRunPublicationEvidenceStatus.Absent)
                {
                    return null;
                }

                if (observation.StagingSidecarStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    stagingStepCount++;
                }
                else if (observation.StagingSidecarStatus != CaptureRunPublicationEvidenceStatus.Absent)
                {
                    return null;
                }
            }

            CaptureRunPublicationCaptureCompleteCleanupStep[] steps =
                new CaptureRunPublicationCaptureCompleteCleanupStep[fixedStepCount + stagingStepCount];

            int position = 0;

            if (deletePublicationPlanTemporary)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary, -1, CaptureRunPublicationArtifactKind.None);
            }

            if (deleteCaptureIndexTemporary)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary, -1, CaptureRunPublicationArtifactKind.None);
            }

            for (int i = 0; i < entryCount; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

                if (observation.StagingPngStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                        CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, i, CaptureRunPublicationArtifactKind.Png);
                }

                if (observation.StagingSidecarStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                        CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, i, CaptureRunPublicationArtifactKind.Sidecar);
                }
            }

            if (removeStagingFramesRoot)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None);
            }

            if (deletePublicationPlan)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.None);
            }

            steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker, -1, CaptureRunPublicationArtifactKind.None);
            steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker, -1, CaptureRunPublicationArtifactKind.None);
            steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot, -1, CaptureRunPublicationArtifactKind.None);
            steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady, -1, CaptureRunPublicationArtifactKind.None);

            return steps;
        }

        private static bool HasValidCommitReceipt(
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result)
        {
            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult = result.ExecutionResult;
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = result.Batch;

            if (executionResult == null || batch == null || batch.Count != 1 || executionResult.Count != 1)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryPreparedStep preparedStep = batch.GetStep(0);
            if (preparedStep == null
                || preparedStep.Action != CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex
                || preparedStep.CaptureIndexCommitOperation == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryCompletedStep completedStep = executionResult.GetCompletedStep(0);
            if (completedStep == null || !ReferenceEquals(completedStep.PreparedStep, preparedStep))
            {
                return false;
            }

            CaptureRunCaptureIndexCommitReceipt commitReceipt = completedStep.CommitReceipt;
            if (commitReceipt == null || !commitReceipt.IsValid)
            {
                return false;
            }

            return ReferenceEquals(commitReceipt.IssuedBy, executionResult.IssuedBy.CaptureIndexCommitter)
                && ReferenceEquals(commitReceipt.Operation, preparedStep.CaptureIndexCommitOperation);
        }
    }
}
