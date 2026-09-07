using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless Phase 0.11 Run chunk finalization operation factory. It
    /// delegates exactly once to
    /// <see cref="NvencRunChunkFinalizationOperation.Create"/> after the
    /// object null checks; it performs no evidence re-issuance, no frame
    /// relation copy, and no descriptor generation.
    /// </summary>
    /// <remarks>
    /// This type holds no state and is not an <see cref="IDisposable"/>.
    /// </remarks>
    internal static class NvencRunChunkFinalizationOperationFactory
    {
        internal static NvencRunChunkFinalizationOperation Build(
            NvencRunChunkSink sink,
            NvencRunChunkSinkFinalizationEvidence evidence,
            string artifactId)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            return NvencRunChunkFinalizationOperation.Create(sink, evidence, artifactId);
        }
    }
}
