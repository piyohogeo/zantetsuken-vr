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
    internal sealed class CaptureIndexCommitFileSystem : ICaptureIndexCommitFileSystem
    {
        private const uint GenericRead = 0x80000000u;
        private const uint GenericWrite = 0x40000000u;
        private const uint DeleteAccess = 0x00010000u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint FileShareDelete = 0x00000004u;
        private const uint CreateNewDisposition = 1u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const int FileRenameInfoClass = 3;
        private const int FileDispositionInfoClass = 4;
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
                GenericRead | GenericWrite,
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

            string fullPath = Path.Combine(directory.OriginalPath, name);

            SafeFileHandle handle = CreateFileW(
                fullPath,
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                CreateNewDisposition,
                FileAttributeNormal,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new IOException("Failed to create " + name + ".");
            }

            // Confirm the created file lives inside the verified directory
            // identity, so a parent directory swapped before the create cannot
            // leave a file outside the run root.
            string canonicalPath = GetCanonicalPath(handle);
            if (canonicalPath == null || !IsWithinDirectory(canonicalPath, directory.CanonicalPath))
            {
                DeleteByHandle(handle);
                handle.Dispose();
                throw new IOException(name + " was created outside the final run root.");
            }

            FileStream stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            return new CaptureIndexCommitFile(handle, stream);
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
            // The destination is derived from that same file's own canonical
            // location, re-resolved through the file handle at rename time, so
            // a swapped parent directory cannot redirect the destination
            // outside the verified run root either.
            string fileCanonicalPath = GetCanonicalPath(file.Handle);
            if (fileCanonicalPath == null)
            {
                throw new IOException("Failed to resolve the temporary file path for rename.");
            }

            string fileDirectory = Path.GetDirectoryName(fileCanonicalPath);
            if (string.IsNullOrEmpty(fileDirectory)
                || !IsWithinDirectory(fileCanonicalPath, directory.CanonicalPath))
            {
                throw new IOException("The temporary file is not inside the verified run root.");
            }

            string fullDestination = fileDirectory + "\\" + newName;

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
    }
}
