namespace Zantetsu.Observability
{
    /// <summary>
    /// Minimal filesystem capability surface for the Fresh NVENC Run chunk
    /// artifact publication: it moves the already-finalized staging chunk to
    /// its final name and verifies the placed final file exactly once, all
    /// bound to no-follow-verified directory and file handles so a path or
    /// parent-directory swap after verification can never redirect the
    /// placement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surface deliberately exposes no delete, no cleanup, no retry, no
    /// plan read or write, and no disposition update. A failed publication
    /// deletes and rolls back nothing - not the plan, not the staging chunk,
    /// and not a placed but unverified final file - and hands the Run to
    /// <c>PublicationRecoveryRequired</c>. It is not a no-op on the
    /// filesystem, however: the final destination directory chain is created
    /// before the staging chunk is inspected, so an empty destination
    /// directory can remain after a failure.
    /// </para>
    /// <para>
    /// <see cref="IsSupported"/> reports the no-follow capability. It is a
    /// configuration property of the platform, preflighted by the publisher
    /// before the first side effect, and is never reported as a publication
    /// content failure.
    /// </para>
    /// <para>
    /// <see cref="TryPublishFresh"/> returns <c>false</c> for every ordinary
    /// publication failure: an I/O failure, an access denial, a concurrent
    /// change, a refused non-overwriting rename, or a final length or hash
    /// mismatch. Failed-before-rename and rename-outcome-unknown are
    /// deliberately not split: both are handed to Recovery unchanged. The
    /// staging file's content is never read and never re-hashed; the Fresh
    /// path trusts the streaming hash confirmed at Context finalization and
    /// pays for exactly one full read of the placed final file.
    /// </para>
    /// </remarks>
    internal interface INvencRunArtifactPublicationFileSystem
    {
        bool IsSupported { get; }

        bool TryPublishFresh(
            CaptureRunRootLayout rootLayout,
            CaptureArtifactDescriptor descriptor,
            byte[] verificationBuffer);
    }
}
