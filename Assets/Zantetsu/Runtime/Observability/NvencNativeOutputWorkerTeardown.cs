using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The Output Worker's own teardown of one real native encoder session:
    /// the five prepared resource groups released in the exact reverse of the
    /// order they were prepared in, and then the session closed. It holds the
    /// exact session owner and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every step here is native. The Unity-managed conversion
    /// <c>CommandBuffer</c> and the source RGBA RenderTextures are deliberately
    /// left alone: they belong to the Main Thread, which destroys them after
    /// this worker has completed this teardown and physically stopped. That is
    /// why the close used here is the native half alone.
    /// </para>
    /// <para>
    /// A step that throws stops the teardown where it is and propagates
    /// unchanged. Nothing later is attempted, nothing already released is
    /// rolled back, nothing is retried here, and nothing is handed to another
    /// thread: after a failure this session's ownership is no longer known,
    /// and the Output Worker decides what that means for the process - in
    /// practice by poisoning rather than by asking for this teardown again.
    /// </para>
    /// <para>
    /// There is no attempt latch here. The Output Worker calls this exactly
    /// once and each native release and the close are each one-shot in the
    /// session owner, so a second call would be refused there rather than
    /// silently repeated. The teardown is admitted by the existing Output
    /// Processor, Worker and Join boundaries, so no emptiness check is
    /// repeated here either, and the owned Access Unit region is a managed
    /// array that needs no clearing, shrinking, or freeing.
    /// </para>
    /// </remarks>
    internal sealed class NvencNativeOutputWorkerTeardown : INvencOutputWorkerTeardown
    {
        private readonly NvencNativeEncoderSessionOwner _session;

        internal NvencNativeOutputWorkerTeardown(NvencNativeEncoderSessionOwner session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _session = session;
        }

        /// <summary>
        /// Releases this session's native resource groups in reverse
        /// preparation order, closes the session, and only then issues the
        /// receipt for this exact implementation.
        /// </summary>
        public NvencOutputWorkerTeardownReceipt TearDown()
        {
            // Reverse of Initialize, Bind Sources, Inputs, Conversion
            // Commands, Output Buffers, Completion Events. Each of these
            // refuses before spending its one attempt if something it depends
            // on is still held, so the order is a real requirement and not a
            // convention.
            _session.ReleaseCompletionEvents();
            _session.ReleaseOutputBuffers();

            // Native conversion resources only. The managed buffer that
            // issued the render events is still this owner's and is destroyed
            // on the Main Thread.
            _session.ReleaseConversionCommands();

            _session.ReleaseInputSurfaces();
            _session.ReleaseSourceSurfaces();
            _session.CloseNativeSession();

            // Only a teardown that got all the way here has a receipt.
            return NvencOutputWorkerTeardownReceipt.Issue(this);
        }
    }
}
