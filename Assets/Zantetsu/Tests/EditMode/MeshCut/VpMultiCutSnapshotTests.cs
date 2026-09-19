using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The multi-cut display snapshot on the CPU (D-180, D-181), on real ledgers. Expectations come from the layouts:
    /// a box [-1, 1]³ cut by planes chosen so that which half-spaces, which offsets and which caps are expected can be
    /// read off them, and the one-command pyramid of the display tests for the comparison with the current display.
    /// Changing the reflected set here is the test's own input; it is not a geometry commit.
    /// </summary>
    public class VpMultiCutSnapshotTests
    {
        internal const float Separation = 0.25f;
        private const float Tolerance = 1e-4f;

        internal static readonly Bounds k_box = new Bounds(Vector3.zero, Vector3.one * 2f);
        internal static readonly VpClipBoundary[] k_none = Array.Empty<VpClipBoundary>();

        private readonly List<Object> _objects = new List<Object>();
        private int _frame;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
        }

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        // ----- helpers -------------------------------------------------------------------------------------------------

        internal static LogicalCutLedger NewLedger() => new LogicalCutLedger(new LogicalCutIncompleteBudget(64));

        internal static VpMultiCutSnapshot NewSnapshot(int branches = 64, int candidates = 1024, int renderFragments = 64, int caps = 512, int chain = 64)
        {
            return new VpMultiCutSnapshot(new VpMultiCutCapacities(branches, candidates, renderFragments, caps, chain));
        }

        internal static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId source, float4 plane)
        {
            Assert.That(ledger.Admit(source, plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return cut;
        }

        internal static (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) Cut(
            LogicalCutLedger ledger, LogicalFragmentId source, float4 plane)
        {
            CutOperationId cut = Admit(ledger, source, plane);
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (cut, positive, negative);
        }

        internal static VpMultiCutBuildOutcome Build(
            VpMultiCutSnapshot snapshot, LogicalCutLedger ledger, LogicalFragmentId root, IReadOnlyCollection<VpClipBoundary> reflected = null,
            Matrix4x4? placement = null, Matrix4x4? mapping = null, Bounds? box = null)
        {
            Bounds bounds = box ?? k_box;
            return snapshot.TryBuild(
                ledger, root, bounds, placement ?? Matrix4x4.identity, mapping ?? Matrix4x4.identity, reflected ?? k_none,
                Separation, VpCapBoundsPolygon.EpsilonFor(bounds));
        }

        internal static VpClipBoundary B(LogicalCutLedger ledger, CutOperationId cut, float side) => new VpClipBoundary(new VpCapFace(ledger, cut), side);

        private static Vector3 N(float4 plane) => new Vector3(plane.x, plane.y, plane.z).normalized;

        internal static VpMultiCutRenderFragment RenderFragmentOf(VpMultiCutSnapshot snapshot, LogicalFragmentId fragment, float pendingSide = 0f)
        {
            for (int b = 0; b < snapshot.BranchCount; b++)
            {
                Assert.That(snapshot.TryGetBranch(b, out VpMultiCutBranch branch), Is.True);
                if (branch.fragment == fragment && branch.pendingSide == pendingSide)
                {
                    Assert.That(snapshot.TryGetRenderFragment(branch.renderFragment, out VpMultiCutRenderFragment rf), Is.True);
                    return rf;
                }
            }

            Assert.Fail("no branch for that fragment");
            return default;
        }

        private static VpClipBoundary[] SelectedOf(VpMultiCutSnapshot snapshot, in VpMultiCutRenderFragment rf)
        {
            var boundaries = new VpClipBoundary[rf.conditionCount];
            for (int i = 0; i < rf.conditionCount; i++)
            {
                Assert.That(snapshot.TryGetCondition(rf.conditionStart + i, out VpCapConstraint condition), Is.True);
                boundaries[i] = new VpClipBoundary(condition.face, condition.side);
            }

            return boundaries;
        }

        private static void AssertNear(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(Tolerance), what + ": " + actual + " against " + expected);
        }

        private static void AssertNear(Vector4 actual, Vector4 expected, string what)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(Tolerance), what + ": " + actual + " against " + expected);
        }

        /// <summary>
        /// Every cap of <paramref name="rf"/> lies on its own plane and inside every other selected half-space, once its
        /// separation is taken back off; its outward normal is <c>-side * n</c>.
        /// </summary>
        private static void AssertCapsInside(VpMultiCutSnapshot snapshot, in VpMultiCutRenderFragment rf, string what)
        {
            for (int c = 0; c < rf.capCount; c++)
            {
                Assert.That(snapshot.TryGetCap(rf.capStart + c, out VpMultiCutCap cap), Is.True);
                Assert.That(cap.initialVertexCount, Is.InRange(0, 6), what + ": the section has at most six vertices");
                Assert.That(cap.vertexCount, Is.InRange(0, 14), what + ": the clipped cap at most fourteen");
                Vector3 normal = new Vector3(cap.worldPlane.x, cap.worldPlane.y, cap.worldPlane.z);
                AssertNear(cap.outwardNormal, -cap.boundary.side * normal, what + ": outward normal");
                for (int v = 0; v < cap.vertexCount; v++)
                {
                    Assert.That(snapshot.TryGetCapVertex(rf.capStart + c, v, out Vector3 world), Is.True);
                    Vector3 point = world - rf.offset;
                    Assert.That(math.dot(cap.worldPlane.xyz, (float3)point) + cap.worldPlane.w, Is.EqualTo(0f).Within(Tolerance), what + ": on its plane");
                    for (int k = 0; k < rf.conditionCount; k++)
                    {
                        Assert.That(snapshot.TryGetCondition(rf.conditionStart + k, out VpCapConstraint other), Is.True);
                        float d = other.side * (Vector3.Dot(new Vector3(other.worldPlane.x, other.worldPlane.y, other.worldPlane.z), point) + other.worldPlane.w);
                        Assert.That(d, Is.GreaterThanOrEqualTo(-Tolerance), what + ": inside every selected half-space");
                    }
                }
            }
        }

        // ----- the settled distribution ----------------------------------------------------------------------------------

        /// <summary>
        /// The settled distribution is read after publication, completion and termination; not before preparation, not
        /// after an abort or a stale reclamation; a prepared distribution that gave both sides nothing is not
        /// "unprepared". The pending-time call keeps its own contract.
        /// </summary>
        [Test]
        public void TheSettledDistribution_IsReadAfterPublication_AndOnlyWhenPrepared()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
            Assert.That(ledger.Admit(root, new float4(0f, 1f, 0f, 0f), true, out CutOperationId a), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.TryGetSettledAnchorDistribution(a, out _), Is.False, "not prepared yet");
            Assert.That(ledger.PrepareAnchorDistribution(a, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.TryGetSettledAnchorDistribution(a, out AnchorDistributionResult pending), Is.True, "prepared, pending");
            Assert.That(pending.negativeCount, Is.EqualTo(1));
            Assert.That(ledger.Publish(a, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.TryGetPreparedAnchorDistribution(a, out _), Is.False, "the pending-time call is unchanged");
            Assert.That(ledger.TryGetSettledAnchorDistribution(a, out AnchorDistributionResult published), Is.True, "published");
            Assert.That(published.negativeCount, Is.EqualTo(1));
            Assert.That(published.positiveCount, Is.Zero);
            Assert.That(ledger.CompleteGeometry(a), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.TryGetSettledAnchorDistribution(a, out _), Is.True, "completed");

            CutOperationId none = Admit(ledger, positive, new float4(1f, 0f, 0f, 0f));
            Assert.That(ledger.TryGetSettledAnchorDistribution(none, out AnchorDistributionResult empty), Is.True, "prepared with nothing");
            Assert.That(empty.positiveCount + empty.negativeCount, Is.Zero, "both sides zero, and still settled");
            Assert.That(ledger.Publish(none, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Terminate(none), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.TryGetSettledAnchorDistribution(none, out _), Is.True, "terminated");

            CutOperationId aborted = Admit(ledger, negative, new float4(1f, 0f, 0f, 0f));
            Assert.That(ledger.Abort(aborted), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.TryGetSettledAnchorDistribution(aborted, out _), Is.False, "aborted");
            Assert.That(ledger.TryGetSettledAnchorDistribution(default, out _), Is.False, "unknown");
        }

        // ----- single cut against the current display -------------------------------------------------------------------

        /// <summary>
        /// One cut of the display tests' pyramid, placed off the origin and turned: before admission, pending and
        /// published, the snapshot's render fragments, clips, offsets and caps are the current display's own sides and
        /// cap records, in the same order.
        /// </summary>
        [Test]
        public void ASingleCut_IsTheCurrentDisplaysGeometry_BeforeWhilePendingAndAfter()
        {
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(0.7f, -0.3f, 2f), Quaternion.Euler(10f, 25f, 5f), Vector3.one);
            var pyramidBox = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f));
            using (var storage = new VpCpuGeometryStorage(4096, 16384, 32, 128, 128, Allocator.Persistent))
            {
                LogicalCutLedger ledger = NewLedger();
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                Assert.That(
                    VpLogicalCutDisplay.TryCreate(
                        storage, table, ledger, Materials(), null, null, 4, 8,
                        VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(4), () => _frame,
                        out VpLogicalCutDisplay display),
                    Is.True);
                using (new AfterTheFrame(() => _frame++, display))
                {
                    display.Separation = Separation;
                    LogicalFragmentId body = ledger.AddFragment(new List<float3> { new float3(0f, 0.2f, 0f) });
                    Assert.That(display.TryShow(body, AppendPyramid(storage), placement), Is.True);
                    VpMultiCutSnapshot snapshot = NewSnapshot();

                    Assert.That(display.TryBeginFrame(), Is.True);
                    AssertSameAsDisplay(snapshot, ledger, body, display, placement, pyramidBox, "before admission");

                    var plane = new float4(0f, 1f, 0f, -1f);
                    CutOperationId cut = Admit(ledger, body, plane);
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True);
                    AssertSameAsDisplay(snapshot, ledger, body, display, placement, pyramidBox, "pending");

                    Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True);
                    AssertSameAsDisplay(snapshot, ledger, body, display, placement, pyramidBox, "published");
                }
            }
        }

        private static void AssertSameAsDisplay(
            VpMultiCutSnapshot snapshot, LogicalCutLedger ledger, LogicalFragmentId body, VpLogicalCutDisplay display,
            Matrix4x4 placement, Bounds box, string what)
        {
            Assert.That(Build(snapshot, ledger, body, placement: placement, box: box), Is.EqualTo(VpMultiCutBuildOutcome.Built), what);
            Assert.That(snapshot.RenderFragmentCount, Is.EqualTo(display.SideCount), what + ": one render fragment per drawn side");
            for (int i = 0; i < display.SideCount; i++)
            {
                Assert.That(display.TryGetSide(i, out LogicalCutDisplaySide side), Is.True);
                Assert.That(snapshot.TryGetRenderFragment(i, out VpMultiCutRenderFragment rf), Is.True);
                Assert.That(rf.clip.PlaneCount, Is.EqualTo(side.clip.PlaneCount), what + ": plane count " + i);
                AssertNear(rf.offset, side.offset, what + ": offset " + i);
                AssertNear(rf.clip.Offset, side.clip.Offset, what + ": clip offset " + i);
                for (int p = 0; p < side.clip.PlaneCount; p++)
                {
                    AssertNear(rf.clip.SignedPlane(p), side.clip.SignedPlane(p), what + ": signed plane " + i);
                }
            }

            Assert.That(snapshot.CapCount, Is.EqualTo(display.CapRecordCount), what + ": one cap per side");
            for (int c = 0; c < display.CapRecordCount; c++)
            {
                Assert.That(display.TryGetCapRecord(c, out LogicalCutCapRecord record), Is.True);
                Assert.That(snapshot.TryGetCap(c, out VpMultiCutCap cap), Is.True);
                Assert.That(cap.boundary.side, Is.EqualTo(record.side), what + ": side");
                Assert.That(cap.boundary.face.operation, Is.EqualTo(record.operation), what + ": face");
                AssertNear(new Vector4(cap.worldPlane.x, cap.worldPlane.y, cap.worldPlane.z, cap.worldPlane.w), record.worldPlane, what + ": plane");
                AssertNear(cap.outwardNormal, record.outwardNormal, what + ": normal");
                Assert.That(cap.vertexCount, Is.EqualTo(record.vertexCount), what + ": vertex count");
                for (int v = 0; v < record.vertexCount; v++)
                {
                    Assert.That(display.TryGetCapVertex(c, v, out Vector3 expected), Is.True);
                    Assert.That(snapshot.TryGetCapVertex(c, v, out Vector3 actual), Is.True);
                    AssertNear(actual, expected, what + ": vertex " + v + " of cap " + c);
                }
            }
        }

        // ----- several cuts -------------------------------------------------------------------------------------------

        /// <summary>
        /// Four cuts down one lineage -- A (y = 0.5, the anchor below, so A's negative side is fixed), B (x = 0) on A+,
        /// C (z = 0.3) on B+, D (x + y = 0.8) on C-: every leaf is its own render fragment with its chain
        /// of boundaries and sides, its offset is the sum of the free sides, and every cap lies inside the others.
        /// </summary>
        [Test]
        public void FourCuts_GiveEachLeafItsChain_ItsOffset_AndItsCaps()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
            var pa = new float4(0f, 1f, 0f, -0.5f);
            var pb = new float4(1f, 0f, 0f, 0f);
            var pc = new float4(0f, 0f, 1f, -0.3f);
            var pd = new float4(1f, 1f, 0f, -0.8f);
            var (a, aPlus, aMinus) = Cut(ledger, root, pa);
            var (b, bPlus, bMinus) = Cut(ledger, aPlus, pb);
            var (c, cPlus, cMinus) = Cut(ledger, bPlus, pc);
            var (d, dPlus, dMinus) = Cut(ledger, cMinus, pd);

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(snapshot.BranchCount, Is.EqualTo(5), "C+, D+, D-, B-, A-");
            Assert.That(snapshot.RenderFragmentCount, Is.EqualTo(5), "nothing is Ignored, so nothing is aggregated");

            var expected = new (LogicalFragmentId leaf, VpClipBoundary[] chain, Vector3 offset)[]
            {
                (cPlus, new[] { B(ledger, a, 1f), B(ledger, b, 1f), B(ledger, c, 1f) }, (N(pa) + N(pb) + N(pc)) * Separation),
                (dPlus, new[] { B(ledger, a, 1f), B(ledger, b, 1f), B(ledger, c, -1f), B(ledger, d, 1f) }, (N(pa) + N(pb) - N(pc) + N(pd)) * Separation),
                (dMinus, new[] { B(ledger, a, 1f), B(ledger, b, 1f), B(ledger, c, -1f), B(ledger, d, -1f) }, (N(pa) + N(pb) - N(pc) - N(pd)) * Separation),
                (bMinus, new[] { B(ledger, a, 1f), B(ledger, b, -1f) }, (N(pa) - N(pb)) * Separation),
                (aMinus, new[] { B(ledger, a, -1f) }, Vector3.zero),
            };

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(snapshot.TryGetBranch(i, out VpMultiCutBranch branch), Is.True);
                Assert.That(branch.fragment, Is.EqualTo(expected[i].leaf), "leaf " + i + ", positive children first");
                Assert.That(branch.candidateCount, Is.EqualTo(expected[i].chain.Length));
                Assert.That(branch.selectedCount, Is.EqualTo(expected[i].chain.Length));
                VpMultiCutRenderFragment rf = RenderFragmentOf(snapshot, expected[i].leaf);
                Assert.That(rf.aggregated, Is.False);
                Assert.That(SelectedOf(snapshot, rf), Is.EqualTo(expected[i].chain), "leaf " + i + ": the chain and its sides, ancestors first");
                AssertNear(rf.offset, expected[i].offset, "leaf " + i + ": the free sides summed; A's negative side is fixed");
                Assert.That(rf.clip.PlaneCount, Is.EqualTo(expected[i].chain.Length));
                AssertNear(rf.clip.Offset, rf.offset, "leaf " + i + ": the clip carries the same offset");
                Assert.That(rf.capCount, Is.EqualTo(expected[i].chain.Length), "leaf " + i + ": one cap per boundary");
                AssertCapsInside(snapshot, rf, "leaf " + i);
            }
        }

        /// <summary>
        /// B pending on A+ and then published: the two pending sides and the two published children have the same
        /// boundaries, planes, clips, offsets and cap vertices, exactly. No child id is issued to the pending sides.
        /// </summary>
        [Test]
        public void PendingToPublished_ChangesNothingDrawn()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
            var (_, aPlus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, -0.5f));
            int fragmentsBefore = ledger.FragmentCount;
            CutOperationId b = Admit(ledger, aPlus, new float4(1f, 0f, 0.2f, -0.1f));

            VpMultiCutSnapshot pending = NewSnapshot();
            Assert.That(Build(pending, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no id is issued for the pending sides");
            VpMultiCutRenderFragment pendingPlus = RenderFragmentOf(pending, aPlus, 1f);
            VpMultiCutRenderFragment pendingMinus = RenderFragmentOf(pending, aPlus, -1f);

            Assert.That(ledger.Publish(b, out LogicalFragmentId bPlus, out LogicalFragmentId bMinus), Is.EqualTo(LogicalCutResultOutcome.Applied));
            VpMultiCutSnapshot published = NewSnapshot();
            Assert.That(Build(published, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(published.RenderFragmentCount, Is.EqualTo(pending.RenderFragmentCount), "no render fragment added or doubled");
            Assert.That(published.CapCount, Is.EqualTo(pending.CapCount), "no cap added or doubled");

            AssertSame(pending, pendingPlus, published, RenderFragmentOf(published, bPlus), "the positive side");
            AssertSame(pending, pendingMinus, published, RenderFragmentOf(published, bMinus), "the negative side");
        }

        private static void AssertSame(
            VpMultiCutSnapshot a, in VpMultiCutRenderFragment ra, VpMultiCutSnapshot b, in VpMultiCutRenderFragment rb, string what)
        {
            Assert.That(rb.offset, Is.EqualTo(ra.offset), what + ": offset, exactly");
            Assert.That(rb.clip, Is.EqualTo(ra.clip), what + ": clip record, exactly");
            Assert.That(SelectedOf(b, rb), Is.EqualTo(SelectedOf(a, ra)), what + ": boundaries and sides");
            Assert.That(rb.capCount, Is.EqualTo(ra.capCount));
            for (int c = 0; c < ra.capCount; c++)
            {
                Assert.That(a.TryGetCap(ra.capStart + c, out VpMultiCutCap capA), Is.True);
                Assert.That(b.TryGetCap(rb.capStart + c, out VpMultiCutCap capB), Is.True);
                Assert.That(capB.boundary, Is.EqualTo(capA.boundary), what);
                Assert.That(capB.worldPlane, Is.EqualTo(capA.worldPlane), what + ": plane, exactly");
                Assert.That(capB.vertexCount, Is.EqualTo(capA.vertexCount), what);
                for (int v = 0; v < capA.vertexCount; v++)
                {
                    a.TryGetCapVertex(ra.capStart + c, v, out Vector3 va);
                    b.TryGetCapVertex(rb.capStart + c, v, out Vector3 vb);
                    Assert.That(vb, Is.EqualTo(va), what + ": vertex, exactly");
                }
            }
        }

        /// <summary>
        /// The fixed side is taken from each cut's own settled distribution. A- has the anchor; cut by E (x = 0) with the
        /// anchor on E's negative side, E- is fixed and E+ free. A- is then replaced and holds no anchor, and E+ is later
        /// retired by an abort: the offsets of what is still drawn do not move, and E+ is drawn as nothing.
        /// </summary>
        [Test]
        public void TheOffsets_ComeFromEachCutsOwnDistribution_AndDoNotMoveWhenOwnersGo()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.8f, 0f) });
            var pa = new float4(0f, 1f, 0f, -0.5f);
            var pe = new float4(1f, 0f, 0f, 0f);
            var (_, aPlus, aMinus) = Cut(ledger, root, pa);
            var (_, ePlus, eMinus) = Cut(ledger, aMinus, pe);
            Assert.That(ledger.IsFixedOwner(aMinus), Is.False, "the layout: A- let its anchor go when it was replaced");

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            AssertNear(RenderFragmentOf(snapshot, aPlus).offset, N(pa) * Separation, "A+ is free");
            AssertNear(RenderFragmentOf(snapshot, eMinus).offset, Vector3.zero, "A- and E- were fixed when they were cut");
            AssertNear(RenderFragmentOf(snapshot, ePlus).offset, N(pe) * Separation, "E+ free; A- still counted fixed");

            CutOperationId f = Admit(ledger, ePlus, new float4(0f, 0f, 1f, 0f));
            Assert.That(ledger.Abort(f), Is.EqualTo(LogicalCutResultOutcome.Applied));
            VpMultiCutSnapshot after = NewSnapshot();
            Assert.That(Build(after, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(after.BranchCount, Is.EqualTo(2), "E+ retired: drawn as nothing, not brought back");
            AssertNear(RenderFragmentOf(after, eMinus).offset, Vector3.zero, "E-'s offset has not moved");
            AssertNear(RenderFragmentOf(after, aPlus).offset, N(pa) * Separation, "nor A+'s");
        }

        // ----- beyond eight ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Nine cuts down the positive children (y = -0.8, -0.6, ... all free): L9+ and L9- share the first eight
        /// boundaries and differ only in the Ignored ninth, so they are one render fragment, drawn once as L8 -- eight
        /// planes, eight caps, no ninth clip, cap or separation -- while each keeps its own nine candidates and their
        /// states. A tenth cut on L9+ joins the same render fragment. A ninth still pending aggregates the same way.
        /// </summary>
        [Test]
        public void NineCuts_AreDrawnOnceAsTheShapeBeforeTheNinth()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var cuts = new List<CutOperationId>();
            var planes = new List<float4>();
            var minus = new List<LogicalFragmentId>();
            LogicalFragmentId at = root;
            for (int k = 0; k < 8; k++)
            {
                var plane = new float4(0f, 1f, 0f, 0.8f - (0.2f * k));
                var (cut, plus, neg) = Cut(ledger, at, plane);
                cuts.Add(cut);
                planes.Add(plane);
                minus.Add(neg);
                at = plus;
            }

            LogicalFragmentId l8 = at;
            var ninth = new float4(1f, 0f, 0f, -0.1f);
            CutOperationId c9 = Admit(ledger, l8, ninth);

            VpMultiCutSnapshot pending = NewSnapshot();
            Assert.That(Build(pending, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutRenderFragment pendingRf = RenderFragmentOf(pending, l8, 1f);
            Assert.That(pendingRf.aggregated, Is.True, "a ninth still pending: aggregated");
            Assert.That(pendingRf.root, Is.EqualTo(l8));
            Assert.That(pendingRf.rootPendingSide, Is.Zero, "drawn whole, not as a pending side");
            Assert.That(pendingRf.branchCount, Is.EqualTo(2), "both pending sides");

            Assert.That(ledger.Publish(c9, out LogicalFragmentId l9Plus, out LogicalFragmentId l9Minus), Is.EqualTo(LogicalCutResultOutcome.Applied));
            VpMultiCutSnapshot published = NewSnapshot();
            Assert.That(Build(published, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(published.BranchCount, Is.EqualTo(10), "L9+, L9-, and the eight negatives");
            Assert.That(published.RenderFragmentCount, Is.EqualTo(9), "L9+ and L9- are drawn once");
            VpMultiCutRenderFragment rf = RenderFragmentOf(published, l9Plus);
            Assert.That(RenderFragmentOf(published, l9Minus).capStart, Is.EqualTo(rf.capStart), "the same render fragment");
            Assert.That(rf.aggregated, Is.True);
            Assert.That(rf.root, Is.EqualTo(l8), "the shape before the ninth: L8 whole");
            Assert.That(rf.branchCount, Is.EqualTo(2));
            Assert.That(rf.clip.PlaneCount, Is.EqualTo(8), "eight planes, the ninth not clipped");
            Assert.That(rf.capCount, Is.EqualTo(8), "eight caps");
            Vector3 eight = Vector3.zero;
            foreach (float4 p in planes)
            {
                eight += N(p) * Separation;
            }

            AssertNear(rf.offset, eight, "the eight free sides, and no ninth separation");
            AssertNear(pendingRf.offset, rf.offset, "the same while the ninth was pending");
            for (int c = 0; c < published.CapCount; c++)
            {
                Assert.That(published.TryGetCap(c, out VpMultiCutCap cap), Is.True);
                Assert.That(cap.boundary.face.operation, Is.Not.EqualTo(c9), "no cap of the Ignored ninth anywhere");
            }

            for (int i = 0; i < rf.branchCount; i++)
            {
                Assert.That(published.TryGetBranch(rf.branchStart + i, out VpMultiCutBranch branch), Is.True);
                Assert.That(branch.candidateCount, Is.EqualTo(9), "each branch keeps all nine candidates");
                Assert.That(branch.selectedCount, Is.EqualTo(8));
                Assert.That(published.TryGetCandidate(branch.candidateStart + 8, out VpClipCandidate last, out VpClipSelectionState state), Is.True);
                Assert.That(state, Is.EqualTo(VpClipSelectionState.IgnoredCapacity));
                Assert.That(last.boundary, Is.EqualTo(B(ledger, c9, branch.fragment == l9Plus ? 1f : -1f)), "and its own side of the ninth");
            }

            AssertCapsInside(published, rf, "L8");

            // A tenth, on L9+: its sides join the same render fragment.
            var (_, _, _) = Cut(ledger, l9Plus, new float4(0f, 0f, 1f, 0f));
            VpMultiCutSnapshot tenth = NewSnapshot();
            Assert.That(Build(tenth, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(tenth.RenderFragmentCount, Is.EqualTo(9));
            Assert.That(RenderFragmentOf(tenth, l9Minus).branchCount, Is.EqualTo(3), "L10+, L10- and L9-");
        }

        /// <summary>
        /// The same nine cuts with the first boundary given as reflected: now every branch has at most eight candidates,
        /// so L9+ and L9- are drawn apart, each with the ninth plane, the ninth cap and the ninth separation -- and the
        /// first boundary is neither clipped nor capped. Changing the reflected set is this test's input, not a commit.
        /// </summary>
        [Test]
        public void AReflectedAncestor_LetsTheNinthIn_AndTheRenderFragmentsAreRebuilt()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var planes = new List<float4>();
            var cuts = new List<CutOperationId>();
            LogicalFragmentId at = root;
            for (int k = 0; k < 8; k++)
            {
                var plane = new float4(0f, 1f, 0f, 0.8f - (0.2f * k));
                var (cut, upper, _) = Cut(ledger, at, plane);
                planes.Add(plane);
                cuts.Add(cut);
                at = upper;
            }

            var ninth = new float4(1f, 0f, 0f, -0.1f);
            var (c9, l9Plus, l9Minus) = Cut(ledger, at, ninth);
            var reflected = new[] { B(ledger, cuts[0], 1f) };

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(Build(snapshot, ledger, root, reflected), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutRenderFragment plus = RenderFragmentOf(snapshot, l9Plus);
            VpMultiCutRenderFragment minusRf = RenderFragmentOf(snapshot, l9Minus);
            Assert.That(plus.capStart, Is.Not.EqualTo(minusRf.capStart), "drawn apart now");
            Assert.That(plus.aggregated || minusRf.aggregated, Is.False);
            Assert.That(plus.clip.PlaneCount, Is.EqualTo(8), "the second to the ninth");
            Assert.That(SelectedOf(snapshot, plus)[7], Is.EqualTo(B(ledger, c9, 1f)), "the ninth is selected");
            Assert.That(Array.IndexOf(SelectedOf(snapshot, plus), B(ledger, cuts[0], 1f)), Is.EqualTo(-1), "the reflected first is not a condition");
            Vector3 lineage = Vector3.zero;
            foreach (float4 p in planes)
            {
                lineage += N(p) * Separation;
            }

            AssertNear(plus.offset, lineage + (N(ninth) * Separation), "the lineage and now the ninth's separation");
            AssertNear(minusRf.offset, lineage - (N(ninth) * Separation), "and on the other side");
            AssertCapsInside(snapshot, plus, "L9+");
            AssertCapsInside(snapshot, minusRf, "L9-");
        }

        // ----- spaces ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// A rigid mapping from the lineage's frame to the geometry's, and a rigid placement: the snapshot built over
        /// planes and anchors given in the lineage's frame is the snapshot built over the same cut given already in the
        /// geometry's frame with an identity mapping -- world planes, half-spaces, polygons and offsets alike.
        /// </summary>
        [Test]
        public void ARigidMapping_GivesTheSameWorldResult_AsPlanesGivenInTheGeometrysFrame()
        {
            Matrix4x4 mapping = Matrix4x4.TRS(new Vector3(0.3f, -0.2f, 0.1f), Quaternion.Euler(30f, -20f, 45f), Vector3.one);
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(-2f, 1f, 4f), Quaternion.Euler(-15f, 60f, 10f), Vector3.one);
            float3 anchor = new float3(0.1f, -0.7f, 0.2f);
            var pa = new float4(0.2f, 1f, -0.1f, -0.3f);
            var pb = new float4(1f, 0.1f, 0.3f, 0.05f);

            LogicalCutLedger lineage = NewLedger();
            LogicalFragmentId lineageRoot = lineage.AddFragment(new List<float3> { anchor });
            var (_, lineagePlus, _) = Cut(lineage, lineageRoot, pa);
            Cut(lineage, lineagePlus, pb);

            LogicalCutLedger geometry = NewLedger();
            LogicalFragmentId geometryRoot = geometry.AddFragment(new List<float3> { (float3)mapping.MultiplyPoint3x4(anchor) });
            Assert.That(VpCutPlane.TryGeometryLocalToWorld(pa, mapping, out float4 qa), Is.True);
            Assert.That(VpCutPlane.TryGeometryLocalToWorld(pb, mapping, out float4 qb), Is.True);
            var (_, geometryPlus, _) = Cut(geometry, geometryRoot, qa);
            Cut(geometry, geometryPlus, qb);

            VpMultiCutSnapshot mapped = NewSnapshot();
            VpMultiCutSnapshot direct = NewSnapshot();
            Assert.That(Build(mapped, lineage, lineageRoot, placement: placement, mapping: mapping), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(Build(direct, geometry, geometryRoot, placement: placement), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(mapped.RenderFragmentCount, Is.EqualTo(direct.RenderFragmentCount));
            Assert.That(mapped.CapCount, Is.EqualTo(direct.CapCount));
            for (int r = 0; r < mapped.RenderFragmentCount; r++)
            {
                mapped.TryGetRenderFragment(r, out VpMultiCutRenderFragment m);
                direct.TryGetRenderFragment(r, out VpMultiCutRenderFragment d);
                AssertNear(m.offset, d.offset, "offset " + r);
                Assert.That(m.clip.PlaneCount, Is.EqualTo(d.clip.PlaneCount));
                for (int p = 0; p < m.clip.PlaneCount; p++)
                {
                    AssertNear(m.clip.SignedPlane(p), d.clip.SignedPlane(p), "half-space " + p + " of " + r);
                }

                AssertCapsInside(mapped, m, "mapped " + r);
            }

            for (int c = 0; c < mapped.CapCount; c++)
            {
                mapped.TryGetCap(c, out VpMultiCutCap m);
                direct.TryGetCap(c, out VpMultiCutCap d);
                AssertNear(new Vector4(m.worldPlane.x, m.worldPlane.y, m.worldPlane.z, m.worldPlane.w), new Vector4(d.worldPlane.x, d.worldPlane.y, d.worldPlane.z, d.worldPlane.w), "cap plane " + c);
                Assert.That(m.vertexCount, Is.EqualTo(d.vertexCount), "cap " + c);
                for (int v = 0; v < m.vertexCount; v++)
                {
                    mapped.TryGetCapVertex(c, v, out Vector3 vm);
                    direct.TryGetCapVertex(c, v, out Vector3 vd);
                    AssertNear(vm, vd, "cap " + c + " vertex " + v);
                }
            }
        }

        // ----- empty, room, input -----------------------------------------------------------------------------------------

        /// <summary>
        /// A (y = 0) and then B (y = -0.5) on A+: B's section lies below A+, so B's cap is cut to nothing by A+ -- four
        /// vertices before, none after, a normal result that keeps its record. A plane missing the box gives a section of
        /// nothing.
        /// </summary>
        [Test]
        public void ACapCutToNothing_IsKeptAsAnEmptyCap()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, aPlus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (b, bPlus, _) = Cut(ledger, aPlus, new float4(0f, 1f, 0f, 0.5f));
            var (miss, _, _) = Cut(ledger, bPlus, new float4(0f, 1f, 0f, -5f));

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            bool sawCutAway = false;
            bool sawMissing = false;
            for (int c = 0; c < snapshot.CapCount; c++)
            {
                snapshot.TryGetCap(c, out VpMultiCutCap cap);
                if (cap.boundary.face.operation == b && cap.boundary.side > 0f && cap.initialVertexCount == 4)
                {
                    sawCutAway |= cap.vertexCount == 0;
                }

                if (cap.boundary.face.operation == miss)
                {
                    Assert.That(cap.initialVertexCount, Is.Zero, "a plane missing the box has no section");
                    Assert.That(cap.vertexCount, Is.Zero);
                    sawMissing = true;
                }
            }

            Assert.That(sawCutAway, Is.True, "B's cap on B+ is four vertices before and nothing after");
            Assert.That(sawMissing, Is.True, "the missing plane's caps are kept, empty");
        }

        /// <summary>
        /// Too little room of any kind is refused with nothing readable; the snapshot adopted before is untouched, and so
        /// are the ledger and the reflected set. The candidate room is not cut at eight. Null reflected is refused, and a
        /// mapping that cannot carry a plane is invalid input.
        /// </summary>
        [Test]
        public void TooLittleRoom_OrBadInput_LeavesTheAdoptedSnapshotTheLedgerAndTheInputs()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
            LogicalFragmentId at = root;
            for (int k = 0; k < 9; k++)
            {
                at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.15f * k))).positive;
            }

            VpMultiCutSnapshot adopted = NewSnapshot();
            Assert.That(Build(adopted, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int rfs = adopted.RenderFragmentCount;
            int caps = adopted.CapCount;
            adopted.TryGetCapVertex(0, 0, out Vector3 firstVertex);
            int operations = ledger.OperationCount;
            int fragments = ledger.FragmentCount;
            var reflected = new List<VpClipBoundary>();

            var tooSmall = new (VpMultiCutSnapshot snapshot, string what)[]
            {
                (NewSnapshot(branches: 3), "branches"),
                (NewSnapshot(candidates: 8), "candidates: not cut at eight"),
                (NewSnapshot(renderFragments: 2), "render fragments"),
                (NewSnapshot(caps: 7), "caps"),
                (NewSnapshot(chain: 4), "chain"),
            };
            foreach ((VpMultiCutSnapshot snapshot, string what) in tooSmall)
            {
                Assert.That(Build(snapshot, ledger, root, reflected), Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded), what);
                Assert.That(snapshot.IsBuilt, Is.False, what);
                Assert.That(snapshot.RenderFragmentCount + snapshot.CapCount + snapshot.BranchCount, Is.Zero, what + ": nothing readable");
            }

            VpMultiCutSnapshot candidate = NewSnapshot();
            Assert.That(Build(candidate, ledger, root, mapping: Matrix4x4.zero), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "a mapping that carries nothing");
            Assert.That(candidate.IsBuilt, Is.False);
            Assert.Throws<ArgumentNullException>(() => candidate.TryBuild(ledger, root, k_box, Matrix4x4.identity, Matrix4x4.identity, null, Separation, 1e-5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpMultiCutSnapshot(new VpMultiCutCapacities(1, 1, 1, int.MaxValue / 14 + 1, 1)), "cap vertices past an int");

            Assert.That(adopted.IsBuilt, Is.True, "the adopted snapshot is another object and is untouched");
            Assert.That(adopted.RenderFragmentCount, Is.EqualTo(rfs));
            Assert.That(adopted.CapCount, Is.EqualTo(caps));
            adopted.TryGetCapVertex(0, 0, out Vector3 again);
            Assert.That(again, Is.EqualTo(firstVertex));
            Assert.That(ledger.OperationCount, Is.EqualTo(operations), "the ledger is only read");
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragments));
            Assert.That(reflected, Is.Empty, "the reflected set is only read");
        }

        /// <summary>
        /// A retired fragment inside what would be drawn once: nine cuts, then a tenth on L9+ aborted, retiring L9+. The
        /// shape before the ninth would draw L9+'s part back, so the build refuses to decide. A retired fragment outside
        /// any aggregate is simply not drawn.
        /// </summary>
        [Test]
        public void ARetiredFragmentInsideAnAggregate_IsReported_NotDrawnBack()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            for (int k = 0; k < 8; k++)
            {
                at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.2f * k))).positive;
            }

            var (_, l9Plus, _) = Cut(ledger, at, new float4(1f, 0f, 0f, 0f));
            CutOperationId tenth = Admit(ledger, l9Plus, new float4(0f, 0f, 1f, 0f));
            Assert.That(ledger.Abort(tenth), Is.EqualTo(LogicalCutResultOutcome.Applied));

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.RetiredInsideAggregate));
            Assert.That(snapshot.IsBuilt, Is.False);
        }

        /// <summary>
        /// Two different aggregation roots with the same selected prefix: eight cuts down to L8, then A on L8 with both
        /// of A's sides given as reflected, so P (A+) and N (A-) have the same unreflected chain; a ninth cut on each makes
        /// both aggregate. P's branches and N's branches are two render fragments, rooted at P and at N -- a shared prefix
        /// alone does not merge them, although the walk puts them next to each other.
        /// </summary>
        [Test]
        public void TheSamePrefix_UnderDifferentAggregationRoots_IsNotMerged()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            for (int k = 0; k < 8; k++)
            {
                at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.2f * k))).positive;
            }

            var (a, p, n) = Cut(ledger, at, new float4(0f, 0f, 1f, 0f));
            var (x, xPlus, _) = Cut(ledger, p, new float4(1f, 0f, 0f, -0.2f));
            var (y, yPlus, _) = Cut(ledger, n, new float4(1f, 0f, 0f, 0.2f));
            var reflected = new[] { B(ledger, a, 1f), B(ledger, a, -1f) };

            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(Build(snapshot, ledger, root, reflected), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int aggregatedCount = 0;
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                if (!rf.aggregated)
                {
                    continue;
                }

                aggregatedCount++;
                Assert.That(rf.branchCount, Is.EqualTo(2), "each root's two branches");
                Assert.That(rf.root == p || rf.root == n, Is.True, "rooted at P or at N");
                for (int i = 0; i < rf.branchCount; i++)
                {
                    snapshot.TryGetBranch(rf.branchStart + i, out VpMultiCutBranch branch);
                    snapshot.TryGetCandidate(branch.candidateStart + branch.selectedCount, out VpClipCandidate firstIgnored, out _);
                    Assert.That(firstIgnored.boundary.face.operation, Is.EqualTo(rf.root == p ? x : y), "each branch under its own root");
                }
            }

            Assert.That(aggregatedCount, Is.EqualTo(2), "P and N are not merged");
            Assert.That(RenderFragmentOf(snapshot, xPlus).root, Is.EqualTo(p));
            Assert.That(RenderFragmentOf(snapshot, yPlus).root, Is.EqualTo(n));
        }

        /// <summary>
        /// The input contract is checked before anything else, also for a root never cut -- no candidate, no plane to
        /// convert: a placement that is not finite, not affine or not invertible, a mapping that is not rigid (scaled,
        /// mirrored, not finite, not affine), and a box that is not finite or has negative extents are all refused. A
        /// sound input with nothing cut builds one whole render fragment, and a scaled placement is still a placement.
        /// </summary>
        [Test]
        public void TheInputContract_IsCheckedWithNoPlaneToConvert()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            VpMultiCutSnapshot snapshot = NewSnapshot();

            Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built), "the layout: nothing cut");
            Assert.That(snapshot.RenderFragmentCount, Is.EqualTo(1));
            Assert.That(snapshot.CandidateCount, Is.Zero, "no plane to convert");

            Matrix4x4 nan = Matrix4x4.identity;
            nan.m03 = float.NaN;
            Matrix4x4 projective = Matrix4x4.identity;
            projective.m30 = 0.5f;
            Matrix4x4 singular = Matrix4x4.Scale(new Vector3(1f, 0f, 1f));
            var placements = new (Matrix4x4 m, string what)[] { (nan, "not finite"), (projective, "not affine"), (singular, "not invertible") };
            foreach ((Matrix4x4 m, string what) in placements)
            {
                Assert.That(Build(snapshot, ledger, root, placement: m), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "placement " + what);
                Assert.That(snapshot.IsBuilt, Is.False);
            }

            var mappings = new (Matrix4x4 m, string what)[]
            {
                (Matrix4x4.Scale(Vector3.one * 2f), "scaled"),
                (Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)), "mirrored"),
                (nan, "not finite"),
                (projective, "not affine"),
            };
            foreach ((Matrix4x4 m, string what) in mappings)
            {
                Assert.That(Build(snapshot, ledger, root, mapping: m), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "mapping " + what);
            }

            Assert.That(Build(snapshot, ledger, root, box: new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one)), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "box not finite");
            Assert.That(Build(snapshot, ledger, root, box: new Bounds(Vector3.zero, new Vector3(-1f, 1f, 1f))), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "negative extents");
            Assert.That(Build(snapshot, ledger, root, placement: Matrix4x4.TRS(Vector3.one, Quaternion.Euler(10f, 20f, 30f), Vector3.one * 3f)), Is.EqualTo(VpMultiCutBuildOutcome.Built), "a scaled placement is a placement");
        }

        /// <summary>
        /// Finite inputs whose sums overflow: two free cuts the same way at a separation of 3e38 add up past a float; and a
        /// cube 2e32 wide, cut across (y = 0) and then along (x = 0), at a separation of the largest float -- every offset
        /// finite and every section computable, but the placed box, 1e32 up, added to an offset of the largest float up,
        /// passes one. With the epsilon a box that size derives, the section check refuses it first. Both are refused as invalid input by the conservative check before the walk -- with room to spare and with too little room of every kind
        /// alike, so a shortage never hides them -- and the reason says it was that check. Nothing is readable, and no
        /// empty cap stands in.
        /// </summary>
        [Test]
        public void AnOffsetOrACapVertexThatOverflows_IsRefused_WhateverTheRoom()
        {
            const float huge = 3e38f;
            Assert.That(float.IsInfinity(huge + huge), Is.True, "the layout: the sums overflow");

            LogicalCutLedger twice = NewLedger();
            LogicalFragmentId root = twice.AddFragment();
            var (_, up, _) = Cut(twice, root, new float4(0f, 1f, 0f, 0f));
            Cut(twice, up, new float4(0f, 1f, 0f, -0.5f));

            LogicalCutLedger along = NewLedger();
            LogicalFragmentId body = along.AddFragment();
            var (_, bodyPlus, _) = Cut(along, body, new float4(0f, 1f, 0f, 0f));
            Admit(along, bodyPlus, new float4(1f, 0f, 0f, 0f));
            // The epsilon is given small: the one EpsilonFor derives from a box this size squares past a float, which the
            // section check refuses (below) before any offset is asked about.
            var tall = new Bounds(Vector3.zero, Vector3.one * 2e32f);
            Assert.That(float.IsInfinity(float.MaxValue + 1e32f), Is.True, "the layout: the largest float and 1e32 overflow");

            foreach ((VpMultiCutSnapshot snapshot, string what) in RoomyAndShort())
            {
                Assert.That(
                    snapshot.TryBuild(twice, root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, huge, VpCapBoundsPolygon.EpsilonFor(k_box)),
                    Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ": 6e38 of offset");
                Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.ConservativeOffset), what);
                Assert.That(snapshot.IsBuilt, Is.False);

                Assert.That(
                    snapshot.TryBuild(along, body, tall, Matrix4x4.identity, Matrix4x4.identity, k_none, float.MaxValue, 1e-3f),
                    Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ": a cap vertex 1e32 above the largest float");
                Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.ConservativeCapVertex), what);
                Assert.That(snapshot.IsBuilt, Is.False);
                Assert.That(snapshot.CapCount, Is.Zero, "nothing readable, no empty cap in its place");

                Assert.That(
                    snapshot.TryBuild(along, body, tall, Matrix4x4.identity, Matrix4x4.identity, k_none, float.MaxValue, VpCapBoundsPolygon.EpsilonFor(tall)),
                    Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ": the derived epsilon");
                Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.ConservativeSection), what);
            }
        }

        /// <summary>
        /// The check is conservative by contract: nine cuts up one lineage, every side free, at a separation whose ninth
        /// multiple is past a float while its eighth is not. L9+ is drawn only inside the aggregate L8+, whose offset is
        /// eight separations and finite -- nothing drawn would overflow -- and still the input is refused, with room to
        /// spare or not, as the conservative check's refusal and not as a value drawn. With a separation a little
        /// smaller the same lineage builds, and a shortage of room is then only a shortage.
        /// </summary>
        [Test]
        public void TheNumericCheck_RefusesWhatIsNotDrawnToo_AndSaysItWasTheCheck()
        {
            const float separation = 4e37f;
            Assert.That(float.IsInfinity(9f * separation), Is.True, "the layout: nine separations overflow");
            Assert.That(float.IsInfinity(8f * separation), Is.False, "and eight do not");

            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            for (int k = 0; k < 9; k++)
            {
                at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.15f * k))).positive;
            }

            foreach ((VpMultiCutSnapshot snapshot, string what) in RoomyAndShort())
            {
                Assert.That(
                    snapshot.TryBuild(ledger, root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, separation, VpCapBoundsPolygon.EpsilonFor(k_box)),
                    Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what);
                Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.ConservativeOffset), what + ": the check, not a drawn value");
            }

            VpMultiCutSnapshot roomy = NewSnapshot();
            Assert.That(
                roomy.TryBuild(ledger, root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, 3e37f, VpCapBoundsPolygon.EpsilonFor(k_box)),
                Is.EqualTo(VpMultiCutBuildOutcome.Built), "a smaller separation builds");
            Assert.That(roomy.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.None));
            VpMultiCutSnapshot small = NewSnapshot(branches: 2);
            Assert.That(
                small.TryBuild(ledger, root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, 3e37f, VpCapBoundsPolygon.EpsilonFor(k_box)),
                Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded), "and short of room it is only short");
            Assert.That(small.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.None));
        }

        /// <summary>
        /// Sections are taken for the caps drawn only: nine cuts take the eight faces the aggregate and its siblings are
        /// capped by, never the ninth (Ignored) one, and a build reusing that snapshot takes none. The numeric check takes
        /// none of its own: a snapshot too short of caps to draw any takes no section at all.
        /// </summary>
        [Test]
        public void OnlyTheCapsDrawnTakeSections_TheCheckTakesNone()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            for (int k = 0; k < 9; k++)
            {
                at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.15f * k))).positive;
            }

            VpMultiCutSnapshot first = NewSnapshot();
            Assert.That(Build(first, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(first.SectionBuildCount, Is.EqualTo(8), "eight faces drawn; the ninth takes no section");

            VpMultiCutSnapshot second = NewSnapshot();
            Assert.That(
                second.TryBuild(ledger, new[] { new VpMultiCutRegistration(root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, VpCapBoundsPolygon.EpsilonFor(k_box)) }, Separation, first),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(second.SectionBuildCount, Is.Zero, "all reused");

            VpMultiCutSnapshot tight = NewSnapshot(caps: 1);
            Assert.That(Build(tight, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded));
            Assert.That(tight.SectionBuildCount, Is.Zero, "the check took no section");
        }

        /// <summary>
        /// Section arithmetic that could pass a float is refused before the walk, as the conservative section check -- with
        /// room to spare and short of room of every kind alike -- and never answered as an empty cap: an epsilon whose
        /// square passes a float (the one derived from a box 1e26 wide), a plane so far off that a corner's distance and
        /// the difference of two could pass one, and a box wider than a float on an axis, whether a plane crosses that axis
        /// or not. An ordinary plane that misses the box still builds, with empty caps.
        /// </summary>
        [Test]
        public void SectionArithmeticThatCouldOverflow_IsRefused_NotTakenAsEmpty()
        {
            var huge = new Bounds(Vector3.zero, Vector3.one * 1e26f);
            Assert.That(float.IsInfinity(VpCapBoundsPolygon.EpsilonFor(huge) * VpCapBoundsPolygon.EpsilonFor(huge)), Is.True, "the layout");
            var wider = new Bounds { center = Vector3.zero, extents = new Vector3(2e38f, 1f, 1f) };
            var cases = new (string what, Bounds box, float epsilon, float4 plane)[]
            {
                ("an epsilon squared past a float", huge, VpCapBoundsPolygon.EpsilonFor(huge), new float4(0f, 1f, 0f, 0f)),
                ("a plane 3.3e38 off", k_box, VpCapBoundsPolygon.EpsilonFor(k_box), new float4(1f, 0f, 0f, -3.3e38f)),
                ("a box 4e38 wide, crossed along it", wider, 1e-3f, new float4(1f, 0f, 0f, 0f)),
                ("a box 4e38 wide, crossed across it", wider, 1e-3f, new float4(0f, 1f, 0f, 0f)),
            };

            foreach ((string what, Bounds box, float epsilon, float4 plane) in cases)
            {
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId root = ledger.AddFragment();
                Cut(ledger, root, plane);
                foreach ((VpMultiCutSnapshot snapshot, string room) in RoomyAndShort())
                {
                    Assert.That(
                        snapshot.TryBuild(ledger, root, box, Matrix4x4.identity, Matrix4x4.identity, k_none, Separation, epsilon),
                        Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ", " + room);
                    Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.ConservativeSection), what + ", " + room);
                    Assert.That(snapshot.IsBuilt, Is.False);
                }
            }

            LogicalCutLedger ordinary = NewLedger();
            LogicalFragmentId body = ordinary.AddFragment();
            Cut(ordinary, body, new float4(1f, 0f, 0f, -10f));
            VpMultiCutSnapshot missed = NewSnapshot();
            Assert.That(Build(missed, ordinary, body), Is.EqualTo(VpMultiCutBuildOutcome.Built), "a plane that misses, ten off");
            for (int c = 0; c < missed.CapCount; c++)
            {
                missed.TryGetCap(c, out VpMultiCutCap cap);
                Assert.That(cap.vertexCount, Is.Zero, "an empty cap, as ever");
            }
        }

        /// <summary>Snapshots with room to spare, and too little room of every kind.</summary>
        private static (VpMultiCutSnapshot snapshot, string what)[] RoomyAndShort()
        {
            return new[]
            {
                (NewSnapshot(), "room to spare"),
                (NewSnapshot(branches: 1), "branches short"),
                (NewSnapshot(candidates: 1), "candidates short"),
                (NewSnapshot(renderFragments: 1), "render fragments short"),
                (NewSnapshot(caps: 1), "caps short"),
                (NewSnapshot(chain: 1), "chain short"),
            };
        }

        /// <summary>
        /// The candidate room is exactly the candidates kept: nine cuts, so L9+ and L9- aggregate and the aggregation
        /// root's chain is checked, and the first negative child retired by an aborted cut, so a retired chain is checked
        /// too. With room for exactly the candidates the branches keep, the build succeeds -- neither check takes from
        /// that room -- and with one fewer it is refused.
        /// </summary>
        [Test]
        public void TheCandidateRoom_IsExactlyWhatIsKept_TheChecksTakeNoneOfIt()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            LogicalFragmentId firstMinus = default;
            for (int k = 0; k < 9; k++)
            {
                var (_, plus, minus) = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.15f * k)));
                firstMinus = k == 0 ? minus : firstMinus;
                at = plus;
            }

            CutOperationId aborted = Admit(ledger, firstMinus, new float4(1f, 0f, 0f, 0f));
            Assert.That(ledger.Abort(aborted), Is.EqualTo(LogicalCutResultOutcome.Applied), "the layout: the first negative child retired");

            VpMultiCutSnapshot roomy = NewSnapshot();
            Assert.That(Build(roomy, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int kept = roomy.CandidateCount;
            bool aggregated = false;
            for (int r = 0; r < roomy.RenderFragmentCount; r++)
            {
                roomy.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                aggregated |= rf.aggregated;
            }

            Assert.That(aggregated, Is.True, "the layout: an aggregation root is checked");

            VpMultiCutSnapshot exact = NewSnapshot(candidates: kept);
            Assert.That(Build(exact, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built), "room for exactly the " + kept + " candidates kept");
            Assert.That(exact.CandidateCount, Is.EqualTo(kept));

            VpMultiCutSnapshot short1 = NewSnapshot(candidates: kept - 1);
            Assert.That(Build(short1, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded), "one fewer is refused");
            Assert.That(short1.IsBuilt, Is.False);
        }

        // ----- several registrations ------------------------------------------------------------------------------------

        /// <summary>
        /// Two registrations built together: each one's branches and render fragments are contiguous and carry its index,
        /// its box and its placement; nothing is aggregated across them; and the result for each is the one a build of
        /// that registration alone gives -- the same clip planes, offsets and cap vertices.
        /// </summary>
        [Test]
        public void TwoRegistrations_AreBuiltTogether_EachAsItWouldBeAlone()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId first = ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
            LogicalFragmentId second = ledger.AddFragment();
            var (_, plus, _) = Cut(ledger, first, new float4(0f, 1f, 0f, 0f));
            Admit(ledger, plus, new float4(1f, 0f, 0f, 0f));
            Admit(ledger, second, new float4(0f, 0f, 1f, 0.2f));
            Matrix4x4 moved = Matrix4x4.TRS(new Vector3(3f, 0f, 0f), Quaternion.Euler(0f, 0f, 20f), Vector3.one);
            var otherBox = new Bounds(Vector3.zero, new Vector3(1f, 2f, 3f));
            var registrations = new[]
            {
                new VpMultiCutRegistration(first, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, VpCapBoundsPolygon.EpsilonFor(k_box)),
                new VpMultiCutRegistration(second, otherBox, moved, Matrix4x4.identity, k_none, VpCapBoundsPolygon.EpsilonFor(otherBox)),
            };

            VpMultiCutSnapshot together = NewSnapshot();
            Assert.That(together.TryBuild(ledger, registrations, Separation), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(together.RenderFragmentCount, Is.EqualTo(5), "A-, A+ as B's two sides, and the second body's two sides");

            int lastRegistration = 0;
            for (int b = 0; b < together.BranchCount; b++)
            {
                together.TryGetBranch(b, out VpMultiCutBranch branch);
                Assert.That(branch.registration, Is.GreaterThanOrEqualTo(lastRegistration), "one registration's branches together");
                lastRegistration = branch.registration;
            }

            for (int g = 0; g < 2; g++)
            {
                VpMultiCutSnapshot alone = NewSnapshot();
                VpMultiCutRegistration r = registrations[g];
                Assert.That(
                    alone.TryBuild(ledger, r.root, r.localBounds, r.geometryLocalToWorld, r.lineageToGeometryLocal, r.reflected, Separation, r.vertexEpsilon),
                    Is.EqualTo(VpMultiCutBuildOutcome.Built));
                int k = 0;
                for (int i = 0; i < together.RenderFragmentCount; i++)
                {
                    together.TryGetRenderFragment(i, out VpMultiCutRenderFragment rf);
                    if (rf.registration != g)
                    {
                        continue;
                    }

                    Assert.That(rf.localBounds, Is.EqualTo(r.localBounds), "its own box");
                    Assert.That(rf.geometryLocalToWorld, Is.EqualTo(r.geometryLocalToWorld), "its own placement");
                    alone.TryGetRenderFragment(k++, out VpMultiCutRenderFragment same);
                    Assert.That(rf.root, Is.EqualTo(same.root));
                    Assert.That(rf.offset, Is.EqualTo(same.offset));
                    Assert.That(rf.clip.PlaneCount, Is.EqualTo(same.clip.PlaneCount));
                    Assert.That(rf.capCount, Is.EqualTo(same.capCount));
                    for (int c = 0; c < rf.capCount; c++)
                    {
                        together.TryGetCap(rf.capStart + c, out VpMultiCutCap cap);
                        alone.TryGetCap(same.capStart + c, out VpMultiCutCap sameCap);
                        Assert.That(cap.vertexCount, Is.EqualTo(sameCap.vertexCount));
                        for (int v = 0; v < cap.vertexCount; v++)
                        {
                            together.TryGetCapVertex(rf.capStart + c, v, out Vector3 x);
                            alone.TryGetCapVertex(same.capStart + c, v, out Vector3 y);
                            Assert.That(x, Is.EqualTo(y));
                        }
                    }
                }

                Assert.That(k, Is.EqualTo(alone.RenderFragmentCount), "registration " + g + ": every render fragment, once");
            }
        }

        /// <summary>
        /// No registration's root may be on another's lineage: the same root twice, and a root below another, are invalid
        /// input -- decided before any room is taken, so a snapshot with room for one branch says so too.
        /// </summary>
        [Test]
        public void RootsOnOneLineage_AreRefused()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, plus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            float epsilon = VpCapBoundsPolygon.EpsilonFor(k_box);
            var rootAlone = new VpMultiCutRegistration(root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, epsilon);
            var child = new VpMultiCutRegistration(plus, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, epsilon);
            foreach (VpMultiCutSnapshot snapshot in new[] { NewSnapshot(), NewSnapshot(branches: 1) })
            {
                Assert.That(snapshot.TryBuild(ledger, new[] { rootAlone, rootAlone }, Separation), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "twice");
                Assert.That(snapshot.TryBuild(ledger, new[] { rootAlone, child }, Separation), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "a descendant");
                Assert.That(snapshot.TryBuild(ledger, new[] { child, rootAlone }, Separation), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "an ancestor");
                Assert.That(snapshot.IsBuilt, Is.False);
            }

            Assert.Throws<ArgumentNullException>(() => NewSnapshot().TryBuild(ledger, (IReadOnlyList<VpMultiCutRegistration>)null, Separation));
            Assert.Throws<ArgumentNullException>(
                () => NewSnapshot().TryBuild(ledger, new[] { new VpMultiCutRegistration(root, k_box, Matrix4x4.identity, Matrix4x4.identity, null, epsilon) }, Separation));
        }

        /// <summary>
        /// A retired fragment inside an aggregate is found before any room is taken: snapshots too small in every way the
        /// walk could run short -- branches, candidates, chain, render fragments, caps -- still answer
        /// <see cref="VpMultiCutBuildOutcome.RetiredInsideAggregate"/>, never a shortage that would let an earlier snapshot
        /// be kept drawing.
        /// </summary>
        [Test]
        public void ARetiredFragmentInsideAnAggregate_IsFoundBeforeAnyRoomIsTaken()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            for (int k = 0; k < 9; k++)
            {
                at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.15f * k))).positive;
            }

            var small = new (VpMultiCutSnapshot snapshot, string what)[]
            {
                (NewSnapshot(branches: 1), "branches"),
                (NewSnapshot(candidates: 1), "candidates"),
                (NewSnapshot(chain: 1), "chain"),
                (NewSnapshot(renderFragments: 1), "render fragments"),
                (NewSnapshot(caps: 1), "caps"),
            };

            foreach ((VpMultiCutSnapshot snapshot, string what) in small)
            {
                Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded), what + ": the layout, only short");
            }

            CutOperationId tenth = Admit(ledger, at, new float4(0f, 0f, 1f, 0f));
            Assert.That(ledger.Abort(tenth), Is.EqualTo(LogicalCutResultOutcome.Applied));
            foreach ((VpMultiCutSnapshot snapshot, string what) in small)
            {
                Assert.That(Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.RetiredInsideAggregate), what);
                Assert.That(snapshot.IsBuilt, Is.False);
            }
        }

        /// <summary>
        /// The sections a build takes are taken once per face, shared by both sides, and taken from the snapshot given to
        /// reuse from when its key is exactly the same: none is taken again then, and every cap vertex is the same. A
        /// moved placement is a different key and is taken again.
        /// </summary>
        [Test]
        public void Sections_AreTakenOncePerFace_AndReusedFromTheSnapshotGiven()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, plus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            Admit(ledger, plus, new float4(1f, 0f, 0f, 0f));
            var registrations = new[]
            {
                new VpMultiCutRegistration(root, k_box, Matrix4x4.identity, Matrix4x4.identity, k_none, VpCapBoundsPolygon.EpsilonFor(k_box)),
            };

            VpMultiCutSnapshot first = NewSnapshot();
            Assert.That(first.TryBuild(ledger, registrations, Separation), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(first.CapCount, Is.EqualTo(5), "the layout: A- one cap, A+ B+ and A+ B- two each");
            Assert.That(first.SectionBuildCount, Is.EqualTo(2), "one section per face");

            VpMultiCutSnapshot second = NewSnapshot();
            Assert.That(second.TryBuild(ledger, registrations, Separation, first), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(second.SectionBuildCount, Is.Zero, "every section reused");
            for (int c = 0; c < first.CapCount; c++)
            {
                first.TryGetCap(c, out VpMultiCutCap a);
                second.TryGetCap(c, out VpMultiCutCap b);
                Assert.That(b.vertexCount, Is.EqualTo(a.vertexCount));
                for (int v = 0; v < a.vertexCount; v++)
                {
                    first.TryGetCapVertex(c, v, out Vector3 x);
                    second.TryGetCapVertex(c, v, out Vector3 y);
                    Assert.That(y, Is.EqualTo(x));
                }
            }

            var moved = new[]
            {
                new VpMultiCutRegistration(root, k_box, Matrix4x4.Translate(new Vector3(0f, 1e-3f, 0f)), Matrix4x4.identity, k_none, VpCapBoundsPolygon.EpsilonFor(k_box)),
            };
            VpMultiCutSnapshot third = NewSnapshot();
            Assert.That(third.TryBuild(ledger, moved, Separation, second), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(third.SectionBuildCount, Is.EqualTo(2), "a moved placement is another key");
        }

        // ----- fixture -------------------------------------------------------------------------------------------------

        private static readonly float3[] k_pyramid =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, 1.0f), new float3(-1.0f, 0.0f, 1.0f),
            new float3(-0.5f, 2.0f, -0.5f), new float3(0.5f, 2.0f, -0.5f), new float3(0.5f, 2.0f, 0.5f), new float3(-0.5f, 2.0f, 0.5f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
            new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

        internal const int SideMaterial = 7;

        internal static VpStoredGeometry AppendPyramid(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            foreach (int[] c in k_faces)
            {
                float3 n = math.normalize(math.cross(k_pyramid[c[1]] - k_pyramid[c[0]], k_pyramid[c[2]] - k_pyramid[c[0]]));
                uint b = (uint)vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new VpRenderVertex { position = k_pyramid[c[k]], normal = n, uv0 = new float2(0.5f, 0.5f) });
                    topology.Add(c[k]);
                }

                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), 8,
                    new[] { new VpGeometrySubmesh(0, indices.Count, SideMaterial) }, out VpStoredGeometry geometry),
                Is.True);
            return geometry;
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null);
            var side = new Material(shader) { name = "side" };
            _objects.Add(side);
            return new Dictionary<int, Material> { { SideMaterial, side } };
        }
    }
}
