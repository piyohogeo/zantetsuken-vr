namespace Zantetsu.Observability
{
    /// <summary>
    /// Instance-scoped authority that reports whether the GPU conversion pass
    /// has finished reading the source surface of one accepted submission. The
    /// caller supplies a <see cref="NvencSubmissionRecord"/> and the source
    /// returns, without waiting or allocating, a
    /// <see cref="NvencSourceReadCompletedEvidence"/> bound to the exact work
    /// when the read has completed, or <c>false</c> when it has not. The source
    /// owns no global hook, registry, nonce, or static mutable state and is
    /// injected per instance.
    /// </summary>
    internal interface INvencSourceReadCompletedSource
    {
        bool TryGetEvidence(
            in NvencSubmissionRecord record,
            out NvencSourceReadCompletedEvidence evidence);
    }
}
