using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run marker path contract: derives the
    /// eight fixed marker paths of a Run directly from its root layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The eight paths are the <c>run.init.tmp</c>, <c>run.init</c>,
    /// <c>run.ready.tmp</c>, and <c>run.ready</c> entries directly under each
    /// of the staging and final Run roots. They are derived with a single
    /// <see cref="Path.Combine"/> per path and stored once.
    /// </para>
    /// <para>
    /// This type performs no file, directory, or stream operation, no existence
    /// check or creation, no reparse point or identity check, no locking, no
    /// marker generation, serialize, or hash computation, no tmp write, flush,
    /// or rename, no recovery, collision classification, or cleanup, and no
    /// clock, random, or Unity static API access. It never mutates the input
    /// <see cref="CaptureRunRootLayout"/> or the stored root strings.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunMarkerPathSet
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly string _stagingInitializationTemporaryPath;
        private readonly string _stagingInitializationPath;
        private readonly string _stagingReadyTemporaryPath;
        private readonly string _stagingReadyPath;
        private readonly string _finalInitializationTemporaryPath;
        private readonly string _finalInitializationPath;
        private readonly string _finalReadyTemporaryPath;
        private readonly string _finalReadyPath;

        internal CaptureRunMarkerPathSet(CaptureRunRootLayout rootLayout)
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

            string stagingInitializationTemporaryPath = RequireMarkerPath(stagingRunRoot, "run.init.tmp");
            string stagingInitializationPath = RequireMarkerPath(stagingRunRoot, "run.init");
            string stagingReadyTemporaryPath = RequireMarkerPath(stagingRunRoot, "run.ready.tmp");
            string stagingReadyPath = RequireMarkerPath(stagingRunRoot, "run.ready");

            string finalInitializationTemporaryPath = RequireMarkerPath(finalRunRoot, "run.init.tmp");
            string finalInitializationPath = RequireMarkerPath(finalRunRoot, "run.init");
            string finalReadyTemporaryPath = RequireMarkerPath(finalRunRoot, "run.ready.tmp");
            string finalReadyPath = RequireMarkerPath(finalRunRoot, "run.ready");

            if (string.Equals(stagingInitializationPath, finalInitializationPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Staging and final initialization marker paths must differ.");
            }

            if (string.Equals(stagingReadyPath, finalReadyPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Staging and final ready marker paths must differ.");
            }

            _rootLayout = rootLayout;
            _stagingInitializationTemporaryPath = stagingInitializationTemporaryPath;
            _stagingInitializationPath = stagingInitializationPath;
            _stagingReadyTemporaryPath = stagingReadyTemporaryPath;
            _stagingReadyPath = stagingReadyPath;
            _finalInitializationTemporaryPath = finalInitializationTemporaryPath;
            _finalInitializationPath = finalInitializationPath;
            _finalReadyTemporaryPath = finalReadyTemporaryPath;
            _finalReadyPath = finalReadyPath;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal string StagingInitializationTemporaryPath => _stagingInitializationTemporaryPath;

        internal string StagingInitializationPath => _stagingInitializationPath;

        internal string StagingReadyTemporaryPath => _stagingReadyTemporaryPath;

        internal string StagingReadyPath => _stagingReadyPath;

        internal string FinalInitializationTemporaryPath => _finalInitializationTemporaryPath;

        internal string FinalInitializationPath => _finalInitializationPath;

        internal string FinalReadyTemporaryPath => _finalReadyTemporaryPath;

        internal string FinalReadyPath => _finalReadyPath;

        private static string RequireMarkerPath(string runRoot, string basename)
        {
            string path = Path.GetFullPath(Path.Combine(runRoot, basename));

            if (!string.Equals(Path.GetDirectoryName(path), runRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Marker path must be a direct child of the run root.");
            }

            if (!string.Equals(Path.GetFileName(path), basename, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Marker path basename must match the fixed name.");
            }

            return path;
        }
    }
}
