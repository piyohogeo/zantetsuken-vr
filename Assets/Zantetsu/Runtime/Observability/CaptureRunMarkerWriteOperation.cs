using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free value contract for a single Capture Run
    /// marker write operation: the root role, marker kind, temporary and final
    /// paths, and the canonical bytes to write. It is the boundary value an
    /// atomic writer consumes; it performs no write itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The temporary path must be the final path with a <c>.tmp</c> suffix and
    /// share its parent directory; both must be fully qualified absolute paths
    /// whose basenames match the fixed per-kind names. Paths are compared with
    /// <see cref="StringComparison.Ordinal"/> and are never corrected,
    /// re-rooted, case-folded, Unicode-normalized, or separator-converted.
    /// </para>
    /// <para>
    /// On successful construction the caller's byte array is taken by reference
    /// without copying and the caller's variable is nulled; on any validation
    /// failure the caller's variable and array contents are left untouched and
    /// the caller keeps ownership. <see cref="GetCanonicalBytes"/> returns a
    /// fresh defensive copy, so callers can never mutate the held array. No
    /// dispose contract is introduced for the managed array.
    /// </para>
    /// <para>
    /// This type holds no document set, plan, binding, or marker, calls no
    /// codec, serializes or decodes nothing, computes no hash, issues no
    /// initialization ID, performs no file, directory, or stream access, no
    /// write, flush, rename, or directory creation, no OS locking, no retry,
    /// recovery, or collision classification, and no logger, registry, draft,
    /// clock, random, or Unity static API access. It is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunMarkerWriteOperation
    {
        private readonly CaptureRunRootRole _rootRole;
        private readonly CaptureRunMarkerKind _markerKind;
        private readonly string _temporaryPath;
        private readonly string _finalPath;
        private readonly byte[] _canonicalBytes;

        internal CaptureRunMarkerWriteOperation(
            CaptureRunRootRole rootRole,
            CaptureRunMarkerKind markerKind,
            string temporaryPath,
            string finalPath,
            ref byte[] canonicalBytes)
        {
            if (rootRole != CaptureRunRootRole.Staging && rootRole != CaptureRunRootRole.Final)
            {
                throw new ArgumentOutOfRangeException(nameof(rootRole), rootRole, "Root role must be Staging or Final.");
            }

            if (markerKind != CaptureRunMarkerKind.Initialization && markerKind != CaptureRunMarkerKind.Ready)
            {
                throw new ArgumentOutOfRangeException(nameof(markerKind), markerKind, "Marker kind must be Initialization or Ready.");
            }

            if (temporaryPath == null)
            {
                throw new ArgumentNullException(nameof(temporaryPath));
            }

            if (finalPath == null)
            {
                throw new ArgumentNullException(nameof(finalPath));
            }

            if (canonicalBytes == null)
            {
                throw new ArgumentNullException(nameof(canonicalBytes));
            }

            if (canonicalBytes.Length == 0)
            {
                throw new ArgumentException("Canonical bytes must not be empty.", nameof(canonicalBytes));
            }

            if (canonicalBytes.Length > 4 * 1024)
            {
                throw new ArgumentException("Canonical bytes exceed the maximum allowed byte count.", nameof(canonicalBytes));
            }

            if (!Path.IsPathFullyQualified(temporaryPath))
            {
                throw new ArgumentException("Temporary marker path must be a fully qualified absolute path.", nameof(temporaryPath));
            }

            if (!Path.IsPathFullyQualified(finalPath))
            {
                throw new ArgumentException("Final marker path must be a fully qualified absolute path.", nameof(finalPath));
            }

            string temporaryParent = Path.GetDirectoryName(temporaryPath);
            string finalParent = Path.GetDirectoryName(finalPath);

            if (!string.Equals(temporaryParent, finalParent, StringComparison.Ordinal))
            {
                throw new ArgumentException("Temporary and final marker paths must share the same parent directory.", nameof(temporaryPath));
            }

            if (!string.Equals(temporaryPath, finalPath + ".tmp", StringComparison.Ordinal))
            {
                throw new ArgumentException("Temporary marker path must be the final marker path with a \".tmp\" suffix.", nameof(temporaryPath));
            }

            string temporaryBasename = markerKind == CaptureRunMarkerKind.Initialization ? "run.init.tmp" : "run.ready.tmp";
            string finalBasename = markerKind == CaptureRunMarkerKind.Initialization ? "run.init" : "run.ready";

            if (!string.Equals(Path.GetFileName(temporaryPath), temporaryBasename, StringComparison.Ordinal))
            {
                throw new ArgumentException("Temporary marker path basename must be \"" + temporaryBasename + "\".", nameof(temporaryPath));
            }

            if (!string.Equals(Path.GetFileName(finalPath), finalBasename, StringComparison.Ordinal))
            {
                throw new ArgumentException("Final marker path basename must be \"" + finalBasename + "\".", nameof(finalPath));
            }

            _rootRole = rootRole;
            _markerKind = markerKind;
            _temporaryPath = temporaryPath;
            _finalPath = finalPath;
            _canonicalBytes = canonicalBytes;

            canonicalBytes = null;
        }

        internal CaptureRunRootRole RootRole => _rootRole;

        internal CaptureRunMarkerKind MarkerKind => _markerKind;

        internal string TemporaryPath => _temporaryPath;

        internal string FinalPath => _finalPath;

        internal int ByteCount => _canonicalBytes.Length;

        internal bool IsValid
        {
            get
            {
                if (_rootRole != CaptureRunRootRole.Staging && _rootRole != CaptureRunRootRole.Final)
                {
                    return false;
                }

                if (_markerKind != CaptureRunMarkerKind.Initialization && _markerKind != CaptureRunMarkerKind.Ready)
                {
                    return false;
                }

                if (_temporaryPath == null || _finalPath == null)
                {
                    return false;
                }

                if (!Path.IsPathFullyQualified(_temporaryPath) || !Path.IsPathFullyQualified(_finalPath))
                {
                    return false;
                }

                if (!string.Equals(_temporaryPath, _finalPath + ".tmp", StringComparison.Ordinal))
                {
                    return false;
                }

                string temporaryBasename = _markerKind == CaptureRunMarkerKind.Initialization ? "run.init.tmp" : "run.ready.tmp";
                string finalBasename = _markerKind == CaptureRunMarkerKind.Initialization ? "run.init" : "run.ready";

                if (!string.Equals(LastSegment(_temporaryPath), temporaryBasename, StringComparison.Ordinal)
                    || !string.Equals(LastSegment(_finalPath), finalBasename, StringComparison.Ordinal))
                {
                    return false;
                }

                if (_canonicalBytes == null)
                {
                    return false;
                }

                return _canonicalBytes.Length >= 1 && _canonicalBytes.Length <= 4 * 1024;
            }
        }

        internal byte[] GetCanonicalBytes()
        {
            byte[] copy = new byte[_canonicalBytes.Length];
            Array.Copy(_canonicalBytes, copy, _canonicalBytes.Length);
            return copy;
        }

        private static string LastSegment(string path)
        {
            int slash = path.LastIndexOf(Path.DirectorySeparatorChar);
            int altSlash = path.LastIndexOf(Path.AltDirectorySeparatorChar);
            int last = slash >= altSlash ? slash : altSlash;
            return last < 0 ? path : path.Substring(last + 1);
        }
    }
}
