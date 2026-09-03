using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        Unsupported = 4,
        EscapesRoot = 5
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

    /// <summary>
    /// Platform-safe no-follow file open for artifact verification. On Windows
    /// the path is opened relative to a run root with
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, its kind is derived from the opened
    /// handle, and the handle's canonical path is verified to stay inside the
    /// run root, so neither a final-component reparse point nor a parent
    /// directory swapped for a junction or symbolic link can be followed. On
    /// platforms without a no-follow open the attempt reports
    /// <see cref="CaptureArtifactNoFollowOpenStatus.Unsupported"/> so the
    /// caller can fail closed.
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

        private static bool? _isSupportedOverride;

        internal static bool IsSupported => _isSupportedOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Test-only seam for forcing the platform capability check. Pass
        /// <c>null</c> to restore platform detection.
        /// </summary>
        internal static void OverrideIsSupported(bool? value)
        {
            _isSupportedOverride = value;
        }

        internal static CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
        {
            if (!IsSupported)
            {
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.Unsupported);
            }

            if (root == null) throw new ArgumentNullException(nameof(root));
            if (relativePath == null) throw new ArgumentNullException(nameof(relativePath));

            string normalizedRoot = Path.GetFullPath(root);
            string fullPath = Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            SafeFileHandle handle = CreateFileW(
                fullPath,
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

            // The opened handle is the single source of truth. Resolve its
            // canonical path and require it to stay inside the run root, so a
            // parent directory swapped for a junction or symbolic link cannot
            // make verification read a file outside the root.
            if (!IsWithinRoot(handle, normalizedRoot))
            {
                handle.Dispose();
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.EscapesRoot);
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

        private static bool IsWithinRoot(SafeFileHandle handle, string root)
        {
            StringBuilder builder = new StringBuilder(4096);
            uint required = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            if (required == 0) return false; // fail closed
            if (required > builder.Capacity)
            {
                builder = new StringBuilder((int)required + 1);
                required = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
                if (required == 0 || required > builder.Capacity) return false;
            }

            string finalPath = builder.ToString();
            string expectedRoot = "\\\\?\\" + root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return finalPath.StartsWith(expectedRoot + "\\", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finalPath, expectedRoot, StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

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
