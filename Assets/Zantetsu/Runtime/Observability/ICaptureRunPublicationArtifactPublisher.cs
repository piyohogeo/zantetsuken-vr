using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous, single-attempt boundary that durably publishes one staging
    /// artifact to its final path and must never overwrite an existing
    /// destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Publish"/> throws <see cref="ArgumentNullException"/> when
    /// <paramref name="operation"/> is <c>null</c> and
    /// <see cref="ArgumentException"/> when it is not
    /// <see cref="CaptureRunPublicationArtifactPublishOperation.IsValid"/>.
    /// Both failures are filesystem-free.
    /// </para>
    /// <para>
    /// A receipt is returned only after every one of the following succeeded:
    /// the exclusive lock remains valid; the source and destination are
    /// handled no-follow with their ancestry identities re-confirmed; the
    /// source is a regular file whose byte length matches
    /// <c>ExpectedByteCount</c> and whose content SHA-256 matches
    /// <c>ExpectedContentSha256</c> Ordinal; the destination does not exist;
    /// the destination becomes visible atomically and non-overwriting in the
    /// final-path directory; the destination data and directory metadata are
    /// durably flushed; and the published destination is re-verified to match
    /// the expected length and hash. The source is never deleted, renamed, or
    /// modified.
    /// </para>
    /// <para>
    /// An existing destination is never treated as success in this call, even
    /// when its content matches, because the inspection may be stale; the
    /// caller must fail closed and return to re-inspection under the held
    /// lock. If an exception occurs after the destination becomes visible
    /// (during flush or re-verification), the destination may exist; blindly
    /// retrying the same operation is forbidden and re-inspection is
    /// mandatory.
    /// </para>
    /// <para>
    /// The staging and final roots may reside on different volumes, so a
    /// direct atomic rename from source to destination is never assumed. A
    /// backend that cannot satisfy these guarantees must not return a receipt.
    /// </para>
    /// <para>
    /// This call is synchronous and single-attempt: it performs no retry, no
    /// fallback, and no alternative-path publication. The publisher must not
    /// mutate or dispose the operation, plan, observation, or path set; must
    /// not retain the operation or any input-derived reference in its own
    /// fields, queues, or caches (transient references during the call and the
    /// returned receipt's operation reference are allowed); must not contact
    /// the run registration, draft, or trace stores, or Unity Objects; and
    /// leaves the choice of execution thread to the upper layer.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunPublicationArtifactPublisher
    {
        CaptureRunPublicationArtifactPublishReceipt Publish(
            CaptureRunPublicationArtifactPublishOperation operation);
    }
}
