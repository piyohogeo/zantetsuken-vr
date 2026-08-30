using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run publication path contract: fixes
    /// the six absolute paths a publication recovery observes, all derived
    /// directly from the run's root layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The six paths are the <c>frames</c> directory, <c>publication.plan.tmp</c>,
    /// and <c>publication.plan</c> under the staging run root, and the
    /// <c>frames</c> directory, <c>capture.index.tmp</c>, and <c>capture.index</c>
    /// under the final run root. Each is derived with a single
    /// <see cref="Path.Combine"/> and confirmed with <see cref="Path.GetFullPath"/>
    /// before being stored once.
    /// </para>
    /// <para>
    /// This type performs no existence check, creation, or enumeration, no
    /// no-follow or reparse point check, no plan or index decode, no hash
    /// computation, no artifact enumeration, no relative path generation, no
    /// locking, no recovery classification, no tmp removal or rename, and no
    /// clock, random, or Unity static API access. Only pure
    /// <see cref="System.IO.Path"/> operations are used.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationPathSet
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly string _stagingFramesRoot;
        private readonly string _publicationPlanTemporaryPath;
        private readonly string _publicationPlanPath;
        private readonly string _finalFramesRoot;
        private readonly string _captureIndexTemporaryPath;
        private readonly string _captureIndexPath;

        internal CaptureRunPublicationPathSet(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            string stagingRunRoot = rootLayout.StagingRunRoot;
            string finalRunRoot = rootLayout.FinalRunRoot;

            if (string.IsNullOrEmpty(stagingRunRoot))
            {
                throw new InvalidOperationException("Staging run root must not be null or empty.");
            }

            if (string.IsNullOrEmpty(finalRunRoot))
            {
                throw new InvalidOperationException("Final run root must not be null or empty.");
            }

            if (!Path.IsPathFullyQualified(stagingRunRoot))
            {
                throw new InvalidOperationException("Staging run root must be a fully qualified absolute path.");
            }

            if (!Path.IsPathFullyQualified(finalRunRoot))
            {
                throw new InvalidOperationException("Final run root must be a fully qualified absolute path.");
            }

            string stagingFramesRoot = RequireChildPath(stagingRunRoot, "frames");
            string publicationPlanTemporaryPath = RequireChildPath(stagingRunRoot, "publication.plan.tmp");
            string publicationPlanPath = RequireChildPath(stagingRunRoot, "publication.plan");
            string finalFramesRoot = RequireChildPath(finalRunRoot, "frames");
            string captureIndexTemporaryPath = RequireChildPath(finalRunRoot, "capture.index.tmp");
            string captureIndexPath = RequireChildPath(finalRunRoot, "capture.index");

            if (string.Equals(stagingFramesRoot, publicationPlanTemporaryPath, StringComparison.Ordinal)
                || string.Equals(stagingFramesRoot, publicationPlanPath, StringComparison.Ordinal)
                || string.Equals(publicationPlanTemporaryPath, publicationPlanPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Staging publication paths must be mutually distinct.");
            }

            if (string.Equals(finalFramesRoot, captureIndexTemporaryPath, StringComparison.Ordinal)
                || string.Equals(finalFramesRoot, captureIndexPath, StringComparison.Ordinal)
                || string.Equals(captureIndexTemporaryPath, captureIndexPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Final publication paths must be mutually distinct.");
            }

            if (string.Equals(stagingFramesRoot, finalFramesRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Staging and final frames roots must differ.");
            }

            _rootLayout = rootLayout;
            _stagingFramesRoot = stagingFramesRoot;
            _publicationPlanTemporaryPath = publicationPlanTemporaryPath;
            _publicationPlanPath = publicationPlanPath;
            _finalFramesRoot = finalFramesRoot;
            _captureIndexTemporaryPath = captureIndexTemporaryPath;
            _captureIndexPath = captureIndexPath;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal string StagingFramesRoot => _stagingFramesRoot;

        internal string PublicationPlanTemporaryPath => _publicationPlanTemporaryPath;

        internal string PublicationPlanPath => _publicationPlanPath;

        internal string FinalFramesRoot => _finalFramesRoot;

        internal string CaptureIndexTemporaryPath => _captureIndexTemporaryPath;

        internal string CaptureIndexPath => _captureIndexPath;

        internal bool IsValid
        {
            get
            {
                if (_rootLayout == null)
                {
                    return false;
                }

                string stagingRunRoot = _rootLayout.StagingRunRoot;
                string finalRunRoot = _rootLayout.FinalRunRoot;

                if (string.IsNullOrEmpty(stagingRunRoot) || string.IsNullOrEmpty(finalRunRoot))
                {
                    return false;
                }

                if (!Path.IsPathFullyQualified(stagingRunRoot) || !Path.IsPathFullyQualified(finalRunRoot))
                {
                    return false;
                }

                if (!MatchesFixed(stagingRunRoot, "frames", _stagingFramesRoot)
                    || !MatchesFixed(stagingRunRoot, "publication.plan.tmp", _publicationPlanTemporaryPath)
                    || !MatchesFixed(stagingRunRoot, "publication.plan", _publicationPlanPath)
                    || !MatchesFixed(finalRunRoot, "frames", _finalFramesRoot)
                    || !MatchesFixed(finalRunRoot, "capture.index.tmp", _captureIndexTemporaryPath)
                    || !MatchesFixed(finalRunRoot, "capture.index", _captureIndexPath))
                {
                    return false;
                }

                if (string.Equals(_stagingFramesRoot, _finalFramesRoot, StringComparison.Ordinal)
                    || string.Equals(_stagingFramesRoot, _publicationPlanTemporaryPath, StringComparison.Ordinal)
                    || string.Equals(_stagingFramesRoot, _publicationPlanPath, StringComparison.Ordinal)
                    || string.Equals(_finalFramesRoot, _captureIndexTemporaryPath, StringComparison.Ordinal)
                    || string.Equals(_finalFramesRoot, _captureIndexPath, StringComparison.Ordinal))
                {
                    return false;
                }

                return true;
            }
        }

        private static bool MatchesFixed(string runRoot, string basename, string storedPath)
        {
            if (storedPath == null)
            {
                return false;
            }

            try
            {
                string derived = Path.GetFullPath(Path.Combine(runRoot, basename));
                return string.Equals(storedPath, derived, StringComparison.Ordinal)
                    && string.Equals(Path.GetDirectoryName(derived), runRoot, StringComparison.Ordinal)
                    && string.Equals(Path.GetFileName(derived), basename, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                return false;
            }
        }

        private static string RequireChildPath(string runRoot, string basename)
        {
            string path = Path.GetFullPath(Path.Combine(runRoot, basename));

            if (!string.Equals(Path.GetDirectoryName(path), runRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Publication path must be a direct child of the run root.");
            }

            if (!string.Equals(Path.GetFileName(path), basename, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Publication path basename must match the fixed name.");
            }

            return path;
        }
    }
}
