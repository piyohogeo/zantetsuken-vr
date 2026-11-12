using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// NPC-PIECE-PICK-1: the piece collection's own parts on a hand-made piece -- a pick written through the capture's sink (the sample, the
    /// bones, the pick's record and the model's table) and read back whole, the bone-aware decision over what was read the same as over the
    /// live parts (verdict, counts, every side), the model's weights kept in the collection's mode and read back bit for bit, and the
    /// reader telling a missing file, a hash that is not the listed one, a sample of another geometry than the bones describe, and a pick
    /// that failed at the time. (The copy of a live fragment is the PlayMode case's.)
    /// </summary>
    public unsafe class NpcPiecePickTests
    {
        private const uint BaseStart = 100;
        private string _root;
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<NpcBoneLineage> _lineages = new List<NpcBoneLineage>();
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zantetsu-piece-pick-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _models.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (NpcBoneLineage lineage in _lineages) while (!lineage.IsReleased) lineage.Release();
            _lineages.Clear();
            for (int i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
            _disposables.Clear();
            foreach (UnityEngine.Object o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private sealed class Disposer : IDisposable
        {
            internal Action action;
            public void Dispose() => action();
        }

        // ---- the piece: the two-arm rig of NpcBoneSplitSearchTests below the waist, with one cut's vertices ------------------------------

        private static readonly int[] TwoArms = { -1, 0, 1, 2, 3, 1, 5, 6, 7 };
        private static readonly float3[] TwoArmJoints =
        {
            new float3(0f, 1.0f, 0f), new float3(0f, 1.3f, 0f),
            new float3(-0.2f, 1.4f, 0f), new float3(-0.25f, 1.1f, 0f), new float3(-0.25f, 0.8f, 0f),
            new float3(0.2f, 1.4f, 0f), new float3(0.25f, 1.1f, 0f), new float3(0.25f, 0.8f, 0f), new float3(0.25f, 0.7f, 0f),
        };
        private static readonly float4 BodyWaist = new float4(0f, 1f, 0f, -1.05f);
        private static readonly int[] ConvexBones = { 0, 4, 7 };

        private static ulong W(params int[] bones)
        {
            var b = new ushort[] { NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None };
            for (int i = 0; i < bones.Length; i++) b[i] = (ushort)bones[i];
            return NpcVertexBones.Pack(b[0], b[1], b[2], b[3]);
        }

        /// <summary>0-11 the lower body on the pelvis, 12-14 the left hand's skin, 15-20 the right hand's, 21-23 the trunk's on the spine; vertex 3 on two bones.</summary>
        private static ulong[] Weights()
        {
            ulong[] w =
            {
                W(0), W(0), W(0), W(0, 1), W(0), W(0), W(0), W(0), W(0), W(0), W(0), W(0),
                W(4), W(4), W(4), W(7), W(7), W(7), W(7), W(7), W(7), W(1), W(1), W(1),
            };
            return w;
        }

        /// <summary>The weights as floats in the bones' places: odd values whose bits a text would not keep.</summary>
        private static float[] WeightValues(ulong[] bones)
        {
            var values = new float[4 * bones.Length];
            var random = new System.Random(11);
            for (int v = 0; v < bones.Length; v++)
            {
                for (int q = 0; q < 4; q++)
                {
                    if (((bones[v] >> (16 * q)) & 0xFFFF) == NpcVertexBones.None) continue;
                    values[(4 * v) + q] = q == 0 ? 0.3333333f + (0.001f * (float)random.NextDouble()) : 1e-7f * (1 + v);
                }
            }

            values[4 * 3] = 0.1f;   // vertex 3's two bones: 0.1 and a tiny one, as a mesh may hold them (nothing says they sum to 1 here)
            return values;
        }

        private NpcVertexBones Table(ulong[] weights, float[] values)
        {
            NpcVertexBones table = NpcVertexBones.FromEntries(weights, 16, values, "pick test model");
            _disposables.Add(new Disposer { action = () => { while (!table.IsReleased) table.Release(); } });
            return table;
        }

        private NpcBoneLineage Lineage(NpcVertexBones table, int baseCount)
        {
            var present = new bool[TwoArms.Length];
            for (int i = 0; i < present.Length; i++) present[i] = true;
            var lineage = new NpcBoneLineage(TwoArms.Length, TwoArms, present, TwoArmJoints, table, BaseStart, baseCount, null);
            lineage.Hold();
            _lineages.Add(lineage);
            return lineage;
        }

        /// <summary>The cut's new vertices at 200: 200 between base 0 and 1 (the pelvis), 201 an auxiliary cap vertex, 202 between 12 and 13 (the left hand).</summary>
        private static void AddCutChunk(NpcBoneLineage lineage)
        {
            var origins = new NativeArray<uint2>(new[]
            {
                new uint2(BaseStart + 0, BaseStart + 1), new uint2(MeshCutOutput.NoVertexOrigin, MeshCutOutput.NoVertexOrigin), new uint2(BaseStart + 12, BaseStart + 13),
            }, Allocator.Persistent);
            lineage.AddChunk(origins, 200, 3);   // disposes the given array
        }

        /// <summary>The piece's triangles (storage ids): the lower body's four, the left hand's one (with the cut's vertex 202), the right hand's two, the trunk's one, and the cap's (0, 200, 201).</summary>
        private static uint[] Indices()
        {
            var corners = new List<uint>();
            void T(uint a, uint b, uint c) { corners.Add(a); corners.Add(b); corners.Add(c); }
            uint B(int i) => BaseStart + (uint)i;
            T(B(0), B(1), B(2)); T(B(3), B(4), B(5)); T(B(6), B(7), B(8)); T(B(9), B(10), B(11));
            T(B(12), 202, B(14));
            T(B(15), B(16), B(17)); T(B(18), B(19), B(20));
            T(B(21), B(22), B(23));
            T(B(0), 200, 201);
            return corners.ToArray();
        }

        private NpcBoneSplitRecord DecideLive(NpcBoneLineage lineage, NpcBonePiece piece, uint[] indices, out byte[] sides, out byte[] boneSides)
        {
            var native = new NativeArray<uint>(indices, Allocator.Temp);
            int c = ConvexBones.Length, t = indices.Length / 3;
            var bytes = new NativeArray<byte>(c + t, Allocator.Temp);
            boneSides = new byte[2 * lineage.NodeCount];
            try
            {
                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)native.GetUnsafeReadOnlyPtr(), indexCount = indices.Length, convexCount = c, convexBones = ConvexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = lineage.NodeCount, parent = lineage.Parent, present = lineage.Present, baseBones = lineage.Vertices.Pointer,
                        baseStart = lineage.BaseStart, baseCount = lineage.BaseCount, chunks = lineage.Chunks, localCount = lineage.LocalCount,
                    },
                    jointKept = piece.JointKept, segmentLo = piece.Lo, segmentHi = piece.Hi, boneSides = boneSides,
                    insufficient = lineage.Insufficient, maxTriangles = 131072, maxOriginSteps = NpcBoneSplitSearch.DefaultMaxOriginSteps,
                };
                var record = default(NpcBoneSplitRecord);
                // NPC-BONE-TOPOLOGY-1: each render vertex its own topology vertex (the fixture has no attribute seam).
                using (NpcBoneTopology topology = NpcBoneTopology.OfLocal(in input.lineage, null, 0))
                {
                    input.topology = topology.Map;
                    NpcBoneSplitSearch.Decide(in input, (byte*)bytes.GetUnsafePtr(), bytes.Length, ref record);
                }
                sides = bytes.ToArray();
                return record;
            }
            finally
            {
                native.Dispose();
                bytes.Dispose();
            }
        }

        // ---- the shape: three boxes, a convex each (the capture tests' way), with their bones ------------------------------------------------

        private static readonly int[][] BoxFaces =
        {
            new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 },
        };

        private PhysicsOwnerShape BoxesShape(float3[] lows, float3[] highs)
        {
            var vertices = new List<float3>();
            var offsets = new List<int>();
            var faceIndices = new List<int>();
            var faceEdges = new List<int>();
            var edges = new List<BrepEdge>();
            var ranges = new ConvexBrepRange[lows.Length];
            var lo = new float3(float.PositiveInfinity);
            var hi = new float3(float.NegativeInfinity);
            for (int c = 0; c < lows.Length; c++)
            {
                ranges[c] = new ConvexBrepRange
                {
                    vertexBase = vertices.Count, vertexCount = 8, faceBase = offsets.Count, faceCount = 6,
                    faceIndexBase = faceIndices.Count, faceIndexCount = 24, edgeBase = edges.Count, edgeCount = 12, maxFaceLoop = 4,
                };
                for (int i = 0; i < 8; i++)
                {
                    var p = new float3((i & 1) != 0 ? highs[c].x : lows[c].x, (i & 2) != 0 ? highs[c].y : lows[c].y, (i & 4) != 0 ? highs[c].z : lows[c].z);
                    vertices.Add(p);
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }

                var local = new Dictionary<(int, int), int>();
                var localEdges = new List<BrepEdge>();
                for (int f = 0; f < 6; f++)
                {
                    offsets.Add(4 * f);
                    for (int k = 0; k < 4; k++)
                    {
                        int a = BoxFaces[f][k], b = BoxFaces[f][(k + 1) % 4];
                        var key = (math.min(a, b), math.max(a, b));
                        if (!local.TryGetValue(key, out int e))
                        {
                            e = localEdges.Count;
                            local[key] = e;
                            localEdges.Add(new BrepEdge { v0 = key.Item1, v1 = key.Item2, f0 = -1, f1 = -1 });
                        }

                        BrepEdge edge = localEdges[e];
                        if (a < b) edge.f0 = f; else edge.f1 = f;
                        localEdges[e] = edge;
                        faceIndices.Add(a);
                        faceEdges.Add(e);
                    }
                }

                offsets.Add(24);
                edges.AddRange(localEdges);
            }

            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, vertices = new Vector3[] { lo, hi } };
            mesh.RecalculateBounds();
            _objects.Add(mesh);
            var v = new NativeArray<float3>(vertices.ToArray(), Allocator.Temp);
            var o = new NativeArray<int>(offsets.ToArray(), Allocator.Temp);
            var fi = new NativeArray<int>(faceIndices.ToArray(), Allocator.Temp);
            var fe = new NativeArray<int>(faceEdges.ToArray(), Allocator.Temp);
            var ed = new NativeArray<BrepEdge>(edges.ToArray(), Allocator.Temp);
            try
            {
                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)v.GetUnsafePtr(), faceOffsets = (int*)o.GetUnsafePtr(), faceIndices = (int*)fi.GetUnsafePtr(),
                    faceEdges = (int*)fe.GetUnsafePtr(), edges = (BrepEdge*)ed.GetUnsafePtr(),
                };
                var meshes = new Mesh[lows.Length];
                for (int c = 0; c < meshes.Length; c++) meshes[c] = mesh;
                PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(bank, ranges, meshes, PhysicsShapeSource.External(), float4x4.identity);
                _disposables.Add(shape);
                return shape;
            }
            finally
            {
                v.Dispose(); o.Dispose(); fi.Dispose(); fe.Dispose(); ed.Dispose();
            }
        }

        // ---- a whole pick written as the collector writes one (without a world) ---------------------------------------------------------------

        private sealed class Written
        {
            internal string folder;
            internal NpcBoneLineage lineage;
            internal NpcBonePiece piece;
            internal NpcVertexBones table;
            internal uint[] indices;
            internal NpcPiecePickSample record;
            internal NpcBoneSplitRecord live;
            internal byte[] liveSides, liveBoneSides;
            internal readonly List<(bool ok, string why)> told = new List<(bool, string)>();
        }

        private static readonly int VertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
        private readonly Dictionary<NpcVertexBones, (string file, string hash)> _models = new Dictionary<NpcVertexBones, (string, string)>();

        /// <summary>One pick (number <paramref name="pick"/>) of the piece into <paramref name="sink"/>; <paramref name="strayVertex"/>: an index names a vertex no lineage has (a sample of another geometry).</summary>
        private Written WritePick(SpatialSplitCaptureSink sink, int pick, NpcVertexBones table, NpcBoneLineage lineage, bool strayVertex = false)
        {
            var w = new Written { folder = sink.directory, lineage = lineage, table = table };
            w.piece = NpcBonePiece.Whole(lineage.Parent, lineage.Present).Cut(BodyWaist, false, lineage.Parent, lineage.Present, lineage.Joints);
            w.indices = Indices();
            if (strayVertex) w.indices[w.indices.Length - 1] = 999;
            w.live = DecideLive(lineage, w.piece, w.indices, out w.liveSides, out w.liveBoneSides);
            PhysicsOwnerShape shape = BoxesShape(
                new[] { new float3(-0.3f, 0f, -0.3f), new float3(-0.4f, 0.5f, -0.1f), new float3(0.1f, 0.5f, -0.1f) },
                new[] { new float3(0.3f, 1.05f, 0.3f), new float3(-0.1f, 0.9f, 0.1f), new float3(0.4f, 0.9f, 0.1f) });
            shape.SetConvexBones(ConvexBones);
            Action<bool, string> done = (ok, why) => { lock (w.told) w.told.Add((ok, why)); };

            // 1. The model's table, once a table (as the collector keeps them by reference).
            if (!_models.TryGetValue(table, out (string file, string hash) model))
            {
                Assert.That(NpcPiecePickCopy.TryHandModelTable(sink, table, _models.Count + 1, pick, null, out model, out string why), Is.True, why);
                _models[table] = model;
            }

            // 2. The sample: a storage of 1000 vertices with a pattern, one submesh of every index, the shape's convexes.
            var storage = new NativeArray<VpRenderVertex>(1000, Allocator.Temp);
            var ranges = new NativeArray<ConvexBrepRange>(3, Allocator.Temp);
            var banks = new NativeArray<ConvexBrepBank>(3, Allocator.Temp);
            var indices = new NativeArray<uint>(w.indices, Allocator.Temp);
            var submesh = new NativeArray<int>(2, Allocator.Temp);
            try
            {
                byte* raw = (byte*)storage.GetUnsafePtr();
                var random = new System.Random(pick);
                for (long i = 0; i < 1000L * VertexBytes; i++) raw[i] = (byte)random.Next(256);
                for (int c = 0; c < 3; c++) { ranges[c] = shape.Convex(c); banks[c] = shape.BankOf(c); }
                submesh[0] = 0;
                submesh[1] = w.indices.Length;
                var identity = new SpatialSplitCaptureIdentity { world = 1, attempt = pick, issuedFrame = 40 + pick, fragment = 5, rootFragment = 2, splitSource = 0, generation = 1, epoch = 3, objectName = "NPC c_1" };
                var parameters = new SpatialSplitCaptureParameters
                {
                    margin = 0.01f, supportEpsilon = 0.004f, anchorEpsilon = 0.005f, vertexLimit = 128, submeshCount = 1, parentMass = 12.5, limits = new SpatialSplitSearchLimits(64, 8, 16, 131072),
                    cutConvexes = true, minCutSideTriangles = 50, bentSearch = true, bentLimits = SpatialSplitBentLimits.Default, maxConvexVertices = 8,
                    geometryToOwner = float4x4.TRS(new float3(1f, 2f, 3f), quaternion.identity, new float3(1f)), shapeToOwner = float4x4.identity,
                };
                int* offsets = (int*)submesh.GetUnsafeReadOnlyPtr();
                SpatialSplitCaptureSample sample = SpatialSplitCaptureCopy.TryTake(sink, in identity, in parameters, offsets, offsets + 1, new[] { 7 }, null,
                    (uint*)indices.GetUnsafeReadOnlyPtr(), w.indices.Length, (VpRenderVertex*)storage.GetUnsafeReadOnlyPtr(), 1000, 3,
                    (ConvexBrepRange*)ranges.GetUnsafeReadOnlyPtr(), (ConvexBrepBank*)banks.GetUnsafeReadOnlyPtr());
                Assert.That(sample, Is.Not.Null, "the sample's copy: " + sink.LastRefusal);
                sample.done = done;

                // 3. The pick's record, then the sample, the bones and the record handed (the collector's order).
                w.record = new NpcPiecePickSample
                {
                    sessionId = sink.sessionId, codeVersion = sink.codeVersion, pick = pick, frame = 40 + pick, realtimeSeconds = 12.5 + pick, utc = "2026-11-09T01:02:03.0000000Z",
                    world = 1, ledgerRevision = 77, fragment = 5, rootFragment = 2, splitSource = 0, generation = 1, epoch = 3,
                    geometry = new NpcPieceGeometryIdentity { vertexStart = (int)BaseStart, vertexCount = 24, topologyVertexCount = 24, submeshStart = 4, submeshCount = 1, blockStart = 9, blockCount = 2 },
                    objectName = "NPC c_1", modelName = table.ModelName, modelFile = model.file, modelSha256 = model.hash, weightsKept = table.HasWeights,
                    sampleFile = SpatialSplitCaptureSink.SampleFileName(1, pick), bonesFile = SpatialSplitBoneSample.FileName(1, pick),
                    aimOrigin = new float3(0.1f, 1.5f, -2f), aimDirection = math.normalize(new float3(0.2f, -0.3f, 1f)), hitWorld = new float3(0.123456f, 0.654321f, 1.1f), hitDistance = 3.3f, hitCollider = "collider 2",
                    hitOwner = new float3(0.1f, 0.6f, 1.2f), hitGeometry = new float3(-0.9f, -1.4f, -1.8f),
                    ownerLocalToWorld = float4x4.TRS(new float3(0f, 0f, 1f), quaternion.EulerXYZ(0.1f, 0.2f, 0.3f), new float3(1f)), geometryToOwner = parameters.geometryToOwner, shapeToOwner = float4x4.identity,
                    bodyPosition = new float3(0f, 0f, 1f), bodyVelocity = new float3(0f, -0.5f, 0f), bodyRotation = new float4(0f, 0f, 0f, 1f),
                    convexCount = 3, indexCount = w.indices.Length, examine = "Suppressed",
                    hasNote = true, noteAttempted = true, noteBoneLed = true, noteFellBack = true, noteIsOfThisGeometry = pick == 1, noteEndFrame = 30, noteMadeFrame = 20, noteMadeFrom = 4, noteInputEpoch = 3,
                    noteEnd = "NotFound", noteMadeBy = "the bones (negative side)", noteBoneVerdict = "NotSeparable", noteBoneReason = NpcBoneSplitSearch.ReasonOneGroup,
                    noteInput = new NpcPieceGeometryIdentity { vertexStart = (int)BaseStart, vertexCount = 24, topologyVertexCount = 24, submeshStart = 4, submeshCount = 1, blockStart = 9, blockCount = pick == 1 ? 2 : 1 },
                    noteBones = 9, noteClusters = 1, noteMeshComponents = 5, noteGroups = 1, noteFullGroups = 1, noteReferencedVertices = 26, noteLineageVertices = 27, notePieceBones = 5, noteUnboundGroups = 0,
                    notePositiveConvexes = 0, noteNegativeConvexes = 0, notePositiveTriangles = 0, noteNegativeTriangles = 0, noteOriginSteps = 31, noteRemovedWeights = 2,
                };
                SpatialSplitBoneItem item = NpcPiecePickCopy.MakeRecordItem(w.record, done);
                Assert.That(sink.TryReserve(item.bytes, false), Is.True);
                sink.NoteIssued(sample);
                var settings = new CutSpatialSplitSettings();
                Assert.That(SpatialSplitBoneSample.Write(sink, 1, pick, 5, 3, lineage, w.piece, ConvexBones, null, NpcBoneSplitSearch.DefaultMaxOriginSteps, table.Pointer, lineage.Chunks, lineage.LocalCount,
                    in settings, false, done), Is.True, "the bones handed");
                sink.HandBone(item);
            }
            finally
            {
                storage.Dispose(); ranges.Dispose(); banks.Dispose(); indices.Dispose(); submesh.Dispose();
            }

            return w;
        }

        private SpatialSplitCaptureSink NewSink(string name) =>
            new SpatialSplitCaptureSink(Path.Combine(_root, name), SpatialSplitCaptureLimits.Default, "test code", new[] { "-zantetsuPieceCollect <directory>" });

        private static string Describe(in NpcBoneSplitRecord r) =>
            r.verdict + " (" + r.reason + "): bones held " + r.pieceBones + ", clusters " + r.clusters + ", components " + r.meshComponents + ", groups " + r.groups + " (" + r.fullGroups + " full, "
            + r.unboundGroups + " mesh alone), removed weights " + r.removedWeights + ", referenced " + r.referencedVertices + " of " + r.lineageVertices + ", steps " + r.originSteps
            + ", convexes +" + r.positiveConvexes + " -" + r.negativeConvexes + ", triangles +" + r.positiveTriangles + " -" + r.negativeTriangles;

        /// <summary>
        /// **A pick written through the sink reads back whole, and the decision over it is the live one.** The piece below the waist with one
        /// cut's vertices (a pelvis vertex interpolated, a cap's auxiliary, a left-hand vertex interpolated), three convexes and nine triangles,
        /// written as the collector writes a pick; read back, every file is there with its listed hash, the three agree, and
        /// <see cref="NpcBoneSplitSearch.Decide"/> over the saved parts gives the same verdict, the same counts and the same side for every
        /// convex, triangle and bone as over the live parts. The record's own values -- where the line pointed, the owner's frame, the note
        /// and its generation -- read back as written, and the writer told each of the three items once.
        /// </summary>
        [Test]
        public void APick_WrittenThroughTheSink_ReadsBackWhole_AndTheDecisionOverItIsTheLiveOne()
        {
            ulong[] bones = Weights();
            NpcVertexBones table = Table(bones, WeightValues(bones));
            NpcBoneLineage lineage = Lineage(table, bones.Length);
            AddCutChunk(lineage);
            SpatialSplitCaptureSink sink = NewSink("round");
            Written w = WritePick(sink, 1, table, lineage);
            Assert.That(sink.Close(10.0), Is.True, "the writer ended");
            TestContext.Out.WriteLine("live: " + Describe(in w.live));
            Assert.That(w.live.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable), "the layout: three full groups below the waist (the pelvis, the two hands)");
            Assert.That(w.live.fullGroups, Is.EqualTo(3));
            Assert.That(w.live.originSteps, Is.GreaterThan(w.live.referencedVertices), "the cut's vertices were walked back");
            lock (w.told) Assert.That(w.told.Select(t => t.ok).ToArray(), Is.EqualTo(new[] { true, true, true }), "the sample, the bones and the record: each told once, written: " + string.Join("; ", w.told.Select(t => t.why)));
            Assert.That(sink.Saved, Is.EqualTo(1));
            Assert.That(sink.Lost + sink.NotCapturedInAll + sink.Returned, Is.Zero);
            Assert.That(sink.QueuedItems == 0 && sink.QueuedBytes == 0, Is.True, "everything given back");

            List<NpcPiecePickSet.Pick> picks = NpcPiecePickSet.Read(w.folder);
            TestContext.Out.WriteLine(NpcPiecePickSet.Describe(picks));
            Assert.That(picks, Has.Count.EqualTo(1));
            NpcPiecePickSet.Pick pick = picks[0];
            Assert.That(pick.IsWhole, Is.True, string.Join("; ", pick.problems));
            Assert.That(pick.number, Is.EqualTo(1));

            // The decision over what was read: the same as the live one, side by side.
            NpcBoneSplitRecord again = NpcPiecePickSet.Decide(pick, out byte[] sides, out byte[] boneSides);
            TestContext.Out.WriteLine("read back: " + Describe(in again));
            Assert.That(Describe(in again), Is.EqualTo(Describe(in w.live)));
            Assert.That(again.nativeBytes, Is.EqualTo(w.live.nativeBytes));
            Assert.That(sides, Is.EqualTo(w.liveSides), "every convex and triangle on the same side");
            Assert.That(boneSides, Is.EqualTo(w.liveBoneSides), "every bone and connection on the same side");

            // The record's own values, as written.
            NpcPiecePickSample r = pick.record;
            Assert.That(r.pick == 1 && r.frame == 41 && r.world == 1 && r.ledgerRevision == 77 && r.fragment == 5 && r.rootFragment == 2 && r.generation == 1 && r.epoch == 3, Is.True, "who it is");
            Assert.That(r.geometry.Equals(w.record.geometry), Is.True, "the geometry's identity");
            Assert.That(r.hitWorld.Equals(w.record.hitWorld) && r.hitGeometry.Equals(w.record.hitGeometry) && r.hitOwner.Equals(w.record.hitOwner) && r.aimDirection.Equals(w.record.aimDirection), Is.True, "where the line pointed, bit for bit");
            Assert.That(r.ownerLocalToWorld.Equals(w.record.ownerLocalToWorld) && r.geometryToOwner.Equals(w.record.geometryToOwner), Is.True, "the frames");
            Assert.That(r.hitCollider == "collider 2" && r.objectName == "NPC c_1" && r.examine == "Suppressed" && r.modelName == "pick test model", Is.True);
            Assert.That(r.hasNote && r.noteAttempted && r.noteFellBack && r.noteIsOfThisGeometry && r.noteEnd == "NotFound" && r.noteBoneVerdict == "NotSeparable" && r.noteBoneReason == NpcBoneSplitSearch.ReasonOneGroup
                        && r.noteMadeBy == "the bones (negative side)" && r.noteMadeFrom == 4 && r.noteInput.Equals(w.record.noteInput) && r.noteOriginSteps == 31 && r.noteRemovedWeights == 2 && r.noteClusters == 1, Is.True, "the split's note");
            Assert.That(r.weightsKept && r.modelFile == "model-001.zsw.gz" && r.modelSha256 == pick.model.sha256, Is.True, "the model's table named with its hash");
            Assert.That(pick.sample.indices, Is.EqualTo(w.indices), "the sample's indices as they were");
            Assert.That(pick.sample.convexes.Length == 3 && pick.bones.convexBones.SequenceEqual(ConvexBones), Is.True);
            Assert.That(pick.bones.chunkStarts, Is.EqualTo(new uint[] { 200 }));
            Assert.That(pick.bones.chunkOrigins[0][1].x, Is.EqualTo(MeshCutOutput.NoVertexOrigin), "the cap's auxiliary vertex has no origin");

            string[] lines = File.ReadAllLines(Path.Combine(w.folder, "picks.jsonl"));
            Assert.That(lines, Has.Length.EqualTo(1));
            StringAssert.Contains("\"noteIsOfThisGeometry\":true", lines[0]);
            StringAssert.Contains("\"failure\":\"\"", lines[0]);
            Assert.That(File.ReadAllLines(Path.Combine(w.folder, "models.jsonl")), Has.Length.EqualTo(1));
            Assert.That(Directory.GetFiles(w.folder, "*.partial"), Is.Empty, "no file of the writer's left half done");
        }

        /// <summary>
        /// **The model's weights are kept in the collection's mode and read back bit for bit.** A table of entries with weights -- odd floats a
        /// text would round -- is written as the model's table and read back: every bone and every weight as its bits were, each kept weight with
        /// a bone and each bone with a positive finite weight. A table made without weights says so (the product's), and the mode's switch
        /// decides which a table from a mesh keeps.
        /// </summary>
        [Test]
        public void TheModelsWeights_AreKeptInTheCollectionsMode_AndReadBackBitForBit()
        {
            ulong[] bones = Weights();
            float[] values = WeightValues(bones);
            NpcVertexBones table = Table(bones, values);
            Assert.That(table.HasWeights, Is.True);
            Assert.That(table.Weight(3, 0), Is.EqualTo(0.1f), "the entries' places");
            Assert.That(table.Weight(3, 2), Is.Zero, "no bone: no weight");
            byte[] content = NpcModelWeightTable.Make("session x", table, out string hash, out long raw);
            string path = Path.Combine(_root, "model-001.zsw.gz");
            File.WriteAllBytes(path, content);
            NpcModelWeightTable read = NpcModelWeightTable.Read(path);
            Assert.That(read.sha256, Is.EqualTo(hash));
            Assert.That(read.modelName == "pick test model" && read.vertexCount == bones.Length && read.paletteCount == 16 && read.hasWeights, Is.True);
            Assert.That(read.bones, Is.EqualTo(bones));
            Assert.That(read.weights.Select(BitConverter.SingleToInt32Bits).ToArray(), Is.EqualTo(values.Select(BitConverter.SingleToInt32Bits).ToArray()), "every weight's bits");
            Assert.That(read.Check(), Is.Null);
            Assert.That(raw, Is.EqualTo(8 + 4 + (4 + 9) + (4 + 15) + 4 + 4 + 1 + (8L * bones.Length) + (16L * bones.Length) + 4), "the content's length as laid out");

            NpcVertexBones plain = Table(bones, null);
            Assert.That(plain.HasWeights, Is.False, "the product's table: bones alone");
            byte[] plainContent = NpcModelWeightTable.Make("session x", plain, out _, out _);
            string plainPath = Path.Combine(_root, "model-002.zsw.gz");
            File.WriteAllBytes(plainPath, plainContent);
            NpcModelWeightTable plainRead = NpcModelWeightTable.Read(plainPath);
            Assert.That(plainRead.hasWeights, Is.False);
            Assert.That(plainRead.weights, Is.Null);
            Assert.That(plainRead.Check(), Is.Null);

            // A table whose weight has no bone, or whose bone has no weight, is told.
            read.weights[(4 * 0) + 3] = 0.5f;
            StringAssert.Contains("has a weight and no bone", read.Check());
            read.weights[(4 * 0) + 3] = 0f;
            read.weights[4 * 0] = 0f;
            StringAssert.Contains("has a bone and a weight of 0", read.Check());

            bool before = NpcVertexBones.KeepWeights;
            try
            {
                NpcVertexBones.KeepWeights = false;
                Assert.That(NpcVertexBones.KeepWeights, Is.False, "off unless the collection's launch asks (the product never sets it)");
            }
            finally
            {
                NpcVertexBones.KeepWeights = before;
            }
        }

        /// <summary>
        /// **The reader tells a missing file, a hash that is not the listed one, a sample of another geometry, and a pick that failed.** Two
        /// picks whole and one whose indices name a vertex no lineage has: the bones' file of the first deleted, the sample's listed hash of the
        /// second changed, and a failed pick's line appended; each is named by the reader for what it is, and nothing is read as whole that is not.
        /// </summary>
        [Test]
        public void TheReader_TellsAMissingFile_AWrongHash_ASampleOfAnotherGeometry_AndAFailedPick()
        {
            ulong[] bones = Weights();
            NpcVertexBones table = Table(bones, WeightValues(bones));
            NpcBoneLineage lineage = Lineage(table, bones.Length);
            AddCutChunk(lineage);
            SpatialSplitCaptureSink sink = NewSink("broken");
            Written one = WritePick(sink, 1, table, lineage);
            Written two = WritePick(sink, 2, table, lineage);
            Written three = WritePick(sink, 3, table, lineage, strayVertex: true);
            Assert.That(three.live.verdict, Is.EqualTo(NpcBoneSplitVerdict.Insufficient), "the layout: a vertex of no known origin");
            NpcPiecePickCopy.HandFailure(sink, 1, 4, "the line rests on something that is not a piece of the cut world", -1, null);
            Assert.That(sink.Close(10.0), Is.True, "the writer ended");
            Assert.That(sink.Saved, Is.EqualTo(3));
            string folder = sink.directory;

            List<NpcPiecePickSet.Pick> whole = NpcPiecePickSet.Read(folder);
            Assert.That(whole.Count(p => p.IsWhole), Is.EqualTo(2), NpcPiecePickSet.Describe(whole));
            Assert.That(whole[2].problems.Any(p => p.Contains("geometry generation differs")), Is.True, "the stray vertex: " + string.Join("; ", whole[2].problems));
            Assert.That(whole[3].failure, Is.EqualTo("the line rests on something that is not a piece of the cut world"));
            Assert.That(whole[3].IsWhole, Is.False);

            File.Delete(Path.Combine(folder, SpatialSplitBoneSample.FileName(1, 1)));
            string samples = Path.Combine(folder, "samples.jsonl");
            string[] lines = File.ReadAllLines(samples);
            int second = Array.FindIndex(lines, l => l.Contains("\"attempt\":2,"));
            Assert.That(second, Is.GreaterThanOrEqualTo(0));
            lines[second] = System.Text.RegularExpressions.Regex.Replace(lines[second], "\"sha256\":\"[0-9a-f]{8}", "\"sha256\":\"00000000");
            File.WriteAllLines(samples, lines);

            List<NpcPiecePickSet.Pick> read = NpcPiecePickSet.Read(folder);
            string description = NpcPiecePickSet.Describe(read);
            TestContext.Out.WriteLine(description);
            Assert.That(read, Has.Count.EqualTo(4));
            Assert.That(read[0].IsWhole, Is.False);
            Assert.That(read[0].problems.Any(p => p.Contains("the bones is missing")), Is.True, string.Join("; ", read[0].problems));
            Assert.That(read[1].IsWhole, Is.False);
            Assert.That(read[1].problems.Any(p => p.Contains("the sample's hash is not the listed one")), Is.True, string.Join("; ", read[1].problems));
            Assert.That(read[2].IsWhole, Is.False);
            Assert.That(read[3].failure, Is.Not.Null);
            Assert.That(read.Count(p => p.IsWhole), Is.Zero);
            StringAssert.Contains("whole 0, failed at the time 1, missing or not agreeing 3", description);
            Assert.That(one.record.pick == 1 && two.record.pick == 2, Is.True);
        }
    }
}
