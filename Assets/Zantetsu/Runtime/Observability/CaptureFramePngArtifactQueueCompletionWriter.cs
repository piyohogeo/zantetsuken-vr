using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects <see cref="CaptureFramePngArtifactQueue"/> to
    /// <see cref="CaptureFramePngArtifactCompletionWriter"/>: publishes the
    /// sidecar of the FIFO head artifact and dequeues it only after the sidecar
    /// is atomically published and the corresponding record is removed from the
    /// registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. Under that contract the
    /// artifact queue and the registry are not modified externally between the
    /// peek and the dequeue.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the completion writer, the artifact queue,
    /// the registry, any artifact, frame record, PNG receipt, sidecar receipt,
    /// or PNG/sidecar file. It performs no Dispose or Clear and does not
    /// implement <see cref="IDisposable"/>. The PNG is never re-read, verified,
    /// or modified.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactQueueCompletionWriter
    {
        private readonly CaptureFramePngArtifactCompletionWriter _completionWriter;

        public CaptureFramePngArtifactQueueCompletionWriter(CaptureFramePngArtifactCompletionWriter completionWriter)
        {
            if (completionWriter == null)
            {
                throw new ArgumentNullException(nameof(completionWriter));
            }

            _completionWriter = completionWriter;
        }

        /// <summary>
        /// Publishes the sidecar of the FIFO head of
        /// <paramref name="artifactQueue"/> and, only after successful
        /// publication, dequeues the head and publishes the completed artifact
        /// and sidecar receipt.
        /// </summary>
        public CaptureFramePngArtifactCompletionStatus TryCompleteNext(
            CaptureFramePngArtifactQueue artifactQueue,
            string sidecarDestinationPath,
            out CaptureFramePngArtifact completedArtifact,
            out CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            completedArtifact = null;
            sidecarReceipt = null;

            if (artifactQueue == null)
            {
                throw new ArgumentNullException(nameof(artifactQueue));
            }

            if (!artifactQueue.TryPeek(out CaptureFramePngArtifact queued))
            {
                return CaptureFramePngArtifactCompletionStatus.None;
            }

            CaptureFramePngArtifactSaveReceipt receipt = _completionWriter.SaveAtomic(
                sidecarDestinationPath,
                queued.FrameRecord.Request,
                queued.PngReceipt,
                out CaptureFramePngArtifact completed);

            if (completed == null)
            {
                throw new InvalidOperationException("The completion writer returned no artifact; the sidecar may already be published.");
            }

            if (receipt == null)
            {
                throw new InvalidOperationException("The completion writer returned no sidecar receipt; the sidecar may already be published.");
            }

            if (!ReferenceEquals(completed.FrameRecord, queued.FrameRecord))
            {
                throw new InvalidOperationException("The completed artifact references a different frame record; the sidecar may already be published.");
            }

            if (!ReferenceEquals(completed.PngReceipt, queued.PngReceipt))
            {
                throw new InvalidOperationException("The completed artifact references a different PNG receipt; the sidecar may already be published.");
            }

            if (!completed.FrameRecord.Request.IdenticalTo(queued.FrameRecord.Request))
            {
                throw new InvalidOperationException("The completed artifact request does not match the queued artifact request; the sidecar may already be published.");
            }

            if (!artifactQueue.TryDequeue(out CaptureFramePngArtifact dequeued))
            {
                throw new InvalidOperationException("The artifact queue head could not be dequeued after the sidecar was published; the sidecar may already be published.");
            }

            if (!ReferenceEquals(dequeued, queued))
            {
                throw new InvalidOperationException("The dequeued artifact is not the peeked artifact; the sidecar may already be published.");
            }

            completedArtifact = completed;
            sidecarReceipt = receipt;
            return CaptureFramePngArtifactCompletionStatus.Completed;
        }
    }
}
