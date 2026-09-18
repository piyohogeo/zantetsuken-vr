using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Preparing a geometry a <see cref="VpCpuGeometryStorage"/> owns for the Stage 3 indexed indirect path, without a
    /// Unity Mesh in between: the indices come out as stored, in global vertex numbers and in corner order, one command
    /// per submesh carries the stored material index, and the index positions are the upload base plus the submesh's
    /// offset inside the geometry's own range. The cut results used here come from the ordinary product path, so a
    /// child's several vertex blocks and a reused index range are exercised as they really occur.
    /// </summary>
    public class VpStoredGeometryDrawTests
    {
        private const int ControlPoints = 8;
        private const int RenderVertices = 24;
        private const int IndexCount = 36;
        private const int SideIndices = 24;
        private const int EndIndices = 12;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int QuadIndexCount = 6;

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.2f, 0.0f, -1.0f), new float3(1.1f, 0.0f, 0.9f), new float3(-0.8f, 0.0f, 1.0f),
            new float3(-0.5f, 1.3f, -0.4f), new float3(0.7f, 1.3f, -0.6f), new float3(0.6f, 1.3f, 0.5f), new float3(-0.3f, 1.3f, 0.6f),
        };

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
                Submeshes = new[] { new VpGeometrySubmesh(0, QuadIndexCount, 3) },
                TopologyVertexCount = 4,
            };
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendPrepared(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        /// <summary>For the subjects these display tests cut: appended as a cut input, through the gate.</summary>
        private static VpStoredGeometry AppendCuttable(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendCuttable(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry, out _),
                Is.True,
                "append as a cut input");
            return geometry;
        }

        private static float4 TiltedPlane()
        {
            float3 centre = float3.zero;
            foreach (float3 p in k_controlPoints)
            {
                centre += p;
            }

            centre /= k_controlPoints.Length;
            float3 n = math.normalize(new float3(0.37f, 0.61f, -0.7f));
            float3 point = centre + new float3(0.0071f, -0.0233f, 0.0119f);
            return new float4(n, -math.dot(n, point));
        }

        private static float4 SecondPlane()
        {
            float3 n = math.normalize(new float3(0.81f, -0.23f, 0.54f));
            float3 point = new float3(0.0313f, 0.6217f, -0.0119f);
            return new float4(n, -math.dot(n, point));
        }

        private static VpStorageCutResult Cut(VpCpuGeometryStorage storage, VpStoredGeometry geometry, float4 plane)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, "acquire input");
            using (input)
            {
                Assert.That(VpStorageCut.TryExecute(storage, input, plane, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                return result;
            }
        }

        private static uint[] ReadIndices(VpCpuGeometryStorage storage, VpIndexRangeHandle handle)
        {
            Assert.That(storage.TryAcquireIndexReadLease(handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "read lease");
            uint[] indices = view.ToArray();
            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release lease");
            return indices;
        }

        /// <summary>No reader is left: a retirement with one would leave the range Retiring instead of Free.</summary>
        private static void AssertNoLeaseRemains(VpCpuGeometryStorage storage, VpStoredGeometry geometry, string label)
        {
            Assert.That(storage.TryRetireIndices(geometry.indexRange), Is.True, label + ": retire");
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _), Is.True, label + ": state");
            Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), label + ": no reader was left behind");
        }

        [Test]
        public void AGeometryAtNonZeroStarts_UploadsItsOwnIndicesInGlobalNumbersAndCornerOrder()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(subject.vertexStart, Is.Not.Zero, "the vertices do not start at 0");
                Assert.That(storage.TryGetIndexState(subject.indexRange, out _, out int physicalStart, out _), Is.True);
                Assert.That(physicalStart, Is.Not.Zero, "the indices do not start at 0");

                const int uploadBase = 512;
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, uploadBase, out VpStoredGeometryUpload upload), Is.True, "build");

                // the indices are the stored ones, corner for corner, still global vertex numbers
                Assert.That(upload.Indices, Is.EqualTo(ReadIndices(storage, subject.indexRange)), "indices as stored");
                Assert.That(upload.Indices.Length, Is.EqualTo(IndexCount));
                Assert.That(upload.Indices.Min(), Is.GreaterThanOrEqualTo((uint)subject.vertexStart), "global numbers");

                // the command positions are the upload base plus the submesh offset, never the physical start
                Assert.That(upload.IndexBase, Is.EqualTo(uploadBase));
                Assert.That(upload.Commands.Length, Is.EqualTo(2), "one command per submesh");
                Assert.That(upload.Commands[0].range.indexStart, Is.EqualTo(uploadBase));
                Assert.That(upload.Commands[0].range.indexCount, Is.EqualTo(SideIndices));
                Assert.That(upload.Commands[1].range.indexStart, Is.EqualTo(uploadBase + SideIndices));
                Assert.That(upload.Commands[1].range.indexCount, Is.EqualTo(EndIndices));
                Assert.That(upload.Commands.Sum(c => c.range.indexCount), Is.EqualTo(IndexCount), "the submeshes cover the geometry");
                foreach (VpIndirectCommand command in upload.Commands)
                {
                    Assert.That(command.range.indexStart, Is.Not.EqualTo(physicalStart + (command.range.indexStart - uploadBase)),
                        "the physical start is not added");
                    Assert.That(command.instanceCount, Is.EqualTo(1));
                }

                Assert.That(upload.MaterialIndices, Is.EqualTo(new[] { SideMaterial, EndMaterial }), "stored material indices, in submesh order");
            }
        }

        [Test]
        public void TheUploadedIndices_AddressTheStoredAttributesIncludingSeams()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, 0, out VpStoredGeometryUpload upload), Is.True, "build");

                // every index addresses the committed vertex the geometry stored, attributes and all
                NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                for (int i = 0; i < upload.Indices.Length; i++)
                {
                    VpRenderVertex stored = committed[(int)upload.Indices[i]];
                    int local = (int)(upload.Indices[i] - (uint)subject.vertexStart);
                    Assert.That(stored.position, Is.EqualTo(prepared.Vertices[local].position), "position at corner " + i);
                    Assert.That(stored.normal, Is.EqualTo(prepared.Vertices[local].normal), "normal at corner " + i);
                    Assert.That(stored.uv0, Is.EqualTo(prepared.Vertices[local].uv0), "uv0 at corner " + i);
                }

                // the seams survive: the referenced vertices are 24 distinct numbers over 8 distinct positions
                uint[] referenced = upload.Indices.Distinct().ToArray();
                Assert.That(referenced.Length, Is.EqualTo(RenderVertices), "no vertex was merged away");
                Assert.That(referenced.Select(v => (Vector3)committed[(int)v].position).Distinct().Count(), Is.EqualTo(ControlPoints), "they are coincident");
                foreach (IGrouping<Vector3, uint> group in referenced.GroupBy(v => (Vector3)committed[(int)v].position))
                {
                    uint[] members = group.ToArray();
                    Assert.That(members.Length, Is.GreaterThan(1), "each control point carries several render vertices");
                    for (int a = 0; a < members.Length; a++)
                    {
                        for (int b = a + 1; b < members.Length; b++)
                        {
                            VpRenderVertex x = committed[(int)members[a]];
                            VpRenderVertex y = committed[(int)members[b]];
                            Assert.That(x.normal != y.normal || x.uv0 != y.uv0, Is.True, "coincident vertices differ in an attribute");
                        }
                    }
                }

                // the winding is the stored corner order, triangle by triangle
                uint[] stored2 = ReadIndices(storage, subject.indexRange);
                for (int t = 0; t < upload.Indices.Length; t += 3)
                {
                    Assert.That(upload.Indices[t], Is.EqualTo(stored2[t]), "corner 0 of triangle " + (t / 3));
                    Assert.That(upload.Indices[t + 1], Is.EqualTo(stored2[t + 1]), "corner 1 of triangle " + (t / 3));
                    Assert.That(upload.Indices[t + 2], Is.EqualTo(stored2[t + 2]), "corner 2 of triangle " + (t / 3));
                }
            }
        }

        [Test]
        public void BothSidesOfACut_AndAMultiBlockChild_Upload()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = AppendCuttable(storage, prepared);
                VpStorageCutResult cut = Cut(storage, subject, TiltedPlane());
                Assert.That(cut.positive.IsProduced && cut.negative.IsProduced, Is.True, "both sides");

                foreach ((VpStorageCutSide side, string label) in new[] { (cut.positive, "positive"), (cut.negative, "negative") })
                {
                    Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, side.geometry, 100, out VpStoredGeometryUpload upload), Is.True, label + ": build");
                    Assert.That(upload.Indices, Is.EqualTo(ReadIndices(storage, side.geometry.indexRange)), label + ": indices as stored");
                    Assert.That(upload.Commands.Length, Is.EqualTo(2), label + ": one command per submesh");
                    Assert.That(upload.MaterialIndices, Is.EqualTo(new[] { SideMaterial, EndMaterial }), label + ": material mapping kept");
                    Assert.That(upload.Commands[0].range.indexStart, Is.EqualTo(100), label + ": first command at the base");
                    Assert.That(upload.Commands.Sum(c => c.range.indexCount), Is.EqualTo(upload.Indices.Length), label + ": the submeshes cover it");
                }

                // a child names its parent's block and the cut's own, and the indices span both
                VpStoredGeometry child = cut.positive.geometry;
                Assert.That(child.blockCount, Is.EqualTo(2), "the child is multi-block");
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, child, 0, out VpStoredGeometryUpload childUpload), Is.True, "child build");
                Assert.That(storage.TryGetVertexBlocks(child, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True);
                int blocksUsed = 0;
                foreach (VpGeometryVertexBlock block in blocks)
                {
                    if (childUpload.Indices.Any(v => v >= (uint)block.vertexStart && v < (uint)(block.vertexStart + block.vertexCount)))
                    {
                        blocksUsed++;
                    }
                }

                Assert.That(blocksUsed, Is.GreaterThan(1), "the indices draw on more than one block");

                // no renumbering happens: the global numbers are used as they are, across blocks
                Assert.That(childUpload.ReferencedVertexStart, Is.EqualTo((int)childUpload.Indices.Min()));
                Assert.That(childUpload.ReferencedVertexStart + childUpload.ReferencedVertexCount - 1, Is.EqualTo((int)childUpload.Indices.Max()));

                // and a grandchild, cut again, still uploads
                VpStorageCutResult second = Cut(storage, child, SecondPlane());
                VpStoredGeometry grandchild = second.positive.IsProduced ? second.positive.geometry : second.negative.geometry;
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, grandchild, 0, out VpStoredGeometryUpload grand), Is.True, "grandchild build");
                Assert.That(grand.Indices, Is.EqualTo(ReadIndices(storage, grandchild.indexRange)), "grandchild indices as stored");
            }
        }

        [Test]
        public void AfterAnIndexRangeIsReused_TheUploadReadsTheGeometryThatOwnsItNow()
        {
            Prepared prepared = BuildPrepared();
            Prepared quad = BuildQuad(new float3(9, 0, 9));
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, prepared);
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True, "retire the first");
                VpStoredGeometry reuser = Append(storage, quad);
                Assert.That(storage.TryGetIndexState(reuser.indexRange, out _, out int start, out _), Is.True);
                Assert.That(start, Is.Zero, "the freed space is reused");

                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, reuser, 0, out VpStoredGeometryUpload upload), Is.True, "build");
                Assert.That(upload.Indices.Length, Is.EqualTo(QuadIndexCount), "the quad's indices, not the hexahedron's");
                Assert.That(upload.Commands.Length, Is.EqualTo(1));
                Assert.That(upload.MaterialIndices, Is.EqualTo(new[] { 3 }), "the quad's own material index");

                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, first, 0, out VpStoredGeometryUpload gone), Is.False, "the retired geometry is refused");
                Assert.That(gone, Is.Null);
            }
        }

        [Test]
        public void Materials_ResolveByTheStoredIndexAndAMissingOneIsRefused()
        {
            Prepared prepared = BuildPrepared();
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color");
            var side = new Material(shader) { name = "side" };
            var end = new Material(shader) { name = "end" };
            try
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    VpStoredGeometry subject = Append(storage, prepared);
                    Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, 0, out VpStoredGeometryUpload upload), Is.True, "build");

                    // the same resolution the Unity Mesh path uses, over the same stored indices
                    var bySource = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, end } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, bySource, out Material[] materials), Is.True, "resolve");
                    Assert.That(materials, Is.EqualTo(new[] { side, end }));
                    for (int c = 0; c < upload.Commands.Length; c++)
                    {
                        Assert.That(materials[c], Is.EqualTo(bySource[upload.MaterialIndices[c]]), "command " + c + " draws with its own material");
                    }

                    var byOrdinal = new Dictionary<int, Material> { { 0, side }, { 1, end } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, byOrdinal, out _), Is.False, "ordinals are refused");
                    var missing = new Dictionary<int, Material> { { SideMaterial, side } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, missing, out _), Is.False, "a missing material fails");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(side);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        [Test]
        public void SuccessAndRefusal_LeaveNoLeaseAndChangeNothing()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                VpStoredGeometry foreign = Append(other, prepared);
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;
                uint[] before = ReadIndices(storage, subject.indexRange);

                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, 0, out VpStoredGeometryUpload upload), Is.True, "build");
                upload.Indices[0] = 12345u; // the copy is the caller's; the storage must not follow it
                Assert.That(ReadIndices(storage, subject.indexRange), Is.EqualTo(before), "the stored indices are untouched");

                Assert.That(VpStoredGeometryDraw.TryBuildUpload(null, subject, 0, out VpStoredGeometryUpload noStorage), Is.False, "a null storage");
                Assert.That(noStorage, Is.Null);
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, default, 0, out VpStoredGeometryUpload defaultGeometry), Is.False, "a default geometry");
                Assert.That(defaultGeometry, Is.Null);
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, foreign, 0, out VpStoredGeometryUpload foreignUpload), Is.False, "another storage's geometry");
                Assert.That(foreignUpload, Is.Null);
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, -1, out VpStoredGeometryUpload negative), Is.False, "a negative upload base");
                Assert.That(negative, Is.Null);

                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "nothing was committed");
                Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount));
                Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount));

                AssertNoLeaseRemains(storage, subject, "after a build and refusals");
                AssertNoLeaseRemains(other, foreign, "the untouched foreign geometry");
            }
        }

        [Test]
        public void AGeometryWithoutATopologyMapping_IsRefusedRatherThanGuessedAt()
        {
            // As in the Unity Mesh path: the block list is read through the topology mapping, so a geometry appended
            // straight from a Unity Mesh is outside this conversion. Recorded so the limit stays a decision.
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1) });
            mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up });
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.one });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            try
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    Assert.That(storage.TryAppend(mesh, out VpStoredGeometry appended), Is.True, "append the mesh");
                    Assert.That(appended.hasTopology, Is.False);
                    Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, appended, 0, out VpStoredGeometryUpload upload), Is.False, "refused");
                    Assert.That(upload, Is.Null);
                    AssertNoLeaseRemains(storage, appended, "after the refusal");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TheUploadAndTheUnityMeshPath_DescribeTheSameTriangles()
        {
            // The two display paths must agree about the geometry, which is what makes them comparable at all: the
            // Mesh path renumbers into local vertices, the VP3 path keeps the global numbers, and both must name the
            // same corner positions in the same order.
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = AppendCuttable(storage, prepared);
                VpStorageCutResult cut = Cut(storage, subject, TiltedPlane());

                foreach ((VpStoredGeometry geometry, string label) in new[]
                         {
                             (subject, "uncut"), (cut.positive.geometry, "positive"), (cut.negative.geometry, "negative"),
                         })
                {
                    Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, geometry, 0, out VpStoredGeometryUpload upload), Is.True, label + ": upload");
                    Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, geometry, out Mesh mesh), Is.True, label + ": mesh");
                    try
                    {
                        NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                        var fromUpload = upload.Indices.Select(v => (Vector3)committed[(int)v].position).ToArray();
                        Vector3[] meshVertices = mesh.vertices;
                        var fromMesh = new List<Vector3>();
                        for (int s = 0; s < mesh.subMeshCount; s++)
                        {
                            fromMesh.AddRange(mesh.GetTriangles(s).Select(c => meshVertices[c]));
                        }

                        Assert.That(fromUpload, Is.EqualTo(fromMesh.ToArray()), label + ": the same corners in the same order");
                        Assert.That(upload.Commands.Length, Is.EqualTo(mesh.subMeshCount), label + ": the same submesh count");
                        for (int s = 0; s < mesh.subMeshCount; s++)
                        {
                            Assert.That(upload.Commands[s].range.indexCount, Is.EqualTo(mesh.GetTriangles(s).Length), label + ": submesh " + s + " size");
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }
        }

        [Test]
        public void AnUploadBaseThatWouldRunPastAnIntPosition_IsRefused()
        {
            // Nothing large is allocated here: only the base is extreme, and the geometry stays the small fixture.
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);

                // the largest base that still leaves every index expressible
                int highest = int.MaxValue - IndexCount;
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, highest, out VpStoredGeometryUpload upload), Is.True, "the highest base that fits");
                Assert.That(upload.IndexBase, Is.EqualTo(highest));
                Assert.That(upload.Commands.Length, Is.EqualTo(2), "several submeshes, each offset from the same base");
                Assert.That(upload.Commands[0].range.indexStart, Is.EqualTo(highest), "the first submesh");
                Assert.That(upload.Commands[1].range.indexStart, Is.EqualTo(highest + SideIndices), "the second submesh, added without wrapping");
                foreach (VpIndirectCommand command in upload.Commands)
                {
                    Assert.That(command.range.indexStart, Is.GreaterThan(0), "no command starts at a negative position");
                    Assert.That((long)command.range.indexStart + command.range.indexCount, Is.LessThanOrEqualTo(int.MaxValue), "nor ends past one");
                }

                // one more index than fits, and every larger base, is refused outright
                foreach (int base_ in new[] { highest + 1, int.MaxValue - SideIndices, int.MaxValue - 1, int.MaxValue })
                {
                    Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, base_, out VpStoredGeometryUpload over), Is.False, "base " + base_);
                    Assert.That(over, Is.Null, "base " + base_ + " yields nothing");
                }

                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, subject, -1, out _), Is.False, "a negative base is still refused");

                // the refusals took the lease and gave it back: the range retires straight to Free
                AssertNoLeaseRemains(storage, subject, "after the overflow refusals");
            }
        }

        [Test]
        public void AnOverflowingBase_IsRefusedForAMultiSubmeshCutResultToo()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = AppendCuttable(storage, prepared);
                VpStorageCutResult cut = Cut(storage, subject, TiltedPlane());
                VpStoredGeometry child = cut.positive.geometry;
                Assert.That(storage.TryGetIndexState(child.indexRange, out _, out _, out int childIndexCount), Is.True);
                Assert.That(child.submeshCount, Is.EqualTo(2), "the child has several submeshes");

                int highest = int.MaxValue - childIndexCount;
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, child, highest, out VpStoredGeometryUpload upload), Is.True, "the highest base that fits");
                int covered = 0;
                foreach (VpIndirectCommand command in upload.Commands)
                {
                    Assert.That(command.range.indexStart, Is.EqualTo(highest + covered), "each submesh follows the last");
                    covered += command.range.indexCount;
                }

                Assert.That(covered, Is.EqualTo(childIndexCount), "the submeshes cover the child");
                Assert.That(VpStoredGeometryDraw.TryBuildUpload(storage, child, highest + 1, out VpStoredGeometryUpload over), Is.False, "one index too far");
                Assert.That(over, Is.Null);
                AssertNoLeaseRemains(storage, child, "after the refusal on the child");
            }
        }
    }
}
