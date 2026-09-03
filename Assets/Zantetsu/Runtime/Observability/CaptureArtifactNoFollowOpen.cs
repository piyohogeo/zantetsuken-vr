using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>Outcome of a platform-safe no-follow open attempt.</summary>
    internal enum CaptureArtifactNoFollowOpenStatus
    {
        Opened = 0,
        Absent = 1,
        IoFailure = 2,
        InvalidFileKind = 3,
        Unsupported = 4
    }

    /// <summary>Result of a no-follow open: either an opened stream or a status.</summary>
    internal readonly struct CaptureArtifactNoFollowOpenResult
    {
        private readonly FileStream _stream;
        private readonly SafeFileHandle _handle;

        internal CaptureArtifactNoFollowOpenStatus Status { get; }
        internal FileStream Stream => _stream;

        private CaptureArtifactNoFollowOpenResult(
            CaptureArtifactNoFollowOpenStatus status,
            FileStream stream,
            SafeFileHandle handle)
        {
            Status = status;
            _stream = stream;
            _handle = handle;
        }

        internal static CaptureArtifactNoFollowOpenResult Of(CaptureArtifactNoFollowOpenStatus status)
        {
            return new CaptureArtifactNoFollowOpenResult(status, null, null);
        }

        internal static CaptureArtifactNoFollowOpenResult Opened(FileStream stream, SafeFileHandle handle)
        {
            return new CaptureArtifactNoFollowOpenResult(CaptureArtifactNoFollowOpenStatus.Opened, stream, handle);
        }

        /// <summary>
        /// Releases the opened stream and the underlying handle. The handle is
        /// disposed explicitly because some runtimes do not take ownership of a
        /// caller-supplied SafeFileHandle when wrapping it in a FileStream, and
        /// disposing both is safe because SafeHandle disposal is idempotent.
        /// </summary>
        internal void Close()
        {
            _stream?.Dispose();
            _handle?.Dispose();
        }
    }

    /// <summary>
    /// Platform-safe no-follow file open for artifact verification. On Windows
    /// the exact path is opened with <c>FILE_FLAG_OPEN_REPARSE_POINT</c> and its
    /// kind is derived from the opened handle, so a regular file swapped for a
    /// reparse point between inspection and open is never followed; the opened
    /// handle is the single source of truth. On platforms without a no-follow
    /// open the attempt reports <see cref="CaptureArtifactNoFollowOpenStatus.Unsupported"/>
    /// so the caller can fail closed.
    /// </summary>
    internal static class CaptureArtifactNoFollowOpen
    {
        private const uint GenericRead = 0x80000000u;
        private const uint FileShareRead = 0x00000001u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        internal static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        internal static CaptureArtifactNoFollowOpenResult TryOpen(string path)
        {
            if (!IsSupported)
            {
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.Unsupported);
            }

            SafeFileHandle handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return CaptureArtifactNoFollowOpenResult.Of(
                    error == ErrorFileNotFound || error == ErrorPathNotFound
                        ? CaptureArtifactNoFollowOpenStatus.Absent
                        : CaptureArtifactNoFollowOpenStatus.IoFailure);
            }

            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                handle.Dispose();
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.IoFailure);
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0
                || (information.FileAttributes & FileAttributeDirectory) != 0)
            {
                handle.Dispose();
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.InvalidFileKind);
            }

            FileStream stream;
            try
            {
                stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.IoFailure);
            }

            return CaptureArtifactNoFollowOpenResult.Opened(stream, handle);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
