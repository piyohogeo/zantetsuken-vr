using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable StopAccepting snapshot of a Run chunk's accepted Capture Frame
    /// Id sequence. It is bound by reference to the exact
    /// <see cref="NvencRunChunkContext"/> and to the frozen accepted count at
    /// freeze time, so the terminal boundary can prove the accepted sequence
    /// was frozen and cannot have changed while a terminal request is admitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot duplicates no frame-id array, holds no token, nonce,
    /// generation, lease, queue, writer, filesystem, or process-state
    /// reference, and is not an <see cref="IDisposable"/>. Reads are
    /// fail-closed: any index outside the frozen count, a null or uninitialized
    /// context, or a divergence between the frozen count and the context's
    /// current accepted count returns false with a zero id rather than exposing
    /// a frame id from outside the frozen sequence.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunAcceptedFrameSnapshot
    {
        private readonly NvencRunChunkContext _context;
        private readonly int _count;

        internal NvencRunAcceptedFrameSnapshot(NvencRunChunkContext context, int count)
        {
            _context = context;
            _count = count;
        }

        /// <summary>
        /// The exact context whose accepted sequence was frozen.
        /// </summary>
        internal NvencRunChunkContext Context => _context;

        /// <summary>
        /// The Run's test run id, read through the bound context; zero while
        /// the context is null.
        /// </summary>
        internal long TestRunId => _context != null ? _context.TestRunId : 0;

        /// <summary>
        /// The frozen accepted frame count at freeze time.
        /// </summary>
        internal int Count => _count;

        /// <summary>
        /// Fail-closed, range-checked read of one frozen Capture Frame Id, in
        /// accepted order. The index must be in <c>[0, Count)</c>, the bound
        /// context must be non-null, and this snapshot must still be the exact
        /// frozen snapshot held by the context (same reference and same frozen
        /// count); otherwise false with a zero id. A snapshot forged by direct
        /// construction, or a diverged context count, fails closed.
        /// </summary>
        internal bool TryGetCaptureFrameId(int index, out long captureFrameId)
        {
            captureFrameId = 0;

            if (_context == null ||
                index < 0 ||
                index >= _count ||
                !_context.IsFrozenSnapshot(this))
            {
                return false;
            }

            return _context.TryGetAcceptedFrameId(index, out captureFrameId);
        }
    }
}
