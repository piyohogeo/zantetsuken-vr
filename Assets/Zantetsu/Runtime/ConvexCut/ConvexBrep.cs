using Unity.Mathematics;

namespace Zantetsu.ConvexCut
{
    /// <summary>
    /// Undirected edge of the polygon-CSR convex B-rep. <c>v0 &lt; v1</c>; <c>f0</c> is the face whose loop
    /// traverses v0->v1, <c>f1</c> the face traversing v1->v0. Indices are local to the owning convex.
    /// </summary>
    public struct BrepEdge
    {
        public int v0, v1, f0, f1;
    }

    /// <summary>
    /// Element ranges of one convex inside a <see cref="ConvexBrepBank"/>. Face offsets (<c>faceCount + 1</c>
    /// entries starting at <see cref="faceBase"/>) are relative to <see cref="faceIndexBase"/>; face-index
    /// values are local vertex indices (0..vertexCount-1) and face-edge values local edge indices.
    /// </summary>
    public struct ConvexBrepRange
    {
        public int vertexBase, vertexCount;
        public int faceBase, faceCount;
        public int faceIndexBase, faceIndexCount;
        public int edgeBase, edgeCount;
        /// <summary>Longest face loop of the convex (kernel scratch sizing).</summary>
        public int maxFaceLoop;
    }

    /// <summary>
    /// Flat storage for one or more convex B-reps (polygon CSR + persistent undirected edge table), addressed
    /// by <see cref="ConvexBrepRange"/>. Used both for the immutable input compound and for the caller-reserved
    /// output arena. Pointers are caller-owned; the kernels never allocate, resize or retain them.
    /// </summary>
    public unsafe struct ConvexBrepBank
    {
        public float3* vertices;
        public int* faceOffsets;
        public int* faceIndices;
        public int* faceEdges;
        public BrepEdge* edges;

        public BrepBuffer View(in ConvexBrepRange r) => new BrepBuffer
        {
            v = vertices + r.vertexBase,
            faceOff = faceOffsets + r.faceBase,
            faceIdx = faceIndices + r.faceIndexBase,
            faceEdge = faceEdges + r.faceIndexBase,
            edges = edges + r.edgeBase,
            V = r.vertexCount, F = r.faceCount, I = r.faceIndexCount, E = r.edgeCount,
            vCap = r.vertexCount, fCap = r.faceCount, iCap = r.faceIndexCount, eCap = r.edgeCount,
        };

        /// <summary>A writable view over a reserved output range (counts start at 0, capacities from the range).</summary>
        public BrepBuffer OutputView(int vertexBase, int vertexCap, int faceBase, int faceCap, int faceIndexBase, int faceIndexCap, int edgeBase, int edgeCap) => new BrepBuffer
        {
            v = vertices + vertexBase,
            faceOff = faceOffsets + faceBase,
            faceIdx = faceIndices + faceIndexBase,
            faceEdge = faceEdges + faceIndexBase,
            edges = edges + edgeBase,
            vCap = vertexCap, fCap = faceCap, iCap = faceIndexCap, eCap = edgeCap,
        };
    }

    /// <summary>
    /// Pointer view of one convex B-rep: input (counts) or output buffer (capacities + counts written by the kernels).
    /// </summary>
    public unsafe struct BrepBuffer
    {
        public float3* v;
        public int* faceOff;   // F+1 entries
        public int* faceIdx;   // I entries: local vertex index per loop slot
        public int* faceEdge;  // I entries: local edge index of the loop edge starting at this slot
        public BrepEdge* edges;
        public int vCap, fCap, iCap, eCap;
        public int V, F, I, E;
    }

    /// <summary>
    /// Worst-case output capacities of one side of one plane clip (probe CAPACITY_NOTES §1-2). Owned by the
    /// kernel side so that callers never re-derive the formulas.
    /// </summary>
    public struct ClipCapacity
    {
        public int vOut, fOut, iOut, eOut, cutK, hashCap, faceScratch;

        /// <summary>Static (asset-time) bound: every vertex may lie exactly on the plane.</summary>
        public static ClipCapacity Static(int V, int E, int F, int I, int maxFaceLoop)
        {
            var c = new ClipCapacity();
            int minEF = math.min(E, F);
            c.cutK = minEF + V;             // X <= min(E,F), Vo <= V
            c.vOut = V + minEF;             // V_side + Vo + X <= V + min(E,F)
            c.fOut = F + 1;
            c.iOut = I + F + c.cutK;
            c.eOut = c.vOut + c.fOut - 2;
            c.hashCap = math.ceilpow2(math.max(16, 2 * c.eOut));
            c.faceScratch = maxFaceLoop + 2;
            return c;
        }

        /// <summary>
        /// Snapshot bound tightened with the exact-sign vertex counts of the robust-support scan
        /// (<paramref name="vSide"/> vertices strictly on this side, <paramref name="vOn"/> with d == 0).
        /// </summary>
        public static ClipCapacity Snapshot(int V, int E, int F, int I, int maxFaceLoop, int vSide, int vOn)
        {
            var c = Static(V, E, F, I, maxFaceLoop);
            int minEF = math.min(E, F);
            c.cutK = minEF + vOn;
            c.vOut = vSide + vOn + minEF;
            c.iOut = I + F + c.cutK;
            c.eOut = c.vOut + c.fOut - 2;
            c.hashCap = math.ceilpow2(math.max(16, 2 * c.eOut));
            return c;
        }
    }
}
