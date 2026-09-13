using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free value contract for provisioning one brand-new
    /// Capture Run root: the trusted base root and run root derived from a root
    /// layout, with the layout as the single authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TrustedBaseRoot"/>, <see cref="RunRoot"/>, and
    /// <see cref="TestRunId"/> are forwarded from the layout without copying;
    /// no path is re-normalized, re-generated, case-folded, or
    /// Unicode-normalized. The run root must be inside its trusted base root at
    /// a segment boundary. This type performs no filesystem work.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunRootProvisionOperation
    {
        private readonly CaptureRunRootLayout _rootLayout;

        internal CaptureRunRootProvisionOperation(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            string trustedBaseRoot = rootLayout.TrustedBaseRoot;
            string runRoot = rootLayout.RunRoot;

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
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal string TrustedBaseRoot => _rootLayout.TrustedBaseRoot;

        internal string RunRoot => _rootLayout.RunRoot;

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
