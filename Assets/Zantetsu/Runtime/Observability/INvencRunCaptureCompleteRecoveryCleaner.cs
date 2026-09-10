namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC recovery CaptureComplete cleanup boundary:
    /// one call is one attempt to clean up after a recovered Run whose
    /// CaptureComplete has been accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and an invalid one throws
    /// <see cref="System.ArgumentException"/>, both before any side effect.
    /// The attempt is single: no retry, rollback, fallback, or cleanup of some
    /// other path. Because a cleanup may delete more than one thing, a failure
    /// can leave part of the work done; the result says only Cleaned or
    /// Failed, and what remains is decided by the next recovery re-observing
    /// the filesystem under the lock this process still holds.
    /// </para>
    /// <para>
    /// An implementation changes and releases nothing in the operation's
    /// authority graph, releases no OS lock, and owns no thread, queue, task,
    /// wait, or lease. The published final chunk and the authoritative
    /// <c>capture.index</c> are never modified, and no Registry, disposition,
    /// Service, or process state is touched.
    /// </para>
    /// <para>
    /// This interface deliberately fixes neither the deletion order, the exact
    /// targets, the Win32 entry points, nor any flush policy: those belong to
    /// the production cleaner.
    /// </para>
    /// </remarks>
    internal interface INvencRunCaptureCompleteRecoveryCleaner
    {
        NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation);
    }
}
