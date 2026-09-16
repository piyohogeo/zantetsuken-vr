using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Cutting a geometry a <see cref="VpCpuGeometryStorage"/> owns and placing the result in that same storage: the
    /// kernel writes its new vertices, their topology ids and both sides' indices straight into the storage's reserved
    /// space, and a successful run publishes the used part as two geometries that share those vertices and own their
    /// index ranges separately (DESIGN 4.5.6). The cut output is checked with the existing verifier, over a copy of the
    /// storage's arrays that this test assembles; the product path itself verifies nothing.
    /// </summary>
    public unsafe class VpStorageCutOutputTests
    {
        // An asymmetric closed hexahedron: 8 control points, one set of render vertices per face (so every edge is an
        // attribute seam), two submeshes whose material indices are not their ordinals.
        private const int ControlPoints = 8;
        private const int RenderVertices = 24;
        private const int IndexCount = 36;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int QuadIndexCount = 6;

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
            public int TopologyVertexCount;
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
                            uv0 = uv[k] + new float2(f * 0.013f, f * 0.021f),
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
                TopologyVertexCount = ControlPoints,
            };
        }

        /// <summary>A small separate geometry, to take vertex and index space and to prove freed space is reusable.</summary>
        private static Prepared BuildQuad(float3 offset)
        {
            var vertices = new VpRenderVertex[4];
            var topology = new int[4];
            for (int k = 0; k < 4; k++)
            {
                vertices[k] = new VpRenderVertex
                {
                    position = offset + new float3(k == 1 || k == 2 ? 1f : 0f, 0f, k >= 2 ? 1f : 0f),
                    normal = new float3(0, 1, 0),
                    uv0 = new float2(k == 1 || k == 2 ? 1f : 0f, k >= 2 ? 1f : 0f),
                };
                topology[k] = k;
            }

            return new Prepared
            {
                Vertices = vertices,
                Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
                TopologyOfVertex = topology,
                Submeshes = new[] { new VpGeometrySubmesh(0, QuadIndexCount, 0) },
                TopologyVertexCount = 4,
            };
        }

        private static VpCpuGeometryStorage NewStorage(
            int vertexCapacity = 2048,
            int indexCapacity = 8192,
            int descriptorCapacity = 32,
            int submeshCapacity = 128,
            int vertexBlockCapacity = 128)
        {
            return new VpCpuGeometryStorage(vertexCapacity, indexCapacity, descriptorCapacity, submeshCapacity, vertexBlockCapacity, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendPrepared(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        /// <summary>The plane of the ordinary cut: tilted and off-centre, so no vertex lies exactly on it.</summary>
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

        /// <summary>A second plane, for cutting a child again.</summary>
        private static float4 SecondPlane()
        {
            return SyntheticGeometry.Plane(new float3(0.81f, -0.23f, 0.54f), new float3(0.0313f, 0.6217f, -0.0119f));
        }

        /// <summary>The node correspondence of one run, which the verifier needs and only a caller can ask for.</summary>
        private sealed class NodeCorrespondence
        {
            public long[] Keys = Array.Empty<long>();
            public float[] Params = Array.Empty<float>();
        }

        private static bool Cut(VpCpuGeometryStorage storage, VpStoredGeometry geometry, float4 plane, out VpStorageCutResult result)
        {
            return Cut(storage, geometry, plane, default, null, out result);
        }

        private static bool Cut(VpCpuGeometryStorage storage, VpStoredGeometry geometry, float4 plane, in VpStorageCutOptions options, out VpStorageCutResult result)
        {
            return Cut(storage, geometry, plane, options, null, out result);
        }

        private static bool Cut(
            VpCpuGeometryStorage storage,
            VpStoredGeometry geometry,
            float4 plane,
            in VpStorageCutOptions options,
            NodeCorrespondence nodes,
            out VpStorageCutResult result)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, "acquire input");
            using (input)
            {
                return Execute(storage, input, plane, options, nodes, out result);
            }
        }

        /// <summary>
        /// One run through the product path. When this test means to verify the result it asks for the node
        /// correspondence as well, in arrays it owns and copies out of; the product path itself asks for none of it.
        /// </summary>
        private static bool Execute(
            VpCpuGeometryStorage storage,
            VpStorageCutInput input,
            float4 plane,
            in VpStorageCutOptions options,
            NodeCorrespondence nodes,
            out VpStorageCutResult result)
        {
            if (nodes == null)
            {
                return VpStorageCut.TryExecute(storage, input, plane, options, out result);
            }

            Assert.That(input.TryGetInput(plane, out MeshCutInput kernelInput), Is.True, "kernel input");
            var capacity = new MeshCutCapacity();
            MeshCutKernel.QueryCapacity(in kernelInput, ref capacity);
            int nodeCapacity = math.max(1, 2 * capacity.crossingTriangles);
            var keys = new NativeArray<long>(nodeCapacity, Allocator.Persistent);
            var parameters = new NativeArray<float>(nodeCapacity, Allocator.Persistent);
            try
            {
                VpStorageCutOptions withNodes = options;
                withNodes.nodeEdgeKeys = keys;
                withNodes.nodeParams = parameters;
                bool ok = VpStorageCut.TryExecute(storage, input, plane, withNodes, out result);
                Assert.That(result.kernel.nodeCount, Is.LessThanOrEqualTo(nodeCapacity), "the node correspondence fits");
                int written = math.clamp(result.kernel.nodeCount, 0, nodeCapacity);
                nodes.Keys = keys.GetSubArray(0, written).ToArray();
                nodes.Params = parameters.GetSubArray(0, written).ToArray();
                return ok;
            }
            finally
            {
                keys.Dispose();
                parameters.Dispose();
            }
        }

        private static uint[] ReadIndices(VpCpuGeometryStorage storage, VpIndexRangeHandle handle)
        {
            Assert.That(storage.TryAcquireIndexReadLease(handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "read lease");
            uint[] indices = view.ToArray();
            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release lease");
            return indices;
        }

        private static int PhysicalStart(VpCpuGeometryStorage storage, VpIndexRangeHandle handle)
        {
            Assert.That(storage.TryGetIndexState(handle, out _, out int start, out _), Is.True, "index state");
            return start;
        }

        /// <summary>Copies one published range into the pool at the position the storage keeps it at.</summary>
        private static void CopyRangeInto(VpCpuGeometryStorage storage, VpIndexRangeHandle handle, PoolArrays pool)
        {
            uint[] indices = ReadIndices(storage, handle);
            Array.Copy(indices, 0, pool.Indices, PhysicalStart(storage, handle), indices.Length);
        }

        private static int PublishedCount(VpCpuGeometryStorage storage, VpIndexRangeHandle handle)
        {
            Assert.That(storage.TryGetIndexState(handle, out _, out _, out int count), Is.True, "index state");
            return count;
        }

        /// <summary>One geometry of the storage as the verifier's model, over a pool this test assembles.</summary>
        private static CutGeometry AsCutGeometry(VpCpuGeometryStorage storage, VpStoredGeometry geometry, PoolArrays pool, int indexBase, string name)
        {
            var g = new CutGeometry { Name = name, Pool = pool, TopologyVertexCount = geometry.topologyVertexCount };
            Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True, name + " submeshes");
            foreach (VpGeometrySubmesh submesh in submeshes)
            {
                if (submesh.indexCount > 0)
                {
                    g.Ranges.Add(new MeshCutIndexRange { indexStart = (uint)(indexBase + submesh.indexOffset), indexCount = submesh.indexCount });
                }
            }

            Assert.That(storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True, name + " blocks");
            NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
            foreach (VpGeometryVertexBlock block in blocks)
            {
                var ids = new int[block.vertexCount];
                for (int v = 0; v < block.vertexCount; v++)
                {
                    ids[v] = topology[block.vertexStart + v];
                }

                g.Topology.Add(new TopologyBlock { VertexBase = (uint)block.vertexStart, TopologyVertex = ids });
            }

            return g;
        }

        /// <summary>
        /// The run the existing verifier understands, assembled from what the storage now holds. The pool mirrors the
        /// storage's own index buffer, so every range sits where the storage really put it and the side starts the
        /// kernel reported are the same numbers.
        /// </summary>
        private static CutRun BuildCutRun(VpCpuGeometryStorage storage, VpStoredGeometry parent, in VpStorageCutResult result, float4 plane, NodeCorrespondence nodes)
        {
            var pool = new PoolArrays
            {
                Vertices = storage.Vertices.ToArray(),
                Indices = new uint[storage.IndexCapacity],
            };
            CopyRangeInto(storage, parent.indexRange, pool);
            if (result.positive.IsProduced)
            {
                CopyRangeInto(storage, result.positive.geometry.indexRange, pool);
            }

            if (result.negative.IsProduced)
            {
                CopyRangeInto(storage, result.negative.geometry.indexRange, pool);
            }

            CutGeometry input = AsCutGeometry(storage, parent, pool, PhysicalStart(storage, parent.indexRange), "parent");
            var run = new CutRun
            {
                Input = input,
                Plane = plane,
                Result = result.kernel,
                NewVertexCount = result.kernel.newVertexCount,
                TopologyBase = parent.topologyVertexCount,
            };

            VpStoredGeometry shared = result.positive.IsProduced ? result.positive.geometry : result.negative.geometry;
            run.NewVertexBase = (uint)shared.vertexStart;
            var newTopology = new int[result.kernel.newVertexCount];
            NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
            for (int v = 0; v < newTopology.Length; v++)
            {
                newTopology[v] = topology[shared.vertexStart + v];
            }

            run.NewVertexTopology = newTopology;
            run.NodeKeys = nodes.Keys;
            run.NodeParams = nodes.Params;
            run.Positive = result.positive.IsProduced
                ? AsCutGeometry(storage, result.positive.geometry, pool, PhysicalStart(storage, result.positive.geometry.indexRange), "parent+")
                : null;
            run.Negative = result.negative.IsProduced
                ? AsCutGeometry(storage, result.negative.geometry, pool, PhysicalStart(storage, result.negative.geometry.indexRange), "parent-")
                : null;

            int rangeCount = input.Ranges.Count;
            var outputRanges = new List<MeshCutIndexRange>();
            AddOutputRanges(outputRanges, run.Positive, rangeCount);
            AddOutputRanges(outputRanges, run.Negative, rangeCount);
            run.OutputRanges = outputRanges.ToArray();
            return run;
        }

        private static void AddOutputRanges(List<MeshCutIndexRange> into, CutGeometry side, int rangeCount)
        {
            for (int r = 0; r < rangeCount; r++)
            {
                into.Add(side != null && r < side.Ranges.Count ? side.Ranges[r] : default);
            }
        }

        private static void AssertVerifierPasses(CutRun run, string label)
        {
            VerificationReport report = MeshCutVerifier.Verify(run);
            Assert.That(report.Passed, Is.True, label + ": " + report);
        }

        /// <summary>A minimal Unity mesh, only so that the mesh append path can be offered and refused.</summary>
        private static Mesh QuadMesh()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) });
            mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        /// <summary>The geometry's own mapping, to show that a refused operation left it alone.</summary>
        private static int[] TopologyOf(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetTopology(geometry, out NativeArray<int>.ReadOnly topology, out _), Is.True, "topology");
            return topology.ToArray();
        }

        /// <summary>A requirement to resume with never asks for less than was already tried, and asks for more of at least one figure.</summary>
        private static void AssertRequirementGrew(VpStorageCutOptions before, VpStorageCutOptions after, string label)
        {
            Assert.That(after.newVertexCapacity, Is.GreaterThanOrEqualTo(before.newVertexCapacity), label + " vertex figure");
            Assert.That(after.newIndexCapacity, Is.GreaterThanOrEqualTo(before.newIndexCapacity), label + " index figure");
            Assert.That(after.scratchBytes, Is.GreaterThanOrEqualTo(before.scratchBytes), label + " scratch figure");
            bool grew = after.newVertexCapacity > before.newVertexCapacity
                || after.newIndexCapacity > before.newIndexCapacity
                || after.scratchBytes > before.scratchBytes;
            Assert.That(grew, Is.True, label + ": the requirement moved forward");
        }

        private static int[] MaterialIndices(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True, "submeshes");
            return submeshes.ToArray().Select(s => s.materialIndex).ToArray();
        }

        [Test]
        public void ACutAtNonZeroStarts_WritesBothSidesIntoTheStorageAndPassesTheVerifier()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(subject.vertexStart, Is.Not.Zero, "the vertices do not start at 0");
                Assert.That(PhysicalStart(storage, subject.indexRange), Is.Not.Zero, "the indices do not start at 0");

                var nodes = new NodeCorrespondence();
                Assert.That(Cut(storage, subject, TiltedPlane(), default, nodes, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(result.kernel.crossingTriangles, Is.GreaterThan(0), "the plane really cuts");
                Assert.That(result.positive.IsProduced, Is.True, "positive produced");
                Assert.That(result.negative.IsProduced, Is.True, "negative produced");

                AssertVerifierPasses(BuildCutRun(storage, subject, in result, TiltedPlane(), nodes), "storage cut");
            }
        }

        [Test]
        public void TheAppendedVerticesAndTopology_AreWrittenOnceAndSharedByBothSides()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                int vertexCountBefore = storage.VertexCount;
                int blockCountBefore = storage.VertexBlockCount;

                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");
                VpStoredGeometry positive = result.positive.geometry;
                VpStoredGeometry negative = result.negative.geometry;

                Assert.That(result.kernel.newVertexCount, Is.GreaterThan(0), "the cut appends vertices");
                Assert.That(storage.VertexCount - vertexCountBefore, Is.EqualTo(result.kernel.newVertexCount), "appended exactly once");
                Assert.That(positive.vertexStart, Is.EqualTo(vertexCountBefore));
                Assert.That(negative.vertexStart, Is.EqualTo(vertexCountBefore), "both sides name the same appended vertices");
                Assert.That(negative.vertexCount, Is.EqualTo(positive.vertexCount));

                // one shared block list: the parent's blocks and the appended one
                Assert.That(positive.blockStart, Is.EqualTo(negative.blockStart), "the block list is shared");
                Assert.That(positive.blockCount, Is.EqualTo(subject.blockCount + 1));
                Assert.That(negative.blockCount, Is.EqualTo(positive.blockCount));
                Assert.That(storage.VertexBlockCount - blockCountBefore, Is.EqualTo(positive.blockCount), "written once");

                Assert.That(storage.TryGetVertexBlocks(positive, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out int topologyVertexCount), Is.True);
                Assert.That(blocks.Length, Is.EqualTo(2));
                Assert.That(blocks[0].vertexStart, Is.EqualTo(subject.vertexStart), "the inherited block");
                Assert.That(blocks[0].vertexCount, Is.EqualTo(RenderVertices));
                Assert.That(blocks[1].vertexStart, Is.EqualTo(vertexCountBefore), "the appended block");
                Assert.That(blocks[1].vertexCount, Is.EqualTo(result.kernel.newVertexCount));
                Assert.That(topologyVertexCount, Is.EqualTo(result.kernel.newTopologyVertexCount), "the child's id space");

                // the inherited ids are the parent's own, and the appended ids are the kernel's, above the parent's space
                NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
                for (int v = 0; v < RenderVertices; v++)
                {
                    Assert.That(topology[subject.vertexStart + v], Is.EqualTo(prepared.TopologyOfVertex[v]), "inherited id " + v);
                }

                for (int v = 0; v < result.kernel.newVertexCount; v++)
                {
                    int id = topology[vertexCountBefore + v];
                    Assert.That(id, Is.GreaterThanOrEqualTo(ControlPoints), "appended id " + v + " is new");
                    Assert.That(id, Is.LessThan(result.kernel.newTopologyVertexCount));
                }

                // a geometry of several blocks is not offered as one contiguous mapping
                Assert.That(storage.TryGetTopology(positive, out _, out _), Is.False, "no single-block mapping for a child");
                Assert.That(storage.TryGetTopology(subject, out _, out _), Is.True, "the parent still has one");
            }
        }

        [Test]
        public void TheTwoSides_SitInOneReservationInOrderAndTheirUnusedTailIsReusable()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");

                int positiveStart = PhysicalStart(storage, result.positive.geometry.indexRange);
                int negativeStart = PhysicalStart(storage, result.negative.geometry.indexRange);
                int n0 = PublishedCount(storage, result.positive.geometry.indexRange);
                int n1 = PublishedCount(storage, result.negative.geometry.indexRange);
                Assert.That(positiveStart, Is.EqualTo(IndexCount), "the reservation follows the parent's range");
                Assert.That(n0, Is.EqualTo(result.kernel.positive.indexCount));
                Assert.That(n1, Is.EqualTo(result.kernel.negative.indexCount));
                Assert.That(negativeStart, Is.EqualTo(positiveStart + n0), "negative follows positive with no gap");

                // the unused tail of the reservation came back: the next append starts right after the negative side
                VpStoredGeometry next = Append(storage, BuildQuad(new float3(9, 9, 9)));
                Assert.That(PhysicalStart(storage, next.indexRange), Is.EqualTo(negativeStart + n1), "the tail is reusable");
            }
        }

        [Test]
        public void EachSide_KeepsTheInputSubmeshOrderAndMaterialMapping()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");

                foreach (VpStorageCutSide side in new[] { result.positive, result.negative })
                {
                    Assert.That(side.IsProduced, Is.True);
                    Assert.That(side.geometry.submeshCount, Is.EqualTo(prepared.Submeshes.Length), "one range per input submesh");
                    Assert.That(MaterialIndices(storage, side.geometry), Is.EqualTo(new[] { SideMaterial, EndMaterial }), "material mapping");
                    Assert.That(storage.TryGetSubmeshes(side.geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
                    int covered = 0;
                    foreach (VpGeometrySubmesh submesh in submeshes)
                    {
                        Assert.That(submesh.indexOffset, Is.EqualTo(covered), "offsets are cumulative inside the side's own range");
                        Assert.That(submesh.indexCount % 3, Is.Zero, "whole triangles");
                        covered += submesh.indexCount;
                    }

                    Assert.That(covered, Is.EqualTo(PublishedCount(storage, side.geometry.indexRange)), "the submeshes cover the side");
                }

                // no cap-only submesh was invented
                Assert.That(result.positive.geometry.submeshCount + result.negative.geometry.submeshCount, Is.EqualTo(4));
            }
        }

        [Test]
        public void AChild_StaysReadableAfterItsParentIsRetired()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");
                VpStoredGeometry positive = result.positive.geometry;

                Assert.That(storage.TryRetireIndices(subject.indexRange), Is.True, "retire the parent");
                Assert.That(storage.TryGetIndexState(subject.indexRange, out VpIndexRangeState parentState, out _, out _), Is.True);
                Assert.That(parentState, Is.EqualTo(VpIndexRangeState.Free), "no reader was left on the parent");
                Assert.That(storage.TryGetSubmeshes(subject, out _), Is.False, "the parent's own metadata is gone");

                // the child still names the parent's vertices and mapping entries, which were never copied
                Assert.That(storage.TryGetVertexBlocks(positive, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True, "child blocks");
                Assert.That(blocks[0].vertexStart, Is.EqualTo(subject.vertexStart));
                NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
                for (int v = 0; v < RenderVertices; v++)
                {
                    Assert.That(topology[blocks[0].vertexStart + v], Is.EqualTo(prepared.TopologyOfVertex[v]), "inherited id " + v);
                }

                Assert.That(storage.TryGetSubmeshes(positive, out _), Is.True, "and its own metadata reads");
                Assert.That(VpStorageCutInput.TryAcquire(storage, positive, out VpStorageCutInput input), Is.True, "and it can still be read for a cut");
                input.Dispose();
            }
        }

        [Test]
        public void RetiringOneSideAndReusingItsSpace_LeavesTheOtherUntouched()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");
                VpStoredGeometry positive = result.positive.geometry;
                VpStoredGeometry negative = result.negative.geometry;

                int positiveStart = PhysicalStart(storage, positive.indexRange);
                int negativeStart = PhysicalStart(storage, negative.indexRange);
                int negativeCount = PublishedCount(storage, negative.indexRange);
                uint[] negativeBefore = ReadIndices(storage, negative.indexRange);
                int[] negativeMaterials = MaterialIndices(storage, negative);

                Assert.That(storage.TryRetireIndices(positive.indexRange), Is.True, "retire the positive side");
                Assert.That(storage.TryGetIndexState(positive.indexRange, out VpIndexRangeState positiveState, out _, out _), Is.True);
                Assert.That(positiveState, Is.EqualTo(VpIndexRangeState.Free));

                // its space comes back and is handed out again, before the negative side's own range
                VpStoredGeometry reuser = Append(storage, BuildQuad(new float3(3, 3, 3)));
                Assert.That(PhysicalStart(storage, reuser.indexRange), Is.EqualTo(positiveStart), "the freed space is reused");

                Assert.That(storage.TryGetIndexState(negative.indexRange, out VpIndexRangeState negativeState, out int startAfter, out int countAfter), Is.True);
                Assert.That(negativeState, Is.EqualTo(VpIndexRangeState.Published), "the other side is untouched");
                Assert.That(startAfter, Is.EqualTo(negativeStart));
                Assert.That(countAfter, Is.EqualTo(negativeCount));
                Assert.That(ReadIndices(storage, negative.indexRange), Is.EqualTo(negativeBefore), "its indices are unchanged");
                Assert.That(MaterialIndices(storage, negative), Is.EqualTo(negativeMaterials));
                Assert.That(storage.TryRetireIndices(positive.indexRange), Is.False, "retiring the retired side again does nothing");
            }
        }

        [Test]
        public void AChild_CanBeCutAgainThroughItsInheritedAndAppendedTopology()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult first), Is.True, "first cut");
                VpStoredGeometry child = first.positive.geometry;

                Assert.That(VpStorageCutInput.TryAcquire(storage, child, out VpStorageCutInput input), Is.True, "acquire the child");
                using (input)
                {
                    Assert.That(input.TopologyRangeCount, Is.EqualTo(child.blockCount), "one topology range per block");
                    Assert.That(input.TopologyRangeCount, Is.EqualTo(2), "the inherited block and the appended one");
                    Assert.That(input.TopologyVertexCount, Is.EqualTo(first.kernel.newTopologyVertexCount));
                    var nodes = new NodeCorrespondence();
                    Assert.That(Execute(storage, input, SecondPlane(), default, nodes, out VpStorageCutResult second), Is.True, "second cut");
                    Assert.That(second.status, Is.EqualTo(VpStorageCutStatus.Ok));
                    Assert.That(second.kernel.crossingTriangles, Is.GreaterThan(0), "the second plane really cuts");
                    Assert.That(second.positive.IsProduced && second.negative.IsProduced, Is.True, "both grandchildren");

                    VpStoredGeometry grandchild = second.positive.geometry;
                    Assert.That(grandchild.blockCount, Is.EqualTo(child.blockCount + (second.kernel.newVertexCount > 0 ? 1 : 0)));
                    Assert.That(storage.TryGetVertexBlocks(grandchild, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True);
                    Assert.That(blocks[0].vertexStart, Is.EqualTo(subject.vertexStart), "still names the original vertices");
                    Assert.That(blocks[1].vertexStart, Is.EqualTo(child.vertexStart), "and the first cut's vertices");

                    AssertVerifierPasses(BuildCutRun(storage, child, in second, SecondPlane(), nodes), "second cut");
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AGeometryWhollyOnOneSide_BorrowsTheInputAndCommitsNothing(bool positiveSide)
        {
            Prepared prepared = BuildPrepared();
            // the shape spans y in [0, 1.3]; a plane far below leaves it all positive, far above all negative
            float4 plane = SyntheticGeometry.Plane(new float3(0, 1, 0), new float3(0, positiveSide ? -3f : 3f, 0));
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;
                int indexStart = PhysicalStart(storage, subject.indexRange);

                Assert.That(Cut(storage, subject, plane, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(result.kernel.crossingTriangles, Is.Zero, "nothing crosses");

                VpStorageCutSide kept = positiveSide ? result.positive : result.negative;
                VpStorageCutSide other = positiveSide ? result.negative : result.positive;
                Assert.That(kept.IsBorrowed, Is.True, "the side that keeps it all borrows the input");
                Assert.That(kept.IsProduced, Is.False, "and is not a new owner");
                Assert.That(other.IsEmpty, Is.True, "the empty side gets nothing");
                Assert.That(other.geometry.indexRange, Is.EqualTo(default(VpIndexRangeHandle)), "no dummy range");

                // the borrowed side is the input geometry itself, still owned by whoever owned it
                Assert.That(kept.geometry.indexRange, Is.EqualTo(subject.indexRange));
                Assert.That(PhysicalStart(storage, subject.indexRange), Is.EqualTo(indexStart));
                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "no vertex was committed");
                Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), "no submesh was committed");
                Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount), "no block was committed");

                // nothing was reserved either: the next append takes the space right after the input
                VpStoredGeometry next = Append(storage, BuildQuad(new float3(1, 1, 1)));
                Assert.That(PhysicalStart(storage, next.indexRange), Is.EqualTo(indexStart + IndexCount), "no reservation was left behind");
            }
        }

        [Test]
        public void AStorageThatCannotHoldTheOutput_PublishesNeitherSide()
        {
            Prepared prepared = BuildPrepared();
            float4 plane = TiltedPlane();

            // one descriptor for the parent and one for the reservation, so the split has none for the second side
            AssertNothingIsPublished(prepared, plane, NewStorage(descriptorCapacity: 2), "descriptors");

            // three free submesh descriptors where the two sides need four
            AssertNothingIsPublished(prepared, plane, NewStorage(submeshCapacity: prepared.Submeshes.Length + 3), "submesh metadata");

            // one free block where the children's shared list needs two
            AssertNothingIsPublished(prepared, plane, NewStorage(vertexBlockCapacity: 2), "block metadata");

            // no room for the vertices the cut appends
            AssertNothingIsPublished(prepared, plane, NewStorage(vertexCapacity: RenderVertices), "vertices");

            // no room for the index reservation
            AssertNothingIsPublished(prepared, plane, NewStorage(indexCapacity: IndexCount + 8), "index space");
        }

        private static void AssertNothingIsPublished(Prepared prepared, float4 plane, VpCpuGeometryStorage storage, string label)
        {
            using (storage)
            {
                VpStoredGeometry subject = Append(storage, prepared);
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;

                Assert.That(Cut(storage, subject, plane, out VpStorageCutResult result), Is.False, label + ": the cut fails");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.StorageCapacity), label + ": status");
                Assert.That(result.positive.IsProduced, Is.False, label + ": no positive side");
                Assert.That(result.negative.IsProduced, Is.False, label + ": no negative side");
                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), label + ": no vertex committed");
                Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), label + ": no submesh committed");
                Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount), label + ": no block committed");

                // the parent is untouched and the space of the failed attempt is free again
                Assert.That(storage.TryGetIndexState(subject.indexRange, out VpIndexRangeState state, out _, out int count), Is.True, label + ": parent state");
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Published), label + ": the parent stays published");
                Assert.That(count, Is.EqualTo(IndexCount), label + ": the parent's range is unchanged");
                Assert.That(storage.TryGetSubmeshes(subject, out _), Is.True, label + ": the parent still reads");
            }
        }

        [Test]
        public void AReservationTheKernelOutgrows_IsGivenBackAndTheCutReRunToSuccess()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                int vertexCountBefore = storage.VertexCount;
                int submeshCountBefore = storage.SubmeshCount;
                int blockCountBefore = storage.VertexBlockCount;

                // deliberately too small for both the appended vertices and the two sides' indices
                var budget = new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3 };
                var nodes = new NodeCorrespondence();
                Assert.That(Cut(storage, subject, TiltedPlane(), budget, nodes, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(result.attempts, Is.GreaterThan(1), "the first attempt was too small");
                Assert.That(result.attempts, Is.LessThanOrEqualTo(VpStorageCut.MaxAttempts));
                Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "both sides");

                // the attempt that published nothing left nothing behind
                Assert.That(storage.VertexCount - vertexCountBefore, Is.EqualTo(result.kernel.newVertexCount), "vertices committed once");
                Assert.That(storage.SubmeshCount - submeshCountBefore, Is.EqualTo(4), "submeshes committed once");
                Assert.That(storage.VertexBlockCount - blockCountBefore, Is.EqualTo(2), "blocks committed once");

                AssertVerifierPasses(BuildCutRun(storage, subject, in result, TiltedPlane(), nodes), "re-run cut");

                // no lease of the failed attempt is still held: the parent retires without waiting
                Assert.That(storage.TryRetireIndices(subject.indexRange), Is.True, "retire the parent");
                Assert.That(storage.TryGetIndexState(subject.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "no reader was left behind");
            }
        }

        [Test]
        public void AScratchBlockTheKernelOutgrows_IsAlsoReRunToSuccess()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                var budget = new VpStorageCutOptions { scratchBytes = 1 };
                Assert.That(Cut(storage, subject, TiltedPlane(), budget, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(result.attempts, Is.GreaterThan(1), "the first scratch block was too small");
                Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "both sides");
            }
        }

        [Test]
        public void StaleForeignAndRepeatedUse_IsRefusedWithoutChangingAnything()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Append(other, prepared);

                // a disposed adapter builds no input
                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput disposed), Is.True);
                disposed.Dispose();
                Assert.That(VpStorageCut.TryExecute(storage, disposed, TiltedPlane(), out VpStorageCutResult afterDispose), Is.False, "disposed adapter");
                Assert.That(afterDispose.status, Is.EqualTo(VpStorageCutStatus.InvalidInput));
                disposed.Dispose();

                // an adapter of one storage is not a geometry of another
                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput input), Is.True);
                using (input)
                {
                    Assert.That(VpStorageCut.TryExecute(other, input, TiltedPlane(), out VpStorageCutResult foreign), Is.False, "foreign storage");
                    Assert.That(foreign.status, Is.EqualTo(VpStorageCutStatus.InvalidInput));
                    Assert.That(other.VertexBlockCount, Is.EqualTo(1), "the foreign storage is untouched");
                }

                Assert.That(VpStorageCut.TryExecute(null, null, TiltedPlane(), out VpStorageCutResult nulls), Is.False, "null arguments");
                Assert.That(nulls.status, Is.EqualTo(VpStorageCutStatus.InvalidInput));

                // a stale geometry, whose range was retired and its descriptor registered again
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");
                VpStoredGeometry positive = result.positive.geometry;
                Assert.That(storage.TryRetireIndices(positive.indexRange), Is.True, "retire");
                Assert.That(storage.TryRetireIndices(positive.indexRange), Is.False, "retiring twice does nothing");
                Assert.That(VpStorageCutInput.TryAcquire(storage, positive, out VpStorageCutInput stale), Is.False, "a retired side is not an input");
                Assert.That(stale, Is.Null);
                Assert.That(storage.TryGetSubmeshes(positive, out _), Is.False, "and its metadata is gone");

                // the other side is still whole
                Assert.That(storage.TryGetSubmeshes(result.negative.geometry, out _), Is.True, "the other side still reads");
            }
        }

        [Test]
        public void AReservationIsClosedOnce_AndRefusesEveryLaterUse()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(storage.TryReserveCutOutput(subject, 8, 12, 4, 2, out VpCutOutputReservation reservation), Is.True, "reserve");
                Assert.That(reservation.NewVertexCapacity, Is.EqualTo(8));
                Assert.That(reservation.NewIndexCapacity, Is.EqualTo(12));
                Assert.That(reservation.NewVertexBase, Is.EqualTo((uint)RenderVertices));
                Assert.That(reservation.IsClosed, Is.False);

                Assert.That(storage.TryCancelCutOutput(reservation), Is.True, "cancel");
                Assert.That(reservation.IsClosed, Is.True);
                Assert.That(storage.TryCancelCutOutput(reservation), Is.False, "cancelling twice does nothing");
                Assert.That(
                    storage.TryCommitCutOutput(reservation, 0, ControlPoints, 3, 0, new[] { new VpGeometrySubmesh(0, 3, 0) }, 1, 0, out _, out _),
                    Is.False,
                    "committing a cancelled reservation does nothing");
                Assert.That(storage.VertexCount, Is.EqualTo(RenderVertices), "nothing was committed");
                Assert.That(storage.TryCancelCutOutput(null), Is.False, "a null reservation is refused");

                // the cancelled index space is free again
                VpStoredGeometry next = Append(storage, BuildQuad(float3.zero));
                Assert.That(PhysicalStart(storage, next.indexRange), Is.EqualTo(IndexCount), "the cancelled space is reused");
            }
        }

        [Test]
        public void AnOpenReservation_KeepsTheAppendTailsToItselfUntilItIsClosed()
        {
            Prepared prepared = BuildPrepared();
            Prepared other = BuildQuad(new float3(4, 4, 4));
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;
                int[] topologyBefore = TopologyOf(storage, subject);

                Assert.That(storage.TryReserveCutOutput(subject, 16, 24, 4, 2, out VpCutOutputReservation reservation), Is.True, "reserve");

                // while it is open, nothing else may write into the tails it holds
                Assert.That(storage.TryReserveCutOutput(subject, 8, 12, 4, 2, out VpCutOutputReservation second), Is.False, "a second reservation");
                Assert.That(second, Is.Null);
                Assert.That(
                    storage.TryAppendPrepared(other.Vertices, other.Indices, other.TopologyOfVertex, other.TopologyVertexCount, other.Submeshes, out VpStoredGeometry prepAppended),
                    Is.False,
                    "a prepared append");
                Assert.That(prepAppended, Is.EqualTo(default(VpStoredGeometry)));

                Mesh mesh = QuadMesh();
                try
                {
                    Assert.That(storage.TryAppend(mesh, out VpStoredGeometry meshAppended), Is.False, "a mesh append");
                    Assert.That(meshAppended, Is.EqualTo(default(VpStoredGeometry)));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }

                // the refused operations changed nothing at all
                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "no vertex committed");
                Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), "no submesh committed");
                Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount), "no block committed");
                Assert.That(TopologyOf(storage, subject), Is.EqualTo(topologyBefore), "the existing mapping is untouched");
                Assert.That(PublishedCount(storage, subject.indexRange), Is.EqualTo(IndexCount), "and so is its index range");
                Assert.That(reservation.IsClosed, Is.False, "the reservation is still the open one");

                Assert.That(storage.TryCancelCutOutput(reservation), Is.True, "cancel");

                // appending resumes on exactly the tail the reservation had been holding
                VpStoredGeometry resumed = Append(storage, other);
                Assert.That(resumed.vertexStart, Is.EqualTo(vertexCount), "the vertex tail was never taken");
                Assert.That(resumed.blockStart, Is.EqualTo(blockCount), "nor the block tail");
                Assert.That(resumed.submeshStart, Is.EqualTo(submeshCount), "nor the submesh tail");
            }
        }

        [Test]
        public void ACommittedCut_LetsAppendingResumeAfterItsOwnOutput()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");

                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;
                VpStoredGeometry appended = Append(storage, BuildQuad(new float3(7, 7, 7)));
                Assert.That(appended.vertexStart, Is.EqualTo(vertexCount), "the commit released the vertex tail");
                Assert.That(appended.submeshStart, Is.EqualTo(submeshCount), "and the submesh tail");
                Assert.That(appended.blockStart, Is.EqualTo(blockCount), "and the block tail");
                Assert.That(storage.TryGetSubmeshes(result.positive.geometry, out _), Is.True, "the cut output is untouched by the append");
            }
        }

        [Test]
        public void AReservationOfAnotherStorage_IsRefusedAndLeavesBothAlone()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                VpStoredGeometry elsewhere = Append(other, prepared);
                Assert.That(storage.TryReserveCutOutput(subject, 16, 24, 4, 2, out VpCutOutputReservation reservation), Is.True, "reserve");

                Assert.That(other.TryCancelCutOutput(reservation), Is.False, "another storage cannot cancel it");
                Assert.That(
                    other.TryCommitCutOutput(reservation, 0, ControlPoints, 3, 0, new[] { new VpGeometrySubmesh(0, 3, 0) }, 1, 0, out _, out _),
                    Is.False,
                    "nor commit it");
                Assert.That(reservation.IsClosed, Is.False, "and it stays open where it belongs");

                // the other storage never took a reservation of its own, so it still appends
                Assert.That(other.TryReserveCutOutput(elsewhere, 8, 12, 4, 2, out VpCutOutputReservation mine), Is.True, "its own reservation");
                Assert.That(other.TryCancelCutOutput(mine), Is.True, "cancelled");
                Assert.That(storage.TryCancelCutOutput(reservation), Is.True, "the owner can still cancel");
            }
        }

        [Test]
        public void DisposingTheStorage_ClosesAnOpenReservation()
        {
            Prepared prepared = BuildPrepared();
            var storage = NewStorage();
            VpStoredGeometry subject = Append(storage, prepared);
            Assert.That(storage.TryReserveCutOutput(subject, 16, 24, 4, 2, out VpCutOutputReservation reservation), Is.True, "reserve");
            Assert.That(reservation.IsClosed, Is.False);

            storage.Dispose();
            Assert.That(reservation.IsClosed, Is.True, "the reservation dies with the storage");
            storage.Dispose();
        }

        [Test]
        public void TheAttemptLimit_ReportsWhatToReserveAndTheCutResumesFromIt()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;

                Assert.That(VpStorageCutInput.TryAcquire(storage, subject, out VpStorageCutInput input), Is.True, "acquire");
                using (input)
                {
                    Assert.That(input.TryGetInput(TiltedPlane(), out MeshCutInput kernelInput), Is.True, "kernel input");
                    var capacity = new MeshCutCapacity();
                    MeshCutKernel.QueryCapacity(in kernelInput, ref capacity);
                    int nodeCapacity = math.max(1, 2 * capacity.crossingTriangles);

                    // the caller's own record arrays: a run that resumes must still write into these very arrays
                    var keys = new NativeArray<long>(nodeCapacity, Allocator.Persistent);
                    var parameters = new NativeArray<float>(nodeCapacity, Allocator.Persistent);
                    try
                    {
                        // Small enough that the scratch, the vertices and the indices all come up short in turn, and one
                        // attempt per call, so every round really reaches its limit and says what to reserve next.
                        var options = new VpStorageCutOptions
                        {
                            newVertexCapacity = 1,
                            newIndexCapacity = 3,
                            scratchBytes = 1,
                            maxAttempts = 1,
                            nodeEdgeKeys = keys,
                            nodeParams = parameters,
                        };

                        var result = default(VpStorageCutResult);
                        bool finished = false;
                        int rounds = 0;
                        while (rounds < 8 && !finished)
                        {
                            rounds++;
                            string label = "round " + rounds;
                            finished = VpStorageCut.TryExecute(storage, input, TiltedPlane(), options, out result);
                            if (finished)
                            {
                                break;
                            }

                            Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.CapacityRetry), label + " is an unfinished retry, not a failure");
                            Assert.That(result.attempts, Is.EqualTo(1), label + " used its one attempt");
                            Assert.That(result.required.maxAttempts, Is.EqualTo(1), label + " keeps the caller's pacing");
                            Assert.That(result.required.nodeEdgeKeys.Equals(keys), Is.True, label + " keeps the caller's node key array");
                            Assert.That(result.required.nodeParams.Equals(parameters), Is.True, label + " keeps the caller's node parameter array");
                            AssertRequirementGrew(options, result.required, label);

                            // the unfinished round published nothing and left nothing held
                            Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), label + ": no vertex committed");
                            Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), label + ": no submesh committed");
                            Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount), label + ": no block committed");
                            Assert.That(storage.TryReserveCutOutput(subject, 1, 3, 4, 2, out VpCutOutputReservation probe), Is.True, label + ": no reservation is still open");
                            Assert.That(storage.TryCancelCutOutput(probe), Is.True, label + ": probe cancelled");

                            options = result.required;
                        }

                        Assert.That(rounds, Is.GreaterThan(1), "the first round really ran out of attempts");
                        Assert.That(finished, Is.True, "the cut finishes once the requirement is met");
                        Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                        Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "both sides");
                        Assert.That(storage.VertexCount - vertexCount, Is.EqualTo(result.kernel.newVertexCount), "committed exactly once");
                        Assert.That(storage.SubmeshCount - submeshCount, Is.EqualTo(4), "four submesh descriptors");
                        Assert.That(storage.VertexBlockCount - blockCount, Is.EqualTo(2), "two blocks");

                        // the record the caller asked for survived the resumption and is the one the verifier wants
                        Assert.That(result.kernel.nodeCount, Is.GreaterThan(0), "the cut really has intersection nodes");
                        Assert.That(result.kernel.nodeCorrespondenceWritten, Is.Not.Zero, "the resumed run wrote the node correspondence");
                        Assert.That(result.kernel.nodeCount, Is.LessThanOrEqualTo(nodeCapacity), "the record fits");
                        var nodes = new NodeCorrespondence
                        {
                            Keys = keys.GetSubArray(0, result.kernel.nodeCount).ToArray(),
                            Params = parameters.GetSubArray(0, result.kernel.nodeCount).ToArray(),
                        };
                        AssertVerifierPasses(BuildCutRun(storage, subject, in result, TiltedPlane(), nodes), "resumed cut");
                    }
                    finally
                    {
                        keys.Dispose();
                        parameters.Dispose();
                    }
                }
            }
        }
    }
}
