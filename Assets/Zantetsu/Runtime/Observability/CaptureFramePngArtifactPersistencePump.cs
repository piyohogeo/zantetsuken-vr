using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Advances the capture artifact persistence pipeline by exactly one step:
    /// publishes the sidecar of a pending artifact when one exists, otherwise
    /// saves and prepares the next PNG. Never performs both publications in a
    /// single call, so a retryable artifact queue state always remains between
    /// the PNG save and the sidecar publication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. Under that contract the PNG
    /// queue, the artifact queue, and the registry are not modified externally
    /// while this method runs.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the queue preparer, the queue completion
    /// writer, the PNG queue, the artifact queue, the registry, any artifact,
    /// record, or receipt, or any PNG/sidecar file. It performs no Dispose,
    /// Clear, trace recording, logging, directory creation, or file name
    /// generation, and does not implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactPersistencePump
    {
        private readonly CaptureFramePngArtifactQueuePreparer _queuePreparer;
        private readonly CaptureFramePngArtifactQueueCompletionWriter _queueCompletionWriter;

        public CaptureFramePngArtifactPersistencePump(
            CaptureFramePngArtifactQueuePreparer queuePreparer,
            CaptureFramePngArtifactQueueCompletionWriter queueCompletionWriter)
        {
            if (queuePreparer == null)
            {
                throw new ArgumentNullException(nameof(queuePreparer));
            }

            if (queueCompletionWriter == null)
            {
                throw new ArgumentNullException(nameof(queueCompletionWriter));
            }

            _queuePreparer = queuePreparer;
            _queueCompletionWriter = queueCompletionWriter;
        }

        /// <summary>
        /// Advances persistence by one step. A pending artifact takes priority
        /// over a new PNG, so the sidecar stage runs whenever the artifact queue
        /// is non-empty.
        /// </summary>
        public CaptureFramePngArtifactPersistenceStatus TryAdvanceNext(
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            string pngDestinationPath,
            string sidecarDestinationPath,
            out CaptureFramePngArtifact completedArtifact,
            out CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            completedArtifact = null;
            sidecarReceipt = null;

            if (pngQueue == null)
            {
                throw new ArgumentNullException(nameof(pngQueue));
            }

            if (artifactQueue == null)
            {
                throw new ArgumentNullException(nameof(artifactQueue));
            }

            if (!pngQueue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            if (artifactQueue.Count > 0)
            {
                return CompleteSidecar(artifactQueue, sidecarDestinationPath, out completedArtifact, out sidecarReceipt);
            }

            if (pngQueue.Count == 0)
            {
                return CaptureFramePngArtifactPersistenceStatus.None;
            }

            return PreparePng(pngQueue, artifactQueue, pngDestinationPath);
        }

        /// <summary>
        /// Advances persistence by one step using a correlated destination. The
        /// destination's capture frame ID must match the FIFO head this call
        /// would process (the artifact queue head when non-empty, otherwise the
        /// PNG queue head), verified before any file I/O.
        /// </summary>
        public CaptureFramePngArtifactPersistenceStatus TryAdvanceNext(
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            CaptureFramePngArtifactDestination destination,
            out CaptureFramePngArtifact completedArtifact,
            out CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            completedArtifact = null;
            sidecarReceipt = null;

            if (pngQueue == null)
            {
                throw new ArgumentNullException(nameof(pngQueue));
            }

            if (artifactQueue == null)
            {
                throw new ArgumentNullException(nameof(artifactQueue));
            }

            if (!pngQueue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            long headCaptureFrameId;
            if (artifactQueue.Count > 0)
            {
                if (!artifactQueue.TryPeek(out CaptureFramePngArtifact queued))
                {
                    throw new InvalidOperationException("The artifact queue head could not be peeked.");
                }

                headCaptureFrameId = queued.CaptureFrameId;
            }
            else if (pngQueue.Count == 0)
            {
                return CaptureFramePngArtifactPersistenceStatus.None;
            }
            else
            {
                if (!pngQueue.TryPeek(out CaptureFrameRequest peekedRequest, out _))
                {
                    throw new InvalidOperationException("The PNG queue head could not be peeked.");
                }

                headCaptureFrameId = peekedRequest.TraceContext.CaptureFrameId;
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destination.CaptureFrameId != headCaptureFrameId)
            {
                throw new ArgumentException("The destination capture frame ID does not match the queue head capture frame ID.", nameof(destination));
            }

            return TryAdvanceNext(
                pngQueue,
                artifactQueue,
                destination.PngDestinationPath,
                destination.SidecarDestinationPath,
                out completedArtifact,
                out sidecarReceipt);
        }

        private CaptureFramePngArtifactPersistenceStatus CompleteSidecar(
            CaptureFramePngArtifactQueue artifactQueue,
            string sidecarDestinationPath,
            out CaptureFramePngArtifact completedArtifact,
            out CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            CaptureFramePngArtifactCompletionStatus status = _queueCompletionWriter.TryCompleteNext(
                artifactQueue,
                sidecarDestinationPath,
                out CaptureFramePngArtifact completed,
                out CaptureFramePngArtifactSaveReceipt receipt);

            if (status != CaptureFramePngArtifactCompletionStatus.Completed)
            {
                throw new InvalidOperationException("The completion writer did not complete the queue head; the sidecar may already be published.");
            }

            if (completed == null)
            {
                throw new InvalidOperationException("The completion writer returned no artifact; the sidecar may already be published.");
            }

            if (receipt == null)
            {
                throw new InvalidOperationException("The completion writer returned no sidecar receipt; the sidecar may already be published.");
            }

            completedArtifact = completed;
            sidecarReceipt = receipt;
            return CaptureFramePngArtifactPersistenceStatus.SidecarCompleted;
        }

        private CaptureFramePngArtifactPersistenceStatus PreparePng(
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            string pngDestinationPath)
        {
            int artifactCountBefore = artifactQueue.Count;
            int pngCountBefore = pngQueue.Count;

            CaptureFramePngArtifactPreparationStatus status = _queuePreparer.TryPrepareNext(
                pngQueue,
                artifactQueue,
                pngDestinationPath);

            if (status != CaptureFramePngArtifactPreparationStatus.Queued)
            {
                throw new InvalidOperationException("The queue preparer did not prepare the PNG; the PNG may already be published.");
            }

            if (artifactQueue.Count != artifactCountBefore + 1)
            {
                throw new InvalidOperationException("The artifact queue count did not increase by one; the PNG may already be published.");
            }

            if (pngQueue.Count != pngCountBefore - 1)
            {
                throw new InvalidOperationException("The PNG queue count did not decrease by one; the PNG may already be published.");
            }

            if (!artifactQueue.TryPeek(out _))
            {
                throw new InvalidOperationException("The artifact queue head could not be peeked; the PNG may already be published.");
            }

            return CaptureFramePngArtifactPersistenceStatus.PngPrepared;
        }
    }
}
