using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    public enum MeshCutStatus : int
    {
        Ok = 0,
        /// <summary>A view, range or topology reference is invalid (index outside the view, vertex not in the topology map, empty geometry).</summary>
        InvalidInput = 1,
        /// <summary>The new-vertex reservation is too small; <see cref="MeshCutResult.requiredVertexCapacity"/> holds the exact need.</summary>
        CapacityVertex = 2,
        /// <summary>The new-index reservation is too small; <see cref="MeshCutResult.requiredIndexCapacity"/> holds the exact need.</summary>
        CapacityIndex = 3,
        /// <summary>The scratch block is too small; <see cref="MeshCutResult.requiredScratchBytes"/> holds the need (exact for the fixed part, doubled for the cap arena).</summary>
        CapacityScratch = 4,
    }

    /// <summary>
    /// One contiguous block of render vertices of the input geometry with the topology vertex id of each. The
    /// <c>RenderCutTopologyMap</c> of DESIGN 6.2 is the union of such blocks: existing vertices keep their global
    /// numbers across cuts, so a child geometry's map is the parent's blocks plus the block the cut appended.
    /// </summary>
    public unsafe struct RenderTopologyRange
    {
        public uint vertexBase;
        public int count;
        /// <summary>Topology vertex id per render vertex of the block (count entries).</summary>
        public int* topologyVertex;
    }

    /// <summary>
    /// Logical topology of the input geometry: which render vertices are the same topology vertex (attribute seams,
    /// hard edges) and, by exclusion, which coincident vertices are distinct topology (never welded by position).
    /// Topology vertex ids are dense in [0, topologyVertexCount); the geometry may use only a subset of them.
    /// </summary>
    public unsafe struct RenderCutTopologyMap
    {
        public RenderTopologyRange* ranges;
        public int rangeCount;
        /// <summary>Upper bound (exclusive) of the topology vertex ids in use. New ids created by a cut start here.</summary>
        public int topologyVertexCount;
    }

    /// <summary>One index range of the global index buffer; one range per submesh (draw range) of the geometry.</summary>
    public struct MeshCutIndexRange
    {
        public uint indexStart;
        public int indexCount;
    }

    /// <summary>
    /// Numerical input of one display/stencil cut (DESIGN 6.1): the global AoS vertex and index views (addressing only;
    /// the kernel touches only what the ranges reference), the input geometry's submesh ranges, its topology map and
    /// the adopted plane in the same local frame. All of it is caller-owned and immutable from scheduling to completion.
    /// </summary>
    public unsafe struct MeshCutInput
    {
        public RenderVertex* vertices;
        /// <summary>Element count of the vertex view (byte size is computed in 64 bit; the count itself fits an int).</summary>
        public int vertexViewLength;
        public uint* indices;
        public int indexViewLength;
        public MeshCutIndexRange* ranges;
        public int rangeCount;
        public RenderCutTopologyMap topology;
        /// <summary>s(x) = dot(plane.xyz, x) + plane.w; s &gt;= 0 is the positive side (DESIGN 6.4: OnPlane is owned by the positive side).</summary>
        public float4 plane;
    }

    /// <summary>
    /// Caller-reserved output of one cut: one contiguous new-vertex reservation, one contiguous new-index reservation
    /// (positive side first, then negative, DESIGN 4.5.6), the topology output for the new vertices, the per-submesh
    /// output ranges and the scratch block. The kernel writes only inside these ranges and retains nothing.
    /// </summary>
    public unsafe struct MeshCutOutput
    {
        public RenderVertex* newVertices;
        /// <summary>Global vertex number of newVertices[0]; new indices reference newVertexBase + i.</summary>
        public uint newVertexBase;
        public int newVertexCapacity;
        /// <summary>Topology vertex id per new vertex (newVertexCapacity entries): the block a child map appends.</summary>
        public int* newVertexTopology;
        public uint* newIndices;
        /// <summary>Global index-buffer position of newIndices[0].</summary>
        public uint newIndexBase;
        public int newIndexCapacity;
        /// <summary>2 * input.rangeCount entries: positive side per input range, then negative side per input range.</summary>
        public MeshCutIndexRange* outputRanges;
        /// <summary>
        /// Optional node correspondence (null when not wanted): per intersection node, the topology edge it lies on
        /// packed as (lo &lt;&lt; 32 | hi) and its parameter from the lo endpoint. Node i has topology id
        /// input.topology.topologyVertexCount + i. Written only when nodeCapacity covers the node count.
        /// </summary>
        public long* nodeEdgeKeys;
        public float* nodeParams;
        public int nodeCapacity;
        public byte* scratch;
        public int scratchBytes;
    }

    /// <summary>One side of the result. When <see cref="reusesInput"/> is set the side is the unchanged input geometry and no new index was written for it.</summary>
    public struct MeshCutSideResult
    {
        public uint indexStart;
        public int indexCount;
        public byte reusesInput;
        public float3 boundsMin, boundsMax;
        public bool IsEmpty => indexCount == 0;
    }

    public struct MeshCutResult
    {
        public MeshCutStatus status;
        public MeshCutSideResult positive, negative;
        /// <summary>New render vertices actually written from the head of the reservation.</summary>
        public int newVertexCount;
        /// <summary>New indices actually written: positive.indexCount + negative.indexCount when neither side reuses the input.</summary>
        public int newIndexCount;
        /// <summary>Topology vertex id space after the cut (input count plus the ids created for intersection nodes and cap auxiliary vertices).</summary>
        public int newTopologyVertexCount;

        public int triangleCount, crossingTriangles, nodeCount, interpolatedVertices, capRenderVertices;
        public int loopCount, openContourCount, capTriangles, capCyclesClosedBySurface, capAuxVertices;
        public int capDegenerateTriangles, capStalledEars, capCrossings, capContacts;
        /// <summary>Cycles the split decomposition could not close and that were capped by the fan instead (doubled regions).</summary>
        public int capFanFallbacks;

        /// <summary>On a capacity status: the reservation that would have sufficed (0 when not determinable).</summary>
        public int requiredVertexCapacity, requiredIndexCapacity, requiredScratchBytes;
        public int usedScratchBytes;
        /// <summary>1 when the optional node correspondence arrays were written (nodeCapacity covered nodeCount).</summary>
        public byte nodeCorrespondenceWritten;
        /// <summary>1 when the kernel body ran as managed code instead of Burst (test diagnostic; never set under Burst).</summary>
        public byte executedManaged;
    }

    /// <summary>
    /// Reservation estimate for one cut, owned by the kernel side (DESIGN 6.1). The index and vertex figures are upper
    /// bounds for everything except cap auxiliary vertices, which depend on the boundary loops' self-contacts; a run that
    /// exceeds them fails before writing outside the reservation and reports the exact need.
    /// </summary>
    public struct MeshCutCapacity
    {
        public int newVertices, newIndices, scratchBytes;
        public int triangleCount, crossingTriangles;
        /// <summary>+1 / -1 when every vertex lies on that side (no output needed, the input is reused); 0 otherwise.</summary>
        public sbyte wholeMeshSide;
        /// <summary>1 when a range or index reference is invalid; the scratch figure then covers only the classification pass so a run reports InvalidInput.</summary>
        public byte invalidInput;
    }
}
