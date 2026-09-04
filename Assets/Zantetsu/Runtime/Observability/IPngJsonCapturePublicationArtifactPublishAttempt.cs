namespace Zantetsu.Observability
{
    /// <summary>
    /// Opaque capability minted by a publisher's <c>TryBegin</c> for one
    /// execution-wide publish attempt and released by <c>End</c>. The
    /// interface exposes no common members and no state; it is only a typed
    /// capability the exact publisher recognizes in <c>PublishReserved</c> and
    /// <c>End</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt is intentionally an empty marker: no common base class,
    /// nonce, generation, registry, or global state is attached, and no
    /// reservation, buffer, or lease is exposed through the interface. A
    /// concrete attempt implementation may privately hold the exact
    /// publisher/batch/token correlation, the underlying reservation, and its
    /// terminal state; the concrete publisher body must not retain any
    /// call-scoped active attempt or reservation mapping. <c>PublishReserved</c>
    /// and <c>End</c> are responsible for validating the concrete attempt, and
    /// must reject a foreign attempt before any side effect.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationArtifactPublishAttempt
    {
    }
}
