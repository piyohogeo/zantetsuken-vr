using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, per-committer filesystem collaborator for Capture Index
    /// commits. It delegates no-follow read open to a single
    /// <see cref="ICaptureArtifactNoFollowOpener"/> and performs directory
    /// metadata flush through a Windows directory handle opened with
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSupported"/> requires both the no-follow opener capability
    /// and the directory-flush capability; on other platforms it is false and
    /// the committer must refuse before any side effect. A directory flush is
    /// never faked: the handle open and <c>FlushFileBuffers</c> must both
    /// succeed or an <see cref="IOException"/> propagates.
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
        private const uint FileShareRead = 0x00000001u;
        private const uint FileShareWrite = 0x00000002u;
        private const uint FileShareDelete = 0x00000004u;
        private const uint OpenExisting = 3u;
        private const uint FileFlagBackupSemantics = 0x02000000u;

        private readonly ICaptureArtifactNoFollowOpener _noFollowOpener;

        internal CaptureIndexCommitFileSystem(ICaptureArtifactNoFollowOpener noFollowOpener)
        {
            _noFollowOpener = noFollowOpener ?? throw new ArgumentNullException(nameof(noFollowOpener));
        }

        internal static CaptureIndexCommitFileSystem Create()
        {
            return new CaptureIndexCommitFileSystem(CaptureArtifactNoFollowOpen.Create());
        }

        public bool IsSupported => _noFollowOpener.IsSupported;

        public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
        {
            return _noFollowOpener.TryOpen(root, relativePath);
        }

        public void FlushDirectory(string directoryPath)
        {
            if (directoryPath == null)
            {
                throw new ArgumentNullException(nameof(directoryPath));
            }

            if (!IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Directory metadata flush is not supported on this platform.");
            }

            SafeFileHandle handle = CreateFileW(
                directoryPath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new IOException("Failed to open the directory for a metadata flush.");
            }

            try
            {
                if (!FlushFileBuffers(handle))
                {
                    throw new IOException("Directory metadata flush failed.");
                }
            }
            finally
            {
                handle.Dispose();
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
    }
}
