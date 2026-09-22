using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The extent a stored geometry is recorded with -- per submesh the bounds of the vertices its indices name, and
    /// the span of vertex indices named -- is what walking the published indices would find, for every producer:
    /// an appended cuttable geometry, a geometry appended from a Mesh, both sides of a cut (all submeshes, caps
    /// included, and a side that took no triangle of a submesh), the children of a child, and a geometry that took
    /// over a retired one's descriptor. The reference here walks the indices; the product no longer does.
    /// </summary>
    public class VpRecordedExtentTests
    {
        private VpCpuGeometryStorage _storage;

        [SetUp]
        public void SetUp()
        {
            _storage = new VpCpuGeometryStorage(1 << 15, 1 << 16, 64, 256, 256, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        {
            _storage.Dispose();
        }

        [Test]
        public void AnAppendedCuttableGeometry_IsRecordedAsItsIndicesMeasure()
        {
            VpStoredGeometry box = Append(Box());
            AssertRecordedEqualsMeasured(box, "appended box");
        }

        [Test]
        public void AGeometryAppendedFromAMesh_IsRecordedAsItsIndicesMeasure()
        {
            SyntheticMesh synthetic = Box();
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            var positions = new Vector3[synthetic.Vertices.Length];
            var normals = new Vector3[synthetic.Vertices.Length];
            var uv = new Vector2[synthetic.Vertices.Length];
            for (int v = 0; v < positions.Length; v++)
            {
                positions[v] = synthetic.Vertices[v].position;
                normals[v] = synthetic.Vertices[v].normal;
                uv[v] = synthetic.Vertices[v].uv0;
            }

            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.subMeshCount = synthetic.SubmeshIndexCounts.Count;
            int offset = 0;
            for (int s = 0; s < synthetic.SubmeshIndexCounts.Count; s++)
            {
                var triangles = new int[synthetic.SubmeshIndexCounts[s]];
                for (int i = 0; i < triangles.Length; i++)
                {
                    triangles[i] = (int)synthetic.Indices[offset + i];
                }

                mesh.SetTriangles(triangles, s);
                offset += triangles.Length;
            }

            try
            {
                Assert.That(_storage.TryAppend(mesh, out VpStoredGeometry geometry), Is.True, "the mesh is appended");
                Assert.That(geometry.submeshCount, Is.EqualTo(2));
                AssertRecordedEqualsMeasured(geometry, "mesh box");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BothSidesOfACut_AreRecordedAsTheirIndicesMeasure_CapsAndAllSubmeshes()
        {
            VpStoredGeometry box = Append(Box());
            VpStorageCutResult result = Cut(box, Tilted());
            Assert.That(result.positive.IsProduced, Is.True, "a positive side was produced");
            Assert.That(result.negative.IsProduced, Is.True, "a negative side was produced");
            Assert.That(result.kernel.capTriangles, Is.GreaterThan(0), "the cut has caps");
            AssertRecordedEqualsMeasured(result.positive.geometry, "positive side");
            AssertRecordedEqualsMeasured(result.negative.geometry, "negative side");
        }

        [Test]
        public void ASideThatTookNoTriangleOfASubmesh_IsRecordedWithThatSubmeshEmpty()
        {
            // Two boxes in one geometry, one per submesh, either side of x = 0: the plane x = 0 puts the whole of
            // submesh 1 on the positive side and the whole of submesh 0 on the negative side, so each side really
            // has one submesh with no triangle at all.
            LogicalMeshBuilder builder = SyntheticGeometry.Box(2, new float3(1f, 1f, 1f), new float3(-1f, 0f, 0f));
            builder.CurrentSubmesh = 1;
            SyntheticGeometry.AppendBox(builder, 2, new float3(1f, 1f, 1f), new float3(1f, 0f, 0f));
            SyntheticMesh twoBoxes = builder.Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
            Assert.That(twoBoxes.SubmeshIndexCounts.Count, Is.EqualTo(2), "the layout: one submesh per box");
            VpStoredGeometry geometry = Append(twoBoxes);
            VpStorageCutResult result = Cut(geometry, SyntheticGeometry.Plane(new float3(1f, 0f, 0f), float3.zero));
            Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
            Assert.That(result.positive.IsProduced, Is.True, "the positive side (the box at +x) was produced");
            Assert.That(result.negative.IsProduced, Is.True, "the negative side (the box at -x) was produced");

            Assert.That(_storage.TryGetSubmeshes(result.positive.geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly positiveSubmeshes), Is.True);
            Assert.That(positiveSubmeshes.Length, Is.EqualTo(2), "the positive side describes both submeshes");
            Assert.That(positiveSubmeshes[0].indexCount, Is.Zero, "the positive side took no triangle of submesh 0");
            Assert.That(positiveSubmeshes[1].indexCount, Is.GreaterThan(0), "and all of submesh 1");
            Assert.That(_storage.TryGetSubmeshes(result.negative.geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly negativeSubmeshes), Is.True);
            Assert.That(negativeSubmeshes.Length, Is.EqualTo(2));
            Assert.That(negativeSubmeshes[1].indexCount, Is.Zero, "the negative side took no triangle of submesh 1");
            Assert.That(negativeSubmeshes[0].indexCount, Is.GreaterThan(0));

            AssertRecordedEqualsMeasured(result.positive.geometry, "positive side with submesh 0 empty");
            AssertRecordedEqualsMeasured(result.negative.geometry, "negative side with submesh 1 empty");
        }

        [Test]
        public void AChildCutAgain_HasGrandchildrenRecordedAsTheirIndicesMeasure()
        {
            VpStoredGeometry box = Append(Box());
            VpStorageCutResult first = Cut(box, Tilted());
            Assert.That(first.positive.IsProduced, Is.True);
            VpStorageCutResult second = Cut(first.positive.geometry, SyntheticGeometry.Plane(new float3(0f, 0f, 1f), new float3(0.01f, 0.02f, 0.03f)));
            Assert.That(second.status, Is.EqualTo(VpStorageCutStatus.Ok));
            if (second.positive.IsProduced)
            {
                AssertRecordedEqualsMeasured(second.positive.geometry, "grandchild +");
            }

            if (second.negative.IsProduced)
            {
                AssertRecordedEqualsMeasured(second.negative.geometry, "grandchild -");
            }

            Assert.That(second.positive.IsProduced || second.negative.IsProduced, Is.True, "the child was cut");
        }

        [Test]
        public void AGeometryTakingOverARetiredDescriptor_HasItsOwnRecord_AndTheRetiredOneHasNone()
        {
            VpStoredGeometry first = Append(Box());
            Assert.That(_storage.TryGetPublishedExtent(first, out _, out _, out _), Is.True);
            Assert.That(_storage.TryRetireIndices(first.indexRange), Is.True, "the first geometry's indices are retired");

            VpStoredGeometry second = Append(SyntheticGeometry.Box(2, new float3(3f, 0.5f, 2f), new float3(10f, 0f, 0f), true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30 }));
            Assert.That(_storage.TryGetPublishedExtent(first, out _, out _, out _), Is.False, "the retired geometry has no extent");
            AssertRecordedEqualsMeasured(second, "second box");
            _storage.TryGetPublishedExtent(second, out _, out _, out Bounds bounds);
            Assert.That(bounds.center.x, Is.EqualTo(10f).Within(1e-4f), "the second's own bounds, not the first's");
        }

        [Test]
        public void TheDrawCommands_CarryTheRecordedBounds_AndTheReferencedRange()
        {
            VpStoredGeometry box = Append(Box());
            VpStorageCutResult result = Cut(box, Tilted());
            VpStoredGeometry side = result.positive.geometry;
            Assert.That(
                VpStoredGeometryDraw.TryBuildCommands(_storage, side, 0, out VpIndirectCommand[] commands, out int[] materials, out Bounds localBounds),
                Is.True, "the side's commands are built from its record");
            Measure(side, out Bounds[] measured, out int referencedStart, out int referencedCount, out Bounds measuredAll);
            Assert.That(commands.Length, Is.EqualTo(measured.Length));
            AssertSameBounds(localBounds, measuredAll, "the side's bounds");
            for (int s = 0; s < commands.Length; s++)
            {
                if (commands[s].range.indexCount > 0)
                {
                    AssertSameBounds(commands[s].localBounds, measured[s], "submesh " + s);
                }

                Assert.That(commands[s].range.vertexStart, Is.EqualTo(referencedStart), "submesh " + s + " referenced start");
                Assert.That(commands[s].range.vertexCount, Is.EqualTo(referencedCount), "submesh " + s + " referenced count");
            }
        }

        // ----- the reference: what walking the published indices finds ------------------------------------------

        private void AssertRecordedEqualsMeasured(VpStoredGeometry geometry, string what)
        {
            Assert.That(
                _storage.TryGetPublishedExtent(geometry, out int referencedStart, out int referencedCount, out Bounds recordedAll),
                Is.True, what + ": an extent is recorded");
            Assert.That(_storage.TryGetSubmeshBounds(geometry, out NativeArray<VpGeometryBounds>.ReadOnly recorded), Is.True);
            Assert.That(_storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
            Measure(geometry, out Bounds[] measured, out int measuredStart, out int measuredCount, out Bounds measuredAll);
            Assert.That(recorded.Length, Is.EqualTo(measured.Length), what + ": one bound per submesh");
            Assert.That(referencedStart, Is.EqualTo(measuredStart), what + ": referenced start");
            Assert.That(referencedCount, Is.EqualTo(measuredCount), what + ": referenced count");
            AssertSameBounds(recordedAll, measuredAll, what + ": the geometry's bounds");
            for (int s = 0; s < measured.Length; s++)
            {
                if (submeshes[s].indexCount == 0)
                {
                    continue;
                }

                AssertSameBounds(recorded[s].ToBounds(), measured[s], what + ": submesh " + s);
            }
        }

        private void Measure(VpStoredGeometry geometry, out Bounds[] perSubmesh, out int referencedStart, out int referencedCount, out Bounds all)
        {
            Assert.That(_storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
            Assert.That(_storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True);
            try
            {
                NativeArray<VpRenderVertex>.ReadOnly vertices = _storage.Vertices;
                perSubmesh = new Bounds[submeshes.Length];
                uint lo = uint.MaxValue, hi = 0;
                Vector3 allMin = Vector3.positiveInfinity, allMax = Vector3.negativeInfinity;
                for (int s = 0; s < submeshes.Length; s++)
                {
                    Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                    for (int i = submeshes[s].indexOffset; i < submeshes[s].indexOffset + submeshes[s].indexCount; i++)
                    {
                        uint g = view[i];
                        Vector3 p = vertices[(int)g].position;
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        lo = System.Math.Min(lo, g);
                        hi = System.Math.Max(hi, g);
                    }

                    perSubmesh[s] = new Bounds((min + max) * 0.5f, max - min);
                    if (submeshes[s].indexCount > 0)
                    {
                        allMin = Vector3.Min(allMin, min);
                        allMax = Vector3.Max(allMax, max);
                    }
                }

                referencedStart = (int)lo;
                referencedCount = (int)(hi - lo + 1);
                all = new Bounds((allMin + allMax) * 0.5f, allMax - allMin);
            }
            finally
            {
                _storage.TryReleaseIndexReadLease(lease);
            }
        }

        private static void AssertSameBounds(Bounds a, Bounds b, string what)
        {
            Assert.That(a.min.x, Is.EqualTo(b.min.x).Within(1e-5f), what + " min.x");
            Assert.That(a.min.y, Is.EqualTo(b.min.y).Within(1e-5f), what + " min.y");
            Assert.That(a.min.z, Is.EqualTo(b.min.z).Within(1e-5f), what + " min.z");
            Assert.That(a.max.x, Is.EqualTo(b.max.x).Within(1e-5f), what + " max.x");
            Assert.That(a.max.y, Is.EqualTo(b.max.y).Within(1e-5f), what + " max.y");
            Assert.That(a.max.z, Is.EqualTo(b.max.z).Within(1e-5f), what + " max.z");
        }

        // ----- fixtures ----------------------------------------------------------------------------------------------

        private static SyntheticMesh Box(int n = 3)
        {
            return SyntheticGeometry.Box(n, new float3(1f, 1f, 1f), float3.zero, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
        }

        private VpStoredGeometry Append(SyntheticMesh mesh)
        {
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(
                _storage.TryAppendCuttable(
                    mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes,
                    out VpStoredGeometry geometry, out VpCutInputVerdict verdict),
                Is.True, "the box is appended as a cut input: " + verdict);
            return geometry;
        }

        private VpStorageCutResult Cut(VpStoredGeometry geometry, float4 plane)
        {
            Assert.That(VpStorageCutInput.TryAcquire(_storage, geometry, out VpStorageCutInput input), Is.True, "acquire the input");
            try
            {
                Assert.That(VpStorageCut.TryExecute(_storage, input, plane, out VpStorageCutResult result), Is.True, "the cut runs");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut finished: " + result.status);
                return result;
            }
            finally
            {
                input.Dispose();
            }
        }

        private static float4 Tilted()
        {
            return SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), new float3(0.0071f, -0.0233f, 0.0119f));
        }
    }
}
