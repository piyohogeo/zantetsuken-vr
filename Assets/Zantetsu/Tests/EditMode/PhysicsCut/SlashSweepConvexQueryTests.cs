using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The Gameplay hit test of one wave update (DESIGN 19.1.7, T-036): the closed hull of the update's four segment
    /// points against one closed convex, with every shape the hull can take going through the one query -- the latch
    /// update's segment, a parallelogram, a growing span's trapezoid, and a line of parallel axes -- and nothing but the
    /// convex deciding: not its box, not a thickness.
    /// </summary>
    public unsafe class SlashSweepConvexQueryTests
    {
        // A box of half size (c, 1, c) about the origin, c = sqrt(2)/2 as the generator rounds it; and the wave's plane
        // y = 0.5, crossing it.
        private static readonly ConvexBrepData s_box = ConvexBrepData.FromPoly(CaseGenerator.Box());
        private static readonly float4 s_plane = new float4(0f, 1f, 0f, -0.5f);

        private static float BoxHalf
        {
            get
            {
                float c = 0f;
                foreach (float3 v in s_box.v) c = math.max(c, v.x);
                return c;
            }
        }

        private static bool Hits(ConvexBrepData convex, float4 plane, float3 previousA, float3 previousB, float3 currentA, float3 currentB)
        {
            fixed (float3* v = convex.v)
            fixed (int* faceOff = convex.faceOff)
            fixed (int* faceIdx = convex.faceIdx)
            fixed (int* faceEdge = convex.faceEdge)
            fixed (BrepEdge* edges = convex.edges)
            {
                var bank = new ConvexBrepBank { vertices = v, faceOffsets = faceOff, faceIndices = faceIdx, faceEdges = faceEdge, edges = edges };
                var range = new ConvexBrepRange
                {
                    vertexCount = convex.V, faceCount = convex.F, faceIndexCount = convex.I, edgeCount = convex.E,
                    maxFaceLoop = convex.Lmax,
                };
                float3[] section = null;
                return SlashSweepConvexQuery.Intersects(plane, previousA, previousB, currentA, currentB, bank, range, ref section);
            }
        }

        private static bool Hits(float4 plane, float3 previousA, float3 previousB, float3 currentA, float3 currentB)
        {
            return Hits(s_box, plane, previousA, previousB, currentA, currentB);
        }

        private static float3 P(float x, float z) => new float3(x, 0.5f, z);

        [Test]
        public void TheLatchUpdatesSegment_IsTheSameQuery_WithAllFourPointsOnTwo()
        {
            // Inside the section, across its edge, and beside it.
            Assert.That(Hits(s_plane, P(0f, -0.3f), P(0f, 0.3f), P(0f, -0.3f), P(0f, 0.3f)), Is.True, "a segment inside");
            Assert.That(Hits(s_plane, P(-2f, 0f), P(2f, 0f), P(-2f, 0f), P(2f, 0f)), Is.True, "a segment across");
            Assert.That(Hits(s_plane, P(-2f, -0.3f), P(-2f, 0.3f), P(-2f, -0.3f), P(-2f, 0.3f)), Is.False, "a segment beside");
        }

        [Test]
        public void AParallelogram_HitsOnlyWhereItSweeps()
        {
            Assert.That(Hits(s_plane, P(-2f, -0.3f), P(-2f, 0.3f), P(-1f, -0.3f), P(-1f, 0.3f)), Is.False, "short of the box");
            Assert.That(Hits(s_plane, P(-1f, -0.3f), P(-1f, 0.3f), P(0f, -0.3f), P(0f, 0.3f)), Is.True, "into it");

            // Wholly past it in one update: the region between the two segments is what hits, not either segment.
            Assert.That(Hits(s_plane, P(-2f, -0.3f), P(-2f, 0.3f), P(2f, -0.3f), P(2f, 0.3f)), Is.True, "over it in one update");
        }

        [Test]
        public void TheRegionASpanGrowsInto_IsHitInThatUpdate()
        {
            // The box moved to z = 2: the previous span's width would pass it by, the grown span does not.
            var moved = ConvexBrepData.FromPoly(Translated(CaseGenerator.Box(), new double3(0.0, 0.0, 2.0)));
            Assert.That(Hits(moved, s_plane, P(-1f, -0.3f), P(-1f, 0.3f), P(0f, -0.3f), P(0f, 0.3f)), Is.False,
                "with the span it had");
            Assert.That(Hits(moved, s_plane, P(-1f, -0.3f), P(-1f, 0.3f), P(0f, -0.3f), P(0f, 2f)), Is.True,
                "with the span grown this update, the trapezoid reaches the box");
        }

        [Test]
        public void AxesParallelOrAntiparallel_AreALine_ThroughTheSameQuery()
        {
            // Travel along the span: all four points on one line.
            Assert.That(Hits(s_plane, P(-3f, 0f), P(-2.6f, 0f), P(-1f, 0f), P(-0.6f, 0f)), Is.True, "a line reaching into it");
            Assert.That(Hits(s_plane, P(-3f, 0f), P(-2.6f, 0f), P(-1.4f, 0f), P(-1f, 0f)), Is.False, "a line short of it");

            // Antiparallel: the same line with the ends the other way round.
            Assert.That(Hits(s_plane, P(-2.6f, 0f), P(-3f, 0f), P(-0.6f, 0f), P(-1f, 0f)), Is.True, "antiparallel, reaching");
            Assert.That(Hits(s_plane, P(-2.6f, 0f), P(-3f, 0f), P(-1f, 0f), P(-1.4f, 0f)), Is.False, "antiparallel, short");
        }

        [Test]
        public void TheBoxOfTheConvex_DoesNotDecide()
        {
            // A tetrahedron: its box is the cube [-1, 1]^3, and at y = 0.9 it is only a thin sliver along x = z.
            var tetra = ConvexBrepData.FromPoly(CaseGenerator.Tetra());
            var plane = new float4(0f, 1f, 0f, -0.9f);
            float3 Q(float x, float z) => new float3(x, 0.9f, z);
            Assert.That(Hits(tetra, plane, Q(0.6f, -0.95f), Q(0.95f, -0.95f), Q(0.6f, -0.6f), Q(0.95f, -0.6f)), Is.False,
                "inside the box, away from the convex");
            Assert.That(Hits(tetra, plane, Q(0.3f, 0.1f), Q(0.1f, 0.3f), Q(0.3f, 0.1f), Q(0.1f, 0.3f)), Is.True,
                "across the sliver");
        }

        [Test]
        public void Touching_Hits_AndThereIsNoThickness()
        {
            float c = BoxHalf;
            Assert.That(Hits(s_plane, P(c, -0.3f), P(c, 0.3f), P(c, -0.3f), P(c, 0.3f)), Is.True, "on the face: closed");
            float past = c + 1e-4f;
            Assert.That(Hits(s_plane, P(past, -0.3f), P(past, 0.3f), P(past, -0.3f), P(past, 0.3f)), Is.False,
                "a tenth of a millimetre off: no thickness");

            // A plane that misses the convex misses, whatever its points lie over.
            var above = new float4(0f, 1f, 0f, -1.5f);
            float3 A(float x, float z) => new float3(x, 1.5f, z);
            Assert.That(Hits(above, A(-0.3f, -0.3f), A(-0.3f, 0.3f), A(0.3f, -0.3f), A(0.3f, 0.3f)), Is.False, "above it");
        }

        private static ConvexPoly Translated(ConvexPoly poly, double3 by)
        {
            var moved = new double3[poly.V.Length];
            for (int i = 0; i < poly.V.Length; i++)
            {
                moved[i] = poly.V[i] + by;
            }

            return new ConvexPoly(moved, poly.F);
        }
    }
}
