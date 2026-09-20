using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Cutting a stored geometry through the shared dispatcher instead of on the main thread: the cut runs on a
    /// geometry pool worker, and the sizes it is given, the input lease, the output reservation and the publication
    /// stay on the main thread, and what comes out is what the synchronous entry produces (DESIGN 4.3, 4.5.6). One
    /// cut is one kind of work: nothing is offered to the dispatcher to measure the input first.
    /// <para>
    /// These tests use the real pool, because where the work runs is the point of the unit. They never wait for a
    /// fixed time: each one drives frames until the cut is over or a generous deadline passes, and a deadline that
    /// passes is a failure. Nothing here measures how long a cut takes.
    /// </para>
    /// </summary>
    public unsafe class VpAsyncStorageCutTests
    {
        private const int DeadlineMilliseconds = 30000;

        // ----- fixture ---------------------------------------------------------------------------------------------

        private sealed class Fixture : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public VpAsyncStorageCut runner;
            public int mainThreadId;
            private int _frame;

            /// <summary>One frame of the caller's own loop: collect and submit, then let the cuts move.</summary>
            public void Frame()
            {
                dispatcher.BeginFrame(++_frame);
                dispatcher.Dispatch();
                runner.Pump();
            }

            /// <summary>Frames until the cut is over. A deadline that passes is a failure, not a wait.</summary>
            public void RunUntilOver(VpStorageCutRequest request, string what = "the cut ends")
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Frame();
                    if (request.IsOver)
                    {
                        return;
                    }

                    Thread.Sleep(1);
                }

                Assert.Fail(what + ": it was still " + request.Stage + " when the deadline passed");
            }

            public void Dispose()
            {
                // The close, then the stop, then one last collection on the main thread: that is the order a caller
                // has to use for a cut a worker was still running to give back what it held.
                runner?.Dispose();
                dispatcher?.Shutdown(DeadlineMilliseconds);
                runner?.Pump();
                geometry?.Dispose();
                background?.Dispose();
                storage?.Dispose();
            }
        }

        private static Fixture NewFixture(
            IWorkExecutor geometryPool = null,
            int vertexCapacity = 4096,
            int indexCapacity = 16384)
        {
            var fixture = new Fixture
            {
                storage = new VpCpuGeometryStorage(vertexCapacity, indexCapacity, 64, 256, 256, Allocator.Persistent),
                job = new UnityJobWorkExecutor(4),
                background = WorkerPoolExecutor.BackgroundPool(2),
                mainThreadId = Thread.CurrentThread.ManagedThreadId,
            };
            if (geometryPool == null)
            {
                fixture.geometry = WorkerPoolExecutor.GeometryPool(4);
                geometryPool = fixture.geometry;
            }

            fixture.dispatcher = new SharedWorkDispatcher(8, 2, 32, fixture.job, geometryPool, fixture.background);
            fixture.runner = new VpAsyncStorageCut(fixture.storage, fixture.dispatcher);
            return fixture;
        }

        /// <summary>A closed box with attribute seams and two submeshes: enough shape for a cut to be interesting.</summary>
        private static SyntheticMesh Box(int n = 3)
        {
            return SyntheticGeometry.Box(n, new float3(1f, 1f, 1f), float3.zero, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, SyntheticMesh mesh)
        {
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(
                storage.TryAppendCuttable(
                    mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes,
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the box is appended as a cut input");
            return geometry;
        }

        private static VpStorageCutInput Acquire(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, "acquire the input");
            return input;
        }

        /// <summary>A plane through the box, tilted so that no vertex lies on it.</summary>
        private static float4 Tilted()
        {
            return SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), new float3(0.0071f, -0.0233f, 0.0119f));
        }

        /// <summary>A plane well clear of the box, which cannot cut it.</summary>
        private static float4 Clear()
        {
            return SyntheticGeometry.Plane(new float3(0f, 1f, 0f), new float3(0f, 8f, 0f));
        }

        /// <summary>
        /// The two sides of one cut run by itself, in a storage of its own, as the reader sees them. What a cut run
        /// beside others must come to, whatever room it was given and whenever it finished.
        /// </summary>
        private static (List<string> positive, List<string> negative) CutOnItsOwn(
            SyntheticMesh mesh, float4 plane, int vertexCapacity = 4096, int indexCapacity = 16384)
        {
            using (var alone = new VpCpuGeometryStorage(vertexCapacity, indexCapacity, 64, 256, 256, Allocator.Persistent))
            {
                VpStoredGeometry box = Append(alone, mesh);
                using (VpStorageCutInput input = Acquire(alone, box))
                {
                    Assert.That(VpStorageCut.TryExecute(alone, input, plane, out VpStorageCutResult result), Is.True, "the cut on its own");
                    Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "it produced both sides");
                    return (Materialise(alone, result.positive.geometry), Materialise(alone, result.negative.geometry));
                }
            }
        }

        /// <summary>
        /// One cut's two sides against what that cut comes to on its own. This is the check that a cut run beside
        /// others produced the right geometry, rather than merely something of the right kind.
        /// </summary>
        private static void AssertIsTheSameCutAsOnItsOwn(
            Fixture f, VpStorageCutRequest request, (List<string> positive, List<string> negative) alone, string what)
        {
            VpStorageCutResult result = request.Result;
            Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, what + ": both sides are new geometry");
            Assert.That(
                Materialise(f.storage, result.positive.geometry), Is.EqualTo(alone.positive),
                what + ": the positive side is the geometry this cut makes on its own");
            Assert.That(
                Materialise(f.storage, result.negative.geometry), Is.EqualTo(alone.negative),
                what + ": and so is the negative side");
        }

        /// <summary>
        /// Two cuts' published rooms, in every array they were given one: the vertices they share, the index space
        /// their sides really occupy, and the vertex blocks. The blocks' contents are read too, because a block that
        /// named another cut's vertices would still sit in a range of its own.
        /// </summary>
        private static void AssertRoomsDoNotOverlap(Fixture f, VpStorageCutRequest a, VpStorageCutRequest b)
        {
            (int start, int count) verticesOfA = VerticesOf(a);
            (int start, int count) verticesOfB = VerticesOf(b);
            AssertRangesAreApart(verticesOfA, verticesOfB, "vertices");

            foreach (VpStoredGeometry first in SidesOf(a))
            {
                foreach (VpStoredGeometry second in SidesOf(b))
                {
                    AssertRangesAreApart(IndicesOf(f, first), IndicesOf(f, second), "indices");
                    AssertRangesAreApart(
                        (first.submeshStart, first.submeshCount), (second.submeshStart, second.submeshCount), "submeshes");
                }
            }

            // Every block of each side names vertices inside that cut's own span, mapping entries included.
            foreach (VpStoredGeometry side in SidesOf(a))
            {
                AssertBlocksNameOnly(f, side, verticesOfA, "the first cut");
            }

            foreach (VpStoredGeometry side in SidesOf(b))
            {
                AssertBlocksNameOnly(f, side, verticesOfB, "the second cut");
            }
        }

        private static void AssertBlocksNameOnly(
            Fixture f, VpStoredGeometry side, (int start, int count) own, string what)
        {
            Assert.That(
                f.storage.TryGetVertexBlocks(side, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _),
                Is.True, what + ": blocks");
            bool sawItsOwn = false;
            for (int b = 0; b < blocks.Length; b++)
            {
                if (blocks[b].vertexCount == 0)
                {
                    continue;
                }

                bool isItsOwn = blocks[b].vertexStart == own.start && blocks[b].vertexCount == own.count;
                sawItsOwn |= isItsOwn;
                if (!isItsOwn)
                {
                    // The parent's blocks, which lie before either cut's room.
                    Assert.That(
                        blocks[b].vertexStart + blocks[b].vertexCount <= own.start || blocks[b].vertexStart >= own.start + own.count,
                        Is.True,
                        what + ": a block it did not append names none of its appended vertices");
                }
            }

            if (own.count > 0)
            {
                Assert.That(sawItsOwn, Is.True, what + ": the block it appended names exactly the vertices it appended");
            }
        }

        private static IEnumerable<VpStoredGeometry> SidesOf(VpStorageCutRequest request)
        {
            if (request.Result.positive.IsProduced)
            {
                yield return request.Result.positive.geometry;
            }

            if (request.Result.negative.IsProduced)
            {
                yield return request.Result.negative.geometry;
            }
        }

        private static (int start, int count) IndicesOf(Fixture f, VpStoredGeometry side)
        {
            Assert.That(f.storage.TryGetIndexState(side.indexRange, out _, out int start, out int count), Is.True, "the index range");
            return (start, count);
        }

        private static void AssertRangesAreApart((int start, int count) first, (int start, int count) second, string what)
        {
            if (first.count == 0 || second.count == 0)
            {
                return;
            }

            Assert.That(
                first.start >= second.start + second.count || second.start >= first.start + first.count,
                Is.True,
                "the two cuts' " + what + " are apart: [" + first.start + ", " + (first.start + first.count) + ") and ["
                + second.start + ", " + (second.start + second.count) + ")");
        }

        /// <summary>
        /// One side as its reader sees it: every index in order, resolved to the vertex it names and that vertex's
        /// topology id, plus the submesh descriptors. Two sides that compare equal by this are the same geometry, in
        /// the same order, whichever storage and whichever route produced them.
        /// </summary>
        private static List<string> Materialise(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            var read = new List<string>();
            Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True, "submeshes");
            for (int s = 0; s < submeshes.Length; s++)
            {
                read.Add(string.Format("submesh {0}: {1}+{2} material {3}", s, submeshes[s].indexOffset, submeshes[s].indexCount, submeshes[s].materialIndex));
            }

            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "read lease");
            NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
            NativeArray<int>.ReadOnly topology = storage.TopologyOfVertex;
            for (int i = 0; i < view.Length; i++)
            {
                uint v = view[i];
                VpRenderVertex vertex = vertices[(int)v];
                read.Add(string.Format(
                    "{0}: {1} {2} {3} t{4}",
                    i, vertex.position, vertex.normal, vertex.uv0, topology[(int)v]));
            }

            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release the lease");
            return read;
        }

        /// <summary>
        /// Whether a range still has a reader. Retiring a range with a lease still out leaves it Retiring rather than
        /// Free, so this is how a test sees that a cut really gave its input lease back.
        /// </summary>
        private static void AssertLeaseWasReturned(VpCpuGeometryStorage storage, VpStoredGeometry geometry, string what)
        {
            Assert.That(storage.TryRetireIndices(geometry.indexRange), Is.True, what + ": the range retires");
            Assert.That(
                storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _) && state == VpIndexRangeState.Free,
                Is.True,
                what + ": and is free at once, so no read lease was left behind");
        }

        // ----- where the work runs ---------------------------------------------------------------------------------

        /// <summary>
        /// The two heavy parts — the capacity query, which walks every triangle, and the cut — both run on a pool
        /// worker, and the result is taken back on the main thread. Nothing of the storage is touched off the main
        /// thread, which is what this division exists for.
        /// </summary>
        [Test]
        public void TheCut_RunsOnAWorker_AndIsCollectedOnTheMainThread()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = Append(f.storage, Box());
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);

                f.RunUntilOver(request);

                Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Finished), "it finished");
                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "with a cut");
                Assert.That(request.cutThreadId, Is.Not.EqualTo(f.mainThreadId), "the cut ran on a worker");
                Assert.That(request.collectThreadId, Is.EqualTo(f.mainThreadId), "the collection is the main thread's");
                Assert.That(f.runner.ReservingCount, Is.Zero, "and nothing of the storage is still reserved");
                Assert.That(f.runner.ActiveCount, Is.Zero, "the runner holds nothing");
            }
        }

        // ----- the same geometry as the synchronous entry ------------------------------------------------------------

        /// <summary>
        /// The two routes produce the same cut: the same two sides, with the same indices in the same order naming
        /// vertices of the same position, normal, uv and topology id, and the same submesh descriptors. The capacity
        /// rule and the publication are shared code, and this is what that sharing is for.
        /// </summary>
        [Test]
        public void ARunnerCut_IsTheSameGeometryAsTheSynchronousEntry()
        {
            SyntheticMesh mesh = Box();
            float4 plane = Tilted();
            List<string> syncPositive, syncNegative;
            using (var sync = new VpCpuGeometryStorage(4096, 16384, 64, 256, 256, Allocator.Persistent))
            {
                VpStoredGeometry box = Append(sync, mesh);
                using (VpStorageCutInput input = Acquire(sync, box))
                {
                    Assert.That(VpStorageCut.TryExecute(sync, input, plane, out VpStorageCutResult result), Is.True, "the synchronous cut");
                    Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "produced both sides");
                    syncPositive = Materialise(sync, result.positive.geometry);
                    syncNegative = Materialise(sync, result.negative.geometry);
                }
            }

            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = Append(f.storage, mesh);
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), plane, default);

                f.RunUntilOver(request);

                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut succeeded");
                Assert.That(request.Result.positive.IsProduced && request.Result.negative.IsProduced, Is.True, "both sides are new geometry");
                Assert.That(Materialise(f.storage, request.Result.positive.geometry), Is.EqualTo(syncPositive), "the positive side is the same");
                Assert.That(Materialise(f.storage, request.Result.negative.geometry), Is.EqualTo(syncNegative), "the negative side is the same");
            }
        }

        // ----- the sides the cut did not produce ---------------------------------------------------------------------

        /// <summary>
        /// A plane that leaves the whole geometry on one side: that side borrows the input, the other is empty, and
        /// **a reservation is taken for the attempt all the same**. Which side everything falls on is not knowable
        /// from the input's size, so the attempt reserves and the run finds out; the reservation is then given back
        /// unused.
        /// <para>
        /// What this case observes is that the reservation is open while the cut is with a worker, that the runner
        /// holds none afterwards, and that the storage holds exactly the vertices it held before. That the giving
        /// back happens whole, through the one path that returns an uncommitted reservation, is a property of
        /// <c>VpStorageCut.TryFinish</c> read in the implementation, not something these three observations prove by
        /// themselves.
        /// </para>
        /// </summary>
        [Test]
        public void APlaneThatMissesTheGeometry_BorrowsTheInput_AndItsReservationGoesBackWhole()
        {
            var gate = new GatedExecutor();
            using (Fixture f = NewFixture(gate))
            {
                VpStoredGeometry box = Append(f.storage, Box(2));
                int verticesBefore = f.storage.VertexCount;
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Clear(), default);

                // A reservation is taken for the attempt: whether the plane misses the geometry is not knowable from
                // the input's size, and only the run itself finds out.
                RunUntilAccepted(f, gate, request, VpStorageCutStage.Cutting);
                Assert.That(request.HoldsReservation, Is.True, "the attempt holds an output reservation");
                Assert.That(request.HoldsReservation, Is.True, "and it is this cut that holds it");
                Assert.That(f.runner.ReservingCount, Is.EqualTo(1), "and it is the only one holding room");

                gate.RunOneOnAWorker();
                f.RunUntilOver(request);

                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut is not a failure");
                Assert.That(request.Result.negative.IsBorrowed, Is.True, "the side that keeps it all borrows the input");
                Assert.That(request.Result.negative.geometry.indexRange, Is.EqualTo(box.indexRange), "which is the input itself");
                Assert.That(request.Result.positive.IsEmpty, Is.True, "and the other side is empty");
                Assert.That(request.Result.positive.geometry.indexRange, Is.EqualTo(default(VpIndexRangeHandle)), "with no range of its own");
                Assert.That(f.runner.ReservingCount, Is.Zero, "the reservation is not held any more");
                Assert.That(
                    f.storage.VertexCount, Is.EqualTo(verticesBefore),
                    "and the storage holds exactly the vertices it held before the cut");
                AssertLeaseWasReturned(f.storage, box, "after a borrowed side");
            }
        }

        /// <summary>
        /// An input the estimate is too small for, at the ordinary settings and with nothing overridden: three
        /// hundred separate bars in one geometry, cut across all of them, so that the plane crosses about two thirds
        /// of the triangles -- far above the few times the square root the rule of thumb expects. The cut takes more than one attempt, each short
        /// reservation goes back before the next is taken, nothing is published in between, and it succeeds.
        /// <para>
        /// This is the estimate falling short by itself. The case below, which sets the figures small through the
        /// options, is a different thing and is kept apart from it.
        /// </para>
        /// </summary>
        [Test]
        public void AnEstimateTooSmallForTheInput_ReservesAgain_AndPublishesNothingUntilItSucceeds()
        {
            using (Fixture f = NewFixture(vertexCapacity: 262144, indexCapacity: 1048576))
            {
                SyntheticMesh bars = SyntheticGeometry.BarField(300, 1, 0.2f, 0.5f, float3.zero)
                    .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
                VpStoredGeometry geometry = Append(f.storage, bars);
                int submeshesBefore = f.storage.SubmeshCount;
                var plane = new float4(0f, 1f, 0f, 0f);

                // What the estimate asked for against what the plane really crosses, so that a failure here says
                // whether the layout stopped holding rather than only that the count changed.
                VpStorageCutInput measured = Acquire(f.storage, geometry);
                Assert.That(measured.TryGetInput(plane, out MeshCutInput kernelInput), Is.True);
                MeshCutCapacity counted = default;
                MeshCutKernel.QueryCapacity(in kernelInput, ref counted);
                MeshCutCapacity estimated = default;
                MeshCutKernel.EstimateCapacity(in kernelInput, ref estimated);
                measured.Dispose();
                TestContext.WriteLine(
                    "the layout: triangles " + counted.triangleCount + ", crossings counted " + counted.crossingTriangles
                    + "; estimate reserves vertices " + estimated.newVertices + ", indices " + estimated.newIndices
                    + ", scratch " + estimated.scratchBytes
                    + "; a counted reservation would be vertices " + counted.newVertices
                    + ", indices " + counted.newIndices + ", scratch " + counted.scratchBytes);

                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, geometry), plane, default);

                int seenPublished = 0;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !request.IsOver)
                {
                    f.Frame();
                    if (!request.IsOver)
                    {
                        // Nothing of a cut in progress is in the storage: a short attempt publishes no part of itself.
                        seenPublished = math.max(seenPublished, f.storage.SubmeshCount - submeshesBefore);
                    }

                    Thread.Sleep(1);
                }

                Assert.That(request.IsOver, Is.True, "the cut ends within the deadline");

                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "it succeeds in the end");
                Assert.That(
                    request.Result.attempts, Is.GreaterThan(1),
                    "the layout: the estimate really is too small for this input, so it reserved again");
                Assert.That(seenPublished, Is.Zero, "and nothing was published before it succeeded");
                Assert.That(f.runner.ReservingCount, Is.Zero, "the last reservation is committed or gone");
                TestContext.WriteLine(
                    "an estimate too small: attempts " + request.Result.attempts
                    + ", kernel status " + request.Result.kernel.status);
            }
        }

        // ----- a reservation that was too small ----------------------------------------------------------------------

        /// <summary>
        /// A first reservation far too small is not a failure: it goes back, a larger one is taken at a later
        /// opportunity and the cut runs again, until it fits. The result is the same cut, and it took more than one
        /// run to get there.
        /// </summary>
        [Test]
        public void AReservationThatIsTooSmall_IsTakenAgainLarger_AndTheCutSucceeds()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = Append(f.storage, Box());
                var tiny = new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3 };
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Tilted(), tiny);

                f.RunUntilOver(request);

                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut succeeded in the end");
                Assert.That(request.Attempts, Is.GreaterThan(1), "after more than one run of the kernel");
                Assert.That(request.Result.positive.IsProduced && request.Result.negative.IsProduced, Is.True, "with both sides produced");
                Assert.That(f.runner.ReservingCount, Is.Zero, "and the reservation is back");
            }
        }

        // ----- two cuts of one storage, at the same time ------------------------------------------------------------

        /// <summary>
        /// Two independent cuts of one storage asked for together: both hold room of their own at the same time and
        /// both are with the dispatcher at the same time, neither waiting for the other's turn. Each succeeds, and the
        /// vertices they were given do not overlap.
        /// <para>
        /// Holding room and being offered are watched while the cuts run, because afterwards there is nothing to see.
        /// The rooms are compared through what the two results actually published, not through the reservations, so a
        /// pair that merely looked concurrent could not pass.
        /// </para>
        /// </summary>
        [Test]
        public void TwoCutsOfOneStorage_HoldRoomAndRunAtTheSameTime()
        {
            using (Fixture f = NewFixture())
            {
                SyntheticMesh firstMesh = Box();
                SyntheticMesh secondMesh = Box(2);
                (List<string> positive, List<string> negative) firstAlone = CutOnItsOwn(firstMesh, Tilted());
                (List<string> positive, List<string> negative) secondAlone = CutOnItsOwn(secondMesh, Tilted());
                VpStoredGeometry first = Append(f.storage, firstMesh);
                VpStoredGeometry second = Append(f.storage, secondMesh);
                VpStorageCutRequest a = f.runner.Submit(Acquire(f.storage, first), Tilted(), default);
                VpStorageCutRequest b = f.runner.Submit(Acquire(f.storage, second), Tilted(), default);

                bool sawBothHoldingRoom = false;
                bool sawBothWithTheDispatcher = false;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(a.IsOver && b.IsOver))
                {
                    f.Frame();
                    sawBothHoldingRoom |= a.HoldsReservation && b.HoldsReservation;
                    sawBothWithTheDispatcher |=
                        a.Stage == VpStorageCutStage.Cutting && b.Stage == VpStorageCutStage.Cutting;
                    Thread.Sleep(1);
                }

                Assert.That(a.IsOver && b.IsOver, Is.True, "both cuts ended within the deadline");
                Assert.That(a.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the first cut succeeded");
                Assert.That(b.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and so did the second");
                Assert.That(sawBothHoldingRoom, Is.True, "the two held room of their own at the same time");
                Assert.That(sawBothWithTheDispatcher, Is.True, "and were both with the dispatcher at the same time");
                Assert.That(f.runner.ReservingCount, Is.Zero, "nothing is reserved afterwards");
                AssertIsTheSameCutAsOnItsOwn(f, a, firstAlone, "the first cut");
                AssertIsTheSameCutAsOnItsOwn(f, b, secondAlone, "the second cut");
                AssertRoomsDoNotOverlap(f, a, b);
            }
        }

        /// <summary>
        /// The vertices two cuts published do not overlap. Read from the sides they produced, which is where the
        /// vertices really are, rather than from the reservations they were given.
        /// </summary>
        private static void AssertVerticesDoNotOverlap(VpStorageCutRequest a, VpStorageCutRequest b)
        {
            (int start, int count) one = VerticesOf(a);
            (int start, int count) two = VerticesOf(b);
            if (one.count == 0 || two.count == 0)
            {
                return;
            }

            Assert.That(
                one.start >= two.start + two.count || two.start >= one.start + one.count,
                Is.True,
                "the two cuts wrote into different vertices: [" + one.start + ", " + (one.start + one.count) + ") and ["
                + two.start + ", " + (two.start + two.count) + ")");
        }

        /// <summary>The vertices one cut's sides share, as the storage published them.</summary>
        private static (int start, int count) VerticesOf(VpStorageCutRequest request)
        {
            VpStorageCutResult result = request.Result;
            VpStoredGeometry side = result.positive.IsProduced ? result.positive.geometry : result.negative.geometry;
            return (side.vertexStart, side.vertexCount);
        }

        /// <summary>
        /// Two cuts both with a worker, finished in the opposite order to the one they were offered in. Each result is
        /// its own: the sides are the ones that cut produced, and the vertices they published do not overlap. Finishing
        /// second is not finishing behind -- the later cut publishes where its own room is, not after the other's.
        /// </summary>
        [Test]
        public void TwoCutsFinishingInReverseOrder_EachPublishIntoItsOwnRoom()
        {
            var gate = new GatedExecutor();
            using (Fixture f = NewFixture(gate))
            {
                SyntheticMesh firstMesh = Box();
                SyntheticMesh secondMesh = Box(2);
                (List<string> positive, List<string> negative) firstAlone = CutOnItsOwn(firstMesh, Tilted());
                (List<string> positive, List<string> negative) secondAlone = CutOnItsOwn(secondMesh, Tilted());
                VpStoredGeometry first = Append(f.storage, firstMesh);
                VpStoredGeometry second = Append(f.storage, secondMesh);
                VpStorageCutRequest a = f.runner.Submit(Acquire(f.storage, first), Tilted(), default);
                VpStorageCutRequest b = f.runner.Submit(Acquire(f.storage, second), Tilted(), default);

                RunUntilAccepted(f, gate, a, VpStorageCutStage.Cutting);
                RunUntilAccepted(f, gate, b, VpStorageCutStage.Cutting);
                Assert.That(a.HoldsReservation && b.HoldsReservation, Is.True, "both hold room of their own");
                Assert.That(gate.Accepted, Is.EqualTo(2), "and both are with the executor, neither waiting for the other");

                // The one offered second runs and is collected first.
                gate.RunLastOnAWorker();
                f.RunUntilOver(b, "the second cut ends");
                Assert.That(b.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut that finished first succeeded");
                Assert.That(a.IsOver, Is.False, "and the other is still running");
                Assert.That(a.HoldsReservation, Is.True, "still holding its own room");

                gate.RunOneOnAWorker();
                f.RunUntilOver(a, "the first cut ends");
                Assert.That(a.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and the one that finished second succeeded too");

                AssertIsTheSameCutAsOnItsOwn(f, a, firstAlone, "the cut that finished second");
                AssertIsTheSameCutAsOnItsOwn(f, b, secondAlone, "the cut that finished first");
                AssertRoomsDoNotOverlap(f, a, b);
                Assert.That(f.runner.ReservingCount, Is.Zero, "and no room is held afterwards");
            }
        }

        /// <summary>
        /// One of two cuts is given up while its worker still has it. The other keeps its room, finishes and publishes
        /// normally, and what the abandoned one held comes back only when its worker hands it over -- not early, while
        /// that worker may still be writing into it.
        /// </summary>
        [Test]
        public void OneCutAbandonedMidRun_LeavesTheOtherAlone_AndGivesItsRoomBackOnlyOnCollection()
        {
            var gate = new GatedExecutor();
            using (Fixture f = NewFixture(gate))
            {
                SyntheticMesh keptMesh = Box(2);
                (List<string> positive, List<string> negative) keptAlone = CutOnItsOwn(keptMesh, Tilted());
                VpStoredGeometry first = Append(f.storage, Box());
                VpStoredGeometry second = Append(f.storage, keptMesh);
                VpStorageCutRequest doomed = f.runner.Submit(Acquire(f.storage, first), Tilted(), default);
                VpStorageCutRequest kept = f.runner.Submit(Acquire(f.storage, second), Tilted(), default);

                RunUntilAccepted(f, gate, doomed, VpStorageCutStage.Cutting);
                RunUntilAccepted(f, gate, kept, VpStorageCutStage.Cutting);

                Assert.That(f.runner.Abandon(doomed), Is.True, "it is given up");
                Assert.That(
                    doomed.IsOver, Is.False,
                    "but not ended here: a cut a worker is running is marked, never interrupted");
                Assert.That(doomed.HoldsReservation, Is.True, "its room is still its own while its worker has it");
                Assert.That(kept.HoldsReservation, Is.True, "and the other's is untouched");

                // The kept cut runs and publishes while the other is still being given up.
                gate.RunLastOnAWorker();
                f.RunUntilOver(kept, "the kept cut ends");
                Assert.That(kept.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the kept cut succeeded");
                AssertIsTheSameCutAsOnItsOwn(f, kept, keptAlone, "the kept cut");
                Assert.That(doomed.IsOver, Is.False, "the abandoned one has not ended yet");

                gate.RunOneOnAWorker();
                f.RunUntilOver(doomed, "the abandoned cut is collected");
                Assert.That(doomed.Stage, Is.EqualTo(VpStorageCutStage.Abandoned), "it ends abandoned");
                Assert.That(doomed.HoldsReservation, Is.False, "and only then is its room given back");
                Assert.That(f.runner.ReservingCount, Is.Zero, "nothing is held afterwards");
                AssertLeaseWasReturned(f.storage, first, "after the abandoned cut was collected");
            }
        }

        /// <summary>
        /// One cut whose first reservation is too small, beside one that is not. The short one gives its room back and
        /// takes a larger one, and through all of it the other's room and result are untouched: it publishes what it
        /// produced, and the two still do not overlap.
        /// </summary>
        [Test]
        public void OneCutReservingAgain_DoesNotDisturbTheOther()
        {
            var gate = new GatedExecutor();
            using (Fixture f = NewFixture(gate))
            {
                SyntheticMesh retryingMesh = Box(2);
                SyntheticMesh steadyMesh = Box(2);
                (List<string> positive, List<string> negative) retryingAlone = CutOnItsOwn(retryingMesh, Tilted());
                (List<string> positive, List<string> negative) steadyAlone = CutOnItsOwn(steadyMesh, Tilted());
                VpStoredGeometry first = Append(f.storage, retryingMesh);
                VpStoredGeometry second = Append(f.storage, steadyMesh);

                // Far too little for its own output, so it must give this back and ask for more.
                var tight = new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3, scratchBytes = 1 };
                VpStorageCutRequest retrying = f.runner.Submit(Acquire(f.storage, first), Tilted(), tight);
                VpStorageCutRequest steady = f.runner.Submit(Acquire(f.storage, second), Tilted(), default);

                // Both take room and are offered; then the short one runs and comes back for more **while the other
                // is still holding its own room**, which is the order this case is about.
                RunUntilAccepted(f, gate, retrying, VpStorageCutStage.Cutting);
                RunUntilAccepted(f, gate, steady, VpStorageCutStage.Cutting);
                VpCutOutputReservation steadyRoom = steady.Reservation;
                Assert.That(steadyRoom, Is.Not.Null, "the steady cut holds room");

                gate.RunOneOnAWorker();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && retrying.Result.attempts < 1)
                {
                    f.Frame();
                }

                f.Frame();
                Assert.That(retrying.Result.attempts, Is.GreaterThanOrEqualTo(1), "the short attempt was collected");
                Assert.That(retrying.IsOver, Is.False, "and it is asking again rather than failing");
                Assert.That(
                    ReferenceEquals(steady.Reservation, steadyRoom), Is.True,
                    "the other cut's room is the very one it had: re-reserving did not take or move it");
                Assert.That(steady.HoldsReservation, Is.True, "and it still holds it");

                // Let both finish, in whatever order the gate has them.
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(retrying.IsOver && steady.IsOver))
                {
                    if (gate.Accepted > 0)
                    {
                        gate.RunOneOnAWorker();
                    }

                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(retrying.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the retrying cut succeeded");
                Assert.That(retrying.Result.attempts, Is.GreaterThan(1), "after reserving again");
                Assert.That(steady.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and the other was undisturbed");
                AssertIsTheSameCutAsOnItsOwn(f, retrying, retryingAlone, "the retrying cut");
                AssertIsTheSameCutAsOnItsOwn(f, steady, steadyAlone, "the steady cut");
                AssertRoomsDoNotOverlap(f, retrying, steady);
                Assert.That(f.runner.ReservingCount, Is.Zero, "and no room is held afterwards");
            }
        }

        /// <summary>
        /// One cut fails outright beside one that succeeds. The asynchronous route never gives a cut up for the
        /// number of tries, so the failing one asks for more room than the storage holds at all: it waits while the
        /// other is holding room, and ends as a capacity failure once nothing is. The other publishes the geometry it
        /// would have on its own, and the failure leaves nothing behind.
        /// </summary>
        [Test]
        public void OneCutFailing_LeavesTheOtherWithItsOwnResult()
        {
            using (Fixture f = NewFixture())
            {
                SyntheticMesh goodMesh = Box(2);
                (List<string> positive, List<string> negative) goodAlone = CutOnItsOwn(goodMesh, Tilted());
                VpStoredGeometry doomedBox = Append(f.storage, Box());
                VpStoredGeometry goodBox = Append(f.storage, goodMesh);

                // More vertices than the storage has room for at all, so no reservation can ever be given: asking
                // again would not help, and the cut ends as a capacity failure.
                var hopeless = new VpStorageCutOptions
                {
                    newVertexCapacity = f.storage.VertexCapacity + 1,
                    newIndexCapacity = 3,
                };
                VpStorageCutRequest failing = f.runner.Submit(Acquire(f.storage, doomedBox), Tilted(), hopeless);
                VpStorageCutRequest good = f.runner.Submit(Acquire(f.storage, goodBox), Tilted(), default);

                f.RunUntilOver(failing, "the failing cut ends");
                f.RunUntilOver(good, "the other ends");

                Assert.That(
                    failing.Result.status, Is.EqualTo(VpStorageCutStatus.StorageCapacity),
                    "the first cut failed for room the storage does not have");
                Assert.That(failing.Result.positive.IsProduced, Is.False, "and published nothing");
                Assert.That(failing.Result.negative.IsProduced, Is.False);
                Assert.That(good.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the other succeeded all the same");
                AssertIsTheSameCutAsOnItsOwn(f, good, goodAlone, "the cut beside a failure");
                Assert.That(f.runner.ReservingCount, Is.Zero, "and the failure held nothing at the end");
                AssertLeaseWasReturned(f.storage, doomedBox, "after the failure");
                TestContext.WriteLine(
                    "one cut failed as " + failing.Result.status + " after " + failing.Result.attempts
                    + " attempt(s) while the other published its own geometry");
            }
        }

        /// <summary>
        /// Room comes back two ways, and neither leaves anything behind. A cut given up before it was offered returns
        /// everything it took, so rounds of that leave the free room exactly where it was. A cut that commits returns
        /// the part of its reservation it did not write, which is most of it: the estimate reserves generously and a
        /// run writes little, and the difference is free again at once rather than at the end of anything.
        /// <para>
        /// Retiring a published geometry is a different question and is unchanged here: its index range goes back and
        /// its vertices do not.
        /// </para>
        /// </summary>
        [Test]
        public void RoomGivenUpAndRoomNotUsed_AreBothFreeAgainAtOnce()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = Append(f.storage, Box(2));
                int freeAtTheStart = f.storage.FreeVertexRoom;
                int submeshesAtTheStart = f.storage.FreeSubmeshRoom;
                int blocksAtTheStart = f.storage.FreeVertexBlockRoom;

                // Given up before it is ever offered: everything it took goes straight back, every round.
                for (int round = 0; round < 3; round++)
                {
                    VpStorageCutRequest abandoned = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);
                    f.Frame();
                    Assert.That(f.runner.Abandon(abandoned), Is.True, "round " + round + ": it is given up");
                    f.RunUntilOver(abandoned, "round " + round + ": it ends");
                    Assert.That(
                        f.storage.FreeVertexRoom, Is.EqualTo(freeAtTheStart),
                        "round " + round + ": the vertices it held are free again");
                    Assert.That(f.storage.FreeSubmeshRoom, Is.EqualTo(submeshesAtTheStart), "round " + round + ": and its descriptors");
                    Assert.That(f.storage.FreeVertexBlockRoom, Is.EqualTo(blocksAtTheStart), "round " + round + ": and its blocks");
                    Assert.That(
                        f.storage.FreeVertexSpanCount, Is.EqualTo(1),
                        "round " + round + ": the free room is in one piece, so nothing is lost to fragments");
                }

                // And one that really cuts: what it did not write is free the moment it commits.
                VpStorageCutRequest cut = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);
                f.RunUntilOver(cut, "the cut ends");
                Assert.That(cut.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "it succeeded");

                int wrote = cut.Result.positive.IsProduced ? cut.Result.positive.geometry.vertexCount : 0;
                int freeNow = f.storage.FreeVertexRoom;
                TestContext.WriteLine(
                    "free vertex room " + freeAtTheStart + " -> " + freeNow + " after a cut that published "
                    + wrote + " vertices; free spans " + f.storage.FreeVertexSpanCount);
                Assert.That(
                    freeNow, Is.EqualTo(freeAtTheStart - wrote),
                    "only what it published is still taken: the rest of its reservation came back");
                Assert.That(
                    f.storage.LargestFreeVertexSpan, Is.EqualTo(freeNow),
                    "and it came back next to the rest, so the free room is still in one piece");
            }
        }

        /// <summary>Both sides of one cut, each whole and neither overlapping the other in the index space.</summary>
        private static void AssertSidesAreWholeAndDisjoint(VpStorageCutRequest request, string what)
        {
            VpStorageCutResult result = request.Result;
            Assert.That(
                result.positive.IsProduced || result.positive.IsEmpty || result.positive.IsBorrowed,
                Is.True, what + ": the positive side is one of the three kinds");
            Assert.That(
                result.negative.IsProduced || result.negative.IsEmpty || result.negative.IsBorrowed,
                Is.True, what + ": and so is the negative side");
            if (result.positive.IsProduced && result.negative.IsProduced)
            {
                Assert.That(
                    result.positive.geometry.indexRange.Equals(result.negative.geometry.indexRange), Is.False,
                    what + ": the two sides have index ranges of their own");
            }
        }

        /// <summary>
        /// Two kernels of one storage really running at the same time, on workers of their own. Each piece of work is
        /// started on its own thread; the threads meet at a rendezvous **before** the kernel, and what is timed is the
        /// kernel call itself, from just before it to just after. The evidence is that those two spans **overlap** on
        /// two different threads -- arriving together is only what makes the overlap likely, and is not the claim. A
        /// rendezvous that is not reached inside the deadline is recorded and fails the case rather than passing
        /// quietly. The meshes are large enough that a kernel is not over in an instant.
        /// <para>
        /// The rendezvous lives in the executor, which is the test's own. Nothing of the product waits or
        /// synchronises, and the cut asks for nothing of the kind.
        /// </para>
        /// <para>
        /// Queue time, running time and collection are told apart, because being in the queue is not running and being
        /// run is not collected.
        /// </para>
        /// </summary>
        [Test]
        public void TwoKernelsOfOneStorage_RunOnWorkersAtTheSameTime()
        {
            using (var both = new ConcurrentExecutor(2))
            using (Fixture f = NewFixture(both, 65536, 262144))
            {
                // Big enough that a kernel lasts long enough to be caught overlapping, rather than being over inside
                // the moment two threads take to leave the rendezvous.
                SyntheticMesh firstMesh = Box(20);
                SyntheticMesh secondMesh = Box(20);
                (List<string> positive, List<string> negative) firstAlone = CutOnItsOwn(firstMesh, Tilted(), 65536, 262144);
                (List<string> positive, List<string> negative) secondAlone = CutOnItsOwn(secondMesh, Tilted(), 65536, 262144);
                VpStoredGeometry first = Append(f.storage, firstMesh);
                VpStoredGeometry second = Append(f.storage, secondMesh);
                VpStorageCutRequest a = f.runner.Submit(Acquire(f.storage, first), Tilted(), default);
                VpStorageCutRequest b = f.runner.Submit(Acquire(f.storage, second), Tilted(), default);

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(a.IsOver && b.IsOver))
                {
                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(a.IsOver && b.IsOver, Is.True, "both cuts ended within the deadline");
                Assert.That(both.WaitedInVain, Is.False, "neither worker gave up waiting at the rendezvous");
                Assert.That(
                    both.TwoKernelsOverlapped, Is.True,
                    "two kernel calls overlapped in time, on different threads");
                Assert.That(
                    both.ThreadsThatRan.Count, Is.EqualTo(2),
                    "and they were different threads: " + string.Join(", ", both.ThreadsThatRan));
                Assert.That(a.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the first cut succeeded");
                Assert.That(b.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and so did the second");
                AssertIsTheSameCutAsOnItsOwn(f, a, firstAlone, "the first cut");
                AssertIsTheSameCutAsOnItsOwn(f, b, secondAlone, "the second cut");
                AssertRoomsDoNotOverlap(f, a, b);
                TestContext.WriteLine(
                    "queued " + both.Queued + ", run " + both.Started + " (on " + both.ThreadsThatRan.Count
                    + " threads), collected " + both.Collected + "; kernel calls overlapped: " + both.TwoKernelsOverlapped
                    + ", for " + both.OverlapMilliseconds.ToString("0.000") + " ms");
            }
        }

        /// <summary>
        /// One cut fails **after** it has taken room and been accepted by the executor, which is where a worker's
        /// exception falls. Both cuts hold room of their own and are with the executor before either runs; then one
        /// throws where its kernel would be.
        /// <para>
        /// What this shows, and the cancellation case does not: the failure keeps its room until it is collected and
        /// publishes no part of a result, the collection gives back both that room and the input it held, that room
        /// can be used again afterwards, and the other cut keeps its own room and produces what it would have alone.
        /// </para>
        /// </summary>
        [Test]
        public void OneCutThrowingOnItsWorker_KeepsItsRoomUntilCollection_AndTheOtherIsWhole()
        {
            using (var both = new ConcurrentExecutor(1))
            using (Fixture f = NewFixture(both))
            {
                SyntheticMesh goodMesh = Box(2);
                SyntheticMesh doomedMesh = Box();
                (List<string> positive, List<string> negative) goodAlone = CutOnItsOwn(goodMesh, Tilted());
                VpStoredGeometry doomedBox = Append(f.storage, doomedMesh);
                VpStoredGeometry goodBox = Append(f.storage, goodMesh);

                // The input the cut after the failure will use, appended here with the others so that no append falls
                // between the measurements below. It is the same mesh as the doomed one, so it asks for the same room.
                VpStoredGeometry sameShapeAgain = Append(f.storage, doomedMesh);

                // Nothing runs until both have taken room and been accepted, so the failure below is a failure of a
                // cut that already holds a reservation -- not one that never got one.
                both.HoldRunsBack();
                VpStorageCutRequest failing = f.runner.Submit(Acquire(f.storage, doomedBox), Tilted(), default);
                VpStorageCutRequest good = f.runner.Submit(Acquire(f.storage, goodBox), Tilted(), default);

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && both.Queued < 2)
                {
                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(both.Queued, Is.EqualTo(2), "both were accepted by the executor");
                Assert.That(failing.HoldsReservation, Is.True, "the one that will fail holds room");
                Assert.That(good.HoldsReservation, Is.True, "and so does the other");
                VpCutOutputReservation failingRoom = failing.Reservation;
                VpCutOutputReservation goodRoom = good.Reservation;
                AssertRangesAreApart(
                    (failingRoom.VertexStart, failingRoom.NewVertexCapacity),
                    (goodRoom.VertexStart, goodRoom.NewVertexCapacity),
                    "the two reservations hold vertex room of their own");

                // Every region the failure is holding, so that its return can be seen region by region.
                Room heldByTheFailure = RoomOf(failingRoom);
                Room freeWithBothHeld = FreeRoom(f.storage);

                // The work the executor took first is the failing one, and it is the one watched from here on.
                IDispatchWork doomedWork = both.FirstAccepted;
                Assert.That(doomedWork, Is.Not.Null, "the failing cut's work");
                both.ThrowInsteadOfRunning = w => ReferenceEquals(w, doomedWork);
                both.LetThroughAlone(doomedWork);

                // That one work has thrown and is waiting to be handed back. The other is still held before its
                // kernel, so nothing of it can be mistaken for this.
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !both.IsWaitingToBeHandedBack(doomedWork))
                {
                    Thread.Sleep(1);
                }

                Assert.That(
                    both.IsWaitingToBeHandedBack(doomedWork), Is.True,
                    "the failing cut's own work has run and is waiting to be handed back");
                Assert.That(good.IsOver, Is.False, "the other has not even run yet");
                Assert.That(failing.IsOver, Is.False, "and the failure is not over before it is collected");
                Assert.That(
                    ReferenceEquals(failing.Reservation, failingRoom), Is.True,
                    "it still holds the very room it took: nothing is given back early");
                Assert.That(failing.Result.positive.IsProduced, Is.False, "no part of a result is published");
                Assert.That(failing.Result.negative.IsProduced, Is.False);
                AssertFreeRoomIs(f.storage, freeWithBothHeld, "before the failure is collected, nothing came back");

                // Collected -- and only it, because the other is still held back.
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !failing.IsOver)
                {
                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(failing.IsOver, Is.True, "the failure was collected");
                Assert.That(
                    failing.Result.status, Is.EqualTo(VpStorageCutStatus.InternalError),
                    "the worker's exception ended that cut");
                Assert.That(failing.Failure, Is.Not.Null, "and the exception was carried back");
                Assert.That(failing.Result.positive.IsProduced, Is.False, "it published nothing at all");
                Assert.That(failing.Result.negative.IsProduced, Is.False);
                Assert.That(failing.HoldsReservation, Is.False, "the collection gave its room back");
                AssertFreeRoomIs(
                    f.storage, freeWithBothHeld.Plus(heldByTheFailure),
                    "the collection gave back every region it held, and only its own");

                // The other cut is untouched by any of this: still the same reservation, still not run.
                Assert.That(good.IsOver, Is.False, "the other cut is still held before its kernel");
                Assert.That(
                    ReferenceEquals(good.Reservation, goodRoom), Is.True,
                    "and holds the very room it took, through the other's failure and its collection");

                // The room that came back is the room the next reservation is given. The same geometry is cut again,
                // so the sizes asked for are the same and no append is mixed in to move anything.
                VpStorageCutRequest afterwards = f.runner.Submit(Acquire(f.storage, sameShapeAgain), Tilted(), default);
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !afterwards.HoldsReservation)
                {
                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(afterwards.HoldsReservation, Is.True, "the cut after the failure was given room");
                Room takenAgain = RoomOf(afterwards.Reservation);
                Assert.That(takenAgain.vertices, Is.EqualTo(heldByTheFailure.vertices), "the same vertex room, as sizes");
                Assert.That(
                    afterwards.Reservation.VertexStart, Is.EqualTo(failingRoom.VertexStart),
                    "and at the very place the failure gave back: the vertices");
                Assert.That(
                    afterwards.Reservation.IndexStart, Is.EqualTo(failingRoom.IndexStart),
                    "the indices");
                Assert.That(
                    afterwards.Reservation.SubmeshStart, Is.EqualTo(failingRoom.SubmeshStart),
                    "the submesh descriptors");
                Assert.That(
                    afterwards.Reservation.VertexBlockStart, Is.EqualTo(failingRoom.VertexBlockStart),
                    "and the vertex blocks");

                // The input the failure held is free of it: retiring its range succeeds and leaves it Free at once,
                // which a read lease still out would not allow. This comes after the measurements above, because
                // retiring gives index room back and would move what the reservation above was compared against.
                AssertLeaseWasReturned(f.storage, doomedBox, "after the worker threw");

                // Now let everything finish, and the cut that was beside the failure is the cut it would have been.
                both.ThrowInsteadOfRunning = null;
                both.ReleaseRuns();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(good.IsOver && afterwards.IsOver))
                {
                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(good.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the other cut succeeded");
                AssertIsTheSameCutAsOnItsOwn(f, good, goodAlone, "the cut beside a worker's exception");
                Assert.That(
                    afterwards.Result.status, Is.EqualTo(VpStorageCutStatus.Ok),
                    "and so did the one given the room the failure returned");
                Assert.That(both.WaitedInVain, Is.False, "no worker gave up waiting to be let through");
                TestContext.WriteLine(
                    "a worker threw after its reservation: free vertices " + freeWithBothHeld.vertices + " while held, "
                    + (freeWithBothHeld.vertices + heldByTheFailure.vertices) + " once collected; the next reservation took "
                    + "vertices at " + failingRoom.VertexStart + ", indices at " + failingRoom.IndexStart
                    + ", submeshes at " + failingRoom.SubmeshStart + ", blocks at " + failingRoom.VertexBlockStart);
            }
        }

        /// <summary>How much of each region something holds, or how much of each is free.</summary>
        private readonly struct Room
        {
            internal readonly int vertices;
            internal readonly int indices;
            internal readonly int submeshes;
            internal readonly int blocks;

            internal Room(int vertices, int indices, int submeshes, int blocks)
            {
                this.vertices = vertices;
                this.indices = indices;
                this.submeshes = submeshes;
                this.blocks = blocks;
            }

            internal Room Plus(Room other)
            {
                return new Room(
                    vertices + other.vertices, indices + other.indices, submeshes + other.submeshes, blocks + other.blocks);
            }
        }

        private static Room RoomOf(VpCutOutputReservation reservation)
        {
            return new Room(
                reservation.NewVertexCapacity, reservation.NewIndexCapacity, reservation.SubmeshCapacity,
                reservation.VertexBlockCapacity);
        }

        private static Room FreeRoom(VpCpuGeometryStorage storage)
        {
            return new Room(
                storage.FreeVertexRoom, storage.FreeIndexRoom, storage.FreeSubmeshRoom, storage.FreeVertexBlockRoom);
        }

        /// <summary>Every region's free amount at once, so that a return is seen region by region and not just in the VB.</summary>
        private static void AssertFreeRoomIs(VpCpuGeometryStorage storage, Room expected, string what)
        {
            Assert.That(storage.FreeVertexRoom, Is.EqualTo(expected.vertices), what + ": the vertices");
            Assert.That(storage.FreeIndexRoom, Is.EqualTo(expected.indices), what + ": the indices");
            Assert.That(storage.FreeSubmeshRoom, Is.EqualTo(expected.submeshes), what + ": the submesh descriptors");
            Assert.That(storage.FreeVertexBlockRoom, Is.EqualTo(expected.blocks), what + ": the vertex blocks");
        }

        /// <summary>
        /// A geometry pool that really runs its work on threads of its own, several at a time. It brings
        /// <c>together</c> threads to a rendezvous **before** the kernel and then times the kernel call itself, so
        /// what it reports is when the runs really overlapped and not merely when the threads arrived. A rendezvous
        /// that times out is recorded and made to fail the test rather than passing quietly.
        /// <para>
        /// It can also hold every run back until the test releases it, and make one chosen piece of work throw where
        /// the kernel would be, which is a worker's exception as the dispatcher sees one.
        /// </para>
        /// <para>
        /// What it counts is told apart: taken into the queue, begun on a thread, and handed back.
        /// </para>
        /// </summary>
        private sealed class ConcurrentExecutor : IWorkExecutor, IDisposable
        {
            private readonly object _lock = new object();
            private readonly List<Thread> _threads = new List<Thread>();
            private readonly Queue<KeyValuePair<IDispatchWork, WorkCompletion>> _finished =
                new Queue<KeyValuePair<IDispatchWork, WorkCompletion>>();
            private readonly System.Collections.Generic.HashSet<int> _threadsThatRan = new System.Collections.Generic.HashSet<int>();
            private readonly List<KernelRun> _runs = new List<KernelRun>();
            private readonly ManualResetEventSlim _released = new ManualResetEventSlim(true);
            private readonly Barrier _rendezvous;
            private IDispatchWork _letThrough;
            private bool _closed;
            private int _held;

            internal ConcurrentExecutor(int together)
            {
                _rendezvous = together > 1 ? new Barrier(together) : null;
            }

            /// <summary>One kernel call, as the thread that made it saw it.</summary>
            internal struct KernelRun
            {
                internal int thread;
                internal long begunAt;
                internal long endedAt;
            }

            public WorkDestination Destination => WorkDestination.GeometryPool;

            public int Capacity => 4;

            public int Held => Volatile.Read(ref _held);

            public bool CanAccept => !_closed && Held < Capacity;

            /// <summary>How many pieces of work were taken into the queue.</summary>
            internal int Queued { get; private set; }

            /// <summary>How many were begun on a thread.</summary>
            internal int Started;

            /// <summary>How many were handed back to the main thread.</summary>
            internal int Collected { get; private set; }

            /// <summary>Set when a rendezvous or a release was not reached inside the deadline.</summary>
            internal bool WaitedInVain;

            /// <summary>Chooses the one piece of work that throws where its kernel would be. Null makes none throw.</summary>
            internal Func<IDispatchWork, bool> ThrowInsteadOfRunning;

            /// <summary>The first piece of work this executor took, which is the first that was offered to it.</summary>
            internal IDispatchWork FirstAccepted { get; private set; }

            /// <summary>How many runs are waiting to be handed back.</summary>
            internal int Waiting
            {
                get
                {
                    lock (_lock)
                    {
                        return _finished.Count;
                    }
                }
            }

            /// <summary>
            /// Whether two kernel calls really overlapped: two runs on different threads, each begun before the other
            /// had ended. Read after the runs are over.
            /// </summary>
            internal bool TwoKernelsOverlapped
            {
                get
                {
                    lock (_lock)
                    {
                        for (int i = 0; i < _runs.Count; i++)
                        {
                            for (int j = i + 1; j < _runs.Count; j++)
                            {
                                KernelRun a = _runs[i];
                                KernelRun b = _runs[j];
                                if (a.thread != b.thread && a.begunAt < b.endedAt && b.begunAt < a.endedAt)
                                {
                                    return true;
                                }
                            }
                        }

                        return false;
                    }
                }
            }

            /// <summary>How long the kernel calls overlapped, in milliseconds, for the record.</summary>
            internal double OverlapMilliseconds
            {
                get
                {
                    lock (_lock)
                    {
                        double most = 0;
                        for (int i = 0; i < _runs.Count; i++)
                        {
                            for (int j = i + 1; j < _runs.Count; j++)
                            {
                                KernelRun a = _runs[i];
                                KernelRun b = _runs[j];
                                if (a.thread == b.thread)
                                {
                                    continue;
                                }

                                long from = Math.Max(a.begunAt, b.begunAt);
                                long to = Math.Min(a.endedAt, b.endedAt);
                                if (to > from)
                                {
                                    most = Math.Max(most, (to - from) * 1000.0 / Stopwatch.Frequency);
                                }
                            }
                        }

                        return most;
                    }
                }
            }

            /// <summary>Holds every run just before its kernel until <see cref="ReleaseRuns"/>.</summary>
            internal void HoldRunsBack()
            {
                _released.Reset();
            }

            /// <summary>Lets the held runs go on into their kernels.</summary>
            internal void ReleaseRuns()
            {
                _released.Set();
            }

            /// <summary>Lets one held run, and only that one, go on into its kernel.</summary>
            internal void LetThroughAlone(IDispatchWork work)
            {
                lock (_lock)
                {
                    _letThrough = work;
                }
            }

            /// <summary>Whether that one piece of work has run and is waiting to be handed back to the main thread.</summary>
            internal bool IsWaitingToBeHandedBack(IDispatchWork work)
            {
                lock (_lock)
                {
                    foreach (KeyValuePair<IDispatchWork, WorkCompletion> ended in _finished)
                    {
                        if (ReferenceEquals(ended.Key, work))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            /// <summary>Waits until this run is let through, and says whether it was rather than timing out.</summary>
            private bool WaitToBeLetThrough(IDispatchWork work)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    if (_released.IsSet)
                    {
                        return true;
                    }

                    lock (_lock)
                    {
                        if (ReferenceEquals(_letThrough, work))
                        {
                            return true;
                        }
                    }

                    Thread.Sleep(1);
                }

                return false;
            }

            /// <summary>The threads work really ran on.</summary>
            internal System.Collections.Generic.HashSet<int> ThreadsThatRan
            {
                get
                {
                    lock (_lock)
                    {
                        return new System.Collections.Generic.HashSet<int>(_threadsThatRan);
                    }
                }
            }

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                Queued++;
                if (FirstAccepted == null)
                {
                    FirstAccepted = work;
                }

                Interlocked.Increment(ref _held);
                var thread = new Thread(() => Run(work)) { IsBackground = true };
                lock (_lock)
                {
                    _threads.Add(thread);
                }

                thread.Start();
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                // A pool's own worker begins what it accepted; this one started its thread already.
            }

            private void Run(IDispatchWork work)
            {
                Interlocked.Increment(ref Started);
                lock (_lock)
                {
                    _threadsThatRan.Add(Thread.CurrentThread.ManagedThreadId);
                }

                // Everything that holds a thread back happens **before** the kernel, so that what is timed below is
                // the kernel call and nothing else. This waiting is the executor's, which is the test's: the cut asks
                // for nothing of the kind.
                if (!WaitToBeLetThrough(work))
                {
                    WaitedInVain = true;
                }

                if (_rendezvous != null)
                {
                    try
                    {
                        if (!_rendezvous.SignalAndWait(DeadlineMilliseconds))
                        {
                            WaitedInVain = true;
                        }
                    }
                    catch (BarrierPostPhaseException)
                    {
                        WaitedInVain = true;
                    }
                }

                Exception failure = null;
                long begunAt = Stopwatch.GetTimestamp();
                try
                {
                    if (ThrowInsteadOfRunning != null && ThrowInsteadOfRunning(work))
                    {
                        throw new InvalidOperationException("the worker threw where its kernel would be");
                    }

                    work.Begin();
                }
                catch (Exception e)
                {
                    failure = e;
                }

                long endedAt = Stopwatch.GetTimestamp();
                lock (_lock)
                {
                    _runs.Add(new KernelRun
                    {
                        thread = Thread.CurrentThread.ManagedThreadId,
                        begunAt = begunAt,
                        endedAt = endedAt,
                    });
                }

                lock (_lock)
                {
                    _finished.Enqueue(new KeyValuePair<IDispatchWork, WorkCompletion>(
                        work, failure == null ? WorkCompletion.Finished : WorkCompletion.Failed(failure)));
                }
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                lock (_lock)
                {
                    if (_finished.Count == 0)
                    {
                        work = null;
                        completion = default;
                        return false;
                    }

                    KeyValuePair<IDispatchWork, WorkCompletion> ended = _finished.Dequeue();
                    Interlocked.Decrement(ref _held);
                    Collected++;
                    work = ended.Key;
                    completion = ended.Value;
                    return true;
                }
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            /// <summary>Lets go of anything still held back, so no thread is left waiting at shutdown.</summary>
            public void Dispose()
            {
                _released.Set();
                _rendezvous?.Dispose();
                _released.Dispose();
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                _released.Set();
                List<Thread> running;
                lock (_lock)
                {
                    running = new List<Thread>(_threads);
                }

                foreach (Thread thread in running)
                {
                    thread.Join(timeoutMilliseconds);
                }

                return true;
            }
        }

        // ----- giving up -----------------------------------------------------------------------------------------------

        /// <summary>A cut given up before it was ever offered gives its input lease back there and then.</summary>
        [Test]
        public void ACutGivenUpBeforeItIsOffered_GivesEverythingBackAtOnce()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = Append(f.storage, Box(2));
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);

                Assert.That(f.runner.Abandon(request), Is.True, "it is given up");

                Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Abandoned), "and is over");
                Assert.That(f.runner.ActiveCount, Is.Zero, "the runner holds nothing");
                Assert.That(f.runner.ReservingCount, Is.Zero, "nothing was reserved");
                AssertLeaseWasReturned(f.storage, box, "after giving up before the offer");
            }
        }

        /// <summary>
        /// A cut given up while a worker is running it is never interrupted. Nothing is published from it, everything
        /// it held comes back once the dispatcher hands it over, and the storage is free for the next cut.
        /// </summary>
        [Test]
        public void ACutGivenUpWhileItRuns_IsNotInterrupted_AndGivesEverythingBackWhenItIsCollected()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = Append(f.storage, Box());
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);

                // Let it get as far as the cut itself, so that giving up cannot be a cancellation in the queue.
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && request.Stage != VpStorageCutStage.Cutting)
                {
                    f.Frame();
                    Thread.Sleep(1);
                }

                Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Cutting), "the cut itself is with a worker");
                Assert.That(request.HoldsReservation, Is.True, "with its reservation open");
                Assert.That(f.runner.Abandon(request), Is.True, "it is given up");

                f.RunUntilOver(request, "the abandoned cut comes back");

                Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Abandoned), "it ended as abandoned");
                Assert.That(request.Result.status, Is.Not.EqualTo(VpStorageCutStatus.Ok), "nothing was published from it");
                Assert.That(f.runner.ReservingCount, Is.Zero, "the reservation is back");
                AssertLeaseWasReturned(f.storage, box, "after giving up a running cut");

                // And the storage is usable again: another cut of another geometry goes through.
                VpStoredGeometry other = Append(f.storage, Box(2));
                VpStorageCutRequest next = f.runner.Submit(Acquire(f.storage, other), Tilted(), default);
                f.RunUntilOver(next, "the cut after the abandoned one");
                Assert.That(next.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the next cut is unaffected");
            }
        }

        // ----- a worker that fell over ------------------------------------------------------------------------------------

        /// <summary>
        /// A destination that hands the work back as failed ends the cut with that exception and leaves nothing held:
        /// no reservation, no lease, nothing published. The dispatcher's own contract is that a pool never lets an
        /// exception escape, so this is the shape a failure really arrives in.
        /// </summary>
        [Test]
        public void AWorkerThatFellOver_EndsTheCutWithItsFailure_AndLeavesNothingHeld()
        {
            var failing = new FailingExecutor(WorkDestination.GeometryPool, 4);
            using (Fixture f = NewFixture(failing))
            {
                VpStoredGeometry box = Append(f.storage, Box(2));
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);

                f.RunUntilOver(request);

                Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Finished), "it is over");
                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.InternalError), "as an internal error");
                Assert.That(request.Failure, Is.SameAs(failing.thrown), "carrying what the worker threw");
                Assert.That(f.runner.ReservingCount, Is.Zero, "nothing is reserved");
                Assert.That(f.runner.ActiveCount, Is.Zero, "and the runner holds nothing");
                AssertLeaseWasReturned(f.storage, box, "after a worker fell over");
            }
        }

        // ----- what the sizes cost, before and after -------------------------------------------------------------------

        /// <summary>
        /// The comparison this unit is answerable for, over a few representative inputs: a small ordinary cut, a
        /// larger one, and a plane that leaves everything on one side. For each, what a pass over every triangle
        /// would have reserved is worked out and written down beside what the size alone reserves and what the run
        /// really used, with the attempts it took and the number of worker submissions the dispatcher saw.
        /// <para>
        /// The figures are recorded, not judged: which of them is larger depends on how the estimate is tuned. What is
        /// asserted is what has to hold whatever it is tuned to -- the cut succeeds, the product asks for no pre-scan,
        /// and each attempt is one submission.
        /// </para>
        /// </summary>
        [Test]
        public void TheSizes_CostNoPassOverTheGeometry_AndWhatTheyReserveIsRecorded()
        {
            var cases = new (string what, SyntheticMesh mesh, float4 plane)[]
            {
                ("a small box, cut across", Box(2), new float4(0f, 1f, 0f, 0f)),
                ("a larger box, cut on the slant", Box(8), Tilted()),
                ("a plane that misses: everything on one side", Box(3), new float4(0f, 1f, 0f, -8f)),
            };

            foreach ((string what, SyntheticMesh mesh, float4 plane) in cases)
            {
                var counter = new CountingExecutor();
                using (Fixture f = NewFixture(counter, vertexCapacity: 262144, indexCapacity: 1048576))
                {
                    VpStoredGeometry geometry = Append(f.storage, mesh);

                    // What a pre-scan would have said. The product no longer runs this; it is worked out here only so
                    // that the two can be put side by side.
                    VpStorageCutInput scanned = Acquire(f.storage, geometry);
                    Assert.That(scanned.TryGetInput(plane, out MeshCutInput kernelInput), Is.True);
                    MeshCutCapacity byScan = default;
                    MeshCutKernel.QueryCapacity(in kernelInput, ref byScan);
                    MeshCutCapacity bySize = default;
                    MeshCutKernel.EstimateCapacity(in kernelInput, ref bySize);
                    scanned.Dispose();

                    VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, geometry), plane, default);
                    f.RunUntilOver(request);

                    Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Finished), what + ": it finished");
                    Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), what + ": with a cut");
                    Assert.That(
                        counter.Runs, Is.EqualTo(request.Result.attempts),
                        what + ": one worker submission per attempt, and none for measuring the input");

                    // What the run wrote into its own reservation, told apart from what a borrowed side carries:
                    // a borrowed side's indices are the input's own and nothing was written for them.
                    MeshCutResult kernel = request.Result.kernel;
                    bool borrowed = request.Result.positive.IsBorrowed || request.Result.negative.IsBorrowed;
                    string written = borrowed
                        ? "none: the input was borrowed"
                        : "vertices " + kernel.newVertexCount + ", indices " + kernel.newIndexCount;
                    string carried = borrowed
                        ? "the input's own " + (kernel.positive.indexCount + kernel.negative.indexCount) + " indices, borrowed"
                        : "none";

                    TestContext.WriteLine(
                        what + ":"
                        + " triangles " + byScan.triangleCount
                        + "; crossings counted " + byScan.crossingTriangles
                        + "; QueryCapacity (computed here, not run by the product): vertices " + byScan.newVertices
                        + ", indices " + byScan.newIndices + ", scratch " + byScan.scratchBytes
                        + "; EstimateCapacity, what the product reserved from: vertices " + bySize.newVertices
                        + ", indices " + bySize.newIndices + ", scratch " + bySize.scratchBytes
                        + "; written into the reservation: " + written
                        + "; carried without writing: " + carried
                        + "; scratch used " + kernel.usedScratchBytes
                        + "; attempts " + request.Result.attempts
                        + "; worker submissions " + counter.Runs
                        + "; pre-scans for capacity: before 1, now 0");
                }
            }
        }

        /// <summary>
        /// A geometry pool that runs each piece of work at once on a worker thread of its own and counts them. The
        /// count is the number of submissions the dispatcher made, which is what the comparison is about.
        /// </summary>
        private sealed class CountingExecutor : IWorkExecutor
        {
            private readonly Queue<KeyValuePair<IDispatchWork, WorkCompletion>> _finished =
                new Queue<KeyValuePair<IDispatchWork, WorkCompletion>>();
            private bool _closed;
            private int _held;

            public WorkDestination Destination => WorkDestination.GeometryPool;

            public int Capacity => 4;

            public int Held => _held;

            public bool CanAccept => !_closed && _held < Capacity;

            /// <summary>How many pieces of work this destination has been given.</summary>
            internal int Runs { get; private set; }

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _held++;
                Runs++;
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
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                // A pool's own worker begins what it accepted; here it has already run.
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
                _held--;
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
                return true;
            }
        }

        // ----- closing the runner while a worker still has a cut -------------------------------------------------------

        /// <summary>
        /// Closing the runner while the cut itself is with a worker, its output reservation open: the cut runs to the
        /// end, and the collection after the close gives back the reservation, the scratch and the input lease without
        /// publishing anything.
        /// </summary>
        [Test]
        public void ClosingWhileTheCutIsWithAWorker_GivesBackTheReservationWhenItIsCollected()
        {
            var gate = new GatedExecutor();
            using (Fixture f = NewFixture(gate))
            {
                VpStoredGeometry box = Append(f.storage, Box(2));
                VpStorageCutRequest request = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);

                RunUntilAccepted(f, gate, request, VpStorageCutStage.Cutting);

                Assert.That(request.HoldsReservation, Is.True, "the output reservation is open");
                Assert.That(f.runner.ReservingCount, Is.EqualTo(1), "and it is the one cut holding room");
                Assert.That(f.dispatcher.Cancel(request.ticket), Is.False, "the cut is submitted, not merely queued");
                f.runner.Dispose();
                gate.RunOneOnAWorker();

                DrainAndAssertNothingIsLeft(f, request, box, "the cut");
            }
        }

        /// <summary>Frames until the stage's work has been accepted by the gate and is no longer cancellable.</summary>
        private static void RunUntilAccepted(Fixture f, GatedExecutor gate, VpStorageCutRequest request, VpStorageCutStage stage)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < DeadlineMilliseconds
                   && !(gate.Accepted > 0 && request.Stage == stage))
            {
                f.Frame();
            }

            Assert.That(request.Stage, Is.EqualTo(stage), "the cut reached " + stage);
            Assert.That(
                gate.Accepted, Is.GreaterThan(0),
                "and work is with the destination; several cuts may be offered in the one pump");
        }

        /// <summary>
        /// Dispatches and pumps until the closed runner has taken its cut back, then checks that nothing of it is
        /// left: no reservation, no scratch, no range array, no read lease, and no result published.
        /// </summary>
        private static void DrainAndAssertNothingIsLeft(Fixture f, VpStorageCutRequest request, VpStoredGeometry box, string what)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !request.IsOver)
            {
                f.Frame();
            }

            Assert.That(request.IsOver, Is.True, "the closed runner took " + what + " back");
            Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Abandoned), "as abandoned");
            Assert.That(request.Result.status, Is.Not.EqualTo(VpStorageCutStatus.Ok), "publishing nothing");
            Assert.That(f.runner.ReservingCount, Is.Zero, "the storage's reservation is free");
            Assert.That(request.HoldsReservation, Is.False, "and the cut holds none");
            Assert.That(request.scratch.IsCreated, Is.False, "the scratch is disposed");
            Assert.That(request.outputRanges.IsCreated, Is.False, "the range array is disposed");
            Assert.That(f.runner.ActiveCount, Is.Zero, "and the runner holds nothing");
            AssertLeaseWasReturned(f.storage, box, "after closing during " + what);
        }

        /// <summary>
        /// A destination the test starts by hand: it accepts work and holds it, and runs it — really on another
        /// thread, once, to the end — only when the test says so. This is how a test can be at a point where the work
        /// is submitted and no longer cancellable, and decide what happens next.
        /// </summary>
        private sealed class GatedExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _accepted = new List<IDispatchWork>();
            private readonly Queue<KeyValuePair<IDispatchWork, WorkCompletion>> _finished =
                new Queue<KeyValuePair<IDispatchWork, WorkCompletion>>();
            private bool _closed;

            public WorkDestination Destination => WorkDestination.GeometryPool;

            public int Capacity => 4;

            public int Held => _accepted.Count + _finished.Count;

            public bool CanAccept => !_closed && Held < Capacity;

            /// <summary>How many pieces of work are held and not begun.</summary>
            public int Accepted => _accepted.Count;

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _accepted.Add(work);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                // A pool's own worker begins what it accepted; here the test decides when.
            }

            /// <summary>Runs the work held longest on a worker thread of its own, to the end.</summary>
            public void RunOneOnAWorker()
            {
                RunOnAWorker(0);
            }

            /// <summary>
            /// Runs the work held **shortest** on a worker of its own, so that a case can have two cuts finish in the
            /// opposite order to the one they were offered in.
            /// </summary>
            public void RunLastOnAWorker()
            {
                Assert.That(_accepted.Count, Is.GreaterThan(0), "there is work to run");
                RunOnAWorker(_accepted.Count - 1);
            }

            private void RunOnAWorker(int index)
            {
                Assert.That(_accepted.Count, Is.GreaterThan(index), "there is work to run");
                IDispatchWork work = _accepted[index];
                _accepted.RemoveAt(index);
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

        /// <summary>A destination that accepts work and always brings it back as failed, without running it.</summary>
        private sealed class FailingExecutor : IWorkExecutor
        {
            internal readonly Exception thrown = new InvalidOperationException("the worker fell over");
            private readonly Queue<IDispatchWork> _held = new Queue<IDispatchWork>();
            private bool _closed;

            public FailingExecutor(WorkDestination destination, int capacity)
            {
                Destination = destination;
                Capacity = capacity;
            }

            public WorkDestination Destination { get; }

            public int Capacity { get; }

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
    }
}
