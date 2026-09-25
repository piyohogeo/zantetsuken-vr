using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The cut DAG: what an admitted cut depends on, when its kernel may run, and what has to hold before its geometry
    /// is committed (DESIGN 4.2, 4.4, 4.5.6, 7.1, 8).
    /// <para>
    /// Order is controlled, never raced: the commit is a test double that says when it is established and what
    /// geometry each side becomes, and the cases that are about order hold the work at the destination until the test
    /// releases it. One case runs through the real geometry pool. Final physics is synthetic throughout — the ledger's
    /// own publication and abort stand for it, and no convex cut, cook or actor exists here.
    /// </para>
    /// </summary>
    public class CutDagTests
    {
        private const int DeadlineMilliseconds = 30000;

        // ----- fixture ---------------------------------------------------------------------------------------------

        private sealed class Fixture : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public LogicalCutLedger ledger;
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor pool;
            public WorkerPoolExecutor background;
            public GatedExecutor gate;
            public SharedWorkDispatcher dispatcher;
            public FakeCommit commit;
            public RecordingFault fault;
            public CutDag dag;
            public SharedWorkFrame frame;
            private int _frame;

            public void Frame()
            {
                dispatcher.BeginFrame(++_frame);
                dispatcher.Dispatch();
                dag.Pump();
            }

            /// <summary>Frames until the condition holds. A deadline that passes is a failure, not a wait.</summary>
            public void RunUntil(Func<bool> condition, string what)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Frame();
                    if (condition())
                    {
                        return;
                    }

                    if (gate == null)
                    {
                        Thread.Sleep(1);
                    }
                }

                Assert.Fail(what + ": it had not happened when the deadline passed");
            }

            /// <summary>
            /// Frames, releasing whatever the gate is holding as it appears, until the condition holds. One cut takes
            /// two pieces of work — the capacity query and the cut itself — and a main thread turn between them, so a
            /// case that waits for a result drives it here. Nothing depends on how long anything takes: the bound is a
            /// number of turns, and reaching it is a failure.
            /// </summary>
            public void RunGatedUntil(Func<bool> condition, string what)
            {
                for (int turn = 0; turn < 400; turn++)
                {
                    Frame();
                    if (condition())
                    {
                        return;
                    }

                    if (gate.Accepted > 0)
                    {
                        gate.RunOneOnAWorker();
                    }
                }

                Assert.Fail(what + ": it had not happened after 400 turns");
            }

            /// <summary>Runs everything the gate is holding, one piece at a time, on a worker thread of its own.</summary>
            public void ReleaseHeldWork()
            {
                while (gate.Accepted > 0)
                {
                    gate.RunOneOnAWorker();
                }
            }

            public void Dispose()
            {
                dag?.Dispose();
                var clock = Stopwatch.StartNew();
                while (!dag.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    if (gate != null)
                    {
                        ReleaseHeldWork();
                    }

                    Frame();
                }

                dispatcher?.Shutdown(DeadlineMilliseconds);
                dag?.Pump();
                pool?.Dispose();
                background?.Dispose();
                storage?.Dispose();
            }
        }

        /// <summary>
        /// The commit, as this test controls it: it refuses until the test allows an operation, and it decides what
        /// geometry each side becomes — by default the side the cut produced, which is what a real commit of an
        /// unchanged branch would hand on.
        /// </summary>
        private sealed class FakeCommit : ICutGeometryCommit
        {
            private readonly HashSet<CutOperationId> _allowed = new HashSet<CutOperationId>();
            internal readonly List<CutOperationId> committed = new List<CutOperationId>();
            internal readonly List<CutGeometryCommit> seen = new List<CutGeometryCommit>();
            internal bool allowEverything;

            internal void Allow(CutOperationId operation)
            {
                _allowed.Add(operation);
            }

            public bool TryCommit(in CutGeometryCommit commit, out CutGeometryCommitted result)
            {
                result = default;
                if (!allowEverything && !_allowed.Contains(commit.operation))
                {
                    return false;
                }

                seen.Add(commit);
                committed.Add(commit.operation);
                result.positive = commit.positive.geometry;
                result.negative = commit.negative.geometry;
                return true;
            }
        }

        /// <summary>
        /// Where a geometry failure arrives, which is the caller's own boundary onto chapter 4. It can be made to
        /// throw, because a real one may: taking a failure to the common termination is not a quiet call.
        /// </summary>
        private sealed class RecordingFault : ICutGeometryFault
        {
            internal readonly List<CutGeometryFault> faults = new List<CutGeometryFault>();
            internal Exception throws;

            public void GeometryFailed(in CutGeometryFault fault)
            {
                faults.Add(fault);
                if (throws != null)
                {
                    throw throws;
                }
            }
        }

        /// <summary>Frames until the fault port has thrown, and gives back what it threw.</summary>
        private static Exception RunUntilTheNotificationThrows(Fixture f, string what)
        {
            for (int turn = 0; turn < 400; turn++)
            {
                try
                {
                    f.Frame();
                }
                catch (Exception thrown)
                {
                    return thrown;
                }
            }

            Assert.Fail(what + ": the notification had not thrown after 400 turns");
            return null;
        }

        private static Fixture NewFixture(
            bool gated = false,
            IWorkExecutor geometryPool = null,
            int waitingCapacity = 8,
            int reservedForUrgent = 2,
            int frameBudget = 32)
        {
            var fixture = new Fixture
            {
                storage = new VpCpuGeometryStorage(16384, 65536, 256, 1024, 1024, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(16)),
                job = new UnityJobWorkExecutor(4),
                background = WorkerPoolExecutor.BackgroundPool(2),
                commit = new FakeCommit(),
                fault = new RecordingFault(),
            };
            IWorkExecutor geometry = geometryPool;
            if (geometry == null)
            {
                if (gated)
                {
                    fixture.gate = new GatedExecutor();
                    geometry = fixture.gate;
                }
                else
                {
                    fixture.pool = WorkerPoolExecutor.GeometryPool(4);
                    geometry = fixture.pool;
                }
            }

            fixture.dispatcher = new SharedWorkDispatcher(
                waitingCapacity, reservedForUrgent, frameBudget, fixture.job, geometry, fixture.background);
            fixture.dag = new CutDag(fixture.storage, fixture.ledger, fixture.dispatcher, fixture.commit, fixture.fault);
            fixture.frame = new SharedWorkFrame(fixture.dispatcher);
            fixture.frame.Add(fixture.dag);
            return fixture;
        }

        /// <summary>The two sides a cut is holding before anything is committed, as index ranges.</summary>
        private static List<VpIndexRangeHandle> ProducedRanges(Fixture f, CutOperationId operation)
        {
            Assert.That(f.dag.TryGetResultBeforeCommit(operation, out VpStorageCutResult result), Is.True, "it is holding a result");
            var ranges = new List<VpIndexRangeHandle>();
            if (result.positive.IsProduced)
            {
                ranges.Add(result.positive.geometry.indexRange);
            }

            if (result.negative.IsProduced)
            {
                ranges.Add(result.negative.geometry.indexRange);
            }

            Assert.That(ranges, Is.Not.Empty, "the cut produced geometry");
            return ranges;
        }

        private static void AssertRangesAreFree(Fixture f, IEnumerable<VpIndexRangeHandle> ranges, string what)
        {
            foreach (VpIndexRangeHandle range in ranges)
            {
                Assert.That(
                    f.storage.TryGetIndexState(range, out VpIndexRangeState state, out _, out _) && state == VpIndexRangeState.Free,
                    Is.True,
                    what + ": the produced range is free again, so the space can be used");
            }
        }

        /// <summary>A closed box with two submeshes, appended as a cut input and registered as a branch's base.</summary>
        private static LogicalFragmentId NewBranch(Fixture f, float3 offset, IReadOnlyList<float3> anchors = null)
        {
            SyntheticMesh mesh = SyntheticGeometry.Box(2, new float3(1f, 1f, 1f), offset, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int at = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(at, mesh.SubmeshIndexCounts[s], s);
                at += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(
                f.storage.TryAppendCuttable(
                    mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes,
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the box is appended as a cut input");
            LogicalFragmentId fragment = anchors == null ? f.ledger.AddFragment() : f.ledger.AddFragment(anchors);
            f.dag.RegisterBaseGeometry(fragment, geometry);
            return fragment;
        }

        /// <summary>
        /// The same box, registered with the mapping from the lineage's logical frame to the geometry's own
        /// coordinates. The geometry itself is identical to the one <see cref="NewBranch"/> makes: only the frame the
        /// caller says its planes arrive in is different.
        /// </summary>
        private static LogicalFragmentId NewBranchInFrame(
            Fixture f, Matrix4x4 lineageToGeometryLocal, float3 offset = default)
        {
            SyntheticMesh mesh = SyntheticGeometry.Box(2, new float3(1f, 1f, 1f), offset, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int at = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(at, mesh.SubmeshIndexCounts[s], s);
                at += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(
                f.storage.TryAppendCuttable(
                    mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes,
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the box is appended as a cut input");
            LogicalFragmentId fragment = f.ledger.AddFragment();
            f.dag.RegisterBaseGeometry(fragment, geometry, lineageToGeometryLocal);
            return fragment;
        }

        /// <summary>
        /// A quarter turn about Z followed by a translation, written out entry by entry so that every number in it is
        /// exact and nothing about how a quaternion rounds is assumed. It takes a point of the lineage's frame to the
        /// same point in the geometry's own coordinates: x goes to +y, y goes to -x, and the whole is moved by t.
        /// </summary>
        private static Matrix4x4 TurnedFrame(Vector3 t)
        {
            var m = new Matrix4x4();
            m.SetColumn(0, new Vector4(0f, 1f, 0f, 0f));
            m.SetColumn(1, new Vector4(-1f, 0f, 0f, 0f));
            m.SetColumn(2, new Vector4(0f, 0f, 1f, 0f));
            m.SetColumn(3, new Vector4(t.x, t.y, t.z, 1f));
            return m;
        }

        /// <summary>
        /// The plane <paramref name="plane"/> of the lineage's frame in the coordinates of a geometry placed by
        /// <paramref name="frame"/>, worked out here rather than asked of the product: for y = R x + t, a point x
        /// satisfies dot(n, x) + d = 0 exactly when y satisfies dot(R n, y) + (d - dot(R n, t)) = 0. Only the
        /// rotation and the translation of the frame are used, which is all these cases have in them.
        /// </summary>
        private static float4 PlaneInGeometryByHand(float4 plane, Matrix4x4 frame)
        {
            Vector4 x = frame.GetColumn(0);
            Vector4 y = frame.GetColumn(1);
            Vector4 z = frame.GetColumn(2);
            Vector4 t = frame.GetColumn(3);
            var turned = new float3(
                (x.x * plane.x) + (y.x * plane.y) + (z.x * plane.z),
                (x.y * plane.x) + (y.y * plane.y) + (z.y * plane.z),
                (x.z * plane.x) + (y.z * plane.y) + (z.z * plane.z));
            float moved = plane.w - ((turned.x * t.x) + (turned.y * t.y) + (turned.z * t.z));
            return new float4(turned, moved);
        }

        private static void AssertPlaneIs(float4 actual, float4 expected, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-6f), what + ": the normal's x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-6f), what + ": the normal's y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(1e-6f), what + ": the normal's z");
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(1e-6f), what + ": d");
        }

        /// <summary>What one committed side amounts to, as numbers two separate storages can be compared by.</summary>
        private static string SideShape(Fixture f, VpStorageCutSide side)
        {
            if (side.IsEmpty)
            {
                return "empty";
            }

            Assert.That(
                f.storage.TryGetIndexState(side.geometry.indexRange, out _, out _, out int indices), Is.True,
                "the side's indices are readable");
            return (side.IsProduced ? "produced" : "borrowed") + " v=" + side.geometry.vertexCount + " i=" + indices;
        }

        private static float4 Tilted(float3 through)
        {
            return SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), through + new float3(0.0071f, -0.0233f, 0.0119f));
        }

        private static CutOperationId Admit(Fixture f, LogicalFragmentId source, float4 plane)
        {
            Assert.That(
                f.dag.TryAdmit(source, plane, true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted),
                "the cut is admitted");
            return operation;
        }

        /// <summary>The synthetic final physics: the anchors are distributed and the two children are published.</summary>
        private static void PublishPhysics(Fixture f, CutOperationId operation, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            Assert.That(
                f.ledger.PrepareAnchorDistribution(operation, 1e-5f, out _),
                Is.EqualTo(AnchorPreparationOutcome.Prepared),
                "the anchors are distributed");
            Assert.That(
                f.dag.PublishAfterFinalPhysics(operation, out positive, out negative),
                Is.EqualTo(LogicalCutResultOutcome.Applied),
                "the operation and its two children are published");
        }

        private static LogicalCutOperationState StateOf(Fixture f, CutOperationId operation)
        {
            Assert.That(f.ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True, "the operation is known");
            return record.state;
        }

        [Test] public void D4_GeometryCapacity_TwoRootsNoGrowth_NoWorkOrBudget_ClosedRefused()
        {
            using (Fixture f = NewFixture())
            {
                Assert.Throws<ArgumentOutOfRangeException>(()=>f.dag.PrepareGeometryCapacity(-1));
                f.dag.PrepareGeometryCapacity(8);
                int capacity = D4ColdPreparationTests.Capacity(f.dag,"_geometryOf");
                f.dag.PrepareGeometryCapacity(8); f.dag.PrepareGeometryCapacity(0);
                Assert.That(f.ledger.FragmentCount + f.ledger.OperationCount + f.ledger.Revision, Is.Zero);
                Assert.That(f.dag.ActiveCount + f.ledger.Budget.IncompleteCutOperationCount, Is.Zero);
                var a = NewBranch(f,float3.zero); var b = NewBranch(f,new float3(5,0,0));
                Assert.That(a,Is.Not.EqualTo(b));
                Assert.That(D4ColdPreparationTests.Capacity(f.dag,"_geometryOf"),Is.EqualTo(capacity));
                Assert.That(D4ColdPreparationTests.Capacity(f.dag,"_frameOf"),Is.EqualTo(capacity));
                f.dag.Dispose(); Assert.Throws<InvalidOperationException>(()=>f.dag.PrepareGeometryCapacity(8));
            }
        }

        // ----- 1. geometry first, publication later --------------------------------------------------------------------

        /// <summary>
        /// A geometry that is ready before its own logical publication waits: nothing is committed, and the two sides
        /// sit in the storage with nothing outside it changed. The publication is what releases it, and then the
        /// commit happens and the ledger takes the notice once.
        /// </summary>
        [Test]
        public void GeometryThatIsReadyFirst_IsNotCommittedBeforeTheLogicalPublication()
        {
            using (Fixture f = NewFixture())
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                f.commit.allowEverything = true;

                f.RunUntil(() => f.dag.StageOf(cut) == CutGeometryStage.CpuPublished, "the kernel finishes");

                Assert.That(f.commit.committed, Is.Empty, "nothing is committed while the operation is unpublished");
                Assert.That(StateOf(f, cut), Is.EqualTo(LogicalCutOperationState.Admitted), "it is still only admitted");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and it still holds its budget unit");

                PublishPhysics(f, cut, out _, out _);
                f.Frame();

                Assert.That(f.commit.committed, Is.EqualTo(new[] { cut }), "the publication released the commit");
                Assert.That(f.dag.StageOf(cut), Is.EqualTo(CutGeometryStage.Committed), "the cut is committed");
                Assert.That(StateOf(f, cut), Is.EqualTo(LogicalCutOperationState.Completed), "the ledger took the notice");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the budget unit came back once");
            }
        }

        // ----- 2. publication first, the child's kernel still waits -------------------------------------------------------

        /// <summary>
        /// Physics does not wait for geometry: with A published, its child is cut again at once and that cut is
        /// admitted and published while A's geometry is unfinished. Only the child's kernel waits — for A's commit.
        /// </summary>
        [Test]
        public void AChildIsCutAndPublishedWhileTheAncestorGeometryIsUnfinished_AndOnlyItsKernelWaits()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out _);

                CutOperationId b = Admit(f, aPositive, Tilted(new float3(0.2f, 0f, 0f)));
                PublishPhysics(f, b, out _, out _);

                f.Frame();
                f.Frame();

                Assert.That(StateOf(f, b), Is.EqualTo(LogicalCutOperationState.Published), "the child's cut is published");
                Assert.That(f.dag.StageOf(b), Is.EqualTo(CutGeometryStage.WaitingForBasis), "but its kernel has not started");
                Assert.That(f.dag.TryGetGeometry(aPositive, out _), Is.False, "because that child has no geometry yet");
                Assert.That(f.dag.StageOf(a), Is.EqualTo(CutGeometryStage.Running), "while A's own kernel is running");
            }
        }

        // ----- 3. ancestor order and the basis each cut reads ---------------------------------------------------------------

        /// <summary>
        /// A→B runs in ancestor order, and B is cut from exactly the geometry A's commit gave its child — not from
        /// A's uncommitted output. Once A has committed, B becomes ready in that same update.
        /// </summary>
        [Test]
        public void TheAncestorCommitsFirst_AndTheChildIsCutFromWhatThatCommitGaveIt()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out LogicalFragmentId aNegative);
                CutOperationId b = Admit(f, aPositive, Tilted(new float3(0.2f, 0f, 0f)));
                PublishPhysics(f, b, out _, out _);

                // A's kernel runs; B's cannot, because A has not committed.
                f.RunGatedUntil(() => f.dag.StageOf(a) == CutGeometryStage.CpuPublished, "A's kernel finishes");
                Assert.That(f.dag.StageOf(a), Is.EqualTo(CutGeometryStage.CpuPublished), "A's two sides are in the storage");
                Assert.That(f.dag.StageOf(b), Is.EqualTo(CutGeometryStage.WaitingForBasis), "B has still not started");
                Assert.That(f.commit.committed, Is.Empty, "and nothing has been committed");

                f.commit.Allow(a);
                f.Frame();

                Assert.That(f.commit.committed, Is.EqualTo(new[] { a }), "A commits");
                Assert.That(f.dag.TryGetGeometry(aPositive, out VpStoredGeometry positiveGeometry), Is.True, "its positive child has geometry");
                Assert.That(f.dag.TryGetGeometry(aNegative, out _), Is.True, "and so has its negative child");
                Assert.That(f.dag.TryGetGeometry(body, out _), Is.False, "the source is no longer a geometry of its own");
                Assert.That(f.dag.StageOf(b), Is.EqualTo(CutGeometryStage.Running), "B became ready in the same update");
                Assert.That(f.dag.BasisOf(b), Is.EqualTo(positiveGeometry), "and reads exactly what A's commit gave that child");

                f.commit.Allow(b);
                f.RunGatedUntil(() => f.commit.committed.Count == 2, "B commits too");

                Assert.That(f.commit.committed, Is.EqualTo(new[] { a, b }), "B commits after A, in that order");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "both budget units came back");
            }
        }

        /// <summary>
        /// A child's cut is **accepted by the destination** in the same frame its parent's commit happened in. One
        /// update of the product's own frame entry does it: the parent's result is collected, the commit settles, the
        /// child's cut is made and given its reservation, and the destination takes it.
        /// <para>
        /// **What this shows, exactly.** The cut is a real one and the geometry is really committed, but the commit is
        /// this file's <c>FakeCommit</c> -- a caller standing in for the display's own -- not a display commit. The
        /// destination is this file's gate, whose <c>BeginAccepted</c> does nothing and which runs work only when the
        /// test tells it to, so what is seen is **acceptance**, not a <c>Begin</c> at acceptance. Reaching
        /// <see cref="CutGeometryStage.Running"/> is not enough on its own -- that says the cut was made, not that
        /// anything took it -- so the gate's own count of what it has taken is what settles it.
        /// </para>
        /// </summary>
        [Test]
        public void TheChildsCutIsSubmittedInTheFrameTheParentCommittedIn()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out _);
                CutOperationId b = Admit(f, aPositive, Tilted(new float3(0.2f, 0f, 0f)));
                PublishPhysics(f, b, out _, out _);

                // Frame 1, all of it: the parent's work is run and collected, and the commit is allowed, so the child
                // becomes possible inside this one update.
                f.commit.Allow(a);
                var clock = Stopwatch.StartNew();
                int acceptedBeforeTheCommit = 0;
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && f.commit.committed.Count == 0)
                {
                    acceptedBeforeTheCommit = f.gate.TotalAccepted;
                    f.gate.RunEverythingWaiting();
                    f.frame.Update(1);
                }

                Assert.That(f.commit.committed, Is.EqualTo(new[] { a }), "the parent committed");
                Assert.That(
                    f.dag.TryGetGeometry(aPositive, out VpStoredGeometry positiveGeometry), Is.True,
                    "and its positive child has geometry");

                // The child's work is with the destination, in this same frame.
                Assert.That(f.dag.StageOf(b), Is.EqualTo(CutGeometryStage.Running), "the child's cut was made");
                Assert.That(
                    f.gate.TotalAccepted, Is.GreaterThan(acceptedBeforeTheCommit),
                    "and something was taken by the destination after the commit, in the same frame: "
                    + acceptedBeforeTheCommit + " -> " + f.gate.TotalAccepted);
                Assert.That(
                    f.dag.BasisOf(b), Is.EqualTo(positiveGeometry),
                    "and it reads exactly what the parent's commit gave that child");

                f.commit.Allow(b);
                clock.Restart();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && f.commit.committed.Count < 2)
                {
                    f.gate.RunEverythingWaiting();
                    f.frame.Update(1);
                }

                Assert.That(f.commit.committed, Is.EqualTo(new[] { a, b }), "both committed, in that order, in one frame");
            }
        }

        // ----- 4. another branch moving on, and authority -----------------------------------------------------------------

        /// <summary>
        /// A cut of a descendant does not invalidate the ancestor's geometry: with B admitted and published on A's
        /// child, A's own result is still adopted when it arrives.
        /// </summary>
        [Test]
        public void ADescendantCut_DoesNotInvalidateTheAncestorsGeometry()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out _);
                CutOperationId b = Admit(f, aPositive, Tilted(new float3(0.2f, 0f, 0f)));
                PublishPhysics(f, b, out _, out _);
                f.ledger.NoteOwnershipChanged(aPositive);

                f.RunGatedUntil(() => f.dag.StageOf(a) == CutGeometryStage.CpuPublished, "A's kernel finishes");
                f.commit.Allow(a);
                f.Frame();

                Assert.That(f.commit.committed, Is.EqualTo(new[] { a }), "A's geometry is still adopted");
                Assert.That(StateOf(f, a), Is.EqualTo(LogicalCutOperationState.Completed), "and its duty is done");
                Assert.That(f.dag.StageOf(b), Is.EqualTo(CutGeometryStage.Running), "B carries on from it");
            }
        }

        /// <summary>
        /// Being published is not on its own the right to adopt a late result: when every branch of the cut has
        /// retired, the result is reclaimed rather than committed, and the ledger is told the duty ended.
        /// </summary>
        [Test]
        public void WhenNoBranchNeedsItAnyMore_TheLateResultIsNotAdopted()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId positive, out LogicalFragmentId negative);

                // The result is there and the commit is not refusing it: only the branch decides what happens next.
                f.RunGatedUntil(() => f.dag.StageOf(a) == CutGeometryStage.CpuPublished, "A's kernel finishes");
                List<VpIndexRangeHandle> produced = ProducedRanges(f, a);
                Assert.That(f.ledger.Retire(positive), Is.True, "one branch retires");
                Assert.That(f.ledger.Retire(negative), Is.True, "and so does the other");
                f.commit.allowEverything = true;
                f.Frame();

                Assert.That(f.commit.committed, Is.Empty, "nothing was committed");
                AssertRangesAreFree(f, produced, "after a result nobody adopted");
                Assert.That(f.dag.StageOf(a), Is.EqualTo(CutGeometryStage.Reclaimed), "the result was reclaimed");
                Assert.That(StateOf(f, a), Is.EqualTo(LogicalCutOperationState.Terminated), "the duty ended without completing");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the budget unit came back once");
            }
        }

        // ----- 5. one branch retiring, the other still needing it -----------------------------------------------------------

        /// <summary>
        /// One branch retiring does not stop the ancestor work the other branch still needs: the commit happens, and
        /// only the side that retired is without a reader.
        /// </summary>
        [Test]
        public void OneBranchRetiring_KeepsTheAncestorWorkTheOtherBranchNeeds()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId positive, out LogicalFragmentId negative);

                f.RunGatedUntil(() => f.dag.StageOf(a) == CutGeometryStage.CpuPublished, "A's kernel finishes");
                Assert.That(f.ledger.Retire(negative), Is.True, "one branch retires");
                f.commit.allowEverything = true;
                f.Frame();

                Assert.That(f.commit.committed, Is.EqualTo(new[] { a }), "the cut still commits for the living branch");
                Assert.That(StateOf(f, a), Is.EqualTo(LogicalCutOperationState.Completed), "its duty is done");
                Assert.That(f.dag.TryGetGeometry(positive, out _), Is.True, "the living branch has its geometry");
            }
        }

        // ----- 6. busy queue and reservation, and nothing counted twice --------------------------------------------------------

        /// <summary>
        /// Three cuts at once, through the real geometry pool, with a waiting queue that can hold one of them at a
        /// time and a storage that reserves for one at a time: none is lost, each commits once, and each budget unit
        /// comes back exactly once however often the DAG is pumped afterwards.
        /// </summary>
        [Test]
        public void WorkIsNotLostWhileTheQueueAndTheReservationAreBusy_AndNothingIsCountedTwice()
        {
            using (Fixture f = NewFixture(waitingCapacity: 2, reservedForUrgent: 1, frameBudget: 4))
            {
                var cuts = new List<CutOperationId>();
                for (int i = 0; i < 3; i++)
                {
                    var offset = new float3(4f * i, 0f, 0f);
                    LogicalFragmentId body = NewBranch(f, offset);
                    CutOperationId cut = Admit(f, body, Tilted(offset));
                    PublishPhysics(f, cut, out _, out _);
                    cuts.Add(cut);
                }

                f.commit.allowEverything = true;
                f.RunUntil(() => f.commit.committed.Count == 3, "all three cuts commit");

                Assert.That(f.commit.committed, Is.EquivalentTo(cuts), "each cut committed, once");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "every budget unit came back");

                for (int i = 0; i < 5; i++)
                {
                    f.Frame();
                }

                Assert.That(f.commit.committed.Count, Is.EqualTo(3), "pumping again commits nothing a second time");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and returns no unit a second time");
                foreach (CutOperationId cut in cuts)
                {
                    Assert.That(
                        f.ledger.CompleteGeometry(cut),
                        Is.EqualTo(LogicalCutResultOutcome.NotActive),
                        "a second notice for the same operation is refused");
                }

                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "so no unit is returned twice");
            }
        }

        // ----- 7. closing part way through ------------------------------------------------------------------------------------

        /// <summary>
        /// Closing while a kernel is with a worker leaves nothing behind: the work is not interrupted, and after the
        /// ordinary drain the input lease, the output reservation and the result have all gone back, with nothing
        /// committed.
        /// </summary>
        [Test]
        public void ClosingPartWayThrough_LeavesNoLeaseReservationOrResult()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, cut, out _, out _);
                f.commit.allowEverything = true;
                f.Frame();
                Assert.That(f.dag.StageOf(cut), Is.EqualTo(CutGeometryStage.Running), "its kernel is with the destination");

                f.dag.Dispose();
                f.ReleaseHeldWork();
                var clock = Stopwatch.StartNew();
                while (!f.dag.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    f.Frame();
                }

                Assert.That(f.dag.IsDrained, Is.True, "the closed DAG gave everything back");
                Assert.That(f.commit.committed, Is.Empty, "and committed nothing");
                Assert.That(f.dag.TryGetGeometry(body, out VpStoredGeometry geometry), Is.True, "the base geometry is untouched");
                Assert.That(f.storage.TryRetireIndices(geometry.indexRange), Is.True, "its range retires");
                Assert.That(
                    f.storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _) && state == VpIndexRangeState.Free,
                    Is.True,
                    "and is free at once, so the input lease came back");
            }
        }

        // ----- the admission rule the ledger owns --------------------------------------------------------------------------------

        /// <summary>
        /// A second cut of a source whose first operation is not published yet is passed over by the ledger's own
        /// rule, and nothing of it is kept here to be tried again later.
        /// </summary>
        [Test]
        public void ASecondCutOfAnUnpublishedSource_IsPassedOverAndNotKept()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId first = Admit(f, body, Tilted(float3.zero));
                int active = f.dag.ActiveCount;

                Assert.That(
                    f.dag.TryAdmit(body, Tilted(new float3(0.1f, 0f, 0f)), true, out CutOperationId second),
                    Is.EqualTo(LogicalCutAdmission.SourceActive),
                    "the second cut is passed over");

                Assert.That(second.IsSet, Is.False, "it has no operation");
                Assert.That(f.dag.ActiveCount, Is.EqualTo(active), "and nothing was registered for it");
                Assert.That(f.dag.StageOf(first), Is.Not.EqualTo(CutGeometryStage.Reclaimed), "the first cut is untouched");
            }
        }

        /// <summary>
        /// Closing after the kernel has finished but before anything was committed: the two sides it produced are not
        /// left in the storage with nobody owning them. They go back as the closed DAG is drained, with the storage
        /// still alive, and nothing is committed.
        /// </summary>
        [Test]
        public void ClosingWithAResultInHand_GivesTheProducedGeometryBack()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, cut, out _, out _);

                // The commit is not allowed, so the result stays in the DAG's hands.
                f.RunGatedUntil(() => f.dag.StageOf(cut) == CutGeometryStage.CpuPublished, "its kernel finishes");
                List<VpIndexRangeHandle> produced = ProducedRanges(f, cut);

                f.dag.Dispose();
                var clock = Stopwatch.StartNew();
                while (!f.dag.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    f.ReleaseHeldWork();
                    f.Frame();
                }

                Assert.That(f.dag.IsDrained, Is.True, "the closed DAG gave everything back");
                Assert.That(f.commit.committed, Is.Empty, "and committed nothing");
                AssertRangesAreFree(f, produced, "after closing with a result in hand");
            }
        }

        // ----- what an unadopted result is worth ---------------------------------------------------------------------

        /// <summary>
        /// A result that arrives for a branch that has gone is not just dropped: the two sides the kernel really
        /// produced are given back to the storage, so their index space can be used again, with the storage itself
        /// still alive and nothing else disposed.
        /// </summary>
        [Test]
        public void TheProducedSidesOfAnUnadoptedResult_GoBackToTheStorage()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, cut, out LogicalFragmentId positive, out LogicalFragmentId negative);

                f.RunGatedUntil(() => f.dag.StageOf(cut) == CutGeometryStage.CpuPublished, "the kernel finishes");
                List<VpIndexRangeHandle> produced = ProducedRanges(f, cut);
                foreach (VpIndexRangeHandle range in produced)
                {
                    Assert.That(
                        f.storage.TryGetIndexState(range, out VpIndexRangeState state, out _, out _) && state == VpIndexRangeState.Published,
                        Is.True,
                        "before anything is reclaimed the produced range is published");
                }

                Assert.That(f.ledger.Retire(positive) && f.ledger.Retire(negative), Is.True, "both branches retire");
                f.commit.allowEverything = true;
                f.Frame();

                Assert.That(f.commit.committed, Is.Empty, "nothing was committed");
                AssertRangesAreFree(f, produced, "after the branch went");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the budget unit came back");
            }
        }

        // ----- a geometry failure reaching the caller -----------------------------------------------------------------

        /// <summary>
        /// A geometry failure is not absorbed as an ordinary ending: it is reported to the caller once, with what
        /// happened, and it is still readable afterwards. The source is not retired for it and no physics is aborted.
        /// </summary>
        [Test]
        public void AGeometryFailure_IsReportedToTheCaller_AndIsNotAPhysicsFailure()
        {
            var failing = new FailingExecutor();
            using (Fixture f = NewFixture(geometryPool: failing))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, cut, out LogicalFragmentId positive, out _);
                f.commit.allowEverything = true;

                f.RunUntil(() => f.fault.faults.Count > 0, "the failure reaches the caller");

                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "reported once");
                Assert.That(f.fault.faults[0].operation, Is.EqualTo(cut), "for the cut it happened to");
                Assert.That(f.fault.faults[0].status, Is.EqualTo(VpStorageCutStatus.InternalError), "with what happened");
                Assert.That(f.fault.faults[0].failure, Is.SameAs(failing.thrown), "and what was thrown");
                Assert.That(f.commit.committed, Is.Empty, "nothing was committed");

                f.Frame();
                f.Frame();

                Assert.That(f.dag.FailureOf(cut), Is.EqualTo(VpStorageCutStatus.InternalError), "the failure is still readable");
                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "and is not reported again");
                Assert.That(
                    f.ledger.TryGetFragmentState(body, out LogicalFragmentState state) && state == LogicalFragmentState.Replaced,
                    Is.True,
                    "the source was not retired for a geometry failure");
                Assert.That(
                    f.ledger.TryGetFragmentState(positive, out LogicalFragmentState child) && child == LogicalFragmentState.Live,
                    Is.True,
                    "and its physics children are untouched");
            }
        }

        // ----- a branch that becomes unnecessary before its basis arrives -----------------------------------------------

        /// <summary>
        /// A→B with everything published, then every living fragment retires while B is still waiting for A's commit:
        /// both cuts end, nothing is left waiting, and both budget units come back.
        /// </summary>
        [Test]
        public void ABranchRetiringWhileACutWaitsForItsBasis_EndsThatCutToo()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out LogicalFragmentId aNegative);
                CutOperationId b = Admit(f, aPositive, Tilted(new float3(0.2f, 0f, 0f)));
                PublishPhysics(f, b, out LogicalFragmentId bPositive, out LogicalFragmentId bNegative);
                f.commit.allowEverything = true;

                f.Frame();
                Assert.That(f.dag.StageOf(b), Is.EqualTo(CutGeometryStage.WaitingForBasis), "B is waiting for A's commit");

                Assert.That(f.ledger.Retire(aNegative), Is.True, "every living fragment retires");
                Assert.That(f.ledger.Retire(bPositive), Is.True, "every living fragment retires");
                Assert.That(f.ledger.Retire(bNegative), Is.True, "every living fragment retires");

                f.RunGatedUntil(() => f.dag.ActiveCount == 0, "both cuts end");

                Assert.That(f.commit.committed, Is.Empty, "nothing was committed for a branch nobody reads");
                Assert.That(StateOf(f, a), Is.EqualTo(LogicalCutOperationState.Terminated), "A's duty ended");
                Assert.That(StateOf(f, b), Is.EqualTo(LogicalCutOperationState.Terminated), "and so did B's");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "both budget units came back");
                Assert.That(f.dag.ActiveCount, Is.Zero, "and no work is left");
            }
        }

        // ----- an empty side is an ordinary result ------------------------------------------------------------------------

        /// <summary>
        /// A plane that misses the geometry leaves one side empty, which is a normal result: a cut of that empty child
        /// runs no kernel and makes no dummy geometry, commits in the ordinary order after its own publication, and
        /// passes the emptiness on to its own children, so nothing waits for a basis that will never come.
        /// </summary>
        [Test]
        public void ACutOfAnEmptySide_CommitsAsEmptyAndPassesThatOn()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                var misses = SyntheticGeometry.Plane(new float3(0f, 1f, 0f), new float3(0f, 8f, 0f));
                CutOperationId a = Admit(f, body, misses);
                PublishPhysics(f, a, out LogicalFragmentId empty, out LogicalFragmentId kept);
                f.commit.allowEverything = true;

                f.RunGatedUntil(() => f.commit.committed.Count == 1, "the cut that missed commits");

                Assert.That(f.dag.HasNoGeometry(empty), Is.True, "the side with nothing in it has no geometry");
                Assert.That(f.dag.TryGetGeometry(kept, out _), Is.True, "and the side that kept it all has the geometry");

                // Cutting the empty child: no kernel, no dummy geometry, and it still goes through the commit.
                CutOperationId b = Admit(f, empty, Tilted(float3.zero));
                PublishPhysics(f, b, out LogicalFragmentId bPositive, out LogicalFragmentId bNegative);
                int accepted = f.gate.Accepted;
                f.Frame();

                Assert.That(f.gate.Accepted, Is.EqualTo(accepted), "no work was offered for a cut of nothing");
                Assert.That(f.commit.committed, Is.EqualTo(new[] { a, b }), "it committed all the same");
                Assert.That(f.commit.seen[1].IsEmpty, Is.True, "with both sides empty");
                Assert.That(StateOf(f, b), Is.EqualTo(LogicalCutOperationState.Completed), "its duty is done");
                Assert.That(f.dag.HasNoGeometry(bPositive) && f.dag.HasNoGeometry(bNegative), Is.True, "and both its children are empty too");
                Assert.That(
                    f.ledger.TryGetFragmentState(bPositive, out LogicalFragmentState state) && state == LogicalFragmentState.Live,
                    Is.True,
                    "the physics children are there as usual");

                // And one more generation, so that emptiness really travels rather than stopping at the first child.
                CutOperationId c = Admit(f, bPositive, Tilted(float3.zero));
                PublishPhysics(f, c, out _, out _);
                f.Frame();

                Assert.That(f.commit.committed.Count, Is.EqualTo(3), "the grandchild's cut commits as well");
                Assert.That(f.dag.ActiveCount, Is.Zero, "and nothing is left waiting for a basis");
            }
        }

        /// <summary>
        /// The caller's notification throwing does not leave the cut half ended: it was already reclaimed, so the
        /// later pumps find nothing stuck, report nothing a second time, and the failure is still readable.
        /// </summary>
        [Test]
        public void ANotificationThatThrows_StillLeavesTheCutEnded_AndIsNotRepeated()
        {
            var failing = new FailingExecutor();
            using (Fixture f = NewFixture(geometryPool: failing))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, cut, out _, out _);
                f.commit.allowEverything = true;
                f.fault.throws = new InvalidOperationException("the caller takes this to the common termination");

                Exception thrown = RunUntilTheNotificationThrows(f, "the failure is reported");

                Assert.That(thrown, Is.SameAs(f.fault.throws), "what the caller threw reached the pump");
                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "reported once");
                Assert.That(f.dag.ActiveCount, Is.Zero, "the cut was already ended before the notification");
                Assert.That(StateOf(f, cut), Is.EqualTo(LogicalCutOperationState.Terminated), "its duty ended");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the budget unit came back");

                for (int i = 0; i < 5; i++)
                {
                    f.Frame();
                }

                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "and the same failure is never reported again");
                Assert.That(f.dag.FailureOf(cut), Is.EqualTo(VpStorageCutStatus.InternalError), "it is still readable");
                Assert.That(f.commit.committed, Is.Empty, "nothing was committed");
            }
        }

        /// <summary>
        /// The same, before the operation is published: the notice to the ledger waits for that publication exactly as
        /// it does without a throwing notification, and the failure is still reported only once.
        /// </summary>
        [Test]
        public void ANotificationThatThrowsBeforePublication_KeepsTheNoticeForThePublication()
        {
            var failing = new FailingExecutor();
            using (Fixture f = NewFixture(geometryPool: failing))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId cut = Admit(f, body, Tilted(float3.zero));
                f.commit.allowEverything = true;
                f.fault.throws = new InvalidOperationException("the caller takes this to the common termination");

                Exception thrown = RunUntilTheNotificationThrows(f, "the failure is reported");

                Assert.That(thrown, Is.SameAs(f.fault.throws), "what the caller threw reached the pump");
                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "reported once");
                Assert.That(StateOf(f, cut), Is.EqualTo(LogicalCutOperationState.Admitted), "the operation is still only admitted");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "so its budget unit is still out");

                for (int i = 0; i < 3; i++)
                {
                    f.Frame();
                }

                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "nothing is reported again while it waits");

                PublishPhysics(f, cut, out _, out _);
                f.Frame();

                Assert.That(StateOf(f, cut), Is.EqualTo(LogicalCutOperationState.Terminated), "the notice was kept for the publication");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the budget unit came back once");
                Assert.That(f.fault.faults.Count, Is.EqualTo(1), "still reported only once");
                Assert.That(f.dag.ActiveCount, Is.Zero, "and nothing is left");
            }
        }

        // ----- 9. the adopted plane and the kernel's plane ---------------------------------------------------------

        /// <summary>
        /// A caller whose logical frame is the geometry's own coordinates is unaffected: the kernel is given the very
        /// plane that was admitted, and so is the commit.
        /// </summary>
        [Test]
        public void AnIdentityFrame_GivesTheKernelTheAdmittedPlaneUnchanged()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranch(f, float3.zero);
                float4 plane = Tilted(float3.zero);
                CutOperationId a = Admit(f, body, plane);
                PublishPhysics(f, a, out _, out _);
                f.commit.allowEverything = true;

                f.RunGatedUntil(() => f.dag.StageOf(a) != CutGeometryStage.WaitingForBasis, "the cut is offered");

                Assert.That(f.dag.TryGetKernelPlane(a, out float4 kernel), Is.True, "the kernel was given a plane");
                AssertPlaneIs(kernel, plane, "the plane the kernel was given is the one that was admitted");

                f.RunGatedUntil(() => f.commit.committed.Count == 1, "it commits");
                AssertPlaneIs(f.commit.seen[0].plane, plane, "and the commit publishes the same plane");
                Assert.That(
                    f.ledger.TryGetOperation(a, out LogicalCutOperation record) && record.plane.Equals(plane), Is.True,
                    "the ledger still holds the plane it adopted");
            }
        }

        /// <summary>
        /// A frame with a turn and a translation in it: the kernel is given the plane in the geometry's coordinates,
        /// worked out here from the frame rather than asked of the conversion, and the geometry that comes out is the
        /// same as a control cut at that plane directly, through a branch registered the identity way. The admitted
        /// plane and the converted one are genuinely different, which is what makes the comparison worth anything.
        /// </summary>
        [Test]
        public void AFrameWithATurnAndATranslation_CutsWhereTheSamePlaneGivenDirectlyCuts()
        {
            Matrix4x4 frame = TurnedFrame(new Vector3(0.25f, -0.4f, 0.15f));
            var admitted = new float4(0f, 1f, 0f, 0f);
            float4 byHand = PlaneInGeometryByHand(admitted, frame);

            // The quarter turn takes the plane's +y normal to -x, and the translation moves it off the origin.
            AssertPlaneIs(byHand, new float4(-1f, 0f, 0f, 0.25f), "the plane in the geometry's own coordinates");
            Assert.That(
                math.any(math.abs(byHand - admitted) > 1e-3f), Is.True,
                "the two planes are not the same, so cutting at the wrong one would show");

            string mapped;
            string control;
            using (Fixture f = NewFixture())
            {
                LogicalFragmentId body = NewBranchInFrame(f, frame);
                CutOperationId a = Admit(f, body, admitted);
                PublishPhysics(f, a, out _, out _);

                // Read while the cut is still holding it: a committed cut has been let go of by then.
                f.RunUntil(() => f.dag.TryGetKernelPlane(a, out _), "the cut is offered to the kernel");
                Assert.That(f.dag.TryGetKernelPlane(a, out float4 kernel), Is.True, "the kernel was given a plane");
                AssertPlaneIs(kernel, byHand, "the kernel cuts at the plane in the geometry's coordinates");

                f.commit.allowEverything = true;
                f.RunUntil(() => f.commit.committed.Count == 1, "the cut in the turned frame commits");
                mapped = SideShape(f, f.commit.seen[0].positive) + " | " + SideShape(f, f.commit.seen[0].negative)
                    + " | caps=" + f.commit.seen[0].capTriangles;
            }

            using (Fixture f = NewFixture())
            {
                // The control: the same box, no frame to speak of, cut at the geometry-local plane directly.
                LogicalFragmentId body = NewBranch(f, float3.zero);
                CutOperationId a = Admit(f, body, byHand);
                PublishPhysics(f, a, out _, out _);
                f.commit.allowEverything = true;
                f.RunUntil(() => f.commit.committed.Count == 1, "the control cut commits");
                control = SideShape(f, f.commit.seen[0].positive) + " | " + SideShape(f, f.commit.seen[0].negative)
                    + " | caps=" + f.commit.seen[0].capTriangles;
            }

            Assert.That(mapped, Is.EqualTo(control), "both cuts made the same two sides");
        }

        /// <summary>
        /// B is admitted and published on A's child while A's geometry is still unfinished, and only then does A
        /// commit. B is not held up for the geometry, and when its turn comes it is cut in the frame A's commit left
        /// the child in - the same frame, since this commit does not rebuild a child's coordinates.
        /// </summary>
        [Test]
        public void AFrameIsInheritedThroughTheCommit_ByACutAdmittedBeforeIt()
        {
            Matrix4x4 frame = TurnedFrame(new Vector3(0.25f, -0.4f, 0.15f));
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranchInFrame(f, frame);
                CutOperationId a = Admit(f, body, new float4(0f, 1f, 0f, 0f));
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out _);

                // Admitted and published on a child that has no geometry at all yet.
                var bPlane = new float4(1f, 0f, 0f, -0.2f);
                CutOperationId b = Admit(f, aPositive, bPlane);
                PublishPhysics(f, b, out _, out _);
                Assert.That(f.dag.TryGetGeometry(aPositive, out _), Is.False, "the child has no geometry when B is admitted");
                Assert.That(f.dag.TryGetKernelPlane(b, out _), Is.False, "and B has not been offered anything to cut");

                f.commit.allowEverything = true;
                f.RunGatedUntil(() => f.commit.committed.Count == 1, "A commits");

                Assert.That(
                    f.dag.TryGetGeometryFrame(aPositive, out Matrix4x4 childFrame), Is.True,
                    "the child is in a frame of its own from the commit on");
                Assert.That(childFrame, Is.EqualTo(frame), "which is the frame its basis was read in");

                Assert.That(f.dag.TryGetKernelPlane(b, out float4 kernel), Is.True, "B became ready in that same update");
                AssertPlaneIs(
                    kernel, PlaneInGeometryByHand(bPlane, frame), "B was cut in the frame it inherited, not in the lineage's");

                f.RunGatedUntil(() => f.commit.committed.Count == 2, "B commits after it");
                Assert.That(f.commit.committed, Is.EqualTo(new[] { a, b }), "in that order");
            }
        }

        /// <summary>
        /// Converting for the kernel does not write the converted value anywhere else: the ledger's adopted plane and
        /// the plane each commit publishes stay the values that were admitted, for the child's cut as well as the
        /// first one.
        /// </summary>
        [Test]
        public void TheConvertedPlane_DoesNotReplaceTheAdoptedOneOrTheCommitsOwn()
        {
            Matrix4x4 frame = TurnedFrame(new Vector3(0.25f, -0.4f, 0.15f));
            using (Fixture f = NewFixture())
            {
                LogicalFragmentId body = NewBranchInFrame(f, frame);
                var aPlane = new float4(0f, 1f, 0f, 0f);
                CutOperationId a = Admit(f, body, aPlane);
                PublishPhysics(f, a, out LogicalFragmentId aPositive, out _);
                f.commit.allowEverything = true;
                f.RunUntil(() => f.commit.committed.Count == 1, "the first cut commits");

                var bPlane = new float4(1f, 0f, 0f, -0.2f);
                CutOperationId b = Admit(f, aPositive, bPlane);
                PublishPhysics(f, b, out _, out _);
                f.RunUntil(() => f.commit.committed.Count == 2, "the child's cut commits");

                AssertPlaneIs(f.commit.seen[0].plane, aPlane, "the first commit publishes the plane that was admitted");
                AssertPlaneIs(f.commit.seen[1].plane, bPlane, "and so does the child's");
                Assert.That(
                    f.ledger.TryGetOperation(a, out LogicalCutOperation first) && first.plane.Equals(aPlane), Is.True,
                    "the ledger's adopted plane is untouched");
                Assert.That(
                    f.ledger.TryGetOperation(b, out LogicalCutOperation second) && second.plane.Equals(bPlane), Is.True,
                    "for the child's operation too");
            }
        }

        /// <summary>
        /// A plane that misses the geometry: one side is the input borrowed back and the other is empty. Both keep the
        /// frame, so a later cut of either is still read in the geometry's coordinates - the borrowed side is cut
        /// there, and the empty side passes it on to its own children.
        /// </summary>
        [Test]
        public void AnEmptySideAndABorrowedInput_KeepTheFrameForWhatComesNext()
        {
            Matrix4x4 frame = TurnedFrame(new Vector3(0.25f, -0.4f, 0.15f));
            using (Fixture f = NewFixture())
            {
                LogicalFragmentId body = NewBranchInFrame(f, frame);

                // Far off in the lineage's frame, and still far off once carried into the geometry's.
                // Carried into the geometry's coordinates this is x = -7.75, which the box is wholly on one side of:
                // the positive side comes back empty and the negative one is the input borrowed back.
                CutOperationId a = Admit(f, body, new float4(0f, 1f, 0f, -8f));
                PublishPhysics(f, a, out LogicalFragmentId empty, out LogicalFragmentId kept);
                f.commit.allowEverything = true;
                f.RunUntil(() => f.commit.committed.Count == 1, "the cut that missed commits");

                Assert.That(f.dag.TryGetGeometry(kept, out _), Is.True, "one side kept the geometry");
                Assert.That(f.dag.HasNoGeometry(empty), Is.True, "and the other has none");
                Assert.That(
                    f.dag.TryGetGeometryFrame(kept, out Matrix4x4 keptFrame) && keptFrame == frame, Is.True,
                    "the side that borrowed the input is still in the same frame");
                Assert.That(
                    f.dag.TryGetGeometryFrame(empty, out Matrix4x4 emptyFrame) && emptyFrame == frame, Is.True,
                    "and so is the empty one");

                // The borrowed side, cut again: still converted, not taken as if the frames were the same.
                var bPlane = new float4(1f, 0f, 0f, -0.2f);
                CutOperationId b = Admit(f, kept, bPlane);
                PublishPhysics(f, b, out _, out _);
                f.RunUntil(() => f.dag.TryGetKernelPlane(b, out _), "the borrowed side is offered to the kernel");
                Assert.That(f.dag.TryGetKernelPlane(b, out float4 kernel), Is.True, "its kernel was given a plane");
                AssertPlaneIs(kernel, PlaneInGeometryByHand(bPlane, frame), "in the frame the side kept");
                f.RunUntil(() => f.commit.committed.Count == 2, "the borrowed side is cut again");

                // The empty side, cut again: no kernel, and the frame travels on to its children all the same.
                CutOperationId c = Admit(f, empty, bPlane);
                PublishPhysics(f, c, out LogicalFragmentId cPositive, out LogicalFragmentId cNegative);
                f.RunUntil(() => f.commit.committed.Count == 3, "the empty side commits as empty");
                Assert.That(f.dag.TryGetKernelPlane(c, out _), Is.False, "a cut of nothing has no kernel plane");
                Assert.That(
                    f.dag.TryGetGeometryFrame(cPositive, out Matrix4x4 cFrame) && cFrame == frame, Is.True,
                    "its children keep the correspondence");
                Assert.That(f.dag.TryGetGeometryFrame(cNegative, out _), Is.True, "both of them");
            }
        }

        /// <summary>
        /// A basis is the geometry and the frame it was read in together. With the result already in hand, the source
        /// is registered again with the same geometry in a different frame: the result was cut somewhere else now, so
        /// it is not adopted, and nothing is committed at the wrong plane.
        /// </summary>
        [Test]
        public void AResultCutInAFrameTheSourceNoLongerHas_IsNotAdopted()
        {
            using (Fixture f = NewFixture(gated: true))
            {
                LogicalFragmentId body = NewBranchInFrame(f, Matrix4x4.identity);
                CutOperationId a = Admit(f, body, Tilted(float3.zero));
                PublishPhysics(f, a, out _, out _);

                f.RunGatedUntil(() => f.dag.StageOf(a) == CutGeometryStage.CpuPublished, "the result is in hand");
                Assert.That(f.dag.TryGetGeometry(body, out VpStoredGeometry basis), Is.True, "the source still has its geometry");

                // The same geometry, said to be in a different frame from now on.
                f.dag.RegisterBaseGeometry(body, basis, TurnedFrame(new Vector3(0.25f, -0.4f, 0.15f)));
                f.commit.allowEverything = true;
                f.Frame();

                Assert.That(f.commit.committed, Is.Empty, "the result is not committed");
                Assert.That(f.dag.StageOf(a), Is.EqualTo(CutGeometryStage.Reclaimed), "the cut ended without one");
                Assert.That(
                    StateOf(f, a), Is.EqualTo(LogicalCutOperationState.Terminated),
                    "and the ledger was told once, the ordinary way");
            }
        }

        /// <summary>A destination that accepts work and always brings it back as failed, without running it.</summary>
        private sealed class FailingExecutor : IWorkExecutor
        {
            internal readonly Exception thrown = new InvalidOperationException("the worker fell over");
            private readonly Queue<IDispatchWork> _held = new Queue<IDispatchWork>();
            private bool _closed;

            public WorkDestination Destination => WorkDestination.GeometryPool;

            public int Capacity => 4;

            public int Held => _held.Count;

            public bool CanAccept => !_closed && _held.Count < Capacity;

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _held.Enqueue(work);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (_held.Count == 0)
                {
                    work = null;
                    completion = default;
                    return false;
                }

                work = _held.Dequeue();
                completion = WorkCompletion.Failed(thrown);
                return true;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                return true;
            }
        }

        /// <summary>
        /// A destination the test starts by hand: it accepts work and holds it, and runs it — really on another
        /// thread, once, to the end — only when the test says so.
        /// </summary>
        private sealed class GatedExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _accepted = new List<IDispatchWork>();
            private readonly Queue<KeyValuePair<IDispatchWork, WorkCompletion>> _finished =
                new Queue<KeyValuePair<IDispatchWork, WorkCompletion>>();
            private bool _closed;

            public WorkDestination Destination => WorkDestination.GeometryPool;

            public int Capacity => 8;

            public int Held => _accepted.Count + _finished.Count;

            public bool CanAccept => !_closed && Held < Capacity;

            /// <summary>How many pieces of work are held and not begun.</summary>
            public int Accepted => _accepted.Count;

            /// <summary>
            /// How many pieces of work this has taken since it was made. Unlike <see cref="Accepted"/> this does not
            /// go down again when one is run, so a case can say that something was taken **after** a given moment.
            /// </summary>
            public int TotalAccepted { get; private set; }

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _accepted.Add(work);
                TotalAccepted++;
                return true;
            }

            /// <summary>Runs everything held and not begun, each on a worker of its own, and waits for those threads.</summary>
            public void RunEverythingWaiting()
            {
                while (_accepted.Count > 0)
                {
                    RunOneOnAWorker();
                }
            }

            public void BeginAccepted(IDispatchWork work)
            {
            }

            public void RunOneOnAWorker()
            {
                Assert.That(_accepted.Count, Is.GreaterThan(0), "there is work to run");
                IDispatchWork work = _accepted[0];
                _accepted.RemoveAt(0);
                Exception failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        work.Begin();
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                });
                thread.Start();
                Assert.That(thread.Join(DeadlineMilliseconds), Is.True, "the worker finished");
                _finished.Enqueue(new KeyValuePair<IDispatchWork, WorkCompletion>(
                    work, failure == null ? WorkCompletion.Finished : WorkCompletion.Failed(failure)));
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (_finished.Count == 0)
                {
                    work = null;
                    completion = default;
                    return false;
                }

                KeyValuePair<IDispatchWork, WorkCompletion> ended = _finished.Dequeue();
                work = ended.Key;
                completion = ended.Value;
                return true;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                for (int i = 0; i < _accepted.Count; i++)
                {
                    _finished.Enqueue(new KeyValuePair<IDispatchWork, WorkCompletion>(_accepted[i], WorkCompletion.Cancelled));
                }

                _accepted.Clear();
                return true;
            }
        }
    }
}
