using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Creates one brand-new Capture Run root directory on the real
    /// filesystem, relative to a parent it has already verified, and issues
    /// the receipt only when every check has passed. It creates nothing inside
    /// the root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent is opened first and is accepted only when it is a directory,
    /// is not itself a reparse point, and still resolves to the path the run
    /// root's parent was named as. That last comparison is what catches
    /// indirection: the open does not follow a link at the parent's own name,
    /// but the OS does resolve the ancestors above it, so a junction anywhere
    /// on the way makes the handle's real path differ from the path that was
    /// asked for - and the attempt is refused there, before anything is
    /// created.
    /// </para>
    /// <para>
    /// The run root itself is then created <em>relative to that verified
    /// handle</em> with a single leaf name, never through a path, so it can
    /// never come into existence behind a link. An existing run root collides
    /// and fails; it is never adopted.
    /// </para>
    /// <para>
    /// The created directory is then checked through its own handle: a
    /// directory, not a reparse point, resolving to the run root that was
    /// asked for, and empty - enumerated from that handle, so nothing between
    /// the check and the path can be substituted, and an enumeration that
    /// stops for any reason other than running out of entries is a failure
    /// rather than an empty answer.
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
        private const uint FileListDirectory = 0x00000001u;
        private const uint FileReadAttributes = 0x00000080u;
        private const uint Synchronize = 0x00100000u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint FileShareDelete = 0x00000004u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const uint FileNameNormalized = 0x00000000u;
        private const uint FileCreateDisposition = 2u;
        private const uint FileDirectoryFile = 0x00000001u;
        private const uint FileOpenReparsePointOption = 0x00200000u;
        private const uint FileSynchronousIoNonAlert = 0x00000020u;
        private const uint ObjCaseInsensitive = 0x00000040u;
        private const int StatusSuccess = 0;
        private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
        private const int ErrorNoMoreFiles = 18;
        private const int FileIdBothDirectoryInfoClass = 10;
        private const int FileIdBothDirectoryRestartInfoClass = 11;

        internal static CaptureRunRootOsProvisioner Create()
        {
            RequireWindows();
            return new CaptureRunRootOsProvisioner();
        }

        private CaptureRunRootOsProvisioner()
        {
        }

        /// <summary>
        /// One synchronous attempt: verify the parent, create the exact run
        /// root relative to it, verify what was created, and issue the
        /// receipt.
        /// </summary>
        public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            RequireWindows();

            // The trusted base root was already trusted by the caller, and the
            // operation guarantees the run root sits inside it at a segment
            // boundary, so neither is re-derived or re-checked as a string
            // here. What is split out is only the leaf to create and the
            // parent to create it under.
            string runRoot = operation.RunRoot;
            string parentPath = Path.GetDirectoryName(runRoot);
            string leafName = Path.GetFileName(runRoot);

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(leafName))
            {
                throw new ArgumentException(
                    "The provision operation must name a Run root inside a parent directory.",
                    nameof(operation));
            }

            using (SafeFileHandle parent = OpenDirectoryNoFollow(parentPath))
            {
                if (parent.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The Capture Run root's parent could not be opened without following links (win32 error "
                        + error + ").");
                }

                RequireVerifiedDirectory(
                    parent, parentPath, "The Capture Run root's parent");

                // Created relative to the verified parent: one leaf name,
                // no path resolved again, so the run root can never come into
                // existence behind a link.
                using (SafeFileHandle created = CreateDirectoryRelative(parent, leafName))
                {
                    RequireVerifiedDirectory(created, runRoot, "The created Capture Run root");

                    if (!IsDirectoryEmpty(created))
                    {
                        throw new IOException("The created Capture Run root is not empty.");
                    }
                }
            }

            return new CaptureRunRootProvisionReceipt(this, operation);
        }

        private static void RequireWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture Run root provisioning requires Windows no-follow directory handles.");
            }
        }

        /// <summary>
        /// Accepts a handle only as a directory that is not a reparse point
        /// and still resolves to the path it was named as. The path comparison
        /// is what detects indirection introduced by an ancestor, which the
        /// open itself does not prevent.
        /// </summary>
        private static void RequireVerifiedDirectory(
            SafeFileHandle handle, string expectedPath, string what)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    what + "'s identity could not be read (win32 error " + error + ").");
            }

            if ((information.FileAttributes & FileAttributeDirectory) == 0)
            {
                throw new IOException(what + " is not a directory.");
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException(what + " is a reparse point; it is never followed.");
            }

            string finalPath = ReadFinalPath(handle, what);
            if (!PathsAreTheSameEntry(finalPath, expectedPath))
            {
                throw new IOException(
                    what + " resolves to a different location than the operation named.");
            }
        }

        private static SafeFileHandle OpenDirectoryNoFollow(string path)
        {
            return CreateFileW(
                path,
                FileReadAttributes | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
        }

        /// <summary>
        /// Creates one new directory named by a single leaf, resolved by the
        /// kernel against the given parent handle rather than against any
        /// path, and hands back its own handle.
        /// </summary>
        private static SafeFileHandle CreateDirectoryRelative(SafeFileHandle parent, string leafName)
        {
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr nameStringPtr = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(leafName);

                UnicodeString nameString = new UnicodeString
                {
                    Length = (ushort)(leafName.Length * sizeof(char)),
                    MaximumLength = (ushort)((leafName.Length + 1) * sizeof(char)),
                    Buffer = nameBuffer
                };

                nameStringPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(nameString, nameStringPtr, false);

                ObjectAttributes attributes = new ObjectAttributes
                {
                    Length = (uint)Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = nameStringPtr,
                    Attributes = ObjCaseInsensitive,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };

                long allocationSize = 0;
                int status = NtCreateFile(
                    out IntPtr rawHandle,
                    FileListDirectory | FileReadAttributes | Synchronize,
                    ref attributes,
                    out IoStatusBlock ioStatusBlock,
                    ref allocationSize,
                    FileAttributeNormal,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileCreateDisposition,
                    FileDirectoryFile | FileOpenReparsePointOption | FileSynchronousIoNonAlert,
                    IntPtr.Zero,
                    0);

                if (status == StatusObjectNameCollision)
                {
                    // An existing run root is never adopted: this attempt has
                    // no way to know whose it is.
                    throw new IOException(
                        "The Capture Run root already exists; an existing root is never provisioned.");
                }

                if (status != StatusSuccess || rawHandle == IntPtr.Zero)
                {
                    throw new IOException(
                        "The Capture Run root could not be created under its verified parent (NTSTATUS 0x"
                        + status.ToString("X8") + ").");
                }

                return new SafeFileHandle(rawHandle, true);
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

        private static string ReadFinalPath(SafeFileHandle handle, string what)
        {
            uint required = GetFinalPathNameByHandle(handle, null, 0u, FileNameNormalized);
            if (required == 0u)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    what + "'s final path could not be read (win32 error " + error + ").");
            }

            char[] buffer = new char[required];
            uint written = GetFinalPathNameByHandle(handle, buffer, required, FileNameNormalized);
            if (written == 0u || written >= required)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    what + "'s final path could not be read (win32 error " + error + ").");
            }

            return new string(buffer, 0, (int)written);
        }

        /// <summary>
        /// Compares a handle's normalized final path with the path that was
        /// asked for, allowing only for the extended-length prefix the OS adds
        /// and a trailing separator.
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
        /// Enumerates the directory through its own handle. An enumeration
        /// that ends for any reason other than running out of entries is a
        /// failure, never an empty answer.
        /// </summary>
        private static bool IsDirectoryEmpty(SafeFileHandle directory)
        {
            const int BufferSize = 4096;
            IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
            try
            {
                int informationClass = FileIdBothDirectoryRestartInfoClass;
                while (true)
                {
                    if (!GetFileInformationByHandleEx(
                        directory, informationClass, buffer, (uint)BufferSize))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreFiles)
                        {
                            return true;
                        }

                        throw new IOException(
                            "The created Capture Run root could not be enumerated from its handle (win32 error "
                            + error + ").");
                    }

                    informationClass = FileIdBothDirectoryInfoClass;

                    // FILE_ID_BOTH_DIR_INFO: NextEntryOffset at 0,
                    // FileNameLength at 60, and the WCHAR name at 104.
                    int offset = 0;
                    while (true)
                    {
                        IntPtr entry = new IntPtr(buffer.ToInt64() + offset);
                        int nextEntryOffset = Marshal.ReadInt32(entry, 0);
                        int nameLength = Marshal.ReadInt32(entry, 60);
                        string name = Marshal.PtrToStringUni(
                            new IntPtr(entry.ToInt64() + 104), nameLength / sizeof(char));

                        if (name != "." && name != "..")
                        {
                            return false;
                        }

                        if (nextEntryOffset == 0)
                        {
                            break;
                        }

                        offset += nextEntryOffset;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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
            SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle hFile,
            int fileInformationClass,
            IntPtr lpFileInformation,
            uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            [Out] char[] lpszFilePath,
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

        [StructLayout(LayoutKind.Sequential)]
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
    }
}
