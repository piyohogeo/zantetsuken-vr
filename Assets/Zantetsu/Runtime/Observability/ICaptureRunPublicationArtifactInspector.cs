namespace Zantetsu.Observability
{
    /// <summary>
    /// Read-only boundary that observes every artifact of one authoritative
    /// publication plan under the held lock and returns an immutable snapshot
    /// of the observed evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Inspect"/> is one synchronous, single-attempt, read-only pass
    /// over the operation's exact instance and returns a snapshot that is
    /// issued by this inspector and holds that same operation.
    /// </para>
    /// <para>
    /// A null operation is rejected with an
    /// <see cref="System.ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any artifact is touched. An operation whose
    /// <c>IsValid</c> is false is rejected with an
    /// <see cref="System.ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, before any artifact is touched.
    /// </para>
    /// <para>
    /// A compliant backend reads each PNG with a no-follow check limited to a
    /// regular file, probing at most <c>min(expected byte length, MaximumPngByteCount) + 1</c>
    /// bytes and verifying the SHA-256. Each sidecar is probed at most
    /// <c>64 KiB + 1</c> bytes and decoded through the existing codec, with its
    /// record, capture frame ID, PNG hash, and manifest reference correlated
    /// to the plan. The trace manifest is probed at most <c>64 KiB + 1</c>
    /// bytes and decoded through the existing codec, with its test run ID and
    /// canonical manifest SHA-256 correlated to the operation. Any stream or
    /// hash object the inspector opens is disposed on every path; no stream,
    /// handle, or operation is retained in backend fields, queues, or caches.
    /// </para>
    /// <para>
    /// The inspector never creates, deletes, renames, or repairs an artifact;
    /// never acquires or releases a lock; never performs a retry, fallback, or
    /// repair of an observation; and never contacts Unity APIs, the registry,
    /// the draft layer, or the trace logger. Thread selection for blocking I/O
    /// is the caller's responsibility.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunPublicationArtifactInspector
    {
        CaptureRunPublicationArtifactInspectionSnapshot Inspect(
            CaptureRunPublicationArtifactInspectionOperation operation);
    }
}
