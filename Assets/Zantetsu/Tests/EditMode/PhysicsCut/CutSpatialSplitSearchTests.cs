using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The numerical part of the additional split (DESIGN 7.9.2, first version: <see cref="CutSpatialSplitSearch"/>),
    /// over geometries and convexes made here: the plane it finds, the three frames it gives it in, what leaves it
    /// without one -- a mesh that bridges the gap, a convex that does, a gap narrower than the margin, the limits, a
    /// position that is not finite -- and the sharing out of the triangles, none lost and none doubled.
    /// </summary>
    public unsafe class CutSpatialSplitSearchTests
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

            /// <summary>The eight corners of a box, or of the same box turned 45 degrees about z (a diamond in x, y).</summary>
            internal static float3[] Corners(float3 centre, float3 half, bool diamond = false)
            {
                var corners = new[]
                {
                    new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(-1f, -1f, 1f),
                    new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
                };
                for (int i = 0; i < corners.Length; i++)
                {
                    float3 c = corners[i] * half;
                    if (diamond)
                    {
                        const float s = 0.70710678f;
                        c = new float3((c.x - c.y) * s, (c.x + c.y) * s, c.z);
                    }

                    corners[i] = centre + c;
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

            /// <summary>One part: its triangles and its convex, of the same corners.</summary>
            internal uint AddPart(float3 centre, float3 half, bool diamond = false)
            {
                float3[] corners = Corners(centre, half, diamond);
                AddConvex(corners);
                return AddGeometry(corners);
            }
        }

        private static SpatialSplitSearchOutcome Find(
            Scene scene, out SpatialSplitSearchResult result, float margin = Margin, SpatialSplitSearchLimits? limits = null,
            float4x4? geometryToOwner = null, float4x4? shapeToOwner = null)
        {
            VpRenderVertex[] vertices = scene.vertices.ToArray();
            uint[] indices = scene.Indices;
            float3[] convexVertices = scene.convexVertices.ToArray();
            ConvexBrepRange[] ranges = scene.ranges.ToArray();
            var banks = new ConvexBrepBank[ranges.Length];
            var scratch = new byte[CutSpatialSplitSearch.ScratchBytes(ranges.Length) + 16];
            float4x4 g = geometryToOwner ?? float4x4.identity;
            float4x4 s = shapeToOwner ?? float4x4.identity;
            SpatialSplitSearchLimits l = limits ?? k_limits;
            result = default;
            fixed (VpRenderVertex* v = vertices)
            fixed (uint* i = indices)
            fixed (float3* c = convexVertices)
            fixed (ConvexBrepRange* r = ranges)
            fixed (ConvexBrepBank* b = banks)
            fixed (byte* sc = scratch)
            {
                for (int k = 0; k < banks.Length; k++)
                {
                    b[k] = new ConvexBrepBank { vertices = c };
                }

                SpatialSplitSearchOutcome outcome = CutSpatialSplitSearch.FindPlane(
                    v, vertices.Length, i, indices.Length, in g, b, r, ranges.Length, in s, margin, in l, sc, ref result);
                Assert.That(result.outcome, Is.EqualTo(outcome), "the outcome returned is the one recorded");
                return outcome;
            }
        }

        private sealed class Shared
        {
            internal bool ok;
            internal uint[] output;
            internal int[] offsets, counts;
            internal float3[] boundsMin, boundsMax;
            internal CutSpatialSplitSearch.Distribution distribution;
        }

        private static Shared Distribute(Scene scene, float4 planeGeometry, float margin = Margin, int[] submeshOffsets = null, int[] submeshCounts = null)
        {
            VpRenderVertex[] vertices = scene.vertices.ToArray();
            uint[] indices = scene.Indices;
            int[] offsets = submeshOffsets ?? scene.SubmeshOffsets;
            int[] counts = submeshCounts ?? scene.SubmeshCounts;
            int s = offsets.Length;
            var shared = new Shared
            {
                output = new uint[indices.Length], offsets = new int[2 * s], counts = new int[2 * s],
                boundsMin = new float3[2 * s], boundsMax = new float3[2 * s],
            };
            fixed (VpRenderVertex* v = vertices)
            fixed (uint* i = indices)
            fixed (int* so = offsets)
            fixed (int* sc = counts)
            fixed (uint* o = shared.output)
            fixed (int* oo = shared.offsets)
            fixed (int* oc = shared.counts)
            fixed (float3* bmin = shared.boundsMin)
            fixed (float3* bmax = shared.boundsMax)
            {
                shared.ok = CutSpatialSplitSearch.Distribute(
                    v, vertices.Length, i, indices.Length, so, sc, s, planeGeometry, margin, o, oo, oc, bmin, bmax, ref shared.distribution);
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

        /// <summary>A body and a detached part beside it, apart along x.</summary>
        private static Scene BodyAndHand(float handLowX = 2.75f)
        {
            var scene = new Scene();
            scene.AddPart(new float3(0f, 0f, 0f), new float3(1f, 1f, 1f));
            scene.AddPart(new float3(handLowX + 0.25f, -0.5f, 0f), new float3(0.25f, 0.25f, 0.25f));
            return scene;
        }

        /// <summary>
        /// **A body and a detached part are separated by a plane in the gap between them**, and every triangle goes
        /// to the side its part is on: none lost, none written twice, corner order and submesh kept, each side's
        /// submeshes in the parent's order with the box of what they hold and the vertices they refer to.
        /// </summary>
        [Test]
        public void ABodyAndADetachedPart_AreSeparated_AndEveryTriangleGoesToItsSideOnce()
        {
            Scene scene = BodyAndHand();
            Assert.That(Find(scene, out SpatialSplitSearchResult found), Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(found.planeOwner.xyz, Is.EqualTo(new float3(1f, 0f, 0f)), "the first axis that has a gap");
            Assert.That(found.planeOwner.w, Is.EqualTo(-1.875f).Within(1e-5f), "in the middle of the gap between x = 1 and x = 2.75");
            Assert.That(found.gap, Is.EqualTo(1.75f).Within(1e-5f));
            Assert.That(found.positiveConvexes == 1 && found.negativeConvexes == 1, Is.True);
            Assert.That(found.positiveTriangles == 12 && found.negativeTriangles == 12, Is.True);
            Assert.That(found.planesTested, Is.EqualTo(1));
            Assert.That(found.directionsTried, Is.EqualTo(1));

            uint[] input = scene.Indices;
            Shared shared = Distribute(scene, found.planeGeometry);
            Assert.That(shared.ok, Is.True);
            Assert.That(shared.distribution.positiveIndexCount, Is.EqualTo(36));
            Assert.That(shared.distribution.negativeIndexCount, Is.EqualTo(36));

            // Each side: the parent's two submeshes in order, offsets from the side's own start.
            Assert.That(shared.offsets, Is.EqualTo(new[] { 0, 24, 0, 24 }));
            Assert.That(shared.counts, Is.EqualTo(new[] { 24, 12, 24, 12 }));

            // The positive side first: the hand's triangles (vertices 8..15), in the parent's order within each submesh.
            List<(uint, uint, uint)> positiveSides = Triangles(shared.output, 0, 24);
            List<(uint, uint, uint)> positiveEnds = Triangles(shared.output, 24, 12);
            List<(uint, uint, uint)> negativeSides = Triangles(shared.output, 36, 24);
            List<(uint, uint, uint)> negativeEnds = Triangles(shared.output, 60, 12);
            Assert.That(positiveSides, Is.EqualTo(Triangles(input, 24, 24)), "the hand's sides, corner order kept");
            Assert.That(positiveEnds, Is.EqualTo(Triangles(input, 48 + 12, 12)), "the hand's ends");
            Assert.That(negativeSides, Is.EqualTo(Triangles(input, 0, 24)), "the body's sides");
            Assert.That(negativeEnds, Is.EqualTo(Triangles(input, 48, 12)), "the body's ends");

            // None lost and none doubled.
            Assert.That(Triangles(shared.output, 0, 72), Is.EquivalentTo(Triangles(input, 0, 72)));

            Assert.That(shared.distribution.positiveReferencedLo, Is.EqualTo(8u));
            Assert.That(shared.distribution.positiveReferencedHi, Is.EqualTo(15u));
            Assert.That(shared.distribution.negativeReferencedLo, Is.EqualTo(0u));
            Assert.That(shared.distribution.negativeReferencedHi, Is.EqualTo(7u));
            Assert.That(shared.boundsMin[0], Is.EqualTo(new float3(2.75f, -0.75f, -0.25f)), "the hand's box");
            Assert.That(shared.boundsMax[0], Is.EqualTo(new float3(3.25f, -0.25f, 0.25f)));
            Assert.That(shared.boundsMin[2], Is.EqualTo(new float3(-1f, -1f, -1f)), "the body's box");
            Assert.That(shared.boundsMax[3], Is.EqualTo(new float3(1f, 1f, 1f)));
        }

        /// <summary>
        /// **The plane is one plane in three frames.** With the geometry and the shape each placed in the owner by a
        /// rigid transform of its own, every vertex has the same signed distance by the plane of its own frame as its
        /// image in the owner has by the owner's.
        /// </summary>
        [Test]
        public void ThePlane_IsGivenInTheOwnersFrame_TheGeometrys_AndTheShapes()
        {
            Scene scene = BodyAndHand();
            float4x4 geometryToOwner = float4x4.TRS(new float3(0.4f, -2f, 1.5f), quaternion.Euler(0.3f, -0.7f, 0.2f), new float3(1f));
            float4x4 shapeToOwner = geometryToOwner;   // the same body: its triangles and its convexes sit together
            Assert.That(Find(scene, out SpatialSplitSearchResult found, geometryToOwner: geometryToOwner, shapeToOwner: shapeToOwner),
                Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(math.length(found.planeOwner.xyz), Is.EqualTo(1f).Within(1e-5f), "a unit normal in the owner's frame");

            foreach (VpRenderVertex vertex in scene.vertices)
            {
                float3 p = vertex.position;
                float inOwner = math.dot(found.planeOwner.xyz, math.transform(geometryToOwner, p)) + found.planeOwner.w;
                float inGeometry = math.dot(found.planeGeometry.xyz, p) + found.planeGeometry.w;
                Assert.That(inGeometry, Is.EqualTo(inOwner).Within(1e-4f));
                Assert.That(math.abs(inGeometry), Is.GreaterThan(found.margin), "and every vertex keeps the margin");
            }

            foreach (float3 p in scene.convexVertices)
            {
                float inOwner = math.dot(found.planeOwner.xyz, math.transform(shapeToOwner, p)) + found.planeOwner.w;
                float inShape = math.dot(found.planeShape.xyz, p) + found.planeShape.w;
                Assert.That(inShape, Is.EqualTo(inOwner).Within(1e-4f));
            }

            Assert.That(Distribute(scene, found.planeGeometry).ok, Is.True, "and the triangles are shared out by the geometry's own");
        }

        /// <summary>
        /// **Parts that only a direction between their centres separates** are found by one: two diamonds whose
        /// projections overlap on every axis. Without centre directions the same input has no plane.
        /// </summary>
        [Test]
        public void PartsNoAxisSeparates_AreSeparatedAlongTheLineOfTheirCentres()
        {
            var scene = new Scene();
            scene.AddPart(new float3(0f, 0f, 0f), new float3(0.70710678f, 0.70710678f, 1f), diamond: true);
            scene.AddPart(new float3(1.2f, 1.2f, 0f), new float3(0.70710678f, 0.70710678f, 1f), diamond: true);
            Assert.That(Find(scene, out SpatialSplitSearchResult found), Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(found.directionsTried, Is.EqualTo(4), "the three axes had no gap; the fourth direction is the centres'");
            Assert.That(math.abs(found.planeOwner.x), Is.EqualTo(0.70710678f).Within(1e-4f));
            Assert.That(math.abs(found.planeOwner.y), Is.EqualTo(0.70710678f).Within(1e-4f));
            Assert.That(found.planeOwner.z, Is.EqualTo(0f).Within(1e-5f));

            var axesOnly = new SpatialSplitSearchLimits(64, 0, 16, 65536);
            Assert.That(Find(scene, out SpatialSplitSearchResult none, limits: axesOnly), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(none.directionsTried, Is.EqualTo(3));
            Assert.That(none.planesTested, Is.Zero);
        }

        /// <summary>
        /// **A mesh that bridges the gap leaves the object whole**: the convexes are apart, but triangles reach from
        /// one part to the other, so no plane leaves every triangle on one side.
        /// </summary>
        [Test]
        public void AMeshThatBridgesTheGap_HasNoPlane()
        {
            Scene scene = BodyAndHand();
            scene.AddGeometry(Scene.Corners(new float3(1.875f, -0.5f, 0f), new float3(0.875f, 0.1f, 0.1f)));   // from x = 1 to x = 2.75
            Assert.That(Find(scene, out SpatialSplitSearchResult found), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(found.planesTested, Is.GreaterThan(0), "the convexes' gap was tested against the triangles");
        }

        /// <summary>
        /// **A convex that crosses the gap leaves the object whole**, though the mesh is in two parts; and so does a
        /// mesh that lies all on one side of the convexes' gap.
        /// </summary>
        [Test]
        public void AConvexThatCrossesTheGap_OrAMeshAllOnOneSide_HasNoPlane()
        {
            Scene scene = BodyAndHand();
            scene.AddConvex(Scene.Corners(new float3(1.75f, -0.5f, 0f), new float3(1.25f, 0.1f, 0.1f)));   // from x = 0.5 to x = 3
            Assert.That(Find(scene, out SpatialSplitSearchResult crossed), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(crossed.planesTested, Is.Zero, "the convexes leave no gap at all");

            var oneSided = new Scene();
            oneSided.AddPart(new float3(0f, 0f, 0f), new float3(1f, 1f, 1f));
            oneSided.AddConvex(Scene.Corners(new float3(3f, -0.5f, 0f), new float3(0.25f, 0.25f, 0.25f)));   // a convex with nothing drawn
            Assert.That(Find(oneSided, out SpatialSplitSearchResult empty), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(empty.planesTested, Is.GreaterThan(0), "a gap, but a side without a triangle");
        }

        /// <summary>
        /// **A gap no wider than twice the margin is not a gap**, and one a little wider is, with every vertex beyond
        /// the margin.
        /// </summary>
        [Test]
        public void AGapWithinTheMargin_HasNoPlane_AndOneBeyondItHas()
        {
            Assert.That(Find(BodyAndHand(1f + (1.5f * Margin)), out SpatialSplitSearchResult near), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(near.planesTested, Is.Zero);

            Scene wide = BodyAndHand(1f + (3f * Margin));
            Assert.That(Find(wide, out SpatialSplitSearchResult found), Is.EqualTo(SpatialSplitSearchOutcome.Found));
            foreach (VpRenderVertex vertex in wide.vertices)
            {
                float3 p = vertex.position;
                Assert.That(math.abs(math.dot(found.planeGeometry.xyz, p) + found.planeGeometry.w), Is.GreaterThan(Margin));
            }

            // Touching parts: nothing between them.
            Assert.That(Find(BodyAndHand(1f), out _), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
        }

        /// <summary>**Triangles of no area go with their vertices**, counted and written like any other.</summary>
        [Test]
        public void TrianglesWithoutArea_AreSharedOutLikeAnyOther()
        {
            Scene scene = BodyAndHand();
            // Three more triangles on the hand (vertices 8..15), none with any area: a repeated corner, three collinear.
            scene.submeshes[0].AddRange(new uint[] { 8, 8, 9, 9, 9, 9 });
            scene.submeshes[1].AddRange(new uint[] { 12, 13, 12 });
            uint[] input = scene.Indices;
            Assert.That(Find(scene, out SpatialSplitSearchResult found), Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(found.positiveTriangles, Is.EqualTo(15));
            Assert.That(found.negativeTriangles, Is.EqualTo(12));
            Shared shared = Distribute(scene, found.planeGeometry);
            Assert.That(shared.ok, Is.True);
            Assert.That(shared.distribution.positiveIndexCount, Is.EqualTo(45));
            Assert.That(shared.counts, Is.EqualTo(new[] { 30, 15, 24, 12 }));
            Assert.That(Triangles(shared.output, 0, input.Length), Is.EquivalentTo(Triangles(input, 0, input.Length)), "none lost, none doubled");
            Assert.That(Triangles(shared.output, 0, 30).Skip(8), Is.EqualTo(new[] { (8u, 8u, 9u), (9u, 9u, 9u) }), "after the hand's own sides, in order");
        }

        /// <summary>**Over the limits nothing is searched, and the count of tested gaps is kept.**</summary>
        [Test]
        public void TheLimits_StopTheSearch()
        {
            Scene three = BodyAndHand();
            three.AddPart(new float3(6f, 0f, 0f), new float3(0.5f, 0.5f, 0.5f));
            Assert.That(Find(three, out _, limits: new SpatialSplitSearchLimits(2, 8, 16, 65536)), Is.EqualTo(SpatialSplitSearchOutcome.OverTheLimits), "convexes");
            Assert.That(Find(three, out _, limits: new SpatialSplitSearchLimits(64, 8, 16, 35)), Is.EqualTo(SpatialSplitSearchOutcome.OverTheLimits), "triangles");
            Assert.That(Find(three, out SpatialSplitSearchResult found), Is.EqualTo(SpatialSplitSearchOutcome.Found), "within them: the first gap");
            Assert.That(found.positiveConvexes == 2 && found.negativeConvexes == 1, Is.True, "one plane, two sides: the far two stay together");

            // The first gap bridged by the mesh, the second open: one test allowed finds nothing, two find the second.
            three.AddGeometry(Scene.Corners(new float3(1.875f, -0.5f, 0f), new float3(0.875f, 0.1f, 0.1f)));
            Assert.That(Find(three, out SpatialSplitSearchResult one, limits: new SpatialSplitSearchLimits(64, 8, 1, 65536)), Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(one.planesTested, Is.EqualTo(1));
            Assert.That(Find(three, out SpatialSplitSearchResult two, limits: new SpatialSplitSearchLimits(64, 8, 2, 65536)), Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(two.planesTested, Is.EqualTo(2));
            Assert.That(two.positiveConvexes == 1 && two.negativeConvexes == 2, Is.True);

            Assert.That(Find(three, out _, limits: new SpatialSplitSearchLimits(1, 8, 16, 65536)), Is.EqualTo(SpatialSplitSearchOutcome.InvalidInput), "limits that allow nothing");
        }

        /// <summary>**A position that is not finite is not classified**; a single convex or no margin is not searched.</summary>
        [Test]
        public void ANonFinitePosition_IsNotClassified_AndAnInputThatCannotBeSplitIsNotSearched()
        {
            Scene brokenMesh = BodyAndHand();
            VpRenderVertex broken = brokenMesh.vertices[9];
            broken.position = new UnityEngine.Vector3(float.NaN, 0f, 0f);
            brokenMesh.vertices[9] = broken;
            Assert.That(Find(brokenMesh, out _), Is.EqualTo(SpatialSplitSearchOutcome.NotFinite));

            Scene brokenConvex = BodyAndHand();
            brokenConvex.convexVertices[3] = new float3(0f, float.PositiveInfinity, 0f);
            Assert.That(Find(brokenConvex, out _), Is.EqualTo(SpatialSplitSearchOutcome.NotFinite));

            var single = new Scene();
            single.AddPart(new float3(0f, 0f, 0f), new float3(1f, 1f, 1f));
            Assert.That(Find(single, out _), Is.EqualTo(SpatialSplitSearchOutcome.NotFound), "one convex is on one side of anything");
            Assert.That(Find(BodyAndHand(), out _, margin: 0f), Is.EqualTo(SpatialSplitSearchOutcome.InvalidInput), "no margin to keep");
        }

        /// <summary>
        /// **The sharing out refuses what the plane does not leave whole**: a triangle the plane crosses or comes
        /// within the margin of, a side left empty, and submeshes that do not cover the indices once and in order.
        /// </summary>
        [Test]
        public void TheSharingOut_RefusesACrossedTriangle_AnEmptySide_AndSubmeshesThatDoNotCover()
        {
            Scene scene = BodyAndHand();
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, 0f)).ok, Is.False, "x = 0 crosses the body");
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, -(1f + (0.5f * Margin)))).ok, Is.False, "within the margin of the body's face at x = 1");
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, -5f)).ok, Is.False, "everything on one side");
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, -1.875f)).ok, Is.True, "the layout: the plane in the gap is taken");
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, -1.875f), submeshOffsets: new[] { 0, 48 }, submeshCounts: new[] { 46, 26 }).ok,
                Is.False, "not whole triangles");
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, -1.875f), submeshOffsets: new[] { 0, 36 }, submeshCounts: new[] { 48, 24 }).ok,
                Is.False, "submeshes that overlap");
            Assert.That(Distribute(scene, new float4(1f, 0f, 0f, -1.875f), submeshOffsets: new[] { 0, 48 }, submeshCounts: new[] { 48, 12 }).ok,
                Is.False, "submeshes that leave indices out");
        }
    }
}
