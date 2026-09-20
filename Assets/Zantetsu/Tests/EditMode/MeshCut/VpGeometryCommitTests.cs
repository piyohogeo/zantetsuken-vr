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
    /// The cut DAG committing real geometry into the display (DESIGN 4.5.6): the kernel really runs, the two sides are
    /// really transferred, and the body they were cut from is really replaced by them, with that cut's temporary clip
    /// and cap gone and every later cut still temporary on the new bodies.
    /// <para>
    /// The physics side is synthetic throughout — the ledger's own publication stands for it — and no convex cut, cook
    /// or actor exists here. Everything else is the product path: the storage, the asynchronous cut, the transfer, the
    /// display and its collection.
    /// </para>
    /// </summary>
    public class VpGeometryCommitTests
    {
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int DeadlineMilliseconds = 30000;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private int _frame;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
        }

        [TearDown]
        public void DestroyObjects()
        {
            foreach (UnityEngine.Object tracked in _objects)
            {
                if (tracked != null)
                {
                    UnityEngine.Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        // ----- fixture ---------------------------------------------------------------------------------------------

        private sealed class Fixture : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public VpGeometryReferenceTable table;
            public LogicalCutLedger ledger;
            public VpLogicalCutDisplay display;
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor pool;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public VpDisplayGeometryCommit commit;
            public ThrowingFault fault;
            public CutDag dag;
            public Func<int> frame;
            private int _dispatchFrame;

            /// <summary>
            /// One turn of the caller's loop, without a collection. The dispatcher counts its own frames, because its
            /// budget refills per frame and the display's frame counter is the test's to move.
            /// </summary>
            public void Pump()
            {
                dispatcher.BeginFrame(++_dispatchFrame);
                dispatcher.Dispatch();
                dag.Pump();
            }

            /// <summary>A frame that also collects: the display settles what the commits have made of the registrations.</summary>
            public bool Collect()
            {
                return display.TryBeginFrame();
            }

            public void RunUntil(Func<bool> condition, string what)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Pump();
                    if (condition())
                    {
                        return;
                    }

                    Thread.Sleep(1);
                }

                Assert.Fail(what + ": it had not happened when the deadline passed");
            }

            public void Dispose()
            {
                dag?.Dispose();
                var clock = Stopwatch.StartNew();
                while (!dag.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Pump();
                }

                dispatcher?.Shutdown(DeadlineMilliseconds);
                dag?.Pump();
                display?.Dispose();
                pool?.Dispose();
                background?.Dispose();
                storage?.Dispose();
            }
        }

        /// <summary>A geometry failure is not expected in these cases; if one arrives, the test says so.</summary>
        private sealed class ThrowingFault : ICutGeometryFault
        {
            internal readonly List<CutGeometryFault> faults = new List<CutGeometryFault>();

            public void GeometryFailed(in CutGeometryFault fault)
            {
                faults.Add(fault);
                Assert.Fail("the geometry failed: " + fault);
            }
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            return new Dictionary<int, Material>
            {
                { SideMaterial, Track(new Material(shader) { name = "side" }) },
                { EndMaterial, Track(new Material(shader) { name = "end" }) },
            };
        }

        private Fixture NewFixture(
            int commandCapacity = 32,
            int instanceCapacity = 32,
            int geometryCapacity = 64,
            int displayInstanceCapacity = 64)
        {
            var f = new Fixture
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 128, 512, 512, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8)),
                job = new UnityJobWorkExecutor(4),
                pool = WorkerPoolExecutor.GeometryPool(4),
                background = WorkerPoolExecutor.BackgroundPool(2),
                fault = new ThrowingFault(),
            };
            f.table = new VpGeometryReferenceTable(f.storage, geometryCapacity, displayInstanceCapacity);
            f.frame = () => _frame;
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    f.storage, f.table, f.ledger, Materials(), null, null, commandCapacity, instanceCapacity,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth,
                    VpStencilTestSettings.Create(), f.frame, out VpLogicalCutDisplay display),
                Is.True,
                "the display is created");
            f.display = display;
            f.dispatcher = new SharedWorkDispatcher(8, 2, 32, f.job, f.pool, f.background);
            f.commit = new VpDisplayGeometryCommit(display);
            f.dag = new CutDag(f.storage, f.ledger, f.dispatcher, f.commit, f.fault);
            return f;
        }

        // A closed box on eight corners: four side quads in one submesh and the two ends in another, so the two
        // submeshes are of different sizes and a mix-up between them would show.
        private static readonly float3[] k_corners =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(-1f, -1f, 1f),
            new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        /// <summary>A closed box with two submeshes of different sizes, appended as a cut input.</summary>
        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage, float3 offset)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int face = 0; face < k_faces.Length; face++)
                {
                    if (k_faces[face].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[face].cycle;
                    float3 normal = math.normalize(math.cross(
                        k_corners[c[1]] - k_corners[c[0]], k_corners[c[2]] - k_corners[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = k_corners[c[k]] + offset, normal = normal, uv0 = uv[k] });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(
                    start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                submeshes[0].indexCount, Is.Not.EqualTo(submeshes[1].indexCount),
                "the two submeshes are of different sizes, so a mix-up would show");
            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length, submeshes.ToArray(),
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the box is appended as a cut input");
            return geometry;
        }

        private static float4 Level(float y)
        {
            return new float4(0f, 1f, 0f, -y);
        }

        private static CutOperationId Admit(Fixture f, LogicalFragmentId source, float4 plane)
        {
            Assert.That(
                f.dag.TryAdmit(source, plane, true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted),
                "the cut is admitted");
            return operation;
        }

        private static void PublishPhysics(Fixture f, CutOperationId operation, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            Assert.That(
                f.ledger.PrepareAnchorDistribution(operation, 1e-5f, out _),
                Is.EqualTo(AnchorPreparationOutcome.Prepared),
                "the anchors are distributed");
            Assert.That(
                f.dag.PublishAfterFinalPhysics(operation, out positive, out negative),
                Is.EqualTo(LogicalCutResultOutcome.Applied),
                "the cut is published");
        }

        /// <summary>The index ranges the display is drawing from, in order.</summary>
        private static List<(int start, int count)> DrawnRanges(VpLogicalCutDisplay display)
        {
            var ranges = new List<(int, int)>();
            for (int i = 0; i < display.DrawCommandCount; i++)
            {
                Assert.That(display.TryGetDrawCommand(i, out VpIndirectCommand command), Is.True);
                ranges.Add((command.range.indexStart, command.range.indexCount));
            }

            return ranges;
        }

        private static int IndexCountOf(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out int count), Is.True);
            Assert.That(state, Is.EqualTo(VpIndexRangeState.Published), "the side is published");
            return count;
        }

        /// <summary>Where one shown fragment's geometry stands, as a position.</summary>
        private static Vector3 PlacementOf(Fixture f, LogicalFragmentId fragment)
        {
            Assert.That(f.display.TryGetShownPlacement(fragment, out Matrix4x4 placement), Is.True, "the fragment is shown");
            return placement.GetColumn(3);
        }

        /// <summary>
        /// Where one branch of the adopted snapshot really is drawn: the placement of the render fragment that is
        /// drawn for it, which is the one it follows. What a commit must keep unchanged. Read from the snapshot and
        /// not from the registration, because a commit changes which registration a side belongs to and that is
        /// exactly what must not show.
        /// </summary>
        private static Vector3 TotalOf(Fixture f, LogicalFragmentId fragment)
        {
            for (int r = 0; r < f.display.RenderFragmentCount; r++)
            {
                Assert.That(f.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    return rf.geometryLocalToWorld.GetColumn(3);
                }
            }

            Assert.Fail("nothing is drawn for that fragment");
            return default;
        }

        private static void AssertClose(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That(
                (actual - expected).magnitude, Is.LessThan(1e-4f),
                what + ": expected " + expected.ToString("F5") + ", was " + actual.ToString("F5"));
        }

        private static int CapRecordsOf(VpLogicalCutDisplay display, CutOperationId operation)
        {
            int found = 0;
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                Assert.That(display.TryGetCapRecord(i, out LogicalCutCapRecord record), Is.True);
                if (record.operation == operation)
                {
                    found++;
                }
            }

            return found;
        }

        // ----- 1. one cut ----------------------------------------------------------------------------------------------

        /// <summary>
        /// One cut, all the way through: the geometry is ready before the logical publication and is not committed for
        /// it; once published it commits, the two real sides take the body's place, and the temporary cap of that cut
        /// is gone from what is drawn. The sides really carry the cut's own faces, so together they hold more indices
        /// than the body did.
        /// </summary>
        [Test]
        public void OneCut_IsCommittedOnlyAfterThePublication_AndReplacesTheBodyWithItsTwoRealSides()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                int bodyIndices = IndexCountOf(f.storage, body);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");
                int transfersAfterShow = f.display.IndexTransfers;

                CutOperationId cut = Admit(f, fragment, Level(0f));
                f.RunUntil(() => f.dag.StageOf(cut) == CutGeometryStage.CpuPublished, "the kernel finishes");

                Assert.That(f.commit.Commits, Is.Zero, "nothing is committed before the publication");
                Assert.That(f.display.ShownCount, Is.EqualTo(1), "the body is still the one thing shown");

                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the temporary split settles");
                Assert.That(CapRecordsOf(f.display, cut), Is.GreaterThan(0), "and it is drawn as a temporary cap");

                f.Pump();

                Assert.That(f.commit.Commits, Is.EqualTo(1), "the publication released the commit");
                Assert.That(f.display.IndexTransfers, Is.EqualTo(transfersAfterShow + 1), "the two sides went across in one transfer");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the collection after the commit settles");

                Assert.That(f.display.ShownCount, Is.EqualTo(2), "the two sides took the body's place");
                Assert.That(CapRecordsOf(f.display, cut), Is.Zero, "the temporary cap of that cut is gone");
                Assert.That(f.dag.TryGetGeometry(above, out VpStoredGeometry positive), Is.True);
                Assert.That(f.dag.TryGetGeometry(below, out VpStoredGeometry negative), Is.True);
                int sides = IndexCountOf(f.storage, positive) + IndexCountOf(f.storage, negative);
                Assert.That(sides, Is.GreaterThan(bodyIndices), "the two sides carry the cut's own faces as well");

                List<(int start, int count)> drawn = DrawnRanges(f.display);
                int drawnIndices = 0;
                foreach ((int start, int count) in drawn)
                {
                    drawnIndices += count;
                }

                Assert.That(drawnIndices, Is.EqualTo(sides), "and what is drawn is exactly those two sides");
                Assert.That(
                    f.ledger.TryGetOperation(cut, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Completed,
                    Is.True,
                    "the ledger took the geometry notice once");
            }
        }

        // ----- 2. A then B ----------------------------------------------------------------------------------------------

        /// <summary>
        /// A cut, and a cut of its child before the first has any geometry: the display goes from the whole body with
        /// both cuts temporary, to A's two real sides with B still temporary on one of them, to B's two real sides. B
        /// is cut from what A's commit gave its child, and that child is never made live again.
        /// </summary>
        [Test]
        public void ACutOfAChild_FollowsTheAncestorCommit_AndIsTemporaryUntilItsOwn()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");

                CutOperationId a = Admit(f, fragment, Level(0f));
                PublishPhysics(f, a, out LogicalFragmentId above, out LogicalFragmentId below);
                CutOperationId b = Admit(f, above, Level(0.5f));
                PublishPhysics(f, b, out LogicalFragmentId top, out LogicalFragmentId middle);

                Assert.That(f.display.TryBeginFrame(), Is.True, "G0 with both cuts temporary settles");
                Assert.That(f.display.ShownCount, Is.EqualTo(1), "one body is shown");
                Assert.That(CapRecordsOf(f.display, a), Is.GreaterThan(0), "A is temporary");
                Assert.That(CapRecordsOf(f.display, b), Is.GreaterThan(0), "and so is B");

                f.RunUntil(() => f.commit.Commits == 1, "A commits");

                Assert.That(f.dag.TryGetGeometry(above, out VpStoredGeometry aPositive), Is.True, "A's child has geometry");
                Assert.That(f.dag.BasisOf(b), Is.EqualTo(aPositive), "B is cut from what A's commit gave that child");
                Assert.That(
                    f.ledger.TryGetFragmentState(above, out LogicalFragmentState state) && state == LogicalFragmentState.Replaced,
                    Is.True,
                    "and that child is not made live again");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "GA with B still temporary settles");
                Assert.That(f.display.ShownCount, Is.EqualTo(2), "A's two sides are shown");
                Assert.That(CapRecordsOf(f.display, a), Is.Zero, "A is no longer temporary");
                Assert.That(CapRecordsOf(f.display, b), Is.GreaterThan(0), "B still is, on the new body");

                f.RunUntil(() => f.commit.Commits == 2, "B commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "GAB settles");

                Assert.That(f.display.ShownCount, Is.EqualTo(3), "B's two sides and A's other side are shown");
                Assert.That(CapRecordsOf(f.display, b), Is.Zero, "and nothing of the branch is temporary any more");
                Assert.That(f.dag.TryGetGeometry(top, out _), Is.True, "B's positive child has geometry");
                Assert.That(f.dag.TryGetGeometry(middle, out _), Is.True, "and so has its negative child");
                Assert.That(f.dag.TryGetGeometry(below, out _), Is.True, "A's other side still has its own");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "both cuts are done");
            }
        }

        // ----- 3. an empty side and a borrowed input ---------------------------------------------------------------------

        /// <summary>
        /// A plane that misses the body: one side is the input borrowed back and keeps the registration it already
        /// had, the other is empty and is shown as nothing at all — no renderer and no stand-in geometry — and neither
        /// costs a transfer. The empty child's own cut then commits as empty as well, and the logical children of both
        /// are untouched.
        /// </summary>
        [Test]
        public void APlaneThatMisses_BorrowsTheBodyAndShowsNothingForTheEmptySide()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");
                int indexTransfers = f.display.IndexTransfers;
                int vertexTransfers = f.display.VertexTransfers;
                List<(int start, int count)> before = DrawnRanges(f.display);

                CutOperationId cut = Admit(f, fragment, Level(8f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.RunUntil(() => f.commit.Commits == 1, "the cut that missed commits");

                Assert.That(f.display.IndexTransfers, Is.EqualTo(indexTransfers), "nothing was transferred for a side that was borrowed");
                Assert.That(f.display.VertexTransfers, Is.EqualTo(vertexTransfers), "nor any vertex");
                Assert.That(f.dag.HasNoGeometry(above), Is.True, "the side above the plane is empty");
                Assert.That(f.dag.TryGetGeometry(below, out VpStoredGeometry kept), Is.True, "the side below keeps it all");
                Assert.That(kept.indexRange, Is.EqualTo(body.indexRange), "which is the body itself, borrowed");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the collection after the commit settles");
                Assert.That(f.display.ShownCount, Is.EqualTo(1), "one registration, for the side that kept it all");
                Assert.That(DrawnRanges(f.display), Is.EqualTo(before), "drawn from exactly the same range as before");
                Assert.That(
                    f.ledger.TryGetFragmentState(above, out LogicalFragmentState state) && state == LogicalFragmentState.Live,
                    Is.True,
                    "the empty side's logical child is there as usual");

                // A cut of the empty child: no kernel, no geometry, and nothing shown for it.
                CutOperationId second = Admit(f, above, Level(0f));
                PublishPhysics(f, second, out LogicalFragmentId emptyTop, out LogicalFragmentId emptyBottom);
                f.RunUntil(() => f.commit.Commits == 2, "the cut of the empty side commits");

                Assert.That(f.dag.HasNoGeometry(emptyTop) && f.dag.HasNoGeometry(emptyBottom), Is.True, "both its sides are empty");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "and the collection settles");
                Assert.That(f.display.ShownCount, Is.EqualTo(1), "still only the one body is shown");
                Assert.That(f.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "both cuts are done");
            }
        }

        // ----- 4. one branch retired, and a commit that is not established -------------------------------------------------

        /// <summary>
        /// A commit the display cannot take is an ordinary refusal: what is shown does not move, the cut keeps its
        /// result, and when the branch then goes the result is reclaimed rather than left behind.
        /// </summary>
        [Test]
        public void ACommitTheDisplayRefuses_ChangesNothingShown_AndTheResultIsNotLeftBehind()
        {
            // Room for the body's two commands and no more, so the two sides cannot be taken in.
            using (Fixture f = NewFixture(commandCapacity: 2, instanceCapacity: 8))
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");
                List<(int start, int count)> before = DrawnRanges(f.display);

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.RunUntil(() => f.commit.Refusals > 0, "the display refuses the commit");

                Assert.That(f.commit.Commits, Is.Zero, "nothing was committed");
                Assert.That(f.dag.StageOf(cut), Is.EqualTo(CutGeometryStage.CpuPublished), "the cut still holds its result");
                Assert.That(f.display.ShownCount, Is.EqualTo(1), "what is shown has not moved");
                Assert.That(DrawnRanges(f.display), Is.EqualTo(before), "and neither has what is drawn");

                Assert.That(f.dag.TryGetResultBeforeCommit(cut, out VpStorageCutResult result), Is.True, "the result is in hand");
                VpIndexRangeHandle positiveRange = result.positive.geometry.indexRange;
                VpIndexRangeHandle negativeRange = result.negative.geometry.indexRange;

                Assert.That(f.ledger.Retire(above) && f.ledger.Retire(below), Is.True, "both branches retire");
                f.Pump();

                Assert.That(f.dag.ActiveCount, Is.Zero, "the cut is over");
                foreach (VpIndexRangeHandle range in new[] { positiveRange, negativeRange })
                {
                    Assert.That(
                        f.storage.TryGetIndexState(range, out VpIndexRangeState state, out _, out _) && state == VpIndexRangeState.Free,
                        Is.True,
                        "and the geometry nobody adopted went back to the storage");
                }
            }
        }

        /// <summary>
        /// One branch retiring does not stop the commit the other branch needs: the cut commits, the side that retired
        /// stops being shown at the next collection, and the living side is drawn from its own new geometry.
        /// </summary>
        [Test]
        public void OneBranchRetiring_StillCommitsForTheBranchThatIsLeft()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                Assert.That(f.ledger.Retire(above), Is.True, "one branch retires at once");

                f.RunUntil(() => f.commit.Commits == 1, "the cut still commits");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the collection after the commit settles");
                Assert.That(f.display.ShownCount, Is.EqualTo(1), "only the living side is shown");
                Assert.That(f.dag.TryGetGeometry(below, out VpStoredGeometry living), Is.True);
                List<(int start, int count)> drawn = DrawnRanges(f.display);
                int drawnIndices = 0;
                foreach ((int start, int count) in drawn)
                {
                    drawnIndices += count;
                }

                Assert.That(drawnIndices, Is.EqualTo(IndexCountOf(f.storage, living)), "drawn from its own new geometry");
            }
        }

        // ----- what is actually drawn, in pixels ------------------------------------------------------------------------

        /// <summary>
        /// A cut that is only prepared changes nothing outside the body. The same body is drawn before the cut is
        /// admitted and after it is prepared, from a camera that sees its flank across the cut, and the two images are
        /// the same pixel for pixel: no gap is opened, no sliver of the marker behind it appears, and the covered area
        /// does not change. This is the accepted consequence of the removal -- nothing displaces a side for the
        /// display, and no placement of its own has been given to either side here.
        /// </summary>
        [Test]
        public void APreparedCut_LeavesTheOutsideOfTheBodyExactlyAsItWas()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True);
                Assert.That(f.display.TryBeginFrame(), Is.True, "the whole body settles");

                Camera camera = FromTheSide();
                GameObject marker = Marker();
                Color32[] whole = DrawAndRead(f, camera);
                int wholeCovered = Covered(whole);
                int wholeMarker = Count(whole, IsMarker);
                TestContext.WriteLine(
                    "whole body: covered " + wholeCovered + " px, marker behind " + wholeMarker + " px");
                Assert.That(wholeCovered, Is.GreaterThan(Size * Size / 16), "the body really is in view");
                Assert.That(wholeMarker, Is.GreaterThan(Size * Size / 16), "and the marker is visible around it");

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PrepareAnchors(f, cut);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the prepared cut settles");
                Assert.That(f.display.SideCount, Is.EqualTo(2 * f.display.DrawCommandCount), "drawn as two sides now");

                Color32[] prepared = DrawAndRead(f, camera);
                TestContext.WriteLine(
                    "prepared cut: covered " + Covered(prepared) + " px, marker behind " + Count(prepared, IsMarker) + " px");
                Assert.That(Covered(prepared), Is.EqualTo(wholeCovered), "the covered area is the same");
                Assert.That(Count(prepared, IsMarker), Is.EqualTo(wholeMarker), "nothing behind it shows through");
                AssertSamePixels(whole, prepared, "a cut that is only prepared");
                Assert.That(cut.IsSet, Is.True);
                _frame++;
            }
        }

        /// <summary>
        /// Published, and then given a base placement of its own for each side through the display's placement
        /// lookup -- **test input, not a physics owner**: this unit does not connect Provisional publication to
        /// owners. What is checked is the product's drawing path from those placements. The gap between the two sides
        /// is real and read where it is: in the band they used to meet in, the body fills it while both are at the
        /// one placement and nothing of the body is in it once the free side is placed 1.2 higher, with the marker
        /// behind showing through instead. The total covered area is the same either way -- both sides are still
        /// drawn whole -- so an opened gap is told apart from a side that stopped being drawn. Nothing here is a
        /// display offset: take the placements away and the image is the one above.
        /// </summary>
        [Test]
        public void PublishedSidesAtTheirOwnOwners_LeaveARealGapBetweenThem()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True);
                Assert.That(f.display.TryBeginFrame(), Is.True);

                Camera camera = FromTheSide();
                GameObject marker = Marker();
                CutOperationId cut = Admit(f, fragment, Level(0f));
                PrepareAnchors(f, cut);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True);
                Assert.That(f.display.SideCount, Is.EqualTo(2 * f.display.DrawCommandCount), "drawn as two sides");
                Color32[] together = DrawAndRead(f, camera);

                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.display.Placement = new VpTestPlacements()
                    .Put(above, Matrix4x4.Translate(new Vector3(0f, 1.2f, 0f)))
                    .Static(below);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the published sides settle");

                Color32[] apart = DrawAndRead(f, camera);
                // The band the two sides met in, clear of either cap: the cut is at y = 0 and the free side's own
                // cap lands at y = 1.2.
                const float bandLow = 0.15f;
                const float bandHigh = 1.05f;
                int filledBefore = CoveredInBand(together, bandLow, bandHigh);
                int filledAfter = CoveredInBand(apart, bandLow, bandHigh);
                int seenThroughBefore = MarkerInBand(together, bandLow, bandHigh);
                int seenThroughAfter = MarkerInBand(apart, bandLow, bandHigh);
                TestContext.WriteLine(
                    "at one placement: covered " + Covered(together) + " px total, " + filledBefore + " px in the band, "
                    + seenThroughBefore + " px of marker in the band; placed apart: covered " + Covered(apart)
                    + " px total, " + filledAfter + " px in the band, " + seenThroughAfter + " px of marker in the band");

                Assert.That(filledBefore, Is.GreaterThan(Size * Size / 16), "at one placement the body fills that band");
                Assert.That(filledAfter, Is.Zero, "and placed apart nothing of it is there: the gap is real");
                Assert.That(
                    seenThroughAfter, Is.GreaterThan(seenThroughBefore),
                    "the marker behind shows through it");
                Assert.That(
                    Covered(apart), Is.EqualTo(Covered(together)),
                    "and the same area is covered in all: both sides are drawn whole, one of them elsewhere");
                Assert.That(marker != null, Is.True);
                _frame++;
            }
        }

        /// <summary>
        /// A commit takes in no movement of its own: the same image before and after it, pixel for pixel. The two
        /// sides are at base placements that differ, given as test input, so this is not two coincident halves
        /// agreeing with themselves; the kernel, the transfer and the commit are the product's -- the body really is
        /// replaced by the two geometries the kernel made.
        /// </summary>
        [Test]
        public void ACommit_DoesNotMoveWhatIsDrawn_InPixels()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True);
                Assert.That(f.display.TryBeginFrame(), Is.True);

                Camera camera = FromTheSide();
                GameObject marker = Marker();
                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.display.Placement = new VpTestPlacements()
                    .Put(above, Matrix4x4.Translate(new Vector3(0f, 1.2f, 0f)))
                    .Static(below);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the published sides settle");

                Color32[] temporary = DrawAndRead(f, camera);
                Assert.That(Covered(temporary), Is.GreaterThan(Size * Size / 32), "something is drawn to compare");

                f.RunUntil(() => f.commit.Commits == 1, "the cut commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the commit settles");
                Assert.That(f.dag.TryGetGeometry(above, out _), Is.True, "the commit really made the geometry");

                Color32[] committed = DrawAndRead(f, camera);
                TestContext.WriteLine(
                    "across the commit: covered " + Covered(temporary) + " -> " + Covered(committed)
                    + " px, marker behind " + Count(temporary, IsMarker) + " -> " + Count(committed, IsMarker) + " px");
                AssertSamePixels(temporary, committed, "a commit");
                Assert.That(marker != null && below.IsSet, Is.True);
                _frame++;
            }
        }

        /// <summary>Settles the cut's anchor distribution, which is what makes it displayable as two sides.</summary>
        private static void PrepareAnchors(Fixture f, CutOperationId cut)
        {
            Assert.That(
                f.ledger.PrepareAnchorDistribution(cut, 1e-5f, out _),
                Is.EqualTo(AnchorPreparationOutcome.Prepared),
                "the anchors are distributed");
        }

        /// <summary>
        /// The raw depth a prepared cut writes is the raw depth the whole body wrote. Read from the depth buffer
        /// itself, not from what shows through: a surface that stopped writing depth and one that moved would both
        /// leave the colour comparison intact if the marker happened to fall the same way. How many pixels differ and
        /// by how much are recorded either way; nothing is required to agree exactly.
        /// </summary>
        [Test]
        public void APreparedCut_LeavesTheRawDepthAsItWas()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True);
                Assert.That(f.display.TryBeginFrame(), Is.True, "the whole body settles");

                Camera camera = FromTheSide();
                Marker();
                float[] whole = DrawAndReadDepth(f, camera);

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PrepareAnchors(f, cut);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the prepared cut settles");
                Assert.That(f.display.SideCount, Is.EqualTo(2 * f.display.DrawCommandCount), "drawn as two sides now");

                float[] prepared = DrawAndReadDepth(f, camera);
                ReportDepth(whole, prepared, "a cut that is only prepared");
                _frame++;
            }
        }

        /// <summary>
        /// The raw depth across a real commit, with the two sides at placements that differ. Read the same way as
        /// above and recorded the same way.
        /// </summary>
        [Test]
        public void ACommit_LeavesTheRawDepthAsItWas()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True);
                Assert.That(f.display.TryBeginFrame(), Is.True);

                Camera camera = FromTheSide();
                Marker();
                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.display.Placement = new VpTestPlacements()
                    .Put(above, Matrix4x4.Translate(new Vector3(0f, 1.2f, 0f)))
                    .Static(below);
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the published sides settle");
                float[] temporary = DrawAndReadDepth(f, camera);

                f.RunUntil(() => f.commit.Commits == 1, "the cut commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the commit settles");
                Assert.That(f.dag.TryGetGeometry(above, out _), Is.True, "the commit really made the geometry");

                float[] committed = DrawAndReadDepth(f, camera);
                ReportDepth(temporary, committed, "a commit");
                _frame++;
            }
        }

        // ----- the camera, the marker and the reading --------------------------------------------------------------------

        private const int Size = 96;

        /// <summary>
        /// A camera beside the body, looking along +x at its flank, with the cut plane across the middle of what it
        /// sees. Orthographic, so a gap is the same width wherever it is.
        /// </summary>
        private Camera FromTheSide()
        {
            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            target.Create();
            Camera camera = Track(new GameObject("VP Commit Drawing Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 12f;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(-4f, 0.5f, 0f), Quaternion.LookRotation(Vector3.right, Vector3.up));
            return camera;
        }

        /// <summary>
        /// An ordinary blue wall behind the body. What shows through it is read apart from the body's own colour: a
        /// gap in the surface and a surface that stopped writing depth look the same in a silhouette and not here.
        /// </summary>
        private GameObject Marker()
        {
            GameObject quad = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
            UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetPositionAndRotation(new Vector3(4f, 0.5f, 0f), Quaternion.LookRotation(Vector3.right, Vector3.up));
            quad.transform.localScale = new Vector3(12f, 12f, 1f);
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var blue = Track(new Material(unlit) { name = "commit drawing marker" });
            blue.SetColor("_BaseColor", new Color(0f, 0f, 1f));
            blue.SetColor("_Color", new Color(0f, 0f, 1f));
            quad.GetComponent<MeshRenderer>().sharedMaterial = blue;
            return quad;
        }

        /// <summary>
        /// Draws the display for this camera and reads the **depth buffer** back, as it stands, one float per pixel.
        /// A render request for a depth destination is what makes this the buffer and not a reconstruction; if this
        /// build cannot answer one, that is said here rather than passed over.
        /// </summary>
        private float[] DrawAndReadDepth(Fixture f, Camera camera)
        {
            var depth = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.Depth)
            {
                name = "VP Commit Drawing Depth",
            });
            depth.Create();

            f.display.TryRegisterCamera(camera);
            Assert.That(f.display.TryPrepareCamera(camera), Is.True, "the camera is prepared");
            f.display.Render(0, camera);

            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = depth };
            Assert.That(
                UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request), Is.True,
                "this build answers a render request for the depth buffer");
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);

            // The depth destination has no colour format, so it cannot be read back as it stands. Copied into a
            // single-channel float target it keeps its values and can be.
            var readable = Track(new RenderTexture(Size, Size, 0, RenderTextureFormat.RFloat)
            {
                name = "VP Commit Drawing Depth Readable",
            });
            readable.Create();
            Graphics.Blit(depth, readable);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = readable;
            var read = Track(new Texture2D(Size, Size, TextureFormat.RFloat, false));
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;

            Color[] pixels = read.GetPixels();
            var values = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                values[i] = pixels[i].r;
            }

            return values;
        }

        /// <summary>
        /// How two raw depth buffers differ: how many pixels are not the same value, and the largest difference among
        /// them. Written out, and checked only for being a real depth buffer rather than a constant.
        /// </summary>
        private static void ReportDepth(float[] before, float[] after, string what)
        {
            Assert.That(after.Length, Is.EqualTo(before.Length), what + ": the same buffer size");
            int differing = 0;
            float largest = 0f;
            float low = float.MaxValue;
            float high = float.MinValue;
            for (int i = 0; i < before.Length; i++)
            {
                low = Mathf.Min(low, before[i]);
                high = Mathf.Max(high, before[i]);
                float difference = Mathf.Abs(after[i] - before[i]);
                if (difference > 0f)
                {
                    differing++;
                    largest = Mathf.Max(largest, difference);
                }
            }

            TestContext.WriteLine(
                "raw depth across " + what + ": " + differing + " of " + before.Length
                + " pixels differ, largest difference " + largest.ToString("G9")
                + "; the buffer itself runs " + low.ToString("G9") + " to " + high.ToString("G9"));
            Assert.That(
                high - low, Is.GreaterThan(1e-6f),
                what + ": the depth read back has a range, so it is a depth buffer and not a constant");
        }

        private Color32[] DrawAndRead(Fixture f, Camera camera)
        {
            f.display.TryRegisterCamera(camera);
            Assert.That(f.display.TryPrepareCamera(camera), Is.True, "the camera is prepared");
            f.display.Render(0, camera);

            RenderTexture target = camera.targetTexture;
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target };
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                camera.Render();
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var read = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        /// <summary>The marker's own blue, which the body's materials never draw.</summary>
        private static bool IsMarker(Color32 c)
        {
            return c.b > 100 && c.r < 80 && c.g < 80;
        }

        /// <summary>How much of the image the VP body covers: neither the cleared background nor the marker.</summary>
        private static int Covered(Color32[] pixels)
        {
            int n = 0;
            foreach (Color32 c in pixels)
            {
                if (!IsMarker(c) && (c.r > 20 || c.g > 20 || c.b > 20))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// The rows of the image a world y range falls in, for this camera: it stands at y = 0.5 with an orthographic
        /// size of 2, looking along +x with +y up, and the read image runs bottom-up.
        /// </summary>
        private static (int low, int high) Rows(float yLow, float yHigh)
        {
            int low = Mathf.CeilToInt((yLow + 1.5f) / 4f * Size);
            int high = Mathf.FloorToInt((yHigh + 1.5f) / 4f * Size);
            Assert.That(high, Is.GreaterThan(low), "the band holds rows");
            return (low, high);
        }

        /// <summary>How much of one band of rows the VP body covers.</summary>
        private static int CoveredInBand(Color32[] pixels, float yLow, float yHigh)
        {
            (int low, int high) = Rows(yLow, yHigh);
            int n = 0;
            for (int row = low; row < high; row++)
            {
                for (int column = 0; column < Size; column++)
                {
                    Color32 c = pixels[(row * Size) + column];
                    n += !IsMarker(c) && (c.r > 20 || c.g > 20 || c.b > 20) ? 1 : 0;
                }
            }

            return n;
        }

        /// <summary>How much of one band of rows the marker behind the body shows through.</summary>
        private static int MarkerInBand(Color32[] pixels, float yLow, float yHigh)
        {
            (int low, int high) = Rows(yLow, yHigh);
            int n = 0;
            for (int row = low; row < high; row++)
            {
                for (int column = 0; column < Size; column++)
                {
                    n += IsMarker(pixels[(row * Size) + column]) ? 1 : 0;
                }
            }

            return n;
        }

        private static int Count(Color32[] pixels, Func<Color32, bool> test)
        {
            int n = 0;
            foreach (Color32 c in pixels)
            {
                n += test(c) ? 1 : 0;
            }

            return n;
        }

        private static void AssertSamePixels(Color32[] a, Color32[] b, string what)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length), what + ": the same image size");
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b)
                {
                    Assert.Fail(
                        what + " changed pixel " + i + " (" + (i % Size) + ", " + (i / Size) + "): "
                        + a[i] + " -> " + b[i]);
                }
            }
        }

        // ----- placement, capacity, registration room and boundaries ----------------------------------------------------

        /// <summary>
        /// Where each side is drawn is the same before and after the commit, for a turned and moved body, through A
        /// and then B. The three sides stand apart because each follows a base placement of its own, given here as
        /// test input -- nothing in the display puts them there -- and the commit, which moves the root down to the
        /// sides, takes none of that in: every side is drawn at exactly the placement it was drawn at before.
        /// </summary>
        [Test]
        public void WhereEachSideIsDrawn_IsKept_WhenTheRootMovesDownToTheSides()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                Matrix4x4 placement = Matrix4x4.TRS(
                    new Vector3(-0.75f, 0.5f, 1.25f), Quaternion.Euler(23f, 47f, 11f), Vector3.one);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, placement), Is.True, "the body is shown, turned and moved");

                float4 planeA = Level(0f);
                float4 planeB = Level(0.5f);
                // A base placement for each side, given to the display as test input. They differ, and by more
                // than any tolerance here, so that "drawn where it was" cannot pass by the three simply coinciding.
                Matrix4x4 topOwner = Matrix4x4.Translate(new Vector3(0f, 0.9f, 0f)) * placement;
                Matrix4x4 middleOwner = Matrix4x4.Translate(new Vector3(0f, 0.3f, 0f)) * placement;

                CutOperationId a = Admit(f, fragment, planeA);
                PublishPhysics(f, a, out LogicalFragmentId above, out LogicalFragmentId below);
                f.display.Placement = new VpTestPlacements().Put(above, topOwner).Put(below, placement);
                CutOperationId b = Admit(f, above, planeB);
                PublishPhysics(f, b, out LogicalFragmentId top, out LogicalFragmentId middle);
                f.display.Placement = new VpTestPlacements()
                    .Put(top, topOwner)
                    .Put(middle, middleOwner)
                    .Put(below, placement);

                Assert.That(f.display.TryBeginFrame(), Is.True, "both cuts temporary");
                Vector3 topBefore = TotalOf(f, top);
                Vector3 middleBefore = TotalOf(f, middle);
                Vector3 belowBefore = TotalOf(f, below);

                Assert.That(topBefore, Is.Not.EqualTo(middleBefore), "the temporary sides really are apart");
                Assert.That(topBefore, Is.Not.EqualTo(belowBefore), "all three of them");

                f.RunUntil(() => f.commit.Commits == 1, "A commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "GA settles");

                AssertClose(TotalOf(f, below), belowBefore, "A's negative side is where it was");
                AssertClose(TotalOf(f, top), topBefore, "B's positive side is where it was");
                AssertClose(TotalOf(f, middle), middleBefore, "B's negative side is where it was");
                AssertClose(
                    PlacementOf(f, above),
                    (Vector3)placement.GetColumn(3),
                    "and the registration A left behind still stands at the body's placement");

                f.RunUntil(() => f.commit.Commits == 2, "B commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "GAB settles");

                AssertClose(TotalOf(f, below), belowBefore, "A's negative side has not moved");
                AssertClose(TotalOf(f, top), topBefore, "nor has B's positive side");
                AssertClose(TotalOf(f, middle), middleBefore, "nor B's negative side");
            }
        }

        /// <summary>
        /// A body that its anchors fix does not move: with neither side given a placement of its own, both are
        /// committed exactly at the body's placement, the anchored one and the free one alike.
        /// </summary>
        [Test]
        public void ASideItsAnchorsFix_IsCommittedWhereTheBodyWas()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                Matrix4x4 placement = Matrix4x4.TRS(new Vector3(2f, 0f, 0f), Quaternion.identity, Vector3.one);
                LogicalFragmentId fragment = f.ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, placement), Is.True, "the body is shown");

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.RunUntil(() => f.commit.Commits == 1, "the cut commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the collection settles");

                AssertClose(PlacementOf(f, below), placement.GetColumn(3), "the anchored side stays where the body was");
                AssertClose(
                    PlacementOf(f, above),
                    (Vector3)placement.GetColumn(3),
                    "and the free side too, with no placement of its own to be anywhere else");
            }
        }

        /// <summary>
        /// The capacity is judged on what is drawn after the swap, not on what was drawn before it: a body of two
        /// commands becoming two sides of two commands each fits commands and instances of four exactly. The body's
        /// own drawing data goes when the sides take its place; what it keeps until the next adoption is room in the
        /// reference table, which has plenty here, and not drawing data.
        /// </summary>
        [Test]
        public void ADrawCapacityThatFitsTheSwapExactly_IsNotRefused()
        {
            using (Fixture f = NewFixture(commandCapacity: 4, instanceCapacity: 4))
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");
                Assert.That(f.display.DrawCommandCount, Is.EqualTo(2), "two commands before");

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out _, out _);
                f.RunUntil(() => f.commit.Commits == 1, "the cut commits at exactly the capacity");

                Assert.That(f.commit.Refusals, Is.Zero, "and was not refused on the way");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the collection settles");
                Assert.That(f.display.DrawCommandCount, Is.EqualTo(4), "four commands after");
            }
        }

        /// <summary>
        /// Two commits in a row without a collection between them: the second is judged on the registrations as they
        /// are, not on the snapshot the first has not reached yet, and both take effect at the next collection.
        /// </summary>
        [Test]
        public void TwoCommitsWithNoCollectionBetweenThem_BothTakeEffect()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");

                CutOperationId a = Admit(f, fragment, Level(0f));
                PublishPhysics(f, a, out LogicalFragmentId above, out _);
                CutOperationId b = Admit(f, above, Level(0.5f));
                PublishPhysics(f, b, out _, out _);

                // No collection at all between the two commits.
                f.RunUntil(() => f.commit.Commits == 2, "both cuts commit");
                Assert.That(f.commit.Refusals, Is.Zero, "neither was refused");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the one collection after both settles");
                Assert.That(f.display.ShownCount, Is.EqualTo(3), "all three sides are shown");
                Assert.That(f.display.DrawCommandCount, Is.EqualTo(6), "and all of them are drawn");
                Assert.That(CapRecordsOf(f.display, a) + CapRecordsOf(f.display, b), Is.Zero, "nothing is temporary any more");
            }
        }

        /// <summary>
        /// Room in the reference table for one side only: the commit is refused before anything is transferred or
        /// taken, both produced sides stay published in the storage, and once there is room the same cut commits with
        /// one vertex transfer and one index transfer — the refused attempt sent nothing.
        /// </summary>
        [Test]
        public void WhenThereIsRoomForOneSideOnly_NothingIsTransferredAndTheSidesSurvive()
        {
            // Room in the table for the two bodies and one more geometry, and for the display instances the split
            // body and one committed side need: enough for one side of the commit, never for both.
            using (Fixture f = NewFixture(instanceCapacity: 16, geometryCapacity: 3, displayInstanceCapacity: 4))
            {
                VpStoredGeometry first = AppendBox(f.storage, float3.zero);
                VpStoredGeometry second = AppendBox(f.storage, new float3(6f, 0f, 0f));
                LogicalFragmentId cutBody = f.ledger.AddFragment();
                LogicalFragmentId filler = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(cutBody, first);
                Assert.That(f.display.TryShow(cutBody, first, Matrix4x4.identity), Is.True, "the body to cut is shown");
                Assert.That(f.display.TryShow(filler, second, Matrix4x4.identity), Is.True, "and another body fills the table");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");
                int vertexTransfers = f.display.VertexTransfers;
                int indexTransfers = f.display.IndexTransfers;

                CutOperationId cut = Admit(f, cutBody, Level(0f));
                PublishPhysics(f, cut, out _, out _);
                f.RunUntil(() => f.commit.Refusals > 0, "the commit is refused for want of room");

                Assert.That(f.commit.Commits, Is.Zero, "nothing was committed");
                Assert.That(f.display.VertexTransfers, Is.EqualTo(vertexTransfers), "and nothing was transferred");
                Assert.That(f.display.IndexTransfers, Is.EqualTo(indexTransfers), "neither vertices nor indices");
                Assert.That(f.dag.TryGetResultBeforeCommit(cut, out VpStorageCutResult held), Is.True, "the cut still holds its result");
                foreach (VpStorageCutSide side in new[] { held.positive, held.negative })
                {
                    Assert.That(
                        f.storage.TryGetIndexState(side.geometry.indexRange, out VpIndexRangeState state, out _, out _)
                        && state == VpIndexRangeState.Published,
                        Is.True,
                        "both produced sides are still published, so the result is intact");
                }

                // Room is made: the other body is retired, and the next collection lets its registration go.
                Assert.That(f.ledger.Retire(filler), Is.True, "the other body retires");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the collection lets it go");

                f.RunUntil(() => f.commit.Commits == 1, "the same cut now commits");
                Assert.That(f.display.VertexTransfers, Is.EqualTo(vertexTransfers + 1), "with exactly one vertex transfer");
                Assert.That(f.display.IndexTransfers, Is.EqualTo(indexTransfers + 1), "and exactly one index transfer");
            }
        }

        /// <summary>
        /// The boundary a commit publishes is the surface the cut really made: one record for a cut that made a cap,
        /// naming the two sides and the plane it was cut by, and no record at all for a plane that missed.
        /// </summary>
        [Test]
        public void ACommitPublishesTheBoundaryTheCutReallyMade_AndNoneWhereItMadeNothing()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, Matrix4x4.identity), Is.True, "the body is shown");
                Assert.That(f.display.BoundaryRecordCount, Is.Zero, "nothing is recorded before a commit");

                float4 plane = Level(0f);
                CutOperationId cut = Admit(f, fragment, plane);
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);
                f.RunUntil(() => f.commit.Commits == 1, "the cut commits");

                Assert.That(f.display.BoundaryRecordCount, Is.EqualTo(1), "one boundary was published");
                Assert.That(f.display.TryGetBoundaryRecord(0, out LogicalCutBoundaryRecord record), Is.True);
                Assert.That(record.operation, Is.EqualTo(cut), "it is this cut's");
                Assert.That(record.positive, Is.EqualTo(above), "with the side above");
                Assert.That(record.negative, Is.EqualTo(below), "and the side below");
                Assert.That(
                    new Vector4(plane.x, plane.y, plane.z, plane.w), Is.EqualTo(record.plane),
                    "the plane it was cut by, in the frame it was admitted in");
                Assert.That(record.lineageToGeometryLocal, Is.EqualTo(Matrix4x4.identity), "and the frame its sides are in");
                Assert.That(record.capTriangles, Is.GreaterThan(0), "and the surface it really made");

                // A plane that misses makes no surface, so it records no boundary -- although the side that keeps it
                // all is still marked as reflecting that cut, which is a different thing.
                CutOperationId missed = Admit(f, below, Level(-8f));
                PublishPhysics(f, missed, out _, out _);
                f.RunUntil(() => f.commit.Commits == 2, "the cut that missed commits");

                Assert.That(f.display.BoundaryRecordCount, Is.EqualTo(1), "and publishes no boundary of its own");
            }
        }

        /// <summary>
        /// The boundary names what each side was at the commit: its geometry and where that geometry stood, each side
        /// its own. A later cut of one of those sides leaves the record exactly as it was — the fragment's geometry
        /// moves on, the record does not — and the record keeps nothing alive: the geometry it names is retired with
        /// the registration that held it.
        /// </summary>
        [Test]
        public void TheBoundaryNamesEachSidesGeometryAndPlacement_AndKeepsNothingAlive()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, float3.zero);
                Matrix4x4 placement = Matrix4x4.TRS(
                    new Vector3(0.4f, -0.3f, 0.9f), Quaternion.Euler(17f, 39f, 5f), Vector3.one);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, placement), Is.True, "the body is shown");

                // The free side is given a base placement away from the anchored one; the record has to name each
                // side where that side really is, which is the only reason the two entries differ at all.
                Matrix4x4 abovePlacement = Matrix4x4.Translate(new Vector3(0f, 0.7f, 0f)) * placement;

                CutOperationId a = Admit(f, fragment, Level(0f));
                PublishPhysics(f, a, out LogicalFragmentId above, out LogicalFragmentId below);
                f.display.Placement = new VpTestPlacements().Put(above, abovePlacement).Put(below, placement);
                f.RunUntil(() => f.commit.Commits == 1, "A commits");

                Assert.That(f.display.TryGetBoundaryRecord(0, out LogicalCutBoundaryRecord record), Is.True);
                Assert.That(f.dag.TryGetGeometry(above, out VpStoredGeometry positive), Is.True);
                Assert.That(f.dag.TryGetGeometry(below, out VpStoredGeometry negative), Is.True);
                Assert.That(record.positiveGeometry.indexRange, Is.EqualTo(positive.indexRange), "the geometry the positive side became");
                Assert.That(record.negativeGeometry.indexRange, Is.EqualTo(negative.indexRange), "and the negative side's");
                AssertClose(
                    record.positiveObjectToWorld.GetColumn(3),
                    (Vector3)abovePlacement.GetColumn(3),
                    "where the positive side stood: at the placement it was given");
                AssertClose(
                    record.negativeObjectToWorld.GetColumn(3),
                    (Vector3)placement.GetColumn(3),
                    "and where the negative side stood: where the body was");
                Assert.That(
                    record.positiveObjectToWorld, Is.Not.EqualTo(record.negativeObjectToWorld),
                    "the two sides stand apart, each at the placement it follows");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "GA settles");

                // B cuts the positive side: that fragment's geometry moves on, and A's record does not.
                CutOperationId b = Admit(f, above, Level(0.5f));
                PublishPhysics(f, b, out LogicalFragmentId top, out LogicalFragmentId middle);
                f.display.Placement = new VpTestPlacements()
                    .Put(top, abovePlacement)
                    .Put(middle, abovePlacement)
                    .Put(below, placement);
                f.RunUntil(() => f.commit.Commits == 2, "B commits");
                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "GAB settles");

                Assert.That(f.display.TryGetBoundaryRecord(0, out LogicalCutBoundaryRecord after), Is.True);
                Assert.That(after.positiveGeometry.indexRange, Is.EqualTo(positive.indexRange), "A's record still names what A made");
                Assert.That(after.positiveObjectToWorld, Is.EqualTo(record.positiveObjectToWorld), "and where it stood");
                Assert.That(f.dag.TryGetGeometry(above, out _), Is.False, "although that fragment has moved on");

                // And nothing of the record holds the geometry it names: the registration that held it has let it go.
                Assert.That(
                    f.storage.TryGetIndexState(positive.indexRange, out VpIndexRangeState state, out _, out _)
                    && state == VpIndexRangeState.Free,
                    Is.True,
                    "the geometry A made for that side has gone back to the storage");
                Assert.That(f.display.BoundaryRecordCount, Is.EqualTo(2), "and both boundaries are still recorded");
            }
        }

        // ----- 5. placement, submeshes and lifetime ------------------------------------------------------------------------

        /// <summary>
        /// A body at a placement of its own, with two submeshes of different sizes: the sides keep that placement and
        /// both submeshes, each side's commands cover exactly its own published range, and a commit that happens after
        /// a collection has settled changes nothing of what that collection is still drawing — the old ranges are
        /// still there to draw, and the new ones arrive with the next collection.
        /// </summary>
        [Test]
        public void ThePlacementAndBothSubmeshesAreKept_AndACommitAfterACollectionWaitsForTheNextOne()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry body = AppendBox(f.storage, new float3(0.5f, -0.25f, 0.75f));
                Matrix4x4 placement = Matrix4x4.TRS(
                    new Vector3(1.5f, 0.25f, -0.5f), Quaternion.Euler(12f, 34f, 56f), Vector3.one);
                LogicalFragmentId fragment = f.ledger.AddFragment();
                f.dag.RegisterBaseGeometry(fragment, body);
                Assert.That(f.display.TryShow(fragment, body, placement), Is.True, "the body is shown, turned and moved");
                Assert.That(f.display.TryBeginFrame(), Is.True, "the first collection settles");
                Assert.That(f.display.DrawCommandCount, Is.EqualTo(2), "one command per submesh");
                List<(int start, int count)> before = DrawnRanges(f.display);

                CutOperationId cut = Admit(f, fragment, Level(0f));
                PublishPhysics(f, cut, out LogicalFragmentId above, out LogicalFragmentId below);

                // The collection of this frame has settled already, so the commit belongs to the next one.
                f.RunUntil(() => f.commit.Commits == 1, "the cut commits");
                Assert.That(
                    DrawnRanges(f.display), Is.EqualTo(before),
                    "the settled collection keeps drawing exactly what it settled");
                Assert.That(f.display.TryBeginFrame(), Is.True, "and collecting again in the same frame changes nothing");
                Assert.That(DrawnRanges(f.display), Is.EqualTo(before), "still the same");

                _frame++;
                Assert.That(f.display.TryBeginFrame(), Is.True, "the next collection settles");

                Assert.That(f.display.ShownCount, Is.EqualTo(2), "the two sides");
                Assert.That(f.display.DrawCommandCount, Is.EqualTo(4), "each with both submeshes");
                Assert.That(f.dag.TryGetGeometry(above, out VpStoredGeometry positive), Is.True);
                Assert.That(f.dag.TryGetGeometry(below, out VpStoredGeometry negative), Is.True);

                Assert.That(f.storage.TryGetIndexState(positive.indexRange, out _, out int positiveStart, out int positiveCount), Is.True);
                Assert.That(f.storage.TryGetIndexState(negative.indexRange, out _, out int negativeStart, out int negativeCount), Is.True);
                Assert.That(negativeStart, Is.EqualTo(positiveStart + positiveCount), "the two sides are one contiguous run");

                foreach ((int start, int count) in DrawnRanges(f.display))
                {
                    bool insidePositive = start >= positiveStart && start + count <= positiveStart + positiveCount;
                    bool insideNegative = start >= negativeStart && start + count <= negativeStart + negativeCount;
                    Assert.That(insidePositive || insideNegative, Is.True, "every command draws inside one side's own range");
                }

                // Neither side was given a placement of its own, so each registration holds the body's, unchanged:
                // the commit took nothing into it.
                AssertClose(
                    PlacementOf(f, above),
                    (Vector3)placement.GetColumn(3),
                    "the positive side's registration is the body's placement");
                AssertClose(
                    PlacementOf(f, below),
                    (Vector3)placement.GetColumn(3),
                    "and so is the negative side's");
            }
        }
    }
}
