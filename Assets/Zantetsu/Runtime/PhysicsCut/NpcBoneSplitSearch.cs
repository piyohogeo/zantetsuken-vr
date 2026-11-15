using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What the bone-aware decision concluded about one NPC fragment's owner (NPC-BONE-SPLIT-1).</summary>
    public enum NpcBoneSplitVerdict
    {
        /// <summary>Not decided (not an NPC's fragment, or the decision did not run).</summary>
        None = 0,

        /// <summary>Two groups, each with a convex and triangles, logically apart by the bones: the sides were written.</summary>
        Separable,

        /// <summary>Everything is connected by the bones, or fewer than two groups have both a convex and triangles.</summary>
        NotSeparable,

        /// <summary>Something's bone is not known (a convex without its bone, a vertex of no known origin, triangles with no weight, the rig or the pose).</summary>
        Insufficient,

        /// <summary>The input is over the decision's limits (triangles, the origin walk).</summary>
        OverTheLimits,
    }

    /// <summary>One bone-aware decision's record (NPC-BONE-SPLIT-1): its verdict and why, and what it counted.</summary>
    public struct NpcBoneSplitRecord
    {
        public NpcBoneSplitVerdict verdict;
        public string reason;

        /// <summary>The rig's bones; the bone clusters once the cuts' connections are taken off.</summary>
        public int bones, clusters;

        /// <summary>Mesh components (triangles joined by shared topology vertices); groups (connected by the bones) with a convex or triangles; of them, with both.</summary>
        public int meshComponents, groups, fullGroups;

        /// <summary>
        /// NPC-BONE-TOPOLOGY-1: the topology vertices the geometry's mapping addresses; and the render vertices the triangles name that
        /// share their topology vertex with one named before them (an attribute seam's other sides: joined to it, each still read by
        /// its own number for its weights and origins).
        /// </summary>
        public int topologyVertices, seamVertices;

        /// <summary>Vertices the triangles name; the lineage's vertices; steps of the walk from cut vertices to the base vertices they came from.</summary>
        public int referencedVertices, lineageVertices;
        public long originSteps;

        public int positiveConvexes, negativeConvexes, positiveTriangles, negativeTriangles;

        /// <summary>
        /// NPC-BONE-HELD-1: the rig's bones the piece holds (a convex of it in the piece, or its joint on this side of every cut); the
        /// positive weights the triangles' vertices have on known bones the piece does not hold, left out of the connections (counted per
        /// vertex and bone, the walk through cut vertices included); the groups of triangles connected to no held bone though weighted
        /// (a mesh alone).
        /// </summary>
        public int pieceBones;
        public long removedWeights;
        public int unboundGroups;

        /// <summary>
        /// The connections from a mesh component to a bone cluster it supports (a vertex of the component weighted, through its origins,
        /// to a bone of the cluster), one a component and cluster, as they end (NPC-BONE-RATIO-1, 2026-11-13): those used
        /// (<see cref="connectionsKept"/>: by the support ratio, or kept under it by the protection) and those left out
        /// (<see cref="connectionsDropped"/>: under the ratio and not protected).
        /// </summary>
        public int connectionsKept, connectionsDropped;

        /// <summary>
        /// Of <see cref="connectionsKept"/>, the connections under the support ratio that were kept because the component is the
        /// most supporting mesh of a bone of that cluster that has a convex in the piece (NPC-BONE-RATIO-1).
        /// </summary>
        public int connectionsProtected;

        /// <summary>
        /// NPC-PAIR-EVAL-1: the decision ended as not known because its input is malformed -- an array missing or short, the geometry's
        /// topology missing or without a vertex the triangles name, a convex's bone number or a weight's bone number outside the rig's
        /// range, a vertex the triangles name or an origin that is no vertex of the lineage -- and not because something is merely not
        /// known of a well-formed input (the rig or the pose not known, a convex of a bone the rig does not have, a weight on such a
        /// bone, triangles weighted to no bone, two convexes of one bone). A combination with the geometry refuses a malformed input
        /// and goes on by the geometry alone where the bones are only not known.
        /// </summary>
        public bool malformed;

        /// <summary>The decision's own time on the worker (development builds; 0 otherwise).</summary>
        public double seconds;

        /// <summary>The decision's own native scratch, in bytes (NPC-BONE-EVAL-1); 0 where it ended before taking any.</summary>
        public long nativeBytes;

        public bool Entered => verdict != NpcBoneSplitVerdict.None;
    }

    /// <summary>
    /// The bone-aware split's decision (NPC-BONE-SPLIT-1): for one NPC fragment's physics owner, which of its convexes and
    /// triangles are connected through the NPC's bones, and -- where two groups that each hold a convex and triangles are
    /// apart -- a placement of everything on two sides. The connection is logical, from the bones alone: it reads no
    /// position of a convex or a triangle, so convexes that touch or overlap do not hold two groups together, and it says
    /// nothing of a geometric separation (it is not the plane's, the bent partition's or the graph's proof).
    /// <para>
    /// **The bone clusters**, of the bones the piece holds (NPC-BONE-HELD-1, from the piece's record <see cref="NpcBonePiece"/> and
    /// its convexes): bone k is held where a convex of k is in the piece -- a logical meeting point, whether k's joint is on this side
    /// or not (one convex of a bone in a piece is expected; two are refused as not known) -- and, without a convex of k, where k's
    /// joint is kept on this side of every cut of its history (the joints at the pose the first cut was accepted at, in the base
    /// geometry's frame). A bone and its parent are connected where their segment (the parent's joint to the bone's) has a part in
    /// the piece and both are held: a connection a cut took away stays away, and a branching bone whose joint is cut away joins its
    /// branches only through its own convex, when the piece holds it. A cluster is a connected part of that.
    /// </para>
    /// <para>
    /// **The connections**, one union-find over the clusters, the convexes and the lineage's vertices: a convex joins its bone's
    /// cluster; the three corners of a triangle are joined, and so is every render vertex with the others of its **topology
    /// vertex** (NPC-BONE-TOPOLOGY-1, 2026-11-12: the current geometry's render vertex to topology vertex mapping, the one the
    /// cut kernel reads, <see cref="Input.topology"/>) -- a mesh component is triangles that share a topology vertex, so faces an
    /// attribute seam (a UV's, a normal's) gives render vertices of their own are one component with their neighbours, and one
    /// component is never divided: a closed surface goes to one side whole and each side's surface stays closed. Only the
    /// components are made by topology: indices, attributes, weights and a cut vertex's origins are read by the render vertex's
    /// own number, as before. A vertex supports the cluster of every bone it has a positive weight on -- a base vertex its model's weights, a
    /// vertex a cut made the weights of the vertices it interpolates (both where it lies strictly between them, the one where
    /// it lies on it), walked back to the base, a cap's auxiliary vertex none -- **among the bones the piece holds**: a weight on
    /// a bone not held supports nothing (no other bone is chosen for it); a held bone counts whether it is held by its convex or by
    /// its joint. **The connection rule (NPC-BONE-RATIO-1, 2026-11-13): a support ratio of 1/40, and the protection of a convex
    /// bone's main supporting mesh.** The clusters are numbered before any component joins them. For a mesh component M, s(M, c)
    /// is the number of its referenced render vertices that support cluster c (a vertex once a cluster, however many of its
    /// origins, bones of that cluster or triangles reach it) and peak(M) the largest of them. M is connected to c where
    /// <list type="bullet">
    /// <item>**the ratio:** s &gt; 0 and 40 s &gt;= peak (in long; clusters tied at the peak all stay) -- a bridge of very few
    /// vertices between a limb and a trunk no longer holds the two together; or</item>
    /// <item>**the protection:** for a bone k that has a convex in the piece, b(M, k) is the number of M's referenced render
    /// vertices that support bone k (a vertex once a bone). The component whose b(., k) is positive and the largest among all the
    /// piece's mesh components keeps its connection to k's cluster even under the ratio; components tied at the largest all keep
    /// it. So the sleeve, the mesh that mainly supports the upper arm's bone, stays with the arm's convexes although it is one
    /// component with the trunk and its vertices on the arm are few beside the trunk's -- and a hand's island alone does not
    /// take the arm's convexes away (seen on Link 2026-11-12 under the 3/10 bound of NPC-BONE-SUPPORT-1).</item>
    /// </list>
    /// Everything is counted over the piece's own input: its triangles' vertices, its convexes' bones, the bones it holds. A
    /// component and cluster are one connection, counted as they end (<see cref="NpcBoneSplitRecord.connectionsKept"/>,
    /// <see cref="NpcBoneSplitRecord.connectionsDropped"/>, <see cref="NpcBoneSplitRecord.connectionsProtected"/>). Nothing of
    /// the weights is removed. (History: 3/10 from 2026-11-10, NPC-BONE-SUPPORT-1; any positive support from 2026-11-12,
    /// NPC-BONE-ANY-SUPPORT-1, under which a waist cut's lower side kept its arms.)
    /// </para>
    /// <para>
    /// **The scratch of the support rule and of the topology**, beside the decision's own: 2 L + 2 (T + 1) + 8 N + K ints (L the
    /// lineage's vertices, T the triangles, N the rig's bones, K the geometry's topology vertices) -- each vertex's component, the
    /// referenced vertices grouped by component, the components' ends and the placing cursors, each held bone's cluster, a
    /// component's counts, stamps and touched clusters, for each topology vertex the render vertex first met of it, and (4 N,
    /// NPC-BONE-RATIO-1) a component's counts, stamps and touched bones among the bones with a convex and each such bone's
    /// largest count so far. The protection adds no walk: the bones are counted in the one walk that counts the clusters. A
    /// connection under the ratio waits for the last component (a bone's largest count is known only then) as three ints -- the
    /// component, the bone, its count -- in a list that is made only when there is such a connection and grows by doubling (at
    /// most one entry a component and bone with a convex). All taken with the rest and given back before the decision returns
    /// (counted in <see cref="NpcBoneSplitRecord.nativeBytes"/>).
    /// </para>
    /// <para>
    /// **The placement.** Every group (a connected set holding a convex or triangles) must be known: triangles weighted to no
    /// bone at all, a convex without its bone, a vertex of no known origin end the decision as insufficient -- nothing is dropped
    /// to make a split. Triangles weighted only to known bones the piece no longer holds are known: they are a group of mesh
    /// alone (<see cref="NpcBoneSplitRecord.unboundGroups"/>), placed by the rule below, never given to a convex of their choosing. Among the groups that each hold a convex and triangles (the full groups): with three or more, the one of
    /// fewest triangles goes to the positive side alone (the smallest independent group first, 2026-11-07: after a cut at the waist,
    /// one hand comes off, then the other from what remains, rather than both hands together from the lower body; of equal triangles
    /// the one of fewer convexes, of equal convexes the one the scan meets first); with two, the one of more triangles goes to the
    /// positive side, as before. Everything else goes to the negative: a group of mesh or convexes alone stays with the others, as
    /// it was, and is never the one chosen. One split at a time, two outputs as every split: what remains together on the negative
    /// side is split again by a later decision of its own fragment (a large remainder is decided again, so the whole may cost more).
    /// </para>
    /// Plain C# over pointers and arrays the main thread handed over, on a background worker: no Unity object, no allocation but
    /// its own native scratch, which it gives back before it returns.
    /// </summary>
    internal static unsafe class NpcBoneSplitSearch
    {
        /// <summary>The lineage as one decision reads it: a copy of the references taken on the main thread at the issue.</summary>
        internal struct Lineage
        {
            internal int nodeCount;
            internal int[] parent;
            internal bool[] present;
            internal ulong* baseBones;
            internal uint baseStart;
            internal int baseCount;
            internal NpcVertexChunk[] chunks;
            internal int localCount;
        }

        internal struct Input
        {
            internal uint* indices;
            internal int indexCount;
            internal int convexCount;
            internal int[] convexBones;
            internal Lineage lineage;

            /// <summary>NPC-BONE-ACTIVE-2: the piece's part of the rig -- each joint kept, each segment's part kept (<see cref="NpcBonePiece"/>).</summary>
            internal ulong[] jointKept;
            internal float[] segmentLo, segmentHi;

            /// <summary>
            /// Written when Separable (2n bytes): the side of each held bone (0..n-1) and of each used connection's segment part
            /// (n..2n-1): 1 positive, 0 negative, 2 neither (not held or not used, or in no group); null: not wanted.
            /// </summary>
            internal byte[] boneSides;

            internal string insufficient;
            internal int maxTriangles;
            internal long maxOriginSteps;

            /// <summary>
            /// NPC-BONE-TOPOLOGY-1: the current geometry's render vertex to topology vertex mapping, by its vertex blocks -- the map a
            /// cut of the same geometry reads (the storage's blocks: the base geometry's and each cut's), handed over with the indices
            /// and good for as long as they are. Required: without it the decision does not know which render vertices are one point of
            /// the surface, and ends as not known rather than divide a surface at its seams.
            /// </summary>
            internal RenderCutTopologyMap topology;

            /// <summary>
            /// NPC-PAIR-EVAL-1 (the evaluation's; null in the product): where the decision also writes what it found as attributes of the
            /// convexes and the mesh components, for a pairwise combination with the geometry.
            /// </summary>
            internal NpcBonePairAttributes pairs;
        }

        /// <summary>The walk from a cut vertex back to the base: most steps a decision may take before it ends over the limits.</summary>
        internal const long DefaultMaxOriginSteps = 1L << 26;

        internal const string ReasonRig = "the lineage's bones are not known: ";
        internal const string ReasonConvex = "a convex has no bone of the rig";
        internal const string ReasonVertex = "a vertex the triangles name is of no known origin";
        internal const string ReasonWeight = "a vertex is weighted to a bone outside the rig";
        internal const string ReasonUnweighted = "triangles connected to no bone";
        internal const string ReasonOneGroup = "everything is connected by the bones";
        internal const string ReasonFewFull = "fewer than two groups hold both a convex and triangles";
        internal const string ReasonTriangles = "more triangles than the limit";
        internal const string ReasonSteps = "the walk to the base vertices is over its limit";
        internal const string ReasonInput = "the input is malformed";
        internal const string ReasonConvexesOfOneBone = "two convexes of one bone in the piece (one is expected)";
        internal const string ReasonSeparable = "apart by the bones";
        internal const string ReasonTopology = "the geometry's topology is not known";
        internal const string ReasonTopologyVertex = "a vertex the triangles name has no topology vertex";

        /// <summary>
        /// Decides one owner: the sides of its convexes then its triangles into <paramref name="sides"/> (1 positive, 0 negative;
        /// <paramref name="sidesLength"/> at least convexes + triangles) when it is <see cref="NpcBoneSplitVerdict.Separable"/>.
        /// </summary>
        internal static void Decide(in Input input, byte* sides, int sidesLength, ref NpcBoneSplitRecord record)
        {
            record = default;
            int n = input.lineage.nodeCount;
            int c = input.convexCount;
            int t = input.indexCount / 3;
            record.bones = n;
            record.lineageVertices = input.lineage.localCount;
            if (input.insufficient != null)
            {
                End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonRig + input.insufficient);
                return;
            }

            if (input.indices == null || input.indexCount % 3 != 0 || c < 1 || t < 1 || input.convexBones == null || input.convexBones.Length < c
                || input.lineage.parent == null || input.lineage.parent.Length < n || input.lineage.present == null || input.lineage.present.Length < n
                || input.jointKept == null || input.jointKept.Length < ((n + 63) >> 6) || input.segmentLo == null || input.segmentLo.Length < n
                || input.segmentHi == null || input.segmentHi.Length < n || (input.boneSides != null && input.boneSides.Length < 2 * n) || sides == null || sidesLength < c + t
                || input.lineage.baseBones == null)
            {
                record.malformed = true; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonInput);
                return;
            }

            if (t > input.maxTriangles)
            {
                End(ref record, NpcBoneSplitVerdict.OverTheLimits, ReasonTriangles);
                return;
            }

            // The geometry's topology (NPC-BONE-TOPOLOGY-1): required, never guessed from positions or from the render vertices' numbers.
            int topologyCount = input.topology.topologyVertexCount;
            if (input.topology.ranges == null || input.topology.rangeCount < 1 || topologyCount < 1)
            {
                record.malformed = true; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonTopology);
                return;
            }

            record.topologyVertices = topologyCount;
            int l = input.lineage.localCount;
            // The elements: a node for each bone, the convexes, the lineage's vertices.
            long elements = (long)n + c + l;
            if (elements > int.MaxValue / 2)
            {
                End(ref record, NpcBoneSplitVerdict.OverTheLimits, ReasonTriangles);
                return;
            }

            int e = (int)elements;
            int convexBase = n;
            int vertexBase = n + c;
            NativeArray<int> uf = default, tally = default, triangleFirst = default, stack = default, parts = default, support = default, pending = default;
            NativeArray<byte> marks = default, piece = default;
            try
            {
                uf = new NativeArray<int>(e, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                tally = new NativeArray<int>(2 * e, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                triangleFirst = new NativeArray<int>(t, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                marks = new NativeArray<byte>(Math.Max(1, Math.Max(l, e)), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                int stackRoom = input.lineage.chunks.Length + 4;
                stack = new NativeArray<int>(stackRoom, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                // Each lineage vertex weighted to a known bone (l), each group so weighted (e).
                piece = new NativeArray<byte>(Math.Max(1, l + e), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                byte* weighted = (byte*)piece.GetUnsafePtr();
                byte* groupWeighted = weighted + l;
                // Each bone: how many of the piece's convexes are of it, and whether the piece holds it.
                parts = new NativeArray<int>(Math.Max(1, 2 * n), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                int* convexesOfBone = (int*)parts.GetUnsafePtr();
                int* held = convexesOfBone + n;
                // The support rule's scratch (NPC-BONE-SUPPORT-1): each lineage vertex's component (L), the referenced vertices grouped by
                // component (L), the components' ends and the placing cursors (T + 1 each), each held bone's cluster (N), and a component's
                // counts, stamps and touched clusters (3 N); and, for each topology vertex, the render vertex first met of it, plus one (K;
                // NPC-BONE-TOPOLOGY-1).
                // And (NPC-BONE-RATIO-1), among the bones with a convex in the piece: a component's counts, stamps and touched bones, and
                // each bone's largest count over the components (4 N).
                support = new NativeArray<int>(checked((2 * l) + (2 * (t + 1)) + (8 * n) + topologyCount), Allocator.Persistent, NativeArrayOptions.ClearMemory);
                int* componentOf = (int*)support.GetUnsafePtr();
                int* order = componentOf + l;
                int* ends = order + l;
                int* cursor = ends + t + 1;
                int* clusterOf = cursor + t + 1;
                int* supportCount = clusterOf + n;
                int* stamp = supportCount + n;
                int* touched = stamp + n;
                int* firstOfTopology = touched + n;
                int* boneCount = firstOfTopology + topologyCount;
                int* boneStamp = boneCount + n;
                int* touchedBones = boneStamp + n;
                int* bestOfBone = touchedBones + n;
                record.nativeBytes = ((long)uf.Length * sizeof(int)) + ((long)tally.Length * sizeof(int)) + ((long)triangleFirst.Length * sizeof(int)) + marks.Length + ((long)stack.Length * sizeof(int))
                    + piece.Length + ((long)parts.Length * sizeof(int)) + ((long)support.Length * sizeof(int));
                int* parent = (int*)uf.GetUnsafePtr();
                for (int i = 0; i < e; i++) parent[i] = i;

                // 1. The bones the piece holds (NPC-BONE-HELD-1): bone k where a convex of k is in the piece -- a logical meeting point,
                // whether k's joint is on this side or not (the joint's flag is not written) -- and, without a convex of k, where k's
                // joint is kept. One convex of a bone in a piece is expected; two are not known, and refused.
                for (int i = 0; i < c; i++)
                {
                    int bone = input.convexBones[i];
                    if ((uint)bone >= (uint)n || !input.lineage.present[bone])
                    {
                        record.malformed = (uint)bone >= (uint)n; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonConvex);
                        return;
                    }

                    if (++convexesOfBone[bone] > 1)
                    {
                        End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonConvexesOfOneBone);
                        return;
                    }
                }

                for (int k = 0; k < n; k++)
                {
                    held[k] = input.lineage.present[k] && (convexesOfBone[k] > 0 || (input.jointKept[k >> 6] & (1UL << (k & 63))) != 0) ? 1 : 0;
                    if (held[k] != 0) record.pieceBones++;
                }

                // 2. The bone clusters: a parent-child connection is used where its segment has a part in the piece and both its bones are
                // held (a connection a cut took away -- its segment's part gone -- stays away, whatever holds its bones).
                for (int s = 0; s < n; s++)
                {
                    if (Connected(in input, held, s)) Union(parent, s, input.lineage.parent[s]);
                }

                for (int k = 0; k < n; k++)
                {
                    if (held[k] != 0 && Find(parent, k) == k) record.clusters++;
                }

                // Every convex to its bone (held by it).
                for (int i = 0; i < c; i++) Union(parent, convexBase + i, input.convexBones[i]);

                // Each held bone's cluster (its root, a bone: the lowest index stays), numbered here, before any mesh component joins clusters.
                for (int k = 0; k < n; k++) clusterOf[k] = held[k] != 0 ? Find(parent, k) : -1;

                // 3. The mesh components: the corners of each triangle joined, every vertex in the lineage's own numbering -- and every
                // render vertex joined to the first one met of its topology vertex (NPC-BONE-TOPOLOGY-1), so that the faces an attribute
                // seam gives vertices of their own are one component with the faces they are sewn to. The render vertices keep their own
                // numbers for everything else (the weights, the origins, the support's count).
                byte* referenced = (byte*)marks.GetUnsafePtr();
                int lastRange = -1;
                for (int k = 0; k < t; k++)
                {
                    int a = Local(in input.lineage, input.indices[3 * k]);
                    int b = Local(in input.lineage, input.indices[(3 * k) + 1]);
                    int d = Local(in input.lineage, input.indices[(3 * k) + 2]);
                    if (a < 0 || b < 0 || d < 0)
                    {
                        record.malformed = true; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonVertex);
                        return;
                    }

                    Union(parent, vertexBase + a, vertexBase + b);
                    Union(parent, vertexBase + a, vertexBase + d);
                    triangleFirst[k] = vertexBase + a;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int x = corner == 0 ? a : corner == 1 ? b : d;
                        int topologyVertex = RenderCutTopologyMap.TopologyOf(in input.topology, input.indices[(3 * k) + corner], ref lastRange);
                        if ((uint)topologyVertex >= (uint)topologyCount)
                        {
                            record.malformed = true; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonTopologyVertex);
                            return;
                        }

                        int first = firstOfTopology[topologyVertex] - 1;
                        if (first < 0)
                        {
                            firstOfTopology[topologyVertex] = x + 1;
                        }
                        else if (first != x)
                        {
                            if (referenced[x] == 0) record.seamVertices++;
                            Union(parent, vertexBase + x, vertexBase + first);
                        }

                        referenced[x] = 1;
                    }
                }

                int m = 0;   // the mesh components
                {
                    // The components numbered (a mark a root, cleared after), each referenced vertex's component, and the referenced vertices
                    // grouped by component (a counting sort into order; ends[k] is where component k ends, component 0 starts at 0).
                    int* seen = (int*)tally.GetUnsafePtr();
                    for (int v = 0; v < l; v++)
                    {
                        if (referenced[v] == 0) { componentOf[v] = -1; continue; }
                        int r = Find(parent, vertexBase + v);
                        if (seen[r] == 0) seen[r] = ++m;
                        componentOf[v] = seen[r] - 1;
                        ends[componentOf[v]]++;
                    }

                    UnsafeUtility.MemClear(seen, (long)e * sizeof(int));
                    record.meshComponents = m;
                    for (int k = 1; k < m; k++) ends[k] += ends[k - 1];   // each component's end in order
                    for (int k = 0; k < m; k++) cursor[k] = k == 0 ? 0 : ends[k - 1];   // and where its vertices are placed from
                    for (int v = 0; v < l; v++)
                    {
                        if (componentOf[v] >= 0) order[cursor[componentOf[v]]++] = v;
                    }
                }

                NpcBonePairAttributes pairs = input.pairs;
                if (pairs != null)
                {
                    // NPC-PAIR-EVAL-1: the rows begun -- the clusters numbered, each convex's cluster, each triangle's component (numbered in
                    // the order of the first triangle, as the graph engine numbers its mesh nodes).
                    pairs.Begin(n, c, t, m);
                    for (int k = 0; k < n; k++)
                    {
                        if (held[k] != 0 && clusterOf[k] == k) pairs.ClusterId[k] = pairs.clusters++;
                    }

                    pairs.Shape();
                    for (int i = 0; i < c; i++)
                    {
                        pairs.ConvexOfBone[input.convexBones[i]] = i;
                        pairs.SetCluster(i, pairs.ClusterId[clusterOf[input.convexBones[i]]]);
                        pairs.convexCluster[i] = pairs.ClusterId[clusterOf[input.convexBones[i]]];
                    }

                    int pairNext = 0;
                    for (int k = 0; k < t; k++)
                    {
                        int component = componentOf[triangleFirst[k] - vertexBase];
                        if (pairs.Remap[component] < 0) pairs.Remap[component] = pairNext++;
                        pairs.TriComponent[k] = pairs.Remap[component];
                    }

                    for (int k = 0; k < n; k++) pairs.clusterOfBone[k] = clusterOf[k] < 0 ? -1 : pairs.ClusterId[clusterOf[k]];
                }

                // 4. Every vertex the triangles name, walked back to the base, supports the clusters of the held bones it is weighted to (once
                //    a cluster a vertex) and, of those bones, the ones with a convex in the piece (once a bone a vertex); then, component by
                //    component, its connections by the support ratio, and after the last component those the protection keeps (NPC-BONE-RATIO-1).
                int* walk = (int*)stack.GetUnsafePtr();
                long steps = 0;
                int pendingCount = 0, pendingRoom = 0, underTheRatio = 0;
                for (int component = 0; component < m; component++)
                {
                    int start = component == 0 ? 0 : ends[component - 1], end = ends[component];
                    int touchedCount = 0, touchedBoneCount = 0;
                    for (int at = start; at < end; at++)
                    {
                        int v = order[at];
                        record.referencedVertices++;
                        int top = 0;
                        walk[top++] = v;
                        while (top > 0)
                        {
                            int x = walk[--top];
                            if (++steps > input.maxOriginSteps)
                            {
                                End(ref record, NpcBoneSplitVerdict.OverTheLimits, ReasonSteps);
                                return;
                            }

                            if (x < input.lineage.baseCount)
                            {
                                ulong entry = input.lineage.baseBones[x];
                                for (int q = 0; q < 4; q++)
                                {
                                    int bone = (int)((entry >> (16 * q)) & 0xFFFF);
                                    if (bone == NpcVertexBones.None) continue;
                                    if (bone >= n || !input.lineage.present[bone])
                                    {
                                        record.malformed = bone >= n; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonWeight);
                                        return;
                                    }

                                    // A known bone: the vertex is weighted. It supports only a bone the piece holds (NPC-BONE-HELD-1); a weight on a
                                    // bone not held is left out of the connections (the model's weights are not changed).
                                    weighted[v] = 1;
                                    if (held[bone] == 0)
                                    {
                                        record.removedWeights++;
                                        continue;
                                    }

                                    // One support a vertex and a cluster, whatever the origins, bones or triangles that reach it.
                                    int cluster = clusterOf[bone];
                                    if (stamp[cluster] != v + 1)
                                    {
                                        stamp[cluster] = v + 1;
                                        if (supportCount[cluster]++ == 0) touched[touchedCount++] = cluster;
                                    }

                                    // And one support a vertex and a bone, for a bone that has a convex in the piece (the protection's count).
                                    if (convexesOfBone[bone] > 0 && boneStamp[bone] != v + 1)
                                    {
                                        boneStamp[bone] = v + 1;
                                        if (boneCount[bone]++ == 0) touchedBones[touchedBoneCount++] = bone;
                                    }
                                }

                                continue;
                            }

                            if (!TryOrigin(in input.lineage, x, out uint2 origin))
                            {
                                record.malformed = true; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonVertex);
                                return;
                            }

                            if (origin.x == MeshCutOutput.NoVertexOrigin)
                            {
                                continue;   // a cap's auxiliary vertex: weighted to nothing of its own
                            }

                            int first = Local(in input.lineage, origin.x);
                            int second = origin.y == origin.x ? first : Local(in input.lineage, origin.y);
                            if (first < 0 || second < 0)
                            {
                                record.malformed = true; End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonVertex);
                                return;
                            }

                            if (top + 2 > stackRoom)
                            {
                                End(ref record, NpcBoneSplitVerdict.OverTheLimits, ReasonSteps);
                                return;
                            }

                            walk[top++] = first;
                            if (second != first) walk[top++] = second;
                        }
                    }

                    // The connections of this component by the ratio: the clusters it supports with at least 1/40 of its most supported
                    // cluster's vertices (40 s >= peak, in long; ties at the peak all stay). One under the ratio waits: the protection keeps
                    // it where this component is the most supporting mesh of a bone of that cluster that has a convex, and a bone's largest
                    // count is known only after the last component. A count already under the bone's largest so far cannot be it.
                    int peak = 0;
                    for (int i = 0; i < touchedCount; i++) peak = Math.Max(peak, supportCount[touched[i]]);
                    for (int i = 0; i < touchedBoneCount; i++)
                    {
                        int bone = touchedBones[i];
                        if (boneCount[bone] > bestOfBone[bone]) bestOfBone[bone] = boneCount[bone];
                        if (pairs != null) pairs.AddSupport(pairs.Remap[component], bone, boneCount[bone]);
                    }

                    int anchor = vertexBase + order[start];
                    for (int i = 0; i < touchedCount; i++)
                    {
                        int cluster = touched[i];
                        if (40L * supportCount[cluster] >= peak)
                        {
                            Union(parent, anchor, cluster);
                            record.connectionsKept++;
                            if (pairs != null) pairs.SetCluster(c + pairs.Remap[component], pairs.ClusterId[cluster]);
                        }
                        else
                        {
                            underTheRatio++;
                            for (int j = 0; j < touchedBoneCount; j++)
                            {
                                int bone = touchedBones[j];
                                if (clusterOf[bone] != cluster || boneCount[bone] < bestOfBone[bone]) continue;
                                if (pendingCount == pendingRoom)
                                {
                                    int room = pendingRoom == 0 ? 16 : checked(pendingRoom * 2);
                                    var grown = new NativeArray<int>(checked(3 * room), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                                    if (pendingCount > 0) UnsafeUtility.MemCpy(grown.GetUnsafePtr(), pending.GetUnsafePtr(), (long)pendingCount * 3 * sizeof(int));
                                    if (pending.IsCreated) pending.Dispose();
                                    pending = grown;
                                    pendingRoom = room;
                                }

                                int* entry = (int*)pending.GetUnsafePtr() + (3 * pendingCount++);
                                entry[0] = anchor;
                                entry[1] = bone;
                                entry[2] = boneCount[bone];
                            }
                        }

                        supportCount[cluster] = 0;
                    }

                    for (int i = 0; i < touchedBoneCount; i++) boneCount[touchedBones[i]] = 0;
                }

                // The protection: a connection under the ratio is kept where the component's count for a bone with a convex is that bone's
                // largest over all the piece's components (positive: it was counted; ties all stay). The entries of one component and
                // cluster follow one another: one connection, counted once.
                if (pendingCount > 0)
                {
                    int* entries = (int*)pending.GetUnsafePtr();
                    int lastAnchor = -1, lastCluster = -1;
                    for (int i = 0; i < pendingCount; i++)
                    {
                        int anchor = entries[3 * i], bone = entries[(3 * i) + 1];
                        if (entries[(3 * i) + 2] != bestOfBone[bone]) continue;
                        int cluster = clusterOf[bone];
                        if (anchor == lastAnchor && cluster == lastCluster) continue;
                        Union(parent, anchor, cluster);
                        record.connectionsProtected++;
                        if (pairs != null) pairs.SetCluster(c + pairs.Remap[componentOf[anchor - vertexBase]], pairs.ClusterId[cluster]);
                        lastAnchor = anchor;
                        lastCluster = cluster;
                    }

                    record.nativeBytes += (long)pending.Length * sizeof(int);
                }

                record.connectionsKept += record.connectionsProtected;
                record.connectionsDropped = underTheRatio - record.connectionsProtected;
                if (pairs != null) pairs.End(c, bestOfBone);
                record.originSteps = steps;

                // 5. The groups: what each root holds.
                int* convexesOf = (int*)tally.GetUnsafePtr();
                int* trianglesOf = convexesOf + e;
                byte* hasBone = (byte*)marks.GetUnsafePtr();
                UnsafeUtility.MemClear(hasBone, marks.Length);
                for (int k = 0; k < n; k++)
                {
                    if (held[k] != 0) hasBone[Find(parent, k)] = 1;
                }

                for (int i = 0; i < c; i++) convexesOf[Find(parent, convexBase + i)]++;
                for (int v = 0; v < l; v++)
                {
                    if (weighted[v] != 0) groupWeighted[Find(parent, vertexBase + v)] = 1;
                }
                for (int k = 0; k < t; k++)
                {
                    int r = Find(parent, triangleFirst[k]);
                    triangleFirst[k] = r;   // from here, the triangle's group
                    trianglesOf[r]++;
                }

                // Of the full groups (a convex and triangles), the one of most triangles and the one of fewest (of equal triangles the
                // one of fewer convexes; of equal counts the one met first: a strict comparison keeps it), both kept in the one scan.
                int largest = -1, smallest = -1;
                for (int r = 0; r < e; r++)
                {
                    if (convexesOf[r] == 0 && trianglesOf[r] == 0) continue;
                    if (trianglesOf[r] > 0 && hasBone[r] == 0)
                    {
                        if (groupWeighted[r] == 0)
                        {
                            // Weighted to no bone at all: not known (as before).
                            End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonUnweighted);
                            return;
                        }

                        // Weighted, but only to bones the piece no longer holds: a group of mesh alone (NPC-BONE-ACTIVE-2).
                        record.unboundGroups++;
                    }

                    record.groups++;
                    if (convexesOf[r] > 0 && trianglesOf[r] > 0)
                    {
                        record.fullGroups++;
                        if (largest < 0 || trianglesOf[r] > trianglesOf[largest]) largest = r;
                        if (smallest < 0 || trianglesOf[r] < trianglesOf[smallest] || (trianglesOf[r] == trianglesOf[smallest] && convexesOf[r] < convexesOf[smallest])) smallest = r;
                    }
                }

                if (record.groups < 2)
                {
                    End(ref record, NpcBoneSplitVerdict.NotSeparable, ReasonOneGroup);
                    return;
                }

                if (record.fullGroups < 2)
                {
                    End(ref record, NpcBoneSplitVerdict.NotSeparable, ReasonFewFull);
                    return;
                }

                // 6. The placement: with three full groups or more, the one of fewest triangles positive (the smallest independent group
                // first, 2026-11-07); with two, the one of most triangles, as before. Everything else negative.
                int positive = record.fullGroups >= 3 ? smallest : largest;
                for (int i = 0; i < c; i++)
                {
                    bool up = Find(parent, convexBase + i) == positive;
                    sides[i] = up ? (byte)1 : (byte)0;
                    if (up) record.positiveConvexes++; else record.negativeConvexes++;
                }

                for (int k = 0; k < t; k++)
                {
                    bool up = triangleFirst[k] == positive;
                    sides[c + k] = up ? (byte)1 : (byte)0;
                    if (up) record.positiveTriangles++; else record.negativeTriangles++;
                }

                // 7. Each held bone's side and each used connection's side, for the split's records (a split keeps on each side its bones'
                // joints and its connections' segment parts): the positive group's positive, the other groups' negative, the rest neither.
                if (input.boneSides != null)
                {
                    for (int k = 0; k < n; k++)
                    {
                        input.boneSides[k] = held[k] != 0 ? SideOf(Find(parent, k), positive, convexesOf, trianglesOf) : (byte)2;
                        input.boneSides[n + k] = Connected(in input, held, k) ? SideOf(Find(parent, k), positive, convexesOf, trianglesOf) : (byte)2;
                    }
                }

                End(ref record, NpcBoneSplitVerdict.Separable, ReasonSeparable);
            }
            finally
            {
                if (uf.IsCreated) uf.Dispose();
                if (tally.IsCreated) tally.Dispose();
                if (triangleFirst.IsCreated) triangleFirst.Dispose();
                if (stack.IsCreated) stack.Dispose();
                if (marks.IsCreated) marks.Dispose();
                if (piece.IsCreated) piece.Dispose();
                if (parts.IsCreated) parts.Dispose();
                if (support.IsCreated) support.Dispose();
                if (pending.IsCreated) pending.Dispose();
            }
        }

        /// <summary>The connection of bone s to its parent: its segment (the parent's joint to s's) has a part in the piece, and both bones are held.</summary>
        private static bool Connected(in Input input, int* held, int s)
        {
            int up = input.lineage.parent[s];
            return up >= 0 && up < input.lineage.nodeCount && held[s] != 0 && held[up] != 0 && input.segmentLo[s] <= input.segmentHi[s];
        }

        private static byte SideOf(int root, int positive, int* convexesOf, int* trianglesOf) =>
            root == positive ? (byte)1 : convexesOf[root] > 0 || trianglesOf[root] > 0 ? (byte)0 : (byte)2;

        private static void End(ref NpcBoneSplitRecord record, NpcBoneSplitVerdict verdict, string reason)
        {
            record.verdict = verdict;
            record.reason = reason;
        }

        /// <summary>A global vertex number in the lineage's own numbering: the base geometry's first, then each cut's new vertices; -1 for none.</summary>
        internal static int Local(in Lineage lineage, uint vertex)
        {
            uint fromBase = vertex - lineage.baseStart;
            if (fromBase < (uint)lineage.baseCount) return (int)fromBase;
            NpcVertexChunk[] chunks = lineage.chunks;
            for (int i = 0; i < chunks.Length; i++)
            {
                uint at = vertex - chunks[i].start;
                if (at < (uint)chunks[i].count) return chunks[i].localBase + (int)at;
            }

            return -1;
        }

        /// <summary>Where the lineage's vertex <paramref name="local"/> (a cut's new vertex) came from.</summary>
        internal static bool TryOrigin(in Lineage lineage, int local, out uint2 origin)
        {
            NpcVertexChunk[] chunks = lineage.chunks;
            for (int i = 0; i < chunks.Length; i++)
            {
                int at = local - chunks[i].localBase;
                if (at >= 0 && at < chunks[i].count)
                {
                    origin = chunks[i].origins[at];
                    return true;
                }
            }

            origin = default;
            return false;
        }

        private static int Find(int* parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];   // halving
                x = parent[x];
            }

            return x;
        }

        private static void Union(int* parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;   // the lower root stays: deterministic
        }
    }

    /// <summary>
    /// NPC-PAIR-EVAL-1 (the evaluation's): what one bone-aware decision found, as attributes of the nodes a pairwise combination with the
    /// geometry reads -- one row a node, the convexes first (in their order), then the mesh components (numbered in the order of their
    /// first triangle, <see cref="TriComponent"/>). A row's first <see cref="clusterWords"/> words: the bone clusters the node is
    /// connected to (a convex: its bone's cluster; a mesh component: the clusters the connection rule adopted for it -- by the support
    /// ratio, or under it by the protection). Then, for a mesh component, a bit a convex: the component is the most supporting mesh
    /// (the largest count, ties all) of that convex's bone -- the pairs the protection names, whether the ratio kept them or not.
    /// Two nodes are connected by the bones where their rows share a cluster: two convexes of one cluster, a convex and a mesh whose
    /// adopted clusters hold the convex's, two meshes with an adopted cluster in common. <see cref="filled"/>: the decision reached the
    /// end of its connections (else nothing here is to be read). Native areas of its own; the caller gives them back (Dispose).
    /// </summary>
    internal sealed unsafe class NpcBonePairAttributes : IDisposable
    {
        internal bool filled;
        internal int bones, convexes, triangles, components, clusters, clusterWords, rowWords, protectedPairs;

        /// <summary>
        /// NPC-PAIR-EVAL-3: a row's words after the clusters are two sets of a bit a convex (<see cref="convexWords"/> words each).
        /// The first, the protected convexes (the component is the most supporting mesh of that convex's bone -- seen from the bone;
        /// with <see cref="ApplyOwnMain"/> also the second set's). The second, the component's own main convexes: of the bones that
        /// have a convex in the piece and that the component supports by at least one vertex, the one it supports by the most
        /// vertices, ties all -- seen from the component (<see cref="ownMainPairs"/> of them in all). The counts are the same as
        /// the first set's: a referenced render vertex once a bone, a cut's vertex by its origins.
        /// </summary>
        internal int convexWords, ownMainPairs;
        /// <summary>Each convex's cluster in the rows' numbering.</summary>
        internal int[] convexCluster;
        /// <summary>Each bone's cluster in the rows' numbering; -1 a bone the piece does not hold.</summary>
        internal int[] clusterOfBone;
        private NativeArray<ulong> _rows;
        private NativeArray<int> _triComponent, _scratch, _supports;
        private int _supportCount;

        internal ulong* Rows => (ulong*)_rows.GetUnsafePtr();
        internal int* TriComponent => (int*)_triComponent.GetUnsafePtr();
        internal int* Remap => (int*)_scratch.GetUnsafePtr();
        internal int* ClusterId => Remap + components;
        internal int* ConvexOfBone => ClusterId + bones;
        internal int Nodes => convexes + components;
        internal long NativeBytes => ((long)(_rows.IsCreated ? _rows.Length : 0) * 8) + ((long)((_triComponent.IsCreated ? _triComponent.Length : 0) + (_scratch.IsCreated ? _scratch.Length : 0) + (_supports.IsCreated ? _supports.Length : 0)) * 4);

        internal void Begin(int boneCount, int convexCount, int triangleCount, int componentCount)
        {
            bones = boneCount; convexes = convexCount; triangles = triangleCount; components = componentCount;
            clusterOfBone = new int[boneCount];
            convexCluster = new int[convexCount];
            _triComponent = new NativeArray<int>(Math.Max(1, triangleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _scratch = new NativeArray<int>(Math.Max(1, componentCount + (2 * boneCount)), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int* at = Remap;
            for (int i = 0; i < componentCount + (2 * boneCount); i++) at[i] = -1;
        }

        internal void Shape()
        {
            clusterWords = Math.Max(1, (clusters + 63) >> 6);
            convexWords = Math.Max(1, (convexes + 63) >> 6);
            rowWords = clusterWords + (2 * convexWords);
            _rows = new NativeArray<ulong>(checked(Math.Max(1, Nodes) * rowWords), Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        internal void SetCluster(int node, int cluster) => Rows[((long)node * rowWords) + (cluster >> 6)] |= 1UL << (cluster & 63);

        internal bool HasCluster(int node, int cluster) => (Rows[((long)node * rowWords) + (cluster >> 6)] & (1UL << (cluster & 63))) != 0;

        internal bool IsProtected(int component, int convex) => (Rows[((long)(convexes + component) * rowWords) + clusterWords + (convex >> 6)] & (1UL << (convex & 63))) != 0;

        internal bool IsOwnMain(int component, int convex) => (Rows[((long)(convexes + component) * rowWords) + clusterWords + convexWords + (convex >> 6)] & (1UL << (convex & 63))) != 0;

        /// <summary>
        /// A (NPC-PAIR-EVAL-3): each component's own main convexes are protected too -- added to the protected set, and nothing else.
        /// The component's clusters are left as the connection rule made them (review 1, 2026-11-13: adding the convex's cluster
        /// would connect the component, by the bones, to every other convex and mesh of that cluster -- more than the one pair); a
        /// protected pair is kept by the engine as a pair, whether the rows share a cluster or not. <paramref name="added"/>: the
        /// pairs not protected before; <paramref name="underRatio"/>: of the component's own main pairs, those whose convex's
        /// cluster is not among the component's clusters (the support ratio had left it out). Once, before the rows are read.
        /// </summary>
        internal void ApplyOwnMain(out int added, out int underRatio)
        {
            added = 0;
            underRatio = 0;
            for (int component = 0; component < components; component++)
            {
                ulong* row = Rows + ((long)(convexes + component) * rowWords);
                for (int convex = 0; convex < convexes; convex++)
                {
                    ulong bit = 1UL << (convex & 63);
                    if ((row[clusterWords + convexWords + (convex >> 6)] & bit) == 0) continue;
                    if ((row[clusterWords + (convex >> 6)] & bit) == 0) { row[clusterWords + (convex >> 6)] |= bit; added++; }
                    int cluster = convexCluster[convex];
                    if ((row[cluster >> 6] & (1UL << (cluster & 63))) == 0) underRatio++;   // counted; the cluster is NOT added
                }
            }
        }

        /// <summary>A component's count of the vertices that support a bone with a convex (one entry a component and bone).</summary>
        internal void AddSupport(int component, int bone, int count)
        {
            if (3 * (_supportCount + 1) > (_supports.IsCreated ? _supports.Length : 0))
            {
                var grown = new NativeArray<int>(Math.Max(48, 6 * _supportCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                if (_supportCount > 0) UnsafeUtility.MemCpy(grown.GetUnsafePtr(), _supports.GetUnsafePtr(), (long)_supportCount * 3 * sizeof(int));
                if (_supports.IsCreated) _supports.Dispose();
                _supports = grown;
            }

            _supports[3 * _supportCount] = component;
            _supports[(3 * _supportCount) + 1] = bone;
            _supports[(3 * _supportCount) + 2] = count;
            _supportCount++;
        }

        /// <summary>The protected pairs, now that every bone's largest count is known: the component (or components, tied) with that count, and the bone's convex.</summary>
        internal void End(int convexCount, int* bestOfBone)
        {
            for (int i = 0; i < _supportCount; i++)
            {
                int component = _supports[3 * i], bone = _supports[(3 * i) + 1], count = _supports[(3 * i) + 2];
                int convex = ConvexOfBone[bone];
                if (count <= 0 || count != bestOfBone[bone] || convex < 0) continue;
                Rows[((long)(convexCount + component) * rowWords) + clusterWords + (convex >> 6)] |= 1UL << (convex & 63);
                protectedPairs++;
            }

            // Each component's own main bones: its largest count among the bones with a convex (the entries are one a component and
            // bone it supports; Remap, done with, holds the largest count by component), ties all.
            int* best = Remap;
            for (int k = 0; k < components; k++) best[k] = 0;
            for (int i = 0; i < _supportCount; i++)
            {
                int component = _supports[3 * i], count = _supports[(3 * i) + 2];
                if (ConvexOfBone[_supports[(3 * i) + 1]] >= 0 && count > best[component]) best[component] = count;
            }

            for (int i = 0; i < _supportCount; i++)
            {
                int component = _supports[3 * i], bone = _supports[(3 * i) + 1], count = _supports[(3 * i) + 2];
                int convex = ConvexOfBone[bone];
                if (count <= 0 || count != best[component] || convex < 0) continue;
                Rows[((long)(convexCount + component) * rowWords) + clusterWords + convexWords + (convex >> 6)] |= 1UL << (convex & 63);
                ownMainPairs++;
            }

            filled = true;
        }

        public void Dispose()
        {
            filled = false;
            if (_rows.IsCreated) _rows.Dispose();
            if (_triComponent.IsCreated) _triComponent.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_supports.IsCreated) _supports.Dispose();
        }
    }

    /// <summary>
    /// NPC-BONE-TOPOLOGY-1: a topology map for one bone-aware decision (<see cref="NpcBoneSplitSearch.Input.topology"/>), with the ranges it
    /// is made of; given back by <see cref="Dispose"/> once the decision is done with it.
    /// <para>
    /// <see cref="TryOfGeometry"/> is the product's: the geometry's render vertex to topology vertex mapping as a cut of the geometry takes
    /// it (<see cref="VpStorageCutInput"/>) -- one range a vertex block of the geometry (the base's and each cut's), over the storage's own
    /// mapping entries, which are never moved or rewritten once committed. To be read while the geometry's index range is leased (its
    /// metadata is readable then). <see cref="OfLocal"/> is for a caller that holds the mapping as one id a vertex of the lineage's own
    /// numbering (a test's fixture, a saved input worked out again); with no ids it makes every render vertex a topology vertex of its
    /// own -- a surface with no attribute seam, and what the decision did before it read the topology.
    /// </para>
    /// </summary>
    internal sealed unsafe class NpcBoneTopology : IDisposable
    {
        private NativeArray<RenderTopologyRange> _ranges;
        private NativeArray<int> _ids;

        internal RenderCutTopologyMap Map { get; private set; }

        internal static bool TryOfGeometry(VpCpuGeometryStorage storage, VpStoredGeometry geometry, out NpcBoneTopology topology, out string why)
        {
            topology = null;
            why = null;
            if (storage == null || !storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out int topologyVertexCount)
                || blocks.Length == 0 || topologyVertexCount <= 0)
            {
                why = "the geometry gives no topology mapping";
                return false;
            }

            NativeArray<int>.ReadOnly topologyOfVertex = storage.TopologyOfVertex;
            for (int b = 0; b < blocks.Length; b++)
            {
                VpGeometryVertexBlock block = blocks[b];
                if (block.vertexStart < 0 || block.vertexCount < 0 || (long)block.vertexStart + block.vertexCount > topologyOfVertex.Length)
                {
                    why = "a vertex block of the geometry is outside the storage's committed topology";
                    return false;
                }
            }

            int* topologyBase = (int*)topologyOfVertex.GetUnsafeReadOnlyPtr();
            var made = new NpcBoneTopology { _ranges = new NativeArray<RenderTopologyRange>(blocks.Length, Allocator.Persistent) };
            for (int b = 0; b < blocks.Length; b++)
            {
                VpGeometryVertexBlock block = blocks[b];
                made._ranges[b] = new RenderTopologyRange { vertexBase = (uint)block.vertexStart, count = block.vertexCount, topologyVertex = topologyBase + block.vertexStart };
            }

            made.Map = new RenderCutTopologyMap { ranges = (RenderTopologyRange*)made._ranges.GetUnsafePtr(), rangeCount = blocks.Length, topologyVertexCount = topologyVertexCount };
            topology = made;
            return true;
        }

        /// <summary>
        /// The map over the lineage's own vertex ranges (the base geometry's, then each cut's chunk): <paramref name="topologyOfLocal"/> one
        /// topology id a vertex of the lineage's numbering, ids below <paramref name="topologyVertexCount"/>; null: each render vertex its own.
        /// </summary>
        internal static NpcBoneTopology OfLocal(in NpcBoneSplitSearch.Lineage lineage, int[] topologyOfLocal, int topologyVertexCount)
        {
            int l = Math.Max(0, lineage.localCount);
            if (topologyOfLocal != null && topologyOfLocal.Length < l) throw new ArgumentException("one topology id a vertex of the lineage is needed", nameof(topologyOfLocal));
            int chunks = lineage.chunks != null ? lineage.chunks.Length : 0;
            var made = new NpcBoneTopology
            {
                _ids = new NativeArray<int>(Math.Max(1, l), Allocator.Persistent), _ranges = new NativeArray<RenderTopologyRange>(1 + chunks, Allocator.Persistent),
            };
            for (int i = 0; i < l; i++) made._ids[i] = topologyOfLocal != null ? topologyOfLocal[i] : i;
            int* ids = (int*)made._ids.GetUnsafePtr();
            made._ranges[0] = new RenderTopologyRange { vertexBase = lineage.baseStart, count = Math.Min(lineage.baseCount, l), topologyVertex = ids };
            for (int k = 0; k < chunks; k++)
            {
                NpcVertexChunk chunk = lineage.chunks[k];
                made._ranges[1 + k] = new RenderTopologyRange { vertexBase = chunk.start, count = chunk.count, topologyVertex = ids + chunk.localBase };
            }

            made.Map = new RenderCutTopologyMap
            {
                ranges = (RenderTopologyRange*)made._ranges.GetUnsafePtr(), rangeCount = 1 + chunks, topologyVertexCount = topologyOfLocal != null ? topologyVertexCount : Math.Max(1, l),
            };
            return made;
        }

        public void Dispose()
        {
            Map = default;
            if (_ranges.IsCreated) _ranges.Dispose();
            if (_ids.IsCreated) _ids.Dispose();
        }
    }
}
