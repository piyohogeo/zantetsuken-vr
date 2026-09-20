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
                Assert.That(f.runner.Reserving, Is.Null, "and nothing of the storage is still reserved");
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
                Assert.That(f.runner.Reserving, Is.SameAs(request), "and it is this cut that holds it");

                gate.RunOneOnAWorker();
                f.RunUntilOver(request);

                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut is not a failure");
                Assert.That(request.Result.negative.IsBorrowed, Is.True, "the side that keeps it all borrows the input");
                Assert.That(request.Result.negative.geometry.indexRange, Is.EqualTo(box.indexRange), "which is the input itself");
                Assert.That(request.Result.positive.IsEmpty, Is.True, "and the other side is empty");
                Assert.That(request.Result.positive.geometry.indexRange, Is.EqualTo(default(VpIndexRangeHandle)), "with no range of its own");
                Assert.That(f.runner.Reserving, Is.Null, "the reservation is not held any more");
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
                Assert.That(f.runner.Reserving, Is.Null, "the last reservation is committed or gone");
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
                Assert.That(f.runner.Reserving, Is.Null, "and the reservation is back");
            }
        }

        // ----- two cuts over one reservation ---------------------------------------------------------------------------

        /// <summary>
        /// Two cuts asked for together: the storage keeps one cut reservation at a time, so the second waits for the
        /// first — unoffered, not refused and not blocking the main thread — and runs as soon as the first has given
        /// the reservation back. Neither is failed for the other's sake.
        /// </summary>
        [Test]
        public void TwoCuts_DoNotCollideOverTheOneReservation_AndTheSecondFollowsTheFirst()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry first = Append(f.storage, Box());
                VpStoredGeometry second = Append(f.storage, Box(2));
                VpStorageCutRequest a = f.runner.Submit(Acquire(f.storage, first), Tilted(), default);
                VpStorageCutRequest b = f.runner.Submit(Acquire(f.storage, second), Tilted(), default);

                bool sawOneWaitingForTheOther = false;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(a.IsOver && b.IsOver))
                {
                    f.Frame();
                    Assert.That(
                        a.HoldsReservation && b.HoldsReservation,
                        Is.False,
                        "the two never hold the storage's one reservation at the same time");
                    if (a.HoldsReservation && b.Stage == VpStorageCutStage.Ready)
                    {
                        sawOneWaitingForTheOther = true;
                    }

                    Thread.Sleep(1);
                }

                Assert.That(a.IsOver && b.IsOver, Is.True, "both cuts ended within the deadline");
                Assert.That(a.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the first cut succeeded");
                Assert.That(b.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and so did the second");
                Assert.That(sawOneWaitingForTheOther, Is.True, "the second really did wait, ready and unoffered, for the first");
                Assert.That(f.runner.Reserving, Is.Null, "nothing is reserved afterwards");
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
                Assert.That(f.runner.Reserving, Is.Null, "nothing was reserved");
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
                Assert.That(f.runner.Reserving, Is.Null, "the reservation is back");
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
                Assert.That(f.runner.Reserving, Is.Null, "nothing is reserved");
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
                Assert.That(f.runner.Reserving, Is.SameAs(request), "and it is the one cut holding it");
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
            while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(gate.Accepted == 1 && request.Stage == stage))
            {
                f.Frame();
            }

            Assert.That(request.Stage, Is.EqualTo(stage), "the cut reached " + stage);
            Assert.That(gate.Accepted, Is.EqualTo(1), "and its work is with the destination");
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
            Assert.That(f.runner.Reserving, Is.Null, "the storage's reservation is free");
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
