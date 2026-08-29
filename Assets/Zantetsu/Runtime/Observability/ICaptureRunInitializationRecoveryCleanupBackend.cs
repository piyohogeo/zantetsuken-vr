namespace Zantetsu.Observability
{
    /// <summary>
    /// Boundary that executes one validated cleanup operation against the
    /// actual filesystem: deleting exactly one non-authoritative temporary
    /// marker entry or removing exactly one empty Run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Execute"/> rejects a null operation with an
    /// <see cref="System.ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, and an invalid operation with an
    /// <see cref="System.ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, both before any filesystem contact. It performs one
    /// synchronous attempt with no retry or fallback, and never modifies,
    /// retains, or disposes the operation, plan, path set, or lease. Only a
    /// success receipt may hold the operation reference. Choosing the worker
    /// or main thread is the caller's responsibility.
    /// </para>
    /// <para>
    /// For DeleteMarkerTemporary the backend deletes exactly the target
    /// temporary marker entry with no-follow semantics and without touching the
    /// canonical marker, another temporary, or any payload, then durably
    /// flushes the parent directory metadata. For RemoveEmptyRoot it
    /// re-verifies the exact Run root and removes only an empty directory
    /// non-recursively, then durably flushes the trusted base directory
    /// metadata. A missing target, a non-empty root, or a reparse or identity
    /// mismatch is not treated as success, and no receipt is returned after a
    /// flush failure. After an exception the backend must not blindly retry
    /// the same operation; the caller re-inspects under the held lock. A
    /// backend that cannot guarantee these conditions must not return a
    /// success receipt.
    /// </para>
    /// <para>
    /// The returned receipt must be issued by this backend itself and must
    /// hold the exact <c>operation</c> argument, so the coordinator can reject
    /// a foreign issuer or a mismatched operation fail-closed. A backend must
    /// never return a null receipt.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunInitializationRecoveryCleanupBackend
    {
        CaptureRunInitializationRecoveryCleanupReceipt Execute(
            CaptureRunInitializationRecoveryCleanupOperation operation);
    }
}
