using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The bent partition of the additional split (DESIGN 7.9.2; <see cref="CutSpatialSplitBentSearch"/>), over
    /// geometries and convexes made here, in the order the worker searches (a plane that leaves the convexes whole,
    /// then -- with the convex cut on -- a plane that leaves the triangles whole, then the bent partition): an input
    /// a plane separates is left to the plane; a part inside a U, which no plane separates, is separated by a bent
    /// partition with every triangle shared out once; a fold that reaches into an element between its vertices is
    /// refused though every vertex is on one side; the margin is a length, not the function's value; a degenerate
    /// triangle goes with its vertices and a non-finite position classifies nothing; a convex round both parts or a
    /// mesh that bridges them leaves no partition; the limits stop the search; and the anchors' rule by signed
    /// distances.
    /// </summary>
    public unsafe class CutSpatialSplitBentSearchTests
    {
        private const float Margin = 0.01f;

        private static readonly SpatialSplitSearchLimits k_limits = new SpatialSplitSearchLimits(64, 8, 16, 65536);

        private static readonly int[][] k_sideFaces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
        };

        private static readonly int[][] k_endFaces = { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 } };

        /// <summary>A geometry of two submeshes (every part's sides, then every part's ends) and a set of convexes.</summary>
        private sealed class Scene
        {
            internal readonly List<VpRenderVertex> vertices = new List<VpRenderVertex>();
            internal readonly List<uint>[] submeshes = { new List<uint>(), new List<uint>() };
            internal readonly List<float3> convexVertices = new List<float3>();
            internal readonly List<ConvexBrepRange> ranges = new List<ConvexBrepRange>();

            internal uint[] Indices => submeshes[0].Concat(submeshes[1]).ToArray();

            internal int[] SubmeshOffsets => new[] { 0, submeshes[0].Count };

            internal int[] SubmeshCounts => new[] { submeshes[0].Count, submeshes[1].Count };

            internal int Triangles => (submeshes[0].Count + submeshes[1].Count) / 3;

            internal static float3[] Corners(float3 centre, float3 half)
            {
                var corners = new[]
                {
                    new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(-1f, -1f, 1f),
                    new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
                };
                for (int i = 0; i < corners.Length; i++)
                {
                    corners[i] = centre + (corners[i] * half);
                }

                return corners;
            }

            /// <summary>Twelve triangles of one closed part: eight in the first submesh and four in the second. Returns its first vertex.</summary>
            internal uint AddGeometry(float3[] corners)
            {
                uint first = (uint)vertices.Count;
                foreach (float3 corner in corners)
                {
                    vertices.Add(new VpRenderVertex { position = corner });
                }

                AddFaces(submeshes[0], k_sideFaces, first);
                AddFaces(submeshes[1], k_endFaces, first);
                return first;
            }

            private static void AddFaces(List<uint> into, int[][] faces, uint first)
            {
                foreach (int[] c in faces)
                {
                    into.AddRange(new[]
                    {
                        first + (uint)c[0], first + (uint)c[1], first + (uint)c[2],
                        first + (uint)c[0], first + (uint)c[2], first + (uint)c[3],
                    });
                }
            }

            internal void AddConvex(float3[] corners)
            {
                ranges.Add(new ConvexBrepRange { vertexBase = convexVertices.Count, vertexCount = corners.Length });
                convexVertices.AddRange(corners);
            }

            /// <summary>One part: its triangles and its convex, of the same corners. Returns the geometry's first vertex.</summary>
            internal uint AddPart(float3 centre, float3 half)
            {
                float3[] corners = Corners(centre, half);
                AddConvex(corners);
                return AddGeometry(corners);
            }
        }

        /// <summary>What one search did: the record and, with a bent partition found, the convexes' sides and the scratch (for the sharing out).</summary>
        private sealed class Searched
        {
            internal SpatialSplitSearchOutcome outcome;
            internal SpatialSplitSearchResult result;
            internal ConvexSide[] sides;
            internal byte[] scratch;
        }

        /// <summary>
        /// The worker's order (<see cref="CutSpatialSplit"/>'s attempt): the plane that leaves the convexes whole;
        /// where there is none and the bent partition is on, the bent partition; where there is none and the convex
        /// cut is on, the plane that leaves the triangles whole, whose crossing convexes are cut.
        /// </summary>
        private static Searched Search(
            Scene scene, bool cutConvexes, bool bent, SpatialSplitBentLimits? bentLimits = null, float margin = Margin,
            float4x4? geometryToOwner = null, float4x4? shapeToOwner = null, int minCutSideTriangles = CutSpatialSplitSettings.DefaultMinCutSideTriangles)
        {
            VpRenderVertex[] vertices = scene.vertices.ToArray();
            uint[] indices = scene.Indices;
            float3[] convexVertices = scene.convexVertices.ToArray();
            ConvexBrepRange[] ranges = scene.ranges.ToArray();
            var banks = new ConvexBrepBank[ranges.Length];
            int maxVertices = ranges.Max(r => r.vertexCount);
            var searched = new Searched
            {
                scratch = new byte[System.Math.Max(
                    System.Math.Max(CutSpatialSplitSearch.ScratchBytes(ranges.Length), CutSpatialSplitSearch.MeshScratchBytes(ranges.Length, k_limits)),
                    CutSpatialSplitBentSearch.ScratchBytes(ranges.Length, maxVertices, indices.Length / 3)) + 16],
            };
            float4x4 g = geometryToOwner ?? float4x4.identity;
            float4x4 s = shapeToOwner ?? float4x4.identity;
            SpatialSplitBentLimits bl = bentLimits ?? SpatialSplitBentLimits.Default;
            fixed (VpRenderVertex* v = vertices)
            fixed (uint* i = indices)
            fixed (float3* c = convexVertices)
            fixed (ConvexBrepRange* r = ranges)
            fixed (ConvexBrepBank* b = banks)
            fixed (byte* sc = searched.scratch)
            {
                for (int k = 0; k < banks.Length; k++)
                {
                    b[k] = new ConvexBrepBank { vertices = c };
                }

                CutSpatialSplitSearch.FindPlane(v, vertices.Length, i, indices.Length, in g, b, r, ranges.Length, in s, margin, in k_limits, sc, ref searched.result);
                if (searched.result.outcome == SpatialSplitSearchOutcome.NotFound && bent && ranges.Length >= 2)
                {
                    searched.outcome = CutSpatialSplitBentSearch.FindBentSurface(
                        v, vertices.Length, i, indices.Length, in g, b, r, ranges.Length, in s, margin, in k_limits, in bl, sc, ref searched.result);
                    Assert.That(searched.result.outcome, Is.EqualTo(searched.outcome), "the outcome returned is the one recorded");
                    if (searched.outcome == SpatialSplitSearchOutcome.Found)
                    {
                        searched.sides = new ConvexSide[ranges.Length];
                        byte* sides = CutSpatialSplitBentSearch.SidesOf(sc);
                        for (int k = 0; k < ranges.Length; k++) searched.sides[k] = (ConvexSide)sides[k];
                    }
                }

                // After the bent partition, as the worker does: a convex is cut only where nothing leaves the convexes whole.
                if (searched.result.outcome == SpatialSplitSearchOutcome.NotFound && cutConvexes)
                {
                    CutSpatialSplitSearch.FindMeshPlane(
                        v, vertices.Length, i, indices.Length, in g, b, r, ranges.Length, in s, margin, minCutSideTriangles, in k_limits, sc,
                        ref searched.result);
                }

                searched.outcome = searched.result.outcome;
            }

            return searched;
        }

        private sealed class Shared
        {
            internal bool ok;
            internal uint[] output;
            internal int[] offsets, counts;
            internal CutSpatialSplitSearch.Distribution distribution;
        }

        private static Shared DistributeBent(Scene scene, Searched searched, float margin = Margin, float4x4? geometryToOwner = null)
        {
            VpRenderVertex[] vertices = scene.vertices.ToArray();
            uint[] indices = scene.Indices;
            int[] offsets = scene.SubmeshOffsets;
            int[] counts = scene.SubmeshCounts;
            int s = offsets.Length;
            var shared = new Shared { output = new uint[indices.Length], offsets = new int[2 * s], counts = new int[2 * s] };
            var boundsMin = new float3[2 * s];
            var boundsMax = new float3[2 * s];
            float4x4 g = geometryToOwner ?? float4x4.identity;
            int maxVertices = scene.ranges.Max(r => r.vertexCount);
            fixed (VpRenderVertex* v = vertices)
            fixed (uint* i = indices)
            fixed (int* so = offsets)
            fixed (int* sc = counts)
            fixed (uint* o = shared.output)
            fixed (int* oo = shared.offsets)
            fixed (int* oc = shared.counts)
            fixed (float3* bmin = boundsMin)
            fixed (float3* bmax = boundsMax)
            fixed (byte* scratch = searched.scratch)
            {
                shared.ok = CutSpatialSplitBentSearch.DistributeBent(
                    v, vertices.Length, i, indices.Length, so, sc, s, in g, in searched.result.bentSurface, margin,
                    scene.ranges.Count, maxVertices, scratch, o, oo, oc, bmin, bmax, ref shared.distribution);
            }

            return shared;
        }

        private static List<(uint, uint, uint)> Triangles(IReadOnlyList<uint> indices, int start, int count)
        {
            var triangles = new List<(uint, uint, uint)>();
            for (int i = start; i < start + count; i += 3)
            {
                triangles.Add((indices[i], indices[i + 1], indices[i + 2]));
            }

            return triangles;
        }

        /// <summary>A body and a detached part beside it, apart along x: what one plane separates.</summary>
        private static Scene BodyAndHand()
        {
            var scene = new Scene();
            scene.AddPart(new float3(0f, 0f, 0f), new float3(1f, 1f, 1f));
            scene.AddPart(new float3(3f, -0.5f, 0f), new float3(0.25f, 0.25f, 0.25f));
            return scene;
        }

        /// <summary>
        /// A U of three boxes -- a bottom over x 0..3, walls at x 0..1 and 2..3 up to y 3 -- and a part inside it,
        /// x 1.3..1.7, y 1.3..2.7, which no plane separates from the U (it lies inside the hull of the U's corners).
        /// The U's convexes come first (0, 1, 2) and the part's last (3).
        /// </summary>
        private static Scene UWithAPartInside()
        {
            var scene = new Scene();
            scene.AddPart(new float3(1.5f, 0.5f, 0f), new float3(1.5f, 0.5f, 0.5f));
            scene.AddPart(new float3(0.5f, 2f, 0f), new float3(0.5f, 1f, 0.5f));
            scene.AddPart(new float3(2.5f, 2f, 0f), new float3(0.5f, 1f, 0.5f));
            scene.AddPart(new float3(1.5f, 2f, 0f), new float3(0.2f, 0.7f, 0.5f));
            return scene;
        }

        private static SpatialSplitBentSurface Ridge(params float2[] knots)
        {
            var surface = default(SpatialSplitBentSurface);
            surface.height = new float3(0f, 1f, 0f);
            surface.along = new float3(1f, 0f, 0f);
            surface.knotCount = knots.Length;
            for (int k = 0; k < knots.Length; k++)
            {
                surface.knotU[k] = knots[k].x;
                surface.knotH[k] = knots[k].y;
            }

            return surface;
        }

        private static int SideOf(in SpatialSplitBentSurface surface, float margin, out float clearance, params float2[] points)
        {
            var work = new int[(3 * points.Length) + 2];
            fixed (float2* q = points)
            fixed (int* w = work)
            {
                return CutSpatialSplitBentSearch.SideOfPolygon(in surface, q, points.Length, margin, w, out clearance);
            }
        }

        /// <summary>
        /// **An input one plane separates is split by the plane, and the bent partition is not entered**: with the
        /// bent partition on, the result is the plane search's own -- the same plane, the same gap -- and the record
        /// says the search did not go on.
        /// </summary>
        [Test]
        public void APlaneSeparableInput_IsLeftToThePlane_AndTheBentSearchIsNotEntered()
        {
            Searched withBent = Search(BodyAndHand(), cutConvexes: true, bent: true);
            Searched without = Search(BodyAndHand(), cutConvexes: true, bent: false);
            Assert.That(withBent.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(withBent.result.bent, Is.False, "the search did not go on to the bent partition");
            Assert.That(withBent.result.bentFramesTried, Is.Zero);
            Assert.That(withBent.result.planeOwner, Is.EqualTo(without.result.planeOwner), "the plane search's own plane");
            Assert.That(withBent.result.gap, Is.EqualTo(without.result.gap));
            Assert.That(withBent.result.positiveTriangles, Is.EqualTo(without.result.positiveTriangles));
            Assert.That(withBent.result.meshLed, Is.False);
        }

        /// <summary>
        /// **A part inside a U, which no plane separates, is separated by a bent partition**, with the part alone on
        /// one side and the U's three convexes on the other; every triangle goes to the side its part is on, none
        /// lost and none written twice, corner order and submesh kept; the clearance found is the margin or more;
        /// and neither plane search had found anything (with the convex cut on, the mesh-led search ran and found no
        /// plane either).
        /// </summary>
        [Test]
        public void APartInsideAU_NoPlaneSeparates_ABentPartitionDoes_AndEveryTriangleGoesToItsSideOnce()
        {
            Scene scene = UWithAPartInside();
            Searched planes = Search(scene, cutConvexes: true, bent: false);
            Assert.That(planes.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound), "the layout: no plane, convexes whole or triangles whole");
            Assert.That(planes.result.meshLed, Is.True, "the layout: the mesh-led search ran and found none");

            Searched found = Search(scene, cutConvexes: true, bent: true);
            Assert.That(found.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found), "found by the bent partition: " + found.result.bentFramesTried + " frames, "
                + found.result.bentSeedsTried + " seeds, " + found.result.bentCandidates + " candidates, " + found.result.bentIterations + " iterations");
            SpatialSplitSearchResult r = found.result;
            Assert.That(r.bent, Is.True);
            Assert.That(r.bentPieces, Is.GreaterThanOrEqualTo(3).And.LessThanOrEqualTo(SpatialSplitBentLimits.MaxPieces), "a bent surface, not a plane: " + r.bentPieces + " pieces");
            Assert.That(r.bentSurface.knotCount, Is.EqualTo(r.bentPieces + 1));
            Assert.That(r.gap, Is.GreaterThanOrEqualTo(Margin), "the least clearance found is the margin or more");
            Assert.That(r.positiveConvexes + r.negativeConvexes, Is.EqualTo(4));
            Assert.That(math.min(r.positiveConvexes, r.negativeConvexes), Is.EqualTo(1), "the part alone on its side");
            bool partPositive = found.sides[3] == ConvexSide.Positive;
            Assert.That(found.sides.Take(3).All(side => side == (partPositive ? ConvexSide.Negative : ConvexSide.Positive)), Is.True, "the U's three on the other");
            Assert.That(r.positiveTriangles + r.negativeTriangles, Is.EqualTo(scene.Triangles));
            Assert.That(partPositive ? r.positiveTriangles : r.negativeTriangles, Is.EqualTo(12), "the part's twelve triangles with it");
            Assert.That(r.bentCandidates, Is.GreaterThanOrEqualTo(1));
            Assert.That(r.bentElementsVerified, Is.GreaterThan(0));
            Assert.That(r.directionsTried, Is.EqualTo(planes.result.directionsTried), "the plane search's counts stay in the record");
            Assert.That(r.triangleVisits, Is.GreaterThan(0));
            Assert.That(r.meshLed, Is.False, "the mesh-led plane, which cuts convexes, was not looked for: the bent partition came first and held");

            // The record's height axis and the surface are one.
            Assert.That(math.length(r.bentSurface.height), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(math.length(r.bentSurface.along), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(math.abs(math.dot(r.bentSurface.height, r.bentSurface.along)), Is.LessThan(1e-5f));
            Assert.That(r.planeOwner.xyz, Is.EqualTo(r.bentSurface.height));

            // The sharing out.
            Shared shared = DistributeBent(scene, found);
            Assert.That(shared.ok, Is.True);
            uint[] all = scene.Indices;
            Assert.That(shared.distribution.positiveIndexCount, Is.EqualTo(3 * r.positiveTriangles));
            Assert.That(shared.distribution.negativeIndexCount, Is.EqualTo(3 * r.negativeTriangles));
            List<(uint, uint, uint)> positive = Triangles(shared.output, 0, shared.distribution.positiveIndexCount);
            List<(uint, uint, uint)> negative = Triangles(shared.output, shared.distribution.positiveIndexCount, shared.distribution.negativeIndexCount);
            Assert.That(positive.Concat(negative), Is.EquivalentTo(Triangles(all, 0, all.Length)), "none lost, none doubled, corner order kept");
            uint partFirst = 3 * 8;
            List<(uint, uint, uint)> partTriangles = (partPositive ? positive : negative);
            Assert.That(partTriangles.All(t => t.Item1 >= partFirst && t.Item2 >= partFirst && t.Item3 >= partFirst), Is.True, "the part's side holds the part's triangles only");
            Assert.That(shared.counts[0] + shared.counts[2], Is.EqualTo(scene.SubmeshCounts[0]), "each parent submesh's indices, between the two sides");
            Assert.That(shared.counts[1] + shared.counts[3], Is.EqualTo(scene.SubmeshCounts[1]));
            Assert.That(shared.offsets[0], Is.Zero);
            Assert.That(shared.offsets[1], Is.EqualTo(shared.counts[0]), "the positive side's submeshes, in the parent's order");
            Assert.That(shared.offsets[2], Is.Zero);
            Assert.That(shared.offsets[3], Is.EqualTo(shared.counts[2]));
            Assert.That(shared.distribution.positiveReferencedLo <= shared.distribution.positiveReferencedHi, Is.True);
        }

        /// <summary>
        /// A body A (x 0..2) and a part B (x 3..4) apart along x, and a convex C with nothing drawn for it reaching
        /// from over A to over B (x 1.5..3.5, above both): no plane leaves the three convexes whole, a plane at x 2.5
        /// leaves the triangles whole and would cut C, and a bent partition leaves B alone above it with A and C below.
        /// </summary>
        private static Scene TwoPartsUnderAConvexBridge()
        {
            var scene = new Scene();
            scene.AddPart(new float3(1f, 0.5f, 0f), new float3(1f, 0.5f, 0.5f));
            scene.AddPart(new float3(3.5f, 0.5f, 0f), new float3(0.5f, 0.5f, 0.5f));
            scene.AddConvex(Scene.Corners(new float3(2.5f, 2.1f, 0f), new float3(1f, 0.1f, 0.5f)));
            return scene;
        }

        /// <summary>
        /// **Where both a bent partition and a plane that cuts a convex would hold, the bent partition is taken and
        /// nothing is cut.** The bent partition is searched before the mesh-led plane: the result is its own (the
        /// mesh-led plane was not looked for, no convex crosses anything) and every convex is whole on a side. With
        /// the bent partition off the same input goes to the mesh-led plane, which would cut the bridging convex. With
        /// the bent partition on but allowed no frame, its record stays beside the mesh-led plane's result, and the
        /// result is read as the plane's.
        /// </summary>
        [Test]
        public void WhereBothABentPartitionAndAPlaneThatCutsAConvexHold_TheBentPartitionIsTaken_AndNothingIsCut()
        {
            Scene scene = TwoPartsUnderAConvexBridge();
            Searched bent = Search(scene, cutConvexes: true, bent: true, minCutSideTriangles: 1);
            Assert.That(bent.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(bent.result.bent, Is.True);
            Assert.That(bent.result.meshLed, Is.False, "the mesh-led plane was not looked for");
            Assert.That(bent.result.crossingConvexes, Is.Zero, "no convex is to be cut");
            Assert.That(bent.sides, Is.EqualTo(new[] { ConvexSide.Negative, ConvexSide.Positive, ConvexSide.Negative }), "B alone above; A and the bridge below");
            Assert.That(bent.result.positiveTriangles == 12 && bent.result.negativeTriangles == 12, Is.True);
            Assert.That(CutSpatialSplit.IsBentResult(in bent.result), Is.True);

            Searched plane = Search(scene, cutConvexes: true, bent: false, minCutSideTriangles: 1);
            Assert.That(plane.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found), "the layout: without the bent partition, the mesh-led plane holds");
            Assert.That(plane.result.meshLed, Is.True);
            Assert.That(plane.result.crossingConvexes, Is.EqualTo(1), "and it would cut the bridge");
            Assert.That(plane.result.bent, Is.False);

            Searched noFrame = Search(scene, cutConvexes: true, bent: true, bentLimits: new SpatialSplitBentLimits(0, 2, 8, 4, 8), minCutSideTriangles: 1);
            Assert.That(noFrame.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(noFrame.result.bent && noFrame.result.bentFramesTried == 0, Is.True, "the bent partition's record: entered, no frame allowed");
            Assert.That(noFrame.result.meshLed && noFrame.result.crossingConvexes == 1, Is.True, "the mesh-led plane's result");
            Assert.That(CutSpatialSplit.IsBentResult(in noFrame.result), Is.False, "read as the plane's, not the bent partition's");
        }

        /// <summary>
        /// **A fold that reaches into an element between its vertices is refused though every vertex is on one
        /// side.** Over a ridge, a triangle whose three corners are all above it but whose lower edge passes under
        /// the ridge's top is on no side; the same for a box (eight corners) over the ridge, and for an element under
        /// a valley whose upper edge passes over the valley's bottom. An element wholly beyond the fold is taken.
        /// </summary>
        [Test]
        public void AFoldReachingIntoAnElement_IsRefused_ThoughEveryVertexIsOnOneSide()
        {
            SpatialSplitBentSurface ridge = Ridge(new float2(-1f, 0f), new float2(0f, 1f), new float2(1f, 0f));
            Assert.That(ridge.HeightAt(0f), Is.EqualTo(1f));
            Assert.That(ridge.HeightAt(-5f), Is.EqualTo(0f), "flat beyond the ends");
            Assert.That(ridge.Above(new float2(0f, 1.5f)), Is.EqualTo(0.5f));

            // Every corner above the surface under it (0.5 > 0.1), the edge between the lower two passes under the top (0.5 < 1).
            int side = SideOf(in ridge, Margin, out _, new float2(-0.9f, 0.5f), new float2(0.9f, 0.5f), new float2(-0.9f, 2f));
            Assert.That(side, Is.Zero, "the ridge reaches into the triangle between its corners");

            // The same as a box: its lower edge at h = 0.5 across the ridge.
            var box = new[]
            {
                new float2(-0.9f, 0.5f), new float2(0.9f, 0.5f), new float2(0.9f, 2f), new float2(-0.9f, 2f),
                new float2(-0.8f, 0.6f), new float2(0.8f, 0.6f), new float2(0.8f, 1.9f), new float2(-0.8f, 1.9f),
            };
            Assert.That(SideOf(in ridge, Margin, out _, box), Is.Zero, "and into the box");

            // Lifted above the top, with the margin: taken.
            Assert.That(SideOf(in ridge, Margin, out float clearance, new float2(-0.9f, 1.1f), new float2(0.9f, 1.1f), new float2(-0.9f, 2f)), Is.EqualTo(1));
            Assert.That(clearance, Is.EqualTo(0.1f).Within(1e-4f), "the distance from the top of the ridge to the edge above it");

            // A valley, and an element below it whose upper edge passes over the bottom.
            SpatialSplitBentSurface valley = Ridge(new float2(-1f, 1f), new float2(0f, 0f), new float2(1f, 1f));
            Assert.That(SideOf(in valley, Margin, out _, new float2(-0.9f, 0.5f), new float2(0.9f, 0.5f), new float2(-0.9f, -2f)), Is.Zero);
            Assert.That(SideOf(in valley, Margin, out _, new float2(-0.9f, -0.1f), new float2(0.9f, -0.1f), new float2(-0.9f, -2f)), Is.EqualTo(-1));

            // A corner on the wrong side, or on the surface, is no side.
            Assert.That(SideOf(in ridge, Margin, out _, new float2(-0.9f, 2f), new float2(0.9f, 2f), new float2(0f, 0.5f)), Is.Zero);
            Assert.That(SideOf(in ridge, Margin, out _, new float2(-0.9f, 2f), new float2(0.9f, 2f), new float2(0f, 1f)), Is.Zero, "on the surface");
        }

        /// <summary>
        /// **The margin is a length, not the function's value.** Beside a steep piece (a rise of 10 over 0.1), a
        /// point half a unit above the surface's height is a twentieth of that away from the piece itself: refused at
        /// a margin of 0.01, taken at 0.004, and the clearance reported is the distance, not the height difference.
        /// At a knot the distance is to the knot; beyond the ends it is to the flat extension.
        /// </summary>
        [Test]
        public void TheMargin_IsALength_NotTheFunctionsValue()
        {
            SpatialSplitBentSurface steep = Ridge(new float2(0f, 0f), new float2(0.1f, 10f), new float2(0.2f, 10f));
            var q = new float2(0.05f, 5.5f);
            Assert.That(steep.Above(q), Is.EqualTo(0.5f).Within(1e-4f), "the function's value: half a unit");
            float distance = steep.Distance(q);
            Assert.That(distance, Is.EqualTo(0.5f / math.sqrt(1f + 10000f)).Within(1e-5f), "the distance to the piece: a hundredth of it, nearly");
            Assert.That(SideOf(in steep, 0.01f, out float clearance, q, new float2(0.06f, 6.5f), new float2(0.05f, 7f)), Is.Zero, "under a margin of 0.01");
            Assert.That(SideOf(in steep, 0.004f, out clearance, q, new float2(0.06f, 6.5f), new float2(0.05f, 7f)), Is.EqualTo(1), "and beyond one of 0.004");
            Assert.That(clearance, Is.EqualTo(distance).Within(1e-5f));
            Assert.That(steep.SignedDistance(new float2(5f, 9f)), Is.EqualTo(-1f).Within(1e-5f), "below the flat end, far from the rise: the distance to the end, negative");
            // The rise passes h = 9 at u = 0.09; the point is 0.41 beside it, and the distance to the rise's line is that times its cosine.
            Assert.That(steep.SignedDistance(new float2(0.5f, 9f)), Is.EqualTo(-(0.5f - 0.09f) * 10f / math.sqrt(100.01f)).Within(1e-4f),
                "below the flat end but nearer the rise: the distance to the rise (the whole polyline is one surface)");
            Assert.That(steep.SignedDistance(new float2(-1f, 0.25f)), Is.EqualTo(0.25f).Within(1e-5f), "above the flat start");
            Assert.That(steep.Distance(new float2(0.1f, 10.3f)), Is.EqualTo(0.3f).Within(1e-5f), "above a knot: the distance to the knot");
        }

        /// <summary>
        /// **A degenerate triangle is shared out like any other, and a non-finite position classifies nothing.** A
        /// triangle of no area inside the part goes to the part's side with its vertices; a vertex that is not a
        /// number ends the search with NotFinite, not with a side.
        /// </summary>
        [Test]
        public void ADegenerateTriangle_GoesWithItsVertices_AndANonFinitePosition_IsNotClassified()
        {
            Scene scene = UWithAPartInside();
            uint partFirst = 3 * 8;
            scene.submeshes[1].AddRange(new[] { partFirst, partFirst + 1, partFirst + 1 });   // two corners the same: no area
            Searched found = Search(scene, cutConvexes: false, bent: true);
            Assert.That(found.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(found.result.positiveTriangles + found.result.negativeTriangles, Is.EqualTo(scene.Triangles));
            bool partPositive = found.sides[3] == ConvexSide.Positive;
            Assert.That(partPositive ? found.result.positiveTriangles : found.result.negativeTriangles, Is.EqualTo(13), "the part's twelve and the degenerate one");
            Shared shared = DistributeBent(scene, found);
            Assert.That(shared.ok, Is.True);
            Assert.That(shared.distribution.positiveIndexCount + shared.distribution.negativeIndexCount, Is.EqualTo(scene.Indices.Length));

            Scene broken = UWithAPartInside();
            VpRenderVertex v = broken.vertices[(int)partFirst];
            v.position = new float3(float.NaN, 2f, 0f);
            broken.vertices[(int)partFirst] = v;
            Searched notFinite = Search(broken, cutConvexes: false, bent: true);
            Assert.That(notFinite.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFinite));
            Assert.That(notFinite.result.bent, Is.True, "it was the bent search that met it: the plane search ends with NotFinite too, but it is not entered then");
        }

        /// <summary>
        /// **A convex round both parts, or a mesh that bridges them, leaves no partition.** A convex from inside the
        /// part to inside a wall would have to be cut, which the bent partition does not do; a bar of triangles with
        /// no convex of its own from the part to the wall is a mesh in one piece. Both end NotFound, after a finite
        /// search, and the sides of nothing are written.
        /// </summary>
        [Test]
        public void AConvexRoundBothParts_OrAMeshThatBridgesThem_HasNoBentPartition()
        {
            Scene convexAcross = UWithAPartInside();
            convexAcross.AddConvex(Scene.Corners(new float3(2f, 2f, 0f), new float3(0.6f, 0.1f, 0.1f)));   // x 1.4..2.6: into the part and into the right wall
            Searched across = Search(convexAcross, cutConvexes: false, bent: true);
            Assert.That(across.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(across.result.bent, Is.True);
            Assert.That(across.sides, Is.Null);
            Assert.That(across.result.bentFramesTried, Is.EqualTo(SpatialSplitBentLimits.DefaultFrames), "every frame was tried");

            Scene bridged = UWithAPartInside();
            bridged.AddGeometry(Scene.Corners(new float3(2f, 2f, 0f), new float3(0.6f, 0.1f, 0.1f)));   // drawn, no convex
            Searched bridge = Search(bridged, cutConvexes: false, bent: true);
            Assert.That(bridge.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(bridge.result.bentCandidates, Is.GreaterThanOrEqualTo(1), "the convexes alone close, so the triangles were read, and the bar joins the two");
        }

        /// <summary>
        /// **The limits stop the search.** One frame alone does not hold the partition of the U (the first frame's
        /// height axis is x), and the search ends NotFound having tried that one; with no candidate allowed to read
        /// the triangles it ends NotFound as soon as a seed's convexes close; with a surface of one piece allowed it
        /// has only planes to try and finds none. The provisional limits find it.
        /// </summary>
        [Test]
        public void TheLimits_StopTheSearch()
        {
            Scene scene = UWithAPartInside();
            var oneFrame = new SpatialSplitBentLimits(1, 2, 8, 4, 8);
            Searched one = Search(scene, cutConvexes: false, bent: true, bentLimits: oneFrame);
            Assert.That(one.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(one.result.bentFramesTried, Is.EqualTo(1));

            var onePiece = new SpatialSplitBentLimits(12, 2, 8, 4, 1);
            Searched flat = Search(scene, cutConvexes: false, bent: true, bentLimits: onePiece);
            Assert.That(flat.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound), "one piece is a plane, and no plane separates the U");

            var noCandidates = new SpatialSplitBentLimits(12, 2, 1, 4, 8);
            Searched few = Search(scene, cutConvexes: false, bent: true, bentLimits: noCandidates);
            Assert.That(few.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found).Or.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(few.result.bentCandidates, Is.LessThanOrEqualTo(1), "one closure with the triangles at most");

            Searched all = Search(scene, cutConvexes: false, bent: true);
            Assert.That(all.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found), "the provisional limits hold it");
            Assert.That(all.result.bentFramesTried, Is.LessThanOrEqualTo(SpatialSplitBentLimits.DefaultFrames));
            Assert.That(all.result.bentCandidates, Is.LessThanOrEqualTo(SpatialSplitBentLimits.DefaultCandidates));
            Assert.That(new SpatialSplitBentLimits(13, 2, 8, 4, 8).IsValid, Is.False);
            Assert.That(new SpatialSplitBentLimits(12, 2, 8, 4, SpatialSplitBentLimits.MaxPieces + 1).IsValid, Is.False);
            Assert.That(SpatialSplitBentLimits.Default.IsValid, Is.True);
        }

        /// <summary>
        /// **An owner of one convex has nothing for the other side**, and a frame in which nothing has an extent along
        /// u (a flat input) is passed over: both NotFound, and a scratch too small is not written past (the sizes are
        /// the search's own to say).
        /// </summary>
        [Test]
        public void OneConvex_HasNoOtherSide()
        {
            var scene = new Scene();
            scene.AddPart(new float3(0f, 0f, 0f), new float3(1f, 1f, 1f));
            scene.AddGeometry(Scene.Corners(new float3(3f, 0f, 0f), new float3(0.25f)));
            Searched one = Search(scene, cutConvexes: false, bent: true);
            Assert.That(one.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(one.result.bent, Is.False, "not entered: the attempt's own condition, two convexes or more, is the test helper's too");
            Assert.That(CutSpatialSplitBentSearch.ScratchBytes(2, 8, 24), Is.GreaterThan(0));
            Assert.That(CutSpatialSplitBentSearch.ScratchBytes(64, 128, 131072), Is.LessThan(64 * 1024), "a small scratch: tens of kilobytes at the limits");
        }

        /// <summary>
        /// **The anchors by their signed distances**: beyond the epsilon on a side, that side; within it, each side
        /// once; distances not as many as the anchors, or not finite, refuse the whole distribution.
        /// </summary>
        [Test]
        public void Anchors_AreDistributedByTheirSignedDistances_AsByAPlane()
        {
            var anchors = new List<float3> { new float3(0f, 1f, 0f), new float3(0f, -1f, 0f), new float3(0f, 0.001f, 0f) };
            var positive = new List<float3>();
            var negative = new List<float3>();
            Assert.That(FixedSupportAnchors.TryDistribute(anchors, new List<float> { 0.5f, -0.5f, 0.001f }, 0.01f, positive, negative, out AnchorDistributionResult result), Is.True);
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.Ok));
            Assert.That(positive, Is.EqualTo(new[] { anchors[0], anchors[2] }));
            Assert.That(negative, Is.EqualTo(new[] { anchors[1], anchors[2] }));
            Assert.That(result.positiveCount == 2 && result.negativeCount == 2, Is.True);

            Assert.That(FixedSupportAnchors.TryDistribute(anchors, new List<float> { 0.5f, -0.5f }, 0.01f, new List<float3>(), new List<float3>(), out result), Is.False);
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.MismatchedDistances));
            Assert.That(FixedSupportAnchors.TryDistribute(anchors, new List<float> { 0.5f, float.NaN, 0.001f }, 0.01f, new List<float3>(), new List<float3>(), out result), Is.False);
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.NonFiniteClassification));
            Assert.That(FixedSupportAnchors.TryDistribute(null, null, 0.01f, new List<float3>(), new List<float3>(), out result), Is.True, "no anchor: nothing to distribute");
            Assert.That(result.positiveCount == 0 && result.negativeCount == 0, Is.True);
        }
    }
}
