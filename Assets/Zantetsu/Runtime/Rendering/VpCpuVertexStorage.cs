using System;
using Unity.Collections;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// CPU VP vertex storage (DESIGN 4.5.3, 4.5.4): one linear array in a reserved address range whose pages are
    /// committed as room is given out (<see cref="VpVirtualArray{T}"/>). <see cref="Capacity"/> is the reservation, the
    /// absolute limit; <see cref="CommittedCapacity"/> is what may be used now, and it grows without moving anything.
    /// Room is given out as spans by the geometry storage's own allocator, which commits a span before a writer gets
    /// it; a writer fills the span it holds; a span becomes visible when it is published. Published vertices are never
    /// moved while the storage lives, and they have no read lease in Phase 0.93.
    /// <para>
    /// <see cref="Count"/> is how far publishing has reached, not how many vertices are live: several spans may be
    /// open at once and publish in any order, so a slot below it may be one nobody has published, or one that was
    /// published and whose geometry has gone. Such a slot holds whatever it last held, and no published geometry names
    /// it. What is read is always named by a geometry's own vertex range.
    /// </para>
    /// After <see cref="Dispose"/>, the view, the spans and publishing throw ObjectDisposedException; disposing again
    /// does nothing.
    /// </summary>
    public sealed class VpCpuVertexStorage : IDisposable
    {
        private readonly VpVirtualArray<VpRenderVertex> _memory;
        private NativeArray<VpRenderVertex> _vertices;
        private bool _disposed;

        /// <summary>
        /// A storage whose reservation and first commit are both <paramref name="vertexCapacity"/>: the fixed-size
        /// storage it used to be, on the product's page backing. The allocator is not used for the vertices.
        /// </summary>
        public VpCpuVertexStorage(int vertexCapacity, Allocator allocator)
            : this(VpWindowsPageBacking.Instance, vertexCapacity, vertexCapacity)
        {
        }

        /// <summary>
        /// Reserves <paramref name="reservedVertices"/> and commits the first <paramref name="initialCommittedVertices"/>.
        /// Throws <see cref="InvalidOperationException"/> when the backing refuses either: the caller turns that into
        /// the common termination.
        /// </summary>
        public VpCpuVertexStorage(IVpPageBacking backing, int reservedVertices, int initialCommittedVertices)
        {
            if (reservedVertices < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reservedVertices), reservedVertices, "Must not be negative.");
            }

            if (!VpVirtualArray<VpRenderVertex>.TryCreate(backing, reservedVertices, initialCommittedVertices, out _memory, out string failure))
            {
                throw new InvalidOperationException("the vertex storage's backing could not be established: " + failure);
            }

            _vertices = _memory.View;
            Capacity = reservedVertices;
        }

        /// <summary>The reservation: the most vertices this storage can ever hold.</summary>
        public int Capacity { get; }

        /// <summary>How many vertices from 0 are committed and may be given out now.</summary>
        public int CommittedCapacity => _memory.CommittedLength;

        public long ReservedBytes => _memory.ReservedBytes;

        public long CommittedBytes => _memory.CommittedBytes;

        /// <summary>Commits [0, end) if it is not; see <see cref="VpVirtualArray{T}.TryCommitTo"/>.</summary>
        internal bool TryCommitTo(int end, out string failure)
        {
            ThrowIfDisposed();
            return _memory.TryCommitTo(end, out failure);
        }

        /// <summary>How far publishing has reached: every published vertex lies below this.</summary>
        public int Count { get; private set; }

        /// <summary>The vertices publishing has reached, [0, Count). A view into the storage, not a copy.</summary>
        public NativeArray<VpRenderVertex>.ReadOnly Vertices
        {
            get
            {
                ThrowIfDisposed();
                return _vertices.GetSubArray(0, Count).AsReadOnly();
            }
        }

        /// <summary>
        /// A writable view of the <paramref name="count"/> vertices from <paramref name="start"/>: the span its holder
        /// was given room for, and nothing beyond it. They stay invisible until <see cref="Publish"/>, and a span given
        /// back unwritten is simply taken again later. **The holder of that span writes through it**, which may be a
        /// worker: several spans are open at once and each is written where it was given room. Throws when the span
        /// lies outside the capacity.
        /// </summary>
        internal NativeArray<VpRenderVertex> GetSpan(int start, int count)
        {
            ThrowIfDisposed();
            ThrowIfOutsideCapacity(start, count);
            ThrowIfUncommitted(start, count);
            return _vertices.GetSubArray(start, count);
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

        /// <summary>
        /// Makes the <paramref name="count"/> vertices from <paramref name="start"/> visible, which moves
        /// <see cref="Count"/> on when they reach further than anything published before. Throws when the span lies
        /// outside the capacity.
        /// </summary>
        internal void Publish(int start, int count)
        {
            ThrowIfDisposed();
            ThrowIfOutsideCapacity(start, count);
            Count = Math.Max(Count, start + count);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _vertices = default;
            _memory.Dispose();
        }

        // A span handed out must lie in committed pages: the geometry storage commits before it gives room.
        private void ThrowIfUncommitted(int start, int count)
        {
            if ((long)start + count > _memory.CommittedLength)
            {
                throw new InvalidOperationException(
                    "A span lies within the committed vertices: " + count + " from " + start + " of " + _memory.CommittedLength + " committed.");
            }
        }

        private void ThrowIfOutsideCapacity(int start, int count)
        {
            if (start < 0 || count < 0 || start > Capacity - count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, "A span lies within the capacity: " + count + " from " + start + " of " + Capacity + ".");
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
