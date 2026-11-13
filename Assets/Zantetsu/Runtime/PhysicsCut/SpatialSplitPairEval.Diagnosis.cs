#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// NPC-PAIR-EVAL-2 review 2: one saved input decided by the pairwise combination with everything kept that says why a pair is
    /// or is not an edge -- each node's attributes (a convex's bone and cluster; a mesh component's adopted clusters, protected
    /// convexes, triangles, least vertex and the vertices it has on each bone with a convex), every pair's state (left out by the
    /// bones, by the boxes, taken off by the hulls, left, not settled or protected), and the sides. A diagnosis outside every
    /// measurement: the engine through its compact input (the saved sample's own form), the settings as the evaluation's attempt has them.
    /// </summary>
    internal sealed class SpatialSplitPairDiagnosis
    {
        public NpcBoneSplitRecord bone;
        public SpatialSplitPairBones bones;
        public string verdict = "", stop = "", reason = "", partition = "";
        public int convexes, meshNodes, clusters;
        public int[] convexBone, convexCluster;
        public byte[] nodeSide;                       // 1 positive, 0 negative, 255 no placement
        public int[] meshTriangles;
        public uint[] meshLeastVertex;
        public List<int>[] meshClusters, meshProtected;
        public Dictionary<int, int>[] meshSupport;    // a mesh node: bone with a convex -> its vertices on that bone
        public int[] clusterOfBone;
        public List<int>[] meshOwnMain;
        public int ownProtectedAdded, ownUnderRatio;
        public SpatialSplitGraph.PairInput counts;
        public List<(int a, int b, string state)> pairs = new List<(int, int, string)>();

        internal string Json(bool detail)
        {
            var s = new StringBuilder(1024);
            s.Append("{\"bone\":\"").Append(bone.verdict).Append("\",\"bones\":\"").Append(bones).Append("\",\"verdict\":\"").Append(verdict).Append("\",\"stop\":\"").Append(stop)
                .Append("\",\"partition\":\"").Append(partition).Append("\",\"convexes\":").Append(convexes).Append(",\"meshNodes\":").Append(meshNodes).Append(",\"clusters\":").Append(clusters)
                .Append(",\"ownProtectedAdded\":").Append(ownProtectedAdded).Append(",\"ownUnderRatio\":").Append(ownUnderRatio);
            if (counts != null)
            {
                s.Append(",\"isolatedMesh\":").Append(counts.isolatedMesh).Append(",\"meshOnlyNodes\":").Append(counts.meshOnlyNodes).Append(",\"relocated\":").Append(counts.relocated)
                    .Append(",\"relocateAmbiguous\":").Append(counts.relocateAmbiguous).Append(",\"relocateNoSupport\":").Append(counts.relocateNoSupport)
                    .Append(",\"relocationFailed\":").Append(counts.relocationFailed ? "true" : "false").Append(",\"protectedKeptThoughApart\":").Append(counts.protectedKeptThoughApart);
            }
            if (detail && convexBone != null)
            {
                s.Append(",\"convexNodes\":[");
                for (int k = 0; k < convexes; k++) s.Append(k > 0 ? "," : "").Append('[').Append(convexBone[k]).Append(',').Append(convexCluster[k]).Append(',').Append(nodeSide != null ? nodeSide[k] : (byte)255).Append(']');
                s.Append("],\"meshNodes_\":[");
                for (int k = 0; k < meshNodes; k++)
                {
                    s.Append(k > 0 ? "," : "").Append("{\"triangles\":").Append(meshTriangles[k]).Append(",\"side\":").Append(nodeSide != null ? nodeSide[convexes + k] : (byte)255)
                        .Append(",\"least\":").Append(meshLeastVertex[k]).Append(",\"clusters\":[").Append(string.Join(",", meshClusters[k])).Append("],\"protected\":[").Append(string.Join(",", meshProtected[k])).Append("],\"ownMain\":[").Append(string.Join(",", meshOwnMain != null && k < meshOwnMain.Length ? meshOwnMain[k] : new List<int>()))
                        .Append("],\"support\":{");
                    bool first = true;
                    foreach (KeyValuePair<int, int> e in meshSupport[k]) { s.Append(first ? "" : ",").Append('"').Append(e.Key).Append("\":").Append(e.Value); first = false; }
                    s.Append("}}");
                }

                s.Append("],\"clusterOfBone\":[").Append(string.Join(",", clusterOfBone)).Append("],\"pairs\":[");
                for (int k = 0; k < pairs.Count; k++) s.Append(k > 0 ? "," : "").Append('[').Append(pairs[k].a).Append(',').Append(pairs[k].b).Append(",\"").Append(pairs[k].state).Append("\"]");
                s.Append(']');
            }

            s.Append(",\"reason\":").Append(SpatialSplitCaptureSink.Json(reason)).Append('}');
            return s.ToString();
        }
    }

    public static unsafe partial class SpatialSplitMethodEval
    {
        /// <summary>The diagnosis of one saved input (<paramref name="mode"/> 1: the pairs; 2: with the protection). The table may be null: the input is then Invalid.</summary>
        internal static SpatialSplitPairDiagnosis DiagnosePair(SpatialSplitSample s, SpatialSplitBoneSample b, NpcPieceTopologySample table, int mode, bool detail)
        {
            var d = new SpatialSplitPairDiagnosis();
            int c = b.convexBones.Length, t = s.indices.Length / 3, n = b.nodeCount;
            d.convexes = c;
            var chunks = new NpcVertexChunk[b.chunkStarts.Length];
            var owned = new List<NativeArray<uint2>>();
            var baseBones = new NativeArray<ulong>(Math.Max(1, b.baseBones != null ? b.baseBones.Length : 0), Allocator.Persistent);
            var indices = new NativeArray<uint>(Math.Max(1, s.indices.Length), Allocator.Persistent);
            var bytes = new NativeArray<byte>(Math.Max(1, c + t), Allocator.Persistent);
            var attributes = new NpcBonePairAttributes();
            NpcBoneTopology topology = null;
            try
            {
                if (b.baseBones != null) NativeArray<ulong>.Copy(b.baseBones, baseBones, b.baseBones.Length);
                NativeArray<uint>.Copy(s.indices, indices, s.indices.Length);
                for (int k = 0; k < chunks.Length; k++)
                {
                    var origins = new NativeArray<uint2>(Math.Max(1, b.chunkOrigins[k].Length), Allocator.Persistent);
                    NativeArray<uint2>.Copy(b.chunkOrigins[k], origins, b.chunkOrigins[k].Length);
                    owned.Add(origins);
                    chunks[k] = new NpcVertexChunk { origins = (uint2*)origins.GetUnsafeReadOnlyPtr(), start = b.chunkStarts[k], count = b.chunkOrigins[k].Length, localBase = b.chunkLocalBases[k] };
                }

                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)indices.GetUnsafeReadOnlyPtr(), indexCount = s.indices.Length, convexCount = c, convexBones = b.convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = n, parent = b.parent, present = b.present, baseBones = b.baseBones != null ? (ulong*)baseBones.GetUnsafeReadOnlyPtr() : null,
                        baseStart = b.baseStart, baseCount = b.baseCount, chunks = chunks, localCount = b.localCount,
                    },
                    jointKept = b.jointKept, segmentLo = b.segmentLo, segmentHi = b.segmentHi, boneSides = new byte[2 * n],
                    insufficient = b.insufficient, maxTriangles = s.limits.maxTriangles, maxOriginSteps = b.maxOriginSteps, pairs = attributes,
                };
                int[] topologyIds = null;
                int topologyCount = 0;
                if (table != null)
                {
                    topologyIds = NpcPiecePickSet.SavedTopologyOfLocal(new NpcPiecePickSet.Pick { sample = s, bones = b, topology = table }, out topologyCount);
                    topology = NpcBoneTopology.OfLocal(in input.lineage, topologyIds, topologyCount);
                    input.topology = topology.Map;
                }

                NpcBoneSplitSearch.Decide(in input, (byte*)bytes.GetUnsafePtr(), c + t, ref d.bone);
                bool topologyKnown = table != null;
                d.bones = CutSpatialSplit.Attempt.BonesOf(in d.bone, attributes.filled, topologyKnown);
                if (d.bones == SpatialSplitPairBones.Invalid)
                {
                    d.verdict = "Invalid";
                    d.reason = !topologyKnown ? "the geometry's topology is not known (the bones: " + d.bone.verdict + ", " + (d.bone.reason ?? "") + ")" : "the bones' input is malformed: " + (d.bone.reason ?? "");
                    return d;
                }

                // the engine's compact input: the sample's own form
                int stride = s.vertexStructBytes, at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
                var local = new float3[s.vertexIds.Length];
                for (int k = 0; k < local.Length; k++)
                {
                    local[k] = new float3(BitConverter.ToSingle(s.vertexBytes, (k * stride) + at0), BitConverter.ToSingle(s.vertexBytes, (k * stride) + at0 + 4), BitConverter.ToSingle(s.vertexBytes, (k * stride) + at0 + 8));
                }

                var convexes = new float3[c][];
                for (int k = 0; k < c; k++) convexes[k] = s.convexes[k].vertices;
                var compact = new SpatialSplitGraph.CompactInput
                {
                    local = local, ids = s.vertexIds, indices = s.indices, convexes = convexes, vertexViewLength = s.vertexViewLength,
                    geometryToOwner = s.geometryToOwner, shapeToOwner = s.shapeToOwner, margin = s.margin,
                };
                SpatialSplitGraphLimits g = b.graphSearch ? b.graphLimits : SpatialSplitGraphLimits.Default;
                var counts = new SpatialSplitGraph.PairInput { protect = mode >= 2, relocate = mode == 4, useTopology = true, topology = input.topology, keepCandidates = true };
                bool known = d.bones == SpatialSplitPairBones.Known;
                // the own main convexes are read before A adds them to the protected ones
                var ownMain = new List<int>[known ? attributes.components : 0];
                for (int k = 0; k < ownMain.Length; k++)
                {
                    ownMain[k] = new List<int>();
                    for (int q = 0; q < c; q++) if (attributes.IsOwnMain(k, q)) ownMain[k].Add(q);
                }

                d.meshOwnMain = ownMain;
                if (known && mode == 3) attributes.ApplyOwnMain(out d.ownProtectedAdded, out d.ownUnderRatio);
                d.counts = counts;
                if (known)
                {
                    counts.convexWords = attributes.convexWords;
                    counts.rows = attributes.Rows; counts.nodes = attributes.Nodes; counts.clusterWords = attributes.clusterWords; counts.rowWords = attributes.rowWords; counts.triComponent = attributes.TriComponent;
                }

                var settings = new SpatialSplitGraph.GraphSettings
                {
                    secondsLimit = 0.0, measure = 0, nearestSelection = true, flowDinic = true, pairsSweep = false,
                    maxTriangles = g.maxTriangles, maxConvexes = g.maxConvexes, maxVertexSpan = g.maxVertexSpan, maxNodes = g.maxNodes, maxCandidates = g.maxCandidates,
                    maxGraphBytes = g.maxGraphBytes, workBudget = g.workBudget, pair = counts,
                };
                var snapshot = new SpatialSplitGraph.PrepareSnapshot();
                SpatialSplitGraph.GraphOutcome o = SpatialSplitGraph.DecideCompact(compact, settings, true, out SpatialSplitGraph.NativeRun _, snapshot);
                d.reason = o.reason ?? "";
                switch (o.verdict)
                {
                    case SpatialSplitGraph.GraphVerdict.Separable: d.verdict = "Separable"; break;
                    case SpatialSplitGraph.GraphVerdict.NotSeparable: d.verdict = "NotSeparable"; break;
                    case SpatialSplitGraph.GraphVerdict.NotFound:
                        if (o.cutLimitHit) { d.verdict = "Unfinished"; d.stop = "CutAttempts"; }
                        else if (o.gjkLimitHit > 0) { d.verdict = "Unfinished"; d.stop = "GjkIterations"; }
                        else d.verdict = "NotFound";
                        break;
                    case SpatialSplitGraph.GraphVerdict.InvalidInput: d.verdict = "Invalid"; break;
                    default: d.verdict = "Unfinished"; d.stop = o.stop ?? ""; break;
                }

                d.meshNodes = o.meshComponents;
                d.clusters = attributes.clusters;
                if (o.verdict == SpatialSplitGraph.GraphVerdict.Separable)
                {
                    var cs = new byte[c];
                    var ts = new byte[t];
                    for (int k = 0; k < c; k++) cs[k] = o.convexPositive[k] ? (byte)1 : (byte)0;
                    for (int k = 0; k < t; k++) ts[k] = o.trianglePositive[k] ? (byte)1 : (byte)0;
                    d.partition = PartitionHash(cs, ts);
                }

                if (!detail || !counts.ran || snapshot.triComponent == null) return d;

                // ---- the nodes and the pairs, in full
                int m = o.meshComponents, nodes = c + m;
                d.nodeSide = counts.keptNodeSides;
                d.clusterOfBone = known ? (int[])attributes.clusterOfBone.Clone() : new int[0];
                d.convexBone = (int[])b.convexBones.Clone();
                d.convexCluster = new int[c];
                for (int k = 0; k < c; k++) d.convexCluster[k] = known && (uint)b.convexBones[k] < (uint)n ? attributes.clusterOfBone[b.convexBones[k]] : -1;
                d.meshTriangles = new int[m];
                d.meshLeastVertex = new uint[m];
                d.meshClusters = new List<int>[m];
                d.meshProtected = new List<int>[m];
                d.meshSupport = new Dictionary<int, int>[m];
                var seen = new HashSet<uint>[m];
                for (int k = 0; k < m; k++)
                {
                    d.meshLeastVertex[k] = uint.MaxValue;
                    d.meshClusters[k] = new List<int>();
                    d.meshProtected[k] = new List<int>();
                    d.meshSupport[k] = new Dictionary<int, int>();
                    seen[k] = new HashSet<uint>();
                    if (!known) continue;
                    for (int q = 0; q < attributes.clusters; q++) if (attributes.HasCluster(c + k, q)) d.meshClusters[k].Add(q);
                    for (int q = 0; q < c; q++) if (attributes.IsProtected(k, q)) d.meshProtected[k].Add(q);
                }

                var convexOfBone = new int[n];
                for (int k = 0; k < n; k++) convexOfBone[k] = -1;
                for (int k = 0; k < c; k++) if ((uint)b.convexBones[k] < (uint)n) convexOfBone[b.convexBones[k]] = k;
                var memo = new Dictionary<int, HashSet<int>>();
                for (int k = 0; k < t; k++)
                {
                    int node = snapshot.triComponent[k];
                    d.meshTriangles[node]++;
                    for (int q = 0; q < 3; q++)
                    {
                        uint id = s.indices[(3 * k) + q];
                        if (id < d.meshLeastVertex[node]) d.meshLeastVertex[node] = id;
                        if (!seen[node].Add(id)) continue;
                        foreach (int bone in SpatialSplitPairAnalysis.BonesOf(b, NpcPiecePickSet.LocalOf(b, id), memo))
                        {
                            if ((uint)bone >= (uint)n || convexOfBone[bone] < 0) continue;
                            d.meshSupport[node][bone] = d.meshSupport[node].TryGetValue(bone, out int had) ? had + 1 : 1;
                        }
                    }
                }

                var ended = new Dictionary<long, string>();
                for (int k = 0; k < counts.keptCandidates.Length; k++)
                {
                    int2 p = counts.keptCandidates[k];
                    byte e = counts.keptEnds[k];
                    bool guarded = mode >= 2 && known && p.x < c && p.y >= c && attributes.IsProtected(p.y - c, p.x);
                    ended[((long)p.x << 32) | (uint)p.y] = e == 0 ? "hulls" : guarded ? "protected" : e == 2 ? (counts.keptTried[k] != 0 ? "unsettled" : "blocked") : "left";
                }

                for (int i = 0; i < nodes; i++)
                {
                    for (int j = i + 1; j < nodes; j++)
                    {
                        bool shared = !known;
                        for (int q = 0; known && q < attributes.clusters && !shared; q++) shared = attributes.HasCluster(i, q) && attributes.HasCluster(j, q);
                        // a candidate's own end first: a protected pair is one whatever the rows' clusters
                        string state = ended.TryGetValue(((long)i << 32) | (uint)j, out string e) ? e : !shared ? "bones" : "boxes";
                        d.pairs.Add((i, j, state));
                    }
                }

                return d;
            }
            finally
            {
                attributes.Dispose();
                topology?.Dispose();
                foreach (NativeArray<uint2> o in owned) o.Dispose();
                baseBones.Dispose();
                indices.Dispose();
                bytes.Dispose();
            }
        }
    }
}
#endif
