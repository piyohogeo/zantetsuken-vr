using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Saves the FIFO head of a <see cref="CaptureFramePngQueue"/> atomically
    /// through a <see cref="CaptureFramePngFileStore"/>. Dequeues only after a
    /// successful save, so a failed save keeps the head for retry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving is synchronous I/O and must not be called directly from a
    /// frame-update hot path. The queue is main-thread only.
    /// </para>
    /// <para>
    /// Does not own or dispose the queue or the file store. An empty queue does
    /// not validate the destination path. A failed save keeps the head in the
    /// queue for retry; on success the head is dequeued and the writer disposes
    /// the PNG. Destination naming and Capture Record metadata are the caller's
    /// responsibility. No trace events are recorded.
    /// </para>
    /// <para>
    /// The invariant checks after a successful save (dequeue failure, request
    /// or PNG allocation mismatch) report an
    /// <see cref="InvalidOperationException"/>; under the main-thread-only
    /// contract this state does not normally occur.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngQueueFileWriter
    {
        private readonly CaptureFramePngFileStore _fileStore;

        public CaptureFramePngQueueFileWriter(CaptureFramePngFileStore fileStore)
        {
            if (fileStore == null)
            {
                throw new ArgumentNullException(nameof(fileStore));
            }

            _fileStore = fileStore;
        }

        public CaptureFramePngSaveStatus TrySaveNext(CaptureFramePngQueue queue, string destinationPath)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (!queue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            if (!queue.TryPeek(out CaptureFrameRequest frameRequest, out NativeArray<byte> peekedPng))
            {
                return CaptureFramePngSaveStatus.None;
            }

            _fileStore.SaveAtomic(destinationPath, peekedPng);

            if (!queue.TryDequeue(out CaptureFrameRequest dequeuedRequest, out NativeArray<byte> dequeuedPng))
            {
                throw new InvalidOperationException("Queue became empty between peek and dequeue.");
            }

            try
            {
                if (!frameRequest.IdenticalTo(dequeuedRequest))
                {
                    throw new InvalidOperationException("Queue head changed between peek and dequeue.");
                }

                if (!dequeuedPng.Equals(peekedPng))
                {
                    throw new InvalidOperationException("PNG allocation changed between peek and dequeue.");
                }
            }
            finally
            {
                if (dequeuedPng.IsCreated)
                {
                    dequeuedPng.Dispose();
                }
            }

            return CaptureFramePngSaveStatus.Saved;
        }
    }
}
