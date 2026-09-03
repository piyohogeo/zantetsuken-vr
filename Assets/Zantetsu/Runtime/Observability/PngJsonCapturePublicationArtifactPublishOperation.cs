using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free publication operation for one
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.PublishArtifact"/>
    /// step of a PngJson artifact recovery action plan: the exact action plan,
    /// the step index, and the resolved artifact path set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation forwards its source, destination, expected byte count, and
    /// expected content hash from the authoritative plan entry and path set
    /// according to a fixed artifact-kind correspondence. It holds no bytes,
    /// stream, hash result, validation token, or lease, and performs no
    /// filesystem work.
    /// </para>
    /// <para>
    /// <see cref="Create"/> fully validates the plan once and delegates to the
    /// token-gated <see cref="CreateIndexLocal"/> path; <see cref="IsValid"/>
    /// validates the plan once and delegates to
    /// <see cref="IsValidIndexLocal"/>. The index-local path re-verifies only
    /// the targeted step in constant time without re-validating the plan or
    /// re-issuing a token.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactPublishOperation
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly PngJsonCapturePublicationArtifactInspectionPathSet _artifactPaths;

        private PngJsonCapturePublicationArtifactPublishOperation(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths)
        {
            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _artifactPaths = artifactPaths;
        }

        /// <summary>
        /// Validated factory: validates the action plan once through a
        /// non-throwing token issuance and delegates to the index-local path
        /// with the same token.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactPublishOperation Create(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
            {
                throw new ArgumentException("Action plan must be fully valid.", nameof(actionPlan));
            }

            return CreateIndexLocal(actionPlan, token, stepIndex);
        }

        /// <summary>
        /// O(1) token-gated factory: re-verifies only the targeted step through
        /// the token's index-local publish-input accessor without re-validating
        /// the plan or re-issuing a token.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactPublishOperation CreateIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index must be within the step count.");
            }

            if (!token.TryGetIssuedPublishInputs(actionPlan, stepIndex, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactEntryObservation observation))
            {
                throw new ArgumentException("Step must be a valid publish artifact step bound by the issued token.", nameof(stepIndex));
            }

            int entryIndex = step.EntryIndex;
            CaptureRunPublicationArtifactKind kind = step.ArtifactKind;
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths = observation.ArtifactPaths;

            if (!ReferenceEquals(artifactPaths.Authority, actionPlan.Authority)
                || artifactPaths.EntryIndex != entryIndex)
            {
                throw new ArgumentException("Artifact path set must belong to the plan's authority and entry index.", nameof(stepIndex));
            }

            RequirePublishable(observation, kind, artifactPaths, nameof(stepIndex));

            return new PngJsonCapturePublicationArtifactPublishOperation(actionPlan, stepIndex, artifactPaths);
        }

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _actionPlan.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _actionPlan.AuthorityKind;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal int EntryIndex => Step.EntryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => Step.ArtifactKind;

        internal PngJsonCapturePublicationArtifactInspectionPathSet ArtifactPaths => _artifactPaths;

        internal PngJsonCapturePublicationPlanEntry Entry => _artifactPaths.Entry;

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

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _actionPlan.LockIdentityEvidence;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal string RunManifestContentSha256 => _actionPlan.RunManifestContentSha256;

        /// <summary>
        /// Full validity: validates the plan once through a non-throwing token
        /// issuance and delegates to <see cref="IsValidIndexLocal"/> with the
        /// same token. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || _artifactPaths == null)
                {
                    return false;
                }

                if (!_actionPlan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
                {
                    return false;
                }

                return IsValidIndexLocal(token);
            }
        }

        /// <summary>
        /// O(1), exception-safe index-local validity: re-verifies only this
        /// step's artifact correlation through the token's index-local
        /// publish-input accessor without re-validating the plan or re-issuing
        /// a token.
        /// </summary>
        internal bool IsValidIndexLocal(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            try
            {
                if (token == null || _actionPlan == null || _artifactPaths == null)
                {
                    return false;
                }

                if (!token.TryGetIssuedPublishInputs(_actionPlan, _stepIndex, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactEntryObservation observation))
                {
                    return false;
                }

                if (!ReferenceEquals(observation.ArtifactPaths, _artifactPaths))
                {
                    return false;
                }

                if (!ReferenceEquals(_artifactPaths.Authority, _actionPlan.Authority)
                    || _artifactPaths.EntryIndex != step.EntryIndex)
                {
                    return false;
                }

                return IsPublishable(observation, step.ArtifactKind, _artifactPaths);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void RequirePublishable(
            PngJsonCapturePublicationArtifactEntryObservation observation,
            CaptureRunPublicationArtifactKind kind,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths,
            string stepIndexParamName)
        {
            PngJsonCapturePublicationPlanEntry entry = artifactPaths.Entry;

            if (kind == CaptureRunPublicationArtifactKind.Png)
            {
                if (observation.StagingPngStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                    || observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.Absent)
                {
                    throw new ArgumentException("PNG must have a matching staging source and an absent final artifact.", stepIndexParamName);
                }

                if (string.Equals(artifactPaths.StagingPngPath, artifactPaths.FinalPngPath, StringComparison.Ordinal))
                {
                    throw new ArgumentException("PNG source and destination paths must differ.", stepIndexParamName);
                }

                if (entry.PngByteLength <= 0 || !IsLowercaseHex64(entry.PngContentSha256))
                {
                    throw new ArgumentException("PNG expected byte count and hash must be present and valid.", stepIndexParamName);
                }

                return;
            }

            if (observation.StagingSidecarStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.Absent)
            {
                throw new ArgumentException("Sidecar must have a matching staging source and an absent final artifact.", stepIndexParamName);
            }

            if (string.Equals(artifactPaths.StagingSidecarPath, artifactPaths.FinalSidecarPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Sidecar source and destination paths must differ.", stepIndexParamName);
            }

            if (entry.SidecarByteLength <= 0 || !IsLowercaseHex64(entry.SidecarContentSha256))
            {
                throw new ArgumentException("Sidecar expected byte count and hash must be present and valid.", stepIndexParamName);
            }
        }

        private static bool IsPublishable(
            PngJsonCapturePublicationArtifactEntryObservation observation,
            CaptureRunPublicationArtifactKind kind,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths)
        {
            PngJsonCapturePublicationPlanEntry entry = artifactPaths.Entry;
            if (entry == null)
            {
                return false;
            }

            if (kind == CaptureRunPublicationArtifactKind.Png)
            {
                return observation.StagingPngStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected
                    && observation.FinalPngStatus == CaptureRunPublicationEvidenceStatus.Absent
                    && !string.Equals(artifactPaths.StagingPngPath, artifactPaths.FinalPngPath, StringComparison.Ordinal)
                    && entry.PngByteLength > 0
                    && IsLowercaseHex64(entry.PngContentSha256);
            }

            return observation.StagingSidecarStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected
                && observation.FinalSidecarStatus == CaptureRunPublicationEvidenceStatus.Absent
                && !string.Equals(artifactPaths.StagingSidecarPath, artifactPaths.FinalSidecarPath, StringComparison.Ordinal)
                && entry.SidecarByteLength > 0
                && IsLowercaseHex64(entry.SidecarContentSha256);
        }

        private static bool IsLowercaseHex64(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
