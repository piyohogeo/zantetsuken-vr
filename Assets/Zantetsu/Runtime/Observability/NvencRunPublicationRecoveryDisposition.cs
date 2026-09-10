namespace Zantetsu.Observability
{
    /// <summary>
    /// Disposition selected by the pure Phase 0.11 NVENC publication recovery
    /// classifier for one observed Run root after a process restart. Values are
    /// fixed, explicitly numbered, and append-only; existing values must never
    /// be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// This is a recovery judgement about observed files, not the process-local
    /// <see cref="NvencRunEvidenceDisposition"/> of a live Run: the previous
    /// process's disposition is never restored or inferred.
    /// <see cref="None"/> is the uninitialized value and is never a valid
    /// classification result.
    /// </remarks>
    internal enum NvencRunPublicationRecoveryDisposition : int
    {
        None = 0,
        Incomplete = 1,
        PublicationRecoveryRequired = 2,
        PublicationRecoveryCollision = 3,
        Deferred = 4,
    }
}
