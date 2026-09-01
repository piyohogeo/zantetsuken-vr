using System;
using System.Globalization;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run publication artifact path
    /// contract for the exclusive inspection authority: resolves the four
    /// relative paths of one authoritative plan entry into fixed absolute
    /// paths under the staging and final frames roots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type holds only the authority, the entry index, and the four
    /// derived paths; it duplicates no plan, entry, root layout, lease, or
    /// identifier. The four absolute paths are derived with a single
    /// <see cref="Path.Combine"/> per path from the authority's root layout
    /// run roots and the entry's relative paths, confirmed with
    /// <see cref="Path.GetFullPath"/>, and stored once. Their parent
    /// directories are the staging and final frames roots and their basenames
    /// are the fixed <c>{id}.png.stage</c>, <c>{id}.json.stage</c>,
    /// <c>{id}.png</c>, and <c>{id}.json</c> forms of the invariant shortest
    /// decimal <see cref="CaptureFrameId"/>.
    /// </para>
    /// <para>
    /// This type performs no file, directory, or stream operation, no
    /// existence check or creation, no hash computation, and no locking. It
    /// holds the authority as a non-owning reference and mutates or disposes
    /// nothing.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactInspectionPathSet
    {
        private readonly PngJsonCapturePublicationArtifactInspectionAuthority _authority;
        private readonly int _entryIndex;
        private readonly string _stagingPngPath;
        private readonly string _stagingSidecarPath;
        private readonly string _finalPngPath;
        private readonly string _finalSidecarPath;

        /// <summary>
        /// Normal construction: fully validates the authority and issues a
        /// validation token once, then verifies the entry index and derives
        /// the four paths before assigning fields.
        /// </summary>
        internal PngJsonCapturePublicationArtifactInspectionPathSet(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            int entryIndex)
        {
            if (authority == null)
            {
                throw new ArgumentNullException(nameof(authority));
            }

            if (!PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.TryAcquire(
                authority,
                out PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token))
            {
                throw new ArgumentException("Authority must be fully valid.", nameof(authority));
            }

            PngJsonCapturePublicationArtifactInspectionPathSet built = CreateIndexLocal(token, authority, entryIndex);

            _authority = built._authority;
            _entryIndex = built._entryIndex;
            _stagingPngPath = built._stagingPngPath;
            _stagingSidecarPath = built._stagingSidecarPath;
            _finalPngPath = built._finalPngPath;
            _finalSidecarPath = built._finalSidecarPath;
        }

        private PngJsonCapturePublicationArtifactInspectionPathSet(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            int entryIndex,
            string stagingPngPath,
            string stagingSidecarPath,
            string finalPngPath,
            string finalSidecarPath)
        {
            _authority = authority;
            _entryIndex = entryIndex;
            _stagingPngPath = stagingPngPath;
            _stagingSidecarPath = stagingSidecarPath;
            _finalPngPath = finalPngPath;
            _finalSidecarPath = finalSidecarPath;
        }

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _authority;

        internal int EntryIndex => _entryIndex;

        internal PngJsonCapturePublicationPlan Plan => _authority.AuthoritativePlan;

        internal PngJsonCapturePublicationPlanEntry Entry => Plan.GetEntry(_entryIndex);

        internal long CaptureFrameId => Entry.CaptureFrameId;

        internal string StagingPngPath => _stagingPngPath;

        internal string StagingSidecarPath => _stagingSidecarPath;

        internal string FinalPngPath => _finalPngPath;

        internal string FinalSidecarPath => _finalSidecarPath;

        internal CaptureRunRootLayout RootLayout => _authority.RootLayout;

        internal CaptureRunLockLease LockLease => _authority.LockLease;

        internal long TestRunId => _authority.TestRunId;

        internal string RunInitializationId => _authority.RunInitializationId;

        /// <summary>
        /// Full validity: re-validates the authority once, then re-derives this
        /// single entry's four paths and compares them to the stored values.
        /// Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_authority == null || !_authority.IsValid)
                {
                    return false;
                }

                return ReDerivesToStored();
            }
        }

        /// <summary>
        /// O(1) token-gated index-local validity: confirms the token is still
        /// correlated with this authority and entry in O(1) — without the full
        /// authority validation or a plan scan — then re-derives this single
        /// entry's four paths. Never throws.
        /// </summary>
        internal bool IsValidIndexLocal(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token)
        {
            if (token == null)
            {
                return false;
            }

            if (!token.IsIndexLocalCorrelated(_authority, _entryIndex))
            {
                return false;
            }

            return ReDerivesToStored();
        }

        /// <summary>
        /// Trusted index-local construction: assumes the authority was fully
        /// validated and tokenized by the caller, and verifies only the token
        /// binding, lease liveness, and exact plan, entry, publication path
        /// set, and root layout correlation in O(1) before deriving the four
        /// paths. The authority's full validation is not re-run.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactInspectionPathSet CreateIndexLocal(
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token,
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            int entryIndex)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (authority == null)
            {
                throw new ArgumentNullException(nameof(authority));
            }

            if (!token.IsIssuedFor(authority))
            {
                throw new ArgumentException("Token must be issued for the exact authority.", nameof(token));
            }

            PngJsonCapturePublicationPlan plan;
            try
            {
                plan = authority.AuthoritativePlan;
            }
            catch (Exception)
            {
                throw new ArgumentException("Authority graph must remain uncorrupted.", nameof(authority));
            }

            if (plan == null)
            {
                throw new ArgumentException("Authority must hold an authoritative plan.", nameof(authority));
            }

            if (entryIndex < 0 || entryIndex >= plan.EntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Entry index must be within the authoritative plan entry count.");
            }

            if (!token.IsIndexLocalCorrelated(authority, entryIndex))
            {
                throw new ArgumentException(
                    "Token must be issued for the exact authority, its lease must still be live, and its plan, entry, publication paths, and root layout must remain uncorrupted.",
                    nameof(token));
            }

            Derive(
                authority,
                entryIndex,
                out string stagingPngPath,
                out string stagingSidecarPath,
                out string finalPngPath,
                out string finalSidecarPath);

            return new PngJsonCapturePublicationArtifactInspectionPathSet(
                authority, entryIndex, stagingPngPath, stagingSidecarPath, finalPngPath, finalSidecarPath);
        }

        private bool ReDerivesToStored()
        {
            PngJsonCapturePublicationPlan plan = _authority.AuthoritativePlan;
            if (plan == null)
            {
                return false;
            }

            if (_entryIndex < 0 || _entryIndex >= plan.EntryCount)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = _authority.RootLayout;
            CaptureRunPublicationPathSet publicationPaths = _authority.PublicationPaths;
            if (rootLayout == null || publicationPaths == null)
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

        private static void Derive(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            int entryIndex,
            out string stagingPngPath,
            out string stagingSidecarPath,
            out string finalPngPath,
            out string finalSidecarPath)
        {
            PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
            if (plan == null)
            {
                throw new ArgumentException("Authority must hold an authoritative plan.", nameof(authority));
            }

            if (entryIndex < 0 || entryIndex >= plan.EntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Entry index must be within the authoritative plan entry count.");
            }

            CaptureRunRootLayout rootLayout = authority.RootLayout;
            CaptureRunPublicationPathSet publicationPaths = authority.PublicationPaths;
            if (rootLayout == null || publicationPaths == null)
            {
                throw new ArgumentException("Authority must expose its root layout and publication paths.", nameof(authority));
            }

            PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(entryIndex);
            if (entry == null || !entry.IsValid)
            {
                throw new ArgumentException("Target plan entry must be valid.", nameof(authority));
            }

            string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);

            try
            {
                stagingPngPath = DeriveArtifactPath(rootLayout.StagingRunRoot, entry.PngStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".png.stage");
                stagingSidecarPath = DeriveArtifactPath(rootLayout.StagingRunRoot, entry.SidecarStagingRelativePath, publicationPaths.StagingFramesRoot, id + ".json.stage");
                finalPngPath = DeriveArtifactPath(rootLayout.FinalRunRoot, entry.PngFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".png");
                finalSidecarPath = DeriveArtifactPath(rootLayout.FinalRunRoot, entry.SidecarFinalRelativePath, publicationPaths.FinalFramesRoot, id + ".json");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                throw new ArgumentException("Plan entry paths must resolve to fixed absolute artifact paths.", nameof(authority), ex);
            }

            if (!AreDistinct(stagingPngPath, stagingSidecarPath, finalPngPath, finalSidecarPath))
            {
                throw new ArgumentException("Artifact paths must be mutually distinct.", nameof(authority));
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
    }
}
