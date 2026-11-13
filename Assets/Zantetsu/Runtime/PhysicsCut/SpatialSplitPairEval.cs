#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// NPC-PAIR-EVAL-1: what a placement means for one saved pick, worked out after a run and outside every time (plain managed code
    /// over the saved input): where the pointed triangle went against the trunk, whether each child's surface is closed by the
    /// topology, what a child loses of its supports, and the bones each child would have to keep.
    /// </summary>
    internal sealed class SpatialSplitPairAnalysis
    {
        public int triangles, convexes, positiveTriangles, negativeTriangles, positiveConvexes, negativeConvexes, cutConvexes;
        /// <summary>The triangle nearest the point the person pointed at (geometry coordinates), its side; the convex of the bone nearest the rig's root, its side (2: cut).</summary>
        public int hitTriangle = -1, hitSide = -1, trunkConvex = -1, trunkBone = -1, trunkSide = -1;
        public float hitDistance;
        public bool hitSeparated;
        /// <summary>False where the input carries no pointed point (an attempt the play issued, not a person's pick): the hit fields are then not taken.</summary>
        public bool hitTaken;
        /// <summary>
        /// Review 1 (2026-11-13), for a placement with a loss: each vertex without any of its convexes (its id, its sides' bits -- 1
        /// positive, 2 negative -- and the bones with a convex it supports), each mesh component (its triangles, its side, the least
        /// vertex id of it) and each convex's bone, so that the two children can be followed.
        /// </summary>
        public List<(uint id, int sides, int[] bones)> lostVertices = new List<(uint, int, int[])>();
        public List<(int triangles, int side, uint leastVertex)> components = new List<(int, int, uint)>();
        public int[] convexBones;
        public byte[] convexSidesKept;
        /// <summary>Edges by topology vertex: used once (open), by more than two triangles, or by two in the same direction -- the parent's, then each child's.</summary>
        public int parentBoundary, parentNonManifold, parentBadWinding, positiveBoundary, positiveNonManifold, positiveBadWinding, negativeBoundary, negativeNonManifold, negativeBadWinding;
        public int componentsDivided;
        /// <summary>Convexes whose bone has supporting vertices in the piece and none on the convex's side; vertices that support a bone with a convex and have none of those convexes on their side.</summary>
        public List<int> convexesWithoutMesh = new List<int>();
        public int verticesWithoutSupport;
        /// <summary>The bones each child needs (a convex of it on that side, or a vertex of that side weighted to it), bounded by what the parent holds; and what follows from them.</summary>
        public List<int> positiveBones = new List<int>(), negativeBones = new List<int>(), bothBones = new List<int>();
        public int positiveJoints, negativeJoints, positiveSegments, negativeSegments, positiveAuxiliary, negativeAuxiliary;
        /// <summary>For each bone both children need: bone, its convex's side (-1 none, 2 cut), supporting vertices on the positive side, on the negative side.</summary>
        public List<int4> bothBasis = new List<int4>();

        public bool ChildrenClosed => positiveBoundary == 0 && positiveNonManifold == 0 && positiveBadWinding == 0 && negativeBoundary == 0 && negativeNonManifold == 0 && negativeBadWinding == 0;
        public bool ParentClosed => parentBoundary == 0 && parentNonManifold == 0 && parentBadWinding == 0;

        /// <summary>
        /// The analysis of one placement. <paramref name="topologyOfLocal"/>: one topology id a vertex of the saved lineage (null: each
        /// its own). <paramref name="convexSides"/>: 1 positive, 0 negative, 2 cut; <paramref name="triangleSides"/>: 1 positive, 0 negative.
        /// </summary>
        internal static SpatialSplitPairAnalysis Of(SpatialSplitSample sample, SpatialSplitBoneSample bones, int[] topologyOfLocal, float3 hitGeometry, byte[] convexSides, byte[] triangleSides)
        {
            var a = new SpatialSplitPairAnalysis();
            int t = sample.indices.Length / 3, c = bones.convexBones.Length, n = bones.nodeCount;
            a.triangles = t; a.convexes = c;
            for (int k = 0; k < t; k++) { if (triangleSides[k] != 0) a.positiveTriangles++; else a.negativeTriangles++; }
            for (int k = 0; k < c; k++) { if (convexSides[k] == 2) a.cutConvexes++; else if (convexSides[k] != 0) a.positiveConvexes++; else a.negativeConvexes++; }

            // the vertices: positions by id, lineage numbers, topology keys
            var slotOf = new Dictionary<uint, int>(sample.vertexIds.Length);
            for (int i = 0; i < sample.vertexIds.Length; i++) slotOf[sample.vertexIds[i]] = i;
            int stride = sample.vertexStructBytes, at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            float3 Position(uint id)
            {
                int slot = slotOf[id];
                return new float3(BitConverter.ToSingle(sample.vertexBytes, (slot * stride) + at0), BitConverter.ToSingle(sample.vertexBytes, (slot * stride) + at0 + 4), BitConverter.ToSingle(sample.vertexBytes, (slot * stride) + at0 + 8));
            }

            int Key(uint id)
            {
                int local = NpcPiecePickSet.LocalOf(bones, id);
                return topologyOfLocal != null && local >= 0 && local < topologyOfLocal.Length ? topologyOfLocal[local] : local;
            }

            // 1. the pointed triangle and the trunk's convex
            float best = float.PositiveInfinity;
            a.hitTaken = math.all(math.isfinite(hitGeometry));
            for (int k = 0; k < t && a.hitTaken; k++)
            {
                float d = PointTriangleDistance(hitGeometry, Position(sample.indices[3 * k]), Position(sample.indices[(3 * k) + 1]), Position(sample.indices[(3 * k) + 2]));
                if (d < best) { best = d; a.hitTriangle = k; }
            }

            a.hitDistance = a.hitTaken ? best : -1f;
            a.hitSide = a.hitTriangle >= 0 ? triangleSides[a.hitTriangle] : -1;
            int bestDepth = int.MaxValue;
            for (int k = 0; k < c; k++)
            {
                int depth = 0;
                for (int b = bones.convexBones[k]; b >= 0 && b < n && depth <= n; b = bones.parent[b]) depth++;
                if (depth < bestDepth) { bestDepth = depth; a.trunkConvex = k; a.trunkBone = bones.convexBones[k]; }
            }

            a.trunkSide = a.trunkConvex >= 0 ? convexSides[a.trunkConvex] : -1;
            a.hitSeparated = a.hitSide >= 0 && a.trunkSide >= 0 && a.trunkSide != 2 && a.hitSide != a.trunkSide;

            // 2. the surfaces by topology: the parent's, each child's
            Surface(sample.indices, t, null, 0, Key, out a.parentBoundary, out a.parentNonManifold, out a.parentBadWinding);
            Surface(sample.indices, t, triangleSides, 1, Key, out a.positiveBoundary, out a.positiveNonManifold, out a.positiveBadWinding);
            Surface(sample.indices, t, triangleSides, 0, Key, out a.negativeBoundary, out a.negativeNonManifold, out a.negativeBadWinding);

            // a component (triangles sharing a topology vertex) on two sides
            var parent = new Dictionary<int, int>();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int k = 0; k < t; k++)
            {
                int k0 = Key(sample.indices[3 * k]);
                if (!parent.ContainsKey(k0)) parent[k0] = k0;
                for (int q = 1; q < 3; q++)
                {
                    int kq = Key(sample.indices[(3 * k) + q]);
                    if (!parent.ContainsKey(kq)) parent[kq] = kq;
                    int ra = Find(k0), rb = Find(kq);
                    if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                }
            }

            var sideOfComponent = new Dictionary<int, int>();
            var divided = new HashSet<int>();
            for (int k = 0; k < t; k++)
            {
                int root = Find(Key(sample.indices[3 * k]));
                if (sideOfComponent.TryGetValue(root, out int was)) { if (was != triangleSides[k]) divided.Add(root); }
                else sideOfComponent[root] = triangleSides[k];
            }

            a.componentsDivided = divided.Count;
            var trianglesOfComponent = new Dictionary<int, int>();
            var leastOfComponent = new Dictionary<int, uint>();
            for (int k = 0; k < t; k++)
            {
                int root = Find(Key(sample.indices[3 * k]));
                trianglesOfComponent[root] = trianglesOfComponent.TryGetValue(root, out int had) ? had + 1 : 1;
                uint least = Math.Min(sample.indices[3 * k], Math.Min(sample.indices[(3 * k) + 1], sample.indices[(3 * k) + 2]));
                leastOfComponent[root] = leastOfComponent.TryGetValue(root, out uint was2) ? Math.Min(was2, least) : least;
            }

            foreach (KeyValuePair<int, int> e in trianglesOfComponent) a.components.Add((e.Value, sideOfComponent[e.Key], leastOfComponent[e.Key]));
            a.components.Sort((x, y) => x.leastVertex.CompareTo(y.leastVertex));
            a.convexBones = (int[])bones.convexBones.Clone();
            a.convexSidesKept = (byte[])convexSides.Clone();

            // 3. the supports: each named vertex's bones (walked to the base), its side by its triangles
            var vertexSide = new Dictionary<uint, int>();
            for (int k = 0; k < t; k++)
            {
                for (int q = 0; q < 3; q++)
                {
                    uint id = sample.indices[(3 * k) + q];
                    int side = triangleSides[k] != 0 ? 1 : 2;   // bit 0: positive, bit 1: negative
                    vertexSide[id] = vertexSide.TryGetValue(id, out int had) ? had | side : side;
                }
            }

            var convexOfBone = new int[n];
            for (int k = 0; k < n; k++) convexOfBone[k] = -1;
            for (int k = 0; k < c; k++) if ((uint)bones.convexBones[k] < (uint)n) convexOfBone[bones.convexBones[k]] = k;
            var supportPositive = new int[n];
            var supportNegative = new int[n];
            var memo = new Dictionary<int, HashSet<int>>();
            foreach (KeyValuePair<uint, int> v in vertexSide)
            {
                HashSet<int> of = BonesOf(bones, NpcPiecePickSet.LocalOf(bones, v.Key), memo);
                bool onPositive = (v.Value & 1) != 0, onNegative = (v.Value & 2) != 0;
                bool supports = false, positiveHas = false, negativeHas = false;
                foreach (int bone in of)
                {
                    if ((uint)bone >= (uint)n) continue;
                    if (onPositive) supportPositive[bone]++;
                    if (onNegative) supportNegative[bone]++;
                    int convex = convexOfBone[bone];
                    if (convex < 0) continue;
                    supports = true;
                    if (convexSides[convex] != 0) positiveHas = true;   // positive, or cut (on both)
                    if (convexSides[convex] != 1) negativeHas = true;   // negative, or cut
                }

                if (supports && ((onPositive && !positiveHas) || (onNegative && !negativeHas)))
                {
                    a.verticesWithoutSupport++;
                    var withConvex = new List<int>();
                    foreach (int bone in of) if ((uint)bone < (uint)n && convexOfBone[bone] >= 0) withConvex.Add(bone);
                    withConvex.Sort();
                    a.lostVertices.Add((v.Key, v.Value, withConvex.ToArray()));
                }
            }

            for (int k = 0; k < c; k++)
            {
                int bone = bones.convexBones[k];
                if ((uint)bone >= (uint)n || convexSides[k] == 2) continue;
                int same = convexSides[k] != 0 ? supportPositive[bone] : supportNegative[bone];
                if (supportPositive[bone] + supportNegative[bone] > 0 && same == 0) a.convexesWithoutMesh.Add(k);
            }

            // 4. the bones each child would keep: needed by it (its convex, or a vertex of its side weighted to it) and held by the parent
            for (int k = 0; k < n; k++)
            {
                bool held = bones.piecePresent != null && k < bones.piecePresent.Length ? bones.piecePresent[k] : bones.present[k];
                int convex = convexOfBone[k];
                if (!held && convex < 0) continue;
                bool needsPositive = supportPositive[k] > 0 || (convex >= 0 && convexSides[convex] != 0);
                bool needsNegative = supportNegative[k] > 0 || (convex >= 0 && convexSides[convex] != 1);
                if (needsPositive) { a.positiveBones.Add(k); if (convex < 0) a.positiveAuxiliary++; }
                if (needsNegative) { a.negativeBones.Add(k); if (convex < 0) a.negativeAuxiliary++; }
                if (needsPositive && needsNegative)
                {
                    a.bothBones.Add(k);
                    a.bothBasis.Add(new int4(k, convex < 0 ? -1 : convexSides[convex], supportPositive[k], supportNegative[k]));
                }
            }

            var inPositive = new HashSet<int>(a.positiveBones);
            var inNegative = new HashSet<int>(a.negativeBones);
            for (int k = 0; k < n; k++)
            {
                bool jointKept = bones.jointKept != null && (bones.jointKept[k >> 6] & (1UL << (k & 63))) != 0;
                bool segmentKept = bones.segmentLo != null && bones.segmentLo[k] <= bones.segmentHi[k];
                int up = bones.parent[k];
                if (jointKept && inPositive.Contains(k)) a.positiveJoints++;
                if (jointKept && inNegative.Contains(k)) a.negativeJoints++;
                if (segmentKept && up >= 0 && inPositive.Contains(k) && inPositive.Contains(up)) a.positiveSegments++;
                if (segmentKept && up >= 0 && inNegative.Contains(k) && inNegative.Contains(up)) a.negativeSegments++;
            }

            return a;
        }

        // The bones a lineage vertex is weighted to: a base vertex its model's, a cut's vertex those of the vertices it came from.
        internal static HashSet<int> BonesOf(SpatialSplitBoneSample b, int local, Dictionary<int, HashSet<int>> memo)
        {
            if (memo.TryGetValue(local, out HashSet<int> known)) return known;
            var of = new HashSet<int>();
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            stack.Push(local);
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                if (x < 0 || !seen.Add(x)) continue;
                if (x < b.baseCount)
                {
                    if (b.baseBones == null || x >= b.baseBones.Length) continue;
                    ulong entry = b.baseBones[x];
                    for (int q = 0; q < 4; q++)
                    {
                        int bone = (int)((entry >> (16 * q)) & 0xFFFF);
                        if (bone != NpcVertexBones.None) of.Add(bone);
                    }

                    continue;
                }

                for (int ch = 0; ch < b.chunkStarts.Length; ch++)
                {
                    int at = x - b.chunkLocalBases[ch];
                    if (at < 0 || at >= b.chunkOrigins[ch].Length) continue;
                    uint2 origin = b.chunkOrigins[ch][at];
                    if (origin.x != Zantetsu.MeshCut.MeshCutOutput.NoVertexOrigin)
                    {
                        stack.Push(NpcPiecePickSet.LocalOf(b, origin.x));
                        if (origin.y != origin.x) stack.Push(NpcPiecePickSet.LocalOf(b, origin.y));
                    }

                    break;
                }
            }

            memo[local] = of;
            return of;
        }

        // The edges of the triangles of one side (side 1 or 0; all with sides null), by topology key.
        private static void Surface(uint[] indices, int t, byte[] sides, int side, Func<uint, int> key, out int boundary, out int nonManifold, out int badWinding)
        {
            var uses = new Dictionary<long, int>();
            var directed = new Dictionary<long, int>();
            for (int k = 0; k < t; k++)
            {
                if (sides != null && (sides[k] != 0 ? 1 : 0) != side) continue;
                int a = key(indices[3 * k]), b = key(indices[(3 * k) + 1]), c = key(indices[(3 * k) + 2]);
                Edge(uses, directed, a, b); Edge(uses, directed, b, c); Edge(uses, directed, c, a);
            }

            boundary = 0; nonManifold = 0; badWinding = 0;
            foreach (KeyValuePair<long, int> e in uses)
            {
                if (e.Value == 1) boundary++;
                else if (e.Value > 2) nonManifold++;
                else
                {
                    long lo = e.Key >> 32, hi = e.Key & 0xFFFFFFFFL;
                    directed.TryGetValue((lo << 32) | hi, out int forward);
                    directed.TryGetValue((hi << 32) | lo, out int backward);
                    if (forward != 1 || backward != 1) badWinding++;
                }
            }
        }

        private static void Edge(Dictionary<long, int> uses, Dictionary<long, int> directed, int a, int b)
        {
            long lo = Math.Min(a, b), hi = Math.Max(a, b);
            long undirected = (lo << 32) | hi, d = ((long)a << 32) | (uint)b;
            uses[undirected] = uses.TryGetValue(undirected, out int u) ? u + 1 : 1;
            directed[d] = directed.TryGetValue(d, out int w) ? w + 1 : 1;
        }

        internal static float PointTriangleDistance(float3 p, float3 a, float3 b, float3 c)
        {
            // Ericson's closest point on a triangle
            float3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return math.distance(p, a);
            float3 bp = p - b;
            float d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return math.distance(p, b);
            float vc = (d1 * d4) - (d3 * d2);
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return math.distance(p, a + (ab * (d1 / (d1 - d3))));
            float3 cp = p - c;
            float d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return math.distance(p, c);
            float vb = (d5 * d2) - (d1 * d6);
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return math.distance(p, a + (ac * (d2 / (d2 - d6))));
            float va = (d3 * d6) - (d5 * d4);
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return math.distance(p, b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))));
            float denom = 1f / (va + vb + vc);
            return math.distance(p, a + (ab * (vb * denom)) + (ac * (vc * denom)));
        }

        public bool HasLoss => verticesWithoutSupport > 0 || convexesWithoutMesh.Count > 0 || componentsDivided > 0 || !ChildrenClosed;

        /// <summary>The placement in full, for one with a loss: the convexes (bone, side), the mesh components (triangles, side, least vertex), the vertices without their convexes.</summary>
        internal string LossJson()
        {
            var s = new StringBuilder(512);
            s.Append("{\"convexes\":[");
            for (int k = 0; k < convexBones.Length; k++) s.Append(k > 0 ? "," : "").Append('[').Append(convexBones[k]).Append(',').Append(convexSidesKept[k]).Append(']');
            s.Append("],\"components\":[");
            for (int k = 0; k < components.Count; k++) s.Append(k > 0 ? "," : "").Append('[').Append(components[k].triangles).Append(',').Append(components[k].side).Append(',').Append(components[k].leastVertex).Append(']');
            s.Append("],\"convexesWithoutMesh\":[").Append(string.Join(",", convexesWithoutMesh)).Append("],\"lostVertices\":[");
            for (int k = 0; k < lostVertices.Count; k++)
            {
                s.Append(k > 0 ? "," : "").Append('[').Append(lostVertices[k].id).Append(',').Append(lostVertices[k].sides).Append(",[").Append(string.Join(",", lostVertices[k].bones)).Append("]]");
            }

            s.Append("]}");
            return s.ToString();
        }

        internal string Json()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var s = new StringBuilder(512);
            s.Append("{\"triangles\":").Append(triangles).Append(",\"convexes\":").Append(convexes)
                .Append(",\"positiveTriangles\":").Append(positiveTriangles).Append(",\"negativeTriangles\":").Append(negativeTriangles)
                .Append(",\"positiveConvexes\":").Append(positiveConvexes).Append(",\"negativeConvexes\":").Append(negativeConvexes).Append(",\"cutConvexes\":").Append(cutConvexes)
                .Append(",\"hitTaken\":").Append(hitTaken ? "true" : "false").Append(",\"hitTriangle\":").Append(hitTriangle).Append(",\"hitDistance\":").Append(hitDistance.ToString("R", inv)).Append(",\"hitSide\":").Append(hitSide)
                .Append(",\"trunkConvex\":").Append(trunkConvex).Append(",\"trunkBone\":").Append(trunkBone).Append(",\"trunkSide\":").Append(trunkSide)
                .Append(",\"hitSeparated\":").Append(hitSeparated ? "true" : "false")
                .Append(",\"parentClosed\":").Append(ParentClosed ? "true" : "false").Append(",\"childrenClosed\":").Append(ChildrenClosed ? "true" : "false")
                .Append(",\"parentSurface\":[").Append(parentBoundary).Append(',').Append(parentNonManifold).Append(',').Append(parentBadWinding).Append(']')
                .Append(",\"positiveSurface\":[").Append(positiveBoundary).Append(',').Append(positiveNonManifold).Append(',').Append(positiveBadWinding).Append(']')
                .Append(",\"negativeSurface\":[").Append(negativeBoundary).Append(',').Append(negativeNonManifold).Append(',').Append(negativeBadWinding).Append(']')
                .Append(",\"componentsDivided\":").Append(componentsDivided)
                .Append(",\"convexesWithoutMesh\":[").Append(string.Join(",", convexesWithoutMesh)).Append(']').Append(",\"verticesWithoutSupport\":").Append(verticesWithoutSupport)
                .Append(",\"positiveBones\":[").Append(string.Join(",", positiveBones)).Append("],\"negativeBones\":[").Append(string.Join(",", negativeBones))
                .Append("],\"bothBones\":[").Append(string.Join(",", bothBones)).Append("],\"bothBasis\":[");
            for (int i = 0; i < bothBasis.Count; i++)
            {
                if (i > 0) s.Append(',');
                s.Append('[').Append(bothBasis[i].x).Append(',').Append(bothBasis[i].y).Append(',').Append(bothBasis[i].z).Append(',').Append(bothBasis[i].w).Append(']');
            }

            s.Append("],\"positiveJoints\":").Append(positiveJoints).Append(",\"negativeJoints\":").Append(negativeJoints)
                .Append(",\"positiveSegments\":").Append(positiveSegments).Append(",\"negativeSegments\":").Append(negativeSegments)
                .Append(",\"positiveAuxiliary\":").Append(positiveAuxiliary).Append(",\"negativeAuxiliary\":").Append(negativeAuxiliary).Append('}');
            return s.ToString();
        }
    }

    public static unsafe partial class SpatialSplitMethodEval
    {
        [Serializable]
        private sealed class OutsideTopology
        {
            public int pick, topologyCount;
            public bool exact;
            public int[] ids;
        }

        // The three methods of one position: the current one (the bones, then the method as before), the combination, the combination
        // with the protection -- the leader moving on by one with the input's position and with each repeat.
        /// <summary>The three methods a pair evaluation runs (NPC-PAIR-EVAL-3: selectable; the first is the one the others are held against).</summary>
        internal static string[] PairMethodSet = { BoneFallback, PairOr, PairProtect };

        /// <summary>Whether every input is run once in every method before it is measured (off for a re-decision of one repeat, where no time is read).</summary>
        internal static bool PairWarm = true;

        /// <summary>NPC-PAIR-EVAL-3: the protected combination, it with A, it with B.</summary>
        public static void UseProtectAb() => PairMethodSet = new[] { PairProtect, PairProtectA, PairProtectB };

        public static void NoPairWarm() => PairWarm = false;

        private static string[] PairMethods(int parity)
        {
            string[] all = PairMethodSet;
            int shift = ((parity % 3) + 3) % 3;
            return new[] { all[shift], all[(shift + 1) % 3], all[(shift + 2) % 3] };
        }

        /// <summary>
        /// NPC-PAIR-EVAL-1: the three methods over the saved picks of one folder, in one process: every pick that is whole and has an
        /// exact topology (its own saved table; or, for a pick of the format before, a file of <paramref name="outsideTopology"/> marked
        /// exact). Each input once in every method to warm (no line), then <paramref name="repeats"/> times in every method, the order
        /// rotating with the input's position and the repeat. The reading of the picks and the making of each run's vertices, indices
        /// and shape are outside every time. Into a new folder: inputs.jsonl, baseline.jsonl (the bones alone, as the pick verification
        /// decides them), runs.jsonl (one line a measured run), analysis.jsonl (one line an input and method: the placement's meaning),
        /// summary.txt. <paramref name="exitCode"/>: 0 every run completed, 3 not, 1 nothing was run.
        /// </summary>
        public static string RunPicks(string pickFolder, string outsideTopology, string outputDirectory, int repeats, out int exitCode)
        {
            exitCode = 1;
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("the output exists already: " + outputDirectory);
            if (repeats < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            List<NpcPiecePickSet.Pick> picks = NpcPiecePickSet.Read(pickFolder);
            var inputs = new List<Input>();
            var skipped = new List<string>();
            foreach (NpcPiecePickSet.Pick pick in picks)
            {
                if (!pick.IsWhole) { skipped.Add("pick " + pick.number + ": not whole (" + (pick.failure ?? string.Join("; ", pick.problems)) + ")"); continue; }
                var input = new Input { index = inputs.Count, category = "", sample = pick.sample, bones = pick.bones, pick = pick.number, record = pick.record };
                if (pick.HasTopology)
                {
                    input.topologyIds = NpcPiecePickSet.SavedTopologyOfLocal(pick, out input.topologyCount);
                    input.topologySource = "saved";
                }
                else
                {
                    string file = outsideTopology != null ? Path.Combine(outsideTopology, "pick-" + pick.number.ToString(inv) + ".json") : null;
                    OutsideTopology given = file != null && File.Exists(file) ? JsonUtility.FromJson<OutsideTopology>(File.ReadAllText(file)) : null;
                    if (given == null || given.ids == null || !given.exact) { skipped.Add("pick " + pick.number + ": no exact topology"); continue; }
                    input.topologyIds = given.ids;
                    input.topologyCount = given.topologyCount;
                    input.topologySource = "given";
                }

                MakeNative(input);
                inputs.Add(input);
            }

            text.Append("NPC-PAIR-EVAL-1 pairwise combination: folder ").Append(pickFolder).Append("; picks ").Append(picks.Count).Append(", inputs taken ").Append(inputs.Count).Append(", skipped ").Append(skipped.Count).Append('\n');
            foreach (string s in skipped) text.Append("  SKIPPED ").Append(s).Append('\n');
            if (inputs.Count == 0)
            {
                text.Append("NOT RUN: no input\n");
                return text.ToString();
            }

            Directory.CreateDirectory(outputDirectory);
            var utf8 = new UTF8Encoding(false);

            // 1. The inputs, the bones alone (the accepted decision's check) and the node counts by render vertex and by topology -- not timed.
            using (var inputsFile = new StreamWriter(Path.Combine(outputDirectory, "inputs.jsonl"), false, utf8))
            using (var baseline = new StreamWriter(Path.Combine(outputDirectory, "baseline.jsonl"), false, utf8))
            {
                foreach (Input i in inputs)
                {
                    NpcPiecePickSet.Pick pick = picks.Find(p => p.number == i.pick && p.sample == i.sample);
                    NpcBoneSplitRecord d = NpcPiecePickSet.Decide(pick, i.topologyIds, i.topologyCount, out byte[] sides, out byte[] _);
                    int c = i.bones.convexBones.Length, t = i.sample.indices.Length / 3;
                    var triangleSides = new StringBuilder(t);
                    if (d.verdict == NpcBoneSplitVerdict.Separable) for (int k = 0; k < t; k++) triangleSides.Append(sides[c + k] != 0 ? '1' : '0');
                    baseline.Write("{\"pick\":" + i.pick + ",\"verdict\":\"" + d.verdict + "\",\"reason\":" + SpatialSplitCaptureSink.Json(d.reason ?? "") + ",\"clusters\":" + d.clusters + ",\"groups\":" + d.groups
                        + ",\"fullGroups\":" + d.fullGroups + ",\"meshComponents\":" + d.meshComponents + ",\"connectionsKept\":" + d.connectionsKept + ",\"connectionsDropped\":" + d.connectionsDropped
                        + ",\"connectionsProtected\":" + d.connectionsProtected + ",\"positiveTriangles\":" + d.positiveTriangles + ",\"negativeTriangles\":" + d.negativeTriangles
                        + ",\"positiveConvexes\":" + d.positiveConvexes + ",\"negativeConvexes\":" + d.negativeConvexes + ",\"triangleSides\":\"" + triangleSides + "\"}\n");
                    Components(i, out int bySlot, out int byTopology);
                    inputsFile.Write("{\"index\":" + i.index + ",\"pick\":" + i.pick + ",\"world\":" + i.sample.world + ",\"fragment\":" + i.sample.fragment + ",\"rootFragment\":" + i.sample.rootFragment
                        + ",\"splitSource\":" + i.sample.splitSource + ",\"generation\":" + i.sample.generation + ",\"epoch\":" + i.sample.epoch + ",\"model\":" + SpatialSplitCaptureSink.Json(i.record.modelName ?? "")
                        + ",\"madeBy\":" + SpatialSplitCaptureSink.Json(i.record.noteMadeBy ?? "") + ",\"topology\":\"" + i.topologySource + "\",\"convexes\":" + c + ",\"triangles\":" + t
                        + ",\"namedVertices\":" + i.sample.vertexIds.Length + ",\"bones\":" + i.bones.nodeCount + ",\"meshComponentsByRenderVertex\":" + bySlot + ",\"meshComponentsByTopology\":" + byTopology
                        + ",\"graphSearch\":" + B(i.bones.graphSearch) + ",\"graphFirst\":" + B(i.bones.graphFirst) + ",\"bentSearch\":" + B(i.sample.bentSearch) + ",\"cutConvexes\":" + B(i.sample.cutConvexes)
                        + ",\"anchors\":" + (i.sample.anchors != null ? i.sample.anchors.Length : 0) + ",\"sha256\":\"" + i.sample.sha256 + "\",\"boneSha256\":\"" + i.bones.sha256 + "\"}\n");
                }
            }

            // 2. The once-per-process work, apart.
            long p0 = Stopwatch.GetTimestamp();
            bool graphPrepared = SpatialSplitGraph.IsPrepared || SpatialSplitGraph.Prepare();
            double prepareGraph = Since(p0);
            long p1 = Stopwatch.GetTimestamp();
            int failed = 0, measured = 0;
            var first = new Dictionary<(int, string), Run>();
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Pc, SpatialSplitReplayPoolLoad.Level, 0, SpatialSplitReplayOptions.DefaultCompetitorWork, SpatialSplitReplayPool.DefaultBlockerWork))
            {
                double poolStart = Since(p1);
                text.Append("platform ").Append(Application.platform).Append(' ').Append(Application.unityVersion).Append("; ").Append(Application.isEditor ? "Editor" : UnityEngine.Debug.isDebugBuild ? "Development Player" : "Player")
#if ENABLE_IL2CPP
                    .Append(", IL2CPP")
#else
                    .Append(", Mono")
#endif
                    .Append("; Burst enabled ").Append(Unity.Burst.BurstCompiler.IsEnabled).Append("; processors ").Append(SystemInfo.processorCount).Append(' ').Append(SystemInfo.processorType)
                    .Append("; pool pc (Background ").Append(pool.Capacity).Append("), no competitor; timeline ").Append(CutSpatialSplit.TimelineObserve ? "on" : "off")
                    .Append(", dispatch observation ").Append(Zantetsu.MeshCut.DispatchObservation.Enabled ? "on" : "off").Append('\n');
                text.Append(ProbeManagedCounter()).Append('\n');
                text.Append("methods: ").Append(string.Join(", ", PairMethodSet)).Append(" (").Append(BoneFallback).Append(": the bones, then the method as before; a pair method: the combination, then the method as before where it places nothing); order by (input + repeat) mod 3; warm-up ")
                    .Append(PairWarm ? "on" : "off").Append('\n');
                text.Append("once per process: graph preparation ").Append(Ms(prepareGraph, inv)).Append(" ms (prepared ").Append(graphPrepared).Append("), pool start ").Append(Ms(poolStart, inv)).Append(" ms\n");

                // 3. The warm-up: every input once in every method (no line).
                long w0 = Stopwatch.GetTimestamp();
                int warmFailed = 0;
                foreach (Input i in inputs)
                {
                    if (!PairWarm) break;
                    foreach (string method in PairMethods(i.index))
                    {
                        Run r = RunOne(i, method, pool);
                        if (!r.ok) warmFailed++;
                    }
                }

                text.Append("warm-up: ").Append(inputs.Count).Append(" inputs x 3 methods in ").Append(Ms(Since(w0), inv)).Append(" ms (failed ").Append(warmFailed).Append(")\n");

                // 4. The measurement.
                using (var file = new StreamWriter(Path.Combine(outputDirectory, "runs.jsonl"), false, utf8))
                {
                    for (int repeat = 0; repeat < repeats; repeat++)
                    {
                        foreach (Input i in inputs)
                        {
                            string[] order = PairMethods(i.index + repeat);
                            for (int position = 0; position < order.Length; position++)
                            {
                                Run r = RunOne(i, order[position], pool);
                                measured++;
                                if (!r.ok) failed++;
                                file.Write(PairLine(i, repeat, position, r, inv));
                                if (!first.ContainsKey((i.index, r.method))) first[(i.index, r.method)] = r;
                            }
                        }
                    }
                }

                text.Append("measured: ").Append(measured).Append(" runs (").Append(inputs.Count).Append(" inputs x ").Append(repeats).Append(" repeats x 3 methods); failed ").Append(failed).Append('\n');
                text.Append(pool.Describe()).Append('\n');
            }

            // 5. Outside the measurement: what each method's placement means, from the first measured run of each input and method.
            int analysed = 0, analysisFailed = 0;
            using (var file = new StreamWriter(Path.Combine(outputDirectory, "analysis.jsonl"), false, utf8))
            {
                foreach (Input i in inputs)
                {
                    foreach (string method in PairMethodSet)
                    {
                        if (!first.TryGetValue((i.index, method), out Run r)) continue;
                        string body = "null", error = null;
                        if (r.ok && r.triangleSides != null && r.convexSides != null)
                        {
                            try
                            {
                                body = SpatialSplitPairAnalysis.Of(i.sample, i.bones, i.topologyIds, i.record.hitGeometry, r.convexSides, r.triangleSides).Json();
                                analysed++;
                            }
                            catch (Exception e)
                            {
                                error = e.GetType().Name + ": " + e.Message;
                                analysisFailed++;
                            }
                        }

                        file.Write("{\"index\":" + i.index + ",\"pick\":" + i.pick + ",\"method\":\"" + method + "\",\"end\":" + (r.end != null ? "\"" + r.end + "\"" : "null") + ",\"placement\":\"" + r.placement
                            + "\",\"pairVerdict\":" + (r.pair != null ? "\"" + r.pair.verdict + "\"" : "null") + ",\"sidesHash\":" + (r.sidesHash != null ? "\"" + r.sidesHash + "\"" : "null")
                            + ",\"partition\":\"" + PartitionHash(r.convexSides, r.triangleSides) + "\""
                            + ",\"analysisError\":" + (error != null ? SpatialSplitCaptureSink.Json(error) : "null") + ",\"analysis\":" + body + "}\n");
                    }
                }
            }

            text.Append("analysis: ").Append(analysed).Append(" placements read (failed ").Append(analysisFailed).Append(")\n");
            foreach (Input i in inputs) FreeNative(i);
            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), text.ToString(), utf8);
            exitCode = failed == 0 && analysisFailed == 0 ? 0 : 3;
            return text.ToString();
        }

        // The mesh components of an input as the graph stage made them before (triangles sharing a render vertex) and by its topology.
        private static void Components(Input i, out int bySlot, out int byTopology)
        {
            bySlot = Count(i, false);
            byTopology = Count(i, true);
        }

        private static int Count(Input i, bool topology)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            uint[] indices = i.sample.indices;
            int t = indices.Length / 3;
            var firstKey = new int[t];
            for (int k = 0; k < t; k++)
            {
                for (int q = 0; q < 3; q++)
                {
                    int local = NpcPiecePickSet.LocalOf(i.bones, indices[(3 * k) + q]);
                    int key = topology && i.topologyIds != null && local >= 0 && local < i.topologyIds.Length ? i.topologyIds[local] : -2 - local;
                    if (!parent.ContainsKey(key)) parent[key] = key;
                    if (q == 0) firstKey[k] = key;
                    else
                    {
                        int ra = Find(firstKey[k]), rb = Find(key);
                        if (ra != rb) parent[rb] = ra;
                    }
                }
            }

            var roots = new HashSet<int>();
            for (int k = 0; k < t; k++) roots.Add(Find(firstKey[k]));
            return roots.Count;
        }

        private static string Longs(long[] v) => "[" + v[0] + "," + v[1] + "," + v[2] + "]";

        /// <summary>
        /// A name of the two children a placement makes, the same whichever of them is called positive: the sides with the first
        /// triangle's side as 1 (a convex cut by a plane stays 2), hashed. "" where the placement is not told by sides.
        /// </summary>
        internal static string PartitionHash(byte[] convexSides, byte[] triangleSides)
        {
            if (convexSides == null || triangleSides == null || triangleSides.Length == 0) return "";
            bool flip = triangleSides[0] == 0;
            var bytes = new byte[convexSides.Length + triangleSides.Length];
            for (int k = 0; k < convexSides.Length; k++) bytes[k] = convexSides[k] == 2 ? (byte)2 : (byte)((convexSides[k] != 0) ^ flip ? 1 : 0);
            for (int k = 0; k < triangleSides.Length; k++) bytes[convexSides.Length + k] = (byte)((triangleSides[k] != 0) ^ flip ? 1 : 0);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var s = new StringBuilder(16);
                for (int k = 0; k < 8; k++) s.Append(hash[k].ToString("x2", CultureInfo.InvariantCulture));
                return s.ToString();
            }
        }

        private static string PairLine(Input i, int repeat, int position, Run r, CultureInfo inv)
        {
            var t = new StringBuilder(2048);
            t.Append("{\"index\":").Append(i.index).Append(",\"pick\":").Append(i.pick).Append(",\"repeat\":").Append(repeat).Append(",\"position\":").Append(position)
                .Append(",\"method\":\"").Append(r.method).Append("\",\"ok\":").Append(B(r.ok)).Append(",\"error\":").Append(r.error != null ? SpatialSplitCaptureSink.Json(r.error) : "null")
                .Append(",\"end\":").Append(r.end != null ? "\"" + r.end + "\"" : "null").Append(",\"detail\":").Append(r.detail != null ? SpatialSplitCaptureSink.Json(r.detail) : "null")
                .Append(",\"placement\":\"").Append(r.placement).Append("\",\"pairLed\":").Append(B(r.pairLed)).Append(",\"fellBack\":").Append(B(r.fellBack))
                .Append(",\"prepareMs\":").Append(Ms(r.prepare, inv)).Append(",\"decisionMs\":").Append(Ms(r.decision, inv)).Append(",\"distributionMs\":").Append(Ms(r.distribution, inv))
                .Append(",\"meshesMs\":").Append(Ms(r.meshes, inv)).Append(",\"bakeMs\":").Append(Ms(r.bake, inv)).Append(",\"workerMs\":").Append(Ms(r.worker, inv)).Append(",\"totalMs\":").Append(Ms(Total(r), inv))
                .Append(",\"cut\":").Append(B(r.cut)).Append(",\"graphLed\":").Append(B(r.graphLed)).Append(",\"bent\":").Append(B(r.bent)).Append(",\"meshLed\":").Append(B(r.meshLed))
                .Append(",\"searchOutcome\":").Append(r.searchOutcome != null ? "\"" + r.searchOutcome + "\"" : "null").Append(",\"graphVerdict\":").Append(r.graphVerdict != null ? "\"" + r.graphVerdict + "\"" : "null")
                .Append(",\"graphNodes\":").Append(r.graphNodes).Append(",\"graphMeshComponents\":").Append(r.graphMeshComponents).Append(",\"graphCandidates\":").Append(r.graphCandidates)
                .Append(",\"boneVerdict\":").Append(r.boneVerdict != null ? "\"" + r.boneVerdict + "\"" : "null").Append(",\"boneReason\":").Append(r.boneReason != null ? SpatialSplitCaptureSink.Json(r.boneReason) : "null")
                .Append(",\"positiveTriangles\":").Append(r.positiveTriangles).Append(",\"negativeTriangles\":").Append(r.negativeTriangles)
                .Append(",\"positiveConvexes\":").Append(r.positiveConvexes).Append(",\"negativeConvexes\":").Append(r.negativeConvexes)
                .Append(",\"clusters\":").Append(r.clusters).Append(",\"groups\":").Append(r.groups).Append(",\"fullGroups\":").Append(r.fullGroups).Append(",\"meshComponents\":").Append(r.meshComponents)
                .Append(",\"sidesHash\":").Append(r.sidesHash != null ? "\"" + r.sidesHash + "\"" : "null").Append(",\"partition\":\"").Append(PartitionHash(r.convexSides, r.triangleSides)).Append('"')
                .Append(",\"blockBytes\":").Append(r.blockBytes).Append(",\"classificationBytes\":").Append(r.classificationBytes).Append(",\"sidesBytes\":").Append(r.sidesBytes)
                .Append(",\"workerNativeBytes\":").Append(r.workerNativeBytes).Append(",\"prepareManagedBytes\":").Append(r.prepareManagedBytes)
                .Append(",\"graphBurstRan\":").Append(B(r.graphBurstRan)).Append(",\"kernelManaged\":").Append(B(r.kernelManaged)).Append(",\"pair\":");
            SpatialSplitPairRecord p = r.pair;
            if (p == null)
            {
                t.Append("null}\n");
                return t.ToString();
            }

            t.Append("{\"mode\":").Append(p.mode).Append(",\"verdict\":\"").Append(p.verdict).Append("\",\"stop\":\"").Append(p.stop).Append("\",\"reason\":").Append(SpatialSplitCaptureSink.Json(p.reason))
                .Append(",\"bones\":\"").Append(p.bones).Append("\",\"bonesKnown\":").Append(B(p.bonesKnown)).Append(",\"boneAlone\":\"").Append(p.boneAlone).Append("\",\"usedTopology\":").Append(B(p.usedTopology))
                .Append(",\"bodiesRan\":").Append(B(p.bodiesRan)).Append(",\"burstRan\":").Append(B(p.burstRan))
                .Append(",\"nodes\":").Append(p.nodes).Append(",\"meshComponents\":").Append(p.meshComponents).Append(",\"clusters\":").Append(p.clusters).Append(",\"seamSlots\":").Append(p.seamSlots)
                .Append(",\"candidates\":").Append(p.candidates).Append(",\"cutAttempts\":").Append(p.cutAttempts).Append(",\"gjkIterations\":").Append(p.gjkIterations).Append(",\"gjkLimitHit\":").Append(p.gjkLimitHit)
                .Append(",\"triedB\":").Append(p.triedB).Append(",\"refutedB\":").Append(p.refutedB).Append(",\"ambiguousB\":").Append(p.ambiguousB).Append(",\"work\":").Append(p.work)
                .Append(",\"graphBytes\":").Append(p.graphBytes).Append(",\"attributeBytes\":").Append(p.attributeBytes).Append(",\"boneBytes\":").Append(p.boneBytes)
                .Append(",\"protectedPairsOfBones\":").Append(p.protectedPairsOfBones)
                .Append(",\"ownMainPairs\":").Append(p.ownMainPairs).Append(",\"ownProtectedAdded\":").Append(p.ownProtectedAdded).Append(",\"ownUnderRatio\":").Append(p.ownUnderRatio)
                .Append(",\"boneMs\":").Append(Ms(p.boneSeconds, inv)).Append(",\"convertMs\":").Append(Ms(p.convertSeconds, inv)).Append(",\"nodesMs\":").Append(Ms(p.nodesSeconds, inv))
                .Append(",\"pairsMs\":").Append(Ms(p.pairsSeconds, inv)).Append(",\"allocateMs\":").Append(Ms(p.allocateSeconds, inv)).Append(",\"searchMs\":").Append(Ms(p.searchSeconds, inv))
                .Append(",\"finishMs\":").Append(Ms(p.finishSeconds, inv)).Append(",\"pairTotalMs\":").Append(Ms(p.totalSeconds, inv)).Append(",\"counts\":");
            SpatialSplitGraph.PairInput k = p.counts;
            if (k == null)
            {
                t.Append("null}}\n");
                return t.ToString();
            }

            t.Append("{\"searched\":").Append(B(k.searched)).Append(",\"pairs\":").Append(Longs(k.pairsByKind)).Append(",\"boneApart\":").Append(Longs(k.boneApart)).Append(",\"boxApart\":").Append(Longs(k.boxApart))
                .Append(",\"candidates\":").Append(Longs(k.candidates)).Append(",\"gjkTried\":").Append(Longs(k.gjkTried)).Append(",\"gjkRefuted\":").Append(Longs(k.gjkRefuted))
                .Append(",\"gjkUnsettled\":").Append(Longs(k.gjkUnsettled)).Append(",\"left\":").Append(Longs(k.left))
                .Append(",\"protectedPairs\":").Append(k.protectedPairs).Append(",\"protectedCandidates\":").Append(k.protectedCandidates).Append(",\"protectedKeptThoughApart\":").Append(k.protectedKeptThoughApart)
                .Append(",\"protectedApartByBoxes\":").Append(k.protectedApartByBoxes).Append(",\"protectedRefuted\":").Append(k.protectedRefuted).Append(",\"protectedAcross\":").Append(k.protectedAcross)
                .Append(",\"isolatedMesh\":").Append(k.isolatedMesh).Append(",\"meshOnlyNodes\":").Append(k.meshOnlyNodes).Append(",\"relocated\":").Append(k.relocated)
                .Append(",\"relocateAmbiguous\":").Append(k.relocateAmbiguous).Append(",\"relocateNoSupport\":").Append(k.relocateNoSupport).Append(",\"relocateAlreadyThere\":").Append(k.relocateAlreadyThere)
                .Append(",\"relocationFailed\":").Append(B(k.relocationFailed)).Append(",\"protectedBonesApart\":").Append(k.protectedBonesApart)
                .Append("}}}\n");
            return t.ToString();
        }
    }
}
#endif
