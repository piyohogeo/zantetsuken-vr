namespace Zantetsu.Observability
{
    /// <summary>
    /// Boundary that executes one validated capture-complete cleanup operation
    /// against the actual filesystem: deleting exactly the fixed target of a
    /// single cleanup step under the held recovery lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Execute"/> rejects a null operation with an
    /// <see cref="System.ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, and an invalid operation with an
    /// <see cref="System.ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, both before any filesystem contact. It performs one
    /// synchronous attempt with no retry, fallback, alternate-path deletion,
    /// recursive deletion, or rollback. It never modifies, retains, or
    /// disposes the operation, plan, snapshot, path set, or lease. Only a
    /// success receipt may hold the operation reference. Choosing the worker
    /// or main thread is the caller's responsibility.
    /// </para>
    /// <para>
    /// Every deletion is an exact-path, no-follow, regular-file or
    /// directory-only operation that re-verifies identity before touching
    /// anything. A reparse point, symlink, junction, or identity substitution
    /// is a hard failure, never a success. After a successful deletion the
    /// backend durably flushes the parent directory metadata; on flush failure
    /// no receipt is returned. After an exception the backend must not blindly
    /// retry the same operation; the caller re-inspects under the held lock
    /// and builds a new action plan.
    /// </para>
    /// <para>
    /// For <c>DeleteStagingArtifact</c> the backend deletes exactly the target
    /// staging file, re-verifying that it is a regular file whose byte length
    /// and SHA-256 match the plan entry expectation, and never changes the
    /// final artifact, another entry, or another artifact kind. For
    /// <c>DeletePublicationPlanTemporary</c> it deletes the exact temporary
    /// publication plan only when it is canonical and matches the
    /// authoritative plan. For <c>DeleteCaptureIndexTemporary</c> it deletes
    /// the exact temporary capture index only when a canonical final capture
    /// index exists and the temporary matches the same authoritative plan. For
    /// <c>DeletePublicationPlan</c> it re-confirms the durable canonical
    /// capture index and deletes the publication plan only when it matches the
    /// authoritative plan. For <c>RemoveStagingFramesRoot</c> it re-verifies
    /// that the exact staging frames directory is empty and removes it
    /// non-recursively. For <c>DeleteStagingReadyMarker</c> it deletes exactly
    /// the staging <c>run.ready</c> marker after re-verifying its test run ID,
    /// run initialization ID, and peer binding. For
    /// <c>DeleteStagingInitializationMarker</c> it first confirms the ready
    /// marker is already absent, then deletes exactly the staging
    /// <c>run.init</c> marker after re-verifying its binding to the root
    /// layout. For <c>RemoveStagingRunRoot</c> it confirms the staging
    /// initialization marker, ready marker, publication plan, and frames root
    /// are all absent, verifies the exact staging run root is empty, removes
    /// it non-recursively, and durably flushes the trusted base directory
    /// metadata. A missing target is never treated as success for the same
    /// operation; resumption after partial success is the responsibility of a
    /// fresh inspection and a new action plan.
    /// </para>
    /// <para>
    /// A backend that cannot guarantee these conditions must not return a
    /// success receipt.
    /// </para>
    /// <para>
    /// The returned receipt must be non-null, must be issued by this backend
    /// itself (<c>ReferenceEquals(receipt.IssuedBy, this)</c>), must hold the
    /// exact <c>operation</c> argument
    /// (<c>ReferenceEquals(receipt.Operation, operation)</c>), and must report
    /// <c>receipt.IsIssuedFor(this, operation) == true</c>. A subsequent
    /// coordinator rejects a null receipt, a foreign issuer, or a mismatched
    /// operation fail-closed.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunPublicationCaptureCompleteCleanupBackend
    {
        CaptureRunPublicationCaptureCompleteCleanupReceipt Execute(
            CaptureRunPublicationCaptureCompleteCleanupOperation operation);
    }
}
