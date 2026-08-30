using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Drives the lease-aware capture frame pipeline by one tick, running each
    /// stage at most once per call in a fixed order: artifact persistence,
    /// completed readback collect/encode/PNG enqueue (with render target lease
    /// reclamation), then registered-target readback start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixed order resolves backpressure deterministically: persisting
    /// artifacts first frees PNG queue slots, collecting completed readbacks
    /// then frees dispatcher slots and returns the completed frame's render
    /// target lease to the pool, and starting a new readback last never makes
    /// the request just started eligible for collection in the same tick. Each
    /// stage processes at most one item, so a tick never loops unboundedly
    /// within a frame.
    /// </para>
    /// <para>
    /// A tick is not a transaction. If a later stage throws, earlier stages that
    /// succeeded are not rolled back: a published sidecar is not deleted, an
    /// enqueued PNG is not returned to the queue, a returned lease is not
    /// re-registered, and a started GPU request is not cancelled. The next tick
    /// resumes from the current queue and registry state, and exceptions are
    /// never translated.
    /// </para>
    /// <para>
    /// <see cref="AdvancePendingWork"/> and
    /// <see cref="TryStartNextReadback"/> split a tick so a caller can advance
    /// completed work first and then start a pending readback within the same
    /// frame. <see cref="AdvancePendingWork"/> never starts a request, and
    /// <see cref="TryStartNextReadback"/> never advances persistence, encoding,
    /// or collection. Both are main-thread only.
    /// </para>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. Owns and disposes nothing:
    /// not the pump, completion router, persistence coordinator, PNG queue,
    /// artifact queue, record registry, lease registry, render target pool,
    /// dispatcher, buffer pool, record, artifact, receipt, PNG, lease, or render
    /// texture. It performs no Dispose, Clear, Return, Release, Destroy, or file
    /// deletion and does not implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class PngJsonCaptureFrameRenderTargetPipelineCoordinator
    {
        private readonly CaptureFrameRenderTargetReadbackPump _readbackPump;
        private readonly PngJsonCaptureFrameReadbackCompletionRouter _readbackCompletionRouter;
        private readonly CaptureFramePngArtifactPersistenceCoordinator _persistenceCoordinator;
        private readonly CaptureFramePngQueue _pngQueue;
        private readonly CaptureFramePngArtifactQueue _artifactQueue;
        private readonly CaptureFrameRecordRegistry _recordRegistry;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;
        private readonly CaptureFrameRenderTargetPool _renderTargetPool;

        public PngJsonCaptureFrameRenderTargetPipelineCoordinator(
            CaptureFrameRenderTargetReadbackPump readbackPump,
            PngJsonCaptureFrameReadbackCompletionRouter readbackCompletionRouter,
            CaptureFramePngArtifactPersistenceCoordinator persistenceCoordinator,
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry,
            CaptureFrameRenderTargetPool renderTargetPool)
        {
            if (readbackPump == null)
            {
                throw new ArgumentNullException(nameof(readbackPump));
            }

            if (readbackCompletionRouter == null)
            {
                throw new ArgumentNullException(nameof(readbackCompletionRouter));
            }

            if (persistenceCoordinator == null)
            {
                throw new ArgumentNullException(nameof(persistenceCoordinator));
            }

            if (pngQueue == null)
            {
                throw new ArgumentNullException(nameof(pngQueue));
            }

            if (artifactQueue == null)
            {
                throw new ArgumentNullException(nameof(artifactQueue));
            }

            if (recordRegistry == null)
            {
                throw new ArgumentNullException(nameof(recordRegistry));
            }

            if (leaseRegistry == null)
            {
                throw new ArgumentNullException(nameof(leaseRegistry));
            }

            if (renderTargetPool == null)
            {
                throw new ArgumentNullException(nameof(renderTargetPool));
            }

            _readbackPump = readbackPump;
            _readbackCompletionRouter = readbackCompletionRouter;
            _persistenceCoordinator = persistenceCoordinator;
            _pngQueue = pngQueue;
            _artifactQueue = artifactQueue;
            _recordRegistry = recordRegistry;
            _leaseRegistry = leaseRegistry;
            _renderTargetPool = renderTargetPool;
        }

        /// <summary>
        /// Advances completed work without starting any new readback: artifact
        /// persistence first, then completed readback collect/encode/PNG
        /// enqueue with render target lease reclamation, each at most once. Does
        /// not touch the request queue or the dispatcher start state.
        /// </summary>
        public PngJsonCaptureFramePipelineAdvanceResult AdvancePendingWork()
        {
            CaptureFramePngArtifactPersistenceStatus persistenceStatus = _persistenceCoordinator.TryAdvanceNext(
                _pngQueue,
                _artifactQueue,
                out CaptureFramePngArtifact completedArtifact,
                out CaptureFramePngArtifactSaveReceipt sidecarReceipt);

            CaptureFramePngQueueStatus readbackCompletionStatus = _readbackCompletionRouter.TryCollectEncodeAndEnqueue(
                _pngQueue,
                _recordRegistry,
                _leaseRegistry,
                _renderTargetPool);

            return new PngJsonCaptureFramePipelineAdvanceResult(
                persistenceStatus,
                readbackCompletionStatus,
                completedArtifact,
                sidecarReceipt);
        }

        /// <summary>
        /// Starts at most one pending readback by delegating to the
        /// registered-target readback pump unchanged. Does not persist artifacts
        /// or collect completed readbacks and records no trace.
        /// </summary>
        public bool TryStartNextReadback()
        {
            return _readbackPump.TryStartNext();
        }

        /// <summary>
        /// Runs one pipeline tick: the advance half followed by the start half.
        /// </summary>
        public PngJsonCaptureFramePipelineTickResult Tick()
        {
            PngJsonCaptureFramePipelineAdvanceResult advance = AdvancePendingWork();
            bool readbackStarted = TryStartNextReadback();

            return new PngJsonCaptureFramePipelineTickResult(
                advance.PersistenceStatus,
                advance.ReadbackCompletionStatus,
                readbackStarted,
                advance.CompletedArtifact,
                advance.SidecarReceipt);
        }
    }
}
