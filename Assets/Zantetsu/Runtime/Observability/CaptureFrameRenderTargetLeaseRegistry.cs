using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, main-thread-only registry that correlates in-flight
    /// capture requests with the render target lease they are using.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership transition is expressed by the register/remove pair:
    /// the caller registers a request and a lease, and on a successful
    /// <see cref="TryRegister"/> the registry logically takes ownership of the
    /// lease; the caller must not return it to the pool. When the request has
    /// completed, the caller releases the dispatcher result, removes the entry
    /// with <see cref="TryRemove"/>, and only then returns the returned lease to
    /// the <see cref="CaptureFrameRenderTargetPool"/>. This registry never
    /// calls <c>CaptureFrameRenderTargetPool.Return</c>.
    /// </para>
    /// <para>
    /// This registry does not own or dispose the pool. There is deliberately no
    /// public <c>Clear</c> or <c>Dispose</c>: returning every lease at once
    /// while readbacks are still in flight would be unsafe, and a shutdown path
    /// must remove each completed or cancelled request explicitly and return
    /// its lease to the pool in the correct order.
    /// </para>
    /// <para>
    /// Main-thread only; not thread-safe. <see cref="TryRegister"/>,
    /// <see cref="TryGet"/>, and <see cref="TryRemove"/> perform no managed
    /// allocation, LINQ, enumeration, logging, or string generation. Lookup is
    /// a linear scan of fixed arrays keyed by <c>CaptureFrameId</c>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetLeaseRegistry
    {
        private readonly int _capacity;
        private readonly CaptureFrameRenderTargetPool _renderTargetPool;
        private readonly CaptureFrameRequest[] _requests;
        private readonly CaptureFrameRenderTargetLease[] _leases;
        private readonly bool[] _occupied;

        private int _count;
        private long _totalAccepted;
        private long _totalRejected;

        public CaptureFrameRenderTargetLeaseRegistry(
            int capacity,
            CaptureFrameRenderTargetPool renderTargetPool)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            if (renderTargetPool == null)
            {
                throw new ArgumentNullException(nameof(renderTargetPool));
            }

            _capacity = capacity;
            _renderTargetPool = renderTargetPool;
            _requests = new CaptureFrameRequest[capacity];
            _leases = new CaptureFrameRenderTargetLease[capacity];
            _occupied = new bool[capacity];
            _count = 0;
            _totalAccepted = 0;
            _totalRejected = 0;
        }

        public int Capacity => _capacity;

        public int Count => _count;

        public long TotalAccepted => _totalAccepted;

        public long TotalRejected => _totalRejected;

        /// <summary>
        /// Registers an in-flight request with its render target lease. On a
        /// <c>true</c> result the registry takes logical ownership of the lease
        /// and the caller must not return it to the pool. On <c>false</c> or an
        /// exception the lease remains owned by the caller.
        /// </summary>
        public bool TryRegister(in CaptureFrameRequest request, in CaptureFrameRenderTargetLease lease)
        {
            ThrowIfInvalidRequest(request);

            int existing = FindByCaptureFrameId(request.TraceContext.CaptureFrameId);
            if (existing >= 0)
            {
                bool sameRequest = _requests[existing].IdenticalTo(request);
                bool sameLease = _leases[existing].IdenticalTo(lease);

                if (sameRequest && sameLease)
                {
                    throw new ArgumentException("The capture frame is already registered with the same request and lease.", nameof(request));
                }

                throw new InvalidOperationException("The capture frame is already registered with a different request or lease.");
            }

            if (FindByLease(lease) >= 0)
            {
                throw new InvalidOperationException("The lease is already registered for a different capture frame.");
            }

            _renderTargetPool.GetRenderTexture(lease);

            if (_count == _capacity)
            {
                _totalRejected++;
                return false;
            }

            int free = FindFreeSlot();
            _requests[free] = request;
            _leases[free] = lease;
            _occupied[free] = true;
            _count++;
            _totalAccepted++;
            return true;
        }

        /// <summary>
        /// Returns a non-owning reference copy of the lease registered for the
        /// request. The caller must <b>not</b> return the returned lease to the
        /// pool; the registry still owns it until <see cref="TryRemove"/>.
        /// </summary>
        public bool TryGet(in CaptureFrameRequest request, out CaptureFrameRenderTargetLease lease)
        {
            ThrowIfInvalidRequest(request);

            int index = FindByCaptureFrameId(request.TraceContext.CaptureFrameId);
            if (index < 0)
            {
                lease = default;
                return false;
            }

            if (!_requests[index].IdenticalTo(request))
            {
                throw new InvalidOperationException("The registered request does not match the supplied request.");
            }

            lease = _leases[index];
            return true;
        }

        /// <summary>
        /// Removes the entry for the request and transfers lease ownership back
        /// to the caller. Does <b>not</b> call
        /// <c>CaptureFrameRenderTargetPool.Return</c>; the caller must release
        /// the dispatcher result first, then remove, then return to the pool.
        /// </summary>
        public bool TryRemove(in CaptureFrameRequest request, out CaptureFrameRenderTargetLease lease)
        {
            ThrowIfInvalidRequest(request);

            int index = FindByCaptureFrameId(request.TraceContext.CaptureFrameId);
            if (index < 0)
            {
                lease = default;
                return false;
            }

            if (!_requests[index].IdenticalTo(request))
            {
                throw new InvalidOperationException("The registered request does not match the supplied request.");
            }

            lease = _leases[index];
            _requests[index] = default;
            _leases[index] = default;
            _occupied[index] = false;
            _count--;
            return true;
        }

        private static void ThrowIfInvalidRequest(in CaptureFrameRequest request)
        {
            if (!request.IsValid || request.TraceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentException("Request must be valid with a positive CaptureFrameId.", nameof(request));
            }
        }

        private int FindByCaptureFrameId(long captureFrameId)
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i] && _requests[i].TraceContext.CaptureFrameId == captureFrameId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindByLease(in CaptureFrameRenderTargetLease lease)
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i] && _leases[i].IdenticalTo(lease))
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (!_occupied[i])
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
