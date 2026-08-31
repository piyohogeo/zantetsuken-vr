using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free cleanup operation that correlates one
    /// capture-complete cleanup step of a publication cleanup action plan to
    /// its exact target path under the held recovery lock.
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
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupOperation
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupActionPlan _actionPlan;
        private readonly CaptureRunPublicationPathSet _publicationPaths;
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly int _stepIndex;

        internal CaptureRunPublicationCaptureCompleteCleanupOperation(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
            : this(actionPlan, publicationPaths, markerPaths, stepIndex, AcquireToken(actionPlan, publicationPaths, markerPaths))
        {
        }

        /// <summary>
        /// Token-gated construction path used by a batch that has already
        /// acquired the plan's validation token once. It performs only
        /// index-local validation against the token, so materializing every
        /// cleanup step of a large plan stays linear in the total step count.
        /// </summary>
        internal CaptureRunPublicationCaptureCompleteCleanupOperation(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
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

            if (!TryCorrelateTrusted(actionPlan, publicationPaths, markerPaths, stepIndex, token, out CorrelationFailure failure))
            {
                ThrowFor(failure, stepIndex);
            }

            _actionPlan = actionPlan;
            _publicationPaths = publicationPaths;
            _markerPaths = markerPaths;
            _stepIndex = stepIndex;
        }

        private static CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken AcquireToken(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths)
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

            try
            {
                return actionPlan.AcquireValidationToken();
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException("Action plan must be a valid capture-complete cleanup plan.", nameof(actionPlan), ex);
            }
        }

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan ActionPlan => _actionPlan;

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
                    CaptureRunPublicationArtifactPathSet paths = ArtifactPaths;
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

        internal CaptureRunPublicationArtifactPathSet ArtifactPaths
        {
            get
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = Step;
                if (step == null || step.Action != CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    return null;
                }

                CaptureRunPublicationArtifactInspectionOperation inspection = InspectionOperation;
                if (inspection == null
                    || !inspection.TryGetArtifactPaths(step.EntryIndex, out CaptureRunPublicationArtifactPathSet paths))
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

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockLease LockLease => _actionPlan.LockLease;

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

        private CaptureRunPublicationArtifactInspectionOperation InspectionOperation =>
            _actionPlan.OrchestrationResult.InspectionSnapshot.Operation;

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
        /// one full action plan validation plus one artifact inspection token
        /// acquisition, then delegates to the shared index-local predicate.
        /// The predicate is written with explicit structural guards and never
        /// throws, so <see cref="IsValid"/> needs no catch.
        /// </summary>
        private static bool TryCorrelate(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
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

            if (!actionPlan.IsValid)
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = actionPlan.OrchestrationResult;
            if (result == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = result.InspectionSnapshot;
            if (snapshot == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation inspection = snapshot.Operation;
            if (inspection == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation.ValidationToken inspectionToken;
            try
            {
                inspectionToken = inspection.AcquireValidationToken();
            }
            catch (InvalidOperationException)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            return TryCorrelateIndexLocal(actionPlan, publicationPaths, markerPaths, stepIndex, inspection, inspectionToken, out failure);
        }

        /// <summary>
        /// Token-gated, index-local correlation predicate used by the trusted
        /// constructor. It performs no full plan walk; the caller proves the
        /// plan and inspection were validated by supplying the plan's
        /// validation token, which is checked for reference identity against
        /// both the plan and the inspection operation.
        /// </summary>
        private static bool TryCorrelateTrusted(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token,
            out CorrelationFailure failure)
        {
            failure = CorrelationFailure.None;

            if (actionPlan == null || publicationPaths == null || markerPaths == null || token == null)
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            if (!token.IsIssuedFor(actionPlan))
            {
                failure = CorrelationFailure.InvalidActionPlan;
                return false;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = actionPlan.OrchestrationResult;
            if (result == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = result.InspectionSnapshot;
            if (snapshot == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation inspection = snapshot.Operation;
            if (inspection == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation.ValidationToken inspectionToken = token.InspectionToken;
            if (inspectionToken == null || !inspectionToken.IsIssuedFor(inspection))
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            return TryCorrelateIndexLocal(actionPlan, publicationPaths, markerPaths, stepIndex, inspection, inspectionToken, out failure);
        }

        /// <summary>
        /// Shared index-local correlation predicate: every check here is O(1)
        /// against the already-validated plan and inspection. It re-verifies
        /// root layout identity, the target step, the lease, the publication
        /// path set identity and validity, and, for artifact steps, the
        /// index-local observation and artifact path set predicates.
        /// </summary>
        private static bool TryCorrelateIndexLocal(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunPublicationArtifactInspectionOperation inspection,
            CaptureRunPublicationArtifactInspectionOperation.ValidationToken inspectionToken,
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

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                failure = CorrelationFailure.StepIndexOutOfRange;
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupStep step = actionPlan.GetStep(stepIndex);
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

            if (!ReferenceEquals(inspection.RootLayout, actionPlan.RootLayout))
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunLockLease lease = actionPlan.LockLease;
            if (lease == null || !lease.IsCreated || !ReferenceEquals(lease, inspection.LockLease))
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = actionPlan.OrchestrationResult.InspectionSnapshot;
            CaptureRunPublicationRecoveryDecision publicationDecision = snapshot.Decision;
            if (publicationDecision == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = publicationDecision.Snapshot;
            if (publicationSnapshot == null)
            {
                failure = CorrelationFailure.InspectionInvalid;
                return false;
            }

            CaptureRunPublicationRecoveryInspectionOperation publicationInspection = publicationSnapshot.Operation;
            if (publicationInspection == null
                || !ReferenceEquals(publicationPaths, publicationInspection.PublicationPaths))
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
                if (entryIndex < 0 || entryIndex >= inspection.EntryCount
                    || !inspection.TryGetArtifactPaths(entryIndex, out CaptureRunPublicationArtifactPathSet artifactPaths)
                    || artifactPaths == null
                    || !artifactPaths.IsValidIndexLocal()
                    || artifactPaths.EntryIndex != entryIndex)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                if (snapshot == null || entryIndex >= snapshot.Count)
                {
                    failure = CorrelationFailure.ArtifactInvalid;
                    return false;
                }

                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(entryIndex);
                if (observation == null || !observation.IsValidIndexLocal(inspectionToken))
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
