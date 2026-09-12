using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The Main Thread's own teardown of what Unity owns in one Run: the
    /// conversion <c>CommandBuffer</c> the session holds, and the fixed pool of
    /// source RGBA RenderTextures. It holds the exact Run chunk context it
    /// belongs to, the exact session owner, and the exact pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing native happens here. The NV12 input textures, their views and
    /// registrations, the output buffers, the completion events and the
    /// encoder session itself belong to the native session and are released by
    /// the Output Worker's teardown; this entry runs only after that teardown
    /// has completed and the worker has physically stopped, which is what makes
    /// destroying the buffer that issued its render events safe.
    /// </para>
    /// <para>
    /// A step that throws issues no receipt and propagates unchanged: this
    /// type destroys nothing further and retries nothing itself, and adds no
    /// latch of its own. It does not claim the resources underneath are now
    /// untouchable - the render target pool keeps releasing what it can and
    /// reports what it could not, and the session owner will destroy a buffer
    /// it still holds if asked again. What makes the production path
    /// single-shot is above: a failure here is the Run Coordinator's, which
    /// poisons rather than reissuing this teardown.
    /// </para>
    /// <para>
    /// The binding is exact-reference only - there is no nonce, generation,
    /// thread identity check, or dispatcher here, and the Main Thread is the
    /// caller by contract.
    /// </para>
    /// </remarks>
    internal sealed class NvencMainThreadResourceTeardown : INvencMainThreadTextureTeardown
    {
        private readonly NvencRunChunkContext _context;
        private readonly NvencNativeEncoderSessionOwner _session;
        private readonly CaptureFrameRenderTargetPool _sourcePool;

        internal NvencMainThreadResourceTeardown(
            NvencRunChunkContext context,
            NvencNativeEncoderSessionOwner session,
            CaptureFrameRenderTargetPool sourcePool)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (sourcePool == null)
            {
                throw new ArgumentNullException(nameof(sourcePool));
            }

            _context = context;
            _session = session;
            _sourcePool = sourcePool;
        }

        /// <summary>
        /// Destroys this Run's Unity-managed resources on the Main Thread
        /// and, only if every step succeeded, issues the receipt for this
        /// exact implementation and its exact context. The caller calls it
        /// once; a failure is reported upwards rather than repeated here.
        /// </summary>
        public NvencMainThreadTextureTeardownReceipt TearDown()
        {
            // The buffer that issued the render events goes first, and only
            // because the session it issued them to is already closed - the
            // session owner refuses this otherwise.
            _session.DisposeManagedConversionResources();

            // Then the source surfaces the conversions read from.
            _sourcePool.Dispose();

            // Only a teardown that got all the way here has a receipt.
            return new NvencMainThreadTextureTeardownReceipt(this, _context);
        }

        /// <summary>
        /// O(1) exact-reference binding predicate, so a teardown built for a
        /// foreign Run is rejected before any side effect.
        /// </summary>
        public bool IsBoundTo(NvencRunChunkContext context)
        {
            return ReferenceEquals(_context, context);
        }
    }
}
