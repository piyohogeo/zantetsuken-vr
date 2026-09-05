using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of one PngJson capture-complete notification acceptance by an
    /// external notification sink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is the uninitialized value and is never treated as
    /// success. <see cref="Accepted"/> means the sink durably accepted this
    /// notification identity; <see cref="AlreadyAccepted"/> means the identical
    /// identity was already accepted and the notification is idempotently
    /// complete; <see cref="IdentityConflict"/> means the same test run id
    /// arrived with a different initialization id, run manifest content
    /// SHA-256, or capture index path; <see cref="Rejected"/> means the sink
    /// refused the notification for any other reason.
    /// </para>
    /// </remarks>
    internal enum PngJsonCapturePublicationCaptureCompleteNotificationAcceptance
    {
        None = 0,
        Accepted = 1,
        AlreadyAccepted = 2,
        IdentityConflict = 3,
        Rejected = 4
    }
}
