using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Capture Run root layout: derives the fixed per-Run root from
    /// one trusted base root and computes the root hash stored in
    /// <c>run.init</c>. This is a pure value contract over strings; no
    /// directory or file is checked, created, or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RunRelativePath"/> is always <c>runs/run-{TestRunId}</c>
    /// with the invariant shortest decimal <see cref="TestRunId"/>.
    /// </para>
    /// <para>
    /// The trusted base must be a fully qualified local absolute path.
    /// Relative paths, drive-relative paths, UNC, device, and extended paths
    /// are rejected. Canonicalization resolves <c>.</c> and <c>..</c> with
    /// <see cref="Path.GetFullPath"/>, unifies the alternate directory
    /// separator into the primary one, and removes trailing separators except
    /// for the filesystem root's own separator. Stored paths and hash inputs
    /// are never case-folded or Unicode-normalized. Same-base and
    /// ancestor/descendant checks are segment-aware and use
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> to conservatively
    /// reject case-only collisions without treating a shared prefix like
    /// <c>foo</c>/<c>foobar</c> as an ancestor.
    /// </para>
    /// <para>
    /// The root hash is the lowercase hex SHA-256 of the strict UTF-8 bytes
    /// of the normalized absolute Run root. Filesystem aliases, reparse points,
    /// and existence checks are the responsibility of later lock and filesystem
    /// layers, not of this type.
    /// </para>
    /// <para>
    /// This type holds only string and long values, is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject, and uses
    /// no file, directory, or stream API.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunRootLayout
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly long _testRunId;
        private readonly string _trustedBaseRoot;
        private readonly string _runRelativePath;
        private readonly string _runRoot;
        private readonly string _runRootSha256;

        internal CaptureRunRootLayout(string trustedBaseRoot, long testRunId)
        {
            if (trustedBaseRoot == null)
            {
                throw new ArgumentNullException(nameof(trustedBaseRoot));
            }

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(trustedBaseRoot))
            {
                throw new ArgumentException("Trusted base root must not be empty or whitespace.", nameof(trustedBaseRoot));
            }

            string normalized = NormalizeBaseRoot(trustedBaseRoot, nameof(trustedBaseRoot));
            string runRelativePath = "runs/run-" + testRunId.ToString(CultureInfo.InvariantCulture);
            string runRoot = CombineRoot(normalized, runRelativePath);

            if (!IsAncestor(normalized, runRoot))
            {
                throw new ArgumentException("Run root must fall inside the trusted base root.", nameof(trustedBaseRoot));
            }

            _testRunId = testRunId;
            _trustedBaseRoot = normalized;
            _runRelativePath = runRelativePath;
            _runRoot = runRoot;
            _runRootSha256 = ComputeRootSha256(runRoot);
        }

        internal long TestRunId => _testRunId;

        internal string TrustedBaseRoot => _trustedBaseRoot;

        internal string RunRelativePath => _runRelativePath;

        internal string RunRoot => _runRoot;

        internal string RunRootSha256 => _runRootSha256;

        /// <summary>
        /// Exception-safe recomputation of every invariant this layout
        /// guarantees. Returns <c>false</c> for any corrupted or partially
        /// populated instance without throwing.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    return IsValidCore();
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is NotSupportedException
                    || ex is IOException
                    || ex is EncoderFallbackException)
                {
                    return false;
                }
            }
        }

        private bool IsValidCore()
        {
            if (_testRunId <= 0)
            {
                return false;
            }

            string baseRoot = _trustedBaseRoot;
            string runRelativePath = _runRelativePath;
            string runRoot = _runRoot;
            string hash = _runRootSha256;

            if (baseRoot == null || runRelativePath == null || runRoot == null || hash == null)
            {
                return false;
            }

            if (!IsFullyQualifiedLocalAbsolutePath(baseRoot))
            {
                return false;
            }

            string normalized = NormalizeBaseRoot(baseRoot, "trustedBaseRoot");
            if (!string.Equals(baseRoot, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            string expectedRunRelativePath = "runs/run-" + _testRunId.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(runRelativePath, expectedRunRelativePath, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(runRoot, CombineRoot(normalized, runRelativePath), StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsAncestor(normalized, runRoot))
            {
                return false;
            }

            return string.Equals(hash, ComputeRootSha256(runRoot), StringComparison.Ordinal);
        }

        private static string NormalizeBaseRoot(string baseRoot, string paramName)
        {
            if (!IsFullyQualifiedLocalAbsolutePath(baseRoot))
            {
                throw new ArgumentException("Trusted base root must be a fully qualified local absolute path.", paramName);
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(baseRoot);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                throw new ArgumentException("Trusted base root is not a valid absolute path.", paramName, ex);
            }

            string normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            string root = Path.GetPathRoot(normalized);
            int rootLength = root != null ? root.Length : 0;
            while (normalized.Length > rootLength && normalized[normalized.Length - 1] == Path.DirectorySeparatorChar)
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            return normalized;
        }

        private static bool IsFullyQualifiedLocalAbsolutePath(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                // Windows: drive letter + colon + separator. Rejects UNC,
                // device, extended, and drive-relative paths.
                if (path.Length < 3)
                {
                    return false;
                }

                char drive = path[0];
                if (!((drive >= 'A' && drive <= 'Z') || (drive >= 'a' && drive <= 'z')))
                {
                    return false;
                }

                if (path[1] != ':')
                {
                    return false;
                }

                char separator = path[2];
                return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
            }

            // Non-Windows: a fully qualified local absolute path is rooted at
            // the directory separator (for example "/var/...").
            return path.Length > 0 && path[0] == Path.DirectorySeparatorChar;
        }

        private static bool IsAncestor(string ancestor, string descendant)
        {
            if (ancestor.Length == 0 || ancestor.Length > descendant.Length)
            {
                return false;
            }

            if (!descendant.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ancestor.Length == descendant.Length)
            {
                return true;
            }

            if (ancestor[ancestor.Length - 1] == Path.DirectorySeparatorChar)
            {
                return true;
            }

            return descendant[ancestor.Length] == Path.DirectorySeparatorChar;
        }

        private static string CombineRoot(string baseRoot, string runRelativePath)
        {
            string relative = runRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return Path.Combine(baseRoot, relative);
        }

        private static string ComputeRootSha256(string runRoot)
        {
            byte[] utf8 = StrictUtf8.GetBytes(runRoot);
            using (SHA256 sha = SHA256.Create())
            {
                return CaptureRunInitializationMarkerCodec.ToLowerHex(sha.ComputeHash(utf8));
            }
        }
    }
}
