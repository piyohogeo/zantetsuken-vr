using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Minimal external notification port: synchronously accept one immutable
    /// capture-complete notification identity and report a terminal acceptance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sink is the single seam where persistence, idempotency, and identity
    /// conflict detection belong. <see cref="Accept"/> is synchronous and is
    /// called exactly once per notification by a notifier; the sink must not
    /// mutate the supplied identity and must not retry internally.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationCaptureCompleteNotificationSink
    {
        PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accept(
            PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity);
    }
}
