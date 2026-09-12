using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Creates one brand-new Capture Run root directory on the real
    /// filesystem, verifies it by handle, and issues the receipt only when
    /// every check has passed. It creates nothing inside the root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run root is created with <c>CreateDirectoryW</c> so an existing
    /// directory fails rather than being adopted, and is then opened
    /// no-follow: a reparse point, symbolic link, or junction anywhere on the
    /// path it was created through is refused instead of followed, and the
    /// identity that is verified afterwards is the handle's, not the path
    /// string's. The final path behind that handle must still name the run
    /// root the operation asked for, and the directory must be empty.
    /// </para>
    /// <para>
    /// Nothing inside the root is provisioned here - no <c>chunks</c>
    /// directory, no marker, no lock file: the chunk file session creates what
    /// it needs, and the marker writer writes the markers. Nothing is deleted,
    /// repaired, retried, renamed, or reused, and nothing about the operation,
    /// layout, or path is kept after the call: only the returned receipt holds
    /// the operation.
    /// </para>
    /// <para>
    /// A check that fails after the directory was created leaves an empty
    /// directory behind. That is the contract: an empty root is what a later
    /// recovery pass expects to find, and guessing at a delete here would be
    /// acting on a filesystem this attempt no longer understands.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunRootOsProvisioner : ICaptureRunRootProvisioner
    {
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint FileShareDelete = 0x00000004u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const uint FileNameNormalized = 0x00000000u;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorPathNotFound = 3;

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        internal static CaptureRunRootOsProvisioner Create()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture Run root provisioning requires Windows no-follow directory handles.");
            }

            return new CaptureRunRootOsProvisioner();
        }

        private CaptureRunRootOsProvisioner()
        {
        }

        /// <summary>
        /// One synchronous attempt: create the exact run root, verify it by
        /// handle, and issue the receipt.
        /// </summary>
        public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture Run root provisioning requires Windows no-follow directory handles.");
            }

            string runRoot = operation.RunRoot;

            // The trusted base root was already trusted by the caller, and the
            // operation guarantees the run root sits inside it at a segment
            // boundary, so neither is re-derived, re-normalized, or re-checked
            // as a string here.
            if (!CreateDirectoryW(runRoot, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorAlreadyExists)
                {
                    // An existing run root is never adopted: this attempt has
                    // no way to know whose it is.
                    throw new IOException(
                        "The Capture Run root already exists; an existing root is never provisioned.");
                }

                if (error == ErrorPathNotFound)
                {
                    throw new DirectoryNotFoundException(
                        "The Capture Run root's parent does not exist (win32 error " + error + ").");
                }

                throw new IOException(
                    "The Capture Run root could not be created (win32 error " + error + ").");
            }

            // From here the directory exists. Everything below only observes
            // it, and a failure deliberately leaves it empty rather than
            // guessing at a delete.
            using (SafeFileHandle handle = OpenDirectoryNoFollow(runRoot))
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The created Capture Run root could not be opened without following links (win32 error "
                        + error + ").");
                }

                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The created Capture Run root's identity could not be read (win32 error "
                        + error + ").");
                }

                if ((information.FileAttributes & FileAttributeDirectory) == 0)
                {
                    throw new IOException("The created Capture Run root is not a directory.");
                }

                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new IOException(
                        "The created Capture Run root is a reparse point; it is never followed.");
                }

                // What the handle really is, compared with what was asked for.
                string finalPath = ReadFinalPath(handle);
                if (!PathsAreTheSameEntry(finalPath, runRoot))
                {
                    throw new IOException(
                        "The created Capture Run root resolves to a different location than the operation named.");
                }

                if (!IsDirectoryEmpty(finalPath))
                {
                    throw new IOException("The created Capture Run root is not empty.");
                }
            }

            return new CaptureRunRootProvisionReceipt(this, operation);
        }

        private static SafeFileHandle OpenDirectoryNoFollow(string path)
        {
            return CreateFileW(
                path,
                0u,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }

        private static string ReadFinalPath(SafeFileHandle handle)
        {
            uint required = GetFinalPathNameByHandle(handle, null, 0u, FileNameNormalized);
            if (required == 0u)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    "The created Capture Run root's final path could not be read (win32 error "
                    + error + ").");
            }

            char[] buffer = new char[required];
            uint written = GetFinalPathNameByHandle(handle, buffer, required, FileNameNormalized);
            if (written == 0u || written >= required)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    "The created Capture Run root's final path could not be read (win32 error "
                    + error + ").");
            }

            return new string(buffer, 0, (int)written);
        }

        /// <summary>
        /// Compares a handle's normalized final path with the path that was
        /// asked for, allowing only for the extended-length prefix the OS adds
        /// and the trailing separator a root may carry.
        /// </summary>
        private static bool PathsAreTheSameEntry(string finalPath, string requestedPath)
        {
            return string.Equals(
                Normalize(finalPath), Normalize(requestedPath), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            string trimmed = path;
            if (trimmed.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(4);
            }

            return trimmed.TrimEnd('\\');
        }

        /// <summary>
        /// Enumerates through the handle's own resolved path, so a parent
        /// swapped after verification cannot redirect what is inspected.
        /// </summary>
        private static bool IsDirectoryEmpty(string canonicalPath)
        {
            IntPtr find = FindFirstFileW(canonicalPath + "\\*", out Win32FindData data);
            if (find == InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    "The created Capture Run root could not be enumerated (win32 error " + error + ").");
            }

            try
            {
                do
                {
                    if (data.FileName == "." || data.FileName == "..")
                    {
                        continue;
                    }

                    return false;
                }
                while (FindNextFileW(find, out data));
            }
            finally
            {
                FindClose(find);
            }

            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool CreateDirectoryW(
            string lpPathName, IntPtr lpSecurityAttributes);

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
            SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            [Out] char[] lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(
            string lpFileName, out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(
            IntPtr hFindFile, out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }
    }
}
