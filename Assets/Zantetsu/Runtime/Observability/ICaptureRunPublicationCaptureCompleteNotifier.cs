using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Contract for notifying a downstream lifecycle coordinator that a capture
    /// run publication capture-complete cleanup reached
    /// <c>CaptureCompleteReady</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A notifier implementation must honor the following contract:
    /// <list type="bullet">
    /// <item>A null operation is rejected with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>.</item>
    /// <item>An invalid operation is rejected with
    /// <see cref="ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>.</item>
    /// <item>The notifier never contacts any external notification target
    /// before validation completes.</item>
    /// <item>Notification is synchronous and single-attempt; the notifier
    /// performs no internal retry.</item>
    /// <item>Re-notification of an already accepted identity is
    /// idempotent.</item>
    /// <item>A different initialization id, manifest hash, or index path for
    /// the same test run id is a hard failure (identity conflict).</item>
    /// <item>A receipt is returned only when the notification was durably
    /// accepted or the same identity was already accepted.</item>
    /// <item>No receipt is returned on any exception.</item>
    /// <item>The notifier never mutates, retains, or disposes the operation,
    /// any result, or the lease.</item>
    /// <item>The notifier never contacts the draft registry, the filesystem
    /// cleanup backend, the capture index, markers, or the OS lock.</item>
    /// <item>Notification time and newly generated ids are never mixed into
    /// the completion identity.</item>
    /// <item>Choosing the execution thread is the caller's responsibility.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The stable notification identity is the tuple of test run id, run
    /// initialization id, run manifest content SHA-256, and capture index
    /// path, compared ordinally.
    /// </para>
    /// <para>
    /// On success the returned receipt is always non-null and satisfies
    /// <c>ReferenceEquals(receipt.IssuedBy, this)</c>,
    /// <c>ReferenceEquals(receipt.Operation, operation)</c>, and
    /// <c>receipt.IsIssuedFor(this, operation)</c>. A downstream lifecycle
    /// coordinator must immediately reject a null, foreign-issuer, or
    /// different-operation receipt.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunPublicationCaptureCompleteNotifier
    {
        CaptureRunPublicationCaptureCompleteNotificationReceipt Notify(
            CaptureRunPublicationCaptureCompleteNotificationOperation operation);
    }
}
