using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects <see cref="CaptureFramePngArtifactPreparer"/> to a
    /// <see cref="CaptureFramePngArtifactQueue"/>: saves the FIFO head of a
    /// <see cref="CaptureFramePngQueue"/> and enqueues the prepared artifact
    /// into the pending artifact queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. Under that contract neither
    /// the PNG queue, the artifact queue, nor the registry may be changed
    /// externally while this method runs.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the artifact preparer, the PNG queue, the
    /// artifact queue, the registry, any artifact, receipt, frame record, or PNG
    /// file. It performs no Dispose, Clear, registry removal, or sidecar save,
    /// and does not implement <see cref="IDisposable"/>. PNG <c>NativeArray</c>
    /// ownership is delegated to the underlying preparer and queue file writer.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactQueuePreparer
    {
        private readonly CaptureFramePngArtifactPreparer _artifactPreparer;

        public CaptureFramePngArtifactQueuePreparer(CaptureFramePngArtifactPreparer artifactPreparer)
        {
            if (artifactPreparer == null)
            {
                throw new ArgumentNullException(nameof(artifactPreparer));
            }

            _artifactPreparer = artifactPreparer;
        }

        /// <summary>
        /// Prepares the FIFO head of <paramref name="pngQueue"/> into
        /// <paramref name="artifactQueue"/>. Returns
        /// <see cref="CaptureFramePngArtifactPreparationStatus.None"/> when the
        /// PNG queue is empty, and
        /// <see cref="CaptureFramePngArtifactPreparationStatus.Backpressured"/>
        /// when the artifact queue is full (without saving). Only a successful
        /// save and enqueue returns
        /// <see cref="CaptureFramePngArtifactPreparationStatus.Queued"/>.
        /// </summary>
        public CaptureFramePngArtifactPreparationStatus TryPrepareNext(
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            string pngDestinationPath)
        {
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

            if (pngQueue.Count == 0)
            {
                return CaptureFramePngArtifactPreparationStatus.None;
            }

            if (artifactQueue.Count >= artifactQueue.Capacity)
            {
                return CaptureFramePngArtifactPreparationStatus.Backpressured;
            }

            CaptureFramePngSaveStatus status = _artifactPreparer.TrySaveNext(
                pngQueue,
                pngDestinationPath,
                out CaptureFramePngArtifact artifact);

            if (status != CaptureFramePngSaveStatus.Saved)
            {
                throw new InvalidOperationException("The artifact preparer did not save the queue head for a non-empty PNG queue.");
            }

            if (artifact == null)
            {
                throw new InvalidOperationException("The artifact preparer returned no artifact for a non-empty PNG queue.");
            }

            if (!artifactQueue.TryEnqueue(artifact))
            {
                throw new InvalidOperationException("The artifact queue rejected the enqueue after a free slot was confirmed; the PNG may already be published.");
            }

            return CaptureFramePngArtifactPreparationStatus.Queued;
        }
    }
}
