using System;
using System.Runtime.ExceptionServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, main-thread-only pool of reusable capture render targets
    /// for <c>AsyncGPUReadback</c>. Each slot owns a single 2D
    /// <see cref="RenderTexture"/> sized from the supplied
    /// <see cref="CaptureFrameProfile"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pool owns its render textures; a lease and the value returned by
    /// <see cref="GetRenderTexture"/> are non-owning. If starting a readback
    /// fails because of backpressure, the caller keeps the same lease and the
    /// render texture's contents unchanged and retries. Once a readback has
    /// started successfully, the caller must not return, dispose, re-draw,
    /// release, or destroy the texture until that request has completed and been
    /// collected. This pool does not auto-track that constraint.
    /// </para>
    /// <para>
    /// The pool and its leases are main-thread only and <b>not</b>
    /// thread-safe. <see cref="TryRent"/>, <see cref="GetRenderTexture"/>, and
    /// <see cref="Return"/> perform no managed allocation, LINQ, enumeration,
    /// logging, or string generation. The profile is referenced but not owned,
    /// mutated, or disposed.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetPool : IDisposable
    {
        private readonly Guid _ownerToken;
        private readonly int _capacity;
        private readonly CaptureFrameProfile _profile;
        private readonly RenderTexture[] _textures;
        private readonly bool[] _rented;
        private readonly long[] _generations;

        private int _rentedCount;
        private bool _disposed;

        public CaptureFrameRenderTargetPool(
            int capacity,
            CaptureFrameProfile profile)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (profile.Source != CaptureSource.UnityRenderTexture)
            {
                throw new ArgumentException("Profile source must be UnityRenderTexture.", nameof(profile));
            }

            if (profile.PixelFormat != CapturePixelFormat.Rgba32)
            {
                throw new ArgumentException("Profile pixel format must be Rgba32.", nameof(profile));
            }

            if (profile.ArrayIndex != 0)
            {
                throw new ArgumentException("Array index must be zero for the 2D render texture path.", nameof(profile));
            }

            _ownerToken = Guid.NewGuid();
            _capacity = capacity;
            _profile = profile;
            _textures = new RenderTexture[capacity];
            _rented = new bool[capacity];
            _generations = new long[capacity];
            _rentedCount = 0;
            _disposed = false;

            int width = profile.ImageRect.X + profile.ImageRect.Width;
            int height = profile.ImageRect.Y + profile.ImageRect.Height;

            Exception creationFailure = null;
            try
            {
                for (int i = 0; i < capacity; i++)
                {
                    RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    _textures[i] = rt;

                    rt.dimension = TextureDimension.Tex2D;
                    rt.volumeDepth = 1;
                    rt.antiAliasing = 1;
                    rt.useMipMap = false;
                    rt.enableRandomWrite = false;
                    rt.Create();

                    if (!rt.IsCreated())
                    {
                        throw new InvalidOperationException("Failed to create a render texture.");
                    }
                }
            }
            catch (Exception ex)
            {
                creationFailure = ex;
            }

            if (creationFailure != null)
            {
                Exception cleanupFailure = ReleaseAndDestroyAll();
                if (cleanupFailure == null)
                {
                    ExceptionDispatchInfo.Capture(creationFailure).Throw();
                }
                else
                {
                    throw new AggregateException(creationFailure, cleanupFailure);
                }
            }
        }

        public bool IsCreated => !_disposed;

        public int Capacity
        {
            get
            {
                ThrowIfDisposed();
                return _capacity;
            }
        }

        public int RentedCount
        {
            get
            {
                ThrowIfDisposed();
                return _rentedCount;
            }
        }

        public CaptureFrameProfile Profile
        {
            get
            {
                ThrowIfDisposed();
                return _profile;
            }
        }

        public bool TryRent(out CaptureFrameRenderTargetLease lease)
        {
            ThrowIfDisposed();

            if (_rentedCount == _capacity)
            {
                lease = default;
                return false;
            }

            for (int i = 0; i < _capacity; i++)
            {
                if (!_rented[i])
                {
                    _rented[i] = true;
                    _rentedCount++;
                    lease = new CaptureFrameRenderTargetLease(_ownerToken, i, _generations[i]);
                    return true;
                }
            }

            lease = default;
            return false;
        }

        public RenderTexture GetRenderTexture(in CaptureFrameRenderTargetLease lease)
        {
            ThrowIfDisposed();
            ValidateLease(lease, out int slot);

            RenderTexture rt = _textures[slot];
            if (rt == null || !rt.IsCreated())
            {
                throw new InvalidOperationException("The render texture is not created.");
            }

            return rt;
        }

        public void Return(in CaptureFrameRenderTargetLease lease)
        {
            ThrowIfDisposed();
            ValidateLease(lease, out int slot);

            _rented[slot] = false;
            _rentedCount--;
            _generations[slot]++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_rentedCount != 0)
            {
                throw new InvalidOperationException("Cannot dispose while render targets are rented.");
            }

            Exception cleanupFailure = ReleaseAndDestroyAll();
            if (cleanupFailure != null)
            {
                // Remaining non-null slots are left in place so a later
                // Dispose() call can retry their cleanup.
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }

            _disposed = true;
        }

        private void ValidateLease(in CaptureFrameRenderTargetLease lease, out int slot)
        {
            if (!lease.IsValid)
            {
                throw new InvalidOperationException("The lease is invalid.");
            }

            if (lease.OwnerToken != _ownerToken)
            {
                throw new InvalidOperationException("The lease belongs to a different pool.");
            }

            int index = lease.SlotIndex;
            if (index < 0 || index >= _capacity)
            {
                throw new InvalidOperationException("The lease slot index is out of range.");
            }

            if (lease.Generation != _generations[index])
            {
                throw new InvalidOperationException("The lease is stale.");
            }

            if (!_rented[index])
            {
                throw new InvalidOperationException("The lease slot is not currently rented.");
            }

            slot = index;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CaptureFrameRenderTargetPool));
            }
        }

        private Exception ReleaseAndDestroyAll()
        {
            Exception first = null;

            for (int i = 0; i < _capacity; i++)
            {
                RenderTexture rt = _textures[i];
                if (rt == null)
                {
                    continue;
                }

                try
                {
                    ReleaseAndDestroy(rt);
                    _textures[i] = null;
                }
                catch (Exception ex)
                {
                    if (first == null)
                    {
                        first = ex;
                    }
                    else
                    {
                        first = new AggregateException(first, ex);
                    }
                }
            }

            return first;
        }

        private static void ReleaseAndDestroy(RenderTexture rt)
        {
            rt.Release();

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(rt);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
