using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free value contract for provisioning one brand-new
    /// Capture Run root: the trusted base root and run root derived from a root
    /// layout for a chosen role, with the layout as the single authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TrustedBaseRoot"/>, <see cref="RunRoot"/>, and
    /// <see cref="TestRunId"/> are forwarded from the layout without copying;
    /// no path is re-normalized, re-generated, case-folded, or
    /// Unicode-normalized. The chosen run root must be inside its trusted base
    /// root at a segment boundary. This type performs no filesystem work.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunRootProvisionOperation
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly CaptureRunRootRole _rootRole;

        internal CaptureRunRootProvisionOperation(
            CaptureRunRootLayout rootLayout,
            CaptureRunRootRole rootRole)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            if (rootRole != CaptureRunRootRole.Staging && rootRole != CaptureRunRootRole.Final)
            {
                throw new ArgumentOutOfRangeException(nameof(rootRole), rootRole, "Root role must be Staging or Final.");
            }

            string trustedBaseRoot = rootRole == CaptureRunRootRole.Staging
                ? rootLayout.StagingTrustedBaseRoot
                : rootLayout.FinalTrustedBaseRoot;

            string runRoot = rootRole == CaptureRunRootRole.Staging
                ? rootLayout.StagingRunRoot
                : rootLayout.FinalRunRoot;

            if (string.IsNullOrEmpty(trustedBaseRoot))
            {
                throw new ArgumentException("Trusted base root must not be null or empty.", nameof(rootLayout));
            }

            if (string.IsNullOrEmpty(runRoot))
            {
                throw new ArgumentException("Run root must not be null or empty.", nameof(rootLayout));
            }

            if (!IsWithinTrustedBase(trustedBaseRoot, runRoot))
            {
                throw new ArgumentException("Run root must be inside the trusted base root at a segment boundary.", nameof(rootLayout));
            }

            _rootLayout = rootLayout;
            _rootRole = rootRole;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal CaptureRunRootRole RootRole => _rootRole;

        internal string TrustedBaseRoot => _rootRole == CaptureRunRootRole.Staging
            ? _rootLayout.StagingTrustedBaseRoot
            : _rootLayout.FinalTrustedBaseRoot;

        internal string RunRoot => _rootRole == CaptureRunRootRole.Staging
            ? _rootLayout.StagingRunRoot
            : _rootLayout.FinalRunRoot;

        internal long TestRunId => _rootLayout.TestRunId;

        private static bool IsWithinTrustedBase(string trustedBaseRoot, string runRoot)
        {
            if (trustedBaseRoot.Length >= runRoot.Length)
            {
                return false;
            }

            if (!runRoot.StartsWith(trustedBaseRoot, StringComparison.Ordinal))
            {
                return false;
            }

            char boundary = runRoot[trustedBaseRoot.Length];
            return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
        }
    }
}
