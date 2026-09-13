using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Capture Run marker path set: the four fixed marker paths
    /// directly under the Run root — the initialization marker and the ready
    /// marker, each with its temporary counterpart. No directory or file is
    /// checked, created, or removed.
    /// </summary>
    internal sealed class CaptureRunMarkerPathSet
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly string _initializationTemporaryPath;
        private readonly string _initializationPath;
        private readonly string _readyTemporaryPath;
        private readonly string _readyPath;

        internal CaptureRunMarkerPathSet(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            string runRoot = rootLayout.RunRoot;
            if (string.IsNullOrEmpty(runRoot))
            {
                throw new InvalidOperationException("Run root must not be null or empty.");
            }

            if (!Path.IsPathFullyQualified(runRoot))
            {
                throw new InvalidOperationException("Run root must be a fully qualified absolute path.");
            }

            _rootLayout = rootLayout;
            _initializationTemporaryPath = RequireMarkerPath(runRoot, "run.init.tmp");
            _initializationPath = RequireMarkerPath(runRoot, "run.init");
            _readyTemporaryPath = RequireMarkerPath(runRoot, "run.ready.tmp");
            _readyPath = RequireMarkerPath(runRoot, "run.ready");
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal string InitializationTemporaryPath => _initializationTemporaryPath;

        internal string InitializationPath => _initializationPath;

        internal string ReadyTemporaryPath => _readyTemporaryPath;

        internal string ReadyPath => _readyPath;

        internal bool IsValid
        {
            get
            {
                if (_rootLayout == null)
                {
                    return false;
                }

                string runRoot = _rootLayout.RunRoot;
                if (string.IsNullOrEmpty(runRoot))
                {
                    return false;
                }

                return MatchesFixed(runRoot, "run.init.tmp", _initializationTemporaryPath)
                    && MatchesFixed(runRoot, "run.init", _initializationPath)
                    && MatchesFixed(runRoot, "run.ready.tmp", _readyTemporaryPath)
                    && MatchesFixed(runRoot, "run.ready", _readyPath);
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
