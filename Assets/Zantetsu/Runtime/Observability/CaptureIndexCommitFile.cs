using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>Outcome of a no-follow Capture Index file open.</summary>
    internal enum CaptureIndexFileOpenStatus
    {
        Opened = 0,
        Absent = 1,
        IoFailure = 2,
        InvalidFileKind = 3,
        Unsupported = 4,
        EscapesRoot = 5
    }

    /// <summary>
    /// Result of a no-follow Capture Index file open: either an opened
    /// handle-bound file or a terminal status.
    /// </summary>
    internal sealed class CaptureIndexFileOpen
    {
        private CaptureIndexFileOpen(CaptureIndexFileOpenStatus status, CaptureIndexCommitFile file)
        {
            Status = status;
            File = file;
        }

        internal CaptureIndexFileOpenStatus Status { get; }

        internal CaptureIndexCommitFile File { get; }

        internal static CaptureIndexFileOpen Of(CaptureIndexFileOpenStatus status)
        {
            return new CaptureIndexFileOpen(status, null);
        }

        internal static CaptureIndexFileOpen Opened(CaptureIndexCommitFile file)
        {
            return new CaptureIndexFileOpen(CaptureIndexFileOpenStatus.Opened, file);
        }
    }

    /// <summary>
    /// Handle-bound Capture Index file: a no-follow-verified file identity plus
    /// its read/write stream. All flush, rename, and delete operations are
    /// performed through this handle, so a later path or parent-directory swap
    /// can never redirect them to a different file.
    /// </summary>
    internal sealed class CaptureIndexCommitFile : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly Stream _stream;

        internal CaptureIndexCommitFile(SafeFileHandle handle, Stream stream)
        {
            _handle = handle;
            _stream = stream;
        }

        internal SafeFileHandle Handle => _handle;

        internal Stream Stream => _stream;

        internal long Length => _stream.Length;

        public void Dispose()
        {
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
