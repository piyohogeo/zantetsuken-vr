using System;
using System.Globalization;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run publication artifact path
    /// contract: resolves the four relative paths of one authoritative plan
    /// entry into fixed absolute paths under the staging and final frames
    /// roots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four absolute paths are derived with a single
    /// <see cref="Path.Combine"/> per path from the run root and the entry's
    /// relative path, confirmed with <see cref="Path.GetFullPath"/>, and
    /// stored once. Their parent directories are the staging and final frames
    /// roots and their basenames are the fixed
    /// <c>{id}.png.stage</c>, <c>{id}.json.stage</c>, <c>{id}.png</c>, and
    /// <c>{id}.json</c> forms of the invariant shortest decimal
    /// <see cref="CaptureFrameId"/>.
    /// </para>
    /// <para>
    /// This type performs no file, directory, or stream operation, no
    /// existence check or creation, no reparse point or identity check, no
    /// hash computation, and no locking. It holds the decision, entry, plan,
    /// outcome, and lease as non-owning references and mutates or disposes
    /// none of them.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactPathSet
    {
        private readonly CaptureRunPublicationRecoveryDecision _decision;
        private readonly int _entryIndex;
        private readonly string _stagingPngPath;
        private readonly string _stagingSidecarPath;
        private readonly string _finalPngPath;
        private readonly string _finalSidecarPath;

        internal CaptureRunPublicationArtifactPathSet(
            CaptureRunPublicationRecoveryDecision decision,
            int entryIndex)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException("Decision must be valid.", nameof(decision));
            }

            CaptureRunPublicationRecoveryDisposition disposition = decision.Disposition;
            if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                throw new ArgumentException("Decision must carry an authoritative plan.", nameof(decision));
            }

            PngJsonCapturePublicationPlan plan = decision.AuthoritativePlan;
            if (plan == null || !plan.IsValid)
            {
                throw new ArgumentException("Decision must hold a valid authoritative plan.", nameof(decision));
            }

            if (entryIndex < 0 || entryIndex >= plan.EntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Entry index must be within the authoritative plan entry count.");
            }

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null || !snapshot.IsValid)
            {
                throw new ArgumentException("Decision snapshot must be valid.", nameof(decision));
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;
            if (operation == null || !operation.IsValid)
            {
                throw new ArgumentException("Decision operation must be valid.", nameof(decision));
            }

            CaptureRunPublicationPathSet publicationPaths = operation.PublicationPaths;
            if (publicationPaths == null || !publicationPaths.IsValid)
            {
                throw new ArgumentException("Operation publication path set must be valid.", nameof(decision));
            }

            CaptureRunRootLayout rootLayout = decision.RootLayout;
            if (rootLayout == null || !rootLayout.IsValid
                || !ReferenceEquals(operation.RootLayout, rootLayout)
                || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
            {
                throw new ArgumentException("Decision, operation, and publication path set must share the same root layout.", nameof(decision));
            }

            PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(entryIndex);
            if (entry == null || !entry.IsValid)
            {
                throw new ArgumentException("Target plan entry must be valid.", nameof(decision));
            }

            string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);

            string stagingPngPath;
            string stagingSidecarPath;
            string finalPngPath;
            string finalSidecarPath;

            try
            {
                stagingPngPath = DeriveArtifactPath(rootLayout.StagingRunRoot, entry.PngStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".png.stage");
                stagingSidecarPath = DeriveArtifactPath(rootLayout.StagingRunRoot, entry.SidecarStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".json.stage");
                finalPngPath = DeriveArtifactPath(rootLayout.FinalRunRoot, entry.PngFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".png");
                finalSidecarPath = DeriveArtifactPath(rootLayout.FinalRunRoot, entry.SidecarFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".json");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                throw new ArgumentException("Plan entry paths must resolve to fixed absolute artifact paths.", nameof(decision), ex);
            }

            if (string.Equals(stagingPngPath, stagingSidecarPath, StringComparison.Ordinal)
                || string.Equals(stagingPngPath, finalPngPath, StringComparison.Ordinal)
                || string.Equals(stagingPngPath, finalSidecarPath, StringComparison.Ordinal)
                || string.Equals(stagingSidecarPath, finalPngPath, StringComparison.Ordinal)
                || string.Equals(stagingSidecarPath, finalSidecarPath, StringComparison.Ordinal)
                || string.Equals(finalPngPath, finalSidecarPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Artifact paths must be mutually distinct.", nameof(decision));
            }

            _decision = decision;
            _entryIndex = entryIndex;
            _stagingPngPath = stagingPngPath;
            _stagingSidecarPath = stagingSidecarPath;
            _finalPngPath = finalPngPath;
            _finalSidecarPath = finalSidecarPath;
        }

        internal CaptureRunPublicationRecoveryDecision Decision => _decision;

        internal int EntryIndex => _entryIndex;

        internal PngJsonCapturePublicationPlan Plan => _decision.AuthoritativePlan;

        internal PngJsonCapturePublicationPlanEntry Entry => Plan.GetEntry(_entryIndex);

        internal long CaptureFrameId => Entry.CaptureFrameId;

        internal string StagingPngPath => _stagingPngPath;

        internal string StagingSidecarPath => _stagingSidecarPath;

        internal string FinalPngPath => _finalPngPath;

        internal string FinalSidecarPath => _finalSidecarPath;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                if (_decision == null || !_decision.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationRecoveryDisposition disposition = _decision.Disposition;
                if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                    && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
                {
                    return false;
                }

                PngJsonCapturePublicationPlan plan = _decision.AuthoritativePlan;
                if (plan == null || !plan.IsValid)
                {
                    return false;
                }

                if (_entryIndex < 0 || _entryIndex >= plan.EntryCount)
                {
                    return false;
                }

                CaptureRunPublicationRecoveryInspectionSnapshot snapshot = _decision.Snapshot;
                if (snapshot == null || !snapshot.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;
                if (operation == null || !operation.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationPathSet publicationPaths = operation.PublicationPaths;
                if (publicationPaths == null || !publicationPaths.IsValid)
                {
                    return false;
                }

                CaptureRunRootLayout rootLayout = _decision.RootLayout;
                if (rootLayout == null || !rootLayout.IsValid
                    || !ReferenceEquals(operation.RootLayout, rootLayout)
                    || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
                {
                    return false;
                }

                PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(_entryIndex);
                if (entry == null || !entry.IsValid)
                {
                    return false;
                }

                string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);

                return MatchesFixed(rootLayout.StagingRunRoot, entry.PngStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".png.stage", _stagingPngPath)
                    && MatchesFixed(rootLayout.StagingRunRoot, entry.SidecarStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".json.stage", _stagingSidecarPath)
                    && MatchesFixed(rootLayout.FinalRunRoot, entry.PngFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".png", _finalPngPath)
                    && MatchesFixed(rootLayout.FinalRunRoot, entry.SidecarFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".json", _finalSidecarPath)
                    && AreDistinct(_stagingPngPath, _stagingSidecarPath, _finalPngPath, _finalSidecarPath);
            }
        }

        private static string DeriveArtifactPath(string runRoot, string relativePath, string framesRoot, string expectedBasename)
        {
            if (relativePath == null || Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
            {
                throw new ArgumentException("Relative path must not be rooted or fully qualified.");
            }

            string absolutePath = Path.GetFullPath(Path.Combine(runRoot, relativePath));

            if (!string.Equals(Path.GetDirectoryName(absolutePath), framesRoot, StringComparison.Ordinal))
            {
                throw new ArgumentException("Artifact path must be a direct child of the frames root.");
            }

            if (!string.Equals(Path.GetFileName(absolutePath), expectedBasename, StringComparison.Ordinal))
            {
                throw new ArgumentException("Artifact path basename must match the fixed name.");
            }

            return absolutePath;
        }

        private static bool MatchesFixed(string runRoot, string relativePath, string framesRoot, string expectedBasename, string storedPath)
        {
            if (storedPath == null || relativePath == null)
            {
                return false;
            }

            try
            {
                if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
                {
                    return false;
                }

                string derived = Path.GetFullPath(Path.Combine(runRoot, relativePath));

                return string.Equals(storedPath, derived, StringComparison.Ordinal)
                    && string.Equals(Path.GetDirectoryName(derived), framesRoot, StringComparison.Ordinal)
                    && string.Equals(Path.GetFileName(derived), expectedBasename, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                return false;
            }
        }

        private static bool AreDistinct(string first, string second, string third, string fourth)
        {
            return !string.Equals(first, second, StringComparison.Ordinal)
                && !string.Equals(first, third, StringComparison.Ordinal)
                && !string.Equals(first, fourth, StringComparison.Ordinal)
                && !string.Equals(second, third, StringComparison.Ordinal)
                && !string.Equals(second, fourth, StringComparison.Ordinal)
                && !string.Equals(third, fourth, StringComparison.Ordinal);
        }

        private CaptureRunPublicationArtifactPathSet(
            CaptureRunPublicationRecoveryDecision decision,
            int entryIndex,
            string stagingPngPath,
            string stagingSidecarPath,
            string finalPngPath,
            string finalSidecarPath)
        {
            _decision = decision;
            _entryIndex = entryIndex;
            _stagingPngPath = stagingPngPath;
            _stagingSidecarPath = stagingSidecarPath;
            _finalPngPath = finalPngPath;
            _finalSidecarPath = finalSidecarPath;
        }

        /// <summary>
        /// Index-local construction for an already-validated decision: assumes
        /// the decision graph and authoritative plan were fully validated by
        /// the caller, and validates only this entry index, its entry, and its
        /// four resolved paths.
        /// </summary>
        internal static CaptureRunPublicationArtifactPathSet CreateIndexLocal(
            CaptureRunPublicationArtifactInspectionOperation.ConstructionToken token,
            CaptureRunPublicationRecoveryDecision decision,
            int entryIndex)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!token.IsIssuedFor(decision))
            {
                throw new ArgumentException("Token must be issued for the same decision.", nameof(token));
            }

            CaptureRunPublicationRecoveryDisposition disposition = decision.Disposition;
            if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                throw new ArgumentException("Decision must carry an authoritative plan.", nameof(decision));
            }

            PngJsonCapturePublicationPlan plan = decision.AuthoritativePlan;
            if (plan == null)
            {
                throw new ArgumentException("Decision must hold an authoritative plan.", nameof(decision));
            }

            if (entryIndex < 0 || entryIndex >= plan.EntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Entry index must be within the authoritative plan entry count.");
            }

            if (!CheapCorrelated(decision))
            {
                throw new ArgumentException("Decision graph must remain correlated.", nameof(decision));
            }

            CaptureRunRootLayout rootLayout = decision.RootLayout;
            CaptureRunPublicationPathSet publicationPaths = decision.Snapshot.Operation.PublicationPaths;

            PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(entryIndex);
            if (entry == null || !entry.IsValid)
            {
                throw new ArgumentException("Target plan entry must be valid.", nameof(decision));
            }

            string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);

            string stagingPngPath;
            string stagingSidecarPath;
            string finalPngPath;
            string finalSidecarPath;

            try
            {
                stagingPngPath = DeriveArtifactPath(rootLayout.StagingRunRoot, entry.PngStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".png.stage");
                stagingSidecarPath = DeriveArtifactPath(rootLayout.StagingRunRoot, entry.SidecarStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".json.stage");
                finalPngPath = DeriveArtifactPath(rootLayout.FinalRunRoot, entry.PngFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".png");
                finalSidecarPath = DeriveArtifactPath(rootLayout.FinalRunRoot, entry.SidecarFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".json");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                throw new ArgumentException("Plan entry paths must resolve to fixed absolute artifact paths.", nameof(decision), ex);
            }

            if (!AreDistinct(stagingPngPath, stagingSidecarPath, finalPngPath, finalSidecarPath))
            {
                throw new ArgumentException("Artifact paths must be mutually distinct.", nameof(decision));
            }

            return new CaptureRunPublicationArtifactPathSet(decision, entryIndex, stagingPngPath, stagingSidecarPath, finalPngPath, finalSidecarPath);
        }

        /// <summary>
        /// Index-local validity for an already-validated decision: re-verifies
        /// only this entry index, entry, and four resolved paths without
        /// re-validating the whole decision graph or plan.
        /// </summary>
        internal bool IsValidIndexLocal()
        {
            if (_decision == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryDisposition disposition = _decision.Disposition;
            if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                return false;
            }

            PngJsonCapturePublicationPlan plan = _decision.AuthoritativePlan;
            if (plan == null)
            {
                return false;
            }

            if (_entryIndex < 0 || _entryIndex >= plan.EntryCount)
            {
                return false;
            }

            if (!CheapCorrelated(_decision))
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = _decision.RootLayout;
            CaptureRunPublicationPathSet publicationPaths = _decision.Snapshot.Operation.PublicationPaths;

            PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(_entryIndex);
            if (entry == null || !entry.IsValid)
            {
                return false;
            }

            string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);

            return MatchesFixed(rootLayout.StagingRunRoot, entry.PngStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".png.stage", _stagingPngPath)
                && MatchesFixed(rootLayout.StagingRunRoot, entry.SidecarStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".json.stage", _stagingSidecarPath)
                && MatchesFixed(rootLayout.FinalRunRoot, entry.PngFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".png", _finalPngPath)
                && MatchesFixed(rootLayout.FinalRunRoot, entry.SidecarFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".json", _finalSidecarPath)
                && AreDistinct(_stagingPngPath, _stagingSidecarPath, _finalPngPath, _finalSidecarPath);
        }

        private static bool CheapCorrelated(CaptureRunPublicationRecoveryDecision decision)
        {
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;
            if (operation == null)
            {
                return false;
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = operation.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = operation.RootLayout;
            if (rootLayout == null)
            {
                return false;
            }

            CaptureRunPublicationPathSet publicationPaths = operation.PublicationPaths;
            return publicationPaths != null && ReferenceEquals(publicationPaths.RootLayout, rootLayout);
        }
    }
}
