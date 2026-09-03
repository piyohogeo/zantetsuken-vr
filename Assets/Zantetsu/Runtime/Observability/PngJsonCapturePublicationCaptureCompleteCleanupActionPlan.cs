using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free PngJson capture publication capture-complete
    /// cleanup action plan: the fixed ordered sequence of cleanup steps derived
    /// from an exact, fully valid
    /// <see cref="PngJsonCapturePublicationArtifactRecoveryOrchestrationResult"/>
    /// whose status is
    /// <see cref="CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> validates the orchestration result exactly once and
    /// issues an orchestration proof, derives the expected step count and
    /// conditional cleanup flags from that proof, allocates the step array
    /// exactly once at its exact length, and fills it directly in fixed order;
    /// no external caller can hand in a contradicting step list, and the array
    /// is never exposed. <see cref="IsValid"/> re-derives the expected step
    /// count from the current result graph and compares each held step against
    /// its expected value as a virtual sequence in one linear pass, allocating
    /// no array and no step objects.
    /// </para>
    /// <para>
    /// Recovery authority documents and root observations are read only from
    /// <see cref="PngJsonCapturePublicationArtifactInspectionAuthority.RecoveryDecision"/>;
    /// Fresh authority facts are read only from the Fresh seed and the
    /// commit receipt. The Fresh graph does not prove publication document or
    /// staging-frames-root existence, so those optional steps are never
    /// fabricated for a Fresh authority; only evidence-proven steps are
    /// emitted. This type performs no filesystem work, no cleanup backend call,
    /// no canonical byte re-serialization of plan or artifact content, no
    /// lock acquisition, release, or disposal, and no retry, rollback, or
    /// re-inspection.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupActionPlan
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryOrchestrationResult _orchestrationResult;
        private readonly PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken _orchestrationToken;
        private readonly CaptureRunPublicationCaptureCompleteCleanupStep[] _steps;

        private PngJsonCapturePublicationCaptureCompleteCleanupActionPlan(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult orchestrationResult,
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken orchestrationToken,
            CaptureRunPublicationCaptureCompleteCleanupStep[] steps)
        {
            _orchestrationResult = orchestrationResult;
            _orchestrationToken = orchestrationToken;
            _steps = steps;
        }

        /// <summary>
        /// Atomic validated factory: the single full-validation path. It
        /// validates the orchestration result exactly once and issues an
        /// orchestration proof, then derives the expected step sequence from
        /// that proof, allocates the step array exactly once, and fills it in
        /// fixed order.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupActionPlan Create(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult orchestrationResult)
        {
            if (orchestrationResult == null)
            {
                throw new ArgumentNullException(nameof(orchestrationResult));
            }

            if (!orchestrationResult.TryValidate(
                    out PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken token))
            {
                throw new ArgumentException(
                    "Orchestration result must be fully valid.",
                    nameof(orchestrationResult));
            }

            ExpectedSequence? expected = ComputeExpected(orchestrationResult);
            if (expected == null)
            {
                throw new ArgumentException(
                    "Orchestration result must be a valid capture-complete cleanup result.",
                    nameof(orchestrationResult));
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupActionPlan(
                orchestrationResult, token, BuildSteps(expected.Value));
        }

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _orchestrationResult;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _orchestrationResult.AuthoritativePlan;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _orchestrationResult.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _orchestrationResult.AuthorityKind;

        internal CaptureRunRootLayout RootLayout => _orchestrationResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _orchestrationResult.LockIdentityEvidence;

        internal long TestRunId => _orchestrationResult.TestRunId;

        internal string RunInitializationId => _orchestrationResult.RunInitializationId;

        internal string RunManifestContentSha256 => _orchestrationResult.RunManifestContentSha256;

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
        /// Re-validates the current orchestration result with the already held
        /// proof, then re-derives the expected step count and compares each held
        /// step against its expected value as a virtual sequence, without
        /// allocating any array or step objects and without throwing. Any
        /// forged nested value, corrupted step array, reordered step, corrupted
        /// observation, or released lease makes the plan invalid.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_orchestrationResult == null || _orchestrationToken == null || _steps == null)
                {
                    return false;
                }

                if (!_orchestrationResult.IsValidWithToken(_orchestrationToken))
                {
                    return false;
                }

                ExpectedSequence? expected = ComputeExpected(_orchestrationResult);
                if (expected == null)
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

                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = exp.Snapshot;
                for (int i = 0; i < exp.EntryCount; i++)
                {
                    PngJsonCapturePublicationArtifactEntryObservation observation = snapshot.GetEntry(i);
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
        /// Single combined validation path: performs the full plan validation
        /// once, then mints a token bound to this plan, to the held
        /// orchestration proof, and to a defensive snapshot of the issued step
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
        /// token must be issued for this plan, its orchestration proof must
        /// still bind to this plan's orchestration result, and its defensive
        /// step snapshot must have the same length as the current step array.
        /// Rejects stale tokens, null arrays, and array-level substitution or
        /// reordering.
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
        /// O(1), exception-safe check that the held orchestration proof still
        /// binds to this plan's orchestration result and that the lock identity
        /// evidence is still live. It never walks an entry, re-serializes
        /// canonical bytes, or touches a filesystem.
        /// </summary>
        internal bool IsIndexLocalStructureIntact()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = _orchestrationResult;
            if (result == null || _orchestrationToken == null || !_orchestrationToken.IsIssuedFor(result))
            {
                return false;
            }

            CaptureRunLockIdentityEvidence evidence = result.LockIdentityEvidence;
            return evidence != null && evidence.IsValid;
        }

        /// <summary>
        /// Opaque proof that this plan and its underlying orchestration result
        /// were fully validated at a single point in time. The token is bound
        /// to the exact plan instance, carries the orchestration proof, and
        /// binds to the exact step array for index-local step identity checks.
        /// It exposes no proof array and no internal token.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan _plan;
            private readonly PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken _orchestrationToken;
            private readonly IssuedStepProof[] _issuedSteps;

            private ValidationToken(
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan,
                PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken orchestrationToken,
                IssuedStepProof[] issuedSteps)
            {
                _plan = plan;
                _orchestrationToken = orchestrationToken;
                _issuedSteps = issuedSteps;
            }

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
            /// binding is reference-identical to the plan and to the exact
            /// orchestration result through the carried orchestration proof,
            /// and exposes no reference back to either.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan)
            {
                if (plan == null || !ReferenceEquals(_plan, plan))
                {
                    return false;
                }

                return _orchestrationToken != null
                    && ReferenceEquals(_orchestrationToken, plan._orchestrationToken)
                    && _orchestrationToken.IsIssuedFor(plan._orchestrationResult);
            }

            /// <summary>
            /// Single atomic validated mint: performs the full plan validation
            /// exactly once, then captures a defensive proof snapshot of each
            /// current step's reference and value triple and mints the token
            /// bound to the plan and its already-issued orchestration proof.
            /// No new orchestration token is issued.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan,
                out ValidationToken token)
            {
                token = null;

                if (plan == null || !plan.IsValid || plan._orchestrationToken == null)
                {
                    return false;
                }

                IssuedStepProof[] issuedSteps = new IssuedStepProof[plan._steps.Length];
                for (int i = 0; i < issuedSteps.Length; i++)
                {
                    issuedSteps[i] = new IssuedStepProof(plan._steps[i]);
                }

                token = new ValidationToken(plan, plan._orchestrationToken, issuedSteps);
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
        /// staging steps. It allocates nothing and is shared by the factory
        /// (which allocates and fills the array exactly once) and
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
            internal PngJsonCapturePublicationArtifactInspectionSnapshot Snapshot;

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
        /// Derives the expected step count and conditional cleanup flags from
        /// an already-validated orchestration result, or returns null on any
        /// violation. It allocates no array and no step objects. Recovery
        /// document and root states are read only from the Recovery decision;
        /// Fresh authorities emit no unproven document or root steps.
        /// </summary>
        private static ExpectedSequence? ComputeExpected(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result)
        {
            try
            {
                if (result == null
                    || result.Status != CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired)
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

                PngJsonCapturePublicationArtifactRecoveryDecision decision = result.Decision;
                if (decision == null)
                {
                    return null;
                }

                PngJsonCapturePublicationPlan authoritativePlan = decision.AuthoritativePlan;
                if (authoritativePlan == null)
                {
                    return null;
                }

                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = result.InspectionSnapshot;
                if (snapshot == null)
                {
                    return null;
                }

                if (snapshot.TraceManifestStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    return null;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = result.Authority;
                if (authority == null)
                {
                    return null;
                }

                PngJsonCapturePublicationArtifactInspectionAuthorityKind authorityKind = result.AuthorityKind;

                // Recovery-only document and root observations.
                CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = null;
                CaptureRunPublicationDocumentObservation publicationPlanTemporary = null;
                CaptureRunPublicationDocumentObservation publicationPlan = null;
                CaptureRunPublicationDocumentObservation captureIndexTemporary = null;
                CaptureRunPublicationDocumentObservation captureIndex = null;
                CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Absent;
                bool hasPublicationSnapshot = false;
                bool freshRoute = false;

                if (authorityKind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision)
                {
                    CaptureRunPublicationRecoveryDecision recoveryDecision = authority.RecoveryDecision;
                    if (recoveryDecision == null)
                    {
                        return null;
                    }

                    publicationSnapshot = recoveryDecision.Snapshot;
                    if (publicationSnapshot == null || !publicationSnapshot.IsValid)
                    {
                        return null;
                    }

                    publicationPlanTemporary = publicationSnapshot.PublicationPlanTemporary;
                    publicationPlan = publicationSnapshot.PublicationPlan;
                    captureIndexTemporary = publicationSnapshot.CaptureIndexTemporary;
                    captureIndex = publicationSnapshot.CaptureIndex;
                    stagingFramesStatus = publicationSnapshot.StagingFramesStatus;
                    hasPublicationSnapshot = true;

                    if (publicationPlanTemporary == null || !publicationPlanTemporary.IsValid
                        || publicationPlan == null || !publicationPlan.IsValid
                        || captureIndexTemporary == null || !captureIndexTemporary.IsValid
                        || captureIndex == null || !captureIndex.IsValid)
                    {
                        return null;
                    }
                }
                else if (authorityKind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun)
                {
                    // Fresh: the frozen publication result proves the
                    // publication plan was written; staged artifacts prove the
                    // frames directory exists.
                    PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed = authority.FreshSeed;
                    if (freshSeed == null)
                    {
                        return null;
                    }

                    CaptureEvidenceFrozenRunPublicationResult frozen = freshSeed.FrozenPublicationResult;
                    if (frozen == null || frozen.PlanWriteReceipt == null)
                    {
                        return null;
                    }

                    freshRoute = true;
                }
                else
                {
                    return null;
                }

                // Canonical capture-index proof.
                if (commitRoute)
                {
                    if (!HasValidCommitReceipt(result))
                    {
                        return null;
                    }
                }
                else
                {
                    // CaptureComplete is Recovery-only and requires a canonical
                    // final capture index matching the authoritative plan.
                    if (!hasPublicationSnapshot
                        || captureIndex.Status != CaptureRunPublicationDocumentObservationStatus.Canonical
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

                bool deletePublicationPlanTemporary = false;
                bool deleteCaptureIndexTemporary = false;
                bool removeStagingFramesRoot = false;
                bool deletePublicationPlan = false;

                if (hasPublicationSnapshot)
                {
                    switch (publicationPlanTemporary.Status)
                    {
                        case CaptureRunPublicationDocumentObservationStatus.Absent:
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

                    if (commitRoute)
                    {
                        // The commit receipt guarantees the temporary index is
                        // already absent on success; never derive a delete step
                        // from the pre-commit temporary state.
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

                    switch (stagingFramesStatus)
                    {
                        case CaptureRunPublicationFramesObservationStatus.Absent:
                            break;

                        case CaptureRunPublicationFramesObservationStatus.Directory:
                            removeStagingFramesRoot = true;
                            break;

                        default:
                            return null;
                    }

                    switch (publicationPlan.Status)
                    {
                        case CaptureRunPublicationDocumentObservationStatus.Absent:
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
                }
                else if (freshRoute)
                {
                    // Fresh: the frozen publication result proves the
                    // publication plan exists and must be deleted before the
                    // staging run root is removed.
                    deletePublicationPlan = true;
                }

                // Per-entry validation and staging step count.
                int stagingStepCount = 0;
                for (int i = 0; i < entryCount; i++)
                {
                    PngJsonCapturePublicationArtifactEntryObservation observation = snapshot.GetEntry(i);
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

                if (freshRoute)
                {
                    // Fresh: any plan entry proves artifacts were staged into
                    // the parent frames directory; File.Move leaves the empty
                    // directory behind, so remove it whenever the plan has
                    // entries, independent of how many staging files remain.
                    removeStagingFramesRoot = entryCount > 0;
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
            catch (Exception)
            {
                return null;
            }
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
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = expected.Snapshot;

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
                PngJsonCapturePublicationArtifactEntryObservation observation = snapshot.GetEntry(i);

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

        /// <summary>
        /// Confirms the exact commit route evidence inside the execution
        /// result: a single commit step with the exact prepared step, the
        /// exact commit operation, and an exact committer-issued commit
        /// receipt that fully validates through its held action plan token.
        /// </summary>
        private static bool HasValidCommitReceipt(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result)
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult = result.ExecutionResult;
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = result.Batch;

            if (executionResult == null || batch == null || batch.Count != 1 || executionResult.Count != 1)
            {
                return false;
            }

            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep = batch.GetStep(0);
            if (preparedStep == null
                || preparedStep.Action != CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex
                || preparedStep.CaptureIndexCommitOperation == null)
            {
                return false;
            }

            PngJsonCapturePublicationArtifactRecoveryCompletedStep completedStep = executionResult.GetCompletedStep(0);
            if (completedStep == null || !ReferenceEquals(completedStep.PreparedStep, preparedStep))
            {
                return false;
            }

            PngJsonCaptureRunCaptureIndexCommitReceipt commitReceipt = completedStep.CommitReceipt;
            if (commitReceipt == null || !commitReceipt.IsValid)
            {
                return false;
            }

            return ReferenceEquals(commitReceipt.IssuedBy, executionResult.IssuedBy.Committer)
                && ReferenceEquals(commitReceipt.Operation, preparedStep.CaptureIndexCommitOperation);
        }
    }
}
