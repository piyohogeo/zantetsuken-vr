using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Where each render fragment is drawn when the fragments of one registration stand in different places
    /// (DESIGN 5.1, 5.2, 7.1.2): the body, the caps, the planes and the separation of one branch all come from that
    /// branch's own placement, and a commit takes its own boundary's separation into the geometry's frame so that what
    /// was folded in moves with the fragment afterwards.
    /// <para>
    /// The expected values are built here from the placements the test chose — matrices multiplied out by hand — and
    /// never from the lookup the product reads. The frames are deliberately different: the geometry's local origin is
    /// not the owner's, so a mapping taken from the wrong end shows up as a wrong position rather than as nothing.
    /// </para>
    /// </summary>
    public class VpFragmentPlacementTests
    {
        private const float Tolerance = 1e-4f;
        private const float Separation = 0.5f;

        /// <summary>The geometry's local frame inside the frame a fragment follows: its origin is at (0, 1, 0) there.</summary>
        private static readonly Matrix4x4 k_geometryLocalToOwner = Matrix4x4.Translate(new Vector3(0f, 1f, 0f));

        /// <summary>The lineage's logical frame into the geometry's: the other way round, and never identity.</summary>
        private static readonly Matrix4x4 k_lineageToGeometryLocal = Matrix4x4.Translate(new Vector3(0f, -1f, 0f));

        private static readonly Bounds k_box = new Bounds(Vector3.zero, Vector3.one * 2f);
        private static readonly VpClipBoundary[] k_none = System.Array.Empty<VpClipBoundary>();

        /// <summary>Where the test says fragments stand. What it is asked is recorded, so a test can say what was asked.</summary>
        private sealed class Placements : IVpFragmentPlacement
        {
            internal readonly Dictionary<LogicalFragmentId, Matrix4x4> of = new Dictionary<LogicalFragmentId, Matrix4x4>();
            internal readonly HashSet<LogicalFragmentId> notFollowing = new HashSet<LogicalFragmentId>();
            internal readonly List<LogicalFragmentId> asked = new List<LogicalFragmentId>();

            /// <summary>What a fragment nobody said anything about is answered with. Static by default.</summary>
            internal VpFragmentPlacementKind unknown = VpFragmentPlacementKind.Static;

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(
                LogicalFragmentId fragment, out Matrix4x4 geometryLocalToWorld)
            {
                asked.Add(fragment);
                if (of.TryGetValue(fragment, out geometryLocalToWorld))
                {
                    return VpFragmentPlacementKind.Following;
                }

                geometryLocalToWorld = Matrix4x4.identity;
                return notFollowing.Contains(fragment) ? VpFragmentPlacementKind.Static : unknown;
            }
        }

        private static LogicalCutLedger NewLedger() => new LogicalCutLedger(new LogicalCutIncompleteBudget(64));

        private static VpMultiCutSnapshot NewSnapshot() =>
            new VpMultiCutSnapshot(new VpMultiCutCapacities(64, 1024, 64, 512, 64));

        private static (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) Cut(
            LogicalCutLedger ledger, LogicalFragmentId source, float4 plane)
        {
            Assert.That(ledger.Admit(source, plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(
                ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative),
                Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (cut, positive, negative);
        }

        private static Matrix4x4 Owner(Vector3 position, float degreesAboutZ)
        {
            return Matrix4x4.TRS(position, Quaternion.AngleAxis(degreesAboutZ, Vector3.forward), Vector3.one);
        }

        private static VpMultiCutRegistration Registration(
            LogicalFragmentId root, Matrix4x4 geometryLocalToWorld, VpClipBoundary[] reflected = null,
            Matrix4x4? folded = null)
        {
            return new VpMultiCutRegistration(
                root, k_box, geometryLocalToWorld, k_lineageToGeometryLocal, reflected ?? k_none, 1e-4f,
                folded ?? Matrix4x4.identity);
        }

        private static VpMultiCutRenderFragment Of(VpMultiCutSnapshot snapshot, LogicalFragmentId fragment)
        {
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment found), Is.True);
                if (found.root == fragment)
                {
                    return found;
                }
            }

            Assert.Fail("no render fragment is drawn for that fragment");
            return default;
        }

        private static bool Has(VpMultiCutSnapshot snapshot, LogicalFragmentId fragment)
        {
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment found), Is.True);
                if (found.root == fragment)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Same(Matrix4x4 expected, Matrix4x4 actual, string what)
        {
            for (int i = 0; i < 16; i++)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(Tolerance), what + " [" + i + "]");
            }
        }

        private static void Same(Vector3 expected, Vector3 actual, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(Tolerance), what + ".x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(Tolerance), what + ".y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(Tolerance), what + ".z");
        }

        /// <summary>Where a point of the geometry is really drawn: its placement, and the separation on top.</summary>
        private static Vector3 Drawn(in VpMultiCutRenderFragment rf, Vector3 local)
        {
            return rf.geometryLocalToWorld.MultiplyPoint3x4(local) + rf.offset;
        }

        // ----- the two sides of one cut, standing in different places ---------------------------------------------------

        /// <summary>
        /// The sides of a published cut move and turn apart. Each one's body, plane, cap normal and cap vertices come
        /// from its own placement, and the separation each takes runs along its own current normal.
        /// </summary>
        [Test]
        public void TwoSidesThatMovedApart_AreEachDrawnWhereTheyStand()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var plane = new float4(0f, 1f, 0f, 0f);
            var (_, positive, negative) = Cut(ledger, root, plane);

            Matrix4x4 pPositive = Owner(new Vector3(3f, 0f, 0f), 90f);
            Matrix4x4 pNegative = Owner(new Vector3(-2f, 1f, 0f), 0f);
            var placements = new Placements();
            placements.of[positive] = pPositive * k_geometryLocalToOwner;
            placements.of[negative] = pNegative * k_geometryLocalToOwner;

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(
                snapshot.TryBuild(
                    ledger, root, k_box, Matrix4x4.identity * k_geometryLocalToOwner, k_lineageToGeometryLocal, k_none,
                    Separation, 1e-4f, placements),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));

            VpMultiCutRenderFragment rfPositive = Of(snapshot, positive);
            VpMultiCutRenderFragment rfNegative = Of(snapshot, negative);

            // Each side stands where it was put, not where the registration is.
            Same(pPositive * k_geometryLocalToOwner, rfPositive.geometryLocalToWorld, "the positive placement");
            Same(pNegative * k_geometryLocalToOwner, rfNegative.geometryLocalToWorld, "the negative placement");

            // The plane is y = 0 of the lineage frame. Turned 90 degrees about z, its normal is -x for the positive
            // side; the negative side did not turn, so its normal is still +y.
            Same(new Vector3(-1f, 0f, 0f), rfPositive.offset / Separation, "the positive separation direction");
            Same(new Vector3(0f, 1f, 0f), rfNegative.offset / Separation * -1f, "the negative separation direction");
            Same(new Vector3(-0.5f, 0f, 0f), rfPositive.offset, "the positive separation");
            Same(new Vector3(0f, -0.5f, 0f), rfNegative.offset, "the negative separation");

            // Several points of the geometry, each where its own side puts it.
            foreach (Vector3 local in new[] { Vector3.zero, new Vector3(1f, 1f, 1f), new Vector3(-1f, 0.5f, -1f) })
            {
                Same(
                    (pPositive * k_geometryLocalToOwner).MultiplyPoint3x4(local) + new Vector3(-0.5f, 0f, 0f),
                    Drawn(in rfPositive, local), "a positive point");
                Same(
                    (pNegative * k_geometryLocalToOwner).MultiplyPoint3x4(local) + new Vector3(0f, -0.5f, 0f),
                    Drawn(in rfNegative, local), "a negative point");
            }
        }

        /// <summary>
        /// The caps of the two sides are made with the same placements as their bodies: the world plane, the outward
        /// normal and every vertex of the polygon.
        /// </summary>
        [Test]
        public void TheCapsOfEachSide_AreMadeWithThatSidesPlacement()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var plane = new float4(0f, 1f, 0f, 0f);
            var (_, positive, negative) = Cut(ledger, root, plane);

            Matrix4x4 pPositive = Owner(new Vector3(3f, 0f, 0f), 90f);
            Matrix4x4 pNegative = Owner(new Vector3(-2f, 1f, 0f), 0f);
            var placements = new Placements();
            placements.of[positive] = pPositive * k_geometryLocalToOwner;
            placements.of[negative] = pNegative * k_geometryLocalToOwner;

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(
                snapshot.TryBuild(
                    ledger, root, k_box, k_geometryLocalToOwner, k_lineageToGeometryLocal, k_none, Separation, 1e-4f,
                    placements),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));

            VpMultiCutRenderFragment rfPositive = Of(snapshot, positive);
            Assert.That(rfPositive.capCount, Is.GreaterThan(0), "the positive side has a cap");
            Assert.That(snapshot.TryGetCap(rfPositive.capStart, out VpMultiCutCap capPositive), Is.True);

            // The plane of the cut, carried by the positive side's own placement: y = 0 of the lineage frame is
            // x = 3 in the world once that side has been moved to (3,0,0) and turned by 90 degrees.
            Same(new Vector3(-1f, 0f, 0f), new Vector3(capPositive.worldPlane.x, capPositive.worldPlane.y, capPositive.worldPlane.z), "the cap plane normal");
            Assert.That(capPositive.worldPlane.w, Is.EqualTo(3f).Within(Tolerance), "the cap plane offset");
            // The outward normal points out of the side that is kept, so it is the world plane's normal reversed for
            // the positive side. It is the side's own placement that turned both.
            Same(new Vector3(1f, 0f, 0f), capPositive.outwardNormal, "the cap outward normal");

            // Every vertex of the polygon stands where that side stands, with that side's separation: the vertices
            // carry the separation, the plane does not, so they sit on the plane once the separation is taken off.
            Assert.That(capPositive.vertexCount, Is.GreaterThanOrEqualTo(3), "a polygon");
            var normal = new Vector3(capPositive.worldPlane.x, capPositive.worldPlane.y, capPositive.worldPlane.z);
            for (int v = 0; v < capPositive.vertexCount; v++)
            {
                Assert.That(snapshot.TryGetCapVertex(rfPositive.capStart, v, out Vector3 world), Is.True);
                Assert.That(
                    Vector3.Dot(normal, world - rfPositive.offset) + capPositive.worldPlane.w,
                    Is.EqualTo(0f).Within(1e-3f),
                    "cap vertex " + v + " is on the plane its own side carried");
                Assert.That(world.x, Is.EqualTo(2.5f).Within(1e-3f), "cap vertex " + v + " stands where that side stands");
            }

            // The negative side's cap is on the same logical plane, carried by its own placement: y = 1 in the world.
            VpMultiCutRenderFragment rfNegative = Of(snapshot, negative);
            Assert.That(snapshot.TryGetCap(rfNegative.capStart, out VpMultiCutCap capNegative), Is.True);
            Same(new Vector3(0f, 1f, 0f), capNegative.outwardNormal, "the negative cap outward normal");
            for (int v = 0; v < capNegative.vertexCount; v++)
            {
                Assert.That(snapshot.TryGetCapVertex(rfNegative.capStart, v, out Vector3 world), Is.True);
                Assert.That(world.y, Is.EqualTo(0.5f).Within(1e-3f), "negative cap vertex " + v);
            }
        }

        // ----- the worked example: A, then B, with a commit between them -------------------------------------------------

        /// <summary>
        /// The sequence of the design note, in numbers: A published, A+ moved and turned, B admitted on A+ and
        /// published, A committed, the child turned again, B committed. At every step the drawn position is the one
        /// worked out here, and nothing is lost or counted twice as a commit takes its own boundary's separation in.
        /// </summary>
        [Test]
        public void TheWorkedExample_KeepsEveryStepWhereItBelongs()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var placements = new Placements();
            VpMultiCutSnapshot snapshot = NewSnapshot();
            var registrations = new List<VpMultiCutRegistration>(1);

            // --- t0: A published, everything still at the source's placement (identity owner).
            Matrix4x4 p0 = Owner(Vector3.zero, 0f);
            Matrix4x4 m0 = p0 * k_geometryLocalToOwner;
            var (_, aPlus, aMinus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            placements.of[aPlus] = p0 * k_geometryLocalToOwner;
            placements.of[aMinus] = p0 * k_geometryLocalToOwner;
            registrations.Clear();
            registrations.Add(Registration(root, m0));
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));

            VpMultiCutRenderFragment rf = Of(snapshot, aPlus);
            Same(m0, rf.geometryLocalToWorld, "t0 placement");
            Same(new Vector3(0f, 0.5f, 0f), rf.offset, "t0 separation");
            Same(new Vector3(0f, 1.5f, 0f), Drawn(in rf, Vector3.zero), "t0 drawn origin");

            // --- t1: A+ moves to (3,0,0) and turns 90 degrees about z.
            Matrix4x4 p1 = Owner(new Vector3(3f, 0f, 0f), 90f);
            placements.of[aPlus] = p1 * k_geometryLocalToOwner;
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            rf = Of(snapshot, aPlus);
            Same(new Vector3(2f, 0f, 0f), rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero), "t1 placement origin");
            Same(new Vector3(-0.5f, 0f, 0f), rf.offset, "t1 separation turns with the body");
            Same(new Vector3(1.5f, 0f, 0f), Drawn(in rf, Vector3.zero), "t1 drawn origin");

            // --- t2: B admitted on A+ and published. Its children inherit A+'s placement, not the registration's.
            var (_, bPlus, bMinus) = Cut(ledger, aPlus, new float4(1f, 0f, 0f, 0f));
            placements.of[bPlus] = p1 * k_geometryLocalToOwner;
            placements.of[bMinus] = p1 * k_geometryLocalToOwner;
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            rf = Of(snapshot, bPlus);
            Same(new Vector3(2f, 0f, 0f), rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero), "t2 placement origin");
            Same(new Vector3(-0.5f, 0.5f, 0f), rf.offset, "t2 separation is A's and B's");
            Same(new Vector3(1.5f, 0.5f, 0f), Drawn(in rf, Vector3.zero), "t2 drawn origin");

            // Taking the mapping from the ancestor's registration instead would put it back where A was published.
            Matrix4x4 reDerived = p1.inverse * m0;
            Same(new Vector3(0f, 1f, 0f), (p1 * reDerived).MultiplyPoint3x4(Vector3.zero), "the mistake this avoids");

            // --- t3: A committed for this branch. Only A's separation is taken in, in the frame that is followed:
            //     Translate(R^-1 * (-0.5,0,0)) = Translate(0, 0.5, 0) on top of the geometry's own frame.
            Matrix4x4 foldedA = Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f));
            Assert.That(ledger.TryGetOrigin(aPlus, out CutOperationId aCut, out float aSide), Is.True);
            var reflectedA = new[] { new VpClipBoundary(new VpCapFace(ledger, aCut), aSide) };
            registrations.Clear();
            registrations.Add(Registration(aPlus, m0, reflectedA, foldedA));
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            rf = Of(snapshot, bPlus);
            Same(new Vector3(1.5f, 0f, 0f), rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero), "t3 placement origin");
            Same(new Vector3(0f, 0.5f, 0f), rf.offset, "t3 keeps only B's separation");
            Same(new Vector3(1.5f, 0.5f, 0f), Drawn(in rf, Vector3.zero), "t3 does not move: nothing lost, nothing twice");

            // Several points, not only the origin.
            foreach (Vector3 local in new[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(-1f, 0.5f, 0.25f) })
            {
                Vector3 expected = (p1 * k_geometryLocalToOwner).MultiplyPoint3x4(local)
                                   + new Vector3(-0.5f, 0f, 0f) + new Vector3(0f, 0.5f, 0f);
                Same(expected, Drawn(in rf, local), "t3 point " + local);
            }

            // --- t4: the child turns again, to 180 degrees. What was folded in turns with it.
            Matrix4x4 p2 = Owner(new Vector3(3f, 0f, 0f), 180f);
            placements.of[bPlus] = p2 * k_geometryLocalToOwner;
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            rf = Of(snapshot, bPlus);
            Same(new Vector3(3f, -1.5f, 0f), rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero), "t4 placement origin");
            Same(new Vector3(-0.5f, 0f, 0f), rf.offset, "t4 B's separation turns too");
            Same(new Vector3(2.5f, -1.5f, 0f), Drawn(in rf, Vector3.zero), "t4 drawn origin");

            // --- t5: B committed. Its separation goes in the same way, and the drawn position does not move.
            Matrix4x4 foldedB = Matrix4x4.Translate(new Vector3(0.5f, 0f, 0f)) * foldedA;
            Assert.That(ledger.TryGetOrigin(bPlus, out CutOperationId bCut, out float bSide), Is.True);
            var reflectedAB = new[] { reflectedA[0], new VpClipBoundary(new VpCapFace(ledger, bCut), bSide) };
            registrations.Clear();
            registrations.Add(Registration(bPlus, m0, reflectedAB, foldedB));
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            rf = Of(snapshot, bPlus);
            Same(new Vector3(2.5f, -1.5f, 0f), rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero), "t5 placement origin");
            Same(Vector3.zero, rf.offset, "t5 has nothing temporary left");
            Same(new Vector3(2.5f, -1.5f, 0f), Drawn(in rf, Vector3.zero), "t5 does not move either");
            Assert.That(bMinus.IsSet && aMinus.IsSet, Is.True);
        }

        // ----- an aggregate drawn for an ignored boundary ---------------------------------------------------------------

        /// <summary>
        /// A chain of cuts down the positive side: the first on <paramref name="root"/>, the next on its positive
        /// child, and so on. The planes differ so that the separations do not all lie on top of one another; what
        /// matters here is how many boundaries a branch ends up with.
        /// </summary>
        private static LogicalFragmentId Chain(
            LogicalCutLedger ledger, LogicalFragmentId root, int cuts, List<LogicalFragmentId> negatives = null)
        {
            LogicalFragmentId at = root;
            for (int i = 0; i < cuts; i++)
            {
                var (_, positive, negative) = Cut(ledger, at, new float4(0f, 1f, 0f, -0.05f * i));
                negatives?.Add(negative);
                at = positive;
            }

            return at;
        }

        /// <summary>The one render fragment that is drawn for several branches at once.</summary>
        private static VpMultiCutRenderFragment Aggregate(VpMultiCutSnapshot snapshot)
        {
            var found = new List<VpMultiCutRenderFragment>();
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.aggregated)
                {
                    found.Add(rf);
                }
            }

            Assert.That(found.Count, Is.EqualTo(1), "exactly one aggregate is drawn");
            return found[0];
        }

        /// <summary>
        /// Nine published boundaries on one branch: the ninth is past the capacity, so it is Ignored and the branches
        /// behind it are drawn once. The aggregate stands where its first living branch stands -- its root has been
        /// replaced by that ninth cut and stands nowhere -- and moving the branch that is not first moves nothing.
        /// </summary>
        [Test]
        public void AnAggregate_StandsWhereItsFirstLivingBranchDoes()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId last = Chain(ledger, root, VpClipCandidates.Capacity);
            var (ignored, first, second) = Cut(ledger, last, new float4(1f, 0f, 0f, 0f));

            Matrix4x4 mFirst = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
            Matrix4x4 mSecond = Owner(new Vector3(-2f, 1f, 0f), 0f) * k_geometryLocalToOwner;
            var placements = new Placements();
            placements.of[first] = mFirst;
            placements.of[second] = mSecond;

            var registrations = new List<VpMultiCutRegistration> { Registration(root, k_geometryLocalToOwner) };
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));

            VpMultiCutRenderFragment rf = Aggregate(snapshot);
            Assert.That(rf.root, Is.EqualTo(last), "the aggregate is still rooted at the source of the ignored cut");
            Assert.That(
                ledger.TryGetFragmentState(last, out LogicalFragmentState state) && state == LogicalFragmentState.Replaced,
                Is.True,
                "which that cut replaced");
            Assert.That(placements.asked.Contains(last), Is.False, "and nothing asked where the replaced root stands");
            Same(mFirst, rf.geometryLocalToWorld, "the aggregate stands where its first living branch does");
            Assert.That(placements.asked.Contains(first), Is.True, "which is the branch that was asked about");

            // The branch that is not the first moves: nothing of the aggregate moves with it.
            placements.of[second] = Owner(new Vector3(7f, -4f, 2f), 45f) * k_geometryLocalToOwner;
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Same(mFirst, Aggregate(snapshot).geometryLocalToWorld, "the aggregate did not move with it");

            // The first branch moves and turns: the aggregate goes with it.
            Matrix4x4 moved = Owner(new Vector3(-5f, 2f, 1f), 120f) * k_geometryLocalToOwner;
            placements.of[first] = moved;
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Same(moved, Aggregate(snapshot).geometryLocalToWorld, "and follows the one it does stand at");
            Assert.That(ignored.IsSet, Is.True);
        }

        /// <summary>
        /// The aggregate is the shape before the ignored boundary cut it: at the same placement, its clip, its
        /// separation and its caps are what that shape would be drawn with on its own, and the ignored boundary adds
        /// none of them.
        /// </summary>
        [Test]
        public void AnAggregate_IsDrawnAsTheShapeBeforeTheIgnoredBoundary()
        {
            Matrix4x4 stands = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;

            // The control: the same chain, with nothing ignored, drawn at the same placement.
            LogicalCutLedger control = NewLedger();
            LogicalFragmentId controlRoot = control.AddFragment();
            LogicalFragmentId controlLeaf = Chain(control, controlRoot, VpClipCandidates.Capacity);
            var controlPlacements = new Placements();
            controlPlacements.of[controlLeaf] = stands;
            var controlRegistrations = new List<VpMultiCutRegistration> { Registration(controlRoot, k_geometryLocalToOwner) };
            VpMultiCutSnapshot controlSnapshot = NewSnapshot();
            Assert.That(
                controlSnapshot.TryBuild(control, controlRegistrations, Separation, controlPlacements),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutRenderFragment expected = Of(controlSnapshot, controlLeaf);
            Assert.That(expected.aggregated, Is.False, "nothing is ignored in the control");

            // The same chain with one more cut, which is past the capacity.
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId last = Chain(ledger, root, VpClipCandidates.Capacity);
            var (_, first, second) = Cut(ledger, last, new float4(1f, 0f, 0f, 0f));
            var placements = new Placements();
            placements.of[first] = stands;
            placements.of[second] = Owner(new Vector3(-2f, 1f, 0f), 0f) * k_geometryLocalToOwner;
            var registrations = new List<VpMultiCutRegistration> { Registration(root, k_geometryLocalToOwner) };
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));

            VpMultiCutRenderFragment rf = Aggregate(snapshot);
            Same(expected.geometryLocalToWorld, rf.geometryLocalToWorld, "the same placement");
            Assert.That(
                rf.clip.PlaneCount, Is.EqualTo(expected.clip.PlaneCount),
                "clipped by the selected boundaries only, and the ignored one adds no plane");
            Assert.That(rf.clip.PlaneCount, Is.EqualTo(VpClipCandidates.Capacity), "which is the full capacity here");
            Same(expected.offset, rf.offset, "the same separation: the ignored boundary adds none");
            Assert.That(rf.capCount, Is.EqualTo(expected.capCount), "and the same caps: the ignored boundary makes none");
        }

        /// <summary>
        /// The first living branch is cut again and published. The branch that is first in the walk is then its
        /// positive child, and a publication puts both children where their source was, so what is drawn does not
        /// move for the change of branch.
        /// </summary>
        [Test]
        public void WhenTheFirstBranchIsCutAgain_ItsChildIsAskedAndNothingMoves()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId last = Chain(ledger, root, VpClipCandidates.Capacity);
            var (_, first, second) = Cut(ledger, last, new float4(1f, 0f, 0f, 0f));

            Matrix4x4 stands = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
            var placements = new Placements();
            placements.of[first] = stands;
            placements.of[second] = Owner(new Vector3(-2f, 1f, 0f), 0f) * k_geometryLocalToOwner;

            var registrations = new List<VpMultiCutRegistration> { Registration(root, k_geometryLocalToOwner) };
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Matrix4x4 before = Aggregate(snapshot).geometryLocalToWorld;

            // Cut again and published, with both children where their source was.
            var (_, deeper, deeperOther) = Cut(ledger, first, new float4(0f, 0f, 1f, 0f));
            placements.of.Remove(first);
            placements.of[deeper] = stands;
            placements.of[deeperOther] = stands;
            placements.asked.Clear();

            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutRenderFragment rf = Aggregate(snapshot);
            Assert.That(rf.root, Is.EqualTo(last), "the aggregate is rooted where it was");
            Assert.That(placements.asked.Contains(deeper), Is.True, "the child is the branch that is asked about now");
            Same(before, rf.geometryLocalToWorld, "and the base placement is kept across the change of branch");

            // It is really following the child now: moving it moves the aggregate.
            Matrix4x4 moved = Owner(new Vector3(1f, -3f, 0f), 15f) * k_geometryLocalToOwner;
            placements.of[deeper] = moved;
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Same(moved, Aggregate(snapshot).geometryLocalToWorld, "the aggregate follows the child");
        }

        /// <summary>
        /// Nothing else changes. A branch with nothing ignored is drawn where its own fragment stands, a registration
        /// with no lookup draws everything at its own placement, and the two stops are the stops they were.
        /// </summary>
        [Test]
        public void TheOrdinaryBranchesTheStaticPathAndTheStops_AreUnchanged()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var negatives = new List<LogicalFragmentId>();
            LogicalFragmentId last = Chain(ledger, root, VpClipCandidates.Capacity, negatives);
            var (_, first, second) = Cut(ledger, last, new float4(1f, 0f, 0f, 0f));

            Matrix4x4 stands = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
            Matrix4x4 sideways = Owner(new Vector3(0f, 5f, 0f), 30f) * k_geometryLocalToOwner;
            var placements = new Placements();
            placements.of[first] = stands;
            placements.of[second] = Owner(new Vector3(-2f, 1f, 0f), 0f) * k_geometryLocalToOwner;
            placements.of[negatives[0]] = sideways;

            var registrations = new List<VpMultiCutRegistration> { Registration(root, k_geometryLocalToOwner) };
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));

            // An ordinary branch: nothing of it is ignored, and it stands where its own fragment does.
            VpMultiCutRenderFragment ordinary = Of(snapshot, negatives[0]);
            Assert.That(ordinary.aggregated, Is.False, "it has nothing ignored");
            Same(sideways, ordinary.geometryLocalToWorld, "and is drawn where its own fragment stands");

            // A branch nobody placed is drawn where it was registered, as before.
            Same(
                k_geometryLocalToOwner, Of(snapshot, negatives[1]).geometryLocalToWorld,
                "an arrangement that follows nothing is drawn at the registration");

            // No lookup at all: everything, aggregate included, at the registration's placement.
            VpMultiCutSnapshot without = NewSnapshot();
            Assert.That(without.TryBuild(ledger, registrations, Separation), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Same(k_geometryLocalToOwner, Aggregate(without).geometryLocalToWorld, "the static path is untouched");

            // A living branch that follows something with nowhere given is still refused, not drawn where it was.
            var missing = new Placements { unknown = VpFragmentPlacementKind.Missing };
            missing.of[second] = placements.of[second];
            VpMultiCutSnapshot refused = NewSnapshot();
            Assert.That(
                refused.TryBuild(ledger, registrations, Separation, missing), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput),
                "a missing placement for the branch that is asked about still refuses");

            // And a retirement inside what would be drawn once is still its own stop.
            Assert.That(ledger.Retire(second), Is.True, "one branch of the aggregate retires");
            VpMultiCutSnapshot retired = NewSnapshot();
            Assert.That(
                retired.TryBuild(ledger, registrations, Separation, placements),
                Is.EqualTo(VpMultiCutBuildOutcome.RetiredInsideAggregate),
                "which is the stop it was, and is not changed here");
        }

        // ----- what the lookup is and is not asked ----------------------------------------------------------------------

        /// <summary>
        /// Without a lookup, every render fragment is drawn at its registration's placement, exactly as before. This is
        /// the ordinary answer for a display whose shapes move together, not a fallback for a failure.
        /// </summary>
        [Test]
        public void WithoutALookup_EverythingIsDrawnAtTheRegistrationsPlacement()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, positive, negative) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            Matrix4x4 m0 = Owner(new Vector3(1f, 2f, 3f), 30f) * k_geometryLocalToOwner;

            VpMultiCutSnapshot with = NewSnapshot();
            VpMultiCutSnapshot without = NewSnapshot();
            var empty = new Placements();
            Assert.That(
                with.TryBuild(ledger, root, k_box, m0, k_lineageToGeometryLocal, k_none, Separation, 1e-4f, empty),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(
                without.TryBuild(ledger, root, k_box, m0, k_lineageToGeometryLocal, k_none, Separation, 1e-4f),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));

            Assert.That(with.RenderFragmentCount, Is.EqualTo(without.RenderFragmentCount));
            foreach (LogicalFragmentId side in new[] { positive, negative })
            {
                VpMultiCutRenderFragment a = Of(with, side);
                VpMultiCutRenderFragment b = Of(without, side);
                Same(m0, a.geometryLocalToWorld, "a lookup that knows nothing changes nothing");
                Same(b.geometryLocalToWorld, a.geometryLocalToWorld, "the same placement either way");
                Same(b.offset, a.offset, "the same separation either way");
            }

            Assert.That(empty.asked.Count, Is.GreaterThan(0), "it was asked, and said these follow nothing");
        }

        /// <summary>
        /// A fragment that is said to follow something, without where, is not drawn at the placement it was
        /// registered with: the whole input is refused instead. Saying that it follows nothing is a different answer
        /// and keeps it at that placement.
        /// </summary>
        [Test]
        public void AFollowingFragmentWithNoPlacement_IsRefusedRatherThanDrawnWhereItWas()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, positive, negative) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            Matrix4x4 m0 = Owner(new Vector3(5f, 0f, 0f), 0f) * k_geometryLocalToOwner;

            var missing = new Placements { unknown = VpFragmentPlacementKind.Missing };
            missing.of[positive] = Owner(new Vector3(9f, 0f, 0f), 0f) * k_geometryLocalToOwner;
            VpMultiCutSnapshot snapshot = NewSnapshot();
            var registrations = new List<VpMultiCutRegistration> { Registration(root, m0) };
            Assert.That(
                snapshot.TryBuild(ledger, registrations, Separation, missing), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput),
                "the side with no placement is not drawn at the registration's");
            Assert.That(snapshot.IsBuilt, Is.False, "and nothing of the snapshot is readable");

            // The same input, with that side said to follow nothing, is drawn where it was registered.
            var stated = new Placements();
            stated.of[positive] = missing.of[positive];
            stated.notFollowing.Add(negative);
            Assert.That(
                snapshot.TryBuild(ledger, registrations, Separation, stated), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Same(m0, Of(snapshot, negative).geometryLocalToWorld, "what follows nothing keeps the registration's placement");
        }

        /// <summary>
        /// A branch the ledger has retired is not drawn, and nothing asks where it stands. A cut of a child that has
        /// itself been cut keeps drawing the living descendants where they are.
        /// </summary>
        [Test]
        public void ARetiredBranch_IsNotDrawnAndIsNeverAskedAbout()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, aPlus, aMinus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (_, bPlus, bMinus) = Cut(ledger, aPlus, new float4(1f, 0f, 0f, 0f));

            Matrix4x4 m0 = k_geometryLocalToOwner;
            var placements = new Placements();
            placements.of[aMinus] = Owner(new Vector3(-3f, 0f, 0f), 0f) * k_geometryLocalToOwner;
            placements.of[bPlus] = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
            placements.of[bMinus] = Owner(new Vector3(0f, 4f, 0f), 45f) * k_geometryLocalToOwner;

            Assert.That(ledger.Retire(bMinus), Is.True, "one of the living descendants is retired");

            VpMultiCutSnapshot snapshot = NewSnapshot();
            var registrations = new List<VpMultiCutRegistration> { Registration(root, m0) };
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));

            Assert.That(Has(snapshot, bMinus), Is.False, "the retired branch is not drawn");
            Assert.That(placements.asked.Contains(bMinus), Is.False, "and nothing asked where it stands");

            // The living ones keep their own places: A+ is replaced by B, so what is drawn is B+ and A-.
            Assert.That(Has(snapshot, aPlus), Is.False, "a fragment that was itself cut is not a branch any more");
            Same(
                placements.of[bPlus].MultiplyPoint3x4(Vector3.zero), Of(snapshot, bPlus).geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero),
                "the living descendant stands where it stands");
            Same(
                placements.of[aMinus].MultiplyPoint3x4(Vector3.zero), Of(snapshot, aMinus).geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero),
                "and so does the other side");
        }

        /// <summary>
        /// A snapshot that has been built does not read the placements again: what one collection adopted stays as it
        /// was adopted, however much the fragments move afterwards.
        /// </summary>
        [Test]
        public void ABuiltSnapshot_DoesNotReadThePlacementsAgain()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, positive, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var placements = new Placements();
            placements.of[positive] = Owner(new Vector3(1f, 0f, 0f), 0f) * k_geometryLocalToOwner;

            VpMultiCutSnapshot snapshot = NewSnapshot();
            var registrations = new List<VpMultiCutRegistration> { Registration(root, k_geometryLocalToOwner) };
            Assert.That(snapshot.TryBuild(ledger, registrations, Separation, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Vector3 adopted = Of(snapshot, positive).geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero);
            int askedWhileBuilding = placements.asked.Count;

            placements.of[positive] = Owner(new Vector3(100f, 100f, 100f), 17f) * k_geometryLocalToOwner;
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment _), Is.True);
            }

            Same(adopted, Of(snapshot, positive).geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero), "what was adopted stands");
            Assert.That(placements.asked.Count, Is.EqualTo(askedWhileBuilding), "and nothing asked again");
        }
    }
}
