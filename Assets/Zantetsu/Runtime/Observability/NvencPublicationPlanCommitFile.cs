using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Handle-bound NVENC publication plan commit file: a no-follow-verified
    /// file identity plus its read/write stream. The non-overwriting rename is
    /// performed through this handle, so a later path or parent-directory swap
    /// can never redirect it to a different file. It owns and disposes both its
    /// stream and its handle exactly once.
    /// </summary>
    internal sealed class NvencPublicationPlanCommitFile : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly Stream _stream;
        private bool _disposed;

        internal NvencPublicationPlanCommitFile(SafeFileHandle handle, Stream stream)
        {
            _handle = handle;
            _stream = stream;
        }

        internal SafeFileHandle Handle => _handle;

        internal Stream Stream => _stream;

        internal bool IsDisposed => _disposed;

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
                _handle?.Dispose();
            }
        }
    }
}
