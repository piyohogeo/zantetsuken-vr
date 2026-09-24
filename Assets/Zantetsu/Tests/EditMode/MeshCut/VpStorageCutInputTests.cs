using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Running the Phase 2.9 kernel straight over a geometry a <see cref="VpCpuGeometryStorage"/> owns (DESIGN 4.5.3 /
    /// 6): the adapter takes the read lease and hands the kernel the storage's own vertex, index and topology views,
    /// the output is received into memory this test owns, and the existing verifier checks it. The same source data is
    /// also cut through the verification harness at different offsets, and the two runs are compared where they must
    /// agree — crossing nodes, surface and cap boundaries in topology-id space, interpolated attributes and submesh
    /// correspondence — not in triangle order, ear order or per-side cap area.
    /// </summary>
    public unsafe class VpStorageCutInputTests
    {
        // An asymmetric closed hexahedron: 8 control points, one set of render vertices per face (so every edge is an
        // attribute seam), two submeshes whose material indices are not their ordinals.
        private const int ControlPoints = 8;
        private const int RenderVertices = 24;
        private const int IndexCount = 36;
        private const int SideIndices = 24;
        private const int EndIndices = 12;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.2f, 0.0f, -1.0f), new float3(1.1f, 0.0f, 0.9f), new float3(-0.8f, 0.0f, 1.0f),
            new float3(-0.5f, 1.3f, -0.4f), new float3(0.7f, 1.3f, -0.6f), new float3(0.6f, 1.3f, 0.5f), new float3(-0.3f, 1.3f, 0.6f),
        };

        // quads wound so that cross(c1 - c0, c2 - c0) points outward; submesh 0 = sides, 1 = the two ends
        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private sealed class Prepared
        {
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] TopologyOfVertex;
            public VpGeometrySubmesh[] Submeshes;
        }

        private static Prepared BuildPrepared()
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_controlPoints[c[k]],
                            normal = n,
                            uv0 = uv[k] * .8f + new float2(f * 0.013f, f * 0.021f),
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            return new Prepared
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                TopologyOfVertex = topology.ToArray(),
                Submeshes = submeshes.ToArray(),
            };
        }

        /// <summary>The same geometry as a synthetic mesh, so the verification harness can cut the identical source data.</summary>
        private static SyntheticMesh AsSyntheticMesh(Prepared prepared)
        {
            var mesh = new SyntheticMesh
            {
                Vertices = (VpRenderVertex[])prepared.Vertices.Clone(),
                Indices = (uint[])prepared.Indices.Clone(),
                TopologyOfVertex = (int[])prepared.TopologyOfVertex.Clone(),
                TopologyVertexCount = ControlPoints,
            };
            foreach (VpGeometrySubmesh submesh in prepared.Submeshes)
            {
                mesh.SubmeshIndexCounts.Add(submesh.indexCount);
            }

            return mesh;
        }

        private static float4 TiltedPlane()
        {
            float3 centre = float3.zero;
            foreach (float3 p in k_controlPoints)
            {
                centre += p;
            }

            centre /= k_controlPoints.Length;
            return SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), centre + new float3(0.0071f, -0.0233f, 0.0119f));
        }

        private static VpCpuGeometryStorage NewStorage(int indexCapacity = 256, int descriptorCapacity = 4)
        {
            return new VpCpuGeometryStorage(256, indexCapacity, descriptorCapacity, 16, 16, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendCuttable(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, ControlPoints, prepared.Submeshes, out VpStoredGeometry geometry, out _),
                Is.True,
                "append as a cut input");
            return geometry;
        }

        /// <summary>One synchronous kernel run over the adapter's input, with every output array owned by the test.</summary>
        private sealed class StorageRun : IDisposable
        {
            public MeshCutResult Result;
            public CutRun Run;
            public uint[] InputIndices;
            public VpRenderVertex[] NewVertices;

            /// <summary>The whole output reservation after the run, to see whether anything was written into it.</summary>
            public uint[] RawNewIndices;
            public const uint IndexSentinel = 0xDEADBEEFu;

            private NativeArray<VpRenderVertex> _newVertices;
            private NativeArray<uint> _newIndices;
            private NativeArray<int> _newTopology;
            private NativeArray<MeshCutIndexRange> _outputRanges;
            private NativeArray<long> _nodeKeys;
            private NativeArray<float> _nodeParams;
            private NativeArray<byte> _scratch;

            public static StorageRun Execute(VpCpuGeometryStorage storage, VpStorageCutInput adapter, float4 plane)
            {
                var run = new StorageRun();
                Assert.That(adapter.TryGetInput(plane, out MeshCutInput input), Is.True, "build input");

                var capacity = new MeshCutCapacity();
                MeshCutKernel.QueryCapacity(in input, ref capacity);
                Assert.That(capacity.invalidInput, Is.Zero, "the kernel accepts the storage input");

                VpStoredGeometry geometry = adapter.Geometry;
                int vertexBase = storage.VertexCount;
                int indexBase = adapter.IndexViewLength;
                run._newVertices = new NativeArray<VpRenderVertex>(math.max(1, capacity.newVertices), Allocator.Persistent);
                run._newIndices = new NativeArray<uint>(math.max(1, capacity.newIndices), Allocator.Persistent);
                run._newTopology = new NativeArray<int>(math.max(1, capacity.newVertices), Allocator.Persistent);
                run._outputRanges = new NativeArray<MeshCutIndexRange>(2 * adapter.RangeCount, Allocator.Persistent);
                run._nodeKeys = new NativeArray<long>(math.max(1, 2 * capacity.crossingTriangles), Allocator.Persistent);
                run._nodeParams = new NativeArray<float>(math.max(1, 2 * capacity.crossingTriangles), Allocator.Persistent);
                run._scratch = new NativeArray<byte>(math.max(1, capacity.scratchBytes), Allocator.Persistent);
                for (int i = 0; i < run._newIndices.Length; i++)
                {
                    run._newIndices[i] = IndexSentinel;
                }

                var output = new MeshCutOutput
                {
                    newVertices = (VpRenderVertex*)run._newVertices.GetUnsafePtr(),
                    newVertexBase = (uint)vertexBase,
                    newVertexCapacity = capacity.newVertices,
                    newVertexTopology = (int*)run._newTopology.GetUnsafePtr(),
                    newIndices = (uint*)run._newIndices.GetUnsafePtr(),
                    newIndexBase = (uint)indexBase,
                    newIndexCapacity = capacity.newIndices,
                    outputRanges = (MeshCutIndexRange*)run._outputRanges.GetUnsafePtr(),
                    nodeEdgeKeys = (long*)run._nodeKeys.GetUnsafePtr(),
                    nodeParams = (float*)run._nodeParams.GetUnsafePtr(),
                    nodeCapacity = run._nodeKeys.Length,
                    scratch = (byte*)run._scratch.GetUnsafePtr(),
                    scratchBytes = run._scratch.Length,
                };

                var result = new MeshCutResult();
                MeshCutKernel.Execute(in input, in output, ref result);
                run.Result = result;
                Assert.That(result.status, Is.EqualTo(MeshCutStatus.Ok), "kernel status");

                // ---- everything below is the test's own assembly of a run the existing verifier understands
                run.InputIndices = ReadIndexView(input);
                run.NewVertices = run._newVertices.GetSubArray(0, result.newVertexCount).ToArray();
                run.RawNewIndices = run._newIndices.ToArray();

                var pool = new PoolArrays
                {
                    Vertices = new VpRenderVertex[vertexBase + result.newVertexCount],
                    Indices = new uint[indexBase + result.newIndexCount],
                };
                NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                for (int v = 0; v < vertexBase; v++)
                {
                    pool.Vertices[v] = committed[v];
                }

                for (int v = 0; v < result.newVertexCount; v++)
                {
                    pool.Vertices[vertexBase + v] = run._newVertices[v];
                }

                for (int i = 0; i < indexBase; i++)
                {
                    pool.Indices[i] = run.InputIndices[i];
                }

                for (int i = 0; i < result.newIndexCount; i++)
                {
                    pool.Indices[indexBase + i] = run._newIndices[i];
                }

                var inputGeometry = new CutGeometry { Name = "storage", Pool = pool, TopologyVertexCount = ControlPoints };
                for (int r = 0; r < adapter.RangeCount; r++)
                {
                    inputGeometry.Ranges.Add(input.ranges[r]);
                }

                Assert.That(storage.TryGetTopology(geometry, out NativeArray<int>.ReadOnly topology, out _), Is.True, "topology view");
                inputGeometry.Topology.Add(new TopologyBlock { VertexBase = (uint)geometry.vertexStart, TopologyVertex = topology.ToArray() });

                var newTopology = new int[result.newVertexCount];
                for (int v = 0; v < result.newVertexCount; v++)
                {
                    newTopology[v] = run._newTopology[v];
                }

                var cutRun = new CutRun
                {
                    Input = inputGeometry,
                    Plane = plane,
                    Result = result,
                    NewVertexBase = (uint)vertexBase,
                    NewVertexCount = result.newVertexCount,
                    NewVertexTopology = newTopology,
                    TopologyBase = ControlPoints,
                    OutputRanges = run._outputRanges.ToArray(),
                };
                if (result.nodeCorrespondenceWritten != 0 || result.nodeCount == 0)
                {
                    cutRun.NodeKeys = run._nodeKeys.GetSubArray(0, result.nodeCount).ToArray();
                    cutRun.NodeParams = run._nodeParams.GetSubArray(0, result.nodeCount).ToArray();
                }

                TopologyBlock appended = result.newVertexCount > 0
                    ? new TopologyBlock { VertexBase = (uint)vertexBase, TopologyVertex = newTopology }
                    : null;
                cutRun.Positive = Side(inputGeometry, cutRun, 0, appended, result.newTopologyVertexCount, "+");
                cutRun.Negative = Side(inputGeometry, cutRun, 1, appended, result.newTopologyVertexCount, "-");
                run.Run = cutRun;
                return run;
            }

            private static CutGeometry Side(CutGeometry parent, CutRun run, int index, TopologyBlock appended, int topologyCount, string suffix)
            {
                var ranges = new List<MeshCutIndexRange>();
                MeshCutSideResult side = index == 0 ? run.Result.positive : run.Result.negative;
                if (side.reusesInput != 0)
                {
                    ranges.AddRange(parent.Ranges);
                }
                else
                {
                    for (int r = 0; r < parent.Ranges.Count; r++)
                    {
                        ranges.Add(run.OutputRanges[index * parent.Ranges.Count + r]);
                    }
                }

                return parent.Child(parent.Name + suffix, ranges, appended, topologyCount);
            }

            private static uint[] ReadIndexView(MeshCutInput input)
            {
                var indices = new uint[input.indexViewLength];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = input.indices[i];
                }

                return indices;
            }

            public void Dispose()
            {
                if (_newVertices.IsCreated) _newVertices.Dispose();
                if (_newIndices.IsCreated) _newIndices.Dispose();
                if (_newTopology.IsCreated) _newTopology.Dispose();
                if (_outputRanges.IsCreated) _outputRanges.Dispose();
                if (_nodeKeys.IsCreated) _nodeKeys.Dispose();
                if (_nodeParams.IsCreated) _nodeParams.Dispose();
                if (_scratch.IsCreated) _scratch.Dispose();
            }
        }

        private static void AssertVerifierPasses(CutRun run, string label)
        {
            VerificationReport report = MeshCutVerifier.Verify(run);
            Assert.That(report.Passed, Is.True, label + ": " + report);
        }

        /// <summary>Directed boundary of a triangle set in topology-id space; interior edges cancel.</summary>
        private static HashSet<(int, int)> DirectedBoundary(CutGeometry side, Func<uint, bool> isCap, bool capsOnly)
        {
            var count = new Dictionary<(int, int), int>();
            foreach ((uint a, uint b, uint c, int _) in side.Triangles())
            {
                bool cap = isCap(a) && isCap(b) && isCap(c);
                if (cap != capsOnly)
                {
                    continue;
                }

                int ta = side.TopologyOf(a), tb = side.TopologyOf(b), tc = side.TopologyOf(c);
                foreach ((int, int) e in new[] { (ta, tb), (tb, tc), (tc, ta) })
                {
                    count[e] = count.TryGetValue(e, out int n) ? n + 1 : 1;
                }
            }

            var boundary = new HashSet<(int, int)>();
            foreach (KeyValuePair<(int, int), int> pair in count)
            {
                if (!count.ContainsKey((pair.Key.Item2, pair.Key.Item1)))
                {
                    boundary.Add(pair.Key);
                }
            }

            return boundary;
        }

        private static Func<uint, bool> CapPredicate(CutRun run)
        {
            return v =>
            {
                if (!run.IsNew(v))
                {
                    return false;
                }

                VpRenderVertex rv = run.Input.Pool.Vertices[v];
                return rv.uv0.x == RenderCutMarker.CapUvX && rv.uv0.y == RenderCutMarker.CapUvY;
            };
        }

        [Test]
        public void AStoredGeometry_IsCutThroughTheStorageViewsAndPassesTheVerifier()
        {
            Prepared prepared = BuildPrepared();
            float4 plane = TiltedPlane();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, prepared);
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(subject.vertexStart, Is.EqualTo(RenderVertices), "the subject does not start at vertex 0");
                Assert.That(storage.TryGetIndexState(subject.indexRange, out _, out int indexStart, out int indexCount), Is.True);
                Assert.That(indexStart, Is.EqualTo(IndexCount), "and its index range does not start at 0 either");
                Assert.That(indexCount, Is.EqualTo(IndexCount));

                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput adapter), Is.True, "acquire");
                using (adapter)
                {
                    Assert.That(adapter.RangeCount, Is.EqualTo(2), "one kernel range per submesh");
                    Assert.That(adapter.IndexViewLength, Is.EqualTo(IndexCount), "the index view is the leased range");
                    Assert.That(adapter.TryGetInput(plane, out MeshCutInput input), Is.True);
                    Assert.That(input.ranges[0].indexStart, Is.Zero, "the first submesh starts at the view's own start");
                    Assert.That((int)input.ranges[0].indexCount, Is.EqualTo(SideIndices));
                    Assert.That(input.ranges[1].indexStart, Is.EqualTo((uint)SideIndices), "offsets are relative to the view, not the index buffer");
                    Assert.That((int)input.ranges[1].indexCount, Is.EqualTo(EndIndices));
                    Assert.That(input.indexViewLength, Is.EqualTo(IndexCount));
                    Assert.That(input.vertexViewLength, Is.EqualTo(storage.VertexCount), "the vertex view is every committed vertex");
                    Assert.That(input.topology.ranges[0].vertexBase, Is.EqualTo((uint)subject.vertexStart), "the topology range is based at the geometry's vertex start");
                    Assert.That(input.topology.topologyVertexCount, Is.EqualTo(ControlPoints));

                    using (StorageRun run = StorageRun.Execute(storage, adapter, plane))
                    {
                        AssertVerifierPasses(run.Run, "storage run");
                        Assert.That(run.Result.crossingTriangles, Is.GreaterThan(0), "the plane crosses the geometry");
                        Assert.That(run.Result.capTriangles, Is.GreaterThan(0), "and caps are built");
                        Assert.That(run.Run.OutputRanges.Length, Is.EqualTo(4), "two sides of two submeshes");
                    }
                }

                Assert.That(storage.TryGetTopology(first, out _, out _), Is.True, "the other geometry is untouched");
            }
        }

        [Test]
        public void TheStorageRun_AgreesWithTheHarnessRunOnTheSameSourceData()
        {
            Prepared prepared = BuildPrepared();
            float4 plane = TiltedPlane();
            using (VpCpuGeometryStorage storage = NewStorage())
            using (var harness = new MeshCutHarness())
            {
                Append(storage, prepared);
                VpStoredGeometry subject = Append(storage, prepared);

                // the harness holds the same source data at its own, different offsets
                CutGeometry harnessInput = harness.Place(AsSyntheticMesh(prepared), "harness", 4096, 4096);
                CutRun harnessRun = harness.Cut(harnessInput, plane);
                Assert.That(harnessRun.Result.status, Is.EqualTo(MeshCutStatus.Ok), "harness status");
                AssertVerifierPasses(harnessRun, "harness run");

                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput adapter), Is.True);
                using (adapter)
                using (StorageRun storageRun = StorageRun.Execute(storage, adapter, plane))
                {
                    AssertVerifierPasses(storageRun.Run, "storage run");
                    MeshCutResult a = storageRun.Result, b = harnessRun.Result;

                    Assert.That(a.triangleCount, Is.EqualTo(b.triangleCount), "input triangles");
                    Assert.That(a.crossingTriangles, Is.EqualTo(b.crossingTriangles), "crossing triangles");
                    Assert.That(a.nodeCount, Is.EqualTo(b.nodeCount), "nodes");
                    Assert.That(a.newVertexCount, Is.EqualTo(b.newVertexCount), "new vertices");
                    Assert.That(a.newIndexCount, Is.EqualTo(b.newIndexCount), "new indices");
                    Assert.That(a.capTriangles, Is.EqualTo(b.capTriangles), "cap triangles");
                    Assert.That(a.capAuxVertices, Is.EqualTo(b.capAuxVertices), "aux vertices");
                    Assert.That(a.loopCount, Is.EqualTo(b.loopCount), "loops");
                    Assert.That(a.positive.indexCount, Is.EqualTo(b.positive.indexCount), "positive index count");
                    Assert.That(a.negative.indexCount, Is.EqualTo(b.negative.indexCount), "negative index count");

                    // nodes live on topology edges, which are geometry-local in both runs
                    var storageNodes = new Dictionary<long, float>();
                    for (int n = 0; n < a.nodeCount; n++)
                    {
                        storageNodes[storageRun.Run.NodeKeys[n]] = storageRun.Run.NodeParams[n];
                    }

                    for (int n = 0; n < b.nodeCount; n++)
                    {
                        Assert.That(storageNodes.TryGetValue(harnessRun.NodeKeys[n], out float param), Is.True, "node edge key " + harnessRun.NodeKeys[n]);
                        Assert.That(param, Is.EqualTo(harnessRun.NodeParams[n]).Within(1e-6f), "node parameter");
                    }

                    // boundaries in topology-id space, surfaces and caps apart; triangle and ear order are not compared
                    Func<uint, bool> storageCap = CapPredicate(storageRun.Run);
                    Func<uint, bool> harnessCap = CapPredicate(harnessRun);
                    foreach ((string label, CutGeometry x, CutGeometry y) in new[]
                             {
                                 ("positive", storageRun.Run.Positive, harnessRun.Positive),
                                 ("negative", storageRun.Run.Negative, harnessRun.Negative),
                             })
                    {
                        Assert.That(DirectedBoundary(x, storageCap, false), Is.EquivalentTo(DirectedBoundary(y, harnessCap, false)), label + " surface boundary");
                        Assert.That(DirectedBoundary(x, storageCap, true), Is.EquivalentTo(DirectedBoundary(y, harnessCap, true)), label + " cap boundary");
                        Assert.That(x.Ranges.Count, Is.EqualTo(y.Ranges.Count), label + " submesh ranges");
                        for (int r = 0; r < x.Ranges.Count; r++)
                        {
                            Assert.That(x.Ranges[r].indexCount, Is.EqualTo(y.Ranges[r].indexCount), label + " submesh " + r + " index count");
                        }
                    }

                    // the interpolated attributes of the new vertices, matched by topology id and kind
                    var harnessNew = new List<(int topology, VpRenderVertex vertex)>();
                    for (int v = 0; v < b.newVertexCount; v++)
                    {
                        harnessNew.Add((harnessRun.NewVertexTopology[v], harnessRun.Input.Pool.Vertices[harnessRun.NewVertexBase + (uint)v]));
                    }

                    for (int v = 0; v < a.newVertexCount; v++)
                    {
                        int topology = storageRun.Run.NewVertexTopology[v];
                        VpRenderVertex vertex = storageRun.NewVertices[v];
                        bool matched = harnessNew.Any(h => h.topology == topology
                            && math.all(math.abs((float3)h.vertex.position - (float3)vertex.position) < 1e-5f)
                            && math.all(math.abs((float2)h.vertex.uv0 - (float2)vertex.uv0) < 1e-5f)
                            && math.dot((float3)h.vertex.normal, (float3)vertex.normal) > 0.999f);
                        Assert.That(matched, Is.True, "new vertex " + v + " (topology " + topology + ") has a counterpart with the same position, uv0 and normal");
                    }
                }
            }
        }

        [Test]
        public void TheKernelRun_LeavesTheStorageInputUnchanged()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, prepared);
                VpStoredGeometry subject = Append(storage, prepared);
                VpRenderVertex[] verticesBefore = storage.Vertices.ToArray();
                Assert.That(storage.TryGetTopology(subject, out NativeArray<int>.ReadOnly topologyView, out int topologyVertexCount), Is.True);
                int[] topologyBefore = topologyView.ToArray();
                Assert.That(storage.TryGetSubmeshes(subject, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshView), Is.True);
                VpGeometrySubmesh[] submeshesBefore = submeshView.ToArray();

                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput adapter), Is.True);
                uint[] indicesBefore;
                using (adapter)
                using (StorageRun run = StorageRun.Execute(storage, adapter, TiltedPlane()))
                {
                    indicesBefore = run.InputIndices;
                    AssertVerifierPasses(run.Run, "storage run");
                    Assert.That(adapter.TryGetInput(TiltedPlane(), out MeshCutInput after), Is.True, "the input can be built again");
                    var indicesAfter = new uint[after.indexViewLength];
                    for (int i = 0; i < indicesAfter.Length; i++)
                    {
                        indicesAfter[i] = after.indices[i];
                    }

                    Assert.That(indicesAfter, Is.EqualTo(indicesBefore), "the leased indices are unchanged");
                }

                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(verticesBefore), "committed vertices unchanged");
                Assert.That(storage.TryGetTopology(subject, out NativeArray<int>.ReadOnly topologyAfter, out int countAfter), Is.True);
                Assert.That(topologyAfter.ToArray(), Is.EqualTo(topologyBefore), "topology mapping unchanged");
                Assert.That(countAfter, Is.EqualTo(topologyVertexCount), "topology vertex count unchanged");
                Assert.That(storage.TryGetSubmeshes(subject, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshesAfter), Is.True);
                Assert.That(submeshesAfter.ToArray(), Is.EqualTo(submeshesBefore), "submesh descriptors unchanged");
            }
        }

        [Test]
        public void AGeometryRetiredWhileLeased_StaysRetiringUntilTheAdapterIsDisposed()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput adapter), Is.True);

                Assert.That(storage.TryRetireIndices(subject.indexRange), Is.True, "retire while the lease is held");

                Assert.That(storage.TryGetIndexState(subject.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Retiring), "the range waits for the reader");
                using (StorageRun run = StorageRun.Execute(storage, adapter, TiltedPlane()))
                {
                    AssertVerifierPasses(run.Run, "cut while retiring");
                }

                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput second), Is.False, "no new acquisition while retiring");
                Assert.That(second, Is.Null);

                adapter.Dispose();

                Assert.That(storage.TryGetIndexState(subject.indexRange, out state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "the returned lease frees the range");
                Assert.That(adapter.TryGetInput(TiltedPlane(), out _), Is.False, "no input after disposal");
                Assert.DoesNotThrow(adapter.Dispose, "disposing again does nothing");
                Assert.That(storage.TryGetIndexState(subject.indexRange, out state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "and does not release a second lease");
            }
        }

        [Test]
        public void GeometriesTheStorageDoesNotOwn_AreRefusedWithoutLeavingALease()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage(indexCapacity: IndexCount, descriptorCapacity: 1))
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry stale = Append(storage, prepared);
                VpStoredGeometry foreign = Append(other, prepared);
                Assert.That(storage.TryRetireIndices(stale.indexRange), Is.True, "retire so the descriptor is registered again");
                VpStoredGeometry current = Append(storage, prepared);
                Mesh quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                using (VpCpuGeometryStorage meshStorage = NewStorage())
                {
                    Assert.That(meshStorage.TryAppend(quad, out VpStoredGeometry withoutTopology), Is.True, "append a Unity mesh");

                    foreach ((VpCpuGeometryStorage target, VpStoredGeometry geometry, string label) in new[]
                             {
                                 (storage, default(VpStoredGeometry), "default"),
                                 (storage, foreign, "foreign"),
                                 (storage, stale, "stale"),
                                 (meshStorage, withoutTopology, "no topology"),
                                 (storage, new VpStoredGeometry(), "empty"),
                             })
                    {
                        Assert.That(VpStorageCutInput.TryAcquire(target, geometry, out VpStorageCutInput refused), Is.False, label);
                        Assert.That(refused, Is.Null, label + " adapter");
                    }

                    Assert.That(VpStorageCutInput.TryAcquire(null, current, out VpStorageCutInput nullStorage), Is.False, "null storage");
                    Assert.That(nullStorage, Is.Null);

                    // no refusal left a lease behind: a range with a reader could not go straight to Free
                    Assert.That(meshStorage.TryRetireIndices(withoutTopology.indexRange), Is.True);
                    Assert.That(meshStorage.TryGetIndexState(withoutTopology.indexRange, out VpIndexRangeState meshState, out _, out _), Is.True);
                    Assert.That(meshState, Is.EqualTo(VpIndexRangeState.Free), "the mesh geometry never had a reader");
                }

                Assert.That(storage.TryRetireIndices(current.indexRange), Is.True);
                Assert.That(storage.TryGetIndexState(current.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "the current geometry never had a reader either");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AGeometryWhollyOnOneSide_ReusesTheInputInViewNumberingWithoutWritingOutput(bool positiveSide)
        {
            Prepared prepared = BuildPrepared();
            // the shape spans y in [0, 1.3]; a plane far below leaves it all positive, far above all negative
            float4 plane = SyntheticGeometry.Plane(new float3(0, 1, 0), new float3(0, positiveSide ? -3f : 3f, 0));
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, prepared);
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(storage.TryGetIndexState(subject.indexRange, out _, out int physicalStart, out _), Is.True);
                Assert.That(physicalStart, Is.EqualTo(IndexCount), "the physical index start is not 0");

                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput adapter), Is.True);
                using (adapter)
                using (StorageRun run = StorageRun.Execute(storage, adapter, plane))
                {
                    AssertVerifierPasses(run.Run, "whole mesh on one side");

                    MeshCutSideResult reused = positiveSide ? run.Result.positive : run.Result.negative;
                    MeshCutSideResult other = positiveSide ? run.Result.negative : run.Result.positive;
                    Assert.That(reused.reusesInput, Is.Not.Zero, "the input is reused unchanged");
                    Assert.That(other.reusesInput, Is.Zero, "the other side is empty");
                    Assert.That(reused.indexStart, Is.Zero, "the reused range is in the view's numbering, not at the physical start");
                    Assert.That(reused.indexCount, Is.EqualTo(IndexCount));
                    Assert.That(run.Result.crossingTriangles, Is.Zero, "nothing crosses");

                    // the ranges come back exactly as they went in, and the empty side holds nothing
                    int reusedBase = positiveSide ? 0 : adapter.RangeCount;
                    int emptyBase = positiveSide ? adapter.RangeCount : 0;
                    Assert.That(run.Run.OutputRanges[reusedBase + 0].indexStart, Is.Zero);
                    Assert.That(run.Run.OutputRanges[reusedBase + 0].indexCount, Is.EqualTo(SideIndices));
                    Assert.That(run.Run.OutputRanges[reusedBase + 1].indexStart, Is.EqualTo((uint)SideIndices));
                    Assert.That(run.Run.OutputRanges[reusedBase + 1].indexCount, Is.EqualTo(EndIndices));
                    Assert.That(run.Run.OutputRanges[emptyBase + 0], Is.EqualTo(default(MeshCutIndexRange)));
                    Assert.That(run.Run.OutputRanges[emptyBase + 1], Is.EqualTo(default(MeshCutIndexRange)));

                    // reading those ranges reproduces the geometry's own indices, submesh by submesh
                    int written = 0;
                    for (int s = 0; s < adapter.RangeCount; s++)
                    {
                        MeshCutIndexRange range = run.Run.OutputRanges[reusedBase + s];
                        for (int i = 0; i < range.indexCount; i++)
                        {
                            uint expected = prepared.Indices[written + i] + (uint)subject.vertexStart;
                            Assert.That(run.InputIndices[range.indexStart + i], Is.EqualTo(expected), "submesh " + s + " index " + i);
                        }

                        Assert.That(range.indexCount, Is.EqualTo(prepared.Submeshes[s].indexCount), "submesh " + s + " count");
                        written += range.indexCount;
                    }

                    Assert.That(written, Is.EqualTo(IndexCount), "every index is covered once");

                    // nothing was written into the output reservation
                    Assert.That(run.Result.newVertexCount, Is.Zero, "no new vertices");
                    Assert.That(run.Result.newIndexCount, Is.Zero, "no new indices");
                    Assert.That(run.RawNewIndices.All(i => i == StorageRun.IndexSentinel), Is.True, "the index reservation is untouched");
                }
            }
        }

        [Test]
        public void AnEmptyGeometry_IsRefusedWithoutTakingALease()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(
                    storage.TryAppendCuttable(Array.Empty<VpRenderVertex>(), Array.Empty<uint>(), Array.Empty<int>(), 0, Array.Empty<VpGeometrySubmesh>(), out VpStoredGeometry empty, out VpCutInputVerdict verdict),
                    Is.True,
                    "the storage still stores an empty geometry, and the gate has no emptiness rule");
                Assert.That(verdict.Accepted, Is.True, verdict.ToString());

                Assert.That(VpStorageCutInput.TryAcquire(storage, empty, out VpStorageCutInput adapter), Is.False, "the adapter refuses it");
                Assert.That(adapter, Is.Null);

                // no lease was taken: a range with a reader could not go straight to Free
                Assert.That(storage.TryRetireIndices(empty.indexRange), Is.True, "retire");
                Assert.That(storage.TryGetIndexState(empty.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "the empty geometry never had a reader");
            }
        }

        [Test]
        public void ReusedIndexSpace_IsCutAtItsOwnVertexAndIndexOffsets()
        {
            Prepared prepared = BuildPrepared();
            float4 plane = TiltedPlane();
            // room for exactly two geometries' indices, so the third reuses the first one's space while vertices keep appending
            using (VpCpuGeometryStorage storage = NewStorage(indexCapacity: 2 * IndexCount, descriptorCapacity: 2))
            {
                VpStoredGeometry first = Append(storage, prepared);
                Append(storage, prepared);
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True, "retire the first index range");

                VpStoredGeometry third = Append(storage, prepared);

                Assert.That(third.vertexStart, Is.EqualTo(2 * RenderVertices), "vertices keep appending");
                Assert.That(storage.TryGetIndexState(third.indexRange, out _, out int indexStart, out _), Is.True);
                Assert.That(indexStart, Is.Zero, "while its indices sit at the reused start");

                Assert.That(VpStorageCutInput.TryAcquire(storage, third, out VpStorageCutInput adapter), Is.True);
                using (adapter)
                using (StorageRun run = StorageRun.Execute(storage, adapter, plane))
                {
                    AssertVerifierPasses(run.Run, "reused index space");
                    Assert.That(run.Result.crossingTriangles, Is.GreaterThan(0));
                    Assert.That(adapter.IndexViewLength, Is.EqualTo(IndexCount));
                }
            }
        }
    }
}
