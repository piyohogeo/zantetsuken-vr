using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Windows backend for the Fresh NVENC Run chunk artifact publication. It
    /// opens the exact staging and final Run roots no-follow, confirms both
    /// live on the same volume, resolves every intermediate directory
    /// handle-relative without following reparse points, inspects only the
    /// staging file's metadata, performs exactly one non-overwriting
    /// handle-bound rename, and then reads the placed final file exactly once
    /// through the same file identity to confirm its length and SHA-256.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The staging file's content is never read: the Fresh path trusts the
    /// streaming hash already confirmed at Context finalization and pays for
    /// exactly one full read, after placement, of the final file. The
    /// verification buffer is supplied by the caller and is the only buffer
    /// used; no array proportional to the chunk length is ever allocated.
    /// </para>
    /// <para>
    /// Every ordinary failure converges to <c>false</c>: an I/O failure, an
    /// access denial, a concurrent change, a refused rename, or a final length
    /// or hash mismatch. Nothing is retried, rolled back, deleted, renamed a
    /// second time, or re-inspected after a failure, and a placed but
    /// unverified final file is deliberately left in place for Recovery. The
    /// final destination directory chain is created before the staging chunk
    /// is inspected, so a failure can leave an empty destination directory
    /// behind. Unexpected programming failures are never swallowed.
    /// </para>
    /// <para>
    /// Every handle this backend opens refuses delete sharing, and the staging
    /// chunk is opened with read sharing only, so no other handle can write
    /// to, delete, or rename the chunk or move a verified directory while the
    /// publication runs. A concurrent holder of such a handle makes the open
    /// fail, which converges to <c>false</c> before the rename rather than
    /// letting a same-handle verification pass over bytes the descriptor's
    /// final path no longer holds.
    /// </para>
    /// <para>
    /// Phase 0.11 performs no durability flush: this backend never flushes
    /// file or directory buffers to disk and never claims crash or power-loss
    /// durability. A <c>true</c> result means only that the rename and the
    /// post-placement verification both succeeded in this synchronous call.
    /// </para>
    /// <para>
    /// This type holds no handle, stream, descriptor, operation, or buffer in
    /// fields and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunArtifactPublicationFileSystem : INvencRunArtifactPublicationFileSystem
    {
        private const uint DeleteAccess = 0x00010000u;
        private const uint FileGenericRead = 0x00120089u;
        private const uint FileGenericWrite = 0x00120116u;
        private const uint FileTraverse = 0x00000020u;
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint OpenExisting = 3u;
        private const uint FileOpenDisposition = 1u;
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
        private const int FileRenameInformationClass = 10;
        private const int StatusSuccess = 0;
        private const int VerificationStreamBufferLength = 4096;

        internal static NvencRunArtifactPublicationFileSystem Create()
        {
            return new NvencRunArtifactPublicationFileSystem();
        }

        public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public bool TryPublishFresh(
            CaptureRunRootLayout rootLayout,
            CaptureArtifactDescriptor descriptor,
            byte[] verificationBuffer)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (verificationBuffer == null)
            {
                throw new ArgumentNullException(nameof(verificationBuffer));
            }

            if (!rootLayout.IsValid)
            {
                throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));
            }

            if (!descriptor.IsValid)
            {
                throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            }

            if (verificationBuffer.Length < 1)
            {
                throw new ArgumentException("Verification buffer must be non-empty.", nameof(verificationBuffer));
            }

            // Capability insufficiency is a configuration error that belongs
            // before a Run starts; it is never disguised as a publication
            // content failure.
            if (!IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Fresh NVENC Run artifact publication requires Windows no-follow file handles.");
            }

            string[] stagingSegments = SplitRelativePath(descriptor.StagingRelativePath, nameof(descriptor));
            string[] finalSegments = SplitRelativePath(descriptor.FinalRelativePath, nameof(descriptor));

            NvencRunArtifactPublicationDirectoryHandle stagingRoot = null;
            NvencRunArtifactPublicationDirectoryHandle finalRoot = null;
            NvencRunArtifactPublicationDirectoryHandle stagingDirectory = null;
            NvencRunArtifactPublicationDirectoryHandle finalDirectory = null;
            NvencRunArtifactPublicationFileHandle source = null;
            try
            {
                stagingRoot = OpenRunRoot(rootLayout.StagingRunRoot, "staging Run root");
                finalRoot = OpenRunRoot(rootLayout.FinalRunRoot, "final Run root");

                // A handle-bound rename can never cross a volume boundary, and
                // Phase 0.11 has no copy fallback. Refuse before any directory
                // is created and before any rename is attempted.
                if (!IsSameVolume(stagingRoot.Handle, finalRoot.Handle))
                {
                    return false;
                }

                stagingDirectory = OpenExistingDirectoryChain(
                    stagingRoot, stagingSegments, "staging chunk directory");
                finalDirectory = OpenOrCreateDirectoryChain(
                    finalRoot, finalSegments, "final chunk directory");

                source = OpenStagingFile(stagingDirectory, stagingSegments[stagingSegments.Length - 1]);

                // Fresh path: only the staging file's metadata length is
                // confirmed. Its content is never read and never re-hashed.
                if (GetByteLength(source.Handle, "staging chunk file") != descriptor.ByteLength)
                {
                    return false;
                }

                // Exactly one rename attempt. Its outcome is never re-derived,
                // re-read, cleaned up, or retried.
                RenameNonOverwriting(
                    source.Handle, finalDirectory.Handle, finalSegments[finalSegments.Length - 1]);

                return VerifyPlacedFinal(source, descriptor, verificationBuffer);
            }
            catch (Exception ex) when (IsOrdinaryPublicationFailure(ex))
            {
                return false;
            }
            finally
            {
                Release(source);
                Release(finalDirectory);
                Release(stagingDirectory);
                Release(finalRoot);
                Release(stagingRoot);
            }
        }

        private static bool VerifyPlacedFinal(
            NvencRunArtifactPublicationFileHandle source,
            CaptureArtifactDescriptor descriptor,
            byte[] verificationBuffer)
        {
            // The rename is bound to this exact file identity, so the placed
            // final file is read back through the same handle: no re-open, no
            // second path resolution, and exactly one full read.
            FileStream stream = new FileStream(
                source.Handle, FileAccess.Read, VerificationStreamBufferLength, isAsync: false);
            source.AdoptStream(stream);

            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }

            CaptureArtifactVerificationResult result =
                CaptureArtifactStreamingVerifier.Verify(descriptor, stream, verificationBuffer);

            return result.Status == CaptureArtifactVerificationStatus.MatchesExpected;
        }

        private static NvencRunArtifactPublicationDirectoryHandle OpenRunRoot(string runRoot, string label)
        {
            // Delete sharing is refused for the whole publication: a Run root
            // that another handle can rename or delete could still be moved
            // out from under the verified placement after this handle's
            // identity check. The root is never deleted or renamed here, so no
            // DELETE access is requested either.
            SafeFileHandle handle = CreateFileW(
                runRoot,
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
                throw new IOException("Failed to open the " + label + " (win32 error " + error + ").");
            }

            try
            {
                string expected = "\\\\?\\" + runRoot.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string canonicalPath = VerifyDirectoryIdentity(handle, expected, label);
                return new NvencRunArtifactPublicationDirectoryHandle(handle, canonicalPath);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static NvencRunArtifactPublicationDirectoryHandle OpenExistingDirectoryChain(
            NvencRunArtifactPublicationDirectoryHandle root,
            string[] segments,
            string label)
        {
            return OpenDirectoryChain(root, segments, label, FileOpenDisposition);
        }

        private static NvencRunArtifactPublicationDirectoryHandle OpenOrCreateDirectoryChain(
            NvencRunArtifactPublicationDirectoryHandle root,
            string[] segments,
            string label)
        {
            return OpenDirectoryChain(root, segments, label, FileOpenIfDisposition);
        }

        private static NvencRunArtifactPublicationDirectoryHandle OpenDirectoryChain(
            NvencRunArtifactPublicationDirectoryHandle root,
            string[] segments,
            string label,
            uint disposition)
        {
            // Every intermediate directory is opened handle-relative and
            // no-follow, so a junction anywhere on the way to the leaf is
            // refused, not only one at the last segment.
            NvencRunArtifactPublicationDirectoryHandle current = root;
            bool ownsCurrent = false;
            try
            {
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    string name = segments[i];
                    int status = NtCreateFileRelative(
                        current.Handle,
                        name,
                        FileGenericRead | FileGenericWrite | FileTraverse,
                        FileShareRead | FileShareWrite,
                        disposition,
                        FileDirectoryFile | FileFlagOpenReparsePoint | FileSynchronousIoNonAlert,
                        out SafeFileHandle handle);

                    if (status != StatusSuccess || handle == null)
                    {
                        handle?.Dispose();
                        throw new IOException(
                            "Failed to resolve the " + label + " segment (NTSTATUS 0x"
                            + status.ToString("X8") + ").");
                    }

                    NvencRunArtifactPublicationDirectoryHandle opened;
                    try
                    {
                        string canonicalPath = VerifyDirectoryIdentity(
                            handle, current.CanonicalPath + "\\" + name, label);
                        opened = new NvencRunArtifactPublicationDirectoryHandle(handle, canonicalPath);
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }

                    if (ownsCurrent)
                    {
                        current.Dispose();
                    }

                    current = opened;
                    ownsCurrent = true;
                }

                return current;
            }
            catch
            {
                if (ownsCurrent)
                {
                    current.Dispose();
                }

                throw;
            }
        }

        private static NvencRunArtifactPublicationFileHandle OpenStagingFile(
            NvencRunArtifactPublicationDirectoryHandle directory,
            string name)
        {
            // Read sharing only. Another handle that could write to, delete, or
            // rename the chunk while this publication runs would let a
            // concurrent change land after the post-placement verification has
            // already read past it, so the same-handle hash could pass while
            // the descriptor's final path no longer holds those bytes. DELETE
            // access is requested for this handle's own single rename; it does
            // not grant delete sharing to anyone else.
            int status = NtCreateFileRelative(
                directory.Handle,
                name,
                FileGenericRead | DeleteAccess,
                FileShareRead,
                FileOpenDisposition,
                FileNonDirectoryFile | FileFlagOpenReparsePoint | FileSynchronousIoNonAlert,
                out SafeFileHandle handle);

            if (status != StatusSuccess || handle == null)
            {
                handle?.Dispose();
                throw new IOException(
                    "Failed to open the staging chunk file (NTSTATUS 0x" + status.ToString("X8") + ").");
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                {
                    throw new IOException("Failed to inspect the staging chunk file.");
                }

                if ((information.FileAttributes & FileAttributeDirectory) != 0)
                {
                    throw new IOException("The staging chunk path is a directory.");
                }

                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new IOException("The staging chunk path is a reparse point.");
                }

                string canonicalPath = GetCanonicalPath(handle);
                if (canonicalPath == null
                    || !string.Equals(
                        canonicalPath,
                        directory.CanonicalPath + "\\" + name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The staging chunk file identity does not match the expected path.");
                }

                return new NvencRunArtifactPublicationFileHandle(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string VerifyDirectoryIdentity(
            SafeFileHandle handle,
            string expectedCanonicalPath,
            string label)
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

            return canonicalPath;
        }

        private static bool IsSameVolume(SafeFileHandle first, SafeFileHandle second)
        {
            if (!GetFileInformationByHandle(first, out ByHandleFileInformation firstInformation)
                || !GetFileInformationByHandle(second, out ByHandleFileInformation secondInformation))
            {
                throw new IOException("Failed to inspect the Run root volume identity.");
            }

            return firstInformation.VolumeSerialNumber == secondInformation.VolumeSerialNumber;
        }

        private static long GetByteLength(SafeFileHandle handle, string label)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                throw new IOException("Failed to inspect the " + label + " length.");
            }

            return ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        }

        private static void RenameNonOverwriting(
            SafeFileHandle fileHandle,
            SafeFileHandle destinationDirectory,
            string newName)
        {
            // The destination is pinned to the verified directory handle
            // (RootDirectory) plus the fixed basename, so a directory swapped
            // after verification can never redirect the rename. ReplaceIfExists
            // stays zeroed (FALSE) so an existing final is never overwritten.
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

                Marshal.WriteIntPtr(buffer, rootDirectoryOffset, destinationDirectory.DangerousGetHandle());
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
                        "Non-overwriting chunk publication rename failed (NTSTATUS 0x"
                        + status.ToString("X8") + ").");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string[] SplitRelativePath(string relativePath, string paramName)
        {
            string[] segments = relativePath.Split('/');

            // The published chunk always lives in a directory under the Run
            // root, so the leaf always has at least one parent segment to
            // resolve handle-relative. A root-level artifact would need the
            // Run root handle itself as the rename destination and is out of
            // this surface's scope.
            if (segments.Length < 2)
            {
                throw new ArgumentException(
                    "Relative path must name a directory segment and a file segment.", paramName);
            }

            foreach (string segment in segments)
            {
                if (string.IsNullOrEmpty(segment)
                    || string.Equals(segment, ".", StringComparison.Ordinal)
                    || string.Equals(segment, "..", StringComparison.Ordinal)
                    || segment.IndexOf('\\') >= 0
                    || segment.IndexOf(':') >= 0)
                {
                    throw new ArgumentException(
                        "Relative path must consist of plain forward-slash separated segments.", paramName);
                }
            }

            return segments;
        }

        private static bool IsOrdinaryPublicationFailure(Exception ex)
        {
            // Ordinary I/O, access, contention, and rename-refusal failures
            // converge to a Failed publication. Programming and fatal failures
            // are deliberately not caught here.
            return ex is IOException
                || ex is UnauthorizedAccessException
                || ex is SecurityException
                || ex is Win32Exception
                || ex is NotSupportedException;
        }

        private static void Release(IDisposable disposable)
        {
            // Best-effort release: a cleanup failure must not change the
            // already-determined publication outcome, and the caller's ordered
            // releases still free every remaining owned handle.
            try
            {
                disposable?.Dispose();
            }
            catch (Exception ex) when (IsOrdinaryPublicationFailure(ex))
            {
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
