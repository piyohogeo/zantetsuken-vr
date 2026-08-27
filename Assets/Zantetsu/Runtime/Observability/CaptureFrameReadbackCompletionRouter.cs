using System;
using System.Diagnostics;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Routes completed GPU readbacks: successful results are returned to the
    /// caller, while errored results are traced as dropped and released
    /// automatically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only. Does not own, dispose, or drain the dispatcher,
    /// observer, logger, or pool, and does not mutate capture frame IDs or
    /// requests.
    /// </para>
    /// <para>
    /// Release ownership is asymmetric: the caller must release successful
    /// results (after reading their buffer), while the router releases errored
    /// results itself.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameReadbackCompletionRouter
    {
        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly CaptureFrameTraceObserver _observer;

        public CaptureFrameReadbackCompletionRouter(
            UnityRenderTextureReadbackDispatcher dispatcher,
            CaptureFrameTraceObserver observer)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            _dispatcher = dispatcher;
            _observer = observer;
        }

        public int ActiveReadbackCount => _dispatcher.ActiveCount;

        /// <summary>
        /// Collects a completed readback. Successful results are returned as
        /// <see cref="CaptureFrameReadbackCollectStatus.Succeeded"/> and remain
        /// rented until the caller releases them; errored results are traced
        /// with <see cref="CaptureFrameDropReason.ReadbackFailed"/>, released,
        /// and reported as <see cref="CaptureFrameReadbackCollectStatus.Dropped"/>.
        /// </summary>
        public CaptureFrameReadbackCollectStatus TryCollect(out CaptureFrameReadbackResult result)
        {
            if (!_dispatcher.TryCollect(out CaptureFrameReadbackResult collected))
            {
                result = default;
                return CaptureFrameReadbackCollectStatus.None;
            }

            if (!collected.HasError)
            {
                result = collected;
                return CaptureFrameReadbackCollectStatus.Succeeded;
            }

            result = default;

            try
            {
                _observer.RecordDropped(collected.FrameRequest.TraceContext, CaptureFrameDropReason.ReadbackFailed);
            }
            finally
            {
                _dispatcher.Release(collected);
            }

            return CaptureFrameReadbackCollectStatus.Dropped;
        }

        /// <summary>
        /// Collects a completed readback and, for successful results, encodes
        /// the readback buffer as PNG and records a
        /// <c>CaptureFrameEncoded</c> trace event.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Main-thread only. <see cref="CaptureFramePngCollectStatus.None"/>
        /// and <see cref="CaptureFramePngCollectStatus.Dropped"/> return no PNG.
        /// The <see cref="NativeArray{T}"/> returned with
        /// <see cref="CaptureFramePngCollectStatus.Encoded"/> is owned by the
        /// caller and must be disposed.
        /// </para>
        /// <para>
        /// The raw readback slot is always released before this method returns.
        /// Exceptions from PNG encoding or trace recording propagate after
        /// cleanup. No file I/O is performed.
        /// </para>
        /// </remarks>
        public CaptureFramePngCollectStatus TryCollectAndEncodePng(
            out CaptureFrameRequest frameRequest,
            out NativeArray<byte> pngBytes)
        {
            frameRequest = default;
            pngBytes = default;

            CaptureFrameReadbackCollectStatus status = TryCollect(out CaptureFrameReadbackResult result);

            if (status == CaptureFrameReadbackCollectStatus.None)
            {
                return CaptureFramePngCollectStatus.None;
            }

            if (status == CaptureFrameReadbackCollectStatus.Dropped)
            {
                return CaptureFramePngCollectStatus.Dropped;
            }

            NativeArray<byte> encoded = default;
            try
            {
                try
                {
                    NativeArray<byte> buffer = _dispatcher.GetBuffer(result);

                    long startTimestamp = Stopwatch.GetTimestamp();
                    encoded = CaptureFramePngEncoder.Encode(buffer, result.FrameRequest.PixelLayout);
                    long endTimestamp = Stopwatch.GetTimestamp();
                    double elapsedMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

                    _observer.RecordEncoded(result.FrameRequest.TraceContext, elapsedMilliseconds, encoded.Length);
                }
                finally
                {
                    _dispatcher.Release(result);
                }
            }
            catch
            {
                if (encoded.IsCreated)
                {
                    encoded.Dispose();
                }

                throw;
            }

            frameRequest = result.FrameRequest;
            pngBytes = encoded;
            encoded = default;
            return CaptureFramePngCollectStatus.Encoded;
        }

        /// <summary>
        /// Collects a completed readback, encodes it as PNG, and enqueues the
        /// result into <paramref name="queue"/>. On queue-full the generated
        /// PNG is dropped and a <c>CaptureFrameDropped</c> event is recorded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Main-thread only. Does not own or dispose the queue, dispatcher,
        /// observer, logger, or pool.
        /// </para>
        /// <para>
        /// On <see cref="CaptureFramePngQueueStatus.Queued"/> the queue owns the
        /// PNG. On <see cref="CaptureFramePngQueueStatus.None"/> and
        /// <see cref="CaptureFramePngQueueStatus.Dropped"/> no PNG is exposed to
        /// the caller. On queue-full the generated PNG is disposed and a drop
        /// trace is recorded. No file I/O is performed.
        /// </para>
        /// </remarks>
        public CaptureFramePngQueueStatus TryCollectEncodeAndEnqueue(CaptureFramePngQueue queue)
        {
            ValidateQueue(queue);
            return TryCollectEncodeAndEnqueueCore(queue, null);
        }

        /// <summary>
        /// Collects a completed readback, encodes it as PNG, and enqueues the
        /// result into <paramref name="queue"/>, while reconciling the
        /// corresponding <see cref="CaptureFrameRecord"/> in
        /// <paramref name="recordRegistry"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The record is kept while its PNG is successfully enqueued. It is
        /// removed when the readback fails, when the encoded PNG queue is full,
        /// or when an exception leaves the frame unable to continue. The
        /// record's capture frame ID is looked up with
        /// <see cref="CaptureFrameRecordRegistry.TryGet"/> before any resource
        /// work; a missing or mismatched record aborts the operation after the
        /// rented raw slot is released.
        /// </para>
        /// <para>
        /// Main-thread only. Does not own or dispose the queue, registry,
        /// dispatcher, observer, logger, or pool. No file I/O is performed.
        /// </para>
        /// </remarks>
        public CaptureFramePngQueueStatus TryCollectEncodeAndEnqueue(
            CaptureFramePngQueue queue,
            CaptureFrameRecordRegistry recordRegistry)
        {
            ValidateQueue(queue);

            if (recordRegistry == null)
            {
                throw new ArgumentNullException(nameof(recordRegistry));
            }

            return TryCollectEncodeAndEnqueueCore(queue, recordRegistry);
        }

        private static void ValidateQueue(CaptureFramePngQueue queue)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (!queue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }
        }

        private CaptureFramePngQueueStatus TryCollectEncodeAndEnqueueCore(
            CaptureFramePngQueue queue,
            CaptureFrameRecordRegistry recordRegistry)
        {
            if (!_dispatcher.TryCollect(out CaptureFrameReadbackResult collected))
            {
                return CaptureFramePngQueueStatus.None;
            }

            CaptureFrameRecord record = null;
            if (recordRegistry != null)
            {
                bool found;
                try
                {
                    found = recordRegistry.TryGet(collected.FrameRequest, out record);
                }
                catch
                {
                    _dispatcher.Release(collected);
                    throw;
                }

                if (!found)
                {
                    _dispatcher.Release(collected);
                    throw new InvalidOperationException("No capture frame record is registered for the completed capture frame ID.");
                }
            }

            if (collected.HasError)
            {
                return FinishReadbackFailure(recordRegistry, record, collected);
            }

            return FinishEncodeAndEnqueue(queue, recordRegistry, record, collected);
        }

        private CaptureFramePngQueueStatus FinishReadbackFailure(
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFrameRecord record,
            in CaptureFrameReadbackResult collected)
        {
            try
            {
                try
                {
                    _observer.RecordDropped(collected.FrameRequest.TraceContext, CaptureFrameDropReason.ReadbackFailed);
                }
                finally
                {
                    _dispatcher.Release(collected);
                }
            }
            catch
            {
                RemoveRecord(recordRegistry, record, collected.FrameRequest);
                throw;
            }

            RemoveRecord(recordRegistry, record, collected.FrameRequest);
            return CaptureFramePngQueueStatus.Dropped;
        }

        private CaptureFramePngQueueStatus FinishEncodeAndEnqueue(
            CaptureFramePngQueue queue,
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFrameRecord record,
            in CaptureFrameReadbackResult collected)
        {
            NativeArray<byte> encoded = default;
            bool transferred = false;
            bool queued = false;
            try
            {
                try
                {
                    NativeArray<byte> buffer = _dispatcher.GetBuffer(collected);

                    long startTimestamp = Stopwatch.GetTimestamp();
                    encoded = CaptureFramePngEncoder.Encode(buffer, collected.FrameRequest.PixelLayout);
                    long endTimestamp = Stopwatch.GetTimestamp();
                    double elapsedMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

                    _observer.RecordEncoded(collected.FrameRequest.TraceContext, elapsedMilliseconds, encoded.Length);
                }
                finally
                {
                    _dispatcher.Release(collected);
                }

                if (queue.TryEnqueue(collected.FrameRequest, encoded))
                {
                    transferred = true;
                    queued = true;
                }
                else
                {
                    _observer.RecordDropped(collected.FrameRequest.TraceContext, CaptureFrameDropReason.EncodedPngQueueFull);
                }
            }
            catch
            {
                RemoveRecord(recordRegistry, record, collected.FrameRequest);
                throw;
            }
            finally
            {
                if (!transferred && encoded.IsCreated)
                {
                    encoded.Dispose();
                }
            }

            if (queued)
            {
                return CaptureFramePngQueueStatus.Queued;
            }

            RemoveRecord(recordRegistry, record, collected.FrameRequest);
            return CaptureFramePngQueueStatus.Dropped;
        }

        private static void RemoveRecord(
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFrameRecord expected,
            in CaptureFrameRequest request)
        {
            if (recordRegistry == null)
            {
                return;
            }

            if (!recordRegistry.TryRemove(request, out CaptureFrameRecord removed))
            {
                throw new InvalidOperationException("Registry rollback failed: no record matched the completed capture frame.");
            }

            if (!ReferenceEquals(removed, expected))
            {
                throw new InvalidOperationException("Registry rollback failed: the removed record is not the record that was matched.");
            }
        }
    }
}
