using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous, single-attempt side-effect boundary that durably publishes
    /// one <see cref="PngJsonCapturePublicationArtifactPublishOperation"/>'s
    /// staged artifact to its final path and must never overwrite an existing
    /// destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Publish"/> throws <see cref="ArgumentNullException"/> when
    /// <paramref name="operation"/> or <paramref name="token"/> is <c>null</c>,
    /// and <see cref="ArgumentException"/> when the operation is not correlated
    /// with the supplied token through
    /// <see cref="PngJsonCapturePublicationArtifactPublishOperation.IsValidIndexLocal"/>.
    /// All such failures are filesystem-free and never produce a receipt.
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
    /// published destination re-verified. The source is never deleted, renamed,
    /// or modified.
    /// </para>
    /// <para>
    /// An existing destination is never treated as success, even when its
    /// content matches, because the inspection may be stale; the caller must
    /// fail closed and return to re-inspection under the held lock. If an
    /// exception occurs after the destination becomes visible, blindly retrying
    /// the same operation is forbidden and re-inspection is mandatory.
    /// </para>
    /// <para>
    /// This call is synchronous and single-attempt: it performs no retry, no
    /// rollback, no cleanup, no re-inspection, and no alternative-path
    /// fallback. The publisher must not mutate or dispose the operation, action
    /// plan, decision, snapshot, canonical bytes, or owner; must not retain the
    /// operation or any input-derived reference beyond the returned receipt;
    /// and holds no responsibility for releasing the lock owner. An exception
    /// raised by the implementation propagates unchanged and never produces a
    /// receipt.
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
        PngJsonCapturePublicationArtifactPublishReceipt Publish(
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
    }
}
