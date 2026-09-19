using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The Cap Bounds Polygon clipped by the other selected boundaries (DESIGN 5.2). The polygons are known shapes -- a
    /// square in the plane y = 0, the octagon its corners are cut to, and the regular hexagon
    /// <see cref="VpCapBoundsPolygon"/> makes of a cube and the plane x + y + z = 0 -- and every plane is given by the
    /// test in the polygon's own frame, so each expectation is read off the shape. The selection itself is the
    /// selection tests' subject; here it is only consumed, once from a real ledger chain.
    /// </summary>
    public class VpCapPolygonClipTests
    {
        private const float Epsilon = 1e-5f;

        private static readonly LogicalCutLedger k_scope = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));

        /// <summary>The square [-1, 1]² in y = 0, wound so its area vector points along -y.</summary>
        private static Vector3[] Square()
        {
            return new[] { new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(1f, 0f, 1f), new Vector3(-1f, 0f, 1f) };
        }

        private static VpClipBoundary Face(int number, float side = 1f)
        {
            return new VpClipBoundary(new VpCapFace(k_scope, new CutOperationId(number)), side);
        }

        private static readonly VpClipBoundary k_own = Face(100);

        /// <summary>A written-out list of candidates with their planes in the polygon's frame and their selection.</summary>
        private sealed class Planes
        {
            public readonly List<VpClipCandidate> candidates = new List<VpClipCandidate>();
            public readonly List<VpClipSelectionState> states = new List<VpClipSelectionState>();
            public readonly List<float4> planes = new List<float4>();

            public Planes Add(VpClipBoundary boundary, float4 plane, VpClipSelectionState state = VpClipSelectionState.Selected)
            {
                candidates.Add(new VpClipCandidate(boundary, float4.zero, false, default));
                states.Add(state);
                planes.Add(plane);
                return this;
            }
        }

        private static Vector3[] Clip(Vector3[] polygon, Planes p, VpClipBoundary own, out bool ok)
        {
            var output = new Vector3[VpCapPolygonClip.MaxVertices + 4];
            ok = VpCapPolygonClip.TryClip(polygon, polygon.Length, own, p.candidates, p.states, p.planes, Epsilon, output, out int count);
            var result = new Vector3[count];
            Array.Copy(output, result, count);
            return result;
        }

        private static Vector3[] Clip(Vector3[] polygon, Planes p)
        {
            Vector3[] result = Clip(polygon, p, k_own, out bool ok);
            Assert.That(ok, Is.True, "clipped");
            return result;
        }

        private static Vector3 AreaVector(IReadOnlyList<Vector3> ring)
        {
            Vector3 twice = Vector3.zero;
            for (int i = 1; i + 1 < ring.Count; i++)
            {
                twice += Vector3.Cross(ring[i] - ring[0], ring[i + 1] - ring[0]);
            }

            return twice * 0.5f;
        }

        private static void AssertInside(Vector3[] result, Planes p, string what)
        {
            for (int v = 0; v < result.Length; v++)
            {
                for (int i = 0; i < p.candidates.Count; i++)
                {
                    if (p.states[i] != VpClipSelectionState.Selected || p.candidates[i].boundary.face == k_own.face)
                    {
                        continue;
                    }

                    float4 plane = p.planes[i];
                    float d = p.candidates[i].boundary.side * (math.dot(plane.xyz, (float3)result[v]) + plane.w);
                    Assert.That(d, Is.GreaterThanOrEqualTo(-1e-4f), what + ": vertex " + v + " inside plane " + i);
                }
            }
        }

        private static bool Contains(Vector3[] ring, Vector3 point)
        {
            foreach (Vector3 v in ring)
            {
                if ((v - point).magnitude < 1e-4f)
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------------------------------------------------

        /// <summary>With no other plane selected -- only the cap's own -- the polygon comes back as it went in, in order.</summary>
        [Test]
        public void NoOtherSelectedPlane_KeepsThePolygon()
        {
            Vector3[] square = Square();
            Assert.That(Clip(square, new Planes()), Is.EqualTo(square), "no plane at all");
            Assert.That(Clip(square, new Planes().Add(k_own, new float4(1f, 0f, 0f, 0f))), Is.EqualTo(square), "only its own face");
        }

        /// <summary>The plane x = 0 kept on its positive side leaves x from 0 to 1, on its negative side x from -1 to 0.</summary>
        [Test]
        public void EachSide_KeepsItsOwnHalf_WithTheWindingKept()
        {
            Vector3[] square = Square();
            Vector3 winding = AreaVector(square);
            foreach (float side in new[] { 1f, -1f })
            {
                Vector3[] half = Clip(square, new Planes().Add(Face(1, side), new float4(1f, 0f, 0f, 0f)));
                Assert.That(half.Length, Is.EqualTo(4), "side " + side);
                foreach (float z in new[] { -1f, 1f })
                {
                    Assert.That(Contains(half, new Vector3(0f, 0f, z)), Is.True, "side " + side + ": the cut edge at z " + z);
                    Assert.That(Contains(half, new Vector3(side, 0f, z)), Is.True, "side " + side + ": the kept corner at z " + z);
                }

                Vector3 area = AreaVector(half);
                Assert.That(area.magnitude, Is.EqualTo(2f).Within(1e-4f), "side " + side + ": half the square");
                Assert.That(Vector3.Dot(area, winding), Is.GreaterThan(0f), "side " + side + ": wound the same way");
            }
        }

        /// <summary>Three planes at once: every vertex left is in the square's plane and inside every one of them.</summary>
        [Test]
        public void SeveralPlanes_LeaveOnlyWhatIsInsideAllOfThem()
        {
            Planes p = new Planes()
                .Add(Face(1, 1f), new float4(1f, 0f, 0f, 0.5f))      // x >= -0.5
                .Add(Face(2, -1f), new float4(0f, 0f, 1f, -0.5f))    // z <= 0.5
                .Add(Face(3, -1f), new float4(1f, 0f, 1f, -0.8f));   // x + z <= 0.8
            Vector3[] result = Clip(Square(), p);
            Assert.That(result.Length, Is.GreaterThanOrEqualTo(3));
            foreach (Vector3 v in result)
            {
                Assert.That(v.y, Is.EqualTo(0f).Within(1e-6f), "in the cap's plane");
            }

            AssertInside(result, p, "three planes");
            Assert.That(Contains(result, new Vector3(-0.5f, 0f, 0.5f)), Is.True, "the corner the first two planes make");
        }

        /// <summary>
        /// Past six vertices: the square's four corners cut give an octagon; the cube-and-plane hexagon's six corners cut
        /// give twelve. Each cut adds one vertex. With room for eleven only, the twelve are refused, and nothing is written.
        /// </summary>
        [Test]
        public void MoreThanSixVertices_AreKept_AndTooLittleRoomIsRefused()
        {
            // |x| + |z| <= 1.5, as four planes: the square's corners go.
            var octagonPlanes = new Planes();
            int k = 1;
            foreach (float sx in new[] { -1f, 1f })
            {
                foreach (float sz in new[] { -1f, 1f })
                {
                    octagonPlanes.Add(Face(k++, -1f), new float4(sx, 0f, sz, -1.5f));
                }
            }

            Vector3[] octagon = Clip(Square(), octagonPlanes);
            Assert.That(octagon.Length, Is.EqualTo(8), "four corners cut: eight vertices");
            AssertInside(octagon, octagonPlanes, "octagon");

            // The regular hexagon of the cube [-1, 1]³ and x + y + z = 0, each corner cut at 0.9 of its distance.
            var builder = new VpCapBoundsPolygon();
            var hexagon = new Vector3[VpCapBoundsPolygon.MaxVertices];
            Assert.That(
                builder.TryBuild(new Bounds(Vector3.zero, Vector3.one * 2f), new float4(1f, 1f, 1f, 0f), Matrix4x4.identity,
                    VpCapBoundsPolygon.EpsilonFor(new Bounds(Vector3.zero, Vector3.one * 2f)), hexagon, 0, out int built, out _),
                Is.True);
            Assert.That(built, Is.EqualTo(6), "the layout: a hexagon");

            var corners = new Planes();
            for (int i = 0; i < 6; i++)
            {
                Vector3 direction = hexagon[i].normalized;
                corners.Add(Face(10 + i, 1f), new float4(-direction.x, -direction.y, -direction.z, 0.9f * hexagon[i].magnitude));
            }

            Vector3[] twelve = Clip(hexagon, corners);
            Assert.That(twelve.Length, Is.EqualTo(12), "six corners cut: twelve vertices");
            foreach (Vector3 v in twelve)
            {
                Assert.That(v.x + v.y + v.z, Is.EqualTo(0f).Within(1e-4f), "on the cap's plane");
            }

            AssertInside(twelve, corners, "twelve");
            Assert.That(VpCapPolygonClip.MaxVertices, Is.EqualTo(14), "six from the box, one more per cut, eight cuts");

            var small = new Vector3[11];
            for (int i = 0; i < small.Length; i++)
            {
                small[i] = new Vector3(9f, 9f, 9f);
            }

            Assert.That(
                VpCapPolygonClip.TryClip(hexagon, 6, k_own, corners.candidates, corners.states, corners.planes, Epsilon, small, out int count),
                Is.False, "room for eleven, where six plus six cuts need twelve");
            Assert.That(count, Is.Zero);
            Assert.That(small[0], Is.EqualTo(new Vector3(9f, 9f, 9f)), "nothing written");
        }

        /// <summary>
        /// Cut away entirely, down to a segment, down to a point: an empty success. Touching an edge from outside keeps
        /// the whole square.
        /// </summary>
        [Test]
        public void NothingLeft_ASegment_OrAPoint_IsEmpty_AndTouchingKeepsAll()
        {
            var cases = new (float4 plane, string what)[]
            {
                (new float4(1f, 0f, 0f, -2f), "x >= 2: nothing left"),
                (new float4(1f, 0f, 0f, -1f), "x >= 1: only the edge x = 1"),
                (new float4(1f, 0f, 1f, -2f), "x + z >= 2: only the corner (1, 1)"),
            };
            foreach ((float4 plane, string what) in cases)
            {
                Vector3[] result = Clip(Square(), new Planes().Add(Face(1), plane), k_own, out bool ok);
                Assert.That(ok, Is.True, what + ": a normal result");
                Assert.That(result, Is.Empty, what + ": nothing of area");
            }

            Vector3[] touching = Clip(Square(), new Planes().Add(Face(1), new float4(1f, 0f, 0f, 1f)));
            Assert.That(touching, Is.EqualTo(Square()), "x >= -1 touches the edge x = -1 and keeps everything");
        }

        /// <summary>
        /// Finite values past what single precision can add or subtract: the square scaled to ±3e38. Against x + z = 0
        /// a corner's distance is 6e38, and against x = 0 an edge's distance difference and length are 6e38 -- infinite
        /// in single precision, where the crossing would come out wrong or not at all. The clip is still the known half,
        /// and neither case is reported as a polygon with nothing left.
        /// </summary>
        [Test]
        public void LargeFiniteValues_AreClipped_NotLostToOverflow()
        {
            const float Big = 3e38f;
            var big = new[] { new Vector3(-Big, 0f, -Big), new Vector3(Big, 0f, -Big), new Vector3(Big, 0f, Big), new Vector3(-Big, 0f, Big) };
            Assert.That(float.IsInfinity(Big + Big), Is.True, "the layout: the sums overflow in single precision");

            Vector3[] diagonal = Clip(big, new Planes().Add(Face(1), new float4(1f, 0f, 1f, 0f)));
            Assert.That(diagonal, Is.EqualTo(new[] { big[1], big[2], big[3] }), "x + z >= 0: the corner (-3e38, -3e38) goes");

            Vector3[] half = Clip(big, new Planes().Add(Face(1), new float4(1f, 0f, 0f, 0f)));
            Assert.That(half.Length, Is.EqualTo(4), "x >= 0: a half, not an empty result");
            Assert.That(Contains(half, new Vector3(0f, 0f, -Big)), Is.True, "the crossing on the edge z = -3e38");
            Assert.That(Contains(half, new Vector3(0f, 0f, Big)), Is.True, "the crossing on the edge z = 3e38");
            Assert.That(Contains(half, big[1]) && Contains(half, big[2]), Is.True, "the kept corners");
        }

        /// <summary>
        /// The epsilon merges vertices and is not an area threshold. A thin triangle -- vertices half a unit and more apart,
        /// far beyond the epsilon, but a doubled area of 1e-11, below the epsilon squared -- keeps its area, with no
        /// plane and when cut. A segment written with three vertices has none.
        /// </summary>
        [Test]
        public void AThinPolygon_KeepsItsArea_AndASegmentHasNone()
        {
            var thin = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0.5f, 0f, 1e-11f) };
            Assert.That(AreaVector(thin).magnitude * 2f, Is.LessThan(Epsilon * Epsilon), "the layout: below the epsilon squared");

            Assert.That(Clip(thin, new Planes()), Is.EqualTo(thin), "no plane: kept as it is");

            Vector3[] cut = Clip(thin, new Planes().Add(Face(1), new float4(1f, 0f, 0f, -0.25f)));
            Assert.That(cut.Length, Is.GreaterThanOrEqualTo(3), "x >= 0.25: still a polygon");
            foreach (Vector3 v in cut)
            {
                Assert.That(v.x, Is.GreaterThanOrEqualTo(0.25f), "inside the plane");
            }

            var segment = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0f, 0f) };
            Vector3[] none = Clip(segment, new Planes(), k_own, out bool ok);
            Assert.That(ok, Is.True, "a segment is a normal result");
            Assert.That(none, Is.Empty, "with nothing of area");
        }

        /// <summary>
        /// The cap's own face is left out by identity, whatever plane or side is given for it; another boundary with the
        /// very same plane values still cuts.
        /// </summary>
        [Test]
        public void TheCapsOwnFace_IsLeftOutByIdentity_AndAnotherIsNot()
        {
            var ownOnly = new Planes()
                .Add(k_own, new float4(1f, 0f, 0f, -0.5f))
                .Add(new VpClipBoundary(k_own.face, -1f), new float4(1f, 0f, 0f, -0.5f));
            Assert.That(Clip(Square(), ownOnly), Is.EqualTo(Square()), "its own face, either side, never cuts");

            Planes both = new Planes()
                .Add(k_own, new float4(1f, 0f, 0f, -0.5f))
                .Add(Face(7), new float4(1f, 0f, 0f, -0.5f));
            Vector3[] result = Clip(Square(), both);
            Assert.That(result.Length, Is.EqualTo(4));
            foreach (Vector3 v in result)
            {
                Assert.That(v.x, Is.GreaterThanOrEqualTo(0.5f - 1e-5f), "another face with the same plane cuts");
            }
        }

        /// <summary>
        /// Consumed from a real ledger chain of ten: eight selected, two Ignored, the cap being the tenth -- Ignored
        /// itself. An Ignored plane that would remove everything does not cut; a selected one does, although the cap's
        /// own boundary is not among the selected.
        /// </summary>
        [Test]
        public void AnIgnoredPlaneDoesNotCut_AndTheOthersApplyWhenTheCapItselfIsIgnored()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(16));
            LogicalFragmentId at = ledger.AddFragment();
            for (int k = 0; k < 10; k++)
            {
                Assert.That(ledger.Admit(at, new float4(0f, 1f, 0f, -0.1f * (k + 1)), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
                Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                at = positive;
            }

            var candidates = new List<VpClipCandidate>();
            Assert.That(VpClipCandidates.TryCollect(ledger, at, 0f, Array.Empty<VpClipBoundary>(), candidates), Is.True);
            var states = new VpClipSelectionState[candidates.Count];
            VpClipCandidates.Select(candidates, states);
            Assert.That(states[8], Is.EqualTo(VpClipSelectionState.IgnoredCapacity), "the layout: the ninth is Ignored");
            Assert.That(states[9], Is.EqualTo(VpClipSelectionState.IgnoredCapacity), "and so is the tenth, the cap's own");

            // In the square's frame: the selected planes keep everything but one, which keeps x >= 0; the Ignored ninth
            // would keep x >= 5 and so nothing.
            var planes = new float4[candidates.Count];
            for (int i = 0; i < planes.Length; i++)
            {
                planes[i] = new float4(1f, 0f, 0f, 5f);
            }

            planes[3] = new float4(1f, 0f, 0f, 0f);
            planes[8] = new float4(1f, 0f, 0f, -5f);
            VpClipBoundary own = candidates[9].boundary;

            var output = new Vector3[VpCapPolygonClip.MaxVertices];
            Assert.That(VpCapPolygonClip.TryClip(Square(), 4, own, candidates, states, planes, Epsilon, output, out int count), Is.True);
            Assert.That(count, Is.EqualTo(4), "not emptied by the Ignored ninth");
            for (int i = 0; i < count; i++)
            {
                Assert.That(output[i].x, Is.GreaterThanOrEqualTo(-1e-5f), "cut by the selected fourth, though the cap is Ignored");
            }
        }

        /// <summary>
        /// The same clip in a turned and moved frame is the same polygon turned and moved, in the same order and wound the
        /// same way. The inputs come back untouched. Malformed input is refused; a plane that does not cut is not read.
        /// </summary>
        [Test]
        public void AMovedFrame_GivesTheMovedResult_AndTheInputsAreUntouched()
        {
            Planes p = new Planes()
                .Add(Face(1, 1f), new float4(1f, 0f, 0f, 0.5f))
                .Add(Face(2, -1f), new float4(1f, 0f, 1f, -0.8f));
            Vector3[] plain = Clip(Square(), p);

            Matrix4x4 m = Matrix4x4.TRS(new Vector3(3f, -2f, 5f), Quaternion.Euler(20f, 35f, 10f), Vector3.one);
            Vector3[] moved = Square();
            for (int i = 0; i < moved.Length; i++)
            {
                moved[i] = m.MultiplyPoint3x4(moved[i]);
            }

            var movedPlanes = new Planes();
            for (int i = 0; i < p.candidates.Count; i++)
            {
                float4 plane = p.planes[i];
                Vector3 n = m.MultiplyVector(new Vector3(plane.x, plane.y, plane.z));
                float d = plane.w - Vector3.Dot(n, m.GetColumn(3));
                movedPlanes.Add(p.candidates[i].boundary, new float4(n.x, n.y, n.z, d));
            }

            Vector3[] movedCopy = (Vector3[])moved.Clone();
            var planesCopy = new List<float4>(movedPlanes.planes);
            var statesCopy = new List<VpClipSelectionState>(movedPlanes.states);
            var candidatesCopy = new List<VpClipCandidate>(movedPlanes.candidates);
            Vector3[] result = Clip(moved, movedPlanes);

            Assert.That(result.Length, Is.EqualTo(plain.Length));
            for (int i = 0; i < plain.Length; i++)
            {
                Assert.That((result[i] - m.MultiplyPoint3x4(plain[i])).magnitude, Is.LessThan(1e-4f), "vertex " + i);
            }

            Assert.That(Vector3.Dot(AreaVector(result), m.MultiplyVector(AreaVector(plain))), Is.GreaterThan(0f), "wound the same way");
            Assert.That(moved, Is.EqualTo(movedCopy), "the polygon untouched");
            Assert.That(movedPlanes.planes, Is.EqualTo(planesCopy));
            Assert.That(movedPlanes.states, Is.EqualTo(statesCopy));
            Assert.That(movedPlanes.candidates, Is.EqualTo(candidatesCopy));

            var output = new Vector3[VpCapPolygonClip.MaxVertices];
            Assert.That(VpCapPolygonClip.TryClip(Square(), 5, k_own, p.candidates, p.states, p.planes, Epsilon, output, out _), Is.False,
                "a count past the polygon");
            Vector3[] nan = Square();
            nan[2].x = float.NaN;
            Assert.That(VpCapPolygonClip.TryClip(nan, 4, k_own, p.candidates, p.states, p.planes, Epsilon, output, out _), Is.False,
                "a vertex that is not finite");
            Planes zero = new Planes().Add(Face(1), float4.zero);
            Assert.That(VpCapPolygonClip.TryClip(Square(), 4, k_own, zero.candidates, zero.states, zero.planes, Epsilon, output, out _), Is.False,
                "a cutting plane with no normal");
            Planes ignoredNaN = new Planes().Add(Face(1), new float4(float.NaN, 0f, 0f, 0f), VpClipSelectionState.IgnoredOrder);
            Assert.That(VpCapPolygonClip.TryClip(Square(), 4, k_own, ignoredNaN.candidates, ignoredNaN.states, ignoredNaN.planes, Epsilon, output, out int kept), Is.True,
                "an Ignored plane is not read");
            Assert.That(kept, Is.EqualTo(4));
        }
    }
}
