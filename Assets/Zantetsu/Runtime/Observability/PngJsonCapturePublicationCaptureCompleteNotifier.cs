using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Concrete PngJson capture-complete notifier: converts one notification
    /// operation into one immutable identity and hands it to a single held
    /// sink exactly once, issuing a receipt only when the sink accepted the
    /// identity or the identical identity was already accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notifier owns exactly one read-only field — the external sink — and
    /// holds no operation, receipt, lease, token, accepted-identity table,
    /// dictionary, or global state. It performs no retry, rollback, cleanup
    /// re-run, capture index or marker re-read, filesystem operation, OS lock
    /// or ownership-lease release, identity persistence, or thread/task/queue
    /// creation.
    /// </para>
    /// <para>
    /// <see cref="Notify"/> runs the fixed sequence: reject a null operation,
    /// require the operation to be currently valid, build the notification
    /// identity once, call the sink exactly once, treat only
    /// <see cref="PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.Accepted"/>
    /// and
    /// <see cref="PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.AlreadyAccepted"/>
    /// as success, issue the existing receipt factory on success only, and
    /// otherwise throw <see cref="InvalidOperationException"/> with no receipt.
    /// Sink exceptions propagate unchanged and unwrapped.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteNotifier : IPngJsonCapturePublicationCaptureCompleteNotifier
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteNotificationSink _sink;

        internal PngJsonCapturePublicationCaptureCompleteNotifier(
            IPngJsonCapturePublicationCaptureCompleteNotificationSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public PngJsonCapturePublicationCaptureCompleteNotificationReceipt Notify(
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException(
                    "Notification operation must be valid.",
                    nameof(operation));
            }

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);

            PngJsonCapturePublicationCaptureCompleteNotificationAcceptance acceptance = _sink.Accept(identity);

            switch (acceptance)
            {
                case PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.Accepted:
                case PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.AlreadyAccepted:
                    return PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(this, operation);

                case PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.IdentityConflict:
                case PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.Rejected:
                case PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.None:
                    throw new InvalidOperationException(
                        "Capture-complete notification was not accepted (acceptance = " + acceptance + ").");

                default:
                    throw new InvalidOperationException(
                        "Capture-complete notification returned an undefined acceptance value ("
                        + (int)acceptance + ").");
            }
        }
    }
}
