namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Fresh NVENC Session Ownership Lease releaser: one call is
    /// one release attempt on the operation's exact lease, and a successful
    /// return is the only thing that issues a receipt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws <see cref="System.ArgumentNullException"/>
    /// before any side effect. An operation that is not admissible - a
    /// completed release, a broken binding, or a first attempt whose admission
    /// validity is gone, for example because the process was poisoned - throws
    /// <see cref="System.ArgumentException"/>, also before any side effect. A
    /// retry after a partial release failure is admissible even though the
    /// operation's admission validity is necessarily false by then, because a
    /// partially released lease is no longer fully retained. First attempt and
    /// retry are told apart from the lease's own state; no latch is introduced.
    /// </para>
    /// <para>
    /// The attempt calls <c>Dispose</c> on the operation's exact Session
    /// Ownership Lease exactly once. A failure propagates the original
    /// exception by reference and issues no receipt: a partial release is not
    /// folded into a failed result, because the caller must be able to see the
    /// lease's own state and retry. There is no retry, rollback, lock
    /// re-acquisition, filesystem inspection, Registry change, or disposition
    /// change here. After a normal return the lease's release is complete and
    /// no further attempt is possible.
    /// </para>
    /// </remarks>
    internal interface INvencRunSessionOwnershipReleaser
    {
        NvencRunSessionOwnershipReleaseReceipt Release(
            NvencRunSessionOwnershipReleaseOperation operation);
    }
}
