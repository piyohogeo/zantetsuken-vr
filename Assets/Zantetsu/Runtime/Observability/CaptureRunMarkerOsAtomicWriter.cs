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
    /// The temporary entry is created new, so a leftover from an earlier
    /// attempt fails rather than being reused, and it is opened no-follow so a
    /// link put in its place is refused instead of written through. The rename
    /// is done from the file's own handle onto the verified parent directory
    /// handle plus the fixed final name, with replace-if-exists off: the
    /// source cannot be swapped after it was written, the destination cannot
    /// be redirected by a directory swapped after verification, and an
    /// existing final marker always fails.
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
        private const uint CreateNew = 1u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagOpenReparsePoint = 0x00200000u;
        private const uint FileFlagBackupSemantics = 0x02000000u;
        private const uint FileAttributeNormal = 0x00000080u;
        private const int FileRenameInformationClass = 10;
        private const int StatusSuccess = 0;
        private const int ErrorFileNotFound = 2;

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
            string directoryPath = Path.GetDirectoryName(finalPath);

            if (string.IsNullOrEmpty(finalName) || string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException(
                    "The marker operation must name a final marker inside a directory.", nameof(operation));
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

                // 1. the temporary path as a new entry, refusing a leftover.
                SafeFileHandle file = CreateFileW(
                    temporaryPath,
                    GenericWrite | DeleteAccess,
                    0u,
                    IntPtr.Zero,
                    CreateNew,
                    FileAttributeNormal | FileFlagOpenReparsePoint,
                    IntPtr.Zero);

                if (file.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    file.Dispose();
                    throw new IOException(
                        "The marker's temporary entry could not be created (win32 error "
                        + error + ").");
                }

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
            }

            // 6. nothing authoritative is left where the temporary entry was.
            if (TemporaryEntryStillExists(temporaryPath))
            {
                throw new IOException(
                    "An entry still exists at the marker's temporary path after the rename.");
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

        private static bool TemporaryEntryStillExists(string temporaryPath)
        {
            using (SafeFileHandle handle = CreateFileW(
                temporaryPath,
                0u,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (!handle.IsInvalid)
                {
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ErrorFileNotFound)
                {
                    return false;
                }

                throw new IOException(
                    "The marker's temporary path could not be re-examined after the rename (win32 error "
                    + error + ").");
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

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public int Status;
            public IntPtr Information;
        }
    }
}
