namespace Zantetsu.Observability
{
    /// <summary>
    /// Read-only boundary that observes one Capture Run's publication state
    /// under the held lock and returns an immutable snapshot of the observed
    /// state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Inspect"/> is one synchronous read-only pass over the
    /// operation's exact instance and returns a snapshot that corresponds only
    /// to that operation and is issued by this inspector. Direct enumeration
    /// of each root is bounded to <c>RootEntryProbeCount</c> entries; exceeding
    /// the bound is recorded in the snapshot and enumeration stops. Document
    /// reads are bounded to each limit plus one byte, and canonical documents
    /// are decoded only through the existing
    /// <c>PngJsonCapturePublicationPlanCodec.DeserializeCanonical</c>. Any stream the
    /// inspector opens is owned by the inspector and is always closed; no
    /// stream, handle, or operation is retained in backend fields, queues, or
    /// caches.
    /// </para>
    /// <para>
    /// A null operation is rejected with an
    /// <see cref="System.ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any root is touched. An operation whose
    /// <c>IsValid</c> is false is rejected with an
    /// <see cref="System.ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any root is touched. Neither rejection reads
    /// or enumerates a root, document, or payload.
    /// </para>
    /// <para>
    /// The inspector never creates, deletes, renames, or repairs a root,
    /// document, or payload; never acquires or releases a lock; never treats a
    /// no-follow or identity check it cannot guarantee as success; and never
    /// converts backend exceptions into a retry, fallback, or alternate-path
    /// observation. Thread selection for blocking I/O is the caller's
    /// responsibility.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunPublicationRecoveryInspector
    {
        CaptureRunPublicationRecoveryInspectionSnapshot Inspect(
            CaptureRunPublicationRecoveryInspectionOperation operation);
    }
}
