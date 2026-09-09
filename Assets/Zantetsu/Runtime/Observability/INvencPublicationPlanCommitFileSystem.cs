using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable filesystem capability surface for the NVENC publication plan
    /// committer: no-follow directory open, non-overwriting handle-bound file
    /// creation, and handle-bound non-overwriting rename, all pinned to a
    /// stable <see cref="NvencPublicationPlanCommitDirectory"/> or to an exact
    /// file identity (<see cref="NvencPublicationPlanCommitFile"/>), so a path
    /// or parent-directory swap after verification can never redirect an
    /// operation to a different file or outside the staging Run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSupported"/> reports the no-follow capability; the
    /// committer preflights it before the first side effect. This surface
    /// deliberately exposes no delete, no re-read, no durability flush, and no
    /// re-open: Phase 0.11 never deletes its temporary file, never re-reads a
    /// committed file to resolve an unknown outcome, and never claims
    /// crash/power-loss durability.
    /// </para>
    /// </remarks>
    internal interface INvencPublicationPlanCommitFileSystem
    {
        bool IsSupported { get; }

        NvencPublicationPlanCommitDirectory OpenDirectory(string absolutePath);

        NvencPublicationPlanCommitFile CreateNew(
            NvencPublicationPlanCommitDirectory directory,
            string name);

        void Rename(
            NvencPublicationPlanCommitFile file,
            NvencPublicationPlanCommitDirectory directory,
            string newName);
    }
}
