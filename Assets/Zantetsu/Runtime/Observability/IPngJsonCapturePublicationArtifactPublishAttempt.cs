namespace Zantetsu.Observability
{
    /// <summary>
    /// Opaque, call-scoped reservation handle minted by a publisher's
    /// <c>TryBegin</c> for one execution-wide publish attempt and released by
    /// <c>End</c>. It carries no identity, bytes, path, lease, token, or
    /// reservation state of its own; it is only a typed capability the exact
    /// publisher recognizes in <c>PublishReserved</c> and <c>End</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt is intentionally an empty marker: no common base class,
    /// nonce, generation, registry, or global state is attached. The concrete
    /// publisher owns the reservation it represents and must reject a foreign
    /// attempt before any side effect.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationArtifactPublishAttempt
    {
    }
}
