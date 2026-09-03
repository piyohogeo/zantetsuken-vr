using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free PngJson capture-complete cleanup operation
    /// that correlates one side-effecting cleanup step of a PngJson cleanup
    /// action plan to its exact target path under the held recovery lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation holds exactly four values: the action plan, the
    /// publication path set, the marker path set, and the step index. It never
    /// duplicates a path, ID, hash, byte count, step, or artifact path set
    /// into a field; those are re-derived from the held references on demand.
    /// The target path is derived only from the authoritative
    /// <see cref="CaptureRunPublicationPathSet"/>,
    /// <see cref="CaptureRunMarkerPathSet"/>, and
    /// <see cref="CaptureRunRootLayout"/>, never regenerated or normalized.
    /// <see cref="IsValid"/> recomputes every correlation from the held values
    /// without throwing, including after the lock lease has been released.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, holds no stream, byte array,
    /// raw lease, or ownership lease, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupOperation
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan _actionPlan;
        private readonly CaptureRunPublicationPathSet _publicationPaths;
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly int _stepIndex;

        private PngJsonCapturePublicationCaptureCompleteCleanupOperation(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            _actionPlan = actionPlan;
            _publicationPaths = publicationPaths;
            _markerPaths = markerPaths;
            _stepIndex = stepIndex;
        }

        /// <summary>
        /// Full-validation construction path: validates the action plan exactly
        /// once and issues its validation token, then delegates to the
        /// token-gated path with that same token.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupOperation Create(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (publicationPaths == null)
            {
                throw new ArgumentNullException(nameof(publicationPaths));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (!actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
            {
                throw new ArgumentException("Action plan must be a valid capture-complete cleanup plan.", nameof(actionPlan));
            }

            return CreateIndexLocal(token, actionPlan, publicationPaths, markerPaths, stepIndex);
        }

        /// <summary>
        /// Token-gated O(1) construction path used by a batch that has already
        /// acquired the plan's validation token once. It performs only
        /// index-local validation against the token, so materializing every
        /// cleanup step of a large plan stays linear in the total step count.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupOperation CreateIndexLocal(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (publicationPaths == null)
            {
                throw new ArgumentNullException(nameof(publicationPaths));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!TryCorrelateTrusted(token, actionPlan, publicationPaths, markerPaths, stepIndex, out CorrelationFailure failure))
            {
                ThrowFor(failure, stepIndex);
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupOperation(actionPlan, publicationPaths, markerPaths, stepIndex);
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupActionPlan ActionPlan => _actionPlan;

        internal CaptureRunPublicationPathSet PublicationPaths => _publicationPaths;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationCaptureCompleteCleanupStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationCaptureCompleteCleanupAction Action => Step.Action;

        internal int EntryIndex => Step.EntryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => Step.ArtifactKind;

        internal string TargetPath
        {
            get
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = Step;
                if (step == null)
                {
                    return null;
                }

                if (step.Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    PngJsonCapturePublicationArtifactInspectionPathSet paths = ArtifactPaths;
                    if (paths == null)
                    {
                        return null;
                    }

                    return step.ArtifactKind == CaptureRunPublicationArtifactKind.Png
                        ? paths.StagingPngPath
                        : paths.StagingSidecarPath;
                }

                return ComputeFixedTargetPath(_publicationPaths, _markerPaths, step);
            }
        }

        internal PngJsonCapturePublicationArtifactInspectionPathSet ArtifactPaths
        {
            get
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = Step;
                if (step == null || step.Action != CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    return null;
                }

                PngJsonCapturePublicationArtifactInspectionOperation inspection = InspectionOperation(_actionPlan);
                if (inspection == null
                    || !inspection.TryGetArtifactPaths(step.EntryIndex, out PngJsonCapturePublicationArtifactInspectionPathSet paths))
                {
                    return null;
                }

                return paths;
            }
        }

        internal long ExpectedByteCount
        {
            get
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = Step;
                if (step == null || step.Action != CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    return 0;
                }

                PngJsonCapturePublicationPlanEntry entry = ArtifactPaths?.Entry;
                if (entry == null)
                {
                    return 0;
                }

                return step.ArtifactKind == CaptureRunPublicationArtifactKind.Png
                    ? entry.PngByteLength
                    : entry.SidecarByteLength;
            }
        }

        internal string ExpectedContentSha256
        {
            get
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = Step;
                if (step == null || step.Action != CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    return null;
                }

                PngJsonCapturePublicationPlanEntry entry = ArtifactPaths?.Entry;
                if (entry == null)
                {
                    return null;
                }

                return step.ArtifactKind == CaptureRunPublicationArtifactKind.Png
                    ? entry.PngContentSha256
                    : entry.SidecarContentSha256;
            }
        }

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _actionPlan.AuthoritativePlan;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _actionPlan.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _actionPlan.AuthorityKind;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _actionPlan.LockIdentityEvidence;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        /// <summary>
        /// Recomputes the full operation correlation without throwing. Any
        /// forged nested value, released lease, swapped step, corrupted
        /// observation, or corrupted path set makes the operation invalid.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return TryCorrelate(_actionPlan, _publicationPaths, _markerPaths, _stepIndex, out _);
            }
        }

        /// <summary>
        /// O(1), exception-safe index-local correlation against a plan's
        /// validation token. It reuses the token-gated correlation path, so a
        /// prepared step can re-verify its operation after the token was
        /// issued without re-walking the whole plan, while still re-checking
        /// the exact publication path set instance and validity, both path
        /// sets' root layout correlation, lease liveness, inspection
        /// correlation, and, for artifact steps, the index-local observation
        /// and artifact path set predicates, evidence status, and plan entry
        /// correlation.
        /// </summary>
        internal bool IsValidIndexLocal(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            return TryCorrelateTrusted(token, _actionPlan, _publicationPaths, _markerPaths, _stepIndex, out _);
        }

        private static PngJsonCapturePublicationArtifactInspectionOperation InspectionOperation(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                return null;
            }

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = actionPlan.OrchestrationResult;
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = result == null ? null : result.InspectionSnapshot;
            return snapshot == null ? null : snapshot.Operation;
        }

        private enum CorrelationFailure
        {
            None = 0,
            InvalidActionPlan,
            PublicationPathsRootLayoutMismatch,
            MarkerPathsRootLayoutMismatch,
            StepIndexOutOfRange,
            StepInvalid,
            UnsupportedAction,
            InspectionInvalid,
            PublicationPathsInstanceMismatch,
            PublicationPathsInvalid,
            MarkerPathsInvalid,
            ArtifactInvalid,
            TargetPathMismatch
        }

        private static void ThrowFor(CorrelationFailure failure, int stepIndex)
        {
            switch (failure)
            {
                case CorrelationFailure.InvalidActionPlan:
                    throw new ArgumentException("Action plan must be a valid capture-complete cleanup plan.", "actionPlan");

                case CorrelationFailure.PublicationPathsRootLayoutMismatch:
                    throw new ArgumentException("Publication path set must share the action plan's root layout.", "publicationPaths");

                case CorrelationFailure.MarkerPathsRootLayoutMismatch:
                    throw new ArgumentException("Marker path set must share the action plan's root layout.", "markerPaths");

                case CorrelationFailure.StepIndexOutOfRange:
                    throw new ArgumentOutOfRangeException("stepIndex", stepIndex, "Step index must be within the action plan step count.");

                case CorrelationFailure.StepInvalid:
                    throw new ArgumentException("Step must be a valid cleanup step.", "stepIndex");

                case CorrelationFailure.UnsupportedAction:
                    throw new ArgumentException("Step action must be a side-effecting cleanup action.", "stepIndex");

                case CorrelationFailure.InspectionInvalid:
                    throw new ArgumentException("Orchestration result, inspection operation, and lease must be valid and correlated.", "actionPlan");

                case CorrelationFailure.PublicationPathsInstanceMismatch:
                    throw new ArgumentException("Publication path set must be the publication inspection operation's exact path set.", "publicationPaths");

                case CorrelationFailure.PublicationPathsInvalid:
                    throw new ArgumentException("Publication path set must be fully valid.", "publicationPaths");

                case CorrelationFailure.MarkerPathsInvalid:
                    throw new ArgumentException("Marker path set must be fully valid.", "markerPaths");

                case CorrelationFailure.ArtifactInvalid:
                    throw new ArgumentException("Artifact step must target a matching staging artifact observation.", "stepIndex");

                case CorrelationFailure.TargetPathMismatch:
                    throw new ArgumentException("Step action must map to a fixed target path.", "stepIndex");

                default:
                    throw new ArgumentException("Cleanup operation must be valid.", "actionPlan");
            }
        }

        /// <summary>
        /// Full correlation predicate used by <see cref="IsValid"/>. It performs
        /// one full action plan validation plus one token issuance, then
        /// delegates to the shared index-local predicate with the same token.
        /// The predicate never throws.
        /// </summary>
        private static bool TryCorrelate(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            out CorrelationFailure failure)
        {
            failure = CorrelationFailure.None;

            if (actionPlan == null || publicationPaths == null || markerPaths == null)
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            if (!actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            return TryCorrelateTrusted(token, actionPlan, publicationPaths, markerPaths, stepIndex, out failure);
        }

        /// <summary>
        /// Token-gated, index-local correlation predicate used by the trusted
        /// constructor. It performs no full plan walk; the caller proves the
        /// plan was validated by supplying the plan's validation token.
        /// </summary>
        private static bool TryCorrelateTrusted(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            out CorrelationFailure failure)
        {
            failure = CorrelationFailure.None;

            if (actionPlan == null || publicationPaths == null || markerPaths == null || token == null)
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            if (!actionPlan.IsTokenBound(token))
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                failure = CorrelationFailure.StepIndexOutOfRange;
                return false;
            }

            if (!actionPlan.IsStepIdentityAt(token, stepIndex))
            {
                failure = CorrelationFailure.StepInvalid;
                return false;
            }

            if (!actionPlan.IsIndexLocalStructureIntact())
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            if (!token.TryGetIssuedCleanupInputs(
                    actionPlan,
                    stepIndex,
                    out CaptureRunPublicationCaptureCompleteCleanupStep step,
                    out PngJsonCapturePublicationArtifactEntryObservation observation,
                    out PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths))
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            return TryCorrelateIndexLocal(actionPlan, publicationPaths, markerPaths, stepIndex, step, observation, artifactPaths, out failure);
        }

        /// <summary>
        /// Shared index-local correlation predicate: every check here is O(1)
        /// against the already-validated plan and inspection. It re-verifies
        /// root layout identity, the target step, the lease, the publication
        /// path set identity and validity, and, for artifact steps, the
        /// observation and artifact path set predicates.
        /// </summary>
        private static bool TryCorrelateIndexLocal(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunPublicationCaptureCompleteCleanupStep step,
            PngJsonCapturePublicationArtifactEntryObservation observation,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths,
            out CorrelationFailure failure)
        {
            failure = CorrelationFailure.None;

            if (!ReferenceEquals(publicationPaths.RootLayout, actionPlan.RootLayout))
            {
                failure = CorrelationFailure.PublicationPathsRootLayoutMismatch;
                return false;
            }

            if (!ReferenceEquals(markerPaths.RootLayout, actionPlan.RootLayout))
            {
                failure = CorrelationFailure.MarkerPathsRootLayoutMismatch;
                return false;
            }

            if (step == null || !step.IsValid)
            {
                failure = CorrelationFailure.StepInvalid;
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupAction action = step.Action;
            if (!IsSupportedAction(action))
            {
                failure = CorrelationFailure.UnsupportedAction;
                return false;
            }

            PngJsonCapturePublicationArtifactInspectionOperation inspection = InspectionOperation(actionPlan);
            if (inspection == null || !ReferenceEquals(publicationPaths, inspection.PublicationPaths))
            {
                failure = CorrelationFailure.PublicationPathsInstanceMismatch;
                return false;
            }

            if (!publicationPaths.IsValid)
            {
                failure = CorrelationFailure.PublicationPathsInvalid;
                return false;
            }

            if (!markerPaths.IsValid)
            {
                failure = CorrelationFailure.MarkerPathsInvalid;
                return false;
            }

            if (action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
            {
                int entryIndex = step.EntryIndex;
                if (entryIndex < 0 || entryIndex >= inspection.EntryCount)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                if (artifactPaths == null || artifactPaths.EntryIndex != entryIndex)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                if (observation == null)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                CaptureRunPublicationArtifactKind kind = step.ArtifactKind;
                if (kind == CaptureRunPublicationArtifactKind.Png)
                {
                    if (observation.StagingPngStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                    {
                        failure = CorrelationFailure.ArtifactInvalid;
                        return false;
                    }
                }
                else if (kind == CaptureRunPublicationArtifactKind.Sidecar)
                {
                    if (observation.StagingSidecarStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                    {
                        failure = CorrelationFailure.ArtifactInvalid;
                        return false;
                    }
                }
                else
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                if (observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                    || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                PngJsonCapturePublicationPlanEntry entry = artifactPaths.Entry;
                if (entry == null || !entry.IsValid)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                if (string.IsNullOrEmpty(kind == CaptureRunPublicationArtifactKind.Png
                    ? artifactPaths.StagingPngPath
                    : artifactPaths.StagingSidecarPath))
                {
                    failure = CorrelationFailure.TargetPathMismatch;
                    return false;
                }

                return true;
            }

            if (ComputeFixedTargetPath(publicationPaths, markerPaths, step) == null)
            {
                failure = CorrelationFailure.TargetPathMismatch;
                return false;
            }

            return true;
        }

        private static bool IsSupportedAction(CaptureRunPublicationCaptureCompleteCleanupAction action)
        {
            switch (action)
            {
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                    return true;

                default:
                    return false;
            }
        }

        private static string ComputeFixedTargetPath(
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunPublicationCaptureCompleteCleanupStep step)
        {
            switch (step.Action)
            {
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                    return publicationPaths.PublicationPlanTemporaryPath;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                    return publicationPaths.CaptureIndexTemporaryPath;

                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                    return publicationPaths.StagingFramesRoot;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                    return publicationPaths.PublicationPlanPath;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                    return markerPaths.StagingReadyPath;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                    return markerPaths.StagingInitializationPath;

                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                    return publicationPaths.RootLayout.StagingRunRoot;

                default:
                    return null;
            }
        }
    }
}
