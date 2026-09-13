using System;
using System.Globalization;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Capture Run OS lock path: derives the one fixed lock path for
    /// a Run before any handle is acquired. No directory, file, or handle is
    /// created, opened, or held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock path is
    /// <c>{TrustedBaseRoot}/.locks/run-{TestRunId}.lock</c>, never under the
    /// per-Run <c>runs/run-{id}</c> root. The base names are fixed ASCII.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing, performs no file, directory, or
    /// stream access, no reparse point or identity check, no retry, wait, or
    /// backpressure, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunLockPathSet
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly string _lockPath;

        internal CaptureRunLockPathSet(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            string lockPath = NormalizeLockPath(BuildLockPath(rootLayout.TrustedBaseRoot, rootLayout.TestRunId));
            RequireLockPathInside(rootLayout.TrustedBaseRoot, lockPath, rootLayout.TestRunId);

            _rootLayout = rootLayout;
            _lockPath = lockPath;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal string LockPath => _lockPath;

        private static string BuildLockPath(string baseRoot, long testRunId)
        {
            string fileName = "run-" + testRunId.ToString(CultureInfo.InvariantCulture) + ".lock";
            return Path.Combine(baseRoot, ".locks", fileName);
        }

        private static string NormalizeLockPath(string lockPath)
        {
            return Path.GetFullPath(lockPath);
        }

        private static void RequireLockPathInside(string baseRoot, string lockPath, long testRunId)
        {
            string expectedDirectory = Path.Combine(baseRoot, ".locks");
            string expectedFileName = "run-" + testRunId.ToString(CultureInfo.InvariantCulture) + ".lock";

            string actualDirectory = Path.GetDirectoryName(lockPath);
            string actualFileName = Path.GetFileName(lockPath);

            if (!string.Equals(actualDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualFileName, expectedFileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Lock path must be the fixed .locks entry directly under the trusted base root.");
            }
        }
    }
}
