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
    /// <para>
    /// **Each case here is run three times over, on what the blocks of a cut hold before anything writes them**
    /// (DESIGN 7.2): as the product takes them, cleared the way they used to be, and filled with a pattern that is
    /// nothing like zero. Coming out the same in all three says that **on the inputs and paths these cases reach**,
    /// no result depended on what a block held to begin with. It does not prove that nothing is read before it is
    /// written -- what says that is the reading and the writing, matched up region by region. This is the second
    /// line of evidence beside it. The report's <c>ranToEnd</c> and the bake's <c>done</c> are cleared by the
    /// product whatever this says, and the endings below are what says so.
    /// </para>
    /// </summary>
    [TestFixture(-1)]
    [TestFixture(0x00)]
    [TestFixture(0xCD)]
    public unsafe class PhysicsCutCookTests
    {
        private const int DeadlineMilliseconds = 30000;

        private readonly int _fill;
        private int _fillWas;

        public PhysicsCutCookTests(int fill)
        {
            _fill = fill;
        }

        [SetUp]
        public void TakeTheBlocksThisWay()
        {
            _fillWas = PhysicsCutBlocks.Fill;
            PhysicsCutBlocks.Fill = _fill;
        }

        [TearDown]
        public void PutTheBlocksBack()
        {
            PhysicsCutBlocks.Fill = _fillWas;
        }


        private sealed class Fixture : IDisposable
        {
            public IWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public PhysicsCutCook cook;
            public SharedWorkFrame frame;
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
            int jobCapacity = 4,
            HoldingExecutor holding = null,
            WatchedExecutor holdingJob = null)
        {
            IWorkExecutor urgent = holdingJob;
            if (urgent == null)
            {
                urgent = holding == null ? (IWorkExecutor)new UnityJobWorkExecutor(jobCapacity) : holding;
            }

            var f = new Fixture
            {
                job = urgent,
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(2),
            };
            f.dispatcher = new SharedWorkDispatcher(waitingCapacity, reservedForUrgent, frameBudget, f.job, f.geometry, f.background);
            f.cook = new PhysicsCutCook(f.dispatcher, reservations);
            f.frame = new SharedWorkFrame(f.dispatcher);
            f.frame.Add(f.cook);
            return f;
        }

        /// <summary>
        /// The urgent destination, watched: what it accepted and was told to begin, in order, handing back only what
        /// has really finished and the test has released. The work's own Begin is called, so a Unity job really is
        /// scheduled; nothing is completed by force and nothing unfinished is handed over.
        /// </summary>
        private sealed class WatchedExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _held = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _released = new List<IDispatchWork>();
            private bool _closed;

            internal WatchedExecutor(int capacity)
            {
                Capacity = capacity;
            }

            internal List<IDispatchWork> Accepted { get; } = new List<IDispatchWork>();

            internal List<IDispatchWork> Begun { get; } = new List<IDispatchWork>();

            internal bool HoldEverything { get; set; } = true;

            public WorkDestination Destination => WorkDestination.UnityJob;

            public int Capacity { get; }

            public int Held => _held.Count;

            public bool CanAccept => !_closed && _held.Count < Capacity;

            internal void Release(IDispatchWork work)
            {
                if (!_released.Contains(work))
                {
                    _released.Add(work);
                }
            }

            internal void ReleaseEverything()
            {
                HoldEverything = false;
            }

            internal bool FinishedButHeld(IDispatchWork work)
            {
                return _held.Contains(work) && work.IsComplete && !Lets(work);
            }

            private bool Lets(IDispatchWork work)
            {
                return !HoldEverything || _released.Contains(work);
            }

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _held.Add(work);
                Accepted.Add(work);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                Begun.Add(work);
                work.Begin();
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    IDispatchWork candidate = _held[i];
                    if (!candidate.IsComplete || !Lets(candidate))
                    {
                        continue;
                    }

                    _held.RemoveAt(i);
                    work = candidate;
                    completion = WorkCompletion.Finished;
                    return true;
                }

                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                ReleaseEverything();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    bool allDone = true;
                    for (int i = 0; i < _held.Count; i++)
                    {
                        allDone &= _held[i].IsComplete;
                    }

                    if (allDone)
                    {
                        return true;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                return false;
            }
        }

        /// <summary>
        /// The Unity Job destination as the product uses it -- the work's own <see cref="IDispatchWork.Begin"/>, the
        /// work's own <see cref="IDispatchWork.IsComplete"/> -- with one thing added for the tests: a piece of work
        /// the test has kept back is not handed back to the main thread, however finished it is.
        /// <para>
        /// **Nothing is ever completed by force and nothing unfinished is ever handed over.** Keeping work back only
        /// delays a collection that would otherwise have happened; releasing it does not make it finish. This is how
        /// a test decides the order two cuts are collected in, which is otherwise the machine's to decide.
        /// </para>
        /// </summary>
        private sealed class HoldingExecutor : IWorkExecutor
        {
            private struct Slot
            {
                public IDispatchWork work;
                public Exception beginFailure;
            }

            private readonly List<Slot> _held = new List<Slot>();
            private readonly List<IDispatchWork> _keptBack = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _accepted = new List<IDispatchWork>();
            private bool _closed;

            internal HoldingExecutor(int capacity)
            {
                Capacity = capacity;
            }

            public WorkDestination Destination => WorkDestination.UnityJob;

            public int Capacity { get; }

            public int Held => _held.Count;

            public bool CanAccept => !_closed && _held.Count < Capacity;

            /// <summary>Every piece of work this destination has taken, in the order it took them.</summary>
            internal IReadOnlyList<IDispatchWork> Accepted => _accepted;

            /// <summary>Holds this work back from collection, whatever state it reaches.</summary>
            internal void KeepBack(IDispatchWork work)
            {
                if (!_keptBack.Contains(work))
                {
                    _keptBack.Add(work);
                }
            }

            /// <summary>Lets a work that was kept back be collected again, if and when it has really finished.</summary>
            internal void Release(IDispatchWork work)
            {
                _keptBack.Remove(work);
            }

            /// <summary>Lets everything kept back be collected again.</summary>
            internal void ReleaseAll()
            {
                _keptBack.Clear();
            }

            /// <summary>Whether that work is one the test is holding back from collection, finished or not.</summary>
            internal bool IsKeptBack(IDispatchWork work)
            {
                return IndexOf(work) >= 0 && _keptBack.Contains(work);
            }

            /// <summary>Whether that work has really finished and is only waiting because the test is holding it.</summary>
            internal bool FinishedButKeptBack(IDispatchWork work)
            {
                int i = IndexOf(work);
                return i >= 0 && _keptBack.Contains(work) && _held[i].work.IsComplete;
            }

            public bool TryAccept(IDispatchWork work)
            {
                if (work == null)
                {
                    throw new ArgumentNullException(nameof(work));
                }

                if (!CanAccept)
                {
                    return false;
                }

                _held.Add(new Slot { work = work });
                _accepted.Add(work);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                if (IndexOf(work) < 0)
                {
                    throw new InvalidOperationException("this destination has not accepted that work");
                }

                try
                {
                    work.Begin();
                }
                catch (Exception failure)
                {
                    Remember(work, failure);
                    throw;
                }
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    Slot held = _held[i];

                    // Unfinished work is never handed over -- and neither is work the test is holding back.
                    if (!held.work.IsComplete || _keptBack.Contains(held.work))
                    {
                        continue;
                    }

                    _held.RemoveAt(i);
                    work = held.work;
                    completion = held.beginFailure == null
                        ? WorkCompletion.Finished
                        : WorkCompletion.Failed(held.beginFailure);
                    return true;
                }

                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                // Whatever the test was holding is let go here, so that a stop is not blocked by the test itself.
                _keptBack.Clear();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    bool allDone = true;
                    for (int i = 0; i < _held.Count; i++)
                    {
                        allDone &= _held[i].work.IsComplete;
                    }

                    if (allDone)
                    {
                        return true;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                return false;
            }

            private void Remember(IDispatchWork work, Exception failure)
            {
                int i = IndexOf(work);
                if (i >= 0)
                {
                    Slot held = _held[i];
                    held.beginFailure = failure;
                    _held[i] = held;
                }
            }

            private int IndexOf(IDispatchWork work)
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    if (ReferenceEquals(_held[i].work, work))
                    {
                        return i;
                    }
                }

                return -1;
            }
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

        /// <summary>
        /// The same kind of owner with far more convexes, so that its numerical work and its bake take substantially
        /// longer than a <see cref="MixedCompound"/>'s. Used where two cuts run together and the order they finish in
        /// has to be the other way round from the order they were submitted.
        /// </summary>
        private static OwnerCutHarness HeavyCompound()
        {
            var h = new OwnerCutHarness();
            h.planeN = new float3(0f, 1f, 0f);
            h.planeW = 0f;
            h.eps = 1e-5f;
            h.parentMass = 192.0;
            for (int i = 0; i < 64; i++)
            {
                // Every one of them crosses y = 0, so every one is split, meshed and baked.
                h.Add(Translated(CaseGenerator.Box(), new double3(2.5 * i, 0.0, 0.0)));
            }

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
        /// **How many parts each side ends with**, against the rule the outcomes state: a split convex gives one part
        /// to each side, and an uncut one gives a single part to the side that inherits it. The existing tests say
        /// what each part is; this one says how many there are on each side, and that each one is on the side its
        /// outcome puts it on.
        /// <para>
        /// **What it does not say.** It reads no capacity, and it cannot: a list whose initial capacity is wrong
        /// still grows, and ends with the same `PartCount`. So this is **not** a check that the count the products
        /// are made with is right, nor that no backing array was reallocated. It checks the parts themselves.
        /// </para>
        /// </summary>
        [Test]
        public void EachSidesPartCount_IsTheSplitsPlusWhatThatSideInherits()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                ConvexCutOwnerResult expected = Reference(h, out ConvexCutOutcome[] expectedOutcomes);
                Assert.That(expected.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), "the reference run");

                int splits = 0;
                int inheritedPositive = 0;
                int inheritedNegative = 0;
                for (int c = 0; c < expectedOutcomes.Length; c++)
                {
                    if (expectedOutcomes[c].IsSplit)
                    {
                        splits++;
                    }
                    else if (expectedOutcomes[c].InheritsPositive)
                    {
                        inheritedPositive++;
                    }
                    else
                    {
                        inheritedNegative++;
                    }
                }

                // The input this is asked of really has all three, each asked for on its own.
                Assert.That(splits, Is.GreaterThan(0), "this input has a convex the plane split");
                Assert.That(
                    inheritedPositive, Is.GreaterThan(0), "one it missed that lies above the plane");
                Assert.That(
                    inheritedNegative, Is.GreaterThan(0), "and one it missed that lies below it");

                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                f.RunUntil(() => request.IsOver, "the cut and cook end");

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "it ended with products");
                PhysicsCutProducts products = request.Products;
                Assert.That(products, Is.Not.Null);
                using (products)
                {
                    Assert.That(
                        products.PartCount(true), Is.EqualTo(splits + inheritedPositive),
                        "the positive side has one part per split and one per convex it inherits");
                    Assert.That(
                        products.PartCount(false), Is.EqualTo(splits + inheritedNegative),
                        "and so does the negative side");
                    Assert.That(
                        products.PartCount(true) + products.PartCount(false),
                        Is.EqualTo((2 * splits) + inheritedPositive + inheritedNegative),
                        "and between them they hold every part the outcomes call for");

                    // Each part is on the side the outcomes put it on: the counts above are of the right things.
                    foreach (bool side in new[] { true, false })
                    {
                        for (int i = 0; i < products.PartCount(side); i++)
                        {
                            PhysicsCutPart part = products.Part(side, i);
                            ConvexCutOutcome outcome = expectedOutcomes[part.inputConvex];
                            if (part.borrowed)
                            {
                                Assert.That(
                                    outcome.InheritsPositive, Is.EqualTo(side),
                                    "a borrowed part is on the side that inherits its convex");
                                continue;
                            }

                            Assert.That(outcome.IsSplit, Is.True, "and a produced part comes from a split convex");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// **An owner the reduction really runs on, through the product's arena.** The kernel's own tests reduce
        /// these two (<c>reduce-light</c>, and <c>reduce-heavy</c>, which needs the small-ring path as well); here
        /// they go through the cook, so the scratch the reduction lays out is **the arena's**, and what comes back is
        /// compared with the same kernel run directly -- success, the produced convexes, and the mass properties.
        /// <para>
        /// This is the case that covers the reduction for the three ways of taking a block: the earlier failure case
        /// refuses at the kernel's entrance (its vertex limit is below the minimum) and never reaches a reduction.
        /// </para>
        /// </summary>
        [Test]
        public void AnOwnerThatIsReduced_ComesThroughTheProductsArena_WithTheKernelsOwnNumbers()
        {
            foreach ((string name, OwnerCutHarness harness, bool expectR1) in new[]
                     {
                         ("reduce-light", OwnerFixtures.ReduceLight(0), false),
                         ("reduce-heavy", OwnerFixtures.ReduceHeavy(0), true),
                     })
            {
                using (OwnerCutHarness h = harness)
                using (Fixture f = NewFixture())
                {
                    h.Build();
                    ConvexCutOwnerResult expected = Reference(h, out ConvexCutOutcome[] expectedOutcomes);
                    Assert.That(
                        expected.status, Is.EqualTo(ConvexCutOwnerStatus.Ok),
                        name + ": the reference run (red=" + (ReductionStatus)expected.reductionStatus + ")");

                    // **The reduction really ran** in the reference, and the heavy one took the small-ring path.
                    Assert.That(expected.reducedConvexCount, Is.GreaterThan(0), name + ": the reduction ran");
                    Assert.That(expected.removedVertices, Is.GreaterThan(0), name + ": it removed vertices");
                    if (expectR1)
                    {
                        Assert.That(expected.r1Invocations, Is.GreaterThan(0), name + ": the R1 path ran");
                    }

                    PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);
                    f.RunUntil(() => request.IsOver, name + ": the cut and cook end");

                    Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), name + ": it ended with products");
                    PhysicsCutProducts products = request.Products;
                    Assert.That(products, Is.Not.Null, name);
                    using (products)
                    {
                        // **The same reduction happened inside the product's arena**, not only in the reference.
                        Assert.That(
                            products.Result.reducedConvexCount, Is.EqualTo(expected.reducedConvexCount),
                            name + ": the reduction ran the same number of times through the arena");
                        Assert.That(
                            products.Result.removedVertices, Is.EqualTo(expected.removedVertices),
                            name + ": and removed the same vertices");
                        Assert.That(
                            products.Result.r1Invocations, Is.EqualTo(expected.r1Invocations),
                            name + ": and took the small-ring path the same number of times");
                        AssertSameNumbers(in expected, products.Result, name + " through the connection");
                        Assert.That(
                            products.Result.positiveMass + products.Result.negativeMass,
                            Is.EqualTo(h.parentMass).Within(1e-9),
                            name + ": the two sides carry the parent's mass");

                        // What is checked of each produced convex: it comes of a convex the reference split, its
                        // vertex count is the reference's and within the limit, and it was given a mesh. **The shapes
                        // are not compared** -- same vertex count is not same convex, and nothing here reads the
                        // positions. A reduction that went differently would usually change the count, but it need
                        // not, and what rules that out is the listed numbers above, not this.
                        int produced = 0;
                        foreach (bool side in new[] { true, false })
                        {
                            for (int i = 0; i < products.PartCount(side); i++)
                            {
                                PhysicsCutPart part = products.Part(side, i);
                                if (part.borrowed)
                                {
                                    continue;
                                }

                                ConvexCutOutcome outcome = expectedOutcomes[part.inputConvex];
                                Assert.That(outcome.IsSplit, Is.True, name + ": a produced part comes of a split convex");
                                ConvexBrepRange reference = side ? outcome.positive : outcome.negative;
                                Assert.That(
                                    part.range.vertexCount, Is.EqualTo(reference.vertexCount),
                                    name + (side ? " positive" : " negative")
                                    + ": the produced convex has the reference's reduced vertex count (the count, not the shape)");
                                Assert.That(
                                    part.range.vertexCount, Is.LessThanOrEqualTo(h.input.vertexLimit),
                                    name + ": and is within the vertex limit");
                                Assert.That(part.mesh, Is.Not.Null, name + ": and it was given a collider mesh");
                                produced++;
                            }
                        }

                        Assert.That(
                            produced, Is.EqualTo(2 * expected.splitConvexCount),
                            name + ": both sides of every split convex were produced");
                    }
                }
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

        // ----- one frame, carried forward ---------------------------------------------------------------------------

        /// <summary>
        /// The bake of a cut is submitted in the **same frame** its numerical work was collected in. One update of the
        /// product's own frame entry does it: the numbers come back, the main thread applies the meshes, and the bake
        /// that this made ready is submitted at a later occasion of that update -- not at the next frame.
        /// <para>
        /// The frame id does not change. What is watched is the destination: the bake work it accepted and was told to
        /// begin, which for a Unity job is where that job is scheduled -- not where it starts running on a worker.
        /// </para>
        /// </summary>
        [Test]
        public void TheBakeIsSubmittedInTheFrameTheNumbersCameBackIn()
        {
            var watched = new WatchedExecutor(4);
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture(reservations: 1, holdingJob: watched))
            {
                PhysicsCutRequest request = f.cook.Submit(in h.input, float4x4.identity);

                // The numerical work is submitted and begun; nothing has been collected, because it has not finished.
                f.frame.Update(1);
                Assert.That(watched.Accepted.Count, Is.EqualTo(1), "the numerical work was taken by the destination");
                Assert.That(watched.Begun.Count, Is.EqualTo(1), "and its job was scheduled");
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Cutting));

                IDispatchWork numbers = watched.Accepted[0];
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !watched.FinishedButHeld(numbers))
                {
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(watched.FinishedButHeld(numbers), Is.True, "its job has finished and waits only on the test");

                // Still frame 1: collected, meshes applied on the main thread, and the bake submitted.
                watched.Release(numbers);
                SharedWorkFrameProgress carried = f.frame.Update(1);

                Assert.That(carried.collected, Is.GreaterThan(0), "the numbers were taken back");
                Assert.That(
                    watched.Accepted.Count, Is.EqualTo(2),
                    "and the bake was submitted in the same frame, not the next one");
                Assert.That(watched.Begun.Count, Is.EqualTo(2), "its job was scheduled too");
                Assert.That(
                    ReferenceEquals(watched.Accepted[1], numbers), Is.False, "which is a different work from the numbers");
                Assert.That(request.Stage, Is.EqualTo(PhysicsCutStage.Baking), "the cut is at its bake");
                Assert.That(
                    carried.occasions, Is.GreaterThan(1),
                    "and the update it happened in went round more than once: " + carried.occasions
                    + " occasions. Which occasion submitted it is not read from this count.");

                watched.ReleaseEverything();
                clock.Restart();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !request.IsOver)
                {
                    f.frame.Update(1);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "and the whole cut finished");
                TestContext.WriteLine(
                    "frame 1 throughout; works really submitted, in order: numbers then bake, "
                    + watched.Accepted.Count + " in all, begun " + watched.Begun.Count
                    + "; the bake was submitted within the update that collected the numbers, which went round "
                    + carried.occasions + " times");
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

        /// <summary>
        /// With room for two, two independent owners hold reservations at the same time and both are really submitted:
        /// each has a job of its own that began, which is what counts here -- being in the queue is not being run. The
        /// numbers each of them comes back with are its own input's, compared against that input run through the
        /// kernel directly.
        /// </summary>
        [Test]
        public void TwoIndependentOwners_HoldReservationsAtOnce_AndAreBothReallySubmitted()
        {
            using (OwnerCutHarness first = MixedCompound())
            using (OwnerCutHarness second = MixedCompound())
            using (Fixture f = NewFixture(reservations: 2, jobCapacity: 4))
            {
                ConvexCutOwnerResult firstAlone = Reference(first, out _);
                ConvexCutOwnerResult secondAlone = Reference(second, out _);
                PhysicsCutRequest a = f.cook.Submit(in first.input, float4x4.identity);
                PhysicsCutRequest b = f.cook.Submit(in second.input, float4x4.identity);

                // Both take room in the one pump that offers them, because nothing about them is shared.
                f.cook.Pump();
                Assert.That(a.HoldsReservation && b.HoldsReservation, Is.True, "both hold a reservation at once");
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "and the cook counts two");

                // Submitted means a job of its own began, not that it reached the queue.
                f.dispatcher.BeginFrame(1);
                f.dispatcher.Dispatch();
                Assert.That(a.scheduled, Is.GreaterThan(0), "the first owner's numerical job began");
                Assert.That(b.scheduled, Is.GreaterThan(0), "and so did the second's, in the same opportunity");

                f.RunUntil(() => a.IsOver && b.IsOver, "both cuts end");
                Assert.That(a.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the first succeeded");
                Assert.That(b.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "and so did the second");
                AssertSameNumbers(in firstAlone, a.Products.Result, "the first owner beside another");
                AssertSameNumbers(in secondAlone, b.Products.Result, "the second owner beside another");
                a.Products.Dispose();
                b.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero, "and nothing is held afterwards");
            }
        }

        /// <summary>
        /// One owner holding its reservation while it waits for its bake does not stop another's numerical work. The
        /// test keeps the first owner's **bake** back from collection, so that owner really is waiting for its bake
        /// and not merely un-pumped; then the second owner is submitted and its own numerical job is scheduled while
        /// that wait goes on.
        /// <para>
        /// What is held back is named: the collection of the first owner's bake work. The bake itself runs; nothing is
        /// forced to complete and nothing unfinished is handed over.
        /// </para>
        /// <para>
        /// The second owner going on needs room left in all of it -- a free reservation, a place at the destination,
        /// and frame budget -- and the fixture leaves all three.
        /// </para>
        /// </summary>
        [Test]
        public void OneOwnerWaitingForItsBakeToBeCollected_DoesNotStopAnothersNumericalWork()
        {
            var holding = new HoldingExecutor(4);
            using (OwnerCutHarness first = MixedCompound())
            using (OwnerCutHarness second = MixedCompound())
            using (Fixture f = NewFixture(reservations: 2, holding: holding))
            {
                ConvexCutOwnerResult secondAlone = Reference(second, out _);
                PhysicsCutRequest a = f.cook.Submit(in first.input, float4x4.identity);
                f.RunUntil(() => holding.Accepted.Count >= 2, "the first owner's bake reaches the destination");

                // [0] is its numerical work, [1] the bake that followed it. The bake's collection is what is held.
                IDispatchWork bake = holding.Accepted[1];
                holding.KeepBack(bake);
                f.RunUntil(() => holding.FinishedButKeptBack(bake), "its bake finishes and waits on the test");
                Assert.That(a.Stage, Is.EqualTo(PhysicsCutStage.Baking), "the first owner is waiting for its bake");
                Assert.That(a.IsOver, Is.False, "and is not over");
                Assert.That(a.HoldsReservation, Is.True, "holding its reservation the whole time");

                // The second owner, submitted into that wait.
                PhysicsCutRequest b = f.cook.Submit(in second.input, float4x4.identity);
                f.RunUntil(() => b.scheduled > 0, "the other owner's numerical job is scheduled");
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "both hold room: the one waiting on its bake, and the new one");
                Assert.That(a.Stage, Is.EqualTo(PhysicsCutStage.Baking), "the first is still waiting on its bake");
                Assert.That(
                    holding.FinishedButKeptBack(bake), Is.True,
                    "which is waiting on the test and on nothing else");

                // Let the first owner's bake be collected, and both run out.
                holding.Release(bake);
                f.RunUntil(() => a.IsOver && b.IsOver, "both cuts end");
                Assert.That(a.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the one that waited succeeded");
                Assert.That(b.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "and so did the other");
                AssertSameNumbers(in secondAlone, b.Products.Result, "the owner that ran beside a bake");
                a.Products.Dispose();
                b.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero, "and nothing is held at the end");
            }
        }

        /// <summary>
        /// Two owners finishing the other way round from the order they were submitted, with the order decided here
        /// rather than by how long the work happens to take. The first owner's numerical work is kept back from
        /// collection; the second owner then goes the whole way -- numbers, meshes applied, bake, products -- and ends
        /// while the first has not. Only then is the first let through, and it ends after.
        /// <para>
        /// Nothing is forced to complete: the work that is kept back really has finished, and is simply not handed
        /// over until the test says so. Each request comes back with its own products -- its own convex counts, mass,
        /// volume and centre of mass -- and not the other's.
        /// </para>
        /// </summary>
        [Test]
        public void TwoOwnersFinishingInReverseOrder_EachKeepsItsOwnProducts()
        {
            var holding = new HoldingExecutor(4);
            using (OwnerCutHarness heavy = HeavyCompound())
            using (OwnerCutHarness light = MixedCompound())
            using (Fixture f = NewFixture(reservations: 2, holding: holding))
            {
                ConvexCutOwnerResult heavyAlone = Reference(heavy, out _);
                ConvexCutOwnerResult lightAlone = Reference(light, out _);
                Assert.That(
                    heavyAlone.splitConvexCount, Is.GreaterThan(lightAlone.splitConvexCount),
                    "the two owners are really different pieces of work");

                PhysicsCutRequest big = f.cook.Submit(in heavy.input, float4x4.identity);
                PhysicsCutRequest small = f.cook.Submit(in light.input, float4x4.identity);

                f.cook.Pump();
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "both hold room at once");
                f.dispatcher.BeginFrame(1);
                f.dispatcher.Dispatch();
                Assert.That(holding.Accepted.Count, Is.EqualTo(2), "both numerical works were taken by the destination");
                Assert.That(big.scheduled, Is.GreaterThan(0), "the first owner's numerical job was scheduled");
                Assert.That(small.scheduled, Is.GreaterThan(0), "and so was the second's");

                // The first owner's numbers are kept back, so it cannot go on until the test allows it.
                IDispatchWork firstNumbers = holding.Accepted[0];
                holding.KeepBack(firstNumbers);

                f.RunUntil(() => small.IsOver, "the owner submitted second goes the whole way and ends");
                Assert.That(
                    big.IsOver, Is.False,
                    "while the one submitted first has not: it is still at " + big.Stage);
                Assert.That(big.HoldsReservation, Is.True, "and is still holding its own room");
                Assert.That(small.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the one that ended first succeeded");
                AssertSameNumbers(in lightAlone, small.Products.Result, "the light owner's own products");

                // Now the first, which ends after the second.
                holding.Release(firstNumbers);
                f.RunUntil(() => big.IsOver, "and the one submitted first ends after it");
                Assert.That(big.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "it succeeded too");
                AssertSameNumbers(in heavyAlone, big.Products.Result, "the heavy owner's own products");
                Assert.That(
                    big.Products.Result.splitConvexCount, Is.Not.EqualTo(small.Products.Result.splitConvexCount),
                    "and the two did not come back with each other's");
                small.Products.Dispose();
                big.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero, "nothing is held once both are over");
            }
        }

        /// <summary>
        /// One of two owners given up after it took its reservation and was submitted. The one given up is never
        /// interrupted: what it holds comes back when its work is collected, and never before. The other is untouched
        /// -- its own reservation, its own products.
        /// </summary>
        [Test]
        public void OneOwnerGivenUpAfterItsReservation_LeavesTheOtherWhole()
        {
            using (OwnerCutHarness doomed = MixedCompound())
            using (OwnerCutHarness kept = MixedCompound())
            using (Fixture f = NewFixture(reservations: 2, jobCapacity: 4))
            {
                ConvexCutOwnerResult keptAlone = Reference(kept, out _);
                PhysicsCutRequest a = f.cook.Submit(in doomed.input, float4x4.identity);
                PhysicsCutRequest b = f.cook.Submit(in kept.input, float4x4.identity);

                f.cook.Pump();
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "both hold room");
                f.dispatcher.BeginFrame(1);
                f.dispatcher.Dispatch();
                Assert.That(a.scheduled, Is.GreaterThan(0), "the one about to be given up was really submitted");

                // Given up while its work is with the dispatcher: it is marked, not interrupted.
                Assert.That(f.cook.Abandon(a), Is.True, "it is given up");
                Assert.That(a.IsOver, Is.False, "and is not over on the spot, because its work is out");
                Assert.That(a.HoldsReservation, Is.True, "so nothing it holds has come back yet");
                Assert.That(b.HoldsReservation, Is.True, "and the other's room is its own");

                f.RunUntil(() => a.IsOver, "the one given up ends when its work is collected");
                Assert.That(a.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Abandoned), "it was abandoned");
                Assert.That(a.Products, Is.Null, "with nothing handed over");
                AssertHoldsNoResources(a, "the one given up, once collected");
                Assert.That(a.HoldsReservation, Is.False, "and its reservation is closed, at collection and not before");

                f.RunUntil(() => b.IsOver, "the other runs to its end");
                Assert.That(b.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "untouched by the other being given up");
                AssertSameNumbers(in keptAlone, b.Products.Result, "the owner beside one given up");
                b.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero, "and nothing is held afterwards");
            }
        }

        /// <summary>
        /// One of two owners **fails** after it had taken its reservation and been submitted -- which is a different
        /// thing from being given up. The failure is made in the one place the product already lets a test make one:
        /// the collection of its numerical work throws, so this is a cut that failed while being collected.
        /// <para>
        /// The order is the test's, not the machine's: the owner that succeeds is kept back from collection until the
        /// end, so the failure is collected while that one is demonstrably still holding its reservation, and the room
        /// a third owner is then given can only be the room the failure returned.
        /// </para>
        /// <para>
        /// What the failure gave back is checked region by region on the request itself -- arena, the four report
        /// arrays, the writable mesh data and the meshes -- not from the reservation count alone.
        /// </para>
        /// </summary>
        [Test]
        public void OneOwnerFailingAfterItsReservation_LeavesTheOtherWhole_AndFreesItsRoom()
        {
            var holding = new HoldingExecutor(4);
            using (OwnerCutHarness doomed = MixedCompound())
            using (OwnerCutHarness kept = MixedCompound())
            using (OwnerCutHarness waiting = MixedCompound())
            using (Fixture f = NewFixture(reservations: 2, holding: holding))
            {
                ConvexCutOwnerResult keptAlone = Reference(kept, out _);
                ConvexCutOwnerResult waitingAlone = Reference(waiting, out _);
                PhysicsCutRequest a = f.cook.Submit(in doomed.input, float4x4.identity);
                PhysicsCutRequest b = f.cook.Submit(in kept.input, float4x4.identity);
                PhysicsCutRequest c = f.cook.Submit(in waiting.input, float4x4.identity);

                var thrown = new InvalidOperationException("taking this owner's work back threw");
                int hits = 0;
                a.collectHook = () =>
                {
                    hits++;
                    throw thrown;
                };

                // 1. Two hold room and are scheduled; the third waits at the limit with nothing of its own.
                f.cook.Pump();
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "the two that fit hold room");
                f.dispatcher.BeginFrame(1);
                f.dispatcher.Dispatch();
                Assert.That(holding.Accepted.Count, Is.EqualTo(2), "both numerical works were taken by the destination");
                Assert.That(a.scheduled, Is.GreaterThan(0), "the one that will fail was really scheduled");
                Assert.That(b.scheduled, Is.GreaterThan(0), "and so was the other");
                Assert.That(c.Stage, Is.EqualTo(PhysicsCutStage.Waiting), "the third waits at the limit");
                Assert.That(c.HoldsReservation, Is.False, "with no room of its own");
                Assert.That(c.scheduled, Is.Zero, "and nothing scheduled");

                // 2. The one that will succeed is kept back from collection, so only the failure can be collected.
                //    Its work may or may not have finished running by then; that is not controlled and is not needed.
                //    What matters is that it cannot be collected, so it cannot give its reservation back.
                holding.KeepBack(holding.Accepted[1]);
                f.RunUntil(() => a.IsOver, "the failing owner is collected and ends");

                // 3. It failed, handed nothing over, and gave back its own resources -- named one by one.
                Assert.That(hits, Is.EqualTo(1), "its collection was the one that threw");
                Assert.That(a.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.CookFailed), "it failed");
                Assert.That(a.Failure, Is.SameAs(thrown), "carrying what was thrown");
                Assert.That(a.Products, Is.Null, "with no part of a result handed over");
                AssertHoldsNoResources(a, "the failure, once collected");
                Assert.That(a.HoldsReservation, Is.False, "and its reservation is closed");
                Assert.That(b.HoldsReservation, Is.True, "the other still holds its own room");

                // The third may have been given the freed room in the very pump that collected the failure, because
                // the cook advances its requests in the order they were submitted. Either way that room can only be
                // the failure's: the other has demonstrably not given its own up, and is checked again below.
                Assert.That(f.cook.Reserving, Is.LessThanOrEqualTo(2), "never more than the limit holds room");
                Assert.That(b.IsOver, Is.False, "and is not over");
                Assert.That(
                    holding.IsKeptBack(holding.Accepted[1]), Is.True,
                    "because its work is one the test is holding back from collection -- whether that work has "
                    + "finished running is not a condition here, and is not controlled");

                // 4. The third is given room and scheduled -- while the other is still holding its own, so the room it
                //    was given can only be the room the failure returned.
                f.RunUntil(() => c.scheduled > 0, "the owner that was waiting is given room and scheduled");
                Assert.That(b.HoldsReservation, Is.True, "the succeeding owner never gave its room up for this");
                Assert.That(b.IsOver, Is.False);
                Assert.That(c.HoldsReservation, Is.True, "the third holds the room the failure freed");
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "which brings the count back to the limit");

                // 5. Released, the other two run out and each comes back with its own numbers.
                holding.ReleaseAll();
                f.RunUntil(() => b.IsOver && c.IsOver, "the other two run to their ends");
                Assert.That(b.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the owner beside the failure succeeded");
                AssertSameNumbers(in keptAlone, b.Products.Result, "the owner beside a failure");
                Assert.That(c.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "and so did the one that took its room");
                AssertSameNumbers(in waitingAlone, c.Products.Result, "the owner given the room a failure freed");
                b.Products.Dispose();
                c.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero, "nothing is held at the end");
                Assert.That(f.cook.ActiveCount, Is.Zero, "and the cook holds nothing");
            }
        }

        /// <summary>
        /// Every resource one reservation holds, checked on the request itself rather than inferred from the count of
        /// reservations: the arena, the four arrays the job reports through, the writable mesh data and the meshes.
        /// </summary>
        private static void AssertHoldsNoResources(PhysicsCutRequest request, string what)
        {
            Assert.That(request.arena, Is.Null, what + ": the arena went back");
            Assert.That(request.meshSlots.IsCreated, Is.False, what + ": the mesh working slots");
            Assert.That(request.report.IsCreated, Is.False, what + ": the report");
            Assert.That(request.meshDataHeld, Is.False, what + ": the writable mesh data");
            Assert.That(request.meshes, Is.Null, what + ": the meshes");
        }

        /// <summary>
        /// At the limit the existing contract still holds: with room for two, a third owner waits unoffered, with
        /// nothing scheduled, until one of the two gives its reservation back -- and then it runs.
        /// </summary>
        [Test]
        public void AThirdOwnerAtTheLimit_WaitsUnoffered_AndRunsOnceRoomIsFree()
        {
            using (OwnerCutHarness first = MixedCompound())
            using (OwnerCutHarness second = MixedCompound())
            using (OwnerCutHarness third = MixedCompound())
            using (Fixture f = NewFixture(reservations: 2, jobCapacity: 4))
            {
                ConvexCutOwnerResult thirdAlone = Reference(third, out _);
                PhysicsCutRequest a = f.cook.Submit(in first.input, float4x4.identity);
                PhysicsCutRequest b = f.cook.Submit(in second.input, float4x4.identity);
                PhysicsCutRequest c = f.cook.Submit(in third.input, float4x4.identity);

                f.cook.Pump();
                Assert.That(f.cook.Reserving, Is.EqualTo(2), "two hold room, which is all there is");
                Assert.That(c.Stage, Is.EqualTo(PhysicsCutStage.Waiting), "the third is not offered at all");
                Assert.That(c.scheduled, Is.Zero, "and nothing of it is scheduled");
                Assert.That(c.HoldsReservation, Is.False, "nor does it hold room");

                bool sawItWaitingAtTheLimit = false;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !(a.IsOver && b.IsOver))
                {
                    f.Pump();
                    Assert.That(f.cook.Reserving, Is.LessThanOrEqualTo(2), "never more than the limit holds room");
                    if (f.cook.Reserving == 2 && c.Stage == PhysicsCutStage.Waiting)
                    {
                        sawItWaitingAtTheLimit = true;
                        Assert.That(c.scheduled, Is.Zero, "and while it waits it schedules nothing");
                    }
                }

                Assert.That(sawItWaitingAtTheLimit, Is.True, "the third really waited while the limit was reached");
                f.RunUntil(() => c.IsOver, "and it runs once room comes free");
                Assert.That(c.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the third succeeded after waiting");
                AssertSameNumbers(in thirdAlone, c.Products.Result, "the owner that waited for room");
                a.Products.Dispose();
                b.Products.Dispose();
                c.Products.Dispose();
                Assert.That(f.cook.Reserving, Is.Zero, "and nothing is held at the end");
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
