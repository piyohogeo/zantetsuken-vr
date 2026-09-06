namespace Zantetsu.Observability
{
    /// <summary>
    /// A pre-opened Run chunk writer exposed as a single synchronous append
    /// boundary. It owns no filesystem open, close, rename, finalize, or
    /// descriptor duty; it only appends the given span of a caller-provided
    /// buffer and reports one <see cref="NvencRunChunkAppendOutcome"/>.
    /// </summary>
    /// <remarks>
    /// The buffer reference is valid only for the duration of the call and
    /// must never be stored. A partial write or unknown result must be
    /// reported as <see cref="NvencRunChunkAppendOutcome.Indeterminate"/>
    /// rather than being silently converted into a controlled failure.
    /// </remarks>
    internal interface INvencRunChunkAppender
    {
        NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength);
    }
}
