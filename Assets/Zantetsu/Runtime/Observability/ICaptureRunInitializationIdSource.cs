namespace Zantetsu.Observability
{
    /// <summary>
    /// Issues a Capture Run initialization ID. Each call returns exactly one
    /// valid 32-character lowercase hexadecimal identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations must never return null or a malformed identifier, must
    /// not retry, fall back, or cache, and must perform no filesystem,
    /// logging, registration, draft, or trace access. This boundary exists so
    /// bootstrap tests can verify that the identifier is issued exactly once,
    /// after both locks are acquired.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunInitializationIdSource
    {
        string Create();
    }
}
