using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Windows Capture Run OS lock backend. Each call is one non-waiting,
    /// single-attempt acquisition through real file handles; there is no retry,
    /// sleep, process-local registry, or named-mutex fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock file is opened or created with <c>FILE_SHARE_NONE</c>, so the
    /// live OS handle is the only ownership evidence — never the lock file's
    /// existence or content. An ordinary sharing violation is the only result
    /// converted to <c>false</c> with a null handle; unsafe paths, reparse
    /// points, I/O failures, and unsupported platforms throw. The lock file
    /// persists and is never deleted, truncated, or rewritten.
    /// </para>
    /// <para>
    /// The trusted base directory is opened no-follow and its canonical
    /// identity verified. The <c>.locks</c> directory and the lock file are
    /// then opened or created relative to the verified handles, so a parent
    /// directory swapped for a junction or symbolic link cannot redirect the
    /// acquisition. The returned handle retains the <c>.locks</c> and base
    /// directory handles without delete sharing to pin the namespace, and
    /// closes them in file → <c>.locks</c> → base order. On any exception or
    /// contention during acquisition, every acquired handle is closed and no
    /// partial ownership is returned.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunLockOsBackend : ICaptureRunLockBackend
    {
        private const uint FileGenericRead = 0x00120089u;
        private const uint FileGenericWrite = 0x00120116u;
        private const uint FileTraverse = 0x00000020u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint OpenExisting = 3u;
        private const uint FileOpenIfDisposition = 3u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const uint FileDirectoryFile = 0x00000001u;
        private const uint FileNonDirectoryFile = 0x00000040u;
        private const uint FileSynchronousIoNonAlert = 0x00000020u;
        private const uint ObjCaseInsensitive = 0x00000040u;
        private const int StatusSuccess = 0;
        private const int StatusSharingViolation = unchecked((int)0xC0000043);

        internal CaptureRunLockOsBackend()
        {
        }

        internal static CaptureRunLockOsBackend Create()
        {
            return new CaptureRunLockOsBackend();
        }

        public bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle)
        {
            handle = null;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException(
                    "Capture Run OS lock backend is only supported on Windows.");
            }

            if (absoluteLockPath == null)
            {
                throw new ArgumentNullException(nameof(absoluteLockPath));
            }

            string fullPath = Path.GetFullPath(absoluteLockPath);

            string locksDirectoryPath = Path.GetDirectoryName(fullPath);
            string fileName = Path.GetFileName(fullPath);

            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException("Lock path must name a lock file.", nameof(absoluteLockPath));
            }

            if (string.IsNullOrEmpty(locksDirectoryPath)
                || !string.Equals(Path.GetFileName(locksDirectoryPath), ".locks", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Lock path must be a direct entry of a .locks directory.",
                    nameof(absoluteLockPath));
            }

            string baseDirectoryPath = Path.GetDirectoryName(locksDirectoryPath);
            if (string.IsNullOrEmpty(baseDirectoryPath))
            {
                throw new ArgumentException(
                    "Lock path must be rooted inside a trusted base directory.",
                    nameof(absoluteLockPath));
            }

            SafeFileHandle baseHandle = OpenBaseDirectory(baseDirectoryPath);

            SafeFileHandle locksHandle = null;
            SafeFileHandle fileHandle = null;
            try
            {
                locksHandle = OpenLocksDirectoryRelative(baseHandle, baseDirectoryPath);

                bool acquired = TryOpenLockFileRelative(locksHandle, fileName, out fileHandle);
                if (!acquired)
                {
                    // Ordinary contention: release the namespace pins and
                    // report false with a null handle, leaving no partial
                    // ownership with the caller.
                    locksHandle.Dispose();
                    locksHandle = null;
                    baseHandle.Dispose();
                    return false;
                }

                handle = new CaptureRunLockOsHandle(fullPath, fileHandle, locksHandle, baseHandle);
                fileHandle = null;
                locksHandle = null;
                baseHandle = null;
                return true;
            }
            catch
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
                throw;
            }
        }

        private static SafeFileHandle OpenBaseDirectory(string baseDirectoryPath)
        {
            SafeFileHandle handle = CreateFileW(
                baseDirectoryPath,
                FileGenericRead | FileGenericWrite | FileTraverse,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    "Failed to open the trusted base directory (win32 error " + error + ").");
            }

            try
            {
                VerifyDirectoryHandle(handle, baseDirectoryPath, "trusted base directory");
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static SafeFileHandle OpenLocksDirectoryRelative(SafeFileHandle baseHandle, string baseDirectoryPath)
        {
            int status = NtCreateFileRelative(
                baseHandle,
                ".locks",
                FileGenericRead | FileGenericWrite | FileTraverse,
                FileShareRead | FileShareWrite,
                FileOpenIfDisposition,
                FileDirectoryFile | FileFlagOpenReparsePoint | FileSynchronousIoNonAlert,
                out SafeFileHandle handle);

            if (status != StatusSuccess)
            {
                throw new IOException(
                    "Failed to open or create the .locks directory (NTSTATUS 0x" + status.ToString("X8") + ").");
            }

            try
            {
                VerifyDirectoryHandle(
                    handle,
                    Path.Combine(baseDirectoryPath, ".locks"),
                    ".locks directory");
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static bool TryOpenLockFileRelative(
            SafeFileHandle locksHandle,
            string fileName,
            out SafeFileHandle fileHandle)
        {
            int status = NtCreateFileRelative(
                locksHandle,
                fileName,
                FileGenericRead,
                0,
                FileOpenIfDisposition,
                FileNonDirectoryFile | FileFlagOpenReparsePoint | FileSynchronousIoNonAlert,
                out fileHandle);

            if (status == StatusSuccess)
            {
                try
                {
                    VerifyRegularFileHandle(fileHandle, fileName);
                    return true;
                }
                catch
                {
                    fileHandle.Dispose();
                    fileHandle = null;
                    throw;
                }
            }

            fileHandle = null;

            if (status == StatusSharingViolation)
            {
                return false;
            }

            throw new IOException(
                "Failed to open or create the lock file (NTSTATUS 0x" + status.ToString("X8") + ").");
        }

        private static void VerifyDirectoryHandle(SafeFileHandle handle, string expectedPath, string label)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                throw new IOException("Failed to inspect the " + label + ".");
            }

            if ((information.FileAttributes & FileAttributeDirectory) == 0)
            {
                throw new IOException("The " + label + " is not a directory.");
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("The " + label + " is a reparse point.");
            }

            string canonicalPath = GetCanonicalPath(handle);
            string expected = "\\\\?\\" + expectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (canonicalPath == null
                || !string.Equals(canonicalPath, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The " + label + " identity does not match the expected path.");
            }
        }

        private static void VerifyRegularFileHandle(SafeFileHandle handle, string name)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                throw new IOException("Failed to inspect the lock file.");
            }

            if ((information.FileAttributes & FileAttributeDirectory) != 0)
            {
                throw new IOException("The lock file path is a directory.");
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("The lock file path is a reparse point.");
            }
        }

        private static int NtCreateFileRelative(
            SafeFileHandle rootDirectory,
            string name,
            uint desiredAccess,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            out SafeFileHandle handle)
        {
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr nameStringPtr = IntPtr.Zero;
            handle = null;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(name);

                UnicodeString nameString = new UnicodeString
                {
                    Length = (ushort)(name.Length * sizeof(char)),
                    MaximumLength = (ushort)((name.Length + 1) * sizeof(char)),
                    Buffer = nameBuffer
                };

                nameStringPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(nameString, nameStringPtr, false);

                ObjectAttributes attributes = new ObjectAttributes
                {
                    Length = (uint)Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = rootDirectory.DangerousGetHandle(),
                    ObjectName = nameStringPtr,
                    Attributes = ObjCaseInsensitive,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };

                long allocationSize = 0;
                int status = NtCreateFile(
                    out IntPtr rawHandle,
                    desiredAccess,
                    ref attributes,
                    out IoStatusBlock ioStatusBlock,
                    ref allocationSize,
                    FileAttributeNormal,
                    shareAccess,
                    createDisposition,
                    createOptions,
                    IntPtr.Zero,
                    0);

                if (status == StatusSuccess && rawHandle != IntPtr.Zero)
                {
                    handle = new SafeFileHandle(rawHandle, true);
                }

                return status;
            }
            finally
            {
                if (nameStringPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nameStringPtr);
                }

                if (nameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }
        }

        private static string GetCanonicalPath(SafeFileHandle handle)
        {
            StringBuilder builder = new StringBuilder(4096);
            uint required = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            if (required == 0)
            {
                return null;
            }

            if (required > builder.Capacity)
            {
                builder = new StringBuilder((int)required + 1);
                required = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
                if (required == 0 || required > builder.Capacity)
                {
                    return null;
                }
            }

            return builder.ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public uint Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public int Status;
            public IntPtr Information;
        }

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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtCreateFile(
            out IntPtr fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            ref long allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);
    }
}
