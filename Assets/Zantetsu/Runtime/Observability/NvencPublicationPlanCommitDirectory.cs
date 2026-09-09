using System;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stable, no-follow-verified handle to the NVENC publication plan staging
    /// Run root directory. The handle pins the exact directory identity, so a
    /// later path or junction swap cannot redirect a create or rename outside
    /// the staging Run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OriginalPath"/> is the normalized absolute path used for
    /// path-based creates; <see cref="CanonicalPath"/> is the handle-resolved
    /// identity used to confirm every opened or created file still lives inside
    /// this exact directory. The type owns and disposes only its directory
    /// handle and is not a MonoBehaviour or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencPublicationPlanCommitDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _originalPath;
        private readonly string _canonicalPath;
        private bool _disposed;

        internal NvencPublicationPlanCommitDirectory(
            SafeFileHandle handle,
            string originalPath,
            string canonicalPath)
        {
            _handle = handle;
            _originalPath = originalPath;
            _canonicalPath = canonicalPath;
        }

        internal SafeFileHandle Handle => _handle;

        internal string OriginalPath => _originalPath;

        internal string CanonicalPath => _canonicalPath;

        internal bool IsDisposed => _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle?.Dispose();
        }
    }
}
