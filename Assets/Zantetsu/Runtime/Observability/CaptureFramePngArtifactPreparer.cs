using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Prepares a saved capture frame artifact from the FIFO head of a
    /// <see cref="CaptureFramePngQueue"/>: verifies that the corresponding
    /// <see cref="CaptureFrameRecord"/> exists in a
    /// <see cref="CaptureFrameRecordRegistry"/> with a fully identical request,
    /// saves the PNG atomically through a
    /// <see cref="CaptureFramePngQueueFileWriter"/>, and returns an immutable
    /// <see cref="CaptureFramePngArtifact"/> binding the record and the PNG
    /// receipt. The registry record is kept for later sidecar publication by
    /// <see cref="CaptureFramePngArtifactCompletionWriter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. Under that contract the
    /// queue head and the registry cannot change between the pre-save lookups
    /// and the save itself, so the only post-save checks are internal invariant
    /// verifications.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the registry, the queue writer, the queue,
    /// the record, the receipt, or the artifact. PNG <c>NativeArray</c> ownership
    /// is delegated to the queue writer. The PNG file is never deleted, renamed,
    /// re-read, or re-hashed; no sidecar is saved; no trace event, log entry, or
    /// file name is generated; no directory is created. This type does not
    /// implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactPreparer
    {
        private readonly CaptureFrameRecordRegistry _recordRegistry;
        private readonly CaptureFramePngQueueFileWriter _pngQueueFileWriter;

        public CaptureFramePngArtifactPreparer(
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFramePngQueueFileWriter pngQueueFileWriter)
        {
            if (recordRegistry == null)
            {
                throw new ArgumentNullException(nameof(recordRegistry));
            }

            if (pngQueueFileWriter == null)
            {
                throw new ArgumentNullException(nameof(pngQueueFileWriter));
            }

            _recordRegistry = recordRegistry;
            _pngQueueFileWriter = pngQueueFileWriter;
        }

        /// <summary>
        /// Saves the FIFO head of <paramref name="queue"/> to
        /// <paramref name="pngDestinationPath"/> and returns, only on success, an
        /// immutable artifact binding the matching registry record to the PNG
        /// receipt produced by the queue writer.
        /// </summary>
        public CaptureFramePngSaveStatus TrySaveNext(
            CaptureFramePngQueue queue,
            string pngDestinationPath,
            out CaptureFramePngArtifact artifact)
        {
            artifact = null;

            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (!queue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            if (!queue.TryPeek(out CaptureFrameRequest peekedRequest, out _))
            {
                return CaptureFramePngSaveStatus.None;
            }

            if (!_recordRegistry.TryGet(peekedRequest, out CaptureFrameRecord record))
            {
                throw new InvalidOperationException("No capture frame record is registered for the queue head request.");
            }

            CaptureFramePngSaveStatus status = _pngQueueFileWriter.TrySaveNext(
                queue,
                pngDestinationPath,
                out CaptureFrameRequest savedRequest,
                out CaptureFramePngSaveReceipt pngReceipt);

            if (status != CaptureFramePngSaveStatus.Saved)
            {
                throw new InvalidOperationException("The queue writer did not save the peeked head; the PNG may already be published.");
            }

            if (pngReceipt == null)
            {
                throw new InvalidOperationException("The queue writer returned a null receipt; the PNG may already be published.");
            }

            if (!savedRequest.IdenticalTo(peekedRequest))
            {
                throw new InvalidOperationException("The saved request does not match the peeked head request; the PNG may already be published.");
            }

            CaptureFramePngArtifact built = new CaptureFramePngArtifact(record, savedRequest, pngReceipt);

            artifact = built;
            return CaptureFramePngSaveStatus.Saved;
        }
    }
}
