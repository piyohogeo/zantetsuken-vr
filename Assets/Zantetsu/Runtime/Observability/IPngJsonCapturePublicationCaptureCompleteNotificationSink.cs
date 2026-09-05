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
    /// conflict detection belong. <see cref="Accept"/> is synchronous and a
    /// notifier calls it exactly once per notification and performs no retry;
    /// the sink must not mutate the supplied identity.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationCaptureCompleteNotificationSink
    {
        PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accept(
            PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity);
    }
}
