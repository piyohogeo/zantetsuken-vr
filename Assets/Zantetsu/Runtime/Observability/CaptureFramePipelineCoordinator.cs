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
        /// Runs one pipeline tick and returns a summary of what happened.
        /// </summary>
        public CaptureFramePipelineTickResult Tick(RenderTexture source)
        {
            CaptureFramePngArtifactPersistenceStatus persistenceStatus = _persistenceCoordinator.TryAdvanceNext(
                _pngQueue,
                _artifactQueue,
                out CaptureFramePngArtifact completedArtifact,
                out CaptureFramePngArtifactSaveReceipt sidecarReceipt);

            CaptureFramePngQueueStatus readbackCompletionStatus = _readbackCompletionRouter.TryCollectEncodeAndEnqueue(_pngQueue, _recordRegistry);

            bool readbackStarted = _readbackPump.TryStartNext(source);

            return new CaptureFramePipelineTickResult(
                persistenceStatus,
                readbackCompletionStatus,
                readbackStarted,
                completedArtifact,
                sidecarReceipt);
        }
    }
}
