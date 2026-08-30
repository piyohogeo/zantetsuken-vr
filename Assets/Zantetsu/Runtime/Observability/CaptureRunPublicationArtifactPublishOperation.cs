using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free publication operation for one publish step:
    /// the action plan, the step index, and the resolved artifact path set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation forwards its source, destination, expected byte count, and
    /// expected content hash from the authoritative plan entry and path set
    /// according to a fixed artifact-kind correspondence. It holds no bytes,
    /// stream, or hash result, and performs no filesystem work.
    /// </para>
    /// <para>
    /// The normal constructor fully re-validates the plan before issuing a
    /// validation token; the token-gated constructor re-verifies only the
    /// targeted step in constant time. <see cref="IsValid"/> recomputes every
    /// correlation from the held values without throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactPublishOperation
    {
        private readonly CaptureRunPublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly CaptureRunPublicationArtifactPathSet _artifactPaths;

        internal CaptureRunPublicationArtifactPublishOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            CaptureRunPublicationArtifactPathSet artifactPaths)
            : this(actionPlan, ValidateAndIssueToken(actionPlan), stepIndex, artifactPaths)
        {
        }

        internal CaptureRunPublicationArtifactPublishOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex,
            CaptureRunPublicationArtifactPathSet artifactPaths)
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

            if (artifactPaths == null)
            {
                throw new ArgumentNullException(nameof(artifactPaths));
            }

            CaptureRunPublicationArtifactRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid || step.Action != CaptureRunPublicationArtifactRecoveryAction.PublishArtifact)
            {
                throw new ArgumentException("Step must be a valid publish artifact step.", nameof(stepIndex));
            }

            int entryIndex = step.EntryIndex;
            CaptureRunPublicationArtifactKind kind = step.ArtifactKind;

            CapturePublicationPlan plan = actionPlan.AuthoritativePlan;
            if (entryIndex < 0 || entryIndex >= plan.EntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), entryIndex, "Publish entry index must be within the authoritative plan entry count.");
            }

            if (!ReferenceEquals(artifactPaths.Decision, actionPlan.Decision.PublicationDecision) || artifactPaths.EntryIndex != entryIndex)
            {
                throw new ArgumentException("Artifact path set must belong to the plan's decision and entry index.", nameof(artifactPaths));
            }

            if (!artifactPaths.IsValidIndexLocal())
            {
                throw new ArgumentException("Artifact path set must be valid.", nameof(artifactPaths));
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = actionPlan.Decision.Snapshot;
            CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(entryIndex);
            if (observation == null || !ReferenceEquals(observation.ArtifactPaths, artifactPaths))
            {
                throw new ArgumentException("Artifact path set must be the observation's path set for the target entry.", nameof(artifactPaths));
            }

            RequirePublishable(observation, kind, artifactPaths, nameof(artifactPaths));

            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _artifactPaths = artifactPaths;
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken ValidateAndIssueToken(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            try
            {
                return actionPlan.AcquireValidationToken();
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan), ex);
            }
        }

        private static void RequirePublishable(
            CaptureRunPublicationArtifactEntryObservation observation,
            CaptureRunPublicationArtifactKind kind,
            CaptureRunPublicationArtifactPathSet artifactPaths,
            string artifactPathsParamName)
        {
            CapturePublicationPlanEntry entry = artifactPaths.Entry;

            if (kind == CaptureRunPublicationArtifactKind.Png)
            {
                if (observation.StagingPngStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                    || observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.Absent)
                {
                    throw new ArgumentException("PNG must have a matching staging source and an absent final artifact.", artifactPathsParamName);
                }

                if (string.Equals(artifactPaths.StagingPngPath, artifactPaths.FinalPngPath, StringComparison.Ordinal))
                {
                    throw new ArgumentException("PNG source and destination paths must differ.", artifactPathsParamName);
                }

                if (entry.PngByteLength <= 0 || entry.PngContentSha256 == null)
                {
                    throw new ArgumentException("PNG expected byte count and hash must be present.", artifactPathsParamName);
                }

                return;
            }

            if (observation.StagingSidecarStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.Absent)
            {
                throw new ArgumentException("Sidecar must have a matching staging source and an absent final artifact.", artifactPathsParamName);
            }

            if (string.Equals(artifactPaths.StagingSidecarPath, artifactPaths.FinalSidecarPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Sidecar source and destination paths must differ.", artifactPathsParamName);
            }

            if (entry.SidecarByteLength <= 0 || entry.SidecarContentSha256 == null)
            {
                throw new ArgumentException("Sidecar expected byte count and hash must be present.", artifactPathsParamName);
            }
        }

        internal CaptureRunPublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal int EntryIndex => Step.EntryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => Step.ArtifactKind;

        internal CapturePublicationPlanEntry Entry => _artifactPaths.Entry;

        internal long CaptureFrameId => Entry.CaptureFrameId;

        internal string SourcePath
        {
            get
            {
                return ArtifactKind == CaptureRunPublicationArtifactKind.Png
                    ? _artifactPaths.StagingPngPath
                    : _artifactPaths.StagingSidecarPath;
            }
        }

        internal string DestinationPath
        {
            get
            {
                return ArtifactKind == CaptureRunPublicationArtifactKind.Png
                    ? _artifactPaths.FinalPngPath
                    : _artifactPaths.FinalSidecarPath;
            }
        }

        internal long ExpectedByteCount
        {
            get
            {
                return ArtifactKind == CaptureRunPublicationArtifactKind.Png
                    ? Entry.PngByteLength
                    : Entry.SidecarByteLength;
            }
        }

        internal string ExpectedContentSha256
        {
            get
            {
                return ArtifactKind == CaptureRunPublicationArtifactKind.Png
                    ? Entry.PngContentSha256
                    : Entry.SidecarContentSha256;
            }
        }

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || !_actionPlan.IsValid || _artifactPaths == null
                    || _stepIndex < 0 || _stepIndex >= _actionPlan.Count)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryStep step = _actionPlan.GetStep(_stepIndex);
                if (step == null || !step.IsValid || step.Action != CaptureRunPublicationArtifactRecoveryAction.PublishArtifact)
                {
                    return false;
                }

                int entryIndex = step.EntryIndex;
                CaptureRunPublicationArtifactKind kind = step.ArtifactKind;

                if (!ReferenceEquals(_artifactPaths.Decision, _actionPlan.Decision.PublicationDecision)
                    || _artifactPaths.EntryIndex != entryIndex
                    || !_artifactPaths.IsValidIndexLocal())
                {
                    return false;
                }

                CaptureRunPublicationArtifactInspectionSnapshot snapshot = _actionPlan.Decision.Snapshot;
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(entryIndex);
                if (observation == null || !ReferenceEquals(observation.ArtifactPaths, _artifactPaths))
                {
                    return false;
                }

                CapturePublicationPlanEntry entry = _artifactPaths.Entry;

                if (kind == CaptureRunPublicationArtifactKind.Png)
                {
                    return observation.StagingPngStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected
                        && observation.FinalPngStatus == CaptureRunPublicationEvidenceStatus.Absent
                        && !string.Equals(_artifactPaths.StagingPngPath, _artifactPaths.FinalPngPath, StringComparison.Ordinal)
                        && entry.PngByteLength > 0
                        && entry.PngContentSha256 != null;
                }

                return observation.StagingSidecarStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected
                    && observation.FinalSidecarStatus == CaptureRunPublicationEvidenceStatus.Absent
                    && !string.Equals(_artifactPaths.StagingSidecarPath, _artifactPaths.FinalSidecarPath, StringComparison.Ordinal)
                    && entry.SidecarByteLength > 0
                    && entry.SidecarContentSha256 != null;
            }
        }
    }
}
