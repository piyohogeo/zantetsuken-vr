using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The caps' shapes are made in the geometry's local frame and carried by the placement (D-208). Before, a cap was
    /// made where its render fragment stood: the section taken with the placement, and cut by the other planes in
    /// world. This compares the two, cap by cap -- the snapshot's caps as placed against that earlier way, made again
    /// here from the same two parts (<see cref="VpCapBoundsPolygon"/>, <see cref="VpCapPolygonClip"/>) with the same
    /// epsilon -- over lineages with both sides' caps, several faces, thin caps and vertices close together, under
    /// placements that move and turn, scale evenly and unevenly, and mirror.
    /// <para>
    /// **The epsilon.** It is a length in the geometry's own local units (<see cref="VpCapBoundsPolygon"/>), and the
    /// section has always merged its vertices in those units. The cut, before, merged a cap's neighbouring vertices by
    /// the same number applied to their **placed** distance. Under a placement that keeps lengths (a move and a turn --
    /// every placement the product gives) the two are one measure, and the caps must agree: the same vertices, in the
    /// same sense about the same outward normal. Under a placement that scales they are two measures, and a feature
    /// whose length lies between them is merged by one and kept by the other. Where both keep it the caps must still
    /// agree; where they may not, this asserts only what holds either way (the two differ by no more than the merge
    /// length as placed) and writes out which caps came out with another number of vertices.
    /// </para>
    /// <para>
    /// **The rule (DESIGN 5.6, D-209).** The merge is by the distance in the geometry's local frame, for the section
    /// and for the cut alike. The difference written out here under a scaling placement is the recorded difference
    /// from the way before, not something to be brought back: this test keeps comparing so that it stays what was
    /// recorded -- none under a placement that keeps lengths, and only features between the two measures otherwise.
    /// </para>
    /// </summary>
    public class VpMultiCutSnapshotLocalCapFrameTests
    {
        private static readonly Bounds k_box = VpMultiCutSnapshotTests.k_box;   // 2 x 2 x 2 about the origin
        private static readonly float k_epsilon = VpCapBoundsPolygon.EpsilonFor(k_box);

        private sealed class Lineage
        {
            public string what;
            public LogicalCutLedger ledger;
            public LogicalFragmentId root;
            public float feature;   // the least length between two neighbouring vertices of any of its caps, local; 0: not said
        }

        private static float4 Normalized(float4 plane) => plane / math.length(plane.xyz);

        // The corner of the section of y = 0 at (1, 0, 1), cut off by a plane across it: an edge of length t * sqrt(2)
        // on the larger side's cap, and a triangle that small on the other side's.
        private static float4 AcrossTheCorner(float t) => Normalized(new float4(1f, 0f, 1f, -(2f - t)));

        private static Lineage Make(string what, float feature, params float4[] downThePositiveSide)
        {
            var made = new Lineage { what = what, ledger = VpMultiCutSnapshotTests.NewLedger(), feature = feature };
            made.root = made.ledger.AddFragment();
            LogicalFragmentId at = made.root;
            foreach (float4 plane in downThePositiveSide)
            {
                at = VpMultiCutSnapshotTests.Cut(made.ledger, at, plane).positive;
            }

            return made;
        }

        private static List<Lineage> Lineages()
        {
            float e = k_epsilon;
            var across = new float4(0f, 1f, 0f, 0f);
            var list = new List<Lineage>
            {
                Make("one cut across", 2f, across),
                Make("two cuts, the second along", 0.7f, across, new float4(1f, 0f, 0f, -0.3f)),
                Make("three oblique cuts", 0.05f, Normalized(new float4(0.2f, 1f, 0.1f, -0.1f)), Normalized(new float4(1f, 0.3f, 0f, 0.2f)), Normalized(new float4(0f, 0.2f, 1f, -0.3f))),
                Make("four cuts", 0.05f, Normalized(new float4(0.2f, 1f, 0.1f, 0.4f)), Normalized(new float4(1f, 0.3f, 0f, 0.5f)), Normalized(new float4(0f, 0.2f, 1f, 0.3f)), Normalized(new float4(-1f, 0.1f, -0.2f, 0.6f))),
                Make("a thin cap, 0.002 wide", 0.002f, across, new float4(1f, 0f, 0f, -0.998f)),
                Make("a thinner cap, 2e-5 wide", 2e-5f, across, new float4(1f, 0f, 0f, -0.99998f)),
                Make("a corner cut off 1e-4 in", 1e-4f, across, AcrossTheCorner(1e-4f)),
                Make("an edge of 10 epsilon", 10f * e / math.SQRT2, across, AcrossTheCorner(10f * e / math.SQRT2)),
                Make("an edge of half an epsilon", 0.5f * e / math.SQRT2, across, AcrossTheCorner(0.5f * e / math.SQRT2)),
                Make("a plane through a vertex of the section", 0f, across, AcrossTheCorner(0f)),
            };

            // One with both sides cut again, so that a face is shared by more render fragments than its two sides.
            var both = new Lineage { what = "both sides cut again", ledger = VpMultiCutSnapshotTests.NewLedger(), feature = 0.3f };
            both.root = both.ledger.AddFragment();
            (_, LogicalFragmentId positive, LogicalFragmentId negative) = VpMultiCutSnapshotTests.Cut(both.ledger, both.root, Normalized(new float4(0.1f, 1f, 0f, 0.2f)));
            VpMultiCutSnapshotTests.Cut(both.ledger, positive, Normalized(new float4(1f, 0.2f, 0.1f, -0.3f)));
            VpMultiCutSnapshotTests.Cut(both.ledger, negative, Normalized(new float4(0.3f, 0f, 1f, 0.4f)));
            list.Add(both);
            return list;
        }

        private static (Matrix4x4 m, string what)[] Placements() => new[]
        {
            (Matrix4x4.identity, "as it stands"),
            (Matrix4x4.TRS(new Vector3(4f, -1f, 2f), Quaternion.Euler(20f, -55f, 10f), Vector3.one), "moved and turned"),
            (Matrix4x4.TRS(new Vector3(-3f, 2f, 1f), Quaternion.Euler(-70f, 15f, 130f), Vector3.one), "moved and turned again"),
            (Matrix4x4.TRS(new Vector3(1f, 0.5f, -1f), Quaternion.Euler(10f, 20f, 30f), Vector3.one * 0.01f), "a hundredth the size"),
            (Matrix4x4.TRS(new Vector3(1f, 0.5f, -1f), Quaternion.Euler(10f, 20f, 30f), Vector3.one * 0.5f), "half the size"),
            (Matrix4x4.TRS(new Vector3(1f, 0.5f, -1f), Quaternion.Euler(10f, 20f, 30f), Vector3.one * 3f), "three times the size"),
            (Matrix4x4.TRS(new Vector3(1f, 0.5f, -1f), Quaternion.Euler(10f, 20f, 30f), Vector3.one * 100f), "a hundred times the size"),
            (Matrix4x4.TRS(new Vector3(-2f, 3f, 1f), Quaternion.Euler(-15f, 40f, 65f), new Vector3(0.5f, 2.5f, 1.5f)), "scaled unevenly"),
            (Matrix4x4.TRS(new Vector3(1f, 2f, -2f), Quaternion.Euler(0f, 30f, 0f), new Vector3(1f, -2f, 1f)), "mirrored, scaled unevenly"),
            (Matrix4x4.TRS(new Vector3(1f, 2f, -2f), Quaternion.Euler(25f, 30f, -40f), new Vector3(-1f, 1f, 1f)), "mirrored, lengths kept"),
        };

        // The least and the greatest stretch of a length under the placement: the square roots of the extreme
        // eigenvalues of its linear part's Gram matrix, bounded here by its columns' lengths for the axis-aligned
        // scales these placements are made of (a rotation after a scale along the axes).
        private static (float least, float greatest) Stretch(Matrix4x4 m)
        {
            float x = ((Vector3)m.GetColumn(0)).magnitude, y = ((Vector3)m.GetColumn(1)).magnitude, z = ((Vector3)m.GetColumn(2)).magnitude;
            return (Mathf.Min(x, Mathf.Min(y, z)), Mathf.Max(x, Mathf.Max(y, z)));
        }

        /// <summary>
        /// A cap as it was made before D-208, from what the snapshot says of its render fragment: the section of the
        /// cap's own plane through the box taken **with the placement** (its vertices placed, ordered about the world
        /// normal), read backwards for the positive side, and cut **in world** by the render fragment's other selected
        /// planes carried to world, with the same epsilon.
        /// </summary>
        private static Vector3[] MadeWherePlaced(VpMultiCutSnapshot snapshot, in VpMultiCutCap cap, out float4 worldPlane)
        {
            Assert.That(snapshot.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment fragment), Is.True);
            Assert.That(snapshot.TryGetBranch(fragment.branchStart, out VpMultiCutBranch branch), Is.True);
            var candidates = new List<VpClipCandidate>();
            var states = new List<VpClipSelectionState>();
            var planes = new List<float4>();
            float4 ownLocal = default;
            worldPlane = default;
            bool found = false;
            for (int j = 0; j < branch.selectedCount; j++)
            {
                Assert.That(snapshot.TryGetCandidate(branch.candidateStart + j, out VpClipCandidate candidate, out VpClipSelectionState state), Is.True);
                Assert.That(state, Is.EqualTo(VpClipSelectionState.Selected), "the layout: the selected ones come first");
                Assert.That(VpCutPlane.TryGeometryLocalToWorld(candidate.plane, Matrix4x4.identity, out float4 local), Is.True);
                Assert.That(VpCutPlane.TryGeometryLocalToWorld(local, fragment.geometryLocalToWorld, out float4 world), Is.True);
                candidates.Add(candidate);
                states.Add(state);
                planes.Add(world);
                if (candidate.boundary == cap.boundary)
                {
                    ownLocal = local;
                    worldPlane = world;
                    found = true;
                }
            }

            Assert.That(found, Is.True, "the cap's own boundary is one of its render fragment's selected ones");
            var initial = new Vector3[VpCapBoundsPolygon.MaxVertices];
            Assert.That(
                new VpCapBoundsPolygon().TryBuild(fragment.localBounds, ownLocal, fragment.geometryLocalToWorld, k_epsilon, initial, 0, out int count, out _),
                Is.True, "the section, taken where it is placed");
            if (cap.boundary.side > 0f)
            {
                Array.Reverse(initial, 0, count);
            }

            var clipped = new Vector3[VpCapPolygonClip.MaxVertices];
            int kept = 0;
            if (count > 0)
            {
                Assert.That(VpCapPolygonClip.TryClip(initial, count, cap.boundary, candidates, states, planes, k_epsilon, clipped, out kept), Is.True, "cut in world");
            }

            var made = new Vector3[kept];
            Array.Copy(clipped, made, kept);
            return made;
        }

        private static Vector3[] AsPlaced(VpMultiCutSnapshot snapshot, int c, in VpMultiCutCap cap)
        {
            var placed = new Vector3[cap.vertexCount];
            for (int v = 0; v < placed.Length; v++)
            {
                Assert.That(snapshot.TryGetCapVertex(c, v, out placed[v]), Is.True);
            }

            return placed;
        }

        // The doubled area vector, in double and about the polygon's first vertex: along the normal the polygon is
        // wound about.
        private static double3 AreaVector(Vector3[] p)
        {
            double3 sum = default;
            var origin = new double3(p[0].x, p[0].y, p[0].z);
            for (int i = 1; i + 1 < p.Length; i++)
            {
                double3 a = new double3(p[i].x, p[i].y, p[i].z) - origin, b = new double3(p[i + 1].x, p[i + 1].y, p[i + 1].z) - origin;
                sum += math.cross(a, b);
            }

            return sum;
        }

        // A polygon goes round its outward normal. One that is only a few floats wide where it is placed has a normal
        // its own vertices' rounding decides (a cap 2e-5 wide at a hundredth the size is two floats wide): of such a
        // one only the sense is asked.
        private static void AssertGoesRound(Vector3[] p, double3 outward, double resolution, string what)
        {
            if (p.Length < 3)
            {
                return;
            }

            double along = math.dot(math.normalizesafe(AreaVector(p)), outward);
            Assert.That(along, Is.GreaterThan(0d), what + ": the sense");
            if (Width(p) > 1000d * resolution)
            {
                Assert.That(along, Is.GreaterThan(0.999), what + ": about the normal itself");
            }
        }

        private static double ToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            double3 P = new double3(p.x, p.y, p.z), A = new double3(a.x, a.y, a.z), B = new double3(b.x, b.y, b.z);
            double3 ab = B - A;
            double length = math.lengthsq(ab);
            double t = length > 0d ? math.saturate(math.dot(P - A, ab) / length) : 0d;
            return math.length(P - (A + (ab * t)));
        }

        // How far the outlines of two polygons are from one another: the farthest any vertex of either is from the
        // other's outline.
        private static double Apart(Vector3[] a, Vector3[] b)
        {
            double One(Vector3[] from, Vector3[] to)
            {
                double worst = 0d;
                foreach (Vector3 p in from)
                {
                    double nearest = double.MaxValue;
                    for (int i = 0; i < to.Length; i++) nearest = Math.Min(nearest, ToSegment(p, to[i], to[(i + 1) % to.Length]));
                    worst = Math.Max(worst, nearest);
                }

                return worst;
            }

            return Math.Max(One(a, b), One(b, a));
        }

        // How far a polygon is from being a segment: the farthest any vertex is from the line of its two vertices
        // farthest apart. What a cap is taken for when one way keeps it and the other merges it away.
        private static double Width(Vector3[] p)
        {
            int from = 0, to = 0;
            float farthest = -1f;
            for (int i = 0; i < p.Length; i++)
                for (int j = i + 1; j < p.Length; j++)
                    if ((p[i] - p[j]).sqrMagnitude > farthest) { farthest = (p[i] - p[j]).sqrMagnitude; from = i; to = j; }
            double worst = 0d;
            foreach (Vector3 q in p) worst = Math.Max(worst, ToSegment(q, p[from], p[to]));
            return worst;
        }

        [Test]
        public void LocalCapsCarriedByThePlacement_AreTheCapsMadeWherePlaced_UnderEveryPlacementThatKeepsLengths_AndWithinTheMergeLengthUnderOneThatScales()
        {
            int compared = 0, mustAgree = 0, otherCounts = 0, mirroredCaps = 0, emptyBoth = 0;
            double worstWhereLengthsKept = 0d, worstUlps = 0d;
            var differing = new List<string>();
            foreach (Lineage lineage in Lineages())
            {
                foreach ((Matrix4x4 placement, string where) in Placements())
                {
                    string what = lineage.what + ", " + where;
                    (float least, float greatest) = Stretch(placement);
                    bool keepsLengths = Mathf.Abs(least - 1f) < 1e-5f && Mathf.Abs(greatest - 1f) < 1e-5f;
                    bool mirrors = placement.determinant < 0f;

                    // What a float at the farthest a placed vertex reaches can tell apart, 64 times over: the two ways
                    // round at other steps (a vertex placed and then cut; cut and then placed).
                    float reach = (greatest * k_box.extents.magnitude) + ((Vector3)placement.GetColumn(3)).magnitude;
                    double rounding = 64d * 1.2e-7 * reach;

                    // Both ways keep a feature that is clear of the epsilon in local and as placed (twice over); both
                    // merge nothing then, and the caps must be the same.
                    bool bothKeep = lineage.feature >= 2f * k_epsilon && lineage.feature * least >= 2f * k_epsilon;
                    double merge = 4d * k_epsilon * Math.Max(1f, greatest);

                    VpMultiCutSnapshot snapshot = VpMultiCutSnapshotTests.NewSnapshot();
                    Assert.That(
                        snapshot.TryBuild(lineage.ledger, lineage.root, k_box, placement, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, k_epsilon),
                        Is.EqualTo(VpMultiCutBuildOutcome.Built), what);
                    Assert.That(snapshot.CapCount, Is.GreaterThan(0), what + ": caps");
                    for (int c = 0; c < snapshot.CapCount; c++)
                    {
                        Assert.That(snapshot.TryGetCap(c, out VpMultiCutCap cap), Is.True);
                        string which = what + ", cap " + c + " (side " + cap.boundary.side + ")";
                        Vector3[] carried = AsPlaced(snapshot, c, cap);
                        Vector3[] before = MadeWherePlaced(snapshot, cap, out float4 worldPlane);
                        compared++;
                        if (mirrors) mirroredCaps++;

                        // The plane and the outward normal: the plane the cap's own boundary is carried to, and the
                        // direction away from the side that is kept.
                        Assert.That(math.length(cap.worldPlane - worldPlane), Is.LessThan(1e-5f * Math.Max(1f, math.abs(worldPlane.w))), which + ": the world plane");
                        var outward = new double3(cap.outwardNormal.x, cap.outwardNormal.y, cap.outwardNormal.z);
                        Assert.That(math.length(outward - (-cap.boundary.side * new double3(worldPlane.x, worldPlane.y, worldPlane.z))), Is.LessThan(1e-5), which + ": the outward normal");

                        // The sense: each way's vertices go round the outward normal, mirrored or not.
                        AssertGoesRound(carried, outward, 1.2e-7 * reach, which + ": the carried cap is wound about its outward normal");
                        AssertGoesRound(before, outward, 1.2e-7 * reach, which + ": the layout -- so was the cap made where placed");

                        if (carried.Length == 0 && before.Length == 0)
                        {
                            emptyBoth++;
                            continue;
                        }

                        if (carried.Length != before.Length)
                        {
                            otherCounts++;
                            differing.Add(which + ": " + carried.Length + " vertices carried, " + before.Length + " made where placed (feature " + (lineage.feature / k_epsilon).ToString("G3")
                                + " epsilon local, " + (lineage.feature * least / k_epsilon).ToString("G3") + " to " + (lineage.feature * greatest / k_epsilon).ToString("G3") + " as placed)");
                        }

                        // How far apart the two came out. One of them merged away: the other is taken as the segment it
                        // nearly is.
                        double apart = carried.Length == 0 ? Width(before) : before.Length == 0 ? Width(carried) : Apart(carried, before);
                        if (bothKeep)
                        {
                            mustAgree++;
                            Assert.That(carried.Length, Is.EqualTo(before.Length), which + ": the same number of vertices");
                            Assert.That(apart, Is.LessThan(rounding), which + ": the same vertices, to what a float tells apart there");
                            worstUlps = Math.Max(worstUlps, apart / (1.2e-7 * reach));
                            if (keepsLengths) worstWhereLengthsKept = Math.Max(worstWhereLengthsKept, apart);
                        }
                        else
                        {
                            Assert.That(apart, Is.LessThan(merge + rounding), which + ": apart by no more than the merge length as placed");
                            if (keepsLengths)
                            {
                                // One measure: a feature under the epsilon is merged by both, to the same cap but for
                                // which of two vertices a hair apart is the one kept.
                                Assert.That(apart, Is.LessThan((4d * k_epsilon) + rounding), which);
                            }
                        }
                    }
                }
            }

            TestContext.Out.WriteLine("caps compared " + compared + " (" + mirroredCaps + " under a mirroring placement, " + emptyBoth + " empty both ways); required to be the same " + mustAgree
                + "; another number of vertices in " + otherCounts);
            TestContext.Out.WriteLine("where required the same: the farthest apart under a placement that keeps lengths " + worstWhereLengthsKept.ToString("G3")
                + ", and over all " + worstUlps.ToString("F1") + " times what a float tells apart at the placed reach (allowed 64)");
            foreach (string line in differing) TestContext.Out.WriteLine("  another number of vertices: " + line);
            Assert.That(compared, Is.GreaterThan(300), "the layout: the cases were compared");
            Assert.That(mustAgree, Is.GreaterThan(200));
        }
    }
}
