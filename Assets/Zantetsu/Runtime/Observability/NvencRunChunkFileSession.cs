using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Windows Run chunk file session: the real <c>.partial</c> file pinned to
    /// a no-follow-verified staging Run root handle. Every operation — open,
    /// create, append, close, and rename — is performed through file identities
    /// or the pinned directory handles, so a path or parent-directory swap can
    /// never redirect a write or a rename outside the Run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creation is only possible through the validated static factory, which
    /// accepts the exact <see cref="CaptureRunInitializationSessionIssue"/> and
    /// verifies it, the liveness of its ownership lease, and the exact binding
    /// of its lock identity evidence before any filesystem side effect. The
    /// session never releases the ownership lease or the OS lock.
    /// </para>
    /// <para>
    /// The pending file is <c>chunks/chunk-0.nvenc-idr-chunk-v1.h264.partial</c>
    /// and the staging file is <c>chunks/chunk-0.nvenc-idr-chunk-v1.h264</c>,
    /// both fixed names under the staging Run root. The <c>chunks</c> directory
    /// is created or verified handle-relative, the <c>.partial</c> is created
    /// create-new handle-relative, and the staging rename is a single
    /// non-overwriting rename of a retained file identity to a destination
    /// resolved from the pinned chunks directory handle. No
    /// step is retried, no step is rolled back, no path fallback or copy-delete
    /// exists, the file is never re-opened or read back, and a flush-to-disk is
    /// never required.
    /// </para>
    /// <para>
    /// This type holds no counter, hash, or frame relation of its own, and
    /// <see cref="Dispose"/> is idempotent and releases only the file and
    /// directory handles it owns. An unmoved <c>.partial</c> is not guaranteed
    /// to be deleted, and an unknown rename result is never re-observed.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunChunkFileSession : INvencRunChunkFileSession, IDisposable
    {
        private const string ChunksDirectoryName = "chunks";
        private const string PendingFileName = "chunk-0.nvenc-idr-chunk-v1.h264.partial";
        private const string StagingFileName = "chunk-0.nvenc-idr-chunk-v1.h264";

        private const uint GenericRead = 0x80000000u;
        private const uint GenericWrite = 0x40000000u;
        private const uint DeleteAccess = 0x00010000u;
        private const uint FileGenericRead = 0x00120089u;
        private const uint FileGenericWrite = 0x00120116u;
        private const uint FileTraverse = 0x00000020u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint FileShareDelete = 0x00000004u;
        private const uint OpenExisting = 3u;
        private const uint FileOpenDisposition = 1u;
        private const uint FileCreateDisposition = 2u;
        private const uint FileOpenIfDisposition = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const uint FileDirectoryFile = 0x00000001u;
        private const uint FileNonDirectoryFile = 0x00000040u;
        private const uint FileSynchronousIoNonAlert = 0x00000020u;
        private const uint ObjCaseInsensitive = 0x00000040u;
        private const uint DuplicateSameAccess = 0x00000002u;
        private const int FileRenameInformationClass = 10;
        private const int FileDispositionInfoClass = 4;
        private const int StatusSuccess = 0;
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);

        private readonly SafeFileHandle _runRootHandle;
        private readonly SafeFileHandle _chunksDirectoryHandle;
        private SafeFileHandle _appendHandle;
        private FileStream _appendStream;
        private SafeFileHandle _identityHandle;
        private bool _closed;
        private bool _moved;
        private bool _disposed;

        private NvencRunChunkFileSession(
            SafeFileHandle runRootHandle,
            SafeFileHandle chunksDirectoryHandle,
            FileStream appendStream,
            SafeFileHandle appendHandle,
            SafeFileHandle identityHandle)
        {
            _runRootHandle = runRootHandle;
            _chunksDirectoryHandle = chunksDirectoryHandle;
            _appendStream = appendStream;
            _appendHandle = appendHandle;
            _identityHandle = identityHandle;
        }

        /// <summary>
        /// Validated factory: verifies the exact session issue, its ownership
        /// lease liveness, and the exact lock identity binding, then pins the
        /// staging Run root no-follow and reserves the fixed chunk file. Any
        /// failure before acceptance leaves no acquired handle behind.
        /// </summary>
        internal static NvencRunChunkFileSession Create(CaptureRunInitializationSessionIssue issue)
        {
            if (issue == null)
            {
                throw new ArgumentNullException(nameof(issue));
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Run chunk file session requires Windows no-follow file handles.");
            }

            if (!issue.IsValid)
            {
                throw new ArgumentException("Session issue must be valid.", nameof(issue));
            }

            CaptureRunInitializationSessionOwnershipLease ownershipLease = issue.OwnershipLease;
            if (ownershipLease == null || !ownershipLease.IsCreated)
            {
                throw new ArgumentException("Ownership lease must be live.", nameof(issue));
            }

            CaptureRunLockIdentityEvidence identityEvidence = issue.LockIdentityEvidence;
            if (identityEvidence == null || !identityEvidence.IsIssuedFor(ownershipLease))
            {
                throw new ArgumentException(
                    "Lock identity evidence must be issued for the exact ownership lease.", nameof(issue));
            }

            if (!ReferenceEquals(issue.Session.RootLayout, identityEvidence.RootLayout))
            {
                throw new ArgumentException(
                    "Session and lock identity evidence must share the same root layout.", nameof(issue));
            }

            SafeFileHandle runRootHandle = null;
            SafeFileHandle chunksHandle = null;
            SafeFileHandle appendHandle = null;
            SafeFileHandle identityHandle = null;
            FileStream appendStream = null;
            try
            {
                string stagingRunRoot = issue.Session.RootLayout.StagingRunRoot;

                runRootHandle = OpenRunRootDirectory(stagingRunRoot);
                string runRootCanonical = GetCanonicalPathOrThrow(runRootHandle, "staging Run root");

                chunksHandle = OpenOrCreateChunksDirectory(runRootHandle, runRootCanonical);
                string chunksCanonical = GetCanonicalPathOrThrow(chunksHandle, "chunks directory");

                RejectIfStagingExists(chunksHandle);

                appendHandle = CreatePartialFile(chunksHandle, chunksCanonical);

                identityHandle = DuplicateFileHandle(appendHandle);

                appendStream = new FileStream(appendHandle, FileAccess.Write, 4096, isAsync: false);

                return new NvencRunChunkFileSession(
                    runRootHandle, chunksHandle, appendStream, appendHandle, identityHandle);
            }
            catch
            {
                appendStream?.Dispose();
                identityHandle?.Dispose();
                appendHandle?.Dispose();
                chunksHandle?.Dispose();
                runRootHandle?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Appends the exact byte range once in the Open state. Only a known
        /// pre-write rejection returns <see cref="NvencRunChunkAppendOutcome.RejectedBeforeWrite"/>;
        /// a write attempt that throws is an unknown result reported as
        /// <see cref="NvencRunChunkAppendOutcome.Indeterminate"/>. After
        /// <see cref="CloseAppendHandle"/> no append is accepted.
        /// </summary>
        public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
        {
            if (_closed || _moved || _disposed)
            {
                return NvencRunChunkAppendOutcome.RejectedBeforeWrite;
            }

            if (buffer == null || offset < 0 || validLength < 1 || offset > buffer.Length - validLength)
            {
                return NvencRunChunkAppendOutcome.RejectedBeforeWrite;
            }

            try
            {
                _appendStream.Write(buffer, offset, validLength);
                return NvencRunChunkAppendOutcome.Appended;
            }
            catch
            {
                return NvencRunChunkAppendOutcome.Indeterminate;
            }
        }

        /// <summary>
        /// Closes the append stream and handle. Idempotent; a second close
        /// performs no side effect. The retained identity handle stays open so
        /// the same file identity can be renamed later.
        /// </summary>
        public void CloseAppendHandle()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            FileStream stream = _appendStream;
            _appendStream = null;
            SafeFileHandle appendHandle = _appendHandle;
            _appendHandle = null;
            try
            {
                stream?.Dispose();
            }
            finally
            {
                appendHandle?.Dispose();
            }
        }

        /// <summary>
        /// Renames the retained pending file identity to the fixed staging name
        /// with one non-overwriting rename. Only a known success returns
        /// normally; the destination is resolved from the pinned chunks
        /// directory handle, never from a re-resolved path.
        /// </summary>
        public void MovePendingToStaging()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NvencRunChunkFileSession));
            }

            if (_moved)
            {
                return;
            }

            if (!_closed)
            {
                throw new InvalidOperationException("Append handle must be closed before moving pending to staging.");
            }

            RenameNonOverwriting(_identityHandle, _chunksDirectoryHandle, StagingFileName);
            _moved = true;
        }

        /// <summary>
        /// Releases every file and directory handle this session owns, exactly
        /// once. It never releases the ownership lease or the OS lock, and it
        /// does not delete an unmoved <c>.partial</c>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _appendStream?.Dispose();
            }
            finally
            {
                _appendStream = null;
                _appendHandle?.Dispose();
                _appendHandle = null;
                _identityHandle?.Dispose();
                _identityHandle = null;
                _chunksDirectoryHandle?.Dispose();
                _runRootHandle?.Dispose();
            }
        }

        private static SafeFileHandle OpenRunRootDirectory(string stagingRunRoot)
        {
            SafeFileHandle handle = CreateFileW(
                stagingRunRoot,
                GenericRead | GenericWrite | DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("Failed to open the staging Run root directory (win32 error " + error + ").");
            }

            try
            {
                string expectedCanonical = "\\\\?\\" + stagingRunRoot.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                VerifyDirectoryIdentity(handle, expectedCanonical, "staging Run root directory");
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static SafeFileHandle OpenOrCreateChunksDirectory(SafeFileHandle runRootHandle, string runRootCanonical)
        {
            int status = NtCreateFileRelative(
                runRootHandle,
                ChunksDirectoryName,
                FileGenericRead | FileGenericWrite | FileTraverse,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpenIfDisposition,
                FileDirectoryFile | FileFlagOpenReparsePoint | FileSynchronousIoNonAlert,
                out SafeFileHandle handle);

            if (status != StatusSuccess)
            {
                throw new IOException(
                    "Failed to open or create the chunks directory (NTSTATUS 0x" + status.ToString("X8") + ").");
            }

            try
            {
                VerifyDirectoryIdentity(handle, runRootCanonical + "\\" + ChunksDirectoryName, "chunks directory");
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void RejectIfStagingExists(SafeFileHandle chunksHandle)
        {
            int status = NtCreateFileRelative(
                chunksHandle,
                StagingFileName,
                FileGenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                FileOpenDisposition,
                FileNonDirectoryFile | FileFlagOpenReparsePoint | FileSynchronousIoNonAlert,
                out SafeFileHandle existing);

            if (status == StatusObjectNameNotFound || status == StatusObjectPathNotFound)
            {
                return;
            }

            if (status == StatusSuccess)
            {
                existing.Dispose();
                throw new IOException("The staging chunk file already exists and must not be overwritten.");
            }

            throw new IOException(
                "The staging chunk path is occupied by an unexpected entry (NTSTATUS 0x" + status.ToString("X8") + ").");
        }

        private static SafeFileHandle CreatePartialFile(SafeFileHandle chunksHandle, string chunksCanonical)
        {
            int status = NtCreateFileRelative(
                chunksHandle,
                PendingFileName,
                FileGenericRead | FileGenericWrite | DeleteAccess,
                FileShareRead,
                FileCreateDisposition,
                FileNonDirectoryFile | FileSynchronousIoNonAlert,
                out SafeFileHandle handle);

            if (status != StatusSuccess)
            {
                throw new IOException(
                    "Failed to create the pending chunk file (NTSTATUS 0x" + status.ToString("X8") + ").");
            }

            try
            {
                VerifyRegularFileWithin(handle, chunksCanonical, PendingFileName);
                return handle;
            }
            catch
            {
                TryDeleteByHandle(handle);
                handle.Dispose();
                throw;
            }
        }

        private static void VerifyDirectoryIdentity(SafeFileHandle handle, string expectedCanonicalPath, string label)
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
            if (canonicalPath == null
                || !string.Equals(canonicalPath, expectedCanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The " + label + " identity does not match the expected path.");
            }
        }

        private static void VerifyRegularFileWithin(SafeFileHandle handle, string directoryCanonical, string name)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                throw new IOException("Failed to inspect the pending chunk file.");
            }

            if ((information.FileAttributes & FileAttributeDirectory) != 0)
            {
                throw new IOException("The pending chunk path is a directory.");
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("The pending chunk path is a reparse point.");
            }

            string canonicalPath = GetCanonicalPath(handle);
            if (canonicalPath == null || !IsWithinDirectory(canonicalPath, directoryCanonical))
            {
                throw new IOException(name + " was created outside the chunks directory.");
            }
        }

        private static SafeFileHandle DuplicateFileHandle(SafeFileHandle source)
        {
            IntPtr process = GetCurrentProcess();
            if (!DuplicateHandle(
                process,
                source,
                process,
                out SafeFileHandle duplicate,
                0,
                false,
                DuplicateSameAccess))
            {
                throw new IOException(
                    "Failed to duplicate the pending chunk file handle (win32 error " + Marshal.GetLastWin32Error() + ").");
            }

            return duplicate;
        }

        private static void RenameNonOverwriting(SafeFileHandle fileHandle, SafeFileHandle rootDirectory, string newName)
        {
            // The destination is pinned to the verified chunks directory
            // handle (RootDirectory) with the fixed relative name, so a
            // parent-directory swap cannot redirect the rename outside the Run
            // root. ReplaceIfExists stays zeroed (FALSE) so the rename is
            // non-overwriting.
            int nameBytes = checked(newName.Length * sizeof(char));
            int rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
            int fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
            int totalSize = checked(fileNameOffset + nameBytes);

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int i = 0; i < totalSize; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                Marshal.WriteIntPtr(buffer, rootDirectoryOffset, rootDirectory.DangerousGetHandle());
                Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes);
                for (int i = 0; i < newName.Length; i++)
                {
                    Marshal.WriteInt16(buffer, fileNameOffset + i * 2, (short)newName[i]);
                }

                int status = NtSetInformationFile(
                    fileHandle,
                    out IoStatusBlock ioStatusBlock,
                    buffer,
                    (uint)totalSize,
                    FileRenameInformationClass);

                if (status != StatusSuccess)
                {
                    throw new IOException(
                        "Non-overwriting chunk rename failed (NTSTATUS 0x" + status.ToString("X8") + ", name='" + newName + "').");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void TryDeleteByHandle(SafeFileHandle handle)
        {
            try
            {
                FileDispositionInfo dispositionInfo = new FileDispositionInfo { DeleteFile = true };
                SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    ref dispositionInfo,
                    (uint)Marshal.SizeOf(typeof(FileDispositionInfo)));
            }
            catch
            {
                // Best-effort fail-closed cleanup; the reported IOException wins.
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

        private static string GetCanonicalPathOrThrow(SafeFileHandle handle, string label)
        {
            string canonicalPath = GetCanonicalPath(handle);
            if (canonicalPath == null)
            {
                throw new IOException("Failed to resolve the " + label + " canonical path.");
            }

            return canonicalPath;
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

        private static bool IsWithinDirectory(string canonicalFile, string canonicalDirectory)
        {
            string prefix = canonicalDirectory.EndsWith("\\", StringComparison.Ordinal)
                ? canonicalDirectory
                : canonicalDirectory + "\\";
            return canonicalFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            SafeFileHandle hSourceHandle,
            IntPtr hTargetProcessHandle,
            out SafeFileHandle lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            ref FileDispositionInfo lpFileInformation,
            uint dwBufferSize);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

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
