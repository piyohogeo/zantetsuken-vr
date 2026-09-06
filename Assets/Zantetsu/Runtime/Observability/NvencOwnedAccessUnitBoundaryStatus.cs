namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of one owned Access Unit boundary operation on
    /// <see cref="NvencOwnedAccessUnitBuffer"/>. It separates a completed
    /// operation, a deferred commit behind a transient gate, a busy gate that
    /// must be retried, and an invalid lease or state that is an ownership
    /// break.
    /// </summary>
    internal enum NvencOwnedAccessUnitBoundaryStatus
    {
        /// <summary>The operation completed and the out parameters are valid.</summary>
        Ready,

        /// <summary>The external consumer returned but the post-return gate was
        /// contended; the caller parks the outcome and commits it later.</summary>
        Deferred,

        /// <summary>The gate was held or the process is poisoned; retry later.</summary>
        Busy,

        /// <summary>The lease, phase, or content state is invalid; an ownership
        /// break that must poison, never a controllable path.</summary>
        Invalid,
    }
}
