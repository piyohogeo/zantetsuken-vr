namespace Zantetsu.Observability
{
    /// <summary>
    /// Read-only boundary that observes a Capture Run's two roots under the
    /// held lock and returns an immutable snapshot of the observed state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Inspect"/> is one synchronous read-only pass over the
    /// operation's exact instance and returns a snapshot that corresponds only
    /// to that operation. Direct enumeration of each root is bounded to
    /// <c>MaximumRootEntryCount + 1</c> entries; exceeding the bound is
    /// recorded in the observation and enumeration stops. Markers are decoded
    /// only through the existing bounded decoder. No stream, handle, or
    /// operation is retained in backend fields, queues, or caches.
    /// </para>
    /// <para>
    /// A null operation is rejected with an
    /// <see cref="System.ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any root is touched. An operation whose
    /// <c>IsValid</c> is false is rejected with an
    /// <see cref="System.ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any root is touched. Neither rejection reads
    /// or enumerates a root, marker, or payload.
    /// </para>
    /// <para>
    /// The inspector never creates, deletes, renames, or repairs a root,
    /// temporary entry, marker, or payload; never acquires or releases a lock;
    /// never treats a no-follow or identity check it cannot guarantee as
    /// success; and never converts backend exceptions into a retry, fallback,
    /// or alternate-path observation. Thread selection is the caller's
    /// responsibility.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunInitializationRecoveryInspector
    {
        CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
            CaptureRunInitializationRecoveryInspectionOperation operation);
    }
}
