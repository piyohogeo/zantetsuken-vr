using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.MeshCut;

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

        /// <summary>Mesh components (triangles joined by shared vertices); groups (connected by the bones) with a convex or triangles; of them, with both.</summary>
        public int meshComponents, groups, fullGroups;

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
    /// cluster; the three corners of a triangle are joined (a mesh component is triangles that share a vertex; one is never
    /// divided); a vertex joins the cluster of every bone it has a positive weight on -- a base vertex its model's weights, a
    /// vertex a cut made the weights of the vertices it interpolates (both where it lies strictly between them, the one where
    /// it lies on it), walked back to the base, a cap's auxiliary vertex none -- **among the bones the piece holds**: a weight on
    /// a bone not held connects nothing (no other bone is chosen for it); a held bone connects whether it is held by its convex or by
    /// its joint. So a convex and a
    /// convex of one cluster are connected, a convex and a mesh component weighted to a bone of that cluster, two mesh components
    /// weighted to the bones of one cluster -- and a mesh component weighted to several clusters connects them. Nothing of the
    /// weights is removed: the others are only left out of the connections, and counted.
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
                End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonInput);
                return;
            }

            if (t > input.maxTriangles)
            {
                End(ref record, NpcBoneSplitVerdict.OverTheLimits, ReasonTriangles);
                return;
            }

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
            NativeArray<int> uf = default, tally = default, triangleFirst = default, stack = default, parts = default;
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
                record.nativeBytes = ((long)uf.Length * sizeof(int)) + ((long)tally.Length * sizeof(int)) + ((long)triangleFirst.Length * sizeof(int)) + marks.Length + ((long)stack.Length * sizeof(int))
                    + piece.Length + ((long)parts.Length * sizeof(int));
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
                        End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonConvex);
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

                // 3. The mesh components: the corners of each triangle joined, every vertex in the lineage's own numbering.
                byte* referenced = (byte*)marks.GetUnsafePtr();
                for (int k = 0; k < t; k++)
                {
                    int a = Local(in input.lineage, input.indices[3 * k]);
                    int b = Local(in input.lineage, input.indices[(3 * k) + 1]);
                    int d = Local(in input.lineage, input.indices[(3 * k) + 2]);
                    if (a < 0 || b < 0 || d < 0)
                    {
                        End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonVertex);
                        return;
                    }

                    Union(parent, vertexBase + a, vertexBase + b);
                    Union(parent, vertexBase + a, vertexBase + d);
                    triangleFirst[k] = vertexBase + a;
                    referenced[a] = 1;
                    referenced[b] = 1;
                    referenced[d] = 1;
                }

                {
                    // The components' count, before the bones join them (a mark a root, cleared after).
                    int* seen = (int*)tally.GetUnsafePtr();
                    for (int k = 0; k < t; k++)
                    {
                        int r = Find(parent, triangleFirst[k]);
                        if (seen[r] == 0) { seen[r] = 1; record.meshComponents++; }
                    }

                    UnsafeUtility.MemClear(seen, (long)e * sizeof(int));
                }

                // 4. Every vertex the triangles name to the clusters of the bones it is weighted to, walked back to the base.
                int* walk = (int*)stack.GetUnsafePtr();
                long steps = 0;
                for (int v = 0; v < l; v++)
                {
                    if (referenced[v] == 0) continue;
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
                                    End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonWeight);
                                    return;
                                }

                                // A known bone: the vertex is weighted. It connects only through a bone the piece holds (NPC-BONE-HELD-1); a weight
                                // on a bone not held is left out of the connections (the model's weights are not changed).
                                weighted[v] = 1;
                                if (held[bone] == 0)
                                {
                                    record.removedWeights++;
                                    continue;
                                }

                                Union(parent, vertexBase + v, bone);
                            }

                            continue;
                        }

                        if (!TryOrigin(in input.lineage, x, out uint2 origin))
                        {
                            End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonVertex);
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
                            End(ref record, NpcBoneSplitVerdict.Insufficient, ReasonVertex);
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
        private static bool TryOrigin(in Lineage lineage, int local, out uint2 origin)
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
}
