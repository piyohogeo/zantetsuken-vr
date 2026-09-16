using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.ReferenceIntake;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Fixed change of basis of the Phase 0.21 common geometry (FBX file basis <-> Unity basis) on a public synthetic
    /// input: an asymmetric closed hexahedron with hard-edge attribute seams, two submeshes and non-zero UVs. Locks
    /// the one common 32 byte vertex (position, normal, uv0 and nothing else), the attribute rules, the corner-order
    /// reversal, the untouched topology / seam / submesh data, input immutability, the involution, plane side
    /// invariance and, through the Phase 2.9 kernel, that both bases yield corresponding nodes, surface boundaries and
    /// cap boundaries under the existing verifier, with uv0 and finite normals surviving the cut. Cap triangle order,
    /// ear order and per-side cap areas are deliberately not compared (not a contract, DESIGN 6.4).
    /// </summary>
    public class CommonGeometryBasisChangeTests
    {
        // ------------------------------------------------------------------------------------------ fixture
        /// <summary>Absolute slack for float interpolation of uv0: four orders of magnitude above the rounding the kernel can produce on this fixture.</summary>
        const float k_uvEpsilon = 1e-5f;

        static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.2f, 0.0f, -1.0f), new float3(1.1f, 0.0f, 0.9f), new float3(-0.8f, 0.0f, 1.0f),   // bottom
            new float3(-0.5f, 1.3f, -0.4f), new float3(0.7f, 1.3f, -0.6f), new float3(0.6f, 1.3f, 0.5f), new float3(-0.3f, 1.3f, 0.6f),   // top, shifted and smaller
        };
        // quads as control point cycles wound so that cross(c1 - c0, c2 - c0) points outward (the kernel's front face); submesh 0 = sides, 1 = ends
        static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        /// <summary>Every face gets its own four render vertices (a seam on every edge), a flat normal and its own UV square.</summary>
        static PreparedCommonGeometry Fixture()
        {
            float3 centroid = float3.zero; foreach (var p in k_controlPoints) centroid += p; centroid /= k_controlPoints.Length;
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<CommonGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh) continue;
                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    Assert.That(math.dot(n, k_controlPoints[c[0]] - centroid), Is.GreaterThan(0f), "fixture face " + f + " must be wound outward");
                    uint b = (uint)vertices.Count;
                    float2[] uv = { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = k_controlPoints[c[k]], normal = n, uv0 = uv[k] + new float2(f * 0.013f, f * 0.021f) });
                        topology.Add(c[k]);
                    }
                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }
                submeshes.Add(new CommonGeometrySubmesh { IndexStart = start, IndexCount = indices.Count - start, MaterialIndex = submesh, MaterialName = submesh == 0 ? "Side" : "EndCap" });
            }
            var g = new PreparedCommonGeometry
            {
                Name = "asymmetric-hexahedron", Basis = CommonGeometryBasis.FbxFile,
                Vertices = vertices.ToArray(), Indices = indices.ToArray(), TopologyOfVertex = topology.ToArray(), TopologyVertexCount = k_controlPoints.Length, Submeshes = submeshes.ToArray(),
            };
            Assert.That(g.Validate(), Is.Empty);
            return g;
        }

        static PreparedCommonGeometry Snapshot(PreparedCommonGeometry g) => new PreparedCommonGeometry
        {
            Name = g.Name, Basis = g.Basis, Vertices = (VpRenderVertex[])g.Vertices.Clone(), Indices = (uint[])g.Indices.Clone(),
            TopologyOfVertex = (int[])g.TopologyOfVertex.Clone(), TopologyVertexCount = g.TopologyVertexCount, Submeshes = (CommonGeometrySubmesh[])g.Submeshes.Clone(),
        };

        static void AssertSame(PreparedCommonGeometry expected, PreparedCommonGeometry actual, string what)
        {
            Assert.That(actual.Name, Is.EqualTo(expected.Name), what + ": name");
            Assert.That(actual.Basis, Is.EqualTo(expected.Basis), what + ": basis");
            Assert.That(actual.TopologyVertexCount, Is.EqualTo(expected.TopologyVertexCount), what + ": topology vertex count");
            Assert.That(actual.Vertices.Length, Is.EqualTo(expected.Vertices.Length), what + ": vertex count");
            for (int i = 0; i < expected.Vertices.Length; i++)
            {
                Assert.That(actual.Vertices[i].position, Is.EqualTo(expected.Vertices[i].position), what + ": position " + i);
                Assert.That(actual.Vertices[i].normal, Is.EqualTo(expected.Vertices[i].normal), what + ": normal " + i);
                Assert.That(actual.Vertices[i].uv0, Is.EqualTo(expected.Vertices[i].uv0), what + ": uv " + i);
            }
            Assert.That(actual.Indices, Is.EqualTo(expected.Indices), what + ": indices");
            Assert.That(actual.TopologyOfVertex, Is.EqualTo(expected.TopologyOfVertex), what + ": topology map");
            Assert.That(actual.Submeshes.Length, Is.EqualTo(expected.Submeshes.Length), what + ": submesh count");
            for (int s = 0; s < expected.Submeshes.Length; s++)
            {
                Assert.That(actual.Submeshes[s].IndexStart, Is.EqualTo(expected.Submeshes[s].IndexStart), what + ": submesh " + s + " start");
                Assert.That(actual.Submeshes[s].IndexCount, Is.EqualTo(expected.Submeshes[s].IndexCount), what + ": submesh " + s + " count");
                Assert.That(actual.Submeshes[s].MaterialIndex, Is.EqualTo(expected.Submeshes[s].MaterialIndex), what + ": submesh " + s + " material index");
                Assert.That(actual.Submeshes[s].MaterialName, Is.EqualTo(expected.Submeshes[s].MaterialName), what + ": submesh " + s + " material");
            }
        }

        /// <summary>Snapshot of everything an ImportedGeometry exposes, to prove a rejected call left it untouched.</summary>
        static (VpRenderVertex[] v, uint[] i, int[] t, int[] counts, string[] names, int[] materials) SnapshotImported(ImportedGeometry g) =>
            ((VpRenderVertex[])g.Mesh.Vertices.Clone(), (uint[])g.Mesh.Indices.Clone(), (int[])g.Mesh.TopologyOfVertex.Clone(), g.Mesh.SubmeshIndexCounts.ToArray(), (string[])g.MaterialNames?.Clone(), (int[])g.SubmeshMaterialIndex?.Clone());

        static void AssertImportedUnchanged((VpRenderVertex[] v, uint[] i, int[] t, int[] counts, string[] names, int[] materials) before, ImportedGeometry g, string what)
        {
            Assert.That(g.Mesh.Vertices.Length, Is.EqualTo(before.v.Length), what + ": vertex count");
            for (int k = 0; k < before.v.Length; k++)
            {
                Assert.That(g.Mesh.Vertices[k].position, Is.EqualTo(before.v[k].position), what + ": position " + k);
                Assert.That(g.Mesh.Vertices[k].normal, Is.EqualTo(before.v[k].normal), what + ": normal " + k);
                Assert.That(g.Mesh.Vertices[k].uv0, Is.EqualTo(before.v[k].uv0), what + ": uv " + k);
            }
            Assert.That(g.Mesh.Indices, Is.EqualTo(before.i), what + ": indices");
            Assert.That(g.Mesh.TopologyOfVertex, Is.EqualTo(before.t), what + ": topology map");
            Assert.That(g.Mesh.SubmeshIndexCounts, Is.EqualTo(before.counts), what + ": submesh counts");
            Assert.That(g.MaterialNames, Is.EqualTo(before.names), what + ": material names");
            Assert.That(g.SubmeshMaterialIndex, Is.EqualTo(before.materials), what + ": submesh material index");
        }

        static float4[] Planes()
        {
            // through the interior with an x component (the reflection changes the normal), plus one that leaves it invariant
            float3 c = new float3(0.05f, 0.62f, 0.03f);
            return new[]
            {
                SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), c + new float3(0.0071f, -0.0233f, 0.0119f)),
                SyntheticGeometry.Plane(new float3(0.9f, 0.1f, 0.3f), c + new float3(0.021f, 0.004f, -0.017f)),
                SyntheticGeometry.Plane(new float3(0, 1, 0), c + new float3(0, 0.0137f, 0)),
            };
        }

        // ------------------------------------------------------------------------------------------ tests
        [Test]
        public void Fixture_IsAsymmetricClosedWithSeamsTwoSubmeshesAndNonZeroUv()
        {
            var g = Fixture();
            Assert.That(g.TriangleCount, Is.EqualTo(12));
            Assert.That(g.Vertices.Length, Is.EqualTo(24), "one render vertex per face corner: every edge is a seam");
            Assert.That(g.Submeshes.Length, Is.EqualTo(2));
            Assert.That(g.Submeshes[0].IndexCount, Is.EqualTo(24)); Assert.That(g.Submeshes[1].IndexCount, Is.EqualTo(12));
            foreach (var v in g.Vertices) Assert.That(math.lengthsq((float2)v.uv0), Is.GreaterThan(0f), "every uv0 is non-zero");
            // asymmetric: reflecting X does not map the control point set onto itself
            var set = new HashSet<float3>(k_controlPoints);
            int mapped = 0; foreach (var p in k_controlPoints) if (set.Contains(FbxUnityBasisChange.Point(p))) mapped++;
            Assert.That(mapped, Is.LessThan(k_controlPoints.Length));
            using (var h = new MeshCutHarness())
            {
                var topo = LogicalTopology.Build(h.Place(ToSynthetic(g), g.Name, 3, 5));
                Assert.That(topo.Validate(), Is.Empty, "closed, manifold and consistently wound on the control-point topology");
                Assert.That(topo.BoundaryEdges, Is.EqualTo(0));
            }
        }

        [Test]
        public void Apply_TransformsEveryAttributeAsSpecified_AndKeepsTopologySeamsAndSubmeshes()
        {
            var g = Fixture();
            var u = FbxUnityBasisChange.Apply(g);
            Assert.That(u.Basis, Is.EqualTo(CommonGeometryBasis.Unity));
            Assert.That(u.Name, Is.EqualTo(g.Name));
            Assert.That(ReferenceEquals(u.Vertices, g.Vertices) || ReferenceEquals(u.Indices, g.Indices) || ReferenceEquals(u.TopologyOfVertex, g.TopologyOfVertex) || ReferenceEquals(u.Submeshes, g.Submeshes), Is.False, "owns new arrays");
            Assert.That(u.Vertices.Length, Is.EqualTo(g.Vertices.Length));
            for (int i = 0; i < g.Vertices.Length; i++)
            {
                VpRenderVertex a = g.Vertices[i], b = u.Vertices[i];
                Assert.That(b.position, Is.EqualTo(new Vector3(-a.position.x, a.position.y, a.position.z)), "position " + i);
                Assert.That(b.normal, Is.EqualTo(new Vector3(-a.normal.x, a.normal.y, a.normal.z)), "normal " + i);
                Assert.That(b.uv0, Is.EqualTo(a.uv0), "uv " + i);
            }
            Assert.That(u.Indices.Length, Is.EqualTo(g.Indices.Length));
            for (int t = 0; t < g.Indices.Length; t += 3)
            {
                Assert.That(u.Indices[t], Is.EqualTo(g.Indices[t]), "corner 0 of triangle " + t / 3);
                Assert.That(u.Indices[t + 1], Is.EqualTo(g.Indices[t + 2]), "corner 1 of triangle " + t / 3);
                Assert.That(u.Indices[t + 2], Is.EqualTo(g.Indices[t + 1]), "corner 2 of triangle " + t / 3);
            }
            Assert.That(u.TopologyOfVertex, Is.EqualTo(g.TopologyOfVertex));
            Assert.That(u.TopologyVertexCount, Is.EqualTo(g.TopologyVertexCount));
            Assert.That(u.Submeshes.Length, Is.EqualTo(g.Submeshes.Length));
            for (int s = 0; s < g.Submeshes.Length; s++)
            {
                Assert.That(u.Submeshes[s].IndexStart, Is.EqualTo(g.Submeshes[s].IndexStart));
                Assert.That(u.Submeshes[s].IndexCount, Is.EqualTo(g.Submeshes[s].IndexCount));
                Assert.That(u.Submeshes[s].MaterialName, Is.EqualTo(g.Submeshes[s].MaterialName));
            }
            Assert.That(u.Validate(), Is.Empty);
            // the surface still faces outward: every reflected face normal points away from the reflected centroid, and agrees with the reversed corner order
            float3 centroid = float3.zero; foreach (var v in u.Vertices) centroid += (float3)v.position; centroid /= u.Vertices.Length;
            for (int t = 0; t < u.Indices.Length; t += 3)
            {
                float3 a = u.Vertices[u.Indices[t]].position, b = u.Vertices[u.Indices[t + 1]].position, c = u.Vertices[u.Indices[t + 2]].position;
                float3 facet = math.cross(b - a, c - a);
                Assert.That(math.dot(facet, u.Vertices[u.Indices[t]].normal), Is.GreaterThan(0f), "triangle " + t / 3 + " winding vs its normal");
                Assert.That(math.dot(facet, a - centroid), Is.GreaterThan(0f), "triangle " + t / 3 + " faces outward");
            }
        }

        [Test]
        public void Apply_DoesNotModifyTheInput()
        {
            var g = Fixture();
            var before = Snapshot(g);
            var arrays = (g.Vertices, g.Indices, g.TopologyOfVertex, g.Submeshes);
            FbxUnityBasisChange.Apply(g);
            FbxUnityBasisChange.Apply(FbxUnityBasisChange.Apply(g));
            Assert.That(ReferenceEquals(arrays.Vertices, g.Vertices) && ReferenceEquals(arrays.Indices, g.Indices) && ReferenceEquals(arrays.TopologyOfVertex, g.TopologyOfVertex) && ReferenceEquals(arrays.Submeshes, g.Submeshes), Is.True, "input arrays were not replaced");
            AssertSame(before, g, "input after Apply");
        }

        [Test]
        public void Apply_Twice_RestoresGeometryAndPlane()
        {
            var g = Fixture();
            var back = FbxUnityBasisChange.Apply(FbxUnityBasisChange.Apply(g));
            AssertSame(g, back, "twice applied");
            foreach (var plane in Planes())
                Assert.That(FbxUnityBasisChange.Plane(FbxUnityBasisChange.Plane(plane)), Is.EqualTo(plane));
        }

        [Test]
        public void Apply_KeepsPlaneSignedDistanceAndSideOfEveryVertex()
        {
            var g = Fixture();
            var u = FbxUnityBasisChange.Apply(g);
            foreach (var plane in Planes())
            {
                float4 planeU = FbxUnityBasisChange.Plane(plane);
                int positive = 0, negative = 0;
                for (int i = 0; i < g.Vertices.Length; i++)
                {
                    float3 p = g.Vertices[i].position, q = u.Vertices[i].position;
                    float d = plane.x * p.x + plane.y * p.y + plane.z * p.z + plane.w;
                    float e = planeU.x * q.x + planeU.y * q.y + planeU.z * q.z + planeU.w;
                    Assert.That(e, Is.EqualTo(d), "signed distance of vertex " + i + " (negation is exact in IEEE arithmetic)");
                    Assert.That(Math.Sign(e), Is.EqualTo(Math.Sign(d)), "side of vertex " + i);
                    if (d > 0) positive++; else if (d < 0) negative++;
                }
                Assert.That(positive, Is.GreaterThan(0)); Assert.That(negative, Is.GreaterThan(0));
            }
        }

        [Test]
        public void Apply_BothBases_CutByTheKernel_HaveCorrespondingNodesSurfaceAndCapBoundaries()
        {
            var g = Fixture();
            var u = FbxUnityBasisChange.Apply(g);
            foreach (var plane in Planes())
            {
                using (var hf = new MeshCutHarness())
                using (var hu = new MeshCutHarness())
                {
                    var cf = hf.Place(ToSynthetic(g), g.Name + "-fbx", 3, 5);
                    var cu = hu.Place(ToSynthetic(u), u.Name + "-unity", 3, 5);
                    var rf = hf.Cut(cf, plane);
                    var ru = hu.Cut(cu, FbxUnityBasisChange.Plane(plane));
                    VerifyOk(hf, rf, "fbx basis " + plane);
                    VerifyOk(hu, ru, "unity basis " + plane);
                    Assert.That(rf.Result.crossingTriangles, Is.GreaterThan(0), "the plane must cut the fixture");
                    Assert.That(ru.Result.crossingTriangles, Is.EqualTo(rf.Result.crossingTriangles), "K");
                    Assert.That(ru.Result.nodeCount, Is.EqualTo(rf.Result.nodeCount), "nodes");
                    Assert.That(ru.Result.loopCount, Is.EqualTo(rf.Result.loopCount), "loops");
                    Assert.That(ru.Result.positive.indexCount, Is.EqualTo(rf.Result.positive.indexCount), "positive side index count");
                    Assert.That(ru.Result.negative.indexCount, Is.EqualTo(rf.Result.negative.indexCount), "negative side index count");

                    // nodes: the same topology edges are crossed at the same parameter (edge keys are control point ids in both bases)
                    var nodeU = new Dictionary<long, int>();
                    for (int n = 0; n < ru.Result.nodeCount; n++) nodeU[ru.NodeKeys[n]] = n;
                    var mapNode = new int[rf.Result.nodeCount];
                    for (int n = 0; n < rf.Result.nodeCount; n++)
                    {
                        Assert.That(nodeU.TryGetValue(rf.NodeKeys[n], out int m), Is.True, "node " + n + " edge key exists in the unity basis");
                        Assert.That(ru.NodeParams[m], Is.EqualTo(rf.NodeParams[n]).Within(1e-6f), "node " + n + " parameter");
                        mapNode[n] = m;
                    }
                    int Map(int id) => id < rf.TopologyBase ? id : (id - rf.TopologyBase < rf.Result.nodeCount ? ru.TopologyBase + mapNode[id - rf.TopologyBase] : int.MinValue);

                    // surface and cap boundaries per side: the unity side must show the reversed directed boundary of the fbx side
                    foreach (var (label, sf, su) in new[] { ("positive", rf.Positive, ru.Positive), ("negative", rf.Negative, ru.Negative) })
                    {
                        Split(rf, sf, out var surfF, out var capF);
                        Split(ru, su, out var surfU, out var capU);
                        var mappedSurfF = new List<(int, int, int)>(); foreach (var (a, b, c) in surfF) mappedSurfF.Add((Map(a), Map(b), Map(c)));
                        var mappedCapF = new List<(int, int, int)>(); foreach (var (a, b, c) in capF) mappedCapF.Add((Map(a), Map(b), Map(c)));
                        Assert.That(surfU.Count, Is.EqualTo(surfF.Count), label + ": surface triangle count");
                        Assert.That(capU.Count, Is.EqualTo(capF.Count), label + ": cap triangle count");
                        Assert.That(DirectedBoundary(surfU, false), Is.EquivalentTo(DirectedBoundary(mappedSurfF, true)), label + ": surface boundary");
                        Assert.That(DirectedBoundary(capU, false), Is.EquivalentTo(DirectedBoundary(mappedCapF, true)), label + ": cap boundary");
                        // the cap closes the surface: its boundary is the surface boundary walked the other way (DESIGN 6.4)
                        Assert.That(DirectedBoundary(capU, true), Is.EquivalentTo(DirectedBoundary(surfU, false)), label + ": cap boundary is the reversed surface boundary");
                    }
                }
            }
        }

        [Test]
        public void FromImported_KeepsRangesAndMapsMaterialNamesBySubmeshMaterialIndex()
        {
            var fixture = Fixture();
            fixture.Name = "model";   // the preparation names the geometry after the FBX model
            var mesh = ToSynthetic(fixture);
            // a file with three materials where the middle one has no triangles: the import records material 0 and 2 for the two submeshes
            var imported = new ImportedGeometry
            {
                ModelName = "model", GeometryName = "geometry", Mesh = mesh, MaterialNames = new[] { "Side", "Unused", "EndCap" }, SubmeshMaterialIndex = new[] { 0, 2 },
            };
            var raw = CommonGeometryPreparation.FromImported(imported);
            Assert.That(raw.Basis, Is.EqualTo(CommonGeometryBasis.FbxFile));
            Assert.That(raw.Name, Is.EqualTo("model"));
            Assert.That(raw.Submeshes.Length, Is.EqualTo(2));
            Assert.That(raw.Submeshes[0].MaterialName, Is.EqualTo("Side")); Assert.That(raw.Submeshes[1].MaterialName, Is.EqualTo("EndCap"));
            Assert.That(raw.Submeshes[0].MaterialIndex, Is.EqualTo(0)); Assert.That(raw.Submeshes[1].MaterialIndex, Is.EqualTo(2), "the recorded file index survives, not the ordinal");
            Assert.That(raw.Submeshes[0].IndexStart, Is.EqualTo(0)); Assert.That(raw.Submeshes[0].IndexCount, Is.EqualTo(24));
            Assert.That(raw.Submeshes[1].IndexStart, Is.EqualTo(24)); Assert.That(raw.Submeshes[1].IndexCount, Is.EqualTo(12));
            Assert.That(ReferenceEquals(raw.Vertices, mesh.Vertices) || ReferenceEquals(raw.Indices, mesh.Indices) || ReferenceEquals(raw.TopologyOfVertex, mesh.TopologyOfVertex), Is.False, "owns copies");
            fixture.Submeshes[1].MaterialIndex = 2;   // the fixture uses ordinal indices; this file records material 2 for its second submesh
            AssertSame(fixture, raw, "raw preparation equals the fixture it was built from");
            var unity = CommonGeometryPreparation.PrepareUnityBasis(imported);
            AssertSame(FbxUnityBasisChange.Apply(fixture), unity, "explicit unity-basis preparation");
            Assert.That(mesh.Indices, Is.EqualTo(fixture.Indices), "the imported mesh is untouched");
        }

        [Test]
        public void FromImported_Rejects_MissingOrRaggedSubmeshMaterialRecord_WithoutTouchingTheInput()
        {
            foreach (var (label, record) in new (string, int[])[] { ("missing", null), ("empty", new int[0]), ("too short", new[] { 0 }), ("too long", new[] { 0, 1, 1 }) })
            {
                var imported = new ImportedGeometry { ModelName = "model", GeometryName = "geometry", Mesh = ToSynthetic(Fixture()), MaterialNames = new[] { "Side", "EndCap" }, SubmeshMaterialIndex = record };
                var before = SnapshotImported(imported);
                Assert.Throws<ArgumentException>(() => CommonGeometryPreparation.FromImported(imported), label);
                Assert.Throws<ArgumentException>(() => CommonGeometryPreparation.PrepareUnityBasis(imported), label + " (unity basis)");
                AssertImportedUnchanged(before, imported, label);
            }
        }

        [Test]
        public void FromImported_Rejects_MaterialIndexOutsideTheMaterialList_OrNegative_WithoutTouchingTheInput()
        {
            foreach (var (label, names, record) in new (string, string[], int[])[]
            {
                ("index past the list", new[] { "Side", "EndCap" }, new[] { 0, 2 }),
                ("index at the list length", new[] { "Only" }, new[] { 0, 1 }),
                ("negative with a list", new[] { "Side", "EndCap" }, new[] { -1, 0 }),
                ("negative without a list", Array.Empty<string>(), new[] { 0, -1 }),
            })
            {
                var imported = new ImportedGeometry { ModelName = "model", GeometryName = "geometry", Mesh = ToSynthetic(Fixture()), MaterialNames = names, SubmeshMaterialIndex = record };
                var before = SnapshotImported(imported);
                Assert.Throws<ArgumentException>(() => CommonGeometryPreparation.FromImported(imported), label);
                AssertImportedUnchanged(before, imported, label);
            }
        }

        [Test]
        public void FromImported_WithoutMaterialList_KeepsTheRecordedMappingWithEmptyNames()
        {
            foreach (var names in new[] { Array.Empty<string>(), null })
            {
                var imported = new ImportedGeometry { ModelName = "model", GeometryName = "geometry", Mesh = ToSynthetic(Fixture()), MaterialNames = names, SubmeshMaterialIndex = new[] { 3, 7 } };
                var before = SnapshotImported(imported);
                var raw = CommonGeometryPreparation.FromImported(imported);
                Assert.That(raw.Submeshes.Length, Is.EqualTo(2));
                Assert.That(raw.Submeshes[0].MaterialIndex, Is.EqualTo(3)); Assert.That(raw.Submeshes[1].MaterialIndex, Is.EqualTo(7));
                Assert.That(raw.Submeshes[0].MaterialName, Is.EqualTo("")); Assert.That(raw.Submeshes[1].MaterialName, Is.EqualTo(""));
                Assert.That(raw.Submeshes[0].IndexCount, Is.EqualTo(24)); Assert.That(raw.Submeshes[1].IndexStart, Is.EqualTo(24));
                Assert.That(raw.Validate(), Is.Empty);
                AssertImportedUnchanged(before, imported, names == null ? "null list" : "empty list");
            }
        }

        [Test]
        public void Apply_Rejects_UndefinedBasisAndStructuralProblems_BeforeProducingOutput_WithoutTouchingTheInput()
        {
            var cases = new (string label, Action<PreparedCommonGeometry> damage)[]
            {
                ("undefined basis 7", g => g.Basis = (CommonGeometryBasis)7),
                ("undefined basis -1", g => g.Basis = (CommonGeometryBasis)(-1)),
                ("index past the vertices", g => g.Indices[5] = (uint)g.Vertices.Length),
                ("index count not a multiple of 3", g => g.Indices = new uint[] { 0, 1, 2, 3 }),
                ("topology id past the count", g => g.TopologyOfVertex[3] = g.TopologyVertexCount),
                ("negative topology id", g => g.TopologyOfVertex[0] = -1),
                ("topology map length", g => g.TopologyOfVertex = new int[g.Vertices.Length - 1]),
                ("ragged submesh start", g => g.Submeshes[1].IndexStart += 3),
                ("submesh ranges not covering the indices", g => g.Submeshes[1].IndexCount -= 3),
                ("negative material index", g => g.Submeshes[0].MaterialIndex = -1),
                ("missing array", g => g.Submeshes = null),
            };
            foreach (var (label, damage) in cases)
            {
                var g = Fixture();
                damage(g);
                Assert.That(g.Validate(), Is.Not.Empty, label + ": Validate reports the problem");
                var before = g.Submeshes != null ? Snapshot(g) : null;
                var arrays = (g.Vertices, g.Indices, g.TopologyOfVertex, g.Submeshes);
                Assert.Throws<ArgumentException>(() => FbxUnityBasisChange.Apply(g), label);
                Assert.That(ReferenceEquals(arrays.Vertices, g.Vertices) && ReferenceEquals(arrays.Indices, g.Indices) && ReferenceEquals(arrays.TopologyOfVertex, g.TopologyOfVertex) && ReferenceEquals(arrays.Submeshes, g.Submeshes), Is.True, label + ": arrays not replaced");
                if (before != null) AssertSame(before, g, label + ": input after the rejected Apply");
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => FbxUnityBasisChange.Other((CommonGeometryBasis)2));
            Assert.Throws<ArgumentNullException>(() => FbxUnityBasisChange.Apply(null));
        }

        [Test]
        public void TheCommonVertex_Is32ByteVpRenderVertex_AndMeshCutDefinesNoVertexOfItsOwn()
        {
            Assert.That(VpRenderVertex.Stride, Is.EqualTo(32));
            Assert.That(UnsafeUtility.SizeOf<VpRenderVertex>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<VpRenderVertex>(), Is.EqualTo(32));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("position").ToInt32(), Is.EqualTo(0));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("normal").ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.OffsetOf<VpRenderVertex>("uv0").ToInt32(), Is.EqualTo(24));
            FieldInfo[] fields = typeof(VpRenderVertex).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(3), "exactly three instance fields");
            Assert.That(fields.Select(f => f.Name), Is.EquivalentTo(new[] { "position", "normal", "uv0" }), "position, normal and uv0, and nothing else");
            // the three offsets above are asserted one by one, so neither the field order nor this list decides the layout

            // every stage of the common path uses that one type, and the cut kernel no longer carries a vertex of its own
            Assert.That(typeof(PreparedCommonGeometry).GetField("Vertices").FieldType, Is.EqualTo(typeof(VpRenderVertex[])));
            Assert.That(typeof(SyntheticMesh).GetField("Vertices").FieldType, Is.EqualTo(typeof(VpRenderVertex[])));
            Assert.That(typeof(PoolArrays).GetField("Vertices").FieldType, Is.EqualTo(typeof(VpRenderVertex[])));
            Assert.That(typeof(MeshCutKernel).Assembly.GetTypes().Where(t => t.Name == "RenderVertex"), Is.Empty,
                "Zantetsu.MeshCut must not define a render vertex of its own beside the common one");
            Assert.That(typeof(VpRenderVertex).GetFields().Any(f => f.Name.ToLowerInvariant().Contains("tangent")), Is.False);
        }

        [Test]
        public void Cut_InBothBases_KeepsUv0InRange_AndNormalsFiniteWithOutwardWinding()
        {
            var g = Fixture();
            var u = FbxUnityBasisChange.Apply(g);
            float2 uvMin = new float2(float.MaxValue), uvMax = new float2(float.MinValue);
            foreach (var v in g.Vertices) { uvMin = math.min(uvMin, (float2)v.uv0); uvMax = math.max(uvMax, (float2)v.uv0); }
            float4 plane = Planes()[0];
            foreach (var (label, geometry, p) in new[] { ("fbx", g, plane), ("unity", u, FbxUnityBasisChange.Plane(plane)) })
            {
                using (var h = new MeshCutHarness())
                {
                    var cg = h.Place(ToSynthetic(geometry), label, 3, 5);
                    var run = h.Cut(cg, p);
                    VerifyOk(h, run, label);
                    int capCorners = 0, surfaceCorners = 0, wound = 0;
                    foreach (var side in new[] { run.Positive, run.Negative })
                        foreach (var (x, y, z, _) in side.Triangles())
                        {
                            foreach (uint vertex in new[] { x, y, z })
                            {
                                VpRenderVertex rv = side.Vertices[vertex];
                                Assert.That(math.all(math.isfinite((float3)rv.normal)), Is.True, label + ": vertex " + vertex + " normal is finite");
                                Assert.That(math.lengthsq((float3)rv.normal), Is.GreaterThan(1e-12f), label + ": vertex " + vertex + " normal is not degenerate");
                                Assert.That(math.all(math.isfinite((float2)rv.uv0)), Is.True, label + ": vertex " + vertex + " uv0 is finite");
                                if (IsCap(run, vertex))
                                {
                                    capCorners++;
                                    Assert.That(rv.uv0.x, Is.EqualTo(RenderCutMarker.CapUvX), label + ": cap marker x");
                                    Assert.That(rv.uv0.y, Is.EqualTo(RenderCutMarker.CapUvY), label + ": cap marker y");
                                }
                                else
                                {
                                    surfaceCorners++;
                                    // the cut interpolates in float (w * a + f * b, w = 1 - f), so an endpoint value can land a few ULP outside
                                    Assert.That(rv.uv0.x, Is.InRange(uvMin.x - k_uvEpsilon, uvMax.x + k_uvEpsilon), label + ": surface uv0.x stays inside the input range");
                                    Assert.That(rv.uv0.y, Is.InRange(uvMin.y - k_uvEpsilon, uvMax.y + k_uvEpsilon), label + ": surface uv0.y stays inside the input range");
                                }
                            }
                            float3 pa = side.Vertices[x].position, pb = side.Vertices[y].position, pc = side.Vertices[z].position;
                            float3 facet = math.cross(pb - pa, pc - pa);
                            float3 normals = (float3)side.Vertices[x].normal + (float3)side.Vertices[y].normal + (float3)side.Vertices[z].normal;
                            // slivers below this length are float noise on a fixture of unit scale and carry no orientation
                            if (math.length(facet) > 1e-7f) { wound++; Assert.That(math.dot(facet, normals), Is.GreaterThan(0f), label + ": triangle winding still presents the side its normals face"); }
                        }
                    Assert.That(capCorners, Is.GreaterThan(0), label + ": the plane must produce caps");
                    Assert.That(surfaceCorners, Is.GreaterThan(0));
                    Assert.That(wound, Is.GreaterThan(0));
                }
            }
        }

        // ------------------------------------------------------------------ intake: what identifies a render vertex
        [Test]
        public void Intake_CornersDifferingOnlyInTangent_BecomeOneRenderVertex()
        {
            string path = WriteSyntheticFbx(withTangents: true);
            try
            {
                var g = FbxGeometryImport.Import(path).Geometries[0];
                Assert.That(g.HasTangent, Is.True, "the file carries a tangent layer");
                Assert.That(g.ControlPoints, Is.EqualTo(4));
                Assert.That(g.Polygons, Is.EqualTo(2));
                // six corners, but the two corners of control point 0 and the two of control point 2 share normal and
                // uv0 and differ only in tangent, so each pair is one render vertex: one per control point
                Assert.That(g.RenderVertices, Is.EqualTo(4), "corners that differ only in tangent must merge");
                Assert.That(g.Mesh.Vertices.Length, Is.EqualTo(4));
                Assert.That(g.Mesh.TopologyOfVertex, Is.EqualTo(new[] { 0, 1, 2, 3 }));
                Assert.That(g.Mesh.Indices.Length, Is.EqualTo(6));
                Assert.That(g.Mesh.SubmeshIndexCounts, Is.EqualTo(new[] { 3, 3 }));
                Assert.That(g.SubmeshMaterialIndex, Is.EqualTo(new[] { 0, 1 }));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Intake_TangentPresence_ChangesTheDiagnosticOnly()
        {
            string withTangent = WriteSyntheticFbx(withTangents: true);
            string withoutTangent = WriteSyntheticFbx(withTangents: false);
            try
            {
                var a = FbxGeometryImport.Import(withTangent).Geometries[0];
                var b = FbxGeometryImport.Import(withoutTangent).Geometries[0];

                Assert.That(a.HasTangent, Is.True);
                Assert.That(b.HasTangent, Is.False, "the diagnostic is the only thing the tangent layer decides");
                Assert.That(a.TangentMapping, Is.EqualTo("ByPolygonVertex"));
                Assert.That(b.TangentMapping, Is.Null);

                Assert.That(b.RenderVertices, Is.EqualTo(a.RenderVertices), "render vertex count");
                Assert.That(b.Mesh.Vertices.Length, Is.EqualTo(a.Mesh.Vertices.Length));
                for (int i = 0; i < a.Mesh.Vertices.Length; i++)
                {
                    Assert.That(b.Mesh.Vertices[i].position, Is.EqualTo(a.Mesh.Vertices[i].position), "position " + i);
                    Assert.That(b.Mesh.Vertices[i].normal, Is.EqualTo(a.Mesh.Vertices[i].normal), "normal " + i);
                    Assert.That(b.Mesh.Vertices[i].uv0, Is.EqualTo(a.Mesh.Vertices[i].uv0), "uv0 " + i);
                }
                Assert.That(b.Mesh.Indices, Is.EqualTo(a.Mesh.Indices), "index order");
                Assert.That(b.Mesh.TopologyOfVertex, Is.EqualTo(a.Mesh.TopologyOfVertex), "topology mapping");
                Assert.That(b.Mesh.SubmeshIndexCounts, Is.EqualTo(a.Mesh.SubmeshIndexCounts), "submesh ranges");
                Assert.That(b.SubmeshMaterialIndex, Is.EqualTo(a.SubmeshMaterialIndex), "material mapping");

                // and the prepared common geometry that comes out of it is identical too
                AssertSame(CommonGeometryPreparation.PrepareUnityBasis(a), CommonGeometryPreparation.PrepareUnityBasis(b),
                    "prepared geometry with and without the file tangent layer");
            }
            finally { File.Delete(withTangent); File.Delete(withoutTangent); }
        }

        /// <summary>
        /// A two-triangle quad written as a Kaydara FBX binary (version 7400): four control points, six corners with
        /// one flat normal and one uv0 per control point, per-polygon materials, and optionally a tangent layer whose
        /// values differ between the two polygons. Public synthetic input: the bytes are built here.
        /// </summary>
        static string WriteSyntheticFbx(bool withTangents)
        {
            var vertices = new double[] { 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1 };
            var polygons = new[] { 0, 1, -3, 0, 2, -4 };            // (0,1,2) and (0,2,3); the last corner of each is negated
            var corners = new[] { 0, 1, 2, 0, 2, 3 };
            var uvOfControlPoint = new[] { new float2(0, 0), new float2(1, 0), new float2(1, 1), new float2(0, 1) };
            var normals = new List<double>();
            var uvs = new List<double>();
            var tangents = new List<double>();
            for (int c = 0; c < corners.Length; c++)
            {
                normals.AddRange(new[] { 0.0, 1.0, 0.0 });
                uvs.AddRange(new[] { (double)uvOfControlPoint[corners[c]].x, (double)uvOfControlPoint[corners[c]].y });
                // the two polygons carry different tangents, so the shared control points differ in tangent alone
                tangents.AddRange(c < 3 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 0.0, 1.0 });
            }

            var geometry = new FbxOut("Geometry", FbxOut.L(1001), FbxOut.S("SyntheticQuad"), FbxOut.S("Mesh"));
            geometry.Kids.Add(new FbxOut("Vertices", FbxOut.D(vertices)));
            geometry.Kids.Add(new FbxOut("PolygonVertexIndex", FbxOut.I(polygons)));
            geometry.Kids.Add(Layer("LayerElementNormal", "Normals", normals.ToArray()));
            geometry.Kids.Add(Layer("LayerElementUV", "UV", uvs.ToArray()));
            if (withTangents) geometry.Kids.Add(Layer("LayerElementTangent", "Tangents", tangents.ToArray()));
            var material = new FbxOut("LayerElementMaterial");
            material.Kids.Add(new FbxOut("MappingInformationType", FbxOut.S("ByPolygon")));
            material.Kids.Add(new FbxOut("ReferenceInformationType", FbxOut.S("IndexToDirect")));
            material.Kids.Add(new FbxOut("Materials", FbxOut.I(new[] { 0, 1 })));
            geometry.Kids.Add(material);

            var objects = new FbxOut("Objects");
            objects.Kids.Add(geometry);

            string path = Path.Combine(Path.GetTempPath(), "zantetsu-synthetic-" + Guid.NewGuid().ToString("N") + ".fbx");
            File.WriteAllBytes(path, FbxOut.File(objects));
            return path;
        }

        static FbxOut Layer(string node, string dataName, double[] data)
        {
            var layer = new FbxOut(node);
            layer.Kids.Add(new FbxOut("MappingInformationType", FbxOut.S("ByPolygonVertex")));
            layer.Kids.Add(new FbxOut("ReferenceInformationType", FbxOut.S("Direct")));
            layer.Kids.Add(new FbxOut(dataName, FbxOut.D(data)));
            return layer;
        }

        /// <summary>Minimal writer of the FBX binary container the reference intake reads (pre-7500 records, uncompressed arrays).</summary>
        sealed class FbxOut
        {
            readonly string m_name;
            readonly List<byte[]> m_properties = new List<byte[]>();
            public readonly List<FbxOut> Kids = new List<FbxOut>();

            public FbxOut(string name, params byte[][] properties)
            {
                m_name = name;
                foreach (var p in properties) m_properties.Add(p);
            }

            public static byte[] S(string v) { byte[] b = Encoding.UTF8.GetBytes(v); return Join(new byte[] { (byte)'S' }, BitConverter.GetBytes(b.Length), b); }
            public static byte[] L(long v) { return Join(new byte[] { (byte)'L' }, BitConverter.GetBytes(v)); }

            public static byte[] D(double[] v)
            {
                var raw = new byte[v.Length * 8];
                Buffer.BlockCopy(v, 0, raw, 0, raw.Length);
                return Join(new byte[] { (byte)'d' }, BitConverter.GetBytes(v.Length), BitConverter.GetBytes(0), BitConverter.GetBytes(raw.Length), raw);
            }

            public static byte[] I(int[] v)
            {
                var raw = new byte[v.Length * 4];
                Buffer.BlockCopy(v, 0, raw, 0, raw.Length);
                return Join(new byte[] { (byte)'i' }, BitConverter.GetBytes(v.Length), BitConverter.GetBytes(0), BitConverter.GetBytes(raw.Length), raw);
            }

            public static byte[] File(params FbxOut[] roots)
            {
                var file = new List<byte>();
                file.AddRange(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
                file.Add(0x00); file.Add(0x1A); file.Add(0x00);
                file.AddRange(BitConverter.GetBytes(7400u));
                foreach (var r in roots) file.AddRange(r.Serialize(file.Count));
                file.AddRange(new byte[13]);                        // end of the root record list
                return file.ToArray();
            }

            byte[] Serialize(int at)
            {
                byte[] properties = Join(m_properties.ToArray());
                int position = at + 12 + 1 + m_name.Length + properties.Length;
                var kids = new List<byte[]>();
                foreach (var k in Kids) { byte[] b = k.Serialize(position); kids.Add(b); position += b.Length; }
                if (Kids.Count > 0) { kids.Add(new byte[13]); position += 13; }
                var record = new List<byte>();
                record.AddRange(BitConverter.GetBytes((uint)position));
                record.AddRange(BitConverter.GetBytes((uint)m_properties.Count));
                record.AddRange(BitConverter.GetBytes((uint)properties.Length));
                record.Add((byte)m_name.Length);
                record.AddRange(Encoding.ASCII.GetBytes(m_name));
                record.AddRange(properties);
                foreach (var k in kids) record.AddRange(k);
                return record.ToArray();
            }

            static byte[] Join(params byte[][] parts)
            {
                var all = new List<byte>();
                foreach (var p in parts) all.AddRange(p);
                return all.ToArray();
            }
        }

        // ------------------------------------------------------------------------------------------ helpers
        /// <summary>Test-side adapter into the verification harness (SyntheticMesh is not part of the API).</summary>
        static SyntheticMesh ToSynthetic(PreparedCommonGeometry g)
        {
            var m = new SyntheticMesh { Vertices = (VpRenderVertex[])g.Vertices.Clone(), Indices = (uint[])g.Indices.Clone(), TopologyOfVertex = (int[])g.TopologyOfVertex.Clone(), TopologyVertexCount = g.TopologyVertexCount };
            foreach (var s in g.Submeshes) m.SubmeshIndexCounts.Add(s.IndexCount);
            return m;
        }

        static void VerifyOk(MeshCutHarness h, CutRun run, string what)
        {
            Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.Ok), what + ": status");
            Assert.That(h.LastBadGuards, Is.Empty, what + ": guards");
            Assert.That(h.LastUnrelatedChanges, Is.Empty, what + ": unrelated changes");
            var report = MeshCutVerifier.Verify(run);
            Assert.That(report.Passed, Is.True, what + ": " + report);
        }

        static void Split(CutRun run, CutGeometry side, out List<(int, int, int)> surface, out List<(int, int, int)> cap)
        {
            surface = new List<(int, int, int)>(); cap = new List<(int, int, int)>();
            foreach (var (a, b, c, _) in side.Triangles())
            {
                bool isCap = IsCap(run, a) && IsCap(run, b) && IsCap(run, c);
                (isCap ? cap : surface).Add((side.TopologyOf(a), side.TopologyOf(b), side.TopologyOf(c)));
            }
        }

        static bool IsCap(CutRun run, uint v)
        {
            if (!run.IsNew(v)) return false;
            var rv = run.Input.Pool.Vertices[v];
            return rv.uv0.x == RenderCutMarker.CapUvX && rv.uv0.y == RenderCutMarker.CapUvY;
        }

        /// <summary>Directed edges walked exactly once (interior edges are walked once each way and cancel). Aux ids (int.MinValue) never occur in this fixture.</summary>
        static List<(int, int)> DirectedBoundary(List<(int, int, int)> tris, bool reversed)
        {
            var count = new Dictionary<(int, int), int>();
            foreach (var (x, y, z) in tris)
                foreach (var e in reversed ? new[] { (x, z), (z, y), (y, x) } : new[] { (x, y), (y, z), (z, x) })
                {
                    Assert.That(e.Item1 != int.MinValue && e.Item2 != int.MinValue, Is.True, "no aux vertex expected on this fixture");
                    count[e] = count.TryGetValue(e, out int c) ? c + 1 : 1;
                }
            var boundary = new List<(int, int)>();
            foreach (var kv in count) if (!count.ContainsKey((kv.Key.Item2, kv.Key.Item1))) boundary.Add(kv.Key);
            return boundary;
        }
    }
}
