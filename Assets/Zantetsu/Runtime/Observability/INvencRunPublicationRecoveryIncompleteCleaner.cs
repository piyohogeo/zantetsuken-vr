namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC publication recovery orphan cleanup
    /// boundary: one call is one attempt to clean up after a recovered Run
    /// whose publication was classified incomplete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and an invalid one throws
    /// <see cref="System.ArgumentException"/>, both before any side effect. A
    /// finished cleanup and a known cleanup failure are both reported through
    /// the attempt result; a programming error, a broken contract, and any
    /// unexpected exception are neither caught nor converted, and propagate.
    /// </para>
    /// <para>
    /// The attempt is single: no retry inside one call, no rollback, no
    /// reclassification, and no fallback to some other path. Because an orphan
    /// cleanup removes more than one thing, a failure can leave part of the
    /// work done; the result says only Cleaned or Failed, and what remains is
    /// decided by a later recovery re-observing the real filesystem while newly
    /// holding the Run's lock.
    /// </para>
    /// <para>
    /// An implementation changes and releases nothing in the operation's
    /// authority graph - not the operation, the publication recovery decision,
    /// its snapshot, the initialization open outcome, or the Session Ownership
    /// Lease - touches no Registry, disposition, Service, or process state, and
    /// owns no thread, queue, task, wait primitive, or lease.
    /// </para>
    /// <para>
    /// This interface deliberately fixes neither the deletion targets, the
    /// deletion order, the no-follow entry points, the flush policy, nor what a
    /// partial deletion means in detail: those belong to the production
    /// cleaner.
    /// </para>
    /// </remarks>
    internal interface INvencRunPublicationRecoveryIncompleteCleaner
    {
        NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation);
    }
}
