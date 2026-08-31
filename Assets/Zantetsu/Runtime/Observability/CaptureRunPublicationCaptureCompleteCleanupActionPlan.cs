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
    /// The constructor validates the orchestration result, derives the expected
    /// step count, allocates the step array exactly once at its exact length,
    /// and fills it directly in fixed order; no external caller can hand in a
    /// contradicting step list, and the array is never exposed.
    /// <see cref="IsValid"/> re-derives the expected step count from the
    /// current result graph and compares each held step against its expected
    /// value as a virtual sequence in one linear pass, allocating no array and
    /// no step objects.
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

            ExpectedSequence? expected = ComputeExpected(orchestrationResult);
            if (expected == null)
            {
                throw new ArgumentException(
                    "Orchestration result must be a valid capture-complete cleanup result.",
                    nameof(orchestrationResult));
            }

            _orchestrationResult = orchestrationResult;
            _steps = BuildSteps(expected.Value);
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
        /// Exception-safe read of the step at the given index: returns false
        /// when the step array or the element is missing, so a forged null step
        /// array never escapes as a NullReferenceException.
        /// </summary>
        internal bool TryGetStep(int index, out CaptureRunPublicationCaptureCompleteCleanupStep step)
        {
            step = null;
            if (_steps == null || index < 0 || index >= _steps.Length)
            {
                return false;
            }

            step = _steps[index];
            return step != null;
        }

        /// <summary>
        /// Re-derives the expected step count from the current result graph and
        /// compares each held step against its expected value as a virtual
        /// sequence, without allocating any array or step objects and without
        /// throwing. Any forged nested value, corrupted step array, reordered
        /// step, corrupted observation, or released lease makes the plan
        /// invalid.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                ExpectedSequence? expected = ComputeExpected(_orchestrationResult);
                if (expected == null || _steps == null)
                {
                    return false;
                }

                ExpectedSequence exp = expected.Value;
                if (_steps.Length != exp.TotalStepCount)
                {
                    return false;
                }

                int position = 0;

                if (exp.DeletePublicationPlanTemporary
                    && !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary))
                {
                    return false;
                }

                if (exp.DeleteCaptureIndexTemporary
                    && !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary))
                {
                    return false;
                }

                CaptureRunPublicationArtifactInspectionSnapshot snapshot = exp.Snapshot;
                for (int i = 0; i < exp.EntryCount; i++)
                {
                    CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);
                    if (observation == null)
                    {
                        return false;
                    }

                    if (observation.StagingPngStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected
                        && !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, i, CaptureRunPublicationArtifactKind.Png))
                    {
                        return false;
                    }

                    if (observation.StagingSidecarStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected
                        && !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, i, CaptureRunPublicationArtifactKind.Sidecar))
                    {
                        return false;
                    }
                }

                if (exp.RemoveStagingFramesRoot
                    && !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot))
                {
                    return false;
                }

                if (exp.DeletePublicationPlan
                    && !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan))
                {
                    return false;
                }

                if (!MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker)
                    || !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker)
                    || !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot)
                    || !MatchAt(position++, CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady))
                {
                    return false;
                }

                return position == _steps.Length;
            }
        }

        /// <summary>
        /// Issues a validation token only after a full plan and artifact
        /// inspection validation pass. The token is bound to this exact plan
        /// instance, carries the artifact inspection token, and binds to the
        /// exact step array so trusted consumers can perform O(1) index-local
        /// validation without re-walking the whole plan.
        /// </summary>
        internal ValidationToken AcquireValidationToken()
        {
            if (!ValidationToken.TryAcquire(this, out ValidationToken token))
            {
                throw new InvalidOperationException("Action plan must be fully valid before issuing a validation token.");
            }

            return token;
        }

        /// <summary>
        /// Single combined validation path: performs the full plan validation
        /// once, then mints a token bound to this plan, to the artifact
        /// inspection token, and to a defensive snapshot of the issued step
        /// references, without re-walking the inspection graph.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        /// <summary>
        /// O(1), exception-safe index-local check that a validation token still
        /// matches this plan's current step array at the given index, that the
        /// step and nested arrays are present, and that the lease is still
        /// live. Rejects step substitution, reordering, null arrays, and stale
        /// tokens without walking the whole plan.
        /// </summary>
        internal bool IsValidIndexLocal(ValidationToken token, int stepIndex)
        {
            if (!IsTokenBound(token))
            {
                return false;
            }

            if (stepIndex < 0 || stepIndex >= _steps.Length)
            {
                return false;
            }

            if (!IsStepIdentityAt(token, stepIndex))
            {
                return false;
            }

            return IsIndexLocalStructureIntact();
        }

        /// <summary>
        /// O(1) check that a validation token still binds to this plan: the
        /// token must be issued for this plan and its defensive step snapshot
        /// must have the same length as the current step array. Rejects stale
        /// tokens, null arrays, and array-level substitution or reordering.
        /// </summary>
        internal bool IsTokenBound(ValidationToken token)
        {
            if (token == null || !token.IsIssuedFor(this))
            {
                return false;
            }

            if (_steps == null || !token.IsIssuedStepCount(_steps.Length))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// O(1) check that the step at the given index matches the proof
        /// snapshot: the same instance (rejecting in-place replacement) and
        /// the same action/entry-index/artifact-kind values (rejecting
        /// reflection-based mutation of the step's own fields).
        /// </summary>
        internal bool IsStepIdentityAt(ValidationToken token, int stepIndex)
        {
            return token.IsIssuedStepIdentityAt(stepIndex, _steps[stepIndex]);
        }

        /// <summary>
        /// O(1), exception-safe check that the index-local core structure this
        /// plan exposes — its orchestration result, execution result, execution
        /// batch, recovery action plan, inspection snapshot, inspection
        /// operation, and live lock lease — is present and correlated, so a
        /// stale validation token cannot navigate a partially corrupted plan.
        /// Each link of the execution result chain is guarded in order so a
        /// forged null never escapes as a NullReferenceException.
        /// </summary>
        internal bool IsIndexLocalStructureIntact()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = _orchestrationResult;
            if (result == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult = result.ExecutionResult;
            if (executionResult == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = executionResult.Batch;
            if (batch == null || !batch.IsIndexLocalStructureIntact())
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryActionPlan recoveryActionPlan = batch.ActionPlan;
            if (recoveryActionPlan == null || !recoveryActionPlan.IsIndexLocalStructureIntact())
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = recoveryActionPlan.Decision.Snapshot;
            if (snapshot == null || !snapshot.IsIndexLocalStructureIntact())
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation inspection = snapshot.Operation;
            if (inspection == null || !inspection.IsIndexLocalStructureIntact())
            {
                return false;
            }

            CaptureRunLockLease lease = result.LockLease;
            if (lease == null || !lease.IsCreated || !ReferenceEquals(lease, inspection.LockLease))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// O(1), exception-safe check that the execution result held by this
        /// plan is still proven valid by the exact execution-result token that
        /// was minted together with this plan's validation token. Re-verifies
        /// the completed-step and receipt correlation of the capture-index
        /// commit evidence, so cleanup cannot start after that evidence was
        /// destroyed while a token remained issued.
        /// </summary>
        internal bool IsExecutionResultIntact(ValidationToken token)
        {
            if (token == null || !token.IsIssuedFor(this))
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = _orchestrationResult;
            if (result == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult = result.ExecutionResult;
            if (executionResult == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken executionResultToken = token.ExecutionResultToken;
            return executionResultToken != null && executionResultToken.IsIssuedFor(executionResult);
        }

        /// <summary>
        /// Proof that this plan and its underlying artifact inspection graph
        /// were fully validated at a single point in time. The token is bound
        /// to the exact plan instance, carries the artifact inspection token,
        /// and binds to the exact step array for index-local step identity
        /// checks.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly CaptureRunPublicationCaptureCompleteCleanupActionPlan _plan;
            private readonly CaptureRunPublicationArtifactInspectionOperation.ValidationToken _inspectionToken;
            private readonly CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken _executionResultToken;
            private readonly IssuedStepProof[] _issuedSteps;

            private ValidationToken(
                CaptureRunPublicationCaptureCompleteCleanupActionPlan plan,
                CaptureRunPublicationArtifactInspectionOperation.ValidationToken inspectionToken,
                CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken executionResultToken,
                IssuedStepProof[] issuedSteps)
            {
                _plan = plan;
                _inspectionToken = inspectionToken;
                _executionResultToken = executionResultToken;
                _issuedSteps = issuedSteps;
            }

            internal CaptureRunPublicationArtifactInspectionOperation.ValidationToken InspectionToken => _inspectionToken;

            internal CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken ExecutionResultToken => _executionResultToken;

            /// <summary>
            /// O(1), exception-safe check that the snapshot array is present
            /// and has the given length. Never exposes the snapshot array or
            /// its length directly, so a forged (null or shorter) snapshot
            /// fails closed instead of throwing.
            /// </summary>
            internal bool IsIssuedStepCount(int count)
            {
                IssuedStepProof[] issued = _issuedSteps;
                return issued != null && issued.Length == count;
            }

            /// <summary>
            /// O(1) check that the current step at the given index is the same
            /// instance and carries the same action/entry-index/artifact-kind
            /// values as when the token was minted. Exposes no array and no
            /// step reference.
            /// </summary>
            internal bool IsIssuedStepIdentityAt(int index, CaptureRunPublicationCaptureCompleteCleanupStep step)
            {
                IssuedStepProof[] issued = _issuedSteps;
                if (issued == null || index < 0 || index >= issued.Length)
                {
                    return false;
                }

                IssuedStepProof proof = issued[index];
                if (step == null || proof.Step == null || !ReferenceEquals(step, proof.Step))
                {
                    return false;
                }

                return step.Matches(proof.Action, proof.EntryIndex, proof.ArtifactKind);
            }

            /// <summary>
            /// Reports whether this token was issued for the given plan. The
            /// binding is reference-identical and exposes no reference back to
            /// the plan.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunPublicationCaptureCompleteCleanupActionPlan plan)
            {
                return plan != null && ReferenceEquals(_plan, plan);
            }

            /// <summary>
            /// Performs the full plan validation exactly once (which validates
            /// the plan and its whole inspection graph) via the single atomic
            /// inspection-token mint, then captures a defensive proof snapshot
            /// of each current step's reference and value triple.
            /// </summary>
            internal static bool TryAcquire(
                CaptureRunPublicationCaptureCompleteCleanupActionPlan plan,
                out ValidationToken token)
            {
                token = null;
                if (plan == null)
                {
                    return false;
                }

                if (!CaptureRunPublicationArtifactInspectionOperation.ValidationToken.TryAcquireFromPlan(
                        plan,
                        out CaptureRunPublicationArtifactInspectionOperation.ValidationToken inspectionToken))
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryOrchestrationResult result = plan._orchestrationResult;
                if (result == null || result.ExecutionResult == null)
                {
                    return false;
                }

                if (!CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken.TryAcquireFromValidatedResult(
                        result.ExecutionResult,
                        inspectionToken,
                        out CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken executionResultToken))
                {
                    return false;
                }

                IssuedStepProof[] issuedSteps = new IssuedStepProof[plan._steps.Length];
                for (int i = 0; i < issuedSteps.Length; i++)
                {
                    issuedSteps[i] = new IssuedStepProof(plan._steps[i]);
                }

                token = new ValidationToken(plan, inspectionToken, executionResultToken, issuedSteps);
                return true;
            }

            /// <summary>
            /// Private proof of one issued step: the step instance plus the
            /// independent value snapshot of its action, entry index, and
            /// artifact kind. Never exposed outside the token.
            /// </summary>
            private readonly struct IssuedStepProof
            {
                internal readonly CaptureRunPublicationCaptureCompleteCleanupStep Step;
                internal readonly CaptureRunPublicationCaptureCompleteCleanupAction Action;
                internal readonly int EntryIndex;
                internal readonly CaptureRunPublicationArtifactKind ArtifactKind;

                internal IssuedStepProof(CaptureRunPublicationCaptureCompleteCleanupStep step)
                {
                    Step = step;
                    Action = step.Action;
                    EntryIndex = step.EntryIndex;
                    ArtifactKind = step.ArtifactKind;
                }
            }
        }

        private bool MatchAt(
            int position,
            CaptureRunPublicationCaptureCompleteCleanupAction action,
            int entryIndex = -1,
            CaptureRunPublicationArtifactKind artifactKind = CaptureRunPublicationArtifactKind.None)
        {
            if (position < 0 || position >= _steps.Length)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupStep held = _steps[position];
            return held != null && held.Matches(action, entryIndex, artifactKind);
        }

        /// <summary>
        /// Expected step sequence derived from a fully validated orchestration
        /// result: the conditional cleanup flags, the entry count, the total
        /// staging step count, and the inspection snapshot used to enumerate
        /// staging steps. It allocates nothing and is shared by the
        /// constructor (which allocates and fills the array exactly once) and
        /// <see cref="IsValid"/> (which compares the held steps against this
        /// virtual sequence).
        /// </summary>
        private struct ExpectedSequence
        {
            internal bool DeletePublicationPlanTemporary;
            internal bool DeleteCaptureIndexTemporary;
            internal bool RemoveStagingFramesRoot;
            internal bool DeletePublicationPlan;
            internal int EntryCount;
            internal int StagingStepCount;
            internal CaptureRunPublicationArtifactInspectionSnapshot Snapshot;

            internal int TotalStepCount
            {
                get
                {
                    int count = StagingStepCount + 4;
                    if (DeletePublicationPlanTemporary)
                    {
                        count++;
                    }

                    if (DeleteCaptureIndexTemporary)
                    {
                        count++;
                    }

                    if (RemoveStagingFramesRoot)
                    {
                        count++;
                    }

                    if (DeletePublicationPlan)
                    {
                        count++;
                    }

                    return count;
                }
            }
        }

        /// <summary>
        /// Fully validates the orchestration result and derives the expected
        /// step count and conditional cleanup flags, or returns null on any
        /// violation. This is the single shared validation used by both the
        /// constructor and <see cref="IsValid"/>; it allocates no array and no
        /// step objects.
        /// </summary>
        private static ExpectedSequence? ComputeExpected(
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

            ExpectedSequence sequence;
            sequence.DeletePublicationPlanTemporary = deletePublicationPlanTemporary;
            sequence.DeleteCaptureIndexTemporary = deleteCaptureIndexTemporary;
            sequence.RemoveStagingFramesRoot = removeStagingFramesRoot;
            sequence.DeletePublicationPlan = deletePublicationPlan;
            sequence.EntryCount = entryCount;
            sequence.StagingStepCount = stagingStepCount;
            sequence.Snapshot = snapshot;
            return sequence;
        }

        /// <summary>
        /// Allocates the step array exactly once at its exact length and fills
        /// it directly in fixed order from the derived sequence. This is the
        /// single step-array allocation site in the plan.
        /// </summary>
        private static CaptureRunPublicationCaptureCompleteCleanupStep[] BuildSteps(ExpectedSequence expected)
        {
            CaptureRunPublicationCaptureCompleteCleanupStep[] steps =
                new CaptureRunPublicationCaptureCompleteCleanupStep[expected.TotalStepCount];

            int position = 0;
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = expected.Snapshot;

            if (expected.DeletePublicationPlanTemporary)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary, -1, CaptureRunPublicationArtifactKind.None);
            }

            if (expected.DeleteCaptureIndexTemporary)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary, -1, CaptureRunPublicationArtifactKind.None);
            }

            for (int i = 0; i < expected.EntryCount; i++)
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

            if (expected.RemoveStagingFramesRoot)
            {
                steps[position++] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None);
            }

            if (expected.DeletePublicationPlan)
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
