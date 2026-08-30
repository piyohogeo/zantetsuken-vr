using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Linear ownership wrapper for a rented capture surface. Caller ownership
    /// transfers only after a backend accepts the submission.
    /// </summary>
    internal sealed class CaptureSurfaceLease : IDisposable
    {
        private readonly CaptureFrameRenderTargetPool _pool;
        private readonly CaptureFrameRenderTargetLease _lease;
        private Guid _backendOwner;
        private CaptureFrameWorkToken _workToken;
        private bool _released;

        internal CaptureSurfaceLease(
            CaptureFrameRenderTargetPool pool,
            in CaptureFrameRenderTargetLease lease)
        {
            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            if (!lease.IsValid)
            {
                throw new ArgumentException("Lease must be valid.", nameof(lease));
            }

            pool.GetRenderTexture(lease);
            _pool = pool;
            _lease = lease;
            _backendOwner = Guid.Empty;
            _workToken = default;
            _released = false;
        }

        internal bool IsCreated => !_released;
        internal bool IsCallerOwned => !_released && _backendOwner == Guid.Empty;
        internal bool IsBackendOwned => !_released && _backendOwner != Guid.Empty;
        internal int SlotIndex => _lease.SlotIndex;

        internal RenderTexture GetSurfaceForCaller()
        {
            if (!IsCallerOwned)
            {
                throw new InvalidOperationException("Surface is not caller-owned.");
            }

            return _pool.GetRenderTexture(_lease);
        }

        internal RenderTexture GetSurfaceForBackend(Guid backendOwner, in CaptureFrameWorkToken token)
        {
            RequireBackendOwner(backendOwner, token);
            return _pool.GetRenderTexture(_lease);
        }

        internal void TransferToBackend(Guid backendOwner, in CaptureFrameWorkToken token)
        {
            if (!IsCallerOwned)
            {
                throw new InvalidOperationException("Surface is not caller-owned.");
            }

            if (backendOwner == Guid.Empty)
            {
                throw new ArgumentException("Backend owner must not be empty.", nameof(backendOwner));
            }

            if (!token.IsValid || token.OwnerToken != backendOwner)
            {
                throw new ArgumentException("Work token must be issued by the backend.", nameof(token));
            }

            _backendOwner = backendOwner;
            _workToken = token;
        }

        internal void ReleaseFromBackend(Guid backendOwner, in CaptureFrameWorkToken token)
        {
            RequireBackendOwner(backendOwner, token);
            _pool.Return(_lease);
            _released = true;
            _backendOwner = Guid.Empty;
            _workToken = default;
        }

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            if (!IsCallerOwned)
            {
                throw new InvalidOperationException("Backend-owned surface cannot be disposed by the caller.");
            }

            _pool.Return(_lease);
            _released = true;
        }

        private void RequireBackendOwner(Guid backendOwner, in CaptureFrameWorkToken token)
        {
            if (!IsBackendOwned || backendOwner == Guid.Empty || _backendOwner != backendOwner || !_workToken.IdenticalTo(token))
            {
                throw new InvalidOperationException("Surface is stale or belongs to another backend operation.");
            }
        }
    }
}
