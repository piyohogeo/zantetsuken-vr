using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-blocking, exclusive, no-follow Windows OS lock backend. One
    /// acquisition attempt per call; never waits, retries, sleeps, or falls
    /// back to another path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock file is opened with <c>FILE_FLAG_OPEN_REPARSE_POINT</c> and no
    /// sharing (<c>dwShareMode = 0</c>, equivalent to <c>FileShare.None</c>).
    /// A sharing violation is the only ordinary contention result and maps to
    /// <c>false</c> with a null handle; every other OS, path, or permission
    /// error is surfaced as an exception. The lock file's existence is never
    /// treated as ownership evidence, so the fixed file may remain after the
    /// handle is disposed.
    /// </para>
    /// <para>
    /// After opening, the backend confirms the handle is not a reparse point
    /// and that its final resolved path matches the expected absolute path
    /// before transferring ownership.
    /// </para>
    /// <para>
    /// The backend owns and holds no handle, is not disposable, and keeps no
    /// mutable static state. It creates only the <c>.locks</c> directory when
    /// absent and never creates, enumerates, or mutates the per-Run root.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunWindowsLockBackend : ICaptureRunLockBackend
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint OpenAlways = 4;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileAttributeReparsePoint = 0x400;
        private const int ErrorSharingViolation = 32;
        private const uint FileNameNormalized = 0x0;

        public bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle)
        {
            handle = null;

            if (absoluteLockPath == null)
            {
                throw new ArgumentNullException(nameof(absoluteLockPath));
            }

            if (absoluteLockPath.Length == 0 || string.IsNullOrWhiteSpace(absoluteLockPath))
            {
                throw new ArgumentException("Lock path must not be empty or whitespace.", nameof(absoluteLockPath));
            }

            if (!Path.IsPathFullyQualified(absoluteLockPath))
            {
                throw new ArgumentException("Lock path must be fully qualified.", nameof(absoluteLockPath));
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("The Windows lock backend requires Windows.");
            }

            string baseDirectory;
            string lockDirectory;
            if (!TryParseLockPath(absoluteLockPath, out baseDirectory, out lockDirectory))
            {
                throw new ArgumentException("Lock path must be a fixed .locks/run-{id}.lock entry under a trusted base root.", nameof(absoluteLockPath));
            }

            string expectedBasePath = Path.GetFullPath(baseDirectory);
            string expectedLocksPath = Path.GetFullPath(lockDirectory);

            // base と .locks を no-follow の directory handle として開き、delete
            // 共有を許可しない。lock file 取得完了まで保持するため、この間に中間
            // directory が junction へ交換されることはありません。
            SafeFileHandle baseHandle = OpenDirectoryHandle(baseDirectory);
            SafeFileHandle locksHandle = null;
            SafeFileHandle fileHandle = null;
            try
            {
                ValidateDirectoryHandle(baseHandle, expectedBasePath);

                DirectoryInfo locksDir = new DirectoryInfo(lockDirectory);
                if (!locksDir.Exists)
                {
                    Directory.CreateDirectory(lockDirectory);
                }

                locksHandle = OpenDirectoryHandle(lockDirectory);
                ValidateDirectoryHandle(locksHandle, expectedLocksPath);

                bool sharingViolation;
                fileHandle = OpenLockFile(absoluteLockPath, out sharingViolation);
                if (sharingViolation)
                {
                    return false;
                }

                ValidateFileHandle(fileHandle, Path.GetFullPath(absoluteLockPath));

                handle = new CaptureRunWindowsLockHandle(Path.GetFullPath(absoluteLockPath), fileHandle);
                fileHandle = null;
                return true;
            }
            finally
            {
                if (fileHandle != null)
                {
                    fileHandle.Dispose();
                }

                if (locksHandle != null)
                {
                    locksHandle.Dispose();
                }

                baseHandle.Dispose();
            }
        }

        private static SafeFileHandle OpenDirectoryHandle(string directoryPath)
        {
            SafeFileHandle handle = CreateFileW(
                directoryPath,
                GenericRead,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("Failed to open directory.", new Win32Exception(error));
            }

            return handle;
        }

        private static void ValidateDirectoryHandle(SafeFileHandle handle, string expectedPath)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Failed to read directory information.", new Win32Exception(error));
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("Directory must not be a reparse point.");
            }

            string finalPath = GetFinalPath(handle);
            if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Directory resolved to an unexpected path.");
            }
        }

        private static SafeFileHandle OpenLockFile(string absoluteLockPath, out bool sharingViolation)
        {
            sharingViolation = false;
            SafeFileHandle fileHandle = CreateFileW(
                absoluteLockPath,
                GenericRead | GenericWrite,
                0,
                IntPtr.Zero,
                OpenAlways,
                FileAttributeNormal | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (fileHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                fileHandle.Dispose();
                if (error == ErrorSharingViolation)
                {
                    sharingViolation = true;
                    return null;
                }

                throw new IOException("Failed to open lock file.", new Win32Exception(error));
            }

            return fileHandle;
        }

        private static void ValidateFileHandle(SafeFileHandle fileHandle, string expectedPath)
        {
            if (!GetFileInformationByHandle(fileHandle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Failed to read lock file information.", new Win32Exception(error));
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("Lock file must not be a reparse point.");
            }

            string finalPath = GetFinalPath(fileHandle);
            if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Lock file resolved to an unexpected path.");
            }
        }

        private static bool TryParseLockPath(string path, out string baseDirectory, out string lockDirectory)
        {
            baseDirectory = null;
            lockDirectory = null;

            string fileName = Path.GetFileName(path);
            long testRunId;
            if (!TryParseLockFileName(fileName, out testRunId))
            {
                return false;
            }

            lockDirectory = Path.GetDirectoryName(path);
            if (lockDirectory == null)
            {
                return false;
            }

            if (!string.Equals(Path.GetFileName(lockDirectory), ".locks", StringComparison.Ordinal))
            {
                return false;
            }

            baseDirectory = Path.GetDirectoryName(lockDirectory);
            return baseDirectory != null && Path.IsPathFullyQualified(baseDirectory);
        }

        private static bool TryParseLockFileName(string fileName, out long testRunId)
        {
            testRunId = 0;
            const string prefix = "run-";
            const string suffix = ".lock";

            if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            string digits = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
            if (digits.Length == 0 || digits[0] == '0')
            {
                return false;
            }

            foreach (char c in digits)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out testRunId))
            {
                return false;
            }

            return testRunId > 0;
        }

        private static string GetFinalPath(SafeFileHandle handle)
        {
            StringBuilder builder = new StringBuilder(512);
            uint length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, FileNameNormalized);
            if (length == 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Failed to resolve the final lock file path.", new Win32Exception(error));
            }

            if (length > builder.Capacity)
            {
                builder = new StringBuilder((int)length);
                uint result = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, FileNameNormalized);
                if (result == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("Failed to resolve the final lock file path.", new Win32Exception(error));
                }
            }

            string path = builder.ToString();
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                path = path.Substring(4);
            }

            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle hFile,
            [Out] StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

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
