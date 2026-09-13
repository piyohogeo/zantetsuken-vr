using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one capture-complete notification: which
    /// notifier issued it and which notification operation it accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type holds exactly two read-only reference fields — the issuing
    /// notifier and the notification operation — and has no public
    /// constructor. The constructor rejects a null issuer with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>issuedBy</c>, a null operation with
    /// <see cref="ArgumentNullException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, and an invalid operation with
    /// <see cref="ArgumentException"/> whose <c>ParamName</c> is
    /// <c>operation</c>, storing the two references only after every check
    /// succeeds.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks without throwing. What was notified is read from
    /// <see cref="Operation"/>; the receipt restates none of it and holds
    /// nothing beyond the notifier and that operation.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteNotificationReceipt
    {
        private readonly ICaptureRunPublicationCaptureCompleteNotifier _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteNotificationOperation _operation;

        internal CaptureRunPublicationCaptureCompleteNotificationReceipt(
            ICaptureRunPublicationCaptureCompleteNotifier issuedBy,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

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

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunPublicationCaptureCompleteNotifier IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteNotificationOperation Operation => _operation;

        internal bool IsValid
        {
            get
            {
                return _issuedBy != null && _operation != null && _operation.IsValid;
            }
        }

        internal bool IsIssuedFor(
            ICaptureRunPublicationCaptureCompleteNotifier notifier,
            CaptureRunPublicationCaptureCompleteNotificationOperation operation)
        {
            return notifier != null
                && operation != null
                && ReferenceEquals(_issuedBy, notifier)
                && ReferenceEquals(_operation, operation)
                && _operation.IsValid;
        }
    }
}
