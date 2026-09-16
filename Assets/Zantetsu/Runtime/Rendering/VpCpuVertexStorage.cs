using System;
using Unity.Collections;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity, append-only CPU VP vertex storage (DESIGN 4.5.3). Vertices are written into the uncommitted tail
    /// and become visible when committed; committed vertices are never moved or overwritten while the storage lives,
    /// and they have no read lease in Phase 0.93. After <see cref="Dispose"/>, the view, the tail and committing throw
    /// ObjectDisposedException; disposing again does nothing.
    /// </summary>
    public sealed class VpCpuVertexStorage : IDisposable
    {
        private NativeArray<VpRenderVertex> _vertices;
        private bool _disposed;

        public VpCpuVertexStorage(int vertexCapacity, Allocator allocator)
        {
            if (vertexCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(vertexCapacity), vertexCapacity, "Must not be negative.");
            }

            _vertices = new NativeArray<VpRenderVertex>(vertexCapacity, allocator);
            Capacity = vertexCapacity;
        }

        public int Capacity { get; }

        public int Count { get; private set; }

        /// <summary>The committed vertices, [0, Count). A view into the storage, not a copy.</summary>
        public NativeArray<VpRenderVertex>.ReadOnly Vertices
        {
            get
            {
                ThrowIfDisposed();
                return _vertices.GetSubArray(0, Count).AsReadOnly();
            }
        }

        /// <summary>
        /// A writable view of the <paramref name="count"/> vertices after the committed ones. They stay invisible until
        /// <see cref="Commit"/>, and an abandoned write is simply overwritten by the next one. Only the main thread
        /// writes through it. Throws when the count is negative or exceeds the free tail.
        /// </summary>
        internal NativeArray<VpRenderVertex> GetUncommittedTail(int count)
        {
            ThrowIfDisposed();
            ThrowIfBeyondTail(count);
            return _vertices.GetSubArray(Count, count);
        }

        /// <summary>
        /// A window on committed vertices [start, start + count), for a transfer that reads them where they are instead
        /// of copying them out. The array is **borrowed**: the caller reads it, never writes it, never keeps it past the
        /// call and never disposes it, and it stays valid only while the storage lives. False with a default window when
        /// the range is negative or reaches past the committed vertices.
        /// </summary>
        internal bool TryGetCommittedRange(int start, int count, out NativeArray<VpRenderVertex> range)
        {
            ThrowIfDisposed();
            if (start < 0 || count < 0 || start > Count - count)
            {
                range = default;
                return false;
            }

            range = _vertices.GetSubArray(start, count);
            return true;
        }

        /// <summary>Makes the next <paramref name="count"/> written vertices visible. Throws when the count is negative or exceeds the free tail.</summary>
        internal void Commit(int count)
        {
            ThrowIfDisposed();
            ThrowIfBeyondTail(count);
            Count += count;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _vertices.Dispose();
        }

        private void ThrowIfBeyondTail(int count)
        {
            if (count < 0 || count > Capacity - Count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Must fit the free vertex tail.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCpuVertexStorage));
            }
        }
    }
}
