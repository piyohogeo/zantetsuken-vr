using System;
using System.Globalization;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Capture Run OS lock path set: derives the two fixed lock paths
    /// for a Run and fixes the deterministic acquisition order shared by all
    /// coordinators, before any handle is acquired. No directory, file, or
    /// handle is created, opened, or held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock paths are
    /// <c>{StagingTrustedBaseRoot}/.locks/run-{TestRunId}.lock</c> and
    /// <c>{FinalTrustedBaseRoot}/.locks/run-{TestRunId}.lock</c>, never under
    /// the per-Run <c>runs/run-{id}</c> roots. The base names are fixed ASCII.
    /// </para>
    /// <para>
    /// Ordering compares the two paths ascending by
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> first and breaks ties
    /// with an ordinal comparison; identical paths are never collapsed into a
    /// single lock. <see cref="FirstRootRole"/> and
    /// <see cref="SecondRootRole"/> report the Staging or Final origin of the
    /// sorted paths so provenance is preserved after sorting.
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
        private readonly string _stagingLockPath;
        private readonly string _finalLockPath;
        private readonly bool _stagingFirst;

        internal CaptureRunLockPathSet(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            string stagingLockPath = NormalizeLockPath(BuildLockPath(rootLayout.StagingTrustedBaseRoot, rootLayout.TestRunId));
            string finalLockPath = NormalizeLockPath(BuildLockPath(rootLayout.FinalTrustedBaseRoot, rootLayout.TestRunId));

            RequireLockPathInside(rootLayout.StagingTrustedBaseRoot, stagingLockPath, rootLayout.TestRunId);
            RequireLockPathInside(rootLayout.FinalTrustedBaseRoot, finalLockPath, rootLayout.TestRunId);

            if (string.Equals(stagingLockPath, finalLockPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Staging and final lock paths must differ.");
            }

            _rootLayout = rootLayout;
            _stagingLockPath = stagingLockPath;
            _finalLockPath = finalLockPath;
            _stagingFirst = StagingComesFirst(stagingLockPath, finalLockPath);
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal string StagingLockPath => _stagingLockPath;

        internal string FinalLockPath => _finalLockPath;

        internal string FirstLockPath => _stagingFirst ? _stagingLockPath : _finalLockPath;

        internal string SecondLockPath => _stagingFirst ? _finalLockPath : _stagingLockPath;

        internal CaptureRunRootRole FirstRootRole => _stagingFirst ? CaptureRunRootRole.Staging : CaptureRunRootRole.Final;

        internal CaptureRunRootRole SecondRootRole => _stagingFirst ? CaptureRunRootRole.Final : CaptureRunRootRole.Staging;

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

        private static bool StagingComesFirst(string stagingLockPath, string finalLockPath)
        {
            int comparison = string.Compare(stagingLockPath, finalLockPath, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            return string.Compare(stagingLockPath, finalLockPath, StringComparison.Ordinal) < 0;
        }
    }
}
