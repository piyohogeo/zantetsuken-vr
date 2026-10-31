#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The GPU copy of the skin models' geometry (DESIGN 4.5.12): a structured buffer of <see cref="VpSkinVertex"/>
    /// (24 bytes) and a hardware index buffer holding global vertex numbers, apart from the 16-byte copy
    /// (<see cref="VpGpuIndexedGeometryBuffers"/>): no 24-byte vertex is ever put into the 16-byte buffers, nor the
    /// other way round. A model is placed once (<see cref="TryPlace"/>), at the end of what is placed, and its range is
    /// the model's for the buffers' life; nothing is retired or packed, since models are few and shared.
    /// <para>
    /// **Growth, generations, retirement** are those of the 16-byte copy (DESIGN 4.5.4, 4.5.8): a buffer that is short
    /// is replaced by one of at least twice its capacity up to the limit, every placed model is written into the new
    /// one before anything is drawn from it (the models keep their vertices), the CPU-write generations are raised
    /// at every write so that the plugin's route takes a native pointer again, and a replaced buffer is released
    /// once a readback requested after everything submitted with it has completed -- or, through the route, once no
    /// issued event can still name it (<see cref="ReleaseRetired(Action{GraphicsBuffer})"/>).
    /// </para>
    /// </summary>
    public sealed class VpGpuSkinGeometryBuffers : IDisposable
    {
        public const int IndexStride = sizeof(uint);

        private GraphicsBuffer _vertexBuffer;
        private GraphicsBuffer _indexBuffer;
        private readonly List<Retired> _retired = new List<Retired>();
        private readonly List<Placed> _placed = new List<Placed>(4);
        private bool _readbackErrorLogged;
        private bool _disposed;
        private int _verticesPlaced, _indicesPlaced;

        private sealed class Placed
        {
            public VpSkinModel model;
            public VpGeometryRange range;
        }

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

        /// <summary>Buffers of the first capacities, which may grow up to the limits. Throws what GraphicsBuffer throws.</summary>
        public VpGpuSkinGeometryBuffers(int vertexCapacity, int indexCapacity, int maxVertexCapacity, int maxIndexCapacity)
        {
            if (vertexCapacity <= 0 || indexCapacity <= 0 || maxVertexCapacity < vertexCapacity || maxIndexCapacity < indexCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(vertexCapacity), "the first capacities are positive and within the limits");
            }

            _vertexBuffer = NewVertexBuffer(vertexCapacity);
            try
            {
                _indexBuffer = NewIndexBuffer(indexCapacity);
            }
            catch
            {
                _vertexBuffer?.Dispose();
                throw;
            }

            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
            MaxVertexCapacity = maxVertexCapacity;
            MaxIndexCapacity = maxIndexCapacity;
        }

        public int VertexCapacity { get; private set; }
        public int IndexCapacity { get; private set; }
        public int MaxVertexCapacity { get; }
        public int MaxIndexCapacity { get; }

        /// <summary>How many vertices and indices the placed models take.</summary>
        public int VerticesPlaced => _verticesPlaced;
        public int IndicesPlaced => _indicesPlaced;
        public int ModelsPlaced => _placed.Count;

        /// <summary>How many times a buffer was replaced by a larger one, and how many replaced buffers await release.</summary>
        public int GrowthCount { get; private set; }
        public int RetiredCount => _retired.Count;

        /// <summary>The CPU-write generations (DESIGN 4.5.8): raised at every write, so that the plugin's route takes a native pointer again.</summary>
        public uint VertexGeneration { get; private set; }
        public uint IndexGeneration { get; private set; }

        /// <summary>How many model writes were made (a placement, or every model again after a growth).</summary>
        public long ModelWrites { get; private set; }

        public GraphicsBuffer VertexBuffer { get { ThrowIfDisposed(); return _vertexBuffer; } }
        public GraphicsBuffer IndexBuffer { get { ThrowIfDisposed(); return _indexBuffer; } }

        /// <summary>The range a placed model holds; false for a model not placed here.</summary>
        public bool TryGetRange(VpSkinModel model, out VpGeometryRange range)
        {
            ThrowIfDisposed();
            for (int i = 0; i < _placed.Count; i++)
            {
                if (ReferenceEquals(_placed[i].model, model))
                {
                    range = _placed[i].range;
                    return true;
                }
            }

            range = default;
            return false;
        }

        /// <summary>
        /// Places <paramref name="model"/> after what is placed, growing the buffers when they are short (every model
        /// is then written into the new buffers), and writes its vertices and its indices as global vertex numbers.
        /// A model placed already answers its range again. False, placing nothing, when a need lies past a limit,
        /// a buffer cannot be made, or the device cannot read back asynchronously; <paramref name="failure"/> says which.
        /// </summary>
        public bool TryPlace(VpSkinModel model, out VpGeometryRange range, out string failure)
        {
            ThrowIfDisposed();
            failure = null;
            if (model == null || model.IsDisposed) throw new ArgumentException("a live model is placed", nameof(model));
            if (TryGetRange(model, out range)) return true;
            int vertices = _verticesPlaced + model.VertexCount;
            int indices = _indicesPlaced + model.IndexCount;
            if (!TryGrow(vertices, indices, out failure))
            {
                range = default;
                return false;
            }

            range = new VpGeometryRange(_verticesPlaced, model.VertexCount, _indicesPlaced, model.IndexCount);
            var placed = new Placed { model = model, range = range };
            Write(placed);
            _placed.Add(placed);
            _verticesPlaced = vertices;
            _indicesPlaced = indices;
            return true;
        }

        private bool TryGrow(int vertices, int indices, out string failure)
        {
            failure = null;
            bool vertexGrew = vertices > VertexCapacity;
            bool indexGrew = indices > IndexCapacity;
            if (!vertexGrew && !indexGrew) return true;
            if (VpGpuStop.WithholdAllocations)
            {
                failure = "the skin GPU copy is not grown (" + VpGpuStop.Argument + " stage " + VpGpuStop.Stage + ": no allocation)";
                return false;
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                failure = "the device cannot tell when a replaced buffer is no longer used (no asynchronous readback)";
                return false;
            }

            if (vertices > MaxVertexCapacity || indices > MaxIndexCapacity)
            {
                failure = "the skin GPU copy's limit is " + MaxVertexCapacity + " vertices and " + MaxIndexCapacity + " indices, and "
                    + vertices + " vertices and " + indices + " indices were needed";
                return false;
            }

            int vertexCapacity = vertexGrew ? Grown(VertexCapacity, vertices, MaxVertexCapacity) : VertexCapacity;
            int indexCapacity = indexGrew ? Grown(IndexCapacity, indices, MaxIndexCapacity) : IndexCapacity;
            GraphicsBuffer vertexBuffer = null, indexBuffer = null;
            try
            {
                if (vertexGrew) vertexBuffer = NewVertexBuffer(vertexCapacity);
                if (indexGrew) indexBuffer = NewIndexBuffer(indexCapacity);
            }
            catch (Exception exception)
            {
                vertexBuffer?.Dispose();
                indexBuffer?.Dispose();
                failure = "a skin GPU buffer could not be made: " + exception.Message;
                return false;
            }

            if (vertexGrew)
            {
                Retire(_vertexBuffer, VpSkinVertex.Stride);
                _vertexBuffer = vertexBuffer;
                VertexCapacity = vertexCapacity;
            }

            if (indexGrew)
            {
                Retire(_indexBuffer, IndexStride);
                _indexBuffer = indexBuffer;
                IndexCapacity = indexCapacity;
            }

            GrowthCount++;
            // The new buffers hold nothing: every placed model is written again before anything is drawn from them.
            for (int i = 0; i < _placed.Count; i++) Write(_placed[i]);
            return true;
        }

        private void Write(Placed placed)
        {
            VpSkinModel model = placed.model;
            VpGeometryRange range = placed.range;
            NativeArray<uint>.ReadOnly local = model.Indices;
            var global = new NativeArray<uint>(range.indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                uint baseVertex = (uint)range.vertexStart;
                for (int i = 0; i < range.indexCount; i++) global[i] = local[i] + baseVertex;
                // The model's own array is given to the write and not written through (the model is immutable).
                _vertexBuffer.SetData(model.VertexArray, 0, range.vertexStart, range.vertexCount, "VpGpuSkinGeometryBuffers.cs:write-vertices", VpUploadKind.Initial);
                _indexBuffer.SetData(global, 0, range.indexStart, range.indexCount, "VpGpuSkinGeometryBuffers.cs:write-indices", VpUploadKind.Initial);
            }
            finally
            {
                global.Dispose();
            }

            VertexGeneration++;
            IndexGeneration++;
            ModelWrites++;
        }

        private void Retire(GraphicsBuffer buffer, int stride)
        {
            var retired = new Retired { buffer = buffer };
            retired.request = AsyncGPUReadback.Request(buffer, stride, 0, retired.Completed);
            _retired.Add(retired);
        }

        /// <summary>Releases, once each, the replaced buffers whose readback has completed. Never waits.</summary>
        public void ReleaseRetired() => ReleaseRetired(null);

        /// <summary>The same; with <paramref name="giveUp"/> a replaced buffer is given up to the route it was drawn through.</summary>
        public void ReleaseRetired(Action<GraphicsBuffer> giveUp)
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                Retired retired = _retired[i];
                if (!retired.done) continue;
                if (retired.error)
                {
                    if (!_readbackErrorLogged)
                    {
                        _readbackErrorLogged = true;
                        Debug.LogError("VpGpuSkinGeometryBuffers: a readback of a replaced buffer failed; it is kept until Dispose.");
                    }

                    continue;
                }

                if (giveUp != null) giveUp(retired.buffer); else retired.buffer.Dispose();
                _retired.RemoveAt(i);
            }
        }

        public string DescribeRoom()
            => "skin gpu vertices " + _verticesPlaced + " of " + VertexCapacity + " (limit " + MaxVertexCapacity + "), skin gpu indices " + _indicesPlaced + " of " + IndexCapacity
               + " (limit " + MaxIndexCapacity + "), models " + _placed.Count + ", grown " + GrowthCount + " times, replaced buffers awaiting release " + _retired.Count;

        public void Dispose() => Dispose(null);

        /// <summary>Ends the buffers, giving each up to <paramref name="giveUp"/> when one is given; teardown alone waits for a readback still outstanding.</summary>
        public void Dispose(Action<GraphicsBuffer> giveUp)
        {
            if (_disposed) return;
            _disposed = true;
            if (_vertexBuffer != null) { if (giveUp != null) giveUp(_vertexBuffer); else _vertexBuffer.Dispose(); }
            if (_indexBuffer != null) { if (giveUp != null) giveUp(_indexBuffer); else _indexBuffer.Dispose(); }
            foreach (Retired retired in _retired)
            {
                if (!retired.done) retired.request.WaitForCompletion();
                if (giveUp != null) giveUp(retired.buffer); else retired.buffer.Dispose();
            }

            _retired.Clear();
            _placed.Clear();
        }

        private static int Grown(int current, int needed, int limit) => (int)Math.Min(limit, Math.Max((long)needed, (long)current * 2));

        private static GraphicsBuffer NewVertexBuffer(int capacity) => VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, capacity, VpSkinVertex.Stride, "VP Skin Vertices");

        private static GraphicsBuffer NewIndexBuffer(int capacity) => VpGpuStop.Buffer(GraphicsBuffer.Target.Index, capacity, IndexStride, "VP Skin Indices");

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VpGpuSkinGeometryBuffers));
        }
    }
}
