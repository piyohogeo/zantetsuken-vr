using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Minimal filesystem capability surface for the capture-complete cleanup
    /// backend: no-follow open, handle-bound delete for files and empty
    /// directories, and directory metadata flush. The cleanup backend never
    /// creates, renames, or writes, so no create/rename/write capability is
    /// exposed here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryOpenDirectory"/> exists so a resumed cleanup can tell a
    /// directory that is confirmed gone from one it could not observe.
    /// <see cref="IsSupported"/> reports the no-follow open capability and
    /// <see cref="IsDirectoryFlushSupported"/> reports directory metadata flush
    /// capability; both must be preflighted before the first side effect. A
    /// missing target is never reported as success: the backend requires an
    /// exact handle-bound identity for every deletion.
    /// </para>
    /// </remarks>
    internal interface ICaptureCompleteCleanupFileSystem
    {
        bool IsSupported { get; }

        bool IsDirectoryFlushSupported { get; }

        CaptureIndexCommitDirectory OpenDirectory(string absolutePath);

        /// <summary>
        /// Opens a directory the way <see cref="OpenDirectory"/> does, but
        /// reports a confirmed missing directory as
        /// <see cref="CaptureIndexFileOpenStatus.Absent"/> instead of throwing.
        /// A cleanup that may resume after a partial failure needs that
        /// distinction, and it must come from the open itself rather than from
        /// an error message or a path-based existence probe.
        /// </summary>
        CaptureIndexDirectoryOpen TryOpenDirectory(string absolutePath);

        CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name);

        void Delete(CaptureIndexCommitFile file);

        void FlushDirectory(CaptureIndexCommitDirectory directory);

        void DeleteDirectory(CaptureIndexCommitDirectory directory);

        bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory);
    }
}
