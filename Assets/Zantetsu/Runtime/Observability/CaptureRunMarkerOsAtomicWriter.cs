using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Commits one Capture Run marker to the real filesystem: the operation's
    /// canonical bytes written to its temporary path, flushed, renamed onto the
    /// final path without overwriting, and the parent directory flushed. The
    /// receipt is issued only when all of that has happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One directory handle is the authority for the whole commit. Its own
    /// name is opened without following a link, and it is accepted only as a
    /// directory that is not itself a reparse point and still resolves to the
    /// directory the operation named - that last comparison being what
    /// detects indirection introduced by an ancestor, which the open itself
    /// does not prevent. The temporary entry is then created <em>relative to
    /// that handle</em>, the rename moves it onto the final name inside the
    /// same handle, and the check that nothing is left behind is made relative
    /// to it too - so every step acts on one verified directory identity, and
    /// a path substituted after that handle was taken cannot send any of them
    /// somewhere else.
    /// </para>
    /// <para>
    /// The temporary entry is created new, so a leftover from an earlier
    /// attempt fails rather than being reused, and the rename runs from the
    /// file's own handle with replace-if-exists off, so an existing final
    /// marker always fails.
    /// </para>
    /// <para>
    /// The bytes written are exactly the ones the operation already carries.
    /// No codec runs here, nothing is re-encoded or re-ordered, and neither the
    /// bytes, the operation, nor any path is kept after the call - only the
    /// returned receipt holds the operation.
    /// </para>
    /// <para>
    /// If the directory flush fails after the rename, the final marker already
    /// exists. That failure is reported as it happened: nothing is deleted,
    /// nothing is retried, and no alternate destination is used, because a
    /// later recovery pass is what re-examines the filesystem.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunMarkerOsAtomicWriter : ICaptureRunMarkerAtomicWriter
    {
        private const uint GenericRead = 0x80000000u;
        private const uint GenericWrite = 0x40000000u;
        private const uint DeleteAccess = 0x00010000u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileGenericWrite = 0x00120116u;
        private const uint FileReadAttributes = 0x00000080u;
        private const uint Synchronize = 0x00100000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const uint FileAttributeDirectory = 0x00000010u;
        private const uint FileAttributeReparsePoint = 0x00000400u;
        private const uint FileNameNormalized = 0x00000000u;
        private const uint FileOpenDisposition = 1u;
        private const uint FileCreateDisposition = 2u;
        private const uint FileNonDirectoryFile = 0x00000040u;
        private const uint FileOpenReparsePointOption = 0x00200000u;
        private const uint FileSynchronousIoNonAlert = 0x00000020u;
        private const uint ObjCaseInsensitive = 0x00000040u;
        private const int FileRenameInformationClass = 10;
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusSuccess = 0;

        internal static CaptureRunMarkerOsAtomicWriter Create()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture Run marker writing requires Windows no-follow file handles.");
            }

            return new CaptureRunMarkerOsAtomicWriter();
        }

        private CaptureRunMarkerOsAtomicWriter()
        {
        }

        /// <summary>
        /// One synchronous attempt at the whole commit, in the contract's
        /// order.
        /// </summary>
        public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture Run marker writing requires Windows no-follow file handles.");
            }

            string temporaryPath = operation.TemporaryPath;
            string finalPath = operation.FinalPath;
            string finalName = Path.GetFileName(finalPath);
            string temporaryName = Path.GetFileName(temporaryPath);
            string directoryPath = Path.GetDirectoryName(finalPath);

            if (string.IsNullOrEmpty(finalName) || string.IsNullOrEmpty(temporaryName)
                || string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException(
                    "The marker operation must name a temporary and a final marker inside a directory.",
                    nameof(operation));
            }

            // The rename is atomic only inside one directory, so both names
            // have to belong to the same one; that is also what lets a single
            // verified handle be the authority for the whole commit.
            if (!string.Equals(
                Path.GetDirectoryName(temporaryPath), directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The marker operation's temporary and final paths must share one directory.",
                    nameof(operation));
            }

            // The rename's destination is this handle plus the fixed name, so
            // it is opened once, no-follow, and held across the whole commit.
            // The handle carries GENERIC_WRITE because the durable directory
            // flush below goes through this same handle and a query-only
            // handle is refused for it.
            using (SafeFileHandle directory = CreateFileW(
                directoryPath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (directory.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The marker's directory could not be opened without following links (win32 error "
                        + error + ").");
                }

                RequireVerifiedDirectory(directory, directoryPath);

                // 1. the temporary entry, created new and resolved by the
                //    kernel against the verified directory handle.
                SafeFileHandle file = CreateNewRelative(
                    directory, temporaryName, FileGenericWrite | DeleteAccess | Synchronize);

                // The handle stays open across the write, the flush and the
                // rename, and is closed here rather than left to the stream:
                // the entry is opened with no sharing, so anything still
                // holding it would lock the final marker.
                try
                {
                    using (FileStream stream = new FileStream(file, FileAccess.Write, 1, false))
                    {
                        // 2. every canonical byte the operation already holds.
                        byte[] canonicalBytes = operation.GetCanonicalBytes();
                        stream.Write(canonicalBytes, 0, canonicalBytes.Length);

                        // 3. the entry's data, durably.
                        stream.Flush();
                        if (!FlushFileBuffers(file))
                        {
                            int error = Marshal.GetLastWin32Error();
                            throw new IOException(
                                "The marker's data flush failed (win32 error " + error + ").");
                        }

                        // 4. onto the final name, from this file's own
                        //    identity, inside this verified directory, without
                        //    overwriting.
                        RenameWithoutReplacing(file, directory, finalName);
                    }
                }
                finally
                {
                    file.Dispose();
                }

                // 5. the parent directory, durably. The final marker already
                //    exists if this fails, and that is reported as it happened.
                if (!FlushFileBuffers(directory))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The marker directory's flush failed after the rename (win32 error "
                        + error + ").");
                }

                // 6. nothing is left under the temporary name, asked of the
                //    same directory identity the entry was created in.
                if (RelativeEntryExists(directory, temporaryName))
                {
                    throw new IOException(
                        "An entry still exists at the marker's temporary name after the rename.");
                }
            }

            // 7. and only now.
            return new CaptureRunMarkerWriteReceipt(this, operation);
        }

        /// <summary>
        /// FILE_RENAME_INFO: BOOLEAN ReplaceIfExists at offset 0, padded to
        /// HANDLE alignment, HANDLE RootDirectory, ULONG FileNameLength, then
        /// the WCHAR name. FileNameLength counts the name bytes only, but the
        /// buffer still reserves an explicit terminator plus native padding so
        /// nothing past the allocation is read, and the whole allocation is
        /// zeroed first.
        /// </summary>
        private static void RenameWithoutReplacing(
            SafeFileHandle file, SafeFileHandle directory, string finalName)
        {
            int nameBytes = checked(finalName.Length * sizeof(char));
            int rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
            int fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
            int minimumSize = checked(fileNameOffset + nameBytes + sizeof(char));
            int totalSize = checked((minimumSize + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size);

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int i = 0; i < totalSize; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                // ReplaceIfExists stays FALSE (zeroed) so an existing final
                // marker is a failure and never an overwrite.
                Marshal.WriteIntPtr(buffer, rootDirectoryOffset, directory.DangerousGetHandle());
                Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes);
                for (int i = 0; i < finalName.Length; i++)
                {
                    Marshal.WriteInt16(buffer, fileNameOffset + (i * sizeof(char)), finalName[i]);
                }

                int status = NtSetInformationFile(
                    file,
                    out IoStatusBlock ioStatusBlock,
                    buffer,
                    (uint)totalSize,
                    FileRenameInformationClass);

                if (status != StatusSuccess)
                {
                    throw new IOException(
                        "The marker's non-overwriting rename onto '" + finalName
                        + "' failed (NTSTATUS 0x" + status.ToString("X8") + ").");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Accepts the handle only as a directory that is not a reparse point
        /// and still resolves to the path the operation named.
        /// </summary>
        private static void RequireVerifiedDirectory(SafeFileHandle handle, string expectedPath)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    "The marker directory's identity could not be read (win32 error " + error + ").");
            }

            if ((information.FileAttributes & FileAttributeDirectory) == 0)
            {
                throw new IOException("The marker's directory is not a directory.");
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException(
                    "The marker's directory is a reparse point; it is never followed.");
            }

            uint required = GetFinalPathNameByHandle(handle, null, 0u, FileNameNormalized);
            if (required == 0u)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    "The marker directory's final path could not be read (win32 error " + error + ").");
            }

            char[] buffer = new char[required];
            uint written = GetFinalPathNameByHandle(handle, buffer, required, FileNameNormalized);
            if (written == 0u || written >= required)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    "The marker directory's final path could not be read (win32 error " + error + ").");
            }

            string finalPath = new string(buffer, 0, (int)written);
            if (!string.Equals(
                Normalize(finalPath), Normalize(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The marker's directory resolves to a different location than the operation named.");
            }
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
        /// Creates one new file named by a single leaf, resolved by the kernel
        /// against the given directory handle rather than against any path.
        /// </summary>
        private static SafeFileHandle CreateNewRelative(
            SafeFileHandle directory, string name, uint desiredAccess)
        {
            int status = OpenRelative(
                directory, name, desiredAccess, FileCreateDisposition, 0u,
                out SafeFileHandle handle);

            if (status != StatusSuccess || handle == null || handle.IsInvalid)
            {
                if (handle != null)
                {
                    handle.Dispose();
                }

                throw new IOException(
                    "The marker's temporary entry could not be created in its verified directory (NTSTATUS 0x"
                    + status.ToString("X8") + ").");
            }

            return handle;
        }

        /// <summary>
        /// Asks the same directory identity whether a leaf name is still
        /// there. Absent is the only answer that is not a failure.
        /// </summary>
        private static bool RelativeEntryExists(SafeFileHandle directory, string name)
        {
            int status = OpenRelative(
                directory, name, FileReadAttributes | Synchronize, FileOpenDisposition,
                FileShareRead, out SafeFileHandle handle);

            if (handle != null)
            {
                handle.Dispose();
            }

            if (status == StatusSuccess)
            {
                return true;
            }

            if (status == StatusObjectNameNotFound)
            {
                return false;
            }

            throw new IOException(
                "The marker's temporary name could not be re-examined after the rename (NTSTATUS 0x"
                + status.ToString("X8") + ").");
        }

        private static int OpenRelative(
            SafeFileHandle directory,
            string name,
            uint desiredAccess,
            uint createDisposition,
            uint shareAccess,
            out SafeFileHandle handle)
        {
            handle = null;
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
                    RootDirectory = directory.DangerousGetHandle(),
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
                    FileNonDirectoryFile | FileOpenReparsePointOption | FileSynchronousIoNonAlert,
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
        private static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

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

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

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
