namespace Zantetsu.Observability
{
    /// <summary>
    /// Boundary that executes one validated PngJson capture-complete cleanup
    /// operation against the actual filesystem: deleting exactly the fixed
    /// target of a single cleanup step under the held recovery lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Execute"/> rejects a null operation or token before any
    /// filesystem contact, then requires
    /// <see cref="PngJsonCapturePublicationCaptureCompleteCleanupOperation.IsValidIndexLocal(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken)"/>
    /// once before the side effect. It performs one synchronous attempt with
    /// no retry, fallback, alternate-path deletion, recursive deletion,
    /// rollback, or re-inspection. It never modifies, retains, or disposes the
    /// operation, plan, token, or owner. Only a success receipt may hold the
    /// operation and token references. Choosing the worker or main thread is
    /// the caller's responsibility.
    /// </para>
    /// <para>
    /// Every deletion is an exact-path, no-follow, regular-file or
    /// directory-only operation that re-verifies identity before touching
    /// anything. A reparse point, symlink, junction, or identity substitution
    /// is a hard failure, never a success. A missing target is never treated
    /// as success for the same operation; resumption after partial success is
    /// the responsibility of a fresh inspection and a new action plan. After a
    /// successful deletion the backend durably flushes the parent directory
    /// metadata; on flush failure no receipt is returned.
    /// </para>
    /// <para>
    /// For <c>DeleteStagingArtifact</c> the backend deletes exactly the target
    /// staging file, re-verifying that it is a regular file whose byte length
    /// and SHA-256 match
    /// <see cref="PngJsonCapturePublicationCaptureCompleteCleanupOperation.ExpectedByteCount"/>
    /// and
    /// <see cref="PngJsonCapturePublicationCaptureCompleteCleanupOperation.ExpectedContentSha256"/>,
    /// and never changes the final artifact, another entry, or another
    /// artifact kind. For <c>DeletePublicationPlanTemporary</c> it deletes the
    /// exact temporary publication plan only when it is canonical and matches
    /// the authoritative plan. For <c>DeleteCaptureIndexTemporary</c> it
    /// deletes the exact temporary capture index only when a canonical final
    /// capture index exists and the temporary matches the same authoritative
    /// plan. For <c>DeletePublicationPlan</c> it re-confirms the durable
    /// canonical capture index and deletes the publication plan only when it
    /// matches the authoritative plan. For <c>RemoveStagingFramesRoot</c> it
    /// re-verifies that the exact staging frames directory is empty and
    /// removes it non-recursively. For <c>DeleteStagingReadyMarker</c> it
    /// deletes exactly the staging <c>run.ready</c> marker after re-verifying
    /// its test run ID, run initialization ID, and peer binding. For
    /// <c>DeleteStagingInitializationMarker</c> it first confirms the ready
    /// marker is already absent, then re-verifies the <c>run.init</c> marker's
    /// test run ID, run initialization ID, and root role/root hash binding to
    /// the exact root layout, and deletes exactly that marker. For
    /// <c>RemoveStagingRunRoot</c> it confirms the
    /// staging initialization marker, ready marker, publication plan, and
    /// frames root are all absent, verifies the exact staging run root is
    /// empty, removes it non-recursively, and durably flushes the trusted base
    /// directory metadata.
    /// </para>
    /// <para>
    /// A backend that cannot guarantee these conditions must not return a
    /// success receipt.
    /// </para>
    /// <para>
    /// The returned receipt must be non-null, must be issued by this backend
    /// itself (<c>ReferenceEquals(receipt.IssuedBy, this)</c>), must hold the
    /// exact <c>operation</c> argument and the exact <c>token</c> argument, and
    /// must report <c>receipt.IsIssuedFor(this, operation, token) == true</c>.
    /// A subsequent coordinator rejects a null receipt, a foreign issuer, a
    /// mismatched operation, or a different token fail-closed.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationCaptureCompleteCleanupBackend
    {
        PngJsonCapturePublicationCaptureCompleteCleanupReceipt Execute(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token);
    }
}
