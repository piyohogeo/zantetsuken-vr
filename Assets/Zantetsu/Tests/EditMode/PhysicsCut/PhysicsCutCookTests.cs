using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The physics cut and cook of one owner, run the way the product runs it (DESIGN 4.3, 4.4, 7.2, 7.3): the
    /// existing numerical kernel and the bake offered to the shared dispatcher as two pieces of work, the collider
    /// meshes applied on the main thread between them, and the unpublished products taken back on the main thread.
    /// <para>
    /// Nothing here publishes anything: no actor, no shape, no logical publication and no display. The numbers are
    /// checked against the same kernel run directly, so that this connection is what is under test and not the
    /// kernel.
    /// </para>
    /// </summary>
    public unsafe class PhysicsCutCookTests
    {
        private const int DeadlineMilliseconds = 30000;

        private sealed class Fixture : IDisposable
        {
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public PhysicsCutCook cook;
            private int _frame;

            public void Pump()
            {
                dispatcher.BeginFrame(++_frame);
                dispatcher.Dispatch();
                cook.Pump();
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

                    System.Threading.Thread.Sleep(1);
                }

                Assert.Fail(what + ": it had not happened when the deadline passed");
            }

            public void Dispose()
            {
                cook?.Dispose();
                var clock = Stopwatch.StartNew();
                while (cook != null && !cook.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Pump();
                }

                dispatcher?.Shutdown(DeadlineMilliseconds);
                cook?.Pump();
                geometry?.Dispose();
                background?.Dispose();
            }
        }

        private static Fixture NewFixture(
            int reservations = 1,
            int waitingCapacity = 8,
            int reservedForUrgent = 2,
            int frameBudget = 32,
            int jobCapacity = 4)
        {
            var f = new Fixture
            {
                job = new UnityJobWorkExecutor(jobCapacity),
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(2),
            };
            f.dispatcher = new SharedWorkDispatcher(waitingCapacity, reservedForUrgent, frameBudget, f.job, f.geometry, f.background);
            f.cook = new PhysicsCutCook(f.dispatcher, reservations);
            return f;
        }

        /// <summary>A piece of urgent work that occupies the destination until the test lets it go.</summary>
        private sealed class Blocker : IDispatchWork
        {
            internal bool released;
            internal bool collected;

            public void Begin()
            {
            }

            public bool IsComplete => released;

            public void Collect(WorkCompletion completion)
            {
                collected = true;
            }
        }

        /// <summary>
        /// A small compound the plane really mixes: two boxes it crosses and two it misses, one on each side. The
        /// harness is the existing kernel harness, so the input is built and classified exactly as the kernel's own
        /// tests build it.
        /// </summary>
        private static OwnerCutHarness MixedCompound()
        {
            var h = new OwnerCutHarness();
            h.planeN = new float3(0f, 1f, 0f);
            h.planeW = 0f;
            h.eps = 1e-5f;
            h.parentMass = 12.0;
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 0.0, 0.0)));      // crosses y = 0
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, 0.0, 0.0)));      // crosses y = 0
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 3.0, 0.0)));      // wholly above
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, -3.0, 0.0)));     // wholly below
            h.Build();
            return h;
        }

        private static ConvexPoly Translated(ConvexPoly poly, double3 by)
        {
            var moved = new double3[poly.V.Length];
            for (int i = 0; i < poly.V.Length; i++)
            {
                moved[i] = poly.V[i] + by;
            }

            return new ConvexPoly { V = moved, F = poly.F };
        }

        /// <summary>Runs the same input through the kernel directly, as the reference to compare against.</summary>
        private static ConvexCutOwnerResult Reference(OwnerCutHarness h, out ConvexCutOutcome[] outcomes)
        {
            ConvexCutOwnerResult r = h.Execute();
            outcomes = new ConvexCutOutcome[h.input.convexCount];
            for (int c = 0; c < outcomes.Length; c++)
            {
                outcomes[c] = h.Outcome(c);
            }

            return r;
        }

        private static void AssertSameNumbers(in ConvexCutOwnerResult expected, in ConvexCutOwnerResult actual, string what)
        {
            Assert.That(actual.status, Is.EqualTo(expected.status), what + ": status");
            Assert.That(actual.splitConvexCount, Is.EqualTo(expected.splitConvexCount), what + ": split convexes");
            Assert.That(actual.positiveConvexCount, Is.EqualTo(expected.positiveConvexCount), what + ": positive convexes");
            Assert.That(actual.negativeConvexCount, Is.EqualTo(expected.negativeConvexCount), what + ": negative convexes");
            Assert.That(actual.positiveMass, Is.EqualTo(expected.positiveMass).Within(1e-12), what + ": positive mass");
            Assert.That(actual.negativeMass, Is.EqualTo(expected.negativeMass).Within(1e-12), what + ": negative mass");
            Assert.That(actual.positiveVolume, Is.EqualTo(expected.positiveVolume).Within(1e-12), what + ": positive volume");
            Assert.That(actual.negativeVolume, Is.EqualTo(expected.negativeVolume).Within(1e-12), what + ": negative volume");
            for (int i = 0; i < 3; i++)
            {
                Assert.That(actual.positiveCenterOfMass[i], Is.EqualTo(expected.positiveCenterOfMass[i]).Within(1e-12), what + ": positive centre");
                Assert.That(actual.negativeCenterOfMass[i], Is.EqualTo(expected.negativeCenterOfMass[i]).Within(1e-12), what + ": negative centre");
            }
        }

        // ----- the whole way through --------------------------------------------------------------------------------

        /// <summary>
        /// One owner cut all the way: the numbers match the kernel run directly, the convexes the plane missed are
        /// borrowed on their own side with no mesh, the ones it split are produced with a baked mesh of their own,
        /// and the mass of the two sides adds up to the parent's.
        /// </summary>
        [Test]
        public void OneOwnerCut_MatchesTheKernel_AndCooksOnlyWhatItProduced()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                ConvexCutOwnerResult expected = Reference(h, out ConvexCutOutcome[] expectedOutcomes);
                Assert.That(expected.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), "the reference run");
                Assert.That(expected.splitConvexCount, Is.EqualTo(2), "two convexes are split");

                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                f.RunUntil(() => request.IsOver, "the cut and cook end");

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "it ended with products");
                PhysicsCutProducts products = request.Products;
                Assert.That(products, Is.Not.Null);
                using (products)
                {
                    AssertSameNumbers(in expected, products.Result, "through the connection");
                    Assert.That(
                        products.Result.positiveMass + products.Result.negativeMass,
                        Is.EqualTo(h.parentMass).Within(1e-9),
                        "the two sides carry the parent's mass");
                    Assert.That(products.Cooking, Is.EqualTo(PhysicsCutCook.DefaultCooking), "the profile is carried with them");
                    Assert.That(products.LocalToOwner, Is.EqualTo(float4x4.identity), "and so is the transform to the owner");

                    int borrowed = 0;
                    int produced = 0;
                    foreach (bool side in new[] { true, false })
                    {
                        for (int i = 0; i < products.PartCount(side); i++)
                        {
                            PhysicsCutPart part = products.Part(side, i);
                            ConvexCutOutcome outcome = expectedOutcomes[part.inputConvex];
                            if (part.borrowed)
                            {
                                borrowed++;
                                Assert.That(outcome.IsSplit, Is.False, "a borrowed part is a convex the plane did not split");
                                Assert.That(outcome.InheritsPositive, Is.EqualTo(side), "and it is on the side it inherited");
                                Assert.That(part.mesh, Is.Null, "nothing is made or cooked for it");
                                Assert.That(
                                    part.range.vertexBase, Is.EqualTo(h.input.convexes[part.inputConvex].vertexBase),
                                    "it names the input convex itself");
                                continue;
                            }

                            produced++;
                            Assert.That(outcome.IsSplit, Is.True, "a produced part comes from a convex that was split");
                            Assert.That(part.mesh, Is.Not.Null, "it has a mesh of its own");
                            Assert.That(part.mesh.vertexCount, Is.EqualTo(part.range.vertexCount), "with its convex's vertices");
                            Assert.That(
                                part.range.vertexCount, Is.EqualTo((side ? outcome.positive : outcome.negative).vertexCount),
                                "and the side's own range");
                        }
                    }

                    Assert.That(borrowed, Is.EqualTo(2), "the two convexes the plane missed are borrowed");
                    Assert.That(produced, Is.EqualTo(4), "and the two it split gave two sides each");
                }

                Assert.That(f.cook.Reserving, Is.Zero, "the reservation is back");
                Assert.That(f.cook.ActiveCount, Is.Zero, "and the runner holds nothing");
            }
        }

        /// <summary>
        /// A produced mesh is one a convex collider takes with the profile the products carry, and answers for. This
        /// says the mesh is a usable cooked input with that profile; it measures nothing about whether the collider
        /// bakes again, and nothing about the cooked hull matching the B-rep (DESIGN 7.3).
        /// </summary>
        [Test]
        public void AProducedMesh_IsUsableAsAConvexColliderWithTheSameProfile()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                f.RunUntil(() => request.IsOver, "the cut and cook end");
                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok));

                using (PhysicsCutProducts products = request.Products)
                {
                    PhysicsCutPart part = default;
                    for (int i = 0; i < products.PartCount(true); i++)
                    {
                        if (!products.Part(true, i).borrowed)
                        {
                            part = products.Part(true, i);
                            break;
                        }
                    }

                    Assert.That(part.mesh, Is.Not.Null, "there is a produced part to try");
                    var go = new GameObject("PhysicsCutCookProbe") { hideFlags = HideFlags.HideAndDontSave };
                    try
                    {
                        MeshCollider collider = go.AddComponent<MeshCollider>();
                        collider.convex = true;
                        collider.cookingOptions = products.Cooking;
                        collider.sharedMesh = part.mesh;
                        UnityEngine.Physics.SyncTransforms();

                        Bounds bounds = collider.bounds;
                        Assert.That(bounds.size.sqrMagnitude, Is.GreaterThan(0f), "the collider has a shape");
                        Vector3 outside = bounds.center + new Vector3(0f, bounds.size.y + 2f, 0f);
                        Assert.That(
                            collider.Raycast(new Ray(outside, Vector3.down), out RaycastHit _, bounds.size.y + 4f),
                            Is.True,
                            "and answers a ray through it");
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                }
            }
        }

        // ----- the dispatcher decides when ------------------------------------------------------------------------------

        /// <summary>
        /// Nothing is scheduled until the dispatcher says so: while its destination is full, the cut's work waits in
        /// the queue with no job scheduled, and while the queue itself is full it is not even offered. Both clear as
        /// soon as the destination frees up.
        /// </summary>
        [Test]
        public void WhileTheQueueOrTheDestinationIsFull_NoJobIsScheduled()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture(waitingCapacity: 2, reservedForUrgent: 1, jobCapacity: 1))
            {
                // One piece of urgent work fills the one place the destination has.
                var blocker = new Blocker();
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.CurrentStatePhysicsSafety, blocker, out WorkTicket _), Is.True);
                f.Pump();

                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                f.Pump();
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Cutting), "the cut's work was taken into the queue");
                Assert.That(request.scheduled, Is.Zero, "but the destination is full, so no job was scheduled");

                // Filling the queue as well: the next offer cannot even be made.
                var queued = new Blocker();
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.CurrentStatePhysicsSafety, queued, out WorkTicket _), Is.True);
                PhysicsCutRequest second = f.cook.Submit(in h.input, float4x4.identity);
                f.Pump();
                Assert.That(second.Stage, Is.EqualTo(PhysicsCutStage.Waiting), "the second cut is not offered at all");
                Assert.That(second.scheduled, Is.Zero, "and nothing of it is scheduled");
                Assert.That(request.scheduled, Is.Zero, "nor of the first");

                blocker.released = true;
                queued.released = true;
                f.RunUntil(() => request.IsOver, "the first cut runs once the destination frees");
                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok));
                Assert.That(request.scheduled, Is.EqualTo(2), "one job for the numbers and one for the bake");
                request.Products.Dispose();

                f.RunUntil(() => second.IsOver, "and so does the second");
                Assert.That(second.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok));
                second.Products.Dispose();
            }
        }

        /// <summary>
        /// A frame whose budget is spent schedules nothing more: the cut's work stays in the queue, unscheduled, until
        /// a frame with budget comes.
        /// </summary>
        [Test]
        public void WhenTheFrameBudgetIsSpent_NothingMoreIsScheduled()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture(frameBudget: 1))
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);

                // One frame, one unit of budget, and something else takes it.
                var other = new Blocker();
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.CurrentStatePhysicsSafety, other, out WorkTicket _), Is.True);
                f.cook.Pump();
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Cutting), "the cut's work is offered");
                f.dispatcher.BeginFrame(1000);
                f.dispatcher.Dispatch();
                Assert.That(request.scheduled, Is.Zero, "the one unit of budget went to the other work");

                f.dispatcher.Dispatch();
                Assert.That(request.scheduled, Is.Zero, "and dispatching again in the same frame finds no budget");

                other.released = true;
                f.RunUntil(() => request.IsOver, "the next frames carry it through");
                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok));
                request.Products.Dispose();
            }
        }

        /// <summary>
        /// The stages are one for one with the dispatcher: the numerical work is collected before the bake is offered,
        /// each is one scheduled job, and the products come only after the bake. When it is all over the dispatcher
        /// holds nothing of it.
        /// </summary>
        [Test]
        public void EachStageIsItsOwnWork_AndTheDispatcherIsLeftEmpty()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                var seen = new List<PhysicsCutStage>();
                var clock = Stopwatch.StartNew();
                while (!request.IsOver && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    f.Pump();
                    if (seen.Count == 0 || seen[seen.Count - 1] != request.Stage)
                    {
                        seen.Add(request.Stage);
                    }

                    if (!request.IsOver)
                    {
                        Assert.That(request.Products, Is.Null, "nothing is handed over before it is over");
                    }
                }

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok));
                Assert.That(seen, Does.Contain(PhysicsCutStage.Cutting), "the numbers were their own work");
                Assert.That(seen, Does.Contain(PhysicsCutStage.Baking), "and the bake its own");
                Assert.That(
                    seen.IndexOf(PhysicsCutStage.Cutting), Is.LessThan(seen.IndexOf(PhysicsCutStage.Baking)),
                    "the bake was offered after the cut came back");
                Assert.That(request.scheduled, Is.EqualTo(2), "exactly two jobs were scheduled");
                Assert.That(f.dispatcher.WaitingCount, Is.Zero, "the dispatcher waits for nothing of it");
                Assert.That(f.dispatcher.SubmittedCount, Is.Zero, "and holds nothing of it");
                Assert.That(f.dispatcher.TotalCollected, Is.EqualTo(2), "both works were collected, one for one");
                request.Products.Dispose();
            }
        }

        // ----- reservation ---------------------------------------------------------------------------------------------

        /// <summary>
        /// With room for one cut at a time, a second owner waits unoffered until the first has given its reservation
        /// back, and then runs. Neither is refused for the other's sake.
        /// </summary>
        [Test]
        public void ASecondOwner_WaitsUnofferedForTheReservation_AndThenRuns()
        {
            using (OwnerCutHarness first = MixedCompound())
            using (OwnerCutHarness second = MixedCompound())
            using (Fixture f = NewFixture(reservations: 1))
            {
                PhysicsCutRequest a = f.cook.Submit(in first.input, float4x4.identity);
                PhysicsCutRequest b = f.cook.Submit(in second.input, float4x4.identity);

                bool sawWaiting = false;
                var clock = Stopwatch.StartNew();
                while (!(a.IsOver && b.IsOver) && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    f.Pump();
                    Assert.That(f.cook.Reserving, Is.LessThanOrEqualTo(1), "only one cut holds a reservation at a time");
                    if (a.HoldsReservation && b.Stage == PhysicsCutStage.Waiting)
                    {
                        sawWaiting = true;
                        Assert.That(b.scheduled, Is.Zero, "and the one that waits has scheduled nothing");
                    }
                }

                Assert.That(a.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the first ran");
                Assert.That(b.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "and so did the second");
                Assert.That(sawWaiting, Is.True, "the second really waited, unoffered, for the first");
                a.Products.Dispose();
                b.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero);
            }
        }

        // ----- giving up and closing --------------------------------------------------------------------------------------

        /// <summary>A cut given up before it was offered ends at once, with nothing reserved and nothing scheduled.</summary>
        [Test]
        public void ACutGivenUpBeforeItIsOffered_EndsAtOnce()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                Assert.That(f.cook.Abandon(request), Is.True);

                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Abandoned));
                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Abandoned));
                Assert.That(request.Products, Is.Null, "nothing was handed over");
                Assert.That(request.scheduled, Is.Zero, "and nothing was scheduled");
                Assert.That(f.cook.Reserving, Is.Zero, "nothing was reserved");
                Assert.That(f.cook.ActiveCount, Is.Zero);
            }
        }

        /// <summary>
        /// A cut given up while its work waits in the dispatcher's queue is taken out of the queue and ends at once,
        /// with nothing scheduled and nothing left in the dispatcher.
        /// </summary>
        [Test]
        public void ACutGivenUpWhileItsWorkWaitsInTheQueue_IsTakenOutOfIt()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture(jobCapacity: 1))
            {
                var blocker = new Blocker();
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.CurrentStatePhysicsSafety, blocker, out WorkTicket _), Is.True);
                f.Pump();

                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                f.Pump();
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Cutting), "its work is in the queue");
                Assert.That(request.scheduled, Is.Zero, "and not scheduled");

                Assert.That(f.cook.Abandon(request), Is.True);
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Abandoned), "it ended at once");
                Assert.That(request.Products, Is.Null);
                Assert.That(f.cook.Reserving, Is.Zero, "with its reservation back");
                Assert.That(f.dispatcher.IsWaiting(request.Ticket), Is.False, "and nothing of it left waiting");

                blocker.released = true;
                f.Pump();
            }
        }

        /// <summary>
        /// A cut given up after the dispatcher has submitted its work is not interrupted: the work is collected, then
        /// everything it held goes back and no products are handed over. The runner is usable afterwards.
        /// </summary>
        [Test]
        public void ACutGivenUpAfterItsWorkWasSubmitted_GivesEverythingBackAndPublishesNothing()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (OwnerCutHarness next = MixedCompound())
            using (Fixture f = NewFixture())
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                f.Pump();
                f.Pump();
                Assert.That(request.scheduled, Is.GreaterThan(0), "its work was submitted and begun");

                Assert.That(f.cook.Abandon(request), Is.True);
                f.RunUntil(() => request.IsOver, "the abandoned cut comes back");

                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Abandoned));
                Assert.That(request.Products, Is.Null, "nothing was handed over");
                Assert.That(request.HoldsReservation, Is.False, "and the reservation is back");
                Assert.That(f.cook.Reserving, Is.Zero);

                PhysicsCutRequest after = f.cook.Submit(in next.input, float4x4.identity);
                f.RunUntil(() => after.IsOver, "the next owner runs");
                Assert.That(after.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok));
                after.Products.Dispose();
            }
        }

        /// <summary>
        /// Closing while a cut's work is with the dispatcher: it is not interrupted, the drain takes it back, and
        /// neither the runner nor the dispatcher is left holding anything.
        /// </summary>
        [Test]
        public void ClosingWhileACutIsOut_DrainsWithoutLeavingAnything()
        {
            using (OwnerCutHarness h = MixedCompound())
            {
                Fixture f = NewFixture();
                PhysicsCutRequest request;
                try
                {
                    request = f.cook.Submit(in h.input, float4x4.identity);
                    f.Pump();
                    f.Pump();
                    Assert.That(request.scheduled, Is.GreaterThan(0), "its work was submitted");
                }
                finally
                {
                    f.Dispose();
                }

                Assert.That(f.cook.IsDrained, Is.True, "the closed runner gave everything back");
                Assert.That(f.cook.Reserving, Is.Zero, "with no reservation left");
                Assert.That(request.Products, Is.Null, "and nothing handed over");
                Assert.That(f.dispatcher.WaitingCount, Is.Zero, "the dispatcher waits for nothing");
                Assert.That(f.dispatcher.SubmittedCount, Is.Zero, "and holds nothing");
            }
        }

        // ----- failure -------------------------------------------------------------------------------------------------

        /// <summary>
        /// A cut the kernel cannot do is handed back as a failure with the kernel's own status, and no part of it is
        /// handed over as products. The source is not retired and nothing is replaced by another shape — connecting
        /// that to a physics abort is not part of this.
        /// </summary>
        [Test]
        public void AKernelFailure_IsHandedBackWithoutProducts()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                // A vertex limit no output can meet: the reduction cannot bring a split convex down to it.
                ConvexCutOwnerInput input = h.input;
                input.vertexLimit = 3;
                PhysicsCutRequest request = f.cook.Submit(in input, float4x4.identity);
                f.RunUntil(() => request.IsOver, "the cut ends");

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.KernelFailed), "it failed");
                Assert.That(request.KernelStatus, Is.Not.EqualTo(ConvexCutOwnerStatus.Ok), "with the kernel's own status");
                Assert.That(request.Products, Is.Null, "and nothing partial was handed over");
                Assert.That(f.cook.Reserving, Is.Zero, "everything it held went back");
                Assert.That(f.cook.ActiveCount, Is.Zero);
                Assert.That(f.dispatcher.SubmittedCount, Is.Zero, "the dispatcher holds nothing of it");
            }
        }

        /// <summary>
        /// A work that comes back without its job having run to the end hands nothing over: an unwritten report is all
        /// zeros, which would otherwise read as a kernel that succeeded with nothing to split, so the end mark is
        /// required on its own. Everything the cut held goes back.
        /// </summary>
        [Test]
        public void AJobThatDidNotRunToItsEnd_HandsOverNothing()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                request.skipCutJob = true;

                f.RunUntil(() => request.IsOver, "the cut ends");

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.CookFailed), "it did not run to its end");
                Assert.That(request.Products, Is.Null, "so nothing was handed over");
                Assert.That(request.KernelStatus, Is.EqualTo(ConvexCutOwnerStatus.Ok), "although the unwritten report reads as Ok");
                Assert.That(f.cook.Reserving, Is.Zero, "everything it held went back");
                Assert.That(f.cook.ActiveCount, Is.Zero);
                Assert.That(f.dispatcher.WaitingCount, Is.Zero, "and the dispatcher holds nothing of it");
                Assert.That(f.dispatcher.SubmittedCount, Is.Zero);
            }
        }

        /// <summary>
        /// A collection that throws does not strand the cut: the dispatcher has already let that work go, so the
        /// throw is recorded as the submission's failure, the cut ends on the next pump, nothing is handed over and
        /// everything it held goes back.
        /// </summary>
        [Test]
        public void AnExceptionWhileCollecting_EndsTheCutAndLeavesNothing()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                var thrown = new InvalidOperationException("taking the work back threw");
                int hits = 0;
                request.collectHook = () =>
                {
                    hits++;
                    throw thrown;
                };

                f.RunUntil(() => request.IsOver, "the cut ends");

                Assert.That(hits, Is.EqualTo(1), "the collection was the one that threw");
                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.CookFailed), "it is the submission's failure");
                Assert.That(request.Failure, Is.SameAs(thrown), "carrying what was thrown");
                Assert.That(request.Products, Is.Null, "nothing was handed over");
                Assert.That(f.cook.Reserving, Is.Zero, "the reservation went back");
                Assert.That(f.cook.ActiveCount, Is.Zero, "the runner holds nothing");
                Assert.That(f.dispatcher.WaitingCount, Is.Zero, "and neither does the dispatcher");
                Assert.That(f.dispatcher.SubmittedCount, Is.Zero);
            }
        }

        /// <summary>An input with no convex is refused before anything is reserved.</summary>
        [Test]
        public void AnInputWithNoConvex_IsRefusedWithoutReserving()
        {
            using (Fixture f = NewFixture())
            {
                var empty = new ConvexCutOwnerInput { convexCount = 0 };
                PhysicsCutRequest request = f.cook.Submit(in empty, float4x4.identity);

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.InvalidInput));
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Finished));
                Assert.That(f.cook.Reserving, Is.Zero);
                Assert.That(f.cook.ActiveCount, Is.Zero);
            }
        }
    }
}
