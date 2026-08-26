using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Poll-based dispatcher that connects UnityRenderTexture capture requests
    /// to <c>AsyncGPUReadback</c>. Owns no buffers: it rents slots from a
    /// <see cref="CaptureFrameReadbackBufferPool"/> for the lifetime of each
    /// in-flight request and returns them on <see cref="Release"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only. All state lives in fixed arrays allocated by the
    /// constructor; the start and poll paths perform no managed allocation,
    /// LINQ, logging, or string formatting.
    /// </para>
    /// <para>
    /// Does not own or dispose the pool, and does not force-complete or cancel
    /// GPU requests. A source render texture must not be released or destroyed
    /// until its readback request has completed and the result has been
    /// collected.
    /// </para>
    /// </remarks>
    public sealed class UnityRenderTextureReadbackDispatcher : IDisposable
    {
        private readonly CaptureFrameReadbackBufferPool _pool;
        private readonly int _capacity;
        private readonly int _bytesPerSlot;
        private readonly Guid _token;

        private readonly AsyncGPUReadbackRequest[] _requests;
        private readonly CaptureFrameRequest[] _frameRequests;
        private readonly long[] _operationIds;
        private readonly int[] _slots;
        private readonly bool[] _active;
        private readonly bool[] _delivered;
        private readonly bool[] _hasError;

        private long _nextOperationId;
        private int _activeCount;
        private bool _disposed;

        public UnityRenderTextureReadbackDispatcher(CaptureFrameReadbackBufferPool bufferPool)
        {
            if (bufferPool == null)
            {
                throw new ArgumentNullException(nameof(bufferPool));
            }

            _pool = bufferPool;
            _capacity = bufferPool.SlotCount;
            _bytesPerSlot = bufferPool.BytesPerSlot;

            _token = Guid.NewGuid();
            _requests = new AsyncGPUReadbackRequest[_capacity];
            _frameRequests = new CaptureFrameRequest[_capacity];
            _operationIds = new long[_capacity];
            _slots = new int[_capacity];
            _active = new bool[_capacity];
            _delivered = new bool[_capacity];
            _hasError = new bool[_capacity];

            for (int i = 0; i < _capacity; i++)
            {
                _slots[i] = -1;
            }

            _nextOperationId = 1;
            _activeCount = 0;
            _disposed = false;
        }

        public bool IsCreated => !_disposed;

        public int ActiveCount
        {
            get
            {
                ThrowIfDisposed();
                return _activeCount;
            }
        }

        public int Capacity
        {
            get
            {
                ThrowIfDisposed();
                return _capacity;
            }
        }

        /// <summary>
        /// Starts an asynchronous readback for a valid UnityRenderTexture
        /// request. Returns false when no pool slot is available. The source
        /// render texture must not be released or destroyed until the request
        /// has completed and its result has been collected.
        /// </summary>
        public bool TryStart(in CaptureFrameRequest request, RenderTexture source)
        {
            ThrowIfDisposed();
            ValidateStart(request, source);

            int index = -1;
            for (int i = 0; i < _capacity; i++)
            {
                if (!_active[i])
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return false;
            }

            if (_nextOperationId == long.MaxValue)
            {
                throw new OverflowException("Operation ID sequence exhausted.");
            }

            if (!_pool.TryRent(out int slot))
            {
                return false;
            }

            NativeArray<byte> output = _pool.GetBuffer(slot).GetSubArray(0, request.RequiredByteCount);

            AsyncGPUReadbackRequest gpuRequest;
            using (ZantetsuProfilerMarkers.CaptureCopy.Auto())
            {
                try
                {
                    gpuRequest = AsyncGPUReadback.RequestIntoNativeArray(
                        ref output,
                        source,
                        0,
                        request.ImageRect.X,
                        request.ImageRect.Width,
                        request.ImageRect.Y,
                        request.ImageRect.Height,
                        request.ArrayIndex,
                        1,
                        TextureFormat.RGBA32,
                        null);
                }
                catch
                {
                    _pool.Return(slot);
                    throw;
                }
            }

            _requests[index] = gpuRequest;
            _frameRequests[index] = request;
            _operationIds[index] = _nextOperationId;
            _nextOperationId++;
            _slots[index] = slot;
            _hasError[index] = false;
            _active[index] = true;
            _delivered[index] = false;
            _activeCount++;

            return true;
        }

        /// <summary>
        /// Collects one completed but not yet delivered request, scanning the
        /// fixed entry array in order. Returns false with
        /// <paramref name="result"/> set to default when nothing is ready.
        /// </summary>
        public bool TryCollect(out CaptureFrameReadbackResult result)
        {
            ThrowIfDisposed();

            for (int i = 0; i < _capacity; i++)
            {
                if (_active[i] && !_delivered[i] && _requests[i].done)
                {
                    _delivered[i] = true;
                    _hasError[i] = _requests[i].hasError;
                    result = new CaptureFrameReadbackResult(
                        _token,
                        _operationIds[i],
                        _frameRequests[i],
                        _slots[i],
                        _requests[i].hasError);
                    return true;
                }
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Returns a non-owned view over the first
        /// <see cref="CaptureFrameRequest.RequiredByteCount"/> bytes of a
        /// successful result's buffer. Throws for errored results.
        /// </summary>
        public NativeArray<byte> GetBuffer(in CaptureFrameReadbackResult result)
        {
            int i = FindDeliveredEntry(result);

            if (result.HasError)
            {
                throw new InvalidOperationException("Cannot obtain the buffer of an errored result.");
            }

            return _pool.GetBuffer(_slots[i]).GetSubArray(0, _frameRequests[i].RequiredByteCount);
        }

        /// <summary>
        /// Returns the result's pooled slot and frees the active entry. Works
        /// for both successful and errored results.
        /// </summary>
        public void Release(in CaptureFrameReadbackResult result)
        {
            int i = FindDeliveredEntry(result);
            int slot = _slots[i];

            _active[i] = false;
            _delivered[i] = false;
            _requests[i] = default;
            _frameRequests[i] = default;
            _operationIds[i] = 0;
            _slots[i] = -1;
            _hasError[i] = false;
            _activeCount--;

            _pool.Return(slot);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_activeCount != 0)
            {
                throw new InvalidOperationException("Cannot dispose while operations are active.");
            }

            _disposed = true;
        }

        private void ValidateStart(in CaptureFrameRequest request, RenderTexture source)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (request.Source != CaptureSource.UnityRenderTexture)
            {
                throw new ArgumentException("Request source must be UnityRenderTexture.", nameof(request));
            }

            if (request.PixelLayout.Format != CapturePixelFormat.Rgba32)
            {
                throw new ArgumentException("Only Rgba32 captures are supported.", nameof(request));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!source.IsCreated())
            {
                throw new ArgumentException("Source render texture is not created.", nameof(source));
            }

            TextureDimension dimension = source.dimension;
            if (dimension != TextureDimension.Tex2D && dimension != TextureDimension.Tex2DArray)
            {
                throw new ArgumentException("Source must be a Tex2D or Tex2DArray render texture.", nameof(source));
            }

            CaptureImageRect rect = request.ImageRect;
            if (rect.X + rect.Width > source.width || rect.Y + rect.Height > source.height)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Image rect must be fully contained within the source.");
            }

            if (dimension == TextureDimension.Tex2D)
            {
                if (request.ArrayIndex != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(request), "Tex2D captures require ArrayIndex 0.");
                }
            }
            else if (request.ArrayIndex >= source.volumeDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Tex2DArray captures require an ArrayIndex within volumeDepth.");
            }

            if (_bytesPerSlot < request.RequiredByteCount)
            {
                throw new InvalidOperationException("Pool slot is too small for the request.");
            }
        }

        private int FindDeliveredEntry(in CaptureFrameReadbackResult result)
        {
            ThrowIfDisposed();

            if (!result.IsValid)
            {
                throw new InvalidOperationException("Result is not valid.");
            }

            if (result.OwnerToken != _token)
            {
                throw new InvalidOperationException("Result belongs to another dispatcher.");
            }

            for (int i = 0; i < _capacity; i++)
            {
                if (_active[i] && _delivered[i] && _operationIds[i] == result.OperationId)
                {
                    if (_slots[i] != result.BufferSlotIndex)
                    {
                        throw new InvalidOperationException("Result does not match the recorded operation.");
                    }

                    if (result.HasError != _hasError[i])
                    {
                        throw new InvalidOperationException("Result error state does not match the recorded operation.");
                    }

                    return i;
                }
            }

            throw new InvalidOperationException("Result is not a currently delivered operation.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UnityRenderTextureReadbackDispatcher));
            }
        }
    }
}
