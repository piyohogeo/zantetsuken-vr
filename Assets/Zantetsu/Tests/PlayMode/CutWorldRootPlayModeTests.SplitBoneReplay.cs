using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// NPC-BONE-FALLBACK-1 review 2: a reproduction from saved data (not a product path, not a regression case of its own: it reads a
    /// capture on this machine and is ignored where the capture is not there). NPC-BONE-LINK-2's input 291 -- the swimmer (c_25) after
    /// its first cut took the head off -- is made a world body again from its sample and its bones' sidecar: its vertices in the lineage's
    /// own numbering (the base model's, then the first cut's new ones), its triangles, its convexes with their bones, the lineage with
    /// the first cut's origins (the piece is the whole rig: what the first cut took off lies above this case's plane). It is then cut
    /// below the elbows (y 1.05 in the geometry's frame, y up) and the lower side -- the hips, the legs, the forearms below the plane and
    /// the hands -- is followed: the bones' clusters, the mesh groups, the convexes' placement and how the additional split ends.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string Replay291Session = @"C:\log\zantetsuken-vr\walk-city-2026-10-03\perf\npc-bone-split\link\run-2\capture\split-capture-20261104-082452-3430972d";
        private const string Replay291Name = "w001-a0000330";

        /// <summary>The bones a vertex of <paramref name="lineage"/> is weighted to, walked back to the base (an auxiliary cap vertex: none); whether a cut's vertex was on the way.</summary>
        private static HashSet<int> ReplayBonesOf(NpcBoneLineage lineage, uint vertex, out bool cut)
        {
            var bones = new HashSet<int>();
            cut = false;
            var stack = new Stack<uint>();
            stack.Push(vertex);
            int steps = 0;
            while (stack.Count > 0 && steps++ < 1 << 16)
            {
                uint x = stack.Pop();
                uint f = x - lineage.BaseStart;
                if (f < (uint)lineage.BaseCount)
                {
                    ulong e = lineage.Vertices.Pointer[f];
                    for (int q = 0; q < 4; q++)
                    {
                        int b = (int)((e >> (16 * q)) & 0xFFFF);
                        if (b != NpcVertexBones.None) bones.Add(b);
                    }

                    continue;
                }

                cut = true;
                foreach (NpcVertexChunk chunk in lineage.Chunks)
                {
                    uint a = x - chunk.start;
                    if (a >= (uint)chunk.count) continue;
                    uint2 o = chunk.origins[a];
                    if (o.x == MeshCutOutput.NoVertexOrigin) break;
                    stack.Push(o.x);
                    if (o.y != o.x) stack.Push(o.y);
                    break;
                }
            }

            return bones;
        }

        private static NpcBoneSplitRecord ReplayDecide(NpcBoneLineage lineage, NpcBonePiece piece, PhysicsOwnerShape shape, List<(uint, uint, uint)> triangles)
        {
            var flat = new uint[triangles.Count * 3];
            for (int k = 0; k < triangles.Count; k++) { flat[3 * k] = triangles[k].Item1; flat[3 * k + 1] = triangles[k].Item2; flat[3 * k + 2] = triangles[k].Item3; }
            int c = shape.ConvexCount;
            var convexBones = new int[c];
            for (int i = 0; i < c; i++) convexBones[i] = shape.ConvexBone(i);
            var native = new NativeArray<uint>(flat, Allocator.Temp);
            var bytes = new NativeArray<byte>(c + triangles.Count, Allocator.Temp);
            try
            {
                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)native.GetUnsafePtr(), indexCount = flat.Length, convexCount = c, convexBones = convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = lineage.NodeCount, parent = lineage.Parent, present = lineage.Present, baseBones = lineage.Vertices.Pointer,
                        baseStart = lineage.BaseStart, baseCount = lineage.BaseCount, chunks = lineage.Chunks, localCount = lineage.LocalCount,
                    },
                    jointKept = piece.JointKept, segmentLo = piece.Lo, segmentHi = piece.Hi, boneSides = new byte[2 * lineage.NodeCount],
                    insufficient = lineage.Insufficient, maxTriangles = 1 << 20, maxOriginSteps = NpcBoneSplitSearch.DefaultMaxOriginSteps,
                };
                var record = default(NpcBoneSplitRecord);
                NpcBoneSplitSearch.Decide(in input, (byte*)bytes.GetUnsafePtr(), bytes.Length, ref record);
                return record;
            }
            finally
            {
                native.Dispose();
                bytes.Dispose();
            }
        }

        private static string ReplayDescribe(in NpcBoneSplitRecord r) =>
            r.verdict + " (" + r.reason + "): bones held " + r.pieceBones + ", clusters " + r.clusters + ", mesh components " + r.meshComponents + ", groups " + r.groups
            + " (" + r.fullGroups + " with a convex and triangles, " + r.unboundGroups + " mesh alone), weights on bones not held " + r.removedWeights
            + ", convexes +" + r.positiveConvexes + " -" + r.negativeConvexes + ", triangles +" + r.positiveTriangles + " -" + r.negativeTriangles;

        /// <summary>The held rule's clusters, the convexes in each, and every mesh component reaching two clusters by its vertices' weights (the test's own count).</summary>
        private static string ReplayExplain(NpcBoneLineage lineage, NpcBonePiece piece, PhysicsOwnerShape shape, List<(uint, uint, uint)> triangles)
        {
            int n = lineage.NodeCount;
            var convexOf = new HashSet<int>();
            for (int i = 0; i < shape.ConvexCount; i++) convexOf.Add(shape.ConvexBone(i));
            var held = new HashSet<int>();
            for (int k = 0; k < n; k++) if (lineage.Present[k] && (convexOf.Contains(k) || piece.IsJointKept(k))) held.Add(k);
            var up = new Dictionary<int, int>();
            foreach (int k in held) up[k] = k;
            int Find(int a) { while (up[a] != a) { up[a] = up[up[a]]; a = up[a]; } return a; }
            for (int s = 0; s < n; s++)
            {
                int p = lineage.Parent[s];
                if (p >= 0 && held.Contains(s) && held.Contains(p) && piece.Lo[s] <= piece.Hi[s]) { int a = Find(s), b = Find(p); if (a != b) up[a] = b; }
            }

            var text = new StringBuilder();
            var clusters = held.GroupBy(Find).ToList();
            text.Append("clusters ").Append(clusters.Count).Append(":\n");
            foreach (IGrouping<int, int> g in clusters)
            {
                text.Append("  [").Append(string.Join(",", g.OrderBy(k => k).Select(k => k + (convexOf.Contains(k) ? "c" : "j")))).Append("] convexes:");
                for (int i = 0; i < shape.ConvexCount; i++)
                {
                    if (held.Contains(shape.ConvexBone(i)) && Find(shape.ConvexBone(i)) == g.Key)
                    {
                        shape.ConvexBounds(i, out float3 lo, out float3 hi);
                        text.Append(' ').Append(shape.ConvexBone(i)).Append("(y ").Append(lo.y.ToString("F3")).Append("..").Append(hi.y.ToString("F3")).Append(')');
                    }
                }

                text.Append('\n');
            }

            var mesh = new Dictionary<uint, uint>();
            uint MFind(uint a) { if (!mesh.ContainsKey(a)) mesh[a] = a; while (mesh[a] != a) { mesh[a] = mesh[mesh[a]]; a = mesh[a]; } return a; }
            void MUnion(uint a, uint b) { uint x = MFind(a), y = MFind(b); if (x != y) mesh[x] = y; }
            foreach ((uint a, uint b, uint c) in triangles) { MUnion(a, b); MUnion(a, c); }
            var reach = new Dictionary<uint, HashSet<int>>();
            var bridges = new Dictionary<uint, List<string>>();
            var counts = new Dictionary<uint, int[]>();
            foreach (uint v in triangles.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).Distinct())
            {
                HashSet<int> bones = ReplayBonesOf(lineage, v, out bool cut);
                var cl = new HashSet<int>(bones.Where(held.Contains).Select(Find));
                uint r = MFind(v);
                if (!reach.ContainsKey(r)) { reach[r] = new HashSet<int>(); bridges[r] = new List<string>(); counts[r] = new int[2]; }
                reach[r].UnionWith(cl);
                counts[r][cut ? 1 : 0]++;
                if (cl.Count >= 2) bridges[r].Add((cut ? "cut " : "base ") + "[" + string.Join(",", bones.Where(held.Contains).OrderBy(b => b)) + "]");
            }

            var tris = triangles.GroupBy(t => MFind(t.Item1)).ToDictionary(g => g.Key, g => g.Count());
            text.Append("mesh components ").Append(reach.Count).Append("; reaching two clusters or more:\n");
            foreach (KeyValuePair<uint, HashSet<int>> e in reach.Where(e => e.Value.Count >= 2))
            {
                text.Append("  ").Append(tris[e.Key]).Append(" triangles (").Append(counts[e.Key][0]).Append(" base, ").Append(counts[e.Key][1]).Append(" cut vertices), clusters of ")
                    .Append(string.Join(" | ", e.Value.Select(r => string.Join(",", clusters.First(g => g.Key == r).OrderBy(k => k).Take(4)))))
                    .Append("; vertices reaching two: ").Append(string.Join(", ", bridges[e.Key].GroupBy(s => s).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key + " x" + g.Count())))
                    .Append('\n');
            }

            return text.ToString();
        }

        /// <summary>Input 291 made a world body again (the steps 1 to 3 of the case below; no frame passes in it).</summary>
        private void BuildReplay291(CutWorldRoot root, SpatialSplitSample sample, SpatialSplitBoneSample saved, out LogicalFragmentId fragment, out VpStoredGeometry geometry,
            out PhysicsOwnerShape shape, out NpcBoneLineage lineage, out Mesh[] meshes)
        {
            int size = UnsafeUtility.SizeOf<VpRenderVertex>();
            // 1. The vertices in the lineage's own numbering: the base model's, then the first cut's new ones.
            int localCount = saved.localCount;
            int LocalOf(uint id)
            {
                uint f = id - saved.baseStart;
                if (f < (uint)saved.baseCount) return (int)f;
                for (int c = 0; c < saved.chunkStarts.Length; c++)
                {
                    uint a = id - saved.chunkStarts[c];
                    if (a < (uint)saved.chunkOrigins[c].Length) return saved.chunkLocalBases[c] + (int)a;
                }

                return -1;
            }

            var vertices = new VpRenderVertex[localCount];
            var named = new bool[localCount];
            for (int k = 0; k < sample.vertexIds.Length; k++)
            {
                int local = LocalOf(sample.vertexIds[k]);
                Assert.That(local, Is.GreaterThanOrEqualTo(0), "a named vertex of the lineage");
                fixed (byte* src = sample.vertexBytes) UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref vertices[local]), src + ((long)k * size), size);
                named[local] = true;
            }

            int filler = Array.IndexOf(named, true);
            var topology = new int[localCount];
            var byPosition = new Dictionary<float3, int>();
            int topologyCount = 0;
            for (int v = 0; v < localCount; v++)
            {
                if (!named[v]) { vertices[v] = vertices[filler]; topology[v] = topologyCount++; continue; }
                float3 p = vertices[v].position;
                if (!byPosition.TryGetValue(p, out int t)) { t = topologyCount++; byPosition[p] = t; }
                topology[v] = t;
            }

            var indices = new uint[sample.indices.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = (uint)LocalOf(sample.indices[i]);
            var submeshes = new VpGeometrySubmesh[sample.submeshOffsets.Length];
            for (int s = 0; s < submeshes.Length; s++) submeshes[s] = new VpGeometrySubmesh(sample.submeshOffsets[s], sample.submeshCounts[s], SideMaterial);

            bool appended = root.Storage.TryAppendCuttable(vertices, indices, topology, topologyCount, submeshes, out geometry, out VpCutInputVerdict verdict);
            Assert.That(appended, Is.True, "the geometry is taken as a cut input: " + verdict);

            // 2. The shape: the sample's convexes, a cooked mesh each, in the shape's frame (the owner's: identity in this sample).
            int cc = sample.convexes.Length;
            meshes = new Mesh[cc];
            var ranges = new ConvexBrepRange[cc];
            int vertexTotal = 0, offsetTotal = 0, faceIndexTotal = 0, edgeTotal = 0;
            foreach (SpatialSplitSample.Convex x in sample.convexes) { vertexTotal += x.range.vertexCount; offsetTotal += x.range.faceCount + 1; faceIndexTotal += x.range.faceIndexCount; edgeTotal += x.range.edgeCount; }
            var vBank = new NativeArray<float3>(Math.Max(1, vertexTotal), Allocator.Temp);
            var oBank = new NativeArray<int>(Math.Max(1, offsetTotal), Allocator.Temp);
            var iBank = new NativeArray<int>(Math.Max(1, faceIndexTotal), Allocator.Temp);
            var fBank = new NativeArray<int>(Math.Max(1, faceIndexTotal), Allocator.Temp);
            var eBank = new NativeArray<BrepEdge>(Math.Max(1, edgeTotal), Allocator.Temp);
            try
            {
                int vv = 0, oo = 0, ii = 0, ee = 0;
                for (int c = 0; c < cc; c++)
                {
                    SpatialSplitSample.Convex x = sample.convexes[c];
                    ConvexBrepRange r = x.range;
                    ranges[c] = new ConvexBrepRange
                    {
                        vertexBase = vv, vertexCount = r.vertexCount, faceBase = oo, faceCount = r.faceCount,
                        faceIndexBase = ii, faceIndexCount = r.faceIndexCount, edgeBase = ee, edgeCount = r.edgeCount, maxFaceLoop = r.maxFaceLoop,
                    };
                    for (int k = 0; k < r.vertexCount; k++) vBank[vv + k] = x.vertices[k];
                    for (int k = 0; k <= r.faceCount; k++) oBank[oo + k] = x.faceOffsets[k];
                    for (int k = 0; k < r.faceIndexCount; k++) { iBank[ii + k] = x.faceIndices[k]; fBank[ii + k] = x.faceEdges[k]; }
                    for (int k = 0; k < r.edgeCount; k++) eBank[ee + k] = x.edges[k];
                    var tri = new List<int>();
                    for (int f = 0; f < r.faceCount; f++)
                    {
                        int a = x.faceOffsets[f], b = x.faceOffsets[f + 1];
                        for (int k = a + 1; k + 1 < b; k++) { tri.Add(x.faceIndices[a]); tri.Add(x.faceIndices[k]); tri.Add(x.faceIndices[k + 1]); }
                    }

                    meshes[c] = new Mesh { name = "Replay 291 convex " + c, hideFlags = HideFlags.HideAndDontSave };
                    meshes[c].SetVertices(x.vertices.Select(p => (Vector3)p).ToList());
                    meshes[c].SetTriangles(tri, 0);
                    meshes[c].RecalculateBounds();
                    vv += r.vertexCount; oo += r.faceCount + 1; ii += r.faceIndexCount; ee += r.edgeCount;
                }

                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)vBank.GetUnsafePtr(), faceOffsets = (int*)oBank.GetUnsafePtr(), faceIndices = (int*)iBank.GetUnsafePtr(),
                    faceEdges = (int*)fBank.GetUnsafePtr(), edges = (BrepEdge*)eBank.GetUnsafePtr(),
                };
                shape = PhysicsOwnerShape.Authored(bank, ranges, meshes, PhysicsShapeSource.External(), sample.shapeToOwner);
            }
            finally
            {
                vBank.Dispose(); oBank.Dispose(); iBank.Dispose(); fBank.Dispose(); eBank.Dispose();
            }

            _disposables.Add(shape);
            var actor = TrackActor(new GameObject("Replay 291"));
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < cc; c++)
            {
                MeshCollider collider = actor.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = meshes[c];
            }

            Assert.That(root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, (Matrix4x4)sample.geometryToOwner, null, false, out fragment), Is.True, "the body was taken into the world");
            _registered.Add(actor);

            // 3. The NPC's records: the convexes' bones, the lineage (the model's weights, the first cut's origins in this geometry's numbers).
            shape.SetConvexBones(saved.convexBones);
            NpcVertexBones table = NpcVertexBones.FromEntries(saved.baseBones, 4);
            lineage = new NpcBoneLineage(saved.nodeCount, saved.parent, saved.present, saved.joints, table, (uint)geometry.vertexStart, saved.baseCount, saved.insufficient);
            table.Release();   // the lineage's hold is the one left
            int vertexStart = geometry.vertexStart;
            for (int c = 0; c < saved.chunkStarts.Length; c++)
            {
                uint2[] o = saved.chunkOrigins[c];
                var origins = new NativeArray<uint2>(Math.Max(1, o.Length), Allocator.Persistent);
                for (int k = 0; k < o.Length; k++)
                {
                    uint X(uint id) => id == MeshCutOutput.NoVertexOrigin ? id : (uint)(vertexStart + LocalOf(id));
                    origins[k] = new uint2(X(o[k].x), X(o[k].y));
                }

                Assert.That(saved.chunkLocalBases[c], Is.EqualTo(lineage.LocalCount), "the chunks in their order");
                lineage.AddChunk(origins, (uint)(geometry.vertexStart + saved.chunkLocalBases[c]), o.Length);
            }

            root.NpcBones.RegisterRoot(fragment, lineage);

        }

        [UnityTest]
        public IEnumerator SplitBone_Replay_TheSwimmersInput291_CutBelowTheElbows_WhereTheLowerSidesSplitEnds()
        {
            string samplePath = Path.Combine(Replay291Session, Replay291Name + ".zss.gz"), bonesPath = Path.Combine(Replay291Session, Replay291Name + ".zsb.gz");
            if (!File.Exists(samplePath) || !File.Exists(bonesPath)) Assert.Ignore("the capture is not on this machine: " + Replay291Session);
            SpatialSplitSample sample = SpatialSplitSample.Read(samplePath);
            SpatialSplitBoneSample saved = SpatialSplitBoneSample.Read(bonesPath);
            Assert.That(sample.sha256, Is.EqualTo("18609811a0146db1f15d6a23f3d82a285f323dbaa9b140cfd4fb3c6db5059621"), "the listed sample");
            Assert.That(saved.sha256, Is.EqualTo("95f4b78392db38afefd8ce362aeb4002469a941d2a7964574b55a1bde765af5f"), "the listed bones");
            Assert.That(sample.vertexStructBytes, Is.EqualTo(UnsafeUtility.SizeOf<VpRenderVertex>()));

            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            BuildReplay291(root, sample, saved, out LogicalFragmentId fragment, out VpStoredGeometry geometry, out PhysicsOwnerShape shape, out NpcBoneLineage lineage, out Mesh[] meshes);
            Assert.That(root.NpcBones.TryGet(fragment, out NpcBoneFragment whole), Is.True);

            // The reconstruction against the saved decision of input 291 (mesh components 10, groups 1): the same triangles and weights.
            List<(uint, uint, uint)> before = TrianglesOf(root, geometry);
            NpcBoneSplitRecord first = ReplayDecide(lineage, whole.Piece, shape, before);
            TestContext.Out.WriteLine("REPLAY 291 before the cut (the whole rig's piece; saved: NotSeparable, clusters 1, mesh components 10, groups 1): " + ReplayDescribe(first));
            Assert.That(first.meshComponents == 10 && first.groups == 1 && before.Count == 2674, Is.True, "the reconstruction's triangles and weights: " + ReplayDescribe(first));
            yield return null;

            // 4. The cut below the elbows: y 1.05 (the owner's frame is the geometry's here).
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, fragment, new float4(0f, 1f, 0f, -1.05f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            LogicalFragmentId lower = pieces[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment lowerRecord) && root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner)
                        && root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True, "the lower side is the NPC's");
            root.NpcBones.TryGet(lower, out lowerRecord);
            root.Owners.TryGet(lower, out lowerOwner);
            root.Geometry.TryGetGeometry(lower, out lowerGeometry);
            Assert.That(lowerRecord.Piece.IsJointKept(0) && !lowerRecord.Piece.IsJointKept(2) && !lowerRecord.Piece.IsJointKept(10) && lowerRecord.Piece.IsJointKept(12), Is.True,
                "the layout: the hips and the wrists below, the second spine bone and the left elbow above");
            List<(uint, uint, uint)> lowerTriangles = TrianglesOf(root, lowerGeometry);
            NpcBoneSplitRecord decided = ReplayDecide(lineage, lowerRecord.Piece, lowerOwner.Shape, lowerTriangles);
            TestContext.Out.WriteLine("REPLAY 291 the lower side: convexes " + lowerOwner.Shape.ConvexCount + ", triangles " + lowerTriangles.Count + "; the decision: " + ReplayDescribe(decided));
            TestContext.Out.WriteLine("REPLAY 291 the lower side, the test's own count:\n" + ReplayExplain(lineage, lowerRecord.Piece, lowerOwner.Shape, lowerTriangles));

            // 5. The world's additional split of the lower side (the fallback off: the bones alone), followed to its end.
            split.BoneFallback = false;
            remaining[0] = 1.0;
            LogicalFragmentId current = default;
            int searches = split.BoneSearches;
            string lowerEnd = null;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (lowerEnd == null && Time.realtimeSinceStartup < deadline)
            {
                if (split.HasAttempt) current = split.AttemptFragment;
                if (split.BoneSearches > searches)
                {
                    searches = split.BoneSearches;
                    if (current == lower) lowerEnd = split.LastEnd + "; the record: " + ReplayDescribe(split.LastBone) + "; last refusal: " + split.LastRefusal;
                }

                if (split.LastSource == lower && lowerEnd == null) lowerEnd = "Published (by the bones " + split.PublishedBone + ")";
                yield return null;
            }

            remaining[0] = 0.0;
            TestContext.Out.WriteLine("REPLAY 291 the lower side's additional split: " + (lowerEnd ?? "NOT DECIDED within 30 s"));
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            if (split.LastSource == lower)
            {
                foreach (LogicalFragmentId child in new[] { split.LastPositive, split.LastNegative })
                {
                    root.Owners.TryGet(child, out PhysicsFragmentOwner o);
                    var bones = new List<int>();
                    for (int c = 0; c < o.Shape.ConvexCount; c++) bones.Add(o.Shape.ConvexBone(c));
                    TestContext.Out.WriteLine("REPLAY 291 published side: convexes' bones " + string.Join(",", bones));
                }
            }

            Assert.That(lowerEnd, Is.Not.Null, "the lower side was decided");
            foreach (Mesh m in meshes) if (m != null) UnityEngine.Object.Destroy(m);
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
