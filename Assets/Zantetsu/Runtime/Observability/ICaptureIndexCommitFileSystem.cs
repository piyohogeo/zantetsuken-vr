using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Small, immutable filesystem capability surface for the Capture Index
    /// committer: platform-safe no-follow read open and directory metadata
    /// flush. It owns no file, stream, handle, or mutable state and performs no
    /// content classification of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSupported"/> is false on platforms that cannot open files
    /// without following reparse points and cannot flush directory metadata;
    /// the committer must fail closed before any side effect in that case.
    /// There is no process-global hook; each committer owns its own collaborator.
    /// </para>
    /// </remarks>
    internal interface ICaptureIndexCommitFileSystem
    {
        bool IsSupported { get; }

        CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath);

        void FlushDirectory(string directoryPath);
    }
}
