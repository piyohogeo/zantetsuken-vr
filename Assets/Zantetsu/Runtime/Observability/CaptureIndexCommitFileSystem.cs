using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, per-committer filesystem collaborator for Capture Index
    /// commits. It pins the final run root directory to a no-follow-verified
    /// handle and performs every file operation — open, create, flush, rename,
    /// and delete — through file identities or that stable directory handle
    /// using <c>SetFileInformationByHandle</c>, so a path or parent-directory
    /// swap cannot redirect an operation after verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSupported"/> requires the no-follow open capability and
    /// <see cref="IsDirectoryFlushSupported"/> requires the directory flush
    /// capability; on other platforms both are false and the committer must
    /// refuse before any side effect. A flush is never faked: the handle open
    /// and <c>FlushFileBuffers</c> must both succeed or an
    /// <see cref="IOException"/> propagates.
    /// </para>
    /// <para>
    /// This type holds no file, stream, lease, or mutable state and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureIndexCommitFileSystem : ICaptureIndexCommitFileSystem, ICaptureCompleteCleanupFileSystem
    {
        private const uint GenericRead = 0x80000000u;
        private const uint GenericWrite = 0x40000000u;
        private const uint DeleteAccess = 0x00010000u;
        private const uint FileGenericRead = 0x00120089u;
        private const uint FileGenericWrite = 0x00120116u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint FileShareDelete = 0x00000004u;
        private const uint OpenExisting = 3u;
        private const uint FileCreateDisposition = 2u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const uint FileNonDirectoryFile = 0x00000040u;
        private const uint FileSynchronousIoNonAlert = 0x00000020u;
        private const uint ObjCaseInsensitive = 0x00000040u;
        private const int FileRenameInfoClass = 3;
        private const int FileDispositionInfoClass = 4;
        private const int StatusSuccess = 0;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        internal static CaptureIndexCommitFileSystem Create()
        {
            return new CaptureIndexCommitFileSystem();
        }

        public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public bool IsDirectoryFlushSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
        {
            if (absolutePath == null)
            {
                throw new ArgumentNullException(nameof(absolutePath));
            }

            if (!IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "No-follow directory open is not supported on this platform.");
            }

            string normalized = Path.GetFullPath(absolutePath);

            SafeFileHandle handle = CreateFileW(
                normalized,
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new IOException("Failed to open the final run root directory.");
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                {
                    throw new IOException("Failed to inspect the final run root directory.");
                }

                if ((information.FileAttributes & FileAttributeDirectory) == 0)
                {
                    throw new IOException("The final run root is not a directory.");
                }

                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new IOException("The final run root is a reparse point.");
                }

                string canonicalPath = GetCanonicalPath(handle);
                string expected = "\\\\?\\" + normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (canonicalPath == null
                    || !string.Equals(canonicalPath, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The final run root directory identity does not match the expected path.");
                }

                // Probe directory metadata flush capability on this exact
                // handle before any side effect. This confirms the filesystem
                // actually supports flushing directory metadata (not just the
                // OS), so an unsupported filesystem is rejected before
                // temporary creation or rename. Transient I/O failures later
                // are out of scope for this probe.
                if (!FlushFileBuffers(handle))
                {
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "Directory metadata flush is not available for the final run root.");
                }

                return new CaptureIndexCommitDirectory(handle, normalized, canonicalPath);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!IsSupported)
            {
                return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.Unsupported);
            }

            string fullPath = Path.Combine(directory.OriginalPath, name);

            SafeFileHandle handle = CreateFileW(
                fullPath,
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                return CaptureIndexFileOpen.Of(
                    error == ErrorFileNotFound || error == ErrorPathNotFound
                        ? CaptureIndexFileOpenStatus.Absent
                        : CaptureIndexFileOpenStatus.IoFailure);
            }

            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                handle.Dispose();
                return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.IoFailure);
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0
                || (information.FileAttributes & FileAttributeDirectory) != 0)
            {
                handle.Dispose();
                return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.InvalidFileKind);
            }

            string canonicalPath = GetCanonicalPath(handle);
            if (canonicalPath == null || !IsWithinDirectory(canonicalPath, directory.CanonicalPath))
            {
                handle.Dispose();
                return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.EscapesRoot);
            }

            FileStream stream;
            try
            {
                stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.IoFailure);
            }

            return CaptureIndexFileOpen.Opened(new CaptureIndexCommitFile(handle, stream));
        }

        public CaptureIndexCommitFile CreateNew(CaptureIndexCommitDirectory directory, string name)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "No-follow file creation is not supported on this platform.");
            }

            SafeFileHandle handle = CreateNewRelativeToDirectory(directory, name);
            try
            {
                // Defense-in-depth: the handle-relative create is bound to the
                // verified directory identity and cannot land outside the run
                // root. If the directory was renamed mid-commit and the resolved
                // path no longer matches, fail closed and remove the file.
                string canonicalPath = GetCanonicalPath(handle);
                if (canonicalPath == null || !IsWithinDirectory(canonicalPath, directory.CanonicalPath))
                {
                    try
                    {
                        DeleteByHandle(handle);
                    }
                    catch
                    {
                        // Deletion failure must not leak the handle; the
                        // IOException below is the reported failure.
                    }

                    throw new IOException(name + " was created outside the final run root.");
                }

                FileStream stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
                CaptureIndexCommitFile file = new CaptureIndexCommitFile(handle, stream);
                handle = null;
                return file;
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static SafeFileHandle CreateNewRelativeToDirectory(CaptureIndexCommitDirectory directory, string name)
        {
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr nameStringPtr = IntPtr.Zero;
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
                    RootDirectory = directory.Handle.DangerousGetHandle(),
                    ObjectName = nameStringPtr,
                    Attributes = ObjCaseInsensitive,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero
                };

                long allocationSize = 0;
                IoStatusBlock ioStatusBlock;
                int status = NtCreateFile(
                    out IntPtr rawHandle,
                    FileGenericRead | FileGenericWrite | DeleteAccess,
                    ref attributes,
                    out ioStatusBlock,
                    ref allocationSize,
                    FileAttributeNormal,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileCreateDisposition,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert,
                    IntPtr.Zero,
                    0);

                if (status != StatusSuccess || rawHandle == IntPtr.Zero)
                {
                    throw new IOException("Failed to create " + name + " (NTSTATUS 0x" + status.ToString("X8") + ").");
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

        public void FlushFileData(CaptureIndexCommitFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            file.Stream.Flush();
            if (!FlushFileBuffers(file.Handle))
            {
                throw new IOException("File data flush failed.");
            }
        }

        public void Rename(CaptureIndexCommitFile file, CaptureIndexCommitDirectory directory, string newName)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (newName == null)
            {
                throw new ArgumentNullException(nameof(newName));
            }

            // The source is the verified file identity (the handle), so a
            // swapped temporary path cannot make us rename a different file.
            // The destination is derived from the verified directory's resolved
            // canonical path (fixed at OpenDirectory time), not from a
            // re-resolved file path, so a transient canonical-path resolution
            // failure on a freshly created file cannot break the rename.
            string fileCanonicalPath = GetCanonicalPath(file.Handle);
            if (fileCanonicalPath == null
                || !IsWithinDirectory(fileCanonicalPath, directory.CanonicalPath))
            {
                throw new IOException("The temporary file is not inside the verified run root.");
            }

            string fullDestination = directory.CanonicalPath + "\\" + newName;

            // FILE_RENAME_INFO: union (4 bytes) + padding to HANDLE alignment,
            // HANDLE RootDirectory, DWORD FileNameLength, then WCHAR
            // FileName[] in bytes. RootDirectory stays NULL (zeroed) because a
            // fully qualified destination path is used.
            int nameBytes = checked(fullDestination.Length * sizeof(char));
            int headerSize = IntPtr.Size == 8 ? 20 : 12;
            int fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int totalSize = checked(headerSize + nameBytes);

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int i = 0; i < totalSize; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                // ReplaceIfExists = FALSE and RootDirectory = NULL (both zeroed).
                Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes);
                for (int i = 0; i < fullDestination.Length; i++)
                {
                    Marshal.WriteInt16(buffer, headerSize + i * 2, (short)fullDestination[i]);
                }

                if (!SetFileInformationByHandle(
                    file.Handle,
                    FileRenameInfoClass,
                    buffer,
                    (uint)totalSize))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("Atomic non-overwriting rename failed (win32 error " + error + ", name='" + newName + "').");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Delete(CaptureIndexCommitFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            DeleteByHandle(file.Handle);
        }

        public void FlushDirectory(CaptureIndexCommitDirectory directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (!IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Directory metadata flush is not supported on this platform.");
            }

            if (!FlushFileBuffers(directory.Handle))
            {
                throw new IOException("Directory metadata flush failed.");
            }
        }

        public void DeleteDirectory(CaptureIndexCommitDirectory directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (!IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Directory deletion is not supported on this platform.");
            }

            // Non-recursive, handle-bound delete: an empty directory is the
            // only thing that can be removed, and the verified directory
            // handle pins the exact identity being removed.
            FileDispositionInfo dispositionInfo = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(
                directory.Handle,
                FileDispositionInfoClass,
                ref dispositionInfo,
                (uint)Marshal.SizeOf(typeof(FileDispositionInfo))))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Directory deletion failed (win32 error " + error + ").");
            }
        }

        public bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            // Enumerate through the resolved canonical path (never the
            // user-supplied path), so a swapped parent junction cannot redirect
            // the enumeration. Deletion itself stays handle-bound.
            IntPtr find = FindFirstFileW(directory.CanonicalPath + "\\*", out Win32FindData data);
            if (find == InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Failed to enumerate the directory (win32 error " + error + ").");
            }

            try
            {
                do
                {
                    string name = data.FileName;
                    if (name != "." && name != "..")
                    {
                        return false;
                    }
                }
                while (FindNextFileW(find, out data));

                return true;
            }
            finally
            {
                FindClose(find);
            }
        }

        private static void DeleteByHandle(SafeFileHandle handle)
        {
            FileDispositionInfo dispositionInfo = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref dispositionInfo,
                (uint)Marshal.SizeOf(typeof(FileDispositionInfo))))
            {
                throw new IOException("File deletion failed.");
            }
        }

        private static bool IsWithinDirectory(string canonicalFile, string canonicalDirectory)
        {
            string prefix = canonicalDirectory.EndsWith("\\", StringComparison.Ordinal)
                ? canonicalDirectory
                : canonicalDirectory + "\\";
            return canonicalFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(
            string lpFileName,
            out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(
            IntPtr hFindFile,
            out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.I1)]
            public bool DeleteFile;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            IntPtr lpFileInformation,
            uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            ref FileDispositionInfo lpFileInformation,
            uint dwBufferSize);

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
