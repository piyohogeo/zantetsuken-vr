using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// DESIGN 6 numerical contract of the display / stencil cut kernel (T-006 / T-083 numerical parts, Phase 2.9):
    /// representative closed inputs cut by several planes, on-plane and degenerate configurations, attribute seams,
    /// the fixed cap marker and its inheritance, sparse global views with non-zero bases, direct positive-then-negative
    /// placement, whole-mesh reuse, K = 0 component splits, capacity safety, scratch independence, the job path and
    /// concurrent readers of one immutable input.
    /// </summary>
    public class MeshCutKernelTests
    {
        static readonly float3 k_origin = new float3(0.37f, -0.21f, 0.11f);

        static LogicalMeshBuilder.AttributeOptions Smooth => new LogicalMeshBuilder.AttributeOptions();
        static LogicalMeshBuilder.AttributeOptions Seams => new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true };

        static float4[] Planes(float3 center, float extent)
        {
            // center, tilted ("nasty"), grazing: offsets are irrational-looking so no lattice vertex lands on the plane by accident
            return new[]
            {
                SyntheticGeometry.Plane(new float3(0, 1, 0), center + new float3(0, 0.0137f * extent, 0)),
                SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), center + new float3(0.0071f * extent, -0.0233f * extent, 0.0119f * extent)),
                SyntheticGeometry.Plane(new float3(1, 0, 0), center + new float3(0.471f * extent, 0, 0)),
            };
        }

        static void AssertClean(MeshCutHarness h, string what)
        {
            Assert.That(h.LastBadGuards, Is.Empty, what + ": guard overrun");
            Assert.That(h.LastUnrelatedChanges, Is.Empty, what + ": unrelated memory changed");
            Assert.That(h.LastInputHashAfter, Is.EqualTo(h.LastInputHashBefore), what + ": input modified");
        }

        static VerificationReport VerifyOk(MeshCutHarness h, CutRun run, string what, VerifyOptions o = null)
        {
            Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.Ok), what + ": status (required v/i/s=" + run.Result.requiredVertexCapacity + "/" + run.Result.requiredIndexCapacity + "/" + run.Result.requiredScratchBytes + ")");
            Assert.That(run.Result.executedManaged, Is.EqualTo(0), what + ": ran managed instead of Burst");
            AssertClean(h, what);
            var report = MeshCutVerifier.Verify(run, o);
            if (!report.Passed)
            {
                // the independent contour reconstruction explains which loop misbehaved
                var contours = ReferenceContours.Build(run.Input, run.Plane, ReferenceCut.Compute(run.Input, run.Plane));
                Assert.Fail(what + ":\n" + report + "\nfan fallbacks " + run.Result.capFanFallbacks + "\nreference contours:\n" + contours.Describe(run.Plane));
            }
            return report;
        }

        static IEnumerable<(string name, LogicalMeshBuilder mesh, LogicalMeshBuilder.AttributeOptions attributes)> Representative()
        {
            yield return ("box-n4", SyntheticGeometry.Box(4, new float3(1, 1.3f, 0.8f), k_origin), Smooth);
            yield return ("box-n4-seams", SyntheticGeometry.Box(4, new float3(1, 1.3f, 0.8f), k_origin), Seams);
            yield return ("sphere", SyntheticGeometry.UvSphere(23, 15, 0.9f, k_origin), Smooth);
            yield return ("sphere-seams", SyntheticGeometry.UvSphere(23, 15, 0.9f, k_origin), Seams);
            yield return ("torus", SyntheticGeometry.Torus(24, 12, 1f, 0.35f, k_origin), Smooth);
            yield return ("star-prism", SyntheticGeometry.StarPrism(8, 1f, 0.4f, 2f, 4, k_origin), Smooth);
            yield return ("lemniscate-self-intersecting", SyntheticGeometry.LemniscateTube(32, 6, 1f, 2f, k_origin), Smooth);
            yield return ("nested-shells", SyntheticGeometry.NestedShells(k_origin), Smooth);
            yield return ("bar-field", SyntheticGeometry.BarField(5, 2, 0.4f, 1f, k_origin), Smooth);
            var reversed = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin); reversed.Reverse();
            yield return ("box-reversed", reversed, Smooth);
            var coincident = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin); coincident.Append(SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin));
            yield return ("coincident-duplicate-components", coincident, Smooth);
            var overlapping = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin); overlapping.Append(SyntheticGeometry.Box(2, new float3(1.2f, 0.7f, 0.9f), k_origin + new float3(0.3f, 0.2f, -0.1f)));
            yield return ("overlapping-components", overlapping, Smooth);
            yield return ("zero-area-box", SyntheticGeometry.BoxWithZeroAreaTriangles(new float3(1, 1, 1), k_origin), Smooth);
            yield return ("touching-prism", SyntheticGeometry.TouchingPrism(2f, 4, k_origin), Smooth);
            yield return ("two-submeshes", SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin, twoSubmeshes: true), Seams);
        }

        [Test]
        public void RepresentativeInputs_ThreePlanes_SatisfyTheOutputContract()
        {
            var log = new StringBuilder();
            foreach (var (name, builder, attributes) in Representative())
            {
                var mesh = builder.Finish(attributes);
                var planes = Planes(k_origin, 1f);
                for (int p = 0; p < planes.Length; p++)
                {
                    using (var h = new MeshCutHarness())
                    {
                        var g = h.Place(mesh, name, vertexOffset: 1013, indexOffset: 2047);
                        var run = h.Cut(g, planes[p]);
                        string what = name + "/plane" + p;
                        var report = VerifyOk(h, run, what);
                        log.Append(what).Append(": ").Append(string.Join("; ", report.Notes)).Append(" us=").Append(h.LastMicroseconds.ToString("F0")).Append('\n');
                    }
                }
            }
            TestContext.Out.WriteLine(log.ToString());
        }

        [Test]
        public void PlaneThroughVertices_Edges_AndAFace_AreOrdinaryCases()
        {
            var cases = new List<(string, SyntheticMesh, float4)>();
            // vertex: a tetrahedron with one vertex exactly on the plane
            var tet = SyntheticGeometry.Tetrahedron(1f).Finish(Smooth);
            cases.Add(("tetrahedron-through-vertex", tet, SyntheticGeometry.Plane(new float3(2, 0, -1), new float3(1, 1, 1))));
            // four vertices and the equator edges: an octahedron on y = 0
            cases.Add(("octahedron-equator", SyntheticGeometry.Octahedron(1f).Finish(Smooth), SyntheticGeometry.Plane(new float3(0, 1, 0), float3.zero)));
            // an edge ring: box n = 2 through its middle lattice ring
            cases.Add(("box-middle-ring", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero).Finish(Smooth), SyntheticGeometry.Plane(new float3(0, 1, 0), float3.zero)));
            cases.Add(("box-middle-ring-seams", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero).Finish(Seams), SyntheticGeometry.Plane(new float3(0, 1, 0), float3.zero)));
            // a whole face lying in the plane: the +Y face of a box (its vertices are OnPlane and stay positive)
            cases.Add(("box-top-face-in-plane", SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero).Finish(Smooth), SyntheticGeometry.Plane(new float3(0, 1, 0), new float3(0, 0.5f, 0))));
            // the coincident pair cut across: a two-node cycle closed by the surface, no cap
            cases.Add(("coincident-pair", SyntheticGeometry.OppositeCoincidentPair(1f, float3.zero).Finish(Smooth), SyntheticGeometry.Plane(new float3(1, 0, 0), new float3(0.013f, 0, 0))));
            // zero-area triangles crossed by the plane
            cases.Add(("zero-area-crossed", SyntheticGeometry.BoxWithZeroAreaTriangles(new float3(1, 1, 1), float3.zero).Finish(Smooth), SyntheticGeometry.Plane(new float3(1, 0, 0), new float3(0.25f, 0, 0))));
            // a self-touching loop: the notch tip node lies exactly on the bottom segment (z = const keeps section coordinates exact)
            cases.Add(("touching-prism-section", SyntheticGeometry.TouchingPrism(2f, 4, float3.zero).Finish(Smooth), SyntheticGeometry.Plane(new float3(0, 0, 1), new float3(0, 0, 0.137f))));

            var log = new StringBuilder();
            foreach (var (name, mesh, plane) in cases)
            {
                using (var h = new MeshCutHarness())
                {
                    var g = h.Place(mesh, name, 5, 9);
                    var run = h.Cut(g, plane);
                    var report = VerifyOk(h, run, name);
                    Assert.That(run.Result.positive.indexCount, Is.GreaterThan(0), name + ": positive side empty");
                    Assert.That(run.Result.negative.indexCount, Is.GreaterThan(0), name + ": negative side empty");
                    log.Append(name).Append(": ").Append(string.Join("; ", report.Notes)).Append('\n');
                    if (name == "coincident-pair") Assert.That(run.Result.capCyclesClosedBySurface, Is.EqualTo(2), name + ": both sides close the two-node cycle by the surface");
                    if (name == "box-top-face-in-plane") Assert.That(run.Result.capTriangles, Is.GreaterThan(0), name + ": the OnPlane face is kept on the positive side and the hole below it is capped");
                }
            }
            TestContext.Out.WriteLine(log.ToString());
        }

        [Test]
        public void WholeMeshOnOneSide_ReusesTheInputWithoutWriting()
        {
            var mesh = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin).Finish(Seams);
            // OnPlane vertices are owned by the positive side (DESIGN 6.4): a plane tangent to the bottom face leaves the whole
            // box positive, while a plane tangent to the top face splits it (the top face and a cap form the positive side;
            // that configuration is covered by PlaneThroughVertices_Edges_AndAFace_AreOrdinaryCases).
            foreach (var (name, plane, positive) in new[]
            {
                ("far-above", SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin + new float3(0, -3, 0)), true),
                ("far-below", SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin + new float3(0, 3, 0)), false),
                ("tangent-bottom-face", SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin + new float3(0, -0.5f, 0)), true),
                ("tangent-top-face-reversed-plane", SyntheticGeometry.Plane(new float3(0, -1, 0), k_origin + new float3(0, 0.5f, 0)), true),
            })
            {
                using (var h = new MeshCutHarness())
                {
                    var g = h.Place(mesh, name, 100, 300);
                    var run = h.Cut(g, plane);
                    var report = VerifyOk(h, run, name);
                    Assert.That(h.LastCapacity.wholeMeshSide, Is.EqualTo(positive ? 1 : -1), name + ": capacity query announces the reuse");
                    Assert.That(h.LastCapacity.newIndices, Is.EqualTo(0), name);
                    Assert.That((positive ? run.Result.positive : run.Result.negative).reusesInput, Is.EqualTo(1), name);
                    Assert.That((positive ? run.Result.negative : run.Result.positive).indexCount, Is.EqualTo(0), name + ": the other side is truly empty");
                    Assert.That(run.Result.newIndexCount, Is.EqualTo(0), name);
                    Assert.That(run.Result.newVertexCount, Is.EqualTo(0), name);
                }
            }
            // a mesh lying entirely in the plane: every vertex OnPlane, kept once on the positive side
            using (var h = new MeshCutHarness())
            {
                var flat = SyntheticGeometry.OppositeCoincidentPair(1f, float3.zero).Finish(Smooth);
                var g = h.Place(flat, "flat-in-plane", 3, 3);
                var run = h.Cut(g, SyntheticGeometry.Plane(new float3(0, 1, 0), float3.zero));
                VerifyOk(h, run, "flat-in-plane");
                Assert.That(run.Result.positive.reusesInput, Is.EqualTo(1));
                Assert.That(run.Result.negative.indexCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void KZero_ComponentsOnBothSides_AreSplitWithoutNodes()
        {
            using (var h = new MeshCutHarness())
            {
                var mesh = SyntheticGeometry.BarField(4, 1, 0.4f, 1f, k_origin).Finish(Smooth);
                var g = h.Place(mesh, "bars", 17, 33);
                // between the second and third bar: nothing crosses, two bars on each side
                var run = h.Cut(g, SyntheticGeometry.Plane(new float3(1, 0, 0), k_origin));
                VerifyOk(h, run, "k0");
                Assert.That(run.Result.crossingTriangles, Is.EqualTo(0));
                Assert.That(run.Result.nodeCount, Is.EqualTo(0));
                Assert.That(run.Result.newVertexCount, Is.EqualTo(0));
                Assert.That(run.Result.positive.indexCount, Is.EqualTo(mesh.Indices.Length / 2));
                Assert.That(run.Result.negative.indexCount, Is.EqualTo(mesh.Indices.Length / 2));
                Assert.That(run.Result.newIndexCount, Is.EqualTo(mesh.Indices.Length));
                Assert.That(run.Result.capTriangles, Is.EqualTo(0));
            }
        }

        [Test]
        public void Seams_KeepSidesApart_AndCapsAreHardEdged()
        {
            var mesh = SyntheticGeometry.UvSphere(23, 15, 0.9f, k_origin).Finish(Seams);
            Assert.That(mesh.Vertices.Length, Is.GreaterThan(mesh.TopologyVertexCount), "the seam layer splits render vertices");
            using (var h = new MeshCutHarness())
            {
                var g = h.Place(mesh, "sphere-seams", 41, 77);
                var run = h.Cut(g, SyntheticGeometry.Plane(new float3(0.2f, 1, 0.1f), k_origin + new float3(0, 0.05f, 0)));
                var report = VerifyOk(h, run, "seams");
                Assert.That(run.Result.interpolatedVertices, Is.GreaterThan(run.Result.nodeCount), "the UV seam yields a second render vertex on the seam edge");
                Assert.That(run.Result.capRenderVertices, Is.EqualTo(2 * run.Result.nodeCount), "one cap render vertex per node and side");
                TestContext.Out.WriteLine(string.Join("; ", report.Notes));
            }
        }

        [Test]
        public void NegativeSourceUv_IsNotRejected_AndCapMarkerIsInheritedByARecut()
        {
            var mesh = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin).Finish(new LogicalMeshBuilder.AttributeOptions { UvOffset = new float2(-2f, -1.5f) });
            using (var h = new MeshCutHarness())
            {
                var g = h.Place(mesh, "negative-uv", 7, 11);
                var first = h.Cut(g, SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin + new float3(0, 0.0137f, 0)));
                VerifyOk(h, first, "negative-uv first cut");
                // interpolated surface vertices keep negative UVs (no clamp, no marker); cap vertices carry the marker
                int negativeInterpolated = 0;
                foreach (uint v in first.Positive.ReferencedVertices())
                    if (first.IsNew(v) && h.Pool.Vertices[v].uv0.x < -1f) negativeInterpolated++;
                Assert.That(negativeInterpolated, Is.GreaterThan(0), "interpolated surface vertices keep the source's negative UV");

                // second cut through the first cap: the first cap's render vertices carry the marker, so every node
                // interpolated on the cap's face side inherits exactly (-0.5, 0) by ordinary attribute interpolation
                var firstCapVertices = new HashSet<uint>();
                foreach (var (a, b, c, _) in first.Positive.Triangles())
                    if (first.IsNew(a) && first.IsNew(b) && first.IsNew(c)) { firstCapVertices.Add(a); firstCapVertices.Add(b); firstCapVertices.Add(c); }
                Assert.That(firstCapVertices.Count, Is.GreaterThan(0));
                var second = h.Cut(first.Positive, SyntheticGeometry.Plane(new float3(1, 0, 0.05f), k_origin + new float3(0.0231f, 0, 0)));
                VerifyOk(h, second, "negative-uv second cut");
                int onOldCap = 0, clippedCapTriangles = 0;
                foreach (var (a, b, c, _) in second.Positive.Triangles())
                {
                    uint[] corners = { a, b, c };
                    bool anyOldCap = false, anyOther = false, anyNew = false;
                    foreach (uint v in corners)
                    {
                        if (second.IsNew(v)) { anyNew = true; continue; }
                        if (firstCapVertices.Contains(v)) anyOldCap = true; else anyOther = true;
                    }
                    if (!anyOldCap || anyOther || !anyNew) continue;   // a clipped piece of a first-cut cap triangle
                    clippedCapTriangles++;
                    foreach (uint v in corners)
                    {
                        if (!second.IsNew(v)) continue;
                        onOldCap++;
                        Assert.That(h.Pool.Vertices[v].uv0.x, Is.EqualTo(RenderCutMarker.CapUvX), "recut cap node keeps the marker");
                        Assert.That(h.Pool.Vertices[v].uv0.y, Is.EqualTo(RenderCutMarker.CapUvY));
                    }
                }
                Assert.That(clippedCapTriangles, Is.GreaterThan(0), "the second plane crosses the first cap");
                Assert.That(onOldCap, Is.GreaterThan(0));
            }
        }

        [Test]
        public void RecutChain_ThreeGenerations_ReferencesExistingVerticesAndAppendsBlocks()
        {
            var log = new StringBuilder();
            var coincident = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin); coincident.Append(SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin));
            var pairs = SyntheticGeometry.OppositeCoincidentPair(1f, k_origin); pairs.Append(SyntheticGeometry.OppositeCoincidentPair(0.7f, k_origin + new float3(0.1f, 0.2f, 0)));
            foreach (var (name, builder, attributes, seed) in new (string, LogicalMeshBuilder, LogicalMeshBuilder.AttributeOptions, uint)[]
            {
                ("sphere-seams", SyntheticGeometry.UvSphere(19, 11, 0.8f, k_origin), Seams, 19),
                ("lemniscate-self-intersecting", SyntheticGeometry.LemniscateTube(48, 6, 1f, 2f, k_origin), Seams, 7),
                ("coincident-duplicate-components", coincident, Smooth, 23),
                ("coincident-pairs", pairs, Smooth, 5),
                ("nested-shells", SyntheticGeometry.NestedShells(k_origin), Smooth, 11),
                ("touching-prism", SyntheticGeometry.TouchingPrism(2f, 4, k_origin), Seams, 3),
            })
            using (var h = new MeshCutHarness())
            {
                var mesh = builder.Finish(attributes);
                CutGeometry current = h.Place(mesh, name, 523, 1201);
                var rng = new Unity.Mathematics.Random(seed);
                for (int gen = 1; gen <= 4; gen++)
                {
                    var (mn, mx) = current.Bounds();
                    float3 c = (float3)(0.5 * (mn + mx));
                    float3 n = math.normalize(new float3(rng.NextFloat(-1, 1), rng.NextFloat(-1, 1), rng.NextFloat(-1, 1)));
                    var run = h.Cut(current, SyntheticGeometry.Plane(n, c + 0.013f * (float)math.cmax(mx - mn) * n));
                    var report = VerifyOk(h, run, name + " gen " + gen);
                    Assert.That(run.Result.crossingTriangles, Is.GreaterThan(0), name + " gen " + gen + " must cut something");
                    // existing vertices are referenced by their global numbers: every old vertex referenced by a child is below the new block
                    int inherited = 0, appended = 0;
                    foreach (uint v in run.Positive.ReferencedVertices()) { if (run.IsNew(v)) appended++; else inherited++; }
                    Assert.That(inherited, Is.GreaterThan(0), name + " gen " + gen + " inherits");
                    Assert.That(appended, Is.GreaterThan(0), name + " gen " + gen + " appends");
                    Assert.That(run.Positive.Topology.Count, Is.EqualTo(gen + 1), name + " gen " + gen + ": one topology block per generation");
                    log.Append(name).Append(" gen ").Append(gen).Append(": ").Append(string.Join("; ", report.Notes)).Append(" blocks=").Append(run.Positive.Topology.Count).Append('\n');
                    current = run.Positive.TriangleCount >= run.Negative.TriangleCount ? run.Positive : run.Negative;
                }
            }
            TestContext.Out.WriteLine(log.ToString());
        }

        [Test]
        public void TwoSubmeshes_KeepTheirRanges_AndCapsJoinTheLastNonEmptyRange()
        {
            var mesh = SyntheticGeometry.Box(3, new float3(1, 1, 1), k_origin, twoSubmeshes: true).Finish(Smooth);
            Assert.That(mesh.SubmeshIndexCounts.Count, Is.EqualTo(2));
            using (var h = new MeshCutHarness())
            {
                var g = h.Place(mesh, "two-submeshes", 61, 121);
                var run = h.Cut(g, SyntheticGeometry.Plane(new float3(0.3f, 1, 0.2f), k_origin + new float3(0, 0.0137f, 0)));
                VerifyOk(h, run, "two-submeshes");
                for (int side = 0; side < 2; side++)
                {
                    var r0 = run.OutputRanges[side * 2];
                    var r1 = run.OutputRanges[side * 2 + 1];
                    Assert.That(r0.indexCount, Is.GreaterThan(0));
                    Assert.That(r1.indexCount, Is.GreaterThan(0));
                    Assert.That(r1.indexStart, Is.EqualTo(r0.indexStart + (uint)r0.indexCount), "ranges are contiguous");
                    // the second (last non-empty) range holds the caps: it ends at the side's end
                    var s = side == 0 ? run.Result.positive : run.Result.negative;
                    Assert.That(r1.indexStart + (uint)r1.indexCount, Is.EqualTo(s.indexStart + (uint)s.indexCount));
                    // no triangle of range 0 is a cap
                    for (int i = 0; i < r0.indexCount; i += 3)
                        Assert.That(run.IsNew(h.Pool.Indices[r0.indexStart + i]) && run.IsNew(h.Pool.Indices[r0.indexStart + i + 1]) && run.IsNew(h.Pool.Indices[r0.indexStart + i + 2]), Is.False, "a cap triangle inside the first submesh range");
                }
            }
        }

        [Test]
        public void CapacityDeficits_FailBeforeWriting_AndReportTheExactNeed()
        {
            var mesh = SyntheticGeometry.LemniscateTube(32, 6, 1f, 2f, k_origin).Finish(Seams);
            var plane = SyntheticGeometry.Plane(new float3(0.1f, 0.05f, 1f), k_origin + new float3(0, 0, 0.0137f));
            int needV, needI;
            using (var h = new MeshCutHarness())
            {
                var g = h.Place(mesh, "lemniscate", 9, 15);
                var ok = h.Cut(g, plane);
                VerifyOk(h, ok, "reference run");
                needV = ok.Result.newVertexCount; needI = ok.Result.newIndexCount;
                Assert.That(needV, Is.LessThanOrEqualTo(h.LastCapacity.newVertices), "capacity estimate covers the vertices");
                Assert.That(needI, Is.LessThanOrEqualTo(h.LastCapacity.newIndices), "capacity estimate covers the indices");
                Assert.That(ok.Result.usedScratchBytes, Is.LessThanOrEqualTo(h.LastCapacity.scratchBytes), "capacity estimate covers the scratch");
            }
            foreach (var viaJob in new[] { false, true })
            {
                using (var h = new MeshCutHarness())
                {
                    var g = h.Place(mesh, "lemniscate", 9, 15);
                    var run = h.Cut(g, plane, new RunOptions { ViaJob = viaJob });
                    VerifyOk(h, run, "job=" + viaJob);
                    int deficit = h.LastCapacity.newVertices - needV + 1;
                    run = h.Cut(g, plane, new RunOptions { VertexDeficit = deficit, ViaJob = viaJob });
                    Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.CapacityVertex), "one vertex short (job=" + viaJob + ")");
                    Assert.That(run.Result.requiredVertexCapacity, Is.EqualTo(needV), "exact vertex need reported");
                    AssertClean(h, "vertex deficit");
                    run = h.Cut(g, plane, new RunOptions { IndexDeficit = h.LastCapacity.newIndices - needI + 1, ViaJob = viaJob });
                    Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.CapacityIndex), "one index short");
                    Assert.That(run.Result.requiredIndexCapacity, Is.EqualTo(needI), "exact index need reported");
                    AssertClean(h, "index deficit");
                    run = h.Cut(g, plane, new RunOptions { ScratchOverride = 64, ViaJob = viaJob });
                    Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.CapacityScratch), "scratch far too small");
                    Assert.That(run.Result.requiredScratchBytes, Is.GreaterThan(64));
                    AssertClean(h, "scratch deficit");
                    // the reported requirement is enough on the next attempt (re-reserve and re-run, DESIGN 4.5.3)
                    int required = run.Result.requiredScratchBytes;
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        run = h.Cut(g, plane, new RunOptions { ScratchOverride = required, ViaJob = viaJob });
                        if (run.Result.status == MeshCutStatus.Ok) break;
                        Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.CapacityScratch), "attempt " + attempt);
                        Assert.That(run.Result.requiredScratchBytes, Is.GreaterThan(required), "the requirement grows on every retry");
                        required = run.Result.requiredScratchBytes;
                    }
                    VerifyOk(h, run, "re-reserved scratch");
                    TestContext.Out.WriteLine("scratch: estimate " + h.LastCapacity.scratchBytes + ", used " + run.Result.usedScratchBytes + ", converged at " + required + " (job=" + viaJob + ")");
                }
            }
        }

        [Test]
        public void ScratchContents_JobPath_AndRepeatedRuns_GiveBitwiseIdenticalOutput()
        {
            var mesh = SyntheticGeometry.LemniscateTube(32, 6, 1f, 2f, k_origin).Finish(Seams);
            var plane = SyntheticGeometry.Plane(new float3(0.1f, 0.05f, 1f), k_origin + new float3(0, 0, 0.0137f));
            ulong reference = 0;
            var variants = new (string, RunOptions)[]
            {
                ("fill-a5", new RunOptions { Fill = 0xA5 }),
                ("fill-00", new RunOptions { Fill = 0x00 }),
                ("fill-ff", new RunOptions { Fill = 0xFF }),
                ("scrambled", new RunOptions { ScratchScrambleSeed = 0xDEADBEEF }),
                ("scrambled-2", new RunOptions { ScratchScrambleSeed = 0x1234567 }),
                ("job", new RunOptions { ViaJob = true, ScratchScrambleSeed = 0x51DE }),
                ("extra-scratch", new RunOptions { ExtraScratch = 1 << 20 }),
            };
            foreach (var (name, opt) in variants)
            {
                using (var h = new MeshCutHarness())
                {
                    var g = h.Place(mesh, "lemniscate", 9, 15);
                    var run = h.Cut(g, plane, opt);
                    VerifyOk(h, run, name);
                    ulong hash = h.OutputHash(run);
                    if (reference == 0) reference = hash;
                    Assert.That(hash, Is.EqualTo(reference), name + ": output differs from the first variant");
                }
            }
        }

        [Test]
        public void ConcurrentJobs_ShareOneInput_AndWriteDisjointReservations()
        {
            using (var h = new MeshCutHarness())
            {
                var mesh = SyntheticGeometry.Torus(24, 12, 1f, 0.35f, k_origin).Finish(Seams);
                var g = h.Place(mesh, "torus", 88, 99);
                var planes = new[]
                {
                    SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin + new float3(0, 0.0137f, 0)),
                    SyntheticGeometry.Plane(new float3(1, 0, 0), k_origin + new float3(0.0231f, 0, 0)),
                    SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin),
                    SyntheticGeometry.Plane(new float3(0, 0, 1), k_origin + new float3(0, 0, 0.0071f)),
                };
                var runs = h.CutConcurrent(g, planes);
                Assert.That(h.LastBadGuards, Is.Empty);
                Assert.That(h.LastUnrelatedChanges, Is.Empty);
                for (int k = 0; k < runs.Length; k++)
                {
                    Assert.That(runs[k].Result.status, Is.EqualTo(MeshCutStatus.Ok), "job " + k);
                    Assert.That(runs[k].Result.executedManaged, Is.EqualTo(0), "job " + k + " ran managed");
                    var report = MeshCutVerifier.Verify(runs[k]);
                    Assert.That(report.Passed, Is.True, "job " + k + ":\n" + report);
                }
                // the same planes one at a time give the same outputs
                for (int k = 0; k < runs.Length; k++)
                {
                    using (var h2 = new MeshCutHarness())
                    {
                        var g2 = h2.Place(mesh, "torus", 88, 99);
                        var single = h2.Cut(g2, planes[k], new RunOptions { VertexGap = runs[k].NewVertexBase - (uint)g2.Vertices.Length, IndexGap = runs[k].Result.positive.indexStart - (uint)g2.Indices.Length });
                        Assert.That(single.Result.status, Is.EqualTo(MeshCutStatus.Ok));
                        Assert.That(h2.OutputHash(single), Is.EqualTo(h.OutputHash(runs[k])), "job " + k + " differs from the sequential run");
                    }
                }
            }
        }

        [Test]
        public void InvalidReferences_AreRejectedWithoutWriting()
        {
            using (var h = new MeshCutHarness())
            {
                var mesh = SyntheticGeometry.Box(2, new float3(1, 1, 1), k_origin).Finish(Smooth);
                var g = h.Place(mesh, "box", 3, 5);
                // a range that runs into the pool's filler slots (indices outside the vertex view)
                var broken = new CutGeometry { Name = "broken", Pool = g.Pool, TopologyVertexCount = g.TopologyVertexCount };
                broken.Ranges.Add(new MeshCutIndexRange { indexStart = g.Ranges[0].indexStart, indexCount = g.Ranges[0].indexCount + 3 });
                broken.Topology.AddRange(g.Topology);
                var run = h.Cut(broken, SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin));
                Assert.That(h.LastCapacity.invalidInput, Is.EqualTo(1), "the capacity query flags the invalid input");
                Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.InvalidInput));
                AssertClean(h, "index outside the vertex view");
                // a crossing triangle whose vertex is not in the topology map
                var unmapped = new CutGeometry { Name = "unmapped", Pool = g.Pool, TopologyVertexCount = g.TopologyVertexCount };
                unmapped.Ranges.AddRange(g.Ranges);
                unmapped.Topology.Add(new TopologyBlock { VertexBase = g.Topology[0].VertexBase, TopologyVertex = new int[g.Topology[0].Count - 1] });
                Array.Copy(g.Topology[0].TopologyVertex, unmapped.Topology[0].TopologyVertex, g.Topology[0].Count - 1);
                run = h.Cut(unmapped, SyntheticGeometry.Plane(new float3(0.3f, 1, 0.2f), k_origin));
                Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.InvalidInput));
                AssertClean(h, "unmapped vertex");
            }
        }
    }
}
