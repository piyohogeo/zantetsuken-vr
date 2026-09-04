using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable filesystem capability surface for the Capture Index committer.
    /// It provides no-follow open, non-overwriting create, file flush, directory
    /// metadata flush, and handle-bound rename and delete, all pinned to a
    /// stable <see cref="CaptureIndexCommitDirectory"/> or to an exact file
    /// identity (<see cref="CaptureIndexCommitFile"/>), so a path or parent
    /// directory swapped after verification can never redirect an operation to
    /// a different file or outside the run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSupported"/> reports no-follow open capability and
    /// <see cref="IsDirectoryFlushSupported"/> reports directory metadata flush
    /// capability. The committer must preflight both before the first side
    /// effect. There is no process-global hook; each committer owns its own
    /// collaborator, and no method performs content classification.
    /// </para>
    /// </remarks>
    internal interface ICaptureIndexCommitFileSystem
    {
        bool IsSupported { get; }

        bool IsDirectoryFlushSupported { get; }

        CaptureIndexCommitDirectory OpenDirectory(string absolutePath);

        CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name);

        CaptureIndexCommitFile CreateNew(CaptureIndexCommitDirectory directory, string name);

        void FlushFileData(CaptureIndexCommitFile file);

        void Rename(CaptureIndexCommitFile file, CaptureIndexCommitDirectory directory, string newName);

        void Delete(CaptureIndexCommitFile file);

        void FlushDirectory(CaptureIndexCommitDirectory directory);
    }
}
