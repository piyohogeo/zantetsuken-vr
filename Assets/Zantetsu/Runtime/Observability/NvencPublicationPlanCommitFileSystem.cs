using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, per-committer filesystem collaborator for NVENC publication
    /// plan commits. It pins the staging Run root directory to a
    /// no-follow-verified handle and performs create and rename through file
    /// identities and that stable directory handle using
    /// <c>NtSetInformationFile</c>, so a path or parent-directory swap
    /// cannot redirect an operation after verification. The rename is
    /// non-overwriting (<c>ReplaceIfExists = FALSE</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSupported"/> requires the no-follow capability; on other
    /// platforms it is false and the committer must refuse before any side
    /// effect. Phase 0.11 performs no durability flush: this backend never
    /// flushes file or directory buffers to disk and never claims
    /// crash/power-loss durability.
    /// </para>
    /// <para>
    /// This type holds no file, stream, lease, or mutable state and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencPublicationPlanCommitFileSystem : INvencPublicationPlanCommitFileSystem
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
        private const int FileRenameInformationClass = 10;
        private const int StatusSuccess = 0;

        internal static NvencPublicationPlanCommitFileSystem Create()
        {
            return new NvencPublicationPlanCommitFileSystem();
        }

        public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public NvencPublicationPlanCommitDirectory OpenDirectory(string absolutePath)
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
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new IOException("Failed to open the staging run root directory.");
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                {
                    throw new IOException("Failed to inspect the staging run root directory.");
                }

                if ((information.FileAttributes & FileAttributeDirectory) == 0)
                {
                    throw new IOException("The staging run root is not a directory.");
                }

                if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    throw new IOException("The staging run root is a reparse point.");
                }

                string canonicalPath = GetCanonicalPath(handle);
                string expected = "\\\\?\\" + normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (canonicalPath == null
                    || !string.Equals(canonicalPath, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The staging run root directory identity does not match the expected path.");
                }

                return new NvencPublicationPlanCommitDirectory(handle, normalized, canonicalPath);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public NvencPublicationPlanCommitFile CreateNew(
            NvencPublicationPlanCommitDirectory directory,
            string name)
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
                // root. If the directory was renamed mid-commit and the
                // resolved path no longer matches, fail closed. The dedicated
                // temporary is never deleted here; it is left for Abort
                // Cleanup / Recovery, and the handle is closed by the finally
                // block below.
                string canonicalPath = GetCanonicalPath(handle);
                if (canonicalPath == null || !IsWithinDirectory(canonicalPath, directory.CanonicalPath))
                {
                    throw new IOException(name + " was created outside the staging run root.");
                }

                FileStream stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
                NvencPublicationPlanCommitFile file = new NvencPublicationPlanCommitFile(handle, stream);
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

        public void Rename(
            NvencPublicationPlanCommitFile file,
            NvencPublicationPlanCommitDirectory directory,
            string newName)
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

            // The destination is handle-relative: RootDirectory pins the
            // verified directory identity and FileName is the fixed basename,
            // so a directory moved or swapped after verification can never
            // redirect the rename to a replacement path. ReplaceIfExists stays
            // FALSE (zeroed) so the rename never overwrites.
            int nameBytes = checked(newName.Length * sizeof(char));
            int headerSize = IntPtr.Size == 8 ? 20 : 12;
            int rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
            int fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int totalSize = checked(headerSize + nameBytes);

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int i = 0; i < totalSize; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                Marshal.WriteIntPtr(buffer, rootDirectoryOffset, directory.Handle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes);
                for (int i = 0; i < newName.Length; i++)
                {
                    Marshal.WriteInt16(buffer, headerSize + i * 2, (short)newName[i]);
                }

                IoStatusBlock ioStatusBlock;
                int status = NtSetInformationFile(
                    file.Handle,
                    out ioStatusBlock,
                    buffer,
                    (uint)totalSize,
                    FileRenameInformationClass);

                if (status != StatusSuccess)
                {
                    throw new IOException(
                        "Atomic non-overwriting rename failed (NTSTATUS 0x" + status.ToString("X8") + ", name='" + newName + "').");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static SafeFileHandle CreateNewRelativeToDirectory(
            NvencPublicationPlanCommitDirectory directory,
            string name)
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
                    FileShareRead,
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
