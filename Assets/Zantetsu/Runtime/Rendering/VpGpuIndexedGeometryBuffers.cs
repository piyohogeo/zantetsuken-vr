using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// VP Stage 3 GPU copy of a VP CPU pool (DESIGN 4.5.4) for indexed indirect draws: a structured buffer of
    /// <see cref="VpRenderVertex"/>, read by the vertex-pulling shader, and a hardware index buffer
    /// (GraphicsBuffer.Target.Index, uint) holding the pool's indices, which already address global vertex numbers.
    /// D3D11 cannot use one buffer as both an index and a structured buffer, so this owns an index buffer of its own and
    /// <see cref="VpGpuGeometryBuffers"/> keeps Stage 2's structured index buffer unchanged.
    /// <para>
    /// **Growth (DESIGN 4.5.4).** The buffers start at a fixed first capacity and may be replaced by larger ones up to
    /// a limit: <see cref="TryGrow"/> makes the new buffer, and the caller transfers into it, from the CPU copy, what is
    /// drawn, before anything is drawn from it. The properties answer the current buffers, and every draw binds them
    /// when it is issued, so the replacement takes effect at the next draw -- a drawing boundary. A replaced buffer is
    /// not released at once: an asynchronous readback of its first element is requested at the switch, after every
    /// GPU command already submitted with it, and the buffer is released exactly once when that readback has
    /// completed -- as <see cref="VpGpuGeometryBuffers"/> does for Stage 2. Normal updates never wait for the GPU; only
    /// <see cref="Dispose"/>, at teardown, waits for a readback still outstanding. A readback that fails keeps its
    /// buffer until Dispose (logged once). Nothing is drawn from a degraded copy.
    /// </para>
    /// After <see cref="Dispose"/>, uploading and the buffers throw; disposing again does nothing.
    /// </summary>
    public sealed class VpGpuIndexedGeometryBuffers : IDisposable
    {
        public const int IndexStride = sizeof(uint);

        private GraphicsBuffer _vertexBuffer;
        private GraphicsBuffer _indexBuffer;
        private readonly List<Retired> _retired = new List<Retired>();
        private bool _readbackErrorLogged;
        private bool _disposed;

        // A replaced buffer and the readback that says the GPU is past everything submitted with it. The callback runs
        // on the main thread; a completed request is later disposed by Unity, so its outcome is copied here.
        private sealed class Retired
        {
            public GraphicsBuffer buffer;
            public AsyncGPUReadbackRequest request;
            public bool done;
            public bool error;

            public void Completed(AsyncGPUReadbackRequest completed)
            {
                error = completed.hasError;
                done = true;
            }
        }

        /// <summary>Fixed buffers: the first capacity is also the limit.</summary>
        public VpGpuIndexedGeometryBuffers(int vertexCapacity, int indexCapacity)
            : this(vertexCapacity, indexCapacity, vertexCapacity, indexCapacity)
        {
        }

        /// <summary>
        /// Buffers of the first capacity given, which may grow up to the limits given. Throws what GraphicsBuffer throws
        /// when the first ones cannot be made.
        /// </summary>
        public VpGpuIndexedGeometryBuffers(int vertexCapacity, int indexCapacity, int maxVertexCapacity, int maxIndexCapacity)
        {
            if (vertexCapacity <= 0 || indexCapacity <= 0 || maxVertexCapacity < vertexCapacity || maxIndexCapacity < indexCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(vertexCapacity), "the first capacities are positive and within the limits");
            }

            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            _vertexBuffer = NewVertexBuffer(vertexCapacity);
            try
            {
                _indexBuffer = NewIndexBuffer(indexCapacity);
            }
            catch
            {
                _vertexBuffer.Dispose();
                throw;
            }

            CreationSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
            MaxVertexCapacity = maxVertexCapacity;
            MaxIndexCapacity = maxIndexCapacity;
        }

        /// <summary>The current vertex buffer's capacity.</summary>
        public int VertexCapacity { get; private set; }

        /// <summary>The current index buffer's capacity.</summary>
        public int IndexCapacity { get; private set; }

        /// <summary>The most vertices a buffer of this copy may ever hold.</summary>
        public int MaxVertexCapacity { get; }

        /// <summary>The most indices a buffer of this copy may ever hold.</summary>
        public int MaxIndexCapacity { get; }

        /// <summary>How many times a buffer has been replaced by a larger one.</summary>
        public int GrowthCount { get; private set; }

        /// <summary>How many times the vertex buffer, and the index buffer, were each replaced by a larger one (2026-10-01).</summary>
        public int VertexGrowthCount { get; private set; }
        public int IndexGrowthCount { get; private set; }

        /// <summary>The most replaced buffers awaiting their release at once.</summary>
        public int MaxRetiredCount { get; private set; }

        /// <summary>The time this object took to make its first two buffers (observation).</summary>
        public double CreationSeconds { get; private set; }

        /// <summary>Replaced buffers not yet released.</summary>
        public int RetiredCount => _retired.Count;

        /// <summary>
        /// The CPU-write generations of the vertex and index buffers (DESIGN 4.5.8): raised at every upload, so that
        /// the plugin's route takes a native pointer again after one (Unity may change the native buffer when its
        /// data is written through its APIs). A replaced buffer is a new object, seen by itself.
        /// </summary>
        public uint VertexGeneration { get; private set; }

        public uint IndexGeneration { get; private set; }

        /// <summary>The structured vertex buffer, for binding. It stays owned and released by this object.</summary>
        public GraphicsBuffer VertexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _vertexBuffer;
            }
        }

        /// <summary>The hardware index buffer, for indexed draws. It stays owned and released by this object.</summary>
        public GraphicsBuffer IndexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _indexBuffer;
            }
        }

        /// <summary>
        /// Makes the buffers hold at least <paramref name="vertices"/> and <paramref name="indices"/>: a buffer that is
        /// short is replaced by a new one of at least twice its capacity (or what is needed, if more), never past its
        /// limit. The new buffer holds nothing yet: the caller transfers what is drawn into it before the next draw.
        /// The one replaced is kept until the readback requested for it now has completed. False, changing nothing, when
        /// a need lies past its limit, a buffer cannot be made or the device cannot read back asynchronously;
        /// <paramref name="failure"/> says which. That is the GPU backing not being established (DESIGN 4.5.4).
        /// </summary>
        public bool TryGrow(int vertices, int indices, out bool vertexGrew, out bool indexGrew, out string failure)
        {
            ThrowIfDisposed();
            vertexGrew = vertices > VertexCapacity;
            indexGrew = indices > IndexCapacity;
            failure = null;
            if (!vertexGrew && !indexGrew)
            {
                return true;
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                failure = "the device cannot tell when a replaced buffer is no longer used (no asynchronous readback)";
                vertexGrew = indexGrew = false;
                return false;
            }

            if (vertices > MaxVertexCapacity || indices > MaxIndexCapacity)
            {
                failure = "the GPU copy's limit is " + MaxVertexCapacity + " vertices and " + MaxIndexCapacity + " indices, and "
                          + vertices + " vertices and " + indices + " indices were needed";
                vertexGrew = indexGrew = false;
                return false;
            }

            int vertexCapacity = vertexGrew ? Grown(VertexCapacity, vertices, MaxVertexCapacity) : VertexCapacity;
            int indexCapacity = indexGrew ? Grown(IndexCapacity, indices, MaxIndexCapacity) : IndexCapacity;
            GraphicsBuffer vertexBuffer = null;
            GraphicsBuffer indexBuffer = null;
            try
            {
                if (vertexGrew) vertexBuffer = NewVertexBuffer(vertexCapacity);
                if (indexGrew) indexBuffer = NewIndexBuffer(indexCapacity);
            }
            catch (Exception exception)
            {
                vertexBuffer?.Dispose();
                indexBuffer?.Dispose();
                failure = "a GPU buffer of " + (vertexGrew ? vertexCapacity + " vertices" : "") + (vertexGrew && indexGrew ? " and " : "")
                          + (indexGrew ? indexCapacity + " indices" : "") + " could not be made: " + exception.Message;
                vertexGrew = indexGrew = false;
                return false;
            }

            if (vertexGrew)
            {
                VertexGrowthCount++;
                Retire(_vertexBuffer, VpRenderVertex.Stride);
                _vertexBuffer = vertexBuffer;
                VertexCapacity = vertexCapacity;
            }

            if (indexGrew)
            {
                IndexGrowthCount++;
                Retire(_indexBuffer, IndexStride);
                _indexBuffer = indexBuffer;
                IndexCapacity = indexCapacity;
            }

            GrowthCount++;
            return true;
        }

        private void Retire(GraphicsBuffer buffer, int stride)
        {
            var retired = new Retired { buffer = buffer };
            retired.request = AsyncGPUReadback.Request(buffer, stride, 0, retired.Completed);
            _retired.Add(retired);
            MaxRetiredCount = Math.Max(MaxRetiredCount, _retired.Count);
        }

        /// <summary>
        /// Releases, once each, the replaced buffers whose readback has completed. One whose readback failed is kept
        /// until Dispose. Never waits.
        /// </summary>
        public void ReleaseRetired()
        {
            ReleaseRetired(null);
        }

        /// <summary>
        /// The same; with <paramref name="giveUp"/> (the owner's hand-over to the plugin's route or routes the buffers
        /// were drawn through) a replaced buffer is given up to it, and disposed once no issued event can still name it.
        /// </summary>
        public void ReleaseRetired(Action<GraphicsBuffer> giveUp)
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                Retired retired = _retired[i];
                if (!retired.done)
                {
                    continue;
                }

                if (retired.error)
                {
                    if (!_readbackErrorLogged)
                    {
                        _readbackErrorLogged = true;
                        Debug.LogError("VpGpuIndexedGeometryBuffers: a readback of a replaced buffer failed; it is kept until Dispose.");
                    }

                    continue;
                }

                if (giveUp != null) giveUp(retired.buffer); else retired.buffer.Dispose();
                _retired.RemoveAt(i);
            }
        }

        /// <summary>The copy's room, in words. Log text only.</summary>
        public string DescribeRoom()
            => "gpu vertices " + VertexCapacity + " of limit " + MaxVertexCapacity + ", gpu indices " + IndexCapacity + " of limit " + MaxIndexCapacity
               + ", grown " + GrowthCount + " times, replaced buffers awaiting release " + _retired.Count;

        /// <summary>
        /// Writes the pool's appended vertices and indices from the start of the buffers. An empty pool succeeds without
        /// writing. Returns false, writing nothing, when either count exceeds its capacity.
        /// </summary>
        public bool TryUpload(VpCpuGeometryPool pool)
        {
            ThrowIfDisposed();
            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            NativeArray<VpRenderVertex> vertices = pool.AppendedVertices;
            NativeArray<uint> indices = pool.AppendedIndices;
            if (vertices.Length > VertexCapacity || indices.Length > IndexCapacity)
            {
                return false;
            }

            if (vertices.Length > 0)
            {
                _vertexBuffer.SetData(vertices);
                VertexGeneration++;
            }

            if (indices.Length > 0)
            {
                _indexBuffer.SetData(indices);
                IndexGeneration++;
            }

            return true;
        }

        public void Dispose()
        {
            Dispose(null);
        }

        /// <summary>
        /// Ends the buffers. With <paramref name="giveUp"/> (the owner's hand-over to the plugin's route or routes they
        /// were drawn through) the current and the replaced buffers are given up to it, and disposed each once no
        /// issued event can still name it; without one they are disposed here.
        /// </summary>
        public void Dispose(Action<GraphicsBuffer> giveUp)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (giveUp != null) giveUp(_vertexBuffer); else _vertexBuffer.Dispose();
            if (giveUp != null) giveUp(_indexBuffer); else _indexBuffer.Dispose();

            // Teardown alone waits: for each replaced buffer's readback, so the GPU is past it when it is released.
            foreach (Retired retired in _retired)
            {
                if (!retired.done)
                {
                    retired.request.WaitForCompletion();
                }

                if (giveUp != null) giveUp(retired.buffer); else retired.buffer.Dispose();
            }

            _retired.Clear();
        }

        private static int Grown(int current, int needed, int limit)
            => (int)Math.Min(limit, Math.Max((long)needed, (long)current * 2));

        private static GraphicsBuffer NewVertexBuffer(int capacity)
            => new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, VpRenderVertex.Stride) { name = "VP Indexed Vertices" };

        private static GraphicsBuffer NewIndexBuffer(int capacity)
            => new GraphicsBuffer(GraphicsBuffer.Target.Index, capacity, IndexStride) { name = "VP Indexed Indices" };

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpGpuIndexedGeometryBuffers));
            }
        }
    }
}
