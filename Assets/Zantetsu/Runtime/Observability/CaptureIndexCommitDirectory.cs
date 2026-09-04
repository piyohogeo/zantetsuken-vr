using System;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stable, no-follow-verified handle to the Capture Index final run root
    /// directory. The handle pins the exact directory identity, so a later
    /// path or junction swap cannot redirect the rename or delete of the files
    /// committed through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OriginalPath"/> is the normalized absolute path used for
    /// path-based creates; <see cref="CanonicalPath"/> is the handle-resolved
    /// identity used to confirm every opened file still lives inside this exact
    /// directory. The type owns and disposes only its directory handle and is
    /// not a MonoBehaviour or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureIndexCommitDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _originalPath;
        private readonly string _canonicalPath;

        internal CaptureIndexCommitDirectory(SafeFileHandle handle, string originalPath, string canonicalPath)
        {
            _handle = handle;
            _originalPath = originalPath;
            _canonicalPath = canonicalPath;
        }

        internal SafeFileHandle Handle => _handle;

        internal string OriginalPath => _originalPath;

        internal string CanonicalPath => _canonicalPath;

        public void Dispose()
        {
            _handle?.Dispose();
        }
    }
}
