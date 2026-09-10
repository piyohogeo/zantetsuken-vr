namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous, read-only Phase 0.11 NVENC publication recovery inspector:
    /// one call is one inspection attempt that observes a restarted Run's
    /// publication files and returns what it saw.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and an invalid one throws
    /// <see cref="System.ArgumentException"/>, both before the filesystem is
    /// touched. The attempt renames, deletes, promotes, or cleans up nothing,
    /// never touches the legacy <c>publication.plan.tmp</c>, releases no lock,
    /// and owns no thread, queue, or task. There is no retry, poll, or wait
    /// inside a call: a caller that wants another observation calls again.
    /// </remarks>
    internal interface INvencRunPublicationRecoveryInspector
    {
        NvencRunPublicationRecoveryInspectionSnapshot Inspect(
            NvencRunPublicationRecoveryInspectionOperation operation);
    }
}
