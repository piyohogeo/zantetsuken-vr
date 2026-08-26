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

        /// <summary>
        /// Saves the FIFO head and returns, only on success, the dequeued
        /// request and the immutable save receipt. The receipt is used exactly
        /// as produced by the file store: its destination path, byte count, and
        /// content SHA-256 are not re-read or re-hashed.
        /// </summary>
        /// <remarks>
        /// On an empty queue this returns <see cref="CaptureFramePngSaveStatus.None"/>
        /// with <paramref name="frameRequest"/> left at default and
        /// <paramref name="receipt"/> left null, without validating the
        /// destination path. A failed save rethrows without dequeuing and leaves
        /// both out arguments untouched. On success the dequeued PNG is disposed
        /// by this writer before the out arguments are published.
        /// </remarks>
        public CaptureFramePngSaveStatus TrySaveNext(
            CaptureFramePngQueue queue,
            string destinationPath,
            out CaptureFrameRequest frameRequest,
            out CaptureFramePngSaveReceipt receipt)
        {
            frameRequest = default;
            receipt = null;

            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (!queue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            if (!queue.TryPeek(out CaptureFrameRequest peekedRequest, out NativeArray<byte> peekedPng))
            {
                return CaptureFramePngSaveStatus.None;
            }

            CaptureFramePngSaveReceipt savedReceipt = _fileStore.SaveAtomicWithReceipt(destinationPath, peekedPng);

            if (!queue.TryDequeue(out CaptureFrameRequest dequeuedRequest, out NativeArray<byte> dequeuedPng))
            {
                throw new InvalidOperationException("Queue became empty between peek and dequeue.");
            }

            try
            {
                if (!peekedRequest.IdenticalTo(dequeuedRequest))
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

            frameRequest = dequeuedRequest;
            receipt = savedReceipt;
            return CaptureFramePngSaveStatus.Saved;
        }

        public CaptureFramePngSaveStatus TrySaveNext(CaptureFramePngQueue queue, string destinationPath)
        {
            return TrySaveNext(queue, destinationPath, out _, out _);
        }
    }
}
