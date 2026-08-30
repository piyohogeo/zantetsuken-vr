namespace Zantetsu.Observability
{
    /// <summary>
    /// Disposition selected by the pure artifact recovery classifier for one
    /// observed Capture Run publication. Values are fixed, explicitly
    /// numbered, and append-only; existing values must never be renumbered or
    /// removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OrphanedPreTrace"/> means a plan exists but no trace
    /// manifest does, so automatic publication is forbidden.
    /// <see cref="PublishMissingArtifacts"/> means the trace matches and every
    /// missing final artifact has a matching staging source.
    /// <see cref="CommitCaptureIndex"/> means the plan is authoritative, the
    /// trace matches, and every final artifact matches.
    /// <see cref="CaptureComplete"/> means the index is authoritative, the
    /// trace matches, and every final artifact matches.
    /// <see cref="ArtifactSourceMissing"/> means the plan is authoritative but
    /// a missing final artifact also lacks its staging source.
    /// <see cref="PublishedArtifactMissing"/> means the index is authoritative
    /// yet a final artifact is missing.
    /// <see cref="RunRootCollision"/> covers content mismatch, invalid,
    /// limit-exceeded, or ordering violations.
    /// </para>
    /// <para>
    /// <see cref="None"/> is never a valid classification result.
    /// </para>
    /// </remarks>
    internal enum CaptureRunPublicationArtifactRecoveryDisposition : int
    {
        None = 0,
        OrphanedPreTrace = 1,
        PublishMissingArtifacts = 2,
        CommitCaptureIndex = 3,
        CaptureComplete = 4,
        ArtifactSourceMissing = 5,
        PublishedArtifactMissing = 6,
        RunRootCollision = 7
    }
}
