using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Drives the capture frame pipeline by one tick, running each stage at most
    /// once per call in a fixed order: artifact persistence, completed readback
    /// collection, then pending readback start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixed order resolves backpressure deterministically: persisting
    /// artifacts first frees PNG queue slots, collecting completed readbacks
    /// then frees dispatcher slots, and starting a new readback last never
    /// makes the request just started eligible for collection in the same tick.
    /// Each stage processes at most one item, so a tick never loops
    /// unboundedly within a frame.
    /// </para>
    /// <para>
    /// A tick is not a transaction. If a later stage throws, earlier stages that
    /// succeeded are not rolled back: a published sidecar is not deleted, a
    /// collected PNG is not returned to the queue, and the registry is not
    /// re-registered or cleared. The next tick resumes from the current queue
    /// state, and exceptions are never translated.
    /// </para>
    /// <para>
    /// <see cref="AdvancePendingWork"/> and
    /// <see cref="TryStartNextReadback"/> split a tick so a caller can advance
    /// completed work first and then connect the current frame's submission and
    /// readback start within the same frame. <see cref="AdvancePendingWork"/>
    /// never starts a request, and <see cref="TryStartNextReadback"/> never
    /// advances persistence, encoding, or collection. If a caller mutates the
    /// queues, registry, or dispatcher between the two calls, the consistency of
    /// that intermediate state is the caller's responsibility. Both are
    /// main-thread only.
    /// </para>
    /// <para>
    /// Readback completion routing internally crosses the Phase 1 encode
    /// submission, synchronous service, completion collection, and main-thread
    /// application boundaries in sequence during the same call. This does not
    /// add a thread, task, job, raw-buffer copy, or an additional item per tick;
    /// it preserves this coordinator's existing ordering and limits while
    /// allowing a future service implementation to replace the encode stage.
    /// </para>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the readback pump, completion router,
    /// persistence coordinator, PNG queue, artifact queue, record registry,
    /// dispatcher, buffer pool, render texture, artifact, or receipt. It
    /// performs no Dispose, Clear, trace recording, logging, directory creation,
    /// or file deletion, and does not implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePipelineCoordinator
    {
        private readonly CaptureFrameReadbackPump _readbackPump;
        private readonly CaptureFrameReadbackCompletionRouter _readbackCompletionRouter;
        private readonly CaptureFramePngArtifactPersistenceCoordinator _persistenceCoordinator;
        private readonly CaptureFramePngQueue _pngQueue;
        private readonly CaptureFramePngArtifactQueue _artifactQueue;
        private readonly CaptureFrameRecordRegistry _recordRegistry;

        public CaptureFramePipelineCoordinator(
            CaptureFrameReadbackPump readbackPump,
            CaptureFrameReadbackCompletionRouter readbackCompletionRouter,
            CaptureFramePngArtifactPersistenceCoordinator persistenceCoordinator,
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            CaptureFrameRecordRegistry recordRegistry)
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

            _readbackPump = readbackPump;
            _readbackCompletionRouter = readbackCompletionRouter;
            _persistenceCoordinator = persistenceCoordinator;
            _pngQueue = pngQueue;
            _artifactQueue = artifactQueue;
            _recordRegistry = recordRegistry;
        }

        /// <summary>
        /// Advances completed work without starting any new readback: artifact
        /// persistence first, then completed readback collect/encode/PNG
        /// enqueue, each at most once. Does not touch the request queue or the
        /// dispatcher start state.
        /// </summary>
        public CaptureFramePipelineAdvanceResult AdvancePendingWork()
        {
            CaptureFramePngArtifactPersistenceStatus persistenceStatus = _persistenceCoordinator.TryAdvanceNext(
                _pngQueue,
                _artifactQueue,
                out CaptureFramePngArtifact completedArtifact,
                out CaptureFramePngArtifactSaveReceipt sidecarReceipt);

            CaptureFramePngQueueStatus readbackCompletionStatus = _readbackCompletionRouter.TryCollectEncodeAndEnqueue(_pngQueue, _recordRegistry);

            return new CaptureFramePipelineAdvanceResult(
                persistenceStatus,
                readbackCompletionStatus,
                completedArtifact,
                sidecarReceipt);
        }

        /// <summary>
        /// Starts at most one pending readback by delegating to the readback
        /// pump unchanged. Does not persist artifacts or collect completed
        /// readbacks and records no trace.
        /// </summary>
        public bool TryStartNextReadback(RenderTexture source)
        {
            return _readbackPump.TryStartNext(source);
        }

        /// <summary>
        /// Runs one pipeline tick: the advance half followed by the start half.
        /// </summary>
        public CaptureFramePipelineTickResult Tick(RenderTexture source)
        {
            CaptureFramePipelineAdvanceResult advance = AdvancePendingWork();
            bool readbackStarted = TryStartNextReadback(source);

            return new CaptureFramePipelineTickResult(
                advance.PersistenceStatus,
                advance.ReadbackCompletionStatus,
                readbackStarted,
                advance.CompletedArtifact,
                advance.SidecarReceipt);
        }
    }
}
