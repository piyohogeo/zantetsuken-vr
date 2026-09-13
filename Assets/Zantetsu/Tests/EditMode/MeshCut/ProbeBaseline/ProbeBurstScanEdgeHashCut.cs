using System;
using Unity.Collections;
using Unity.Jobs;

namespace Zantetsu.MeshCut.Tests.ProbeBaseline
{
    /// <summary>
    /// TEMPORARY test-only copy of the probe's adopted Burst cut (zantetsuken-mesh-cut-probe c77dd96,
    /// Assets/MeshCutProbe/BurstScanEdgeHashCut.cs) with its WorkingMesh / CutOutput plumbing removed, kept only so
    /// the Phase 2.9 performance test can compare the product kernel with the probe implementation on identical
    /// inputs inside one run (DESIGN 14, Phase 2.9 performance confirmation). It is not a product path and is removed
    /// once the comparison is recorded; the probe repository stays the reference for its numbers.
    ///
    /// Scope of the copy: phases 0-3.5 with the split cap, position + game_vertex attributes (9 floats per vertex),
    /// no attribute seams (the probe's Burst port never had them), per-side fully materialized fragments.
    /// </summary>
    public sealed class BurstScanEdgeHashCut : IDisposable
    {
        public struct Counters
        {
            public int CandidateTriangles;
            public int CrossingTriangles;
            public int GeneratedVertices;
            public int OutputTriangles;
            public int OutputVertices;
            public int LoopCount;
            public int OpenContourCount;
            public int LoopVertexTotal;
            public int LoopVertexMax;
            public int CapTriangles;
            public int CapAuxiliaryVertices;
            public int CapCyclesClosedBySurface;
            public int CapCycleNodes;
            public int CapDegenerateTriangles;
            public int CapReflexVertices;
            public int CapArtifactTriangles;
            public int CapBoundaryCrossings;
            public ulong NodeKeyHash;
            public ulong NodeParamHash;
            public sbyte PlaneMissSide;
            public int AllOnPlane;
            public int Failure;
            public int FailureTriangle;
            public bool Unsupported => Failure != 0;
            public bool PlaneMiss => PlaneMissSide != 0;
        }

        private NativeArray<float> _px, _py, _pz;
        private NativeArray<int> _indices;
        private NativeArray<float> _attributes;
        private int _attributeFloats;
        private int _vertexCount, _triangleCount;

        private NativeArray<float> _dist;
        private NativeArray<sbyte> _side;
        private NativeArray<sbyte> _triClass;
        private NativeArray<byte> _loneCorner;
        private NativeArray<int> _nodeP, _nodeQ;
        private NativeArray<long> _mapKeys;
        private NativeArray<int> _mapValues;
        private NativeArray<long> _nodeKeys;
        private NativeArray<double> _nodeParam;
        private NativeArray<int> _nodeSourceVertex;
        private NativeArray<float> _nodeX, _nodeY, _nodeZ, _nodeAttr;
        private NativeHashMap<long, int> _onPlaneEdgeSides;
        private NativeList<long> _onPlaneEdgeOrder, _planeBoundaryEdges;
        private NativeArray<int> _next, _prev;
        private NativeArray<byte> _used;
        private NativeArray<int> _state;
        private NativeArray<Counters> _result;
        private Fragment _pos, _neg;

        private struct Fragment : IDisposable
        {
            public NativeArray<int> OriginalMap, NodeMap;
            public NativeList<int> SourceVertex, SourceNode, Indices, SegFrom, SegTo, ContourNodes, ContourStart;
            public NativeList<byte> ContourClosed;
            public NativeList<F3> Aux;
            public NativeList<float> Px, Py, Pz, Attr;

            public void Allocate(int vertexCount, int nodeCapacity, int triangleCount)
            {
                OriginalMap = new NativeArray<int>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NodeMap = new NativeArray<int>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                SourceVertex = new NativeList<int>(vertexCount, Allocator.Persistent);
                SourceNode = new NativeList<int>(vertexCount, Allocator.Persistent);
                Indices = new NativeList<int>(triangleCount * 3 + 3, Allocator.Persistent);
                SegFrom = new NativeList<int>(64, Allocator.Persistent);
                SegTo = new NativeList<int>(64, Allocator.Persistent);
                ContourNodes = new NativeList<int>(64, Allocator.Persistent);
                ContourStart = new NativeList<int>(8, Allocator.Persistent);
                ContourClosed = new NativeList<byte>(8, Allocator.Persistent);
                Aux = new NativeList<F3>(8, Allocator.Persistent);
                Px = new NativeList<float>(vertexCount, Allocator.Persistent);
                Py = new NativeList<float>(vertexCount, Allocator.Persistent);
                Pz = new NativeList<float>(vertexCount, Allocator.Persistent);
                Attr = new NativeList<float>(vertexCount, Allocator.Persistent);
            }

            public void Dispose()
            {
                if (OriginalMap.IsCreated) OriginalMap.Dispose();
                if (NodeMap.IsCreated) NodeMap.Dispose();
                if (SourceVertex.IsCreated) SourceVertex.Dispose();
                if (SourceNode.IsCreated) SourceNode.Dispose();
                if (Indices.IsCreated) Indices.Dispose();
                if (SegFrom.IsCreated) SegFrom.Dispose();
                if (SegTo.IsCreated) SegTo.Dispose();
                if (ContourNodes.IsCreated) ContourNodes.Dispose();
                if (ContourStart.IsCreated) ContourStart.Dispose();
                if (ContourClosed.IsCreated) ContourClosed.Dispose();
                if (Aux.IsCreated) Aux.Dispose();
                if (Px.IsCreated) Px.Dispose();
                if (Py.IsCreated) Py.Dispose();
                if (Pz.IsCreated) Pz.Dispose();
                if (Attr.IsCreated) Attr.Dispose();
            }
        }

        /// <summary>Uploads a logical mesh (SoA positions, logical indices, optional 9 floats per vertex: normal, uv, tangent). Never inside a timed region.</summary>
        public void SetMesh(float[] px, float[] py, float[] pz, int[] indices, float[] attributes9)
        {
            Dispose();
            _vertexCount = px.Length;
            _triangleCount = indices.Length / 3;
            _attributeFloats = attributes9 == null ? 0 : 9;
            _px = new NativeArray<float>(px, Allocator.Persistent);
            _py = new NativeArray<float>(py, Allocator.Persistent);
            _pz = new NativeArray<float>(pz, Allocator.Persistent);
            _indices = new NativeArray<int>(indices, Allocator.Persistent);
            _attributes = new NativeArray<float>(Math.Max(1, _vertexCount * _attributeFloats), Allocator.Persistent);
            if (attributes9 != null) NativeArray<float>.Copy(attributes9, _attributes, attributes9.Length);

            _dist = new NativeArray<float>(Math.Max(1, _vertexCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _side = new NativeArray<sbyte>(Math.Max(1, _vertexCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _triClass = new NativeArray<sbyte>(Math.Max(1, _triangleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _loneCorner = new NativeArray<byte>(Math.Max(1, _triangleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeP = new NativeArray<int>(Math.Max(1, _triangleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeQ = new NativeArray<int>(Math.Max(1, _triangleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int nodeCapacity = Math.Max(4, _triangleCount * 2 + _vertexCount);
            int mapCapacity = 16;
            while (mapCapacity < nodeCapacity * 4) mapCapacity <<= 1;
            _mapKeys = new NativeArray<long>(mapCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _mapValues = new NativeArray<int>(mapCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeKeys = new NativeArray<long>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeParam = new NativeArray<double>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeSourceVertex = new NativeArray<int>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeX = new NativeArray<float>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeY = new NativeArray<float>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeZ = new NativeArray<float>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _nodeAttr = new NativeArray<float>(Math.Max(1, nodeCapacity * _attributeFloats), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _onPlaneEdgeSides = new NativeHashMap<long, int>(Math.Max(16, _triangleCount * 3), Allocator.Persistent);
            _onPlaneEdgeOrder = new NativeList<long>(16, Allocator.Persistent);
            _planeBoundaryEdges = new NativeList<long>(16, Allocator.Persistent);
            _next = new NativeArray<int>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _prev = new NativeArray<int>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _used = new NativeArray<byte>(nodeCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _state = new NativeArray<int>(CutJob.StateSize, Allocator.Persistent);
            _result = new NativeArray<Counters>(1, Allocator.Persistent);
            _pos.Allocate(_vertexCount, nodeCapacity, _triangleCount);
            _neg.Allocate(_vertexCount, nodeCapacity, _triangleCount);
        }

        public int CapHashThreshold = BurstCapping.HashThreshold;
        public Counters LastCounters => _result[0];
        public int PositiveVertexCount => _pos.SourceVertex.Length;
        public int NegativeVertexCount => _neg.SourceVertex.Length;
        public int PositiveIndexCount => _pos.Indices.Length;
        public int NegativeIndexCount => _neg.Indices.Length;

        public void Cut(float nx, float ny, float nz, float offset, double onPlaneEpsilon, bool withCap, out Counters counters)
        {
            CutJob job = BuildJob(nx, ny, nz, offset, onPlaneEpsilon, withCap);
            job.Phase = CutJob.PhaseAll;
            job.Run();
            counters = _result[0];
        }

        public JobHandle Schedule(float nx, float ny, float nz, float offset, double onPlaneEpsilon, bool withCap)
        {
            CutJob job = BuildJob(nx, ny, nz, offset, onPlaneEpsilon, withCap);
            job.Phase = CutJob.PhaseAll;
            return IJobExtensions.Schedule(job);
        }

        private CutJob BuildJob(float nx, float ny, float nz, float offset, double onPlaneEpsilon, bool withCap)
        {
            return new CutJob
            {
                Px = _px, Py = _py, Pz = _pz, Indices = _indices,
                Attributes = _attributes, AttributeFloats = _attributeFloats,
                VertexCount = _vertexCount, TriangleCount = _triangleCount,
                Nx = nx, Ny = ny, Nz = nz, Offset = offset, OnPlaneEpsilon = onPlaneEpsilon, WithCap = withCap,
                CapHashThreshold = CapHashThreshold,
                State = _state,
                Dist = _dist, Side = _side, TriClass = _triClass, LoneCorner = _loneCorner,
                NodeP = _nodeP, NodeQ = _nodeQ,
                MapKeys = _mapKeys, MapValues = _mapValues,
                NodeKeys = _nodeKeys, NodeParam = _nodeParam, NodeSourceVertex = _nodeSourceVertex,
                NodeX = _nodeX, NodeY = _nodeY, NodeZ = _nodeZ, NodeAttr = _nodeAttr,
                OnPlaneEdgeSides = _onPlaneEdgeSides, OnPlaneEdgeOrder = _onPlaneEdgeOrder, PlaneBoundaryEdges = _planeBoundaryEdges,
                PosOriginalMap = _pos.OriginalMap, PosNodeMap = _pos.NodeMap,
                PosSourceVertex = _pos.SourceVertex, PosSourceNode = _pos.SourceNode,
                PosIndices = _pos.Indices, PosSegFrom = _pos.SegFrom, PosSegTo = _pos.SegTo,
                PosContourNodes = _pos.ContourNodes, PosContourStart = _pos.ContourStart, PosContourClosed = _pos.ContourClosed,
                PosAux = _pos.Aux,
                PosPx = _pos.Px, PosPy = _pos.Py, PosPz = _pos.Pz, PosAttr = _pos.Attr,
                NegOriginalMap = _neg.OriginalMap, NegNodeMap = _neg.NodeMap,
                NegSourceVertex = _neg.SourceVertex, NegSourceNode = _neg.SourceNode,
                NegIndices = _neg.Indices, NegSegFrom = _neg.SegFrom, NegSegTo = _neg.SegTo,
                NegContourNodes = _neg.ContourNodes, NegContourStart = _neg.ContourStart, NegContourClosed = _neg.ContourClosed,
                NegAux = _neg.Aux,
                NegPx = _neg.Px, NegPy = _neg.Py, NegPz = _neg.Pz, NegAttr = _neg.Attr,
                Next = _next, Prev = _prev, Used = _used,
                Result = _result
            };
        }

        public void Dispose()
        {
            if (_px.IsCreated) _px.Dispose();
            if (_py.IsCreated) _py.Dispose();
            if (_pz.IsCreated) _pz.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            if (_attributes.IsCreated) _attributes.Dispose();
            if (_dist.IsCreated) _dist.Dispose();
            if (_side.IsCreated) _side.Dispose();
            if (_triClass.IsCreated) _triClass.Dispose();
            if (_loneCorner.IsCreated) _loneCorner.Dispose();
            if (_nodeP.IsCreated) _nodeP.Dispose();
            if (_nodeQ.IsCreated) _nodeQ.Dispose();
            if (_mapKeys.IsCreated) _mapKeys.Dispose();
            if (_mapValues.IsCreated) _mapValues.Dispose();
            if (_nodeKeys.IsCreated) _nodeKeys.Dispose();
            if (_nodeParam.IsCreated) _nodeParam.Dispose();
            if (_nodeSourceVertex.IsCreated) _nodeSourceVertex.Dispose();
            if (_nodeX.IsCreated) _nodeX.Dispose();
            if (_nodeY.IsCreated) _nodeY.Dispose();
            if (_nodeZ.IsCreated) _nodeZ.Dispose();
            if (_nodeAttr.IsCreated) _nodeAttr.Dispose();
            if (_onPlaneEdgeSides.IsCreated) _onPlaneEdgeSides.Dispose();
            if (_onPlaneEdgeOrder.IsCreated) _onPlaneEdgeOrder.Dispose();
            if (_planeBoundaryEdges.IsCreated) _planeBoundaryEdges.Dispose();
            if (_next.IsCreated) _next.Dispose();
            if (_prev.IsCreated) _prev.Dispose();
            if (_used.IsCreated) _used.Dispose();
            if (_state.IsCreated) _state.Dispose();
            if (_result.IsCreated) _result.Dispose();
            _pos.Dispose();
            _neg.Dispose();
        }
    }
}
