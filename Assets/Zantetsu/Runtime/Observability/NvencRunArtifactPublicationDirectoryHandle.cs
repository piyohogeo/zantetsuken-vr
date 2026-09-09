using System;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// No-follow-verified directory handle used by the Fresh NVENC Run chunk
    /// artifact publication. The handle pins the exact directory identity, so
    /// a later path or junction swap cannot redirect a handle-relative open or
    /// the final rename outside the verified Run root.
    /// </summary>
    /// <remarks>
    /// The wrapper owns exactly one kernel handle and releases it exactly
    /// once. It holds no descriptor, operation, or verification buffer and
    /// exposes no delete, disposition, or rename API.
    /// </remarks>
    internal sealed class NvencRunArtifactPublicationDirectoryHandle : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _canonicalPath;
        private bool _disposed;

        internal NvencRunArtifactPublicationDirectoryHandle(SafeFileHandle handle, string canonicalPath)
        {
            _handle = handle;
            _canonicalPath = canonicalPath;
        }

        internal SafeFileHandle Handle => _handle;

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
