using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// No-follow-verified chunk file handle used by the Fresh NVENC Run chunk
    /// artifact publication. The same file identity carries the staging
    /// inspection, the single non-overwriting rename, and the one full read of
    /// the placed final file, so no step can be redirected to a different file
    /// by a concurrent path swap.
    /// </summary>
    /// <remarks>
    /// The wrapper owns one kernel handle and at most one adopted read stream
    /// and releases both exactly once. A failing stream release still releases
    /// the handle. It holds no descriptor, operation, or verification buffer
    /// and exposes no delete or disposition API.
    /// </remarks>
    internal sealed class NvencRunArtifactPublicationFileHandle : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private Stream _stream;
        private bool _disposed;

        internal NvencRunArtifactPublicationFileHandle(SafeFileHandle handle)
        {
            _handle = handle;
        }

        internal SafeFileHandle Handle => _handle;

        internal Stream Stream => _stream;

        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Takes ownership of the single read stream opened over this exact
        /// file identity. A second adoption is a programming error: it would
        /// leave a stream unreleased and would imply a second read pass.
        /// </summary>
        internal void AdoptStream(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (_stream != null)
            {
                throw new InvalidOperationException("A read stream has already been adopted for this file.");
            }

            _stream = stream;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _stream?.Dispose();
            }
            finally
            {
                _stream = null;
                _handle?.Dispose();
            }
        }
    }
}
