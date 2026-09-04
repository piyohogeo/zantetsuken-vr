using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous, single-attempt side-effect boundary that durably publishes
    /// the staged artifacts of one execution batch to their final paths under
    /// an execution-wide reservation, and must never overwrite an existing
    /// destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryBegin"/> opens one call-scoped reservation for the whole
    /// publish workload of the supplied batch. It validates the batch and
    /// token, reserves the publish resources for that batch before any side
    /// effect, and returns an opaque
    /// <see cref="IPngJsonCapturePublicationArtifactPublishAttempt"/> — or
    /// <c>null</c> when the reservation cannot be obtained. It performs no
    /// filesystem change, no publish, and no retry.
    /// </para>
    /// <para>
    /// <see cref="PublishReserved"/> publishes one reserved artifact inside an
    /// already-opened attempt. It validates the exact attempt, operation, and
    /// token before any side effect, reuses the same attempt for every publish
    /// step of the execution, and never acquires an additional reservation. It
    /// throws <see cref="ArgumentNullException"/> when the attempt, operation,
    /// or token is <c>null</c>, and <see cref="ArgumentException"/> when the
    /// operation is not correlated with the supplied token through
    /// <see cref="PngJsonCapturePublicationArtifactPublishOperation.IsValidIndexLocal"/>
    /// or when the attempt is foreign. All such failures are filesystem-free
    /// and never produce a receipt.
    /// </para>
    /// <para>
    /// The publisher must validate with
    /// <see cref="PngJsonCapturePublicationArtifactPublishOperation.IsValidIndexLocal"/>
    /// only: it must never re-validate the whole action plan and never re-issue
    /// a validation token, but must use the exact token passed by the
    /// coordinator.
    /// </para>
    /// <para>
    /// A receipt is returned only after the staged artifact has been durably
    /// published no-follow and non-overwriting, with the source byte length and
    /// content re-confirmed against the operation's expected values, and the
    /// published destination re-verified. On success the staging source is
    /// moved to the final path without overwriting an existing destination, so
    /// the source may be consumed; if the move fails before the rename, the
    /// source is left unchanged.
    /// </para>
    /// <para>
    /// An existing destination is never treated as success, even when its
    /// content matches, because the inspection may be stale; the caller must
    /// fail closed and return to re-inspection under the held lock. If an
    /// exception occurs after the destination becomes visible, blindly retrying
    /// the same operation is forbidden and re-inspection is mandatory.
    /// </para>
    /// <para>
    /// <see cref="End"/> releases the exact attempt once. It performs no
    /// filesystem change, no publish, and no retry, and may reject a
    /// double-end. The publisher must not mutate or dispose the operation,
    /// action plan, decision, snapshot, canonical bytes, or owner; must not
    /// retain the operation or any input-derived reference beyond the returned
    /// receipt; and holds no responsibility for releasing the lock owner. An
    /// exception raised by the implementation propagates unchanged and never
    /// produces a receipt.
    /// </para>
    /// <para>
    /// This interface neither inherits nor changes the existing
    /// <see cref="ICaptureRunPublicationArtifactPublisher"/> boundary, and the
    /// durability, non-overwrite, no-follow, and post-failure re-inspection
    /// guarantees are preserved here against the PngJson operation types.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationArtifactPublisher
    {
        IPngJsonCapturePublicationArtifactPublishAttempt TryBegin(
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

        PngJsonCapturePublicationArtifactPublishReceipt PublishReserved(
            IPngJsonCapturePublicationArtifactPublishAttempt attempt,
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

        void End(IPngJsonCapturePublicationArtifactPublishAttempt attempt);
    }
}
