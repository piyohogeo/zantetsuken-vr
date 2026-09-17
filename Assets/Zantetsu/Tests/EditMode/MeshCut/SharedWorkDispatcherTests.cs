using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.TestTools;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The main thread's shared dispatch of ready work to the three destinations of DESIGN 4.3 (T-090's part of 4.4),
    /// on test doubles that stand in for those destinations, plus one real job scheduled and collected across real
    /// editor frames. Nothing here cuts, publishes or draws: the dispatch sends work somewhere and takes it back, and
    /// every judgement about a product stays with the caller.
    /// <para>
    /// The budget tests use made-up frame numbers on purpose — they are about counting occasions, not about time, and
    /// no test here uses a real-time threshold or asserts how quickly anything finishes. Nothing here asserts an
    /// order beyond "urgent first where there was a choice": neither between the two pools, nor within a destination,
    /// nor by the order work was handed in.
    /// </para>
    /// </summary>
    public class SharedWorkDispatcherTests
    {
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -0.25f);
        private const float k_epsilon = 0.01f;
        private static readonly float3 k_high = new float3(0f, 4f, 0f);
        private static readonly float3 k_low = new float3(0f, -4f, 0f);

        /// <summary>Work whose progress the test decides, recording what was done to it and when.</summary>
        private sealed class SyntheticWork : IDispatchWork
        {
            private readonly List<string> _log;

            public SyntheticWork(string name, List<string> log = null)
            {
                this.name = name;
                _log = log;
            }

            public readonly string name;
            public int beginCount;
            public int collectCount;
            public WorkCompletion lastCompletion;
            public bool adopted;

            /// <summary>The caller's own authority check, run inside Collect. Null means "adopt".</summary>
            public Func<bool> adopt;

            /// <summary>Makes Begin throw after recording the call, as a failing submission would.</summary>
            public bool throwOnBegin;

            /// <summary>Makes Collect throw after recording the call.</summary>
            public bool throwOnCollect;

            /// <summary>What the Unity Job destination asks. The pools never ask it.</summary>
            public bool complete;

            public void Begin()
            {
                beginCount++;
                _log?.Add("begin " + name);
                if (throwOnBegin)
                {
                    throw new InvalidOperationException("begin of " + name + " failed");
                }
            }

            public bool IsComplete => complete;

            public void Collect(WorkCompletion completion)
            {
                collectCount++;
                lastCompletion = completion;
                adopted = completion.Succeeded && (adopt == null || adopt());
                _log?.Add((adopted ? "adopt " : "reclaim ") + name + " (" + completion.outcome + ")");
                if (throwOnCollect)
                {
                    throw new InvalidOperationException("collect of " + name + " failed");
                }
            }
        }

        /// <summary>
        /// A destination the test stands in for. It bounds what it holds exactly as a real one does — queued, begun
        /// and ended-but-not-taken together — and the test decides when anything begins or ends, so no thread and no
        /// timing takes part in any assertion.
        /// </summary>
        private sealed class FakeExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _queued = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _begun = new List<IDispatchWork>();
            private readonly Queue<KeyValuePair<IDispatchWork, WorkCompletion>> _ended =
                new Queue<KeyValuePair<IDispatchWork, WorkCompletion>>();

            private readonly List<string> _log;
            private bool _closed;

            public FakeExecutor(WorkDestination destination, int capacity, List<string> log = null)
            {
                Destination = destination;
                Capacity = capacity;
                _log = log;
            }

            /// <summary>Whether Submit begins the work at once, the way a free pool worker would.</summary>
            public bool beginOnSubmit = true;

            /// <summary>What a normal stop reports; false stands for a stop that did not confirm in time.</summary>
            public bool stopConfirms = true;

            /// <summary>
            /// Makes accepting refuse while <see cref="CanAccept"/> still says yes: a destination that filled up or
            /// fell over between the question and the answer.
            /// </summary>
            public bool refuseAcceptance;

            public int stopCount;
            public int closeCount;
            public int acceptancesRefused;
            public readonly List<IDispatchWork> submitted = new List<IDispatchWork>();

            public WorkDestination Destination { get; }

            public int Capacity { get; }

            public int Held => _queued.Count + _begun.Count + _ended.Count;

            public bool CanAccept => !_closed && Held < Capacity;

            public bool TryAccept(IDispatchWork work)
            {
                if (refuseAcceptance || _closed || Held >= Capacity)
                {
                    acceptancesRefused++;
                    return false;
                }

                submitted.Add(work);
                _queued.Add(work);
                _log?.Add("submit " + Destination);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                Assert.That(_queued.Contains(work) || _begun.Contains(work), Is.True, "that work was accepted here");
                if (beginOnSubmit)
                {
                    BeginQueued();
                }
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (_ended.Count == 0)
                {
                    work = null;
                    completion = default;
                    return false;
                }

                KeyValuePair<IDispatchWork, WorkCompletion> ended = _ended.Dequeue();
                work = ended.Key;
                completion = ended.Value;
                return true;
            }

            public void CloseForNewWork()
            {
                closeCount++;
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                stopCount++;
                _closed = true;
                _log?.Add("stop " + Destination);

                // What never began is cancelled, whether or not the stop is confirmed.
                for (int i = 0; i < _queued.Count; i++)
                {
                    _ended.Enqueue(new KeyValuePair<IDispatchWork, WorkCompletion>(_queued[i], WorkCompletion.Cancelled));
                }

                _queued.Clear();

                if (!stopConfirms)
                {
                    // A stop that could not be confirmed keeps the work that is still running: it is not ended, so
                    // it is not offered for collection either.
                    return false;
                }

                for (int i = 0; i < _begun.Count; i++)
                {
                    _ended.Enqueue(new KeyValuePair<IDispatchWork, WorkCompletion>(_begun[i], WorkCompletion.Finished));
                }

                _begun.Clear();
                return true;
            }

            // ----- what the test drives -------------------------------------------------------------------------

            /// <summary>Begins everything this destination has queued, as a worker picking them up would.</summary>
            public void BeginQueued()
            {
                while (_queued.Count > 0)
                {
                    IDispatchWork work = _queued[0];
                    _queued.RemoveAt(0);
                    _begun.Add(work);
                    work.Begin();
                }
            }

            /// <summary>Ends one work that has begun here.</summary>
            public void End(IDispatchWork work, WorkCompletion completion)
            {
                Assert.That(_begun.Remove(work), Is.True, "that work has begun at " + Destination);
                _ended.Enqueue(new KeyValuePair<IDispatchWork, WorkCompletion>(work, completion));
            }

            /// <summary>Ends everything that has begun here, successfully.</summary>
            public void EndAll()
            {
                while (_begun.Count > 0)
                {
                    End(_begun[0], WorkCompletion.Finished);
                }
            }
        }

        private sealed class Fixture
        {
            public SharedWorkDispatcher dispatcher;
            public FakeExecutor job;
            public FakeExecutor geometry;
            public FakeExecutor background;
            public List<string> log;

            public FakeExecutor For(WorkDestination destination)
            {
                switch (destination)
                {
                    case WorkDestination.UnityJob: return job;
                    case WorkDestination.GeometryPool: return geometry;
                    default: return background;
                }
            }
        }

        private static Fixture NewFixture(
            int waitingCapacity = 8,
            int reservedForUrgent = 2,
            int frameBudget = 16,
            int jobCapacity = 8,
            int geometryCapacity = 8,
            int backgroundCapacity = 8,
            bool withLog = false)
        {
            var log = withLog ? new List<string>() : null;
            var fixture = new Fixture
            {
                job = new FakeExecutor(WorkDestination.UnityJob, jobCapacity, log),
                geometry = new FakeExecutor(WorkDestination.GeometryPool, geometryCapacity, log),
                background = new FakeExecutor(WorkDestination.BackgroundPool, backgroundCapacity, log),
                log = log,
            };
            fixture.dispatcher = new SharedWorkDispatcher(
                waitingCapacity, reservedForUrgent, frameBudget, fixture.job, fixture.geometry, fixture.background);
            fixture.dispatcher.BeginFrame(1);
            return fixture;
        }

        private static WorkTicket EnqueueOrFail(SharedWorkDispatcher dispatcher, WorkPurpose purpose, IDispatchWork work)
        {
            Assert.That(dispatcher.TryEnqueue(purpose, work, out WorkTicket ticket), Is.True, "the work is taken");
            Assert.That(ticket.IsSet, Is.True, "and gets a ticket");
            return ticket;
        }

        // ----- classification into the three destinations ---------------------------------------------------------

        /// <summary>Each purpose goes where DESIGN 4.3 says, and the three destinations hold their own work.</summary>
        [Test]
        public void EachPurpose_IsSubmittedToTheDestinationItNames()
        {
            Fixture f = NewFixture();
            var safety = new SyntheticWork("safety");
            var physics = new SyntheticWork("physics");
            var geometry = new SyntheticWork("geometry");
            var speculative = new SyntheticWork("speculative");
            var maintenance = new SyntheticWork("maintenance");

            WorkTicket safetyTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.CurrentStatePhysicsSafety, safety);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, physics);
            WorkTicket geometryTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, geometry);
            WorkTicket speculativeTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, speculative);
            EnqueueOrFail(f.dispatcher, WorkPurpose.Maintenance, maintenance);

            Assert.That(f.dispatcher.Dispatch().submitted, Is.EqualTo(5), "all five were submitted");

            Assert.That(f.job.submitted, Is.EquivalentTo(new IDispatchWork[] { safety, physics }), "physics is urgent");
            Assert.That(f.geometry.submitted, Is.EquivalentTo(new IDispatchWork[] { geometry }), "an admitted cut's geometry");
            Assert.That(
                f.background.submitted,
                Is.EquivalentTo(new IDispatchWork[] { speculative, maintenance }),
                "speculation and maintenance");

            Assert.That(f.dispatcher.SubmittedCountFor(WorkDestination.UnityJob), Is.EqualTo(2));
            Assert.That(f.dispatcher.SubmittedCountFor(WorkDestination.GeometryPool), Is.EqualTo(1));
            Assert.That(f.dispatcher.SubmittedCountFor(WorkDestination.BackgroundPool), Is.EqualTo(2));

            Assert.That(f.dispatcher.TryGetDestination(safetyTicket, out WorkDestination where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.UnityJob));
            Assert.That(f.dispatcher.TryGetDestination(geometryTicket, out where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.GeometryPool));
            Assert.That(f.dispatcher.TryGetDestination(speculativeTicket, out where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.BackgroundPool));
        }

        /// <summary>
        /// The room at one destination is not the room at another: a full Unity Job destination does not stop pool
        /// work, and full pools do not stop urgent work. External work is never counted against the job system.
        /// </summary>
        [Test]
        public void AFullDestination_DoesNotTakeRoomFromAnother()
        {
            Fixture f = NewFixture(jobCapacity: 1, geometryCapacity: 1, backgroundCapacity: 1);

            var urgentIn = new SyntheticWork("urgent-in");
            var urgentOut = new SyntheticWork("urgent-out");
            var geometryIn = new SyntheticWork("geometry-in");
            var backgroundIn = new SyntheticWork("background-in");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, urgentIn);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, urgentOut);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, geometryIn);
            EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, backgroundIn);

            DispatchProgress progress = f.dispatcher.Dispatch();

            Assert.That(progress.submitted, Is.EqualTo(3), "one per destination, and no more");
            Assert.That(f.job.Held, Is.EqualTo(1), "the job destination took its one");
            Assert.That(f.geometry.Held, Is.EqualTo(1), "and so did the geometry pool");
            Assert.That(f.background.Held, Is.EqualTo(1), "and the background pool");
            Assert.That(urgentOut.beginCount, Is.Zero, "the urgent work that found no room never began");
            Assert.That(f.dispatcher.WaitingCount, Is.EqualTo(1), "and it is still waiting here");

            // The pools stay full; the job destination frees its place. Only urgent work moves.
            f.job.EndAll();
            f.dispatcher.Dispatch();
            Assert.That(urgentOut.beginCount, Is.EqualTo(1), "urgent work went in as soon as its own destination had room");
            Assert.That(f.dispatcher.WaitingCount, Is.Zero);
        }

        /// <summary>
        /// Urgent work is offered first when several works could go in one opportunity, and an urgent work that
        /// cannot go in does not hold up independent external work: a full job destination is passed over, never
        /// waited for.
        /// </summary>
        [Test]
        public void UrgentIsOfferedFirst_AndAnUrgentWorkThatCannotGoIn_DoesNotStopExternalWork()
        {
            Fixture f = NewFixture(withLog: true);

            // handed in in the opposite order on purpose: nothing here relies on that order
            EnqueueOrFail(f.dispatcher, WorkPurpose.Maintenance, new SyntheticWork("maintenance", f.log));
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, new SyntheticWork("geometry", f.log));
            EnqueueOrFail(f.dispatcher, WorkPurpose.CurrentStatePhysicsSafety, new SyntheticWork("safety", f.log));
            f.dispatcher.Dispatch();

            Assert.That(f.log[0], Is.EqualTo("submit " + WorkDestination.UnityJob), "the urgent work was offered first");

            // Now the job destination is full and an urgent work waits for it. Independent external work still goes.
            Fixture g = NewFixture(jobCapacity: 1);
            var blocking = new SyntheticWork("blocking");
            var waitingUrgent = new SyntheticWork("waiting-urgent");
            EnqueueOrFail(g.dispatcher, WorkPurpose.AdmittedPhysics, blocking);
            g.dispatcher.Dispatch();
            Assert.That(g.job.Held, Is.EqualTo(1), "the job destination is full and stays full");

            EnqueueOrFail(g.dispatcher, WorkPurpose.AdmittedPhysics, waitingUrgent);
            var geometry = new SyntheticWork("geometry");
            var background = new SyntheticWork("background");
            EnqueueOrFail(g.dispatcher, WorkPurpose.AdmittedGeometry, geometry);
            EnqueueOrFail(g.dispatcher, WorkPurpose.Speculative, background);

            DispatchProgress progress = g.dispatcher.Dispatch();

            Assert.That(progress.submitted, Is.EqualTo(2), "the two external works went in");
            Assert.That(geometry.beginCount, Is.EqualTo(1));
            Assert.That(background.beginCount, Is.EqualTo(1));
            Assert.That(waitingUrgent.beginCount, Is.Zero, "while the urgent work waits for its own destination");
            Assert.That(g.dispatcher.IsWaiting(default), Is.False);
        }

        /// <summary>Work below urgent cannot fill the waiting queue and leave physics no way in.</summary>
        [Test]
        public void LowerWork_CannotUseUpTheRoomKeptForUrgent()
        {
            Fixture f = NewFixture(waitingCapacity: 4, reservedForUrgent: 2);

            EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, new SyntheticWork("s1"));
            EnqueueOrFail(f.dispatcher, WorkPurpose.Maintenance, new SyntheticWork("m1"));

            var refused = new SyntheticWork("s2");
            Assert.That(
                f.dispatcher.TryEnqueue(WorkPurpose.Speculative, refused, out WorkTicket noTicket), Is.False,
                "the two places that are not reserved are taken");
            Assert.That(noTicket.IsSet, Is.False, "and no ticket was handed out");
            Assert.That(refused.beginCount, Is.Zero, "the work is untouched and still the caller's");

            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, new SyntheticWork("p1"));
            EnqueueOrFail(f.dispatcher, WorkPurpose.CurrentStatePhysicsSafety, new SyntheticWork("p2"));
            Assert.That(f.dispatcher.WaitingCount, Is.EqualTo(4), "urgent work could still get in, twice");

            Assert.That(
                f.dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, new SyntheticWork("p3"), out _), Is.False,
                "and the whole queue is a bound as well");
        }

        /// <summary>
        /// Nor can the room kept for urgent work be taken from behind, by giving an urgent work that is already
        /// waiting a purpose below it: that is refused while the unreserved part of the queue is full, and the work
        /// keeps the purpose and the destination it had.
        /// </summary>
        [Test]
        public void ReclassifyingUrgentWorkDownwards_CannotTakeTheRoomKeptForUrgentWork()
        {
            Fixture f = NewFixture(waitingCapacity: 4, reservedForUrgent: 2);
            WorkTicket speculative = EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, new SyntheticWork("s1"));
            EnqueueOrFail(f.dispatcher, WorkPurpose.Maintenance, new SyntheticWork("m1"));
            WorkTicket urgent = EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, new SyntheticWork("p1"));

            Assert.That(
                f.dispatcher.TryReclassify(urgent, WorkPurpose.Speculative), Is.False,
                "the two unreserved places are taken, so this one may not become non-urgent");
            Assert.That(f.dispatcher.TryGetDestination(urgent, out WorkDestination where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.UnityJob), "it keeps the destination it had");
            Assert.That(f.dispatcher.WaitingCount, Is.EqualTo(3), "and it is still waiting");

            // With an unreserved place free, the same reclassification is ordinary.
            Assert.That(f.dispatcher.Cancel(speculative), Is.True);
            Assert.That(f.dispatcher.TryReclassify(urgent, WorkPurpose.Speculative), Is.True);
            Assert.That(f.dispatcher.TryGetDestination(urgent, out where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.BackgroundPool));

            // And the other direction is never a problem: urgent work may always be classified as urgent.
            Assert.That(f.dispatcher.TryReclassify(urgent, WorkPurpose.AdmittedPhysics), Is.True);
            Assert.That(f.dispatcher.TryGetDestination(urgent, out where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.UnityJob));
        }

        // ----- the frame budget, and one frame's several opportunities --------------------------------------------

        [Test]
        public void TheFrameBudget_IsSharedByOneFrame_AndRefilledOnlyByANewFrame()
        {
            Fixture f = NewFixture(frameBudget: 2);
            for (int i = 0; i < 4; i++)
            {
                EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, new SyntheticWork("g" + i));
            }

            Assert.That(f.dispatcher.Dispatch().submitted, Is.EqualTo(2), "two occasions is what the frame allows");
            Assert.That(f.dispatcher.RemainingBudget, Is.Zero);
            Assert.That(f.dispatcher.Dispatch().MadeProgress, Is.False, "a second opportunity shares the same budget");

            f.dispatcher.BeginFrame(1);
            Assert.That(f.dispatcher.RemainingBudget, Is.Zero, "opening the same frame again refills nothing");
            Assert.That(f.dispatcher.Dispatch().MadeProgress, Is.False);

            f.dispatcher.BeginFrame(2);
            Assert.That(f.dispatcher.RemainingBudget, Is.EqualTo(2), "a new frame refills it");
            Assert.That(f.dispatcher.Dispatch().submitted, Is.EqualTo(2));
            Assert.That(f.dispatcher.WaitingCount, Is.Zero);
        }

        /// <summary>
        /// A→B→C inside one frame: each opportunity collects what ended and then submits what became ready in that
        /// very collection, with no new frame and no refill in between.
        /// </summary>
        [Test]
        public void ALaterOpportunityInTheSameFrame_CollectsAndThenSubmitsWhatBecameReady()
        {
            Fixture f = NewFixture(frameBudget: 8, withLog: true);
            var a = new SyntheticWork("A", f.log);
            var b = new SyntheticWork("B", f.log);
            var c = new SyntheticWork("C", f.log);

            // B becomes ready when A is collected, C when B is: the successors of an admitted cut.
            a.adopt = () =>
            {
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, b, out _), Is.True);
                return true;
            };
            b.adopt = () =>
            {
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, c, out _), Is.True);
                return true;
            };

            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, a);
            Assert.That(f.dispatcher.Dispatch().submitted, Is.EqualTo(1));
            f.geometry.End(a, WorkCompletion.Finished);

            DispatchProgress second = f.dispatcher.Dispatch();
            Assert.That(second.collected, Is.EqualTo(1), "A was collected");
            Assert.That(second.submitted, Is.EqualTo(1), "and B, ready in that very collection, went in at once");

            f.geometry.End(b, WorkCompletion.Finished);
            DispatchProgress third = f.dispatcher.Dispatch();
            Assert.That(third.collected, Is.EqualTo(1));
            Assert.That(third.submitted, Is.EqualTo(1), "and so did C");

            Assert.That(f.job.submitted, Is.EquivalentTo(new IDispatchWork[] { c }), "C is physics, so it is urgent");
            Assert.That(f.dispatcher.RemainingBudget, Is.EqualTo(8 - 5), "five occasions in one frame, never refilled");
            Assert.That(
                f.log,
                Is.EqualTo(new List<string>
                {
                    "submit " + WorkDestination.GeometryPool, "begin A",
                    "adopt A (Finished)", "submit " + WorkDestination.GeometryPool, "begin B",
                    "adopt B (Finished)", "submit " + WorkDestination.UnityJob, "begin C",
                }),
                "collection came before submission at every opportunity");
        }

        /// <summary>Work that has not ended is passed over, and a collection is never held back for a submission.</summary>
        [Test]
        public void UnendedWorkIsPassedOver_AndCollectionIsNeverHeldBack()
        {
            Fixture f = NewFixture(frameBudget: 1);
            var ended = new SyntheticWork("ended");
            var running = new SyntheticWork("running");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, ended);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, running);
            f.dispatcher.Dispatch();
            f.dispatcher.BeginFrame(2);
            f.dispatcher.Dispatch();
            f.geometry.End(ended, WorkCompletion.Finished);

            var waiting = new SyntheticWork("waiting");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, waiting);

            f.dispatcher.BeginFrame(3);
            DispatchProgress progress = f.dispatcher.Dispatch();

            Assert.That(progress.collected, Is.EqualTo(1), "the one that ended was taken back");
            Assert.That(progress.submitted, Is.Zero, "with the whole budget spent on that collection");
            Assert.That(waiting.beginCount, Is.Zero, "so the waiting work simply waits");
            Assert.That(running.collectCount, Is.Zero, "and the unended work was passed over, not waited for");
            Assert.That(f.dispatcher.SubmittedCount, Is.EqualTo(1), "it is still out there");
        }

        // ----- after a hit ----------------------------------------------------------------------------------------

        /// <summary>
        /// A hit reclassifies what has not been submitted, and leaves what has been submitted where it is: the
        /// speculation already in the background pool continues there, queue wait included, and is never moved,
        /// promoted or issued twice.
        /// </summary>
        [Test]
        public void AfterAHit_NotYetSubmittedWorkIsReclassified_AndSubmittedSpeculationContinues()
        {
            Fixture f = NewFixture(backgroundCapacity: 1);
            f.background.beginOnSubmit = false;      // it sits in the pool's queue, as a busy pool would leave it

            var inTheQueue = new SyntheticWork("in-the-queue");
            var notSubmitted = new SyntheticWork("not-submitted");
            WorkTicket submittedTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, inTheQueue);
            WorkTicket waitingTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, notSubmitted);

            f.dispatcher.Dispatch();
            Assert.That(f.background.Held, Is.EqualTo(1), "one speculation reached the pool");
            Assert.That(f.dispatcher.IsSubmitted(submittedTicket), Is.True);
            Assert.That(f.dispatcher.IsWaiting(waitingTicket), Is.True, "and one never left the dispatch");

            // The hit happens. What is already at a destination is not touched.
            Assert.That(
                f.dispatcher.TryReclassify(submittedTicket, WorkPurpose.AdmittedGeometry), Is.False,
                "submitted work is not reclassified");
            Assert.That(f.dispatcher.TryGetDestination(submittedTicket, out WorkDestination where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.BackgroundPool), "it continues where it was sent");

            // What was not submitted is classified by the purpose it has now.
            Assert.That(f.dispatcher.TryReclassify(waitingTicket, WorkPurpose.AdmittedGeometry), Is.True);
            Assert.That(f.dispatcher.TryGetDestination(waitingTicket, out where), Is.True);
            Assert.That(where, Is.EqualTo(WorkDestination.GeometryPool));

            f.dispatcher.Dispatch();
            Assert.That(f.geometry.submitted, Is.EquivalentTo(new IDispatchWork[] { notSubmitted }));
            Assert.That(f.background.submitted, Is.EquivalentTo(new IDispatchWork[] { inTheQueue }), "and only that one");
            Assert.That(f.dispatcher.TotalSubmitted, Is.EqualTo(2), "two works, two submissions, no duplicate");

            // The successor that becomes ready after the hit is classified then, too: physics goes to urgent.
            f.background.BeginQueued();
            var successor = new SyntheticWork("successor");
            inTheQueue.adopt = () =>
            {
                Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, successor, out _), Is.True);
                return false;      // the speculation itself is not adopted; its successor is what matters
            };
            f.background.End(inTheQueue, WorkCompletion.Finished);
            f.dispatcher.Dispatch();

            Assert.That(inTheQueue.collectCount, Is.EqualTo(1), "the speculation was collected once, where it ran");
            Assert.That(f.job.submitted, Is.EquivalentTo(new IDispatchWork[] { successor }), "and the successor is urgent");
        }

        // ----- a full queue, and the ledger ----------------------------------------------------------------------

        /// <summary>
        /// A full waiting queue means the work is not taken. The admitted cut, its anchors and its place in the
        /// incomplete budget are all untouched, and so is everything the dispatch already holds.
        /// </summary>
        [Test]
        public void AFullQueue_LeavesTheAdmittedCutAndItsAnchorsAlone_AndTheWorkWithTheCaller()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId cut = AdmitAndPrepare(ledger, source);
            List<float3> anchorsBefore = AnchorsOf(ledger, source);

            Fixture f = NewFixture(waitingCapacity: 2, reservedForUrgent: 1);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, new SyntheticWork("queued"));
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, new SyntheticWork("urgent"));

            var refused = new SyntheticWork("refused");
            Assert.That(f.dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, refused, out WorkTicket ticket), Is.False);
            Assert.That(ticket.IsSet, Is.False);
            Assert.That(refused.beginCount, Is.Zero, "nothing was begun");
            Assert.That(refused.collectCount, Is.Zero, "and nothing was collected");
            Assert.That(f.dispatcher.WaitingCount, Is.EqualTo(2), "the queue is what it was");
            Assert.That(f.dispatcher.TotalSubmitted, Is.Zero);

            Assert.That(ledger.IsCurrentTarget(source), Is.True, "the admitted cut is still the current one");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and still holds its place");
            Assert.That(ledger.TryGetOperation(cut, out LogicalCutOperation operation), Is.True);
            Assert.That(operation.state, Is.EqualTo(LogicalCutOperationState.Admitted), "in the state it had");
            Assert.That(AnchorsOf(ledger, source), Is.EqualTo(anchorsBefore), "with its anchors untouched");
        }

        [Test]
        public void CompletingWork_DoesNotReturnTheLedgersIncompleteUnit()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId cut = AdmitAndPrepare(ledger, source);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1));

            Fixture f = NewFixture();
            var work = new SyntheticWork("geometry");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, work);
            f.dispatcher.Dispatch();
            f.geometry.End(work, WorkCompletion.Finished);
            f.dispatcher.Dispatch();

            Assert.That(work.collectCount, Is.EqualTo(1), "the work is done and collected");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "but the cut still holds its place");

            Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "publication does not return it either");

            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "only the ledger's own notice does");
        }

        // ----- bounds, failure, cancellation and the normal stop --------------------------------------------------

        /// <summary>
        /// What a destination holds is bounded even when the main thread never collects: ended work waits there, the
        /// destination fills up, and the dispatch then submits nothing more to it.
        /// </summary>
        [Test]
        public void EndedWork_IsBoundedWhileTheMainThreadDoesNotCollect()
        {
            Fixture f = NewFixture(geometryCapacity: 2);
            var first = new SyntheticWork("g0");
            var second = new SyntheticWork("g1");
            var third = new SyntheticWork("g2");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, first);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, second);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, third);

            f.dispatcher.Dispatch();
            Assert.That(f.geometry.Held, Is.EqualTo(2), "two is the destination's bound");

            // Both end, and nobody collects them. The bound counts them just the same.
            f.geometry.EndAll();
            Assert.That(f.geometry.Held, Is.EqualTo(2), "ended work still occupies the room it was given");
            Assert.That(f.geometry.CanAccept, Is.False);

            f.dispatcher.BeginFrame(2);
            DispatchProgress progress = f.dispatcher.Dispatch();
            Assert.That(progress.collected, Is.EqualTo(2), "this opportunity does collect them");
            Assert.That(progress.submitted, Is.EqualTo(1), "and only then is there room for the third");
            Assert.That(third.beginCount, Is.EqualTo(1));
        }

        /// <summary>A failure travels to the main thread with its exception, once, and nothing re-runs the work.</summary>
        [Test]
        public void AFailure_ReachesTheMainThreadOnce_WithItsException()
        {
            Fixture f = NewFixture();
            var work = new SyntheticWork("failing");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, work);
            f.dispatcher.Dispatch();

            var thrown = new InvalidOperationException("the kernel refused the input");
            f.geometry.End(work, WorkCompletion.Failed(thrown));
            f.dispatcher.Dispatch();

            Assert.That(work.collectCount, Is.EqualTo(1), "collected once");
            Assert.That(work.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Failed));
            Assert.That(work.lastCompletion.failure, Is.SameAs(thrown), "with the exception itself");
            Assert.That(work.adopted, Is.False, "a failure is not adopted");
            Assert.That(work.beginCount, Is.EqualTo(1), "and it is not run again");

            f.dispatcher.BeginFrame(2);
            Assert.That(f.dispatcher.Dispatch().MadeProgress, Is.False, "nor collected again");
            Assert.That(work.collectCount, Is.EqualTo(1));
            Assert.That(f.geometry.Held, Is.Zero, "and the destination let it go");
        }

        [Test]
        public void WaitingWorkCanBeCancelled_AndSubmittedWorkCannot()
        {
            Fixture f = NewFixture(frameBudget: 1);
            var submitted = new SyntheticWork("submitted");
            var waiting = new SyntheticWork("waiting");
            WorkTicket submittedTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, submitted);
            WorkTicket waitingTicket = EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, waiting);
            f.dispatcher.Dispatch();

            Assert.That(f.dispatcher.Cancel(waitingTicket), Is.True, "a waiting work comes straight back");
            Assert.That(waiting.beginCount, Is.Zero, "unbegun");
            Assert.That(waiting.collectCount, Is.Zero, "and uncollected: it is the caller's again");
            Assert.That(f.dispatcher.IsWaiting(waitingTicket), Is.False);

            Assert.That(f.dispatcher.Cancel(submittedTicket), Is.False, "submitted work is never interrupted");
            Assert.That(f.dispatcher.IsSubmitted(submittedTicket), Is.True);
            Assert.That(f.dispatcher.Cancel(new WorkTicket(999)), Is.False, "and an unknown ticket names nothing");
        }

        /// <summary>
        /// The normal stop: what never began is cancelled once, what began is collected once, and nothing is
        /// declared free before every destination has confirmed that its workers stopped. Running twice, collecting
        /// twice and letting go early are all excluded.
        /// </summary>
        [Test]
        public void ANormalStop_CancelsWhatNeverBegan_CollectsWhatDid_AndOnlyAfterTheWorkersStopped()
        {
            Fixture f = NewFixture(withLog: true);
            f.geometry.beginOnSubmit = false;      // queued at the pool, not started yet

            var begun = new SyntheticWork("begun", f.log);
            var queuedAtThePool = new SyntheticWork("queued-at-the-pool", f.log);
            var neverSubmitted = new SyntheticWork("never-submitted", f.log);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedPhysics, begun);
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, queuedAtThePool);
            f.dispatcher.Dispatch();
            EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, neverSubmitted);
            f.background.beginOnSubmit = false;

            DispatchShutdownResult result = f.dispatcher.Shutdown(1000);

            Assert.That(result.workersStopped, Is.True, "every destination confirmed it had stopped");
            Assert.That(result.cancelled, Is.EqualTo(1), "the work that never left the dispatch");
            Assert.That(result.collected, Is.EqualTo(2), "the one that began, and the one queued at the pool");

            Assert.That(neverSubmitted.beginCount, Is.Zero, "what never began was never begun");
            Assert.That(neverSubmitted.collectCount, Is.EqualTo(1), "and got exactly one terminal handling");
            Assert.That(neverSubmitted.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Cancelled));

            Assert.That(queuedAtThePool.beginCount, Is.Zero, "so was the one still in the pool's queue");
            Assert.That(queuedAtThePool.collectCount, Is.EqualTo(1));
            Assert.That(queuedAtThePool.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Cancelled));

            Assert.That(begun.beginCount, Is.EqualTo(1), "and the one that began ran once");
            Assert.That(begun.collectCount, Is.EqualTo(1), "and was collected once");
            Assert.That(begun.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Finished));

            Assert.That(f.job.stopCount, Is.EqualTo(1), "each destination was stopped once");
            Assert.That(f.geometry.stopCount, Is.EqualTo(1));
            Assert.That(f.background.stopCount, Is.EqualTo(1));
            Assert.That(
                f.log.IndexOf("stop " + WorkDestination.GeometryPool),
                Is.LessThan(f.log.IndexOf("reclaim queued-at-the-pool (Cancelled)")),
                "the stop was confirmed before anything of that destination was let go");

            Assert.That(f.dispatcher.IsClosed, Is.True);
            Assert.That(
                f.dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, new SyntheticWork("late"), out _), Is.False,
                "and no new work is taken afterwards");

            DispatchShutdownResult again = f.dispatcher.Shutdown(1000);
            Assert.That(again.cancelled, Is.Zero, "stopping again cancels nothing twice");
            Assert.That(again.collected, Is.Zero, "and collects nothing twice");
            Assert.That(f.dispatcher.TotalCollected, Is.EqualTo(2));
        }

        /// <summary>
        /// A destination that says it could take work and then refuses to accept it — it filled up, or fell over,
        /// in between — has taken nothing: the work is still waiting here, unbegun and uncounted, and the normal
        /// stop still gives it exactly one terminal handling. It cannot end up held by nobody.
        /// </summary>
        [Test]
        public void ADestinationThatRefusesToAccept_LeavesTheWorkWaiting_AndItIsTerminatedOnceAtTheStop()
        {
            Fixture f = NewFixture();
            f.geometry.refuseAcceptance = true;
            var work = new SyntheticWork("refused-on-acceptance");
            WorkTicket ticket = EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, work);

            Assert.That(f.geometry.CanAccept, Is.True, "the destination said it could take one");
            DispatchProgress progress = f.dispatcher.Dispatch();

            Assert.That(f.geometry.acceptancesRefused, Is.EqualTo(1), "and then refused it");
            Assert.That(progress.submitted, Is.Zero, "so nothing was submitted");
            Assert.That(work.beginCount, Is.Zero, "and nothing was begun");
            Assert.That(f.dispatcher.IsWaiting(ticket), Is.True, "the work is still waiting here");
            Assert.That(f.dispatcher.IsSubmitted(ticket), Is.False, "counted as submitted nowhere");
            Assert.That(f.dispatcher.TotalSubmitted, Is.Zero);
            Assert.That(f.dispatcher.WaitingCount, Is.EqualTo(1));
            Assert.That(f.geometry.Held, Is.Zero, "and the destination holds nothing of it");
            Assert.That(f.dispatcher.RemainingBudget, Is.EqualTo(16), "the refused attempt cost nothing");

            // Offered again once the destination accepts, it goes in the ordinary way.
            f.geometry.refuseAcceptance = false;
            Assert.That(f.dispatcher.Dispatch().submitted, Is.EqualTo(1));
            Assert.That(work.beginCount, Is.EqualTo(1));
            Assert.That(f.dispatcher.IsSubmitted(ticket), Is.True);

            // And a work that was refused right up to the stop is terminated exactly once, not lost.
            Fixture g = NewFixture();
            g.background.refuseAcceptance = true;
            var neverAccepted = new SyntheticWork("never-accepted");
            EnqueueOrFail(g.dispatcher, WorkPurpose.Speculative, neverAccepted);
            g.dispatcher.Dispatch();
            Assert.That(g.dispatcher.WaitingCount, Is.EqualTo(1), "still waiting");

            DispatchShutdownResult stop = g.dispatcher.Shutdown(1000);
            Assert.That(stop.cancelled, Is.EqualTo(1), "the stop reached it");
            Assert.That(neverAccepted.beginCount, Is.Zero);
            Assert.That(neverAccepted.collectCount, Is.EqualTo(1), "terminated exactly once");
            Assert.That(neverAccepted.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Cancelled));
            Assert.That(g.dispatcher.Shutdown(1000).cancelled, Is.Zero, "and never twice");
            Assert.That(neverAccepted.collectCount, Is.EqualTo(1));
        }

        /// <summary>A stop that a destination could not confirm says so, rather than reporting a clean stop.</summary>
        [Test]
        public void AStopThatWasNotConfirmed_IsReportedAsSuch()
        {
            Fixture f = NewFixture();
            f.background.stopConfirms = false;
            var work = new SyntheticWork("speculative");
            EnqueueOrFail(f.dispatcher, WorkPurpose.Speculative, work);
            f.dispatcher.Dispatch();

            DispatchShutdownResult result = f.dispatcher.Shutdown(0);
            Assert.That(result.workersStopped, Is.False);
            Assert.That(result.collected, Is.Zero, "and the work that is still running is not collected");
            Assert.That(work.collectCount, Is.Zero);
            Assert.That(f.dispatcher.SubmittedCount, Is.EqualTo(1), "it stays where it is, with what it owns");
        }

        /// <summary>
        /// A stop that timed out does not hand over work that has not finished: it keeps it, and the ownership with
        /// it. Only what has finished is collectable, and the rest is collected once by a later, confirmed stop —
        /// nothing forces a job to complete to get past the timeout.
        /// </summary>
        [Test]
        public void AStopThatTimedOut_KeepsUnfinishedWork_AndALaterStopCollectsItOnce()
        {
            var job = new UnityJobWorkExecutor(4);
            var geometry = new FakeExecutor(WorkDestination.GeometryPool, 4);
            var background = new FakeExecutor(WorkDestination.BackgroundPool, 4);
            var dispatcher = new SharedWorkDispatcher(4, 1, 8, job, geometry, background);
            dispatcher.BeginFrame(1);

            var work = new SyntheticWork("unfinished");
            WorkTicket ticket = EnqueueOrFail(dispatcher, WorkPurpose.AdmittedPhysics, work);
            dispatcher.Dispatch();
            Assert.That(work.beginCount, Is.EqualTo(1), "it began");
            Assert.That(work.IsComplete, Is.False, "and has not finished");

            DispatchShutdownResult timedOut = dispatcher.Shutdown(0);
            Assert.That(timedOut.workersStopped, Is.False, "the stop could not be confirmed");
            Assert.That(timedOut.collected, Is.Zero, "and collected nothing");
            Assert.That(work.collectCount, Is.Zero, "the unfinished work was not touched");
            Assert.That(dispatcher.IsSubmitted(ticket), Is.True, "it is still held");
            Assert.That(job.Held, Is.EqualTo(1), "by the destination it was given to");

            // It finishes. The caller confirms the stop again, and only now is it taken back.
            work.complete = true;
            DispatchShutdownResult confirmed = dispatcher.Shutdown(1000);
            Assert.That(confirmed.workersStopped, Is.True, "now the stop is confirmed");
            Assert.That(confirmed.collected, Is.EqualTo(1));
            Assert.That(work.collectCount, Is.EqualTo(1), "collected exactly once");
            Assert.That(work.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Finished));
            Assert.That(dispatcher.IsSubmitted(ticket), Is.False);
            Assert.That(job.Held, Is.Zero);

            Assert.That(dispatcher.Shutdown(1000).collected, Is.Zero, "and never a second time");
            Assert.That(work.collectCount, Is.EqualTo(1));
            Assert.That(work.beginCount, Is.EqualTo(1), "nor begun again");
        }

        // ----- re-entry, and callbacks that throw ----------------------------------------------------------------

        [Test]
        public void ACallbackMayOfferMoreWork_ButCannotDispatchOpenAFrameOrStopFromInside()
        {
            Fixture f = NewFixture();
            var work = new SyntheticWork("collected");
            var offered = new SyntheticWork("offered");
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, work);
            f.dispatcher.Dispatch();
            f.geometry.End(work, WorkCompletion.Finished);

            bool offeredOk = false;
            work.adopt = () =>
            {
                offeredOk = f.dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, offered, out _);
                Assert.That(() => f.dispatcher.Dispatch(), Throws.InvalidOperationException, "no inner dispatch");
                Assert.That(() => f.dispatcher.BeginFrame(9), Throws.InvalidOperationException, "no inner frame");
                Assert.That(() => f.dispatcher.Shutdown(0), Throws.InvalidOperationException, "and no inner stop");
                return true;
            };

            DispatchProgress progress = f.dispatcher.Dispatch();

            Assert.That(offeredOk, Is.True, "offering work from inside a collection is ordinary");
            Assert.That(progress.collected, Is.EqualTo(1));
            Assert.That(progress.submitted, Is.EqualTo(1), "and the offered work went in during the same pass");
            Assert.That(f.dispatcher.RemainingBudget, Is.EqualTo(16 - 3));
        }

        /// <summary>
        /// On the urgent destination, beginning the work happens on the main thread, so an exception there reaches
        /// the caller. The work stays submitted, is never begun again, and is collected once with that failure when
        /// it reports complete — which is how a job that really was submitted gets its resources back.
        /// </summary>
        [Test]
        public void ABeginThatThrowsOnTheUrgentDestination_LeavesTheWorkSubmitted_AndNeverBegunAgain()
        {
            var job = new UnityJobWorkExecutor(4);
            var geometry = new FakeExecutor(WorkDestination.GeometryPool, 4);
            var background = new FakeExecutor(WorkDestination.BackgroundPool, 4);
            var dispatcher = new SharedWorkDispatcher(4, 1, 8, job, geometry, background);
            dispatcher.BeginFrame(1);

            var work = new SyntheticWork("failing-begin") { throwOnBegin = true };
            WorkTicket ticket = EnqueueOrFail(dispatcher, WorkPurpose.AdmittedPhysics, work);

            Assert.That(() => dispatcher.Dispatch(), Throws.InvalidOperationException, "the exception reaches the caller");
            Assert.That(work.beginCount, Is.EqualTo(1));
            Assert.That(dispatcher.IsSubmitted(ticket), Is.True, "and the work is counted as begun");
            Assert.That(dispatcher.IsWaiting(ticket), Is.False, "never to be begun a second time");

            dispatcher.BeginFrame(2);
            Assert.That(dispatcher.Dispatch().MadeProgress, Is.False, "it has not reported completion yet");
            Assert.That(work.beginCount, Is.EqualTo(1));

            work.complete = true;
            dispatcher.BeginFrame(3);
            Assert.That(dispatcher.Dispatch().collected, Is.EqualTo(1));
            Assert.That(work.collectCount, Is.EqualTo(1), "collected exactly once");
            Assert.That(work.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Failed), "as a failure");
            Assert.That(work.lastCompletion.failure, Is.Not.Null);
            Assert.That(dispatcher.IsSubmitted(ticket), Is.False);
        }

        [Test]
        public void ACollectThatThrows_IsNotCollectedAgain_AndIsNotReRun()
        {
            Fixture f = NewFixture();
            var work = new SyntheticWork("failing-collect") { throwOnCollect = true };
            EnqueueOrFail(f.dispatcher, WorkPurpose.AdmittedGeometry, work);
            f.dispatcher.Dispatch();
            f.geometry.End(work, WorkCompletion.Finished);

            Assert.That(() => f.dispatcher.Dispatch(), Throws.InvalidOperationException);
            Assert.That(work.collectCount, Is.EqualTo(1));
            Assert.That(f.dispatcher.SubmittedCount, Is.Zero, "it was taken out before the callback ran");

            f.dispatcher.BeginFrame(2);
            Assert.That(f.dispatcher.Dispatch().MadeProgress, Is.False);
            Assert.That(work.collectCount, Is.EqualTo(1), "not collected again");
            Assert.That(work.beginCount, Is.EqualTo(1), "and not run again");
        }

        [Test]
        public void TheDispatcherRefusesNonsense_AndNeedsAFrame()
        {
            FakeExecutor Job() => new FakeExecutor(WorkDestination.UnityJob, 2);
            FakeExecutor Geometry() => new FakeExecutor(WorkDestination.GeometryPool, 2);
            FakeExecutor Background() => new FakeExecutor(WorkDestination.BackgroundPool, 2);

            Assert.That(
                () => new SharedWorkDispatcher(0, 1, 1, Job(), Geometry(), Background()),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new SharedWorkDispatcher(4, 0, 1, Job(), Geometry(), Background()),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "urgent work must always keep a place");
            Assert.That(
                () => new SharedWorkDispatcher(4, 4, 1, Job(), Geometry(), Background()),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "and the reservation must leave room for other work");
            Assert.That(
                () => new SharedWorkDispatcher(4, 1, 0, Job(), Geometry(), Background()),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new SharedWorkDispatcher(4, 1, 1, null, Geometry(), Background()),
                Throws.ArgumentNullException, "every destination has to be there");
            Assert.That(
                () => new SharedWorkDispatcher(4, 1, 1, Geometry(), Geometry(), Background()),
                Throws.ArgumentException, "and has to be the destination it is given as");
            FakeExecutor shared = Geometry();
            Assert.That(
                () => new SharedWorkDispatcher(4, 1, 1, Job(), shared, shared),
                Throws.ArgumentException, "one executor cannot serve two destinations");
            Assert.That(
                () => new SharedWorkDispatcher(4, 1, 1, Job(), new FakeExecutor(WorkDestination.GeometryPool, 0), Background()),
                Throws.ArgumentException, "a destination with no room is not a destination");
            Assert.That(() => new WorkTicket(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new UnityJobWorkExecutor(0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => WorkCompletion.Failed(null), Throws.ArgumentNullException);
            Assert.That(() => WorkPurposes.DestinationOf((WorkPurpose)9), Throws.TypeOf<ArgumentOutOfRangeException>());

            var dispatcher = new SharedWorkDispatcher(4, 1, 4, Job(), Geometry(), Background());
            Assert.That(() => dispatcher.Dispatch(), Throws.InvalidOperationException, "a frame must be open first");
            Assert.That(() => dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, null, out _), Throws.ArgumentNullException);

            dispatcher.BeginFrame(1);
            Assert.That(
                () => dispatcher.TryEnqueue((WorkPurpose)7, new SyntheticWork("bogus"), out _),
                Throws.TypeOf<ArgumentOutOfRangeException>(),
                "an undefined purpose is not a purpose");
            Assert.That(
                () => dispatcher.TryEnqueue((WorkPurpose)(-1), new SyntheticWork("bogus"), out _),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => dispatcher.TryReclassify(new WorkTicket(1), (WorkPurpose)7),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => dispatcher.Shutdown(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(dispatcher.WaitingCount, Is.Zero, "and nothing was queued by any of those attempts");
            Assert.That(
                dispatcher.TryReclassify(new WorkTicket(1), WorkPurpose.AdmittedPhysics), Is.False,
                "a ticket this dispatch does not hold names nothing");
            Assert.That(dispatcher.TryGetDestination(new WorkTicket(1), out _), Is.False);
            Assert.That(
                () => dispatcher.Executor((WorkDestination)5), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(dispatcher.Executor(WorkDestination.UnityJob).Destination, Is.EqualTo(WorkDestination.UnityJob));
        }

        // ----- one real job, across real frames ------------------------------------------------------------------

        private struct AddOneJob : IJob
        {
            public int input;
            public NativeReference<int> result;

            public void Execute()
            {
                result.Value = input + 1;
            }
        }

        /// <summary>A real job behind the same three entrances the dispatch uses.</summary>
        private sealed class RealJobWork : IDispatchWork, IDisposable
        {
            private NativeReference<int> _result;
            private JobHandle _handle;
            private bool _begun;

            public RealJobWork(int input)
            {
                this.input = input;
                _result = new NativeReference<int>(Allocator.Persistent);
            }

            public readonly int input;
            public int collectCount;
            public int collectedValue;
            public WorkCompletion lastCompletion;

            public void Begin()
            {
                _handle = new AddOneJob { input = input, result = _result }.Schedule();
                _begun = true;

                // Kicked here, so that nothing has to force it later.
                JobHandle.ScheduleBatchedJobs();
            }

            // Asked without blocking, exactly as the destination requires.
            public bool IsComplete => _begun && _handle.IsCompleted;

            public void Collect(WorkCompletion completion)
            {
                _handle.Complete();
                collectedValue = _result.Value;
                lastCompletion = completion;
                collectCount++;
            }

            public void Dispose()
            {
                if (_begun)
                {
                    _handle.Complete();
                }

                if (_result.IsCreated)
                {
                    _result.Dispose();
                }
            }
        }

        /// <summary>
        /// The dispatch sends a real job to the Unity Job destination and collects it at a later frame, without ever
        /// waiting for it. Each turn of the loop yields, so the editor really does get its update between one look
        /// and the next; the bound on the loop is a safety net, not a claim about how quickly the job finishes.
        /// </summary>
        [UnityTest]
        public IEnumerator ARealJob_IsSubmittedAndCollected_AcrossRealFrames()
        {
            var job = new UnityJobWorkExecutor(2);
            var dispatcher = new SharedWorkDispatcher(
                4, 1, 4, job,
                new FakeExecutor(WorkDestination.GeometryPool, 2),
                new FakeExecutor(WorkDestination.BackgroundPool, 2));
            dispatcher.BeginFrame(1);

            var work = new RealJobWork(41);
            try
            {
                EnqueueOrFail(dispatcher, WorkPurpose.AdmittedPhysics, work);

                DispatchProgress first = dispatcher.Dispatch();
                Assert.That(first.submitted, Is.EqualTo(1), "the job was submitted");
                Assert.That(dispatcher.SubmittedCount, Is.EqualTo(1));
                Assert.That(job.Held, Is.EqualTo(1), "and the urgent destination holds it");

                int frame = 1;
                while (work.collectCount == 0 && frame < 300)
                {
                    yield return null;
                    frame++;
                    dispatcher.BeginFrame(frame);
                    dispatcher.Dispatch();
                }

                Assert.That(work.collectCount, Is.EqualTo(1), "the job was collected exactly once, at a later frame");
                Assert.That(work.collectedValue, Is.EqualTo(42), "and its product came back");
                Assert.That(work.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Finished));
                Assert.That(dispatcher.SubmittedCount, Is.Zero, "with nothing left running");
                Assert.That(job.Held, Is.Zero);
                Assert.That(dispatcher.TotalSubmitted, Is.EqualTo(1));
                Assert.That(dispatcher.TotalCollected, Is.EqualTo(1));

                yield return null;
                dispatcher.BeginFrame(frame + 1);
                Assert.That(dispatcher.Dispatch().MadeProgress, Is.False, "and nothing is collected twice");
                Assert.That(work.collectCount, Is.EqualTo(1));
            }
            finally
            {
                work.Dispose();
            }
        }

        private static CutOperationId AdmitAndPrepare(LogicalCutLedger ledger, LogicalFragmentId source)
        {
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return cut;
        }

        private static List<float3> AnchorsOf(LogicalCutLedger ledger, LogicalFragmentId id)
        {
            var into = new List<float3>();
            Assert.That(ledger.TryGetAnchors(id, into), Is.True, "fragment " + id + " exists");
            return into;
        }
    }
}
