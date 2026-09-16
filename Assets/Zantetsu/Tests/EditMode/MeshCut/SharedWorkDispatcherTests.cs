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
    /// The main thread's shared dispatch of ready work (DESIGN 4.4, T-090's part of it), on synthetic work whose
    /// completion the test decides, plus one real job that is scheduled and collected across real editor frames.
    /// Nothing here cuts, publishes or draws: the dispatcher starts work and takes it back, and every judgement about
    /// a product stays with the caller.
    /// <para>
    /// The budget tests use made-up frame numbers on purpose — they are about counting occasions, not about time, and
    /// no test here uses a real-time threshold or asserts how quickly a job finishes.
    /// </para>
    /// </summary>
    public class SharedWorkDispatcherTests
    {
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -0.25f);
        private const float k_epsilon = 0.01f;
        private static readonly float3 k_high = new float3(0f, 4f, 0f);
        private static readonly float3 k_low = new float3(0f, -4f, 0f);

        /// <summary>Work whose completion the test decides, recording what the dispatcher did to it and when.</summary>
        private sealed class SyntheticWork : IDispatchWork
        {
            private readonly List<string> _log;

            public SyntheticWork(string name, List<string> log)
            {
                this.name = name;
                _log = log;
            }

            public readonly string name;
            public bool finished;
            public int scheduleCount;
            public int collectCount;
            public bool adopted;

            /// <summary>The caller's own authority check, run inside Collect. Null means "adopt".</summary>
            public Func<bool> adopt;

            /// <summary>Makes Schedule throw after recording the call, as a failing submission would.</summary>
            public bool throwOnSchedule;

            /// <summary>Makes Collect throw after recording the call.</summary>
            public bool throwOnCollect;

            public void Schedule()
            {
                scheduleCount++;
                _log?.Add("schedule " + name);
                if (throwOnSchedule)
                {
                    throw new InvalidOperationException("schedule of " + name + " failed");
                }
            }

            public bool IsComplete => finished;

            public void Collect()
            {
                collectCount++;
                adopted = adopt == null || adopt();
                _log?.Add((adopted ? "adopt " : "reclaim ") + name);
                if (throwOnCollect)
                {
                    throw new InvalidOperationException("collect of " + name + " failed");
                }
            }
        }

        private static SharedWorkDispatcher NewDispatcher(
            int waitingCapacity = 8, int reservedForPhysics = 2, int maxConcurrent = 8, int frameBudget = 16)
        {
            var dispatcher = new SharedWorkDispatcher(waitingCapacity, reservedForPhysics, maxConcurrent, frameBudget);
            dispatcher.BeginFrame(1);
            return dispatcher;
        }

        private static WorkTicket EnqueueOrFail(SharedWorkDispatcher dispatcher, WorkPriority priority, IDispatchWork work)
        {
            Assert.That(dispatcher.TryEnqueue(priority, work, out WorkTicket ticket), Is.True, "the work is taken");
            Assert.That(ticket.IsSet, Is.True, "and gets a ticket");
            return ticket;
        }

        [Test]
        public void WorkIsStarted_InPriorityOrder_AndStablyWithinAPriority()
        {
            var log = new List<string>();
            SharedWorkDispatcher dispatcher = NewDispatcher();

            // handed in deliberately out of order, with two of each of two priorities
            var maintenance = new SyntheticWork("maintenance", log);
            var speculativeFirst = new SyntheticWork("speculative-1", log);
            var geometry = new SyntheticWork("geometry", log);
            var speculativeSecond = new SyntheticWork("speculative-2", log);
            var physicsFirst = new SyntheticWork("physics-1", log);
            var safety = new SyntheticWork("safety", log);
            var physicsSecond = new SyntheticWork("physics-2", log);

            EnqueueOrFail(dispatcher, WorkPriority.Maintenance, maintenance);
            EnqueueOrFail(dispatcher, WorkPriority.Speculative, speculativeFirst);
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, geometry);
            EnqueueOrFail(dispatcher, WorkPriority.Speculative, speculativeSecond);
            EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, physicsFirst);
            EnqueueOrFail(dispatcher, WorkPriority.CurrentStatePhysicsSafety, safety);
            EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, physicsSecond);

            DispatchProgress progress = dispatcher.Dispatch();

            Assert.That(progress.scheduled, Is.EqualTo(7), "everything fit in this frame's budget");
            Assert.That(progress.collected, Is.Zero, "nothing had finished yet");
            Assert.That(log, Is.EqualTo(new List<string>
            {
                "schedule safety",
                "schedule physics-1",
                "schedule physics-2",
                "schedule geometry",
                "schedule speculative-1",
                "schedule speculative-2",
                "schedule maintenance",
            }), "priority order, and within a priority the order they were handed in");
        }

        /// <summary>
        /// Work below the two physics priorities cannot take the places kept for them: however much of it is waiting,
        /// physics still has a way in (DESIGN 4.4).
        /// </summary>
        [Test]
        public void LowPriorityWork_CannotUseUpTheRoomKeptForPhysics()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 2);

            Assert.That(dispatcher.TryEnqueue(WorkPriority.Maintenance, new SyntheticWork("m1", null), out _), Is.True);
            Assert.That(dispatcher.TryEnqueue(WorkPriority.Speculative, new SyntheticWork("s1", null), out _), Is.True);
            Assert.That(dispatcher.WaitingCount, Is.EqualTo(2), "two of the four places are taken by low-priority work");

            // the remaining two are reserved, so more low-priority work is refused
            Assert.That(dispatcher.TryEnqueue(WorkPriority.Speculative, new SyntheticWork("s2", null), out WorkTicket refused), Is.False, "the reserved places are not for speculation");
            Assert.That(refused.IsSet, Is.False, "a refused work gets no ticket");
            Assert.That(dispatcher.TryEnqueue(WorkPriority.HitGeometryFinish, new SyntheticWork("g1", null), out _), Is.False, "nor for geometry finishing");
            Assert.That(dispatcher.WaitingCount, Is.EqualTo(2), "and nothing was evicted to make room");

            // physics still gets in, both kinds
            Assert.That(dispatcher.TryEnqueue(WorkPriority.HitPhysics, new SyntheticWork("p1", null), out _), Is.True, "hit physics has its way in");
            Assert.That(dispatcher.TryEnqueue(WorkPriority.CurrentStatePhysicsSafety, new SyntheticWork("safety", null), out _), Is.True, "and so does current-state safety");
            Assert.That(dispatcher.WaitingCount, Is.EqualTo(4), "the queue is full now");

            Assert.That(dispatcher.TryEnqueue(WorkPriority.CurrentStatePhysicsSafety, new SyntheticWork("safety2", null), out _), Is.False, "a full queue takes nothing, physics included");
        }

        [Test]
        public void TheConcurrencyLimit_AndTheFrameBudget_BothBound_WhatOneFrameStarts()
        {
            var log = new List<string>();
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 8, reservedForPhysics: 1, maxConcurrent: 2, frameBudget: 16);
            for (int i = 0; i < 5; i++)
            {
                EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, new SyntheticWork("w" + i, log));
            }

            DispatchProgress first = dispatcher.Dispatch();
            Assert.That(first.scheduled, Is.EqualTo(2), "the concurrency limit stops at two");
            Assert.That(dispatcher.ScheduledCount, Is.EqualTo(2));
            Assert.That(dispatcher.WaitingCount, Is.EqualTo(3), "the rest are still waiting, still the dispatcher's");

            DispatchProgress again = dispatcher.Dispatch();
            Assert.That(again.MadeProgress, Is.False, "with nothing finished, another opportunity moves nothing");

            // a small budget bounds a frame even when concurrency would allow more
            SharedWorkDispatcher tight = NewDispatcher(waitingCapacity: 8, reservedForPhysics: 1, maxConcurrent: 8, frameBudget: 3);
            for (int i = 0; i < 5; i++)
            {
                EnqueueOrFail(tight, WorkPriority.HitPhysics, new SyntheticWork("t" + i, null));
            }

            Assert.That(tight.Dispatch().scheduled, Is.EqualTo(3), "three schedules is what the budget paid for");
            Assert.That(tight.RemainingBudget, Is.Zero);
            Assert.That(tight.Dispatch().MadeProgress, Is.False, "and the frame has nothing left to spend");
            Assert.That(tight.WaitingCount, Is.EqualTo(2), "the remaining work is untouched, waiting for the next frame");
        }

        [Test]
        public void TheBudget_IsNotRefilledWithinAFrame_AndIsRefilledByTheNext()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 8, reservedForPhysics: 1, maxConcurrent: 8, frameBudget: 2);
            for (int i = 0; i < 4; i++)
            {
                EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, new SyntheticWork("w" + i, null));
            }

            Assert.That(dispatcher.Dispatch().scheduled, Is.EqualTo(2));
            Assert.That(dispatcher.RemainingBudget, Is.Zero);

            dispatcher.BeginFrame(1);
            Assert.That(dispatcher.RemainingBudget, Is.Zero, "opening the same frame again refills nothing");
            Assert.That(dispatcher.Dispatch().MadeProgress, Is.False, "so a later opportunity in it can do nothing");

            dispatcher.BeginFrame(2);
            Assert.That(dispatcher.RemainingBudget, Is.EqualTo(2), "the next frame refills it");
            Assert.That(dispatcher.Dispatch().scheduled, Is.EqualTo(2), "and the rest is started there");
            Assert.That(dispatcher.WaitingCount, Is.Zero);
        }

        /// <summary>
        /// A → B → C, where the caller resolves the dependency: B is only offered once A has been collected. Within
        /// one frame, a later opportunity collects the finished A and starts B from what the budget has left.
        /// </summary>
        [Test]
        public void ALaterOpportunityInTheSameFrame_CollectsAndThenStartsWhatBecameReady()
        {
            var log = new List<string>();
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 1, frameBudget: 8);
            var a = new SyntheticWork("A", log);
            var b = new SyntheticWork("B", log);
            var c = new SyntheticWork("C", log);

            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, a);
            Assert.That(dispatcher.Dispatch().scheduled, Is.EqualTo(1), "A is started");
            Assert.That(dispatcher.RemainingBudget, Is.EqualTo(7));

            // not finished yet: the opportunity passes over it and returns, and the caller offers nothing new
            Assert.That(dispatcher.Dispatch().MadeProgress, Is.False, "an unfinished work is not waited for");
            Assert.That(a.collectCount, Is.Zero);
            Assert.That(b.scheduleCount, Is.Zero, "and nothing downstream was started early");

            // A finishes; the caller sees it collected and only then offers B
            a.finished = true;
            DispatchProgress second = dispatcher.Dispatch();
            Assert.That(second.collected, Is.EqualTo(1), "the finished A is taken back");
            Assert.That(a.collectCount, Is.EqualTo(1));
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, b);

            DispatchProgress third = dispatcher.Dispatch();
            Assert.That(third.scheduled, Is.EqualTo(1), "B is started in the same frame, from what the budget had left");
            Assert.That(dispatcher.RemainingBudget, Is.EqualTo(5), "one collection and two schedules were paid for");

            b.finished = true;
            dispatcher.Dispatch();
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, c);
            dispatcher.Dispatch();
            Assert.That(c.scheduleCount, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new List<string>
            {
                "schedule A", "adopt A", "schedule B", "adopt B", "schedule C",
            }), "each one started only after the one before it came back");
        }

        /// <summary>Collecting a finished work is never held back because a low-priority work cannot be started.</summary>
        [Test]
        public void CollectionIsNotHeldBack_WhenNothingNewCanBeStarted()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 1, frameBudget: 8);
            var running = new SyntheticWork("running", null);
            EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, running);
            dispatcher.Dispatch();

            // a maintenance work that the concurrency limit will not let start
            var maintenance = new SyntheticWork("maintenance", null);
            EnqueueOrFail(dispatcher, WorkPriority.Maintenance, maintenance);
            running.finished = true;

            DispatchProgress progress = dispatcher.Dispatch();
            Assert.That(progress.collected, Is.EqualTo(1), "the finished work came back regardless");
            Assert.That(running.collectCount, Is.EqualTo(1));
            Assert.That(progress.scheduled, Is.EqualTo(1), "and the freed slot then let the maintenance work start");
            Assert.That(maintenance.scheduleCount, Is.EqualTo(1));
        }

        [Test]
        public void WaitingWorkCanBeCancelled_AndStartedWorkCannot()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 1, frameBudget: 8);
            var started = new SyntheticWork("started", null);
            var waiting = new SyntheticWork("waiting", null);
            WorkTicket startedTicket = EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, started);
            WorkTicket waitingTicket = EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, waiting);

            dispatcher.Dispatch();
            Assert.That(dispatcher.IsScheduled(startedTicket), Is.True);
            Assert.That(dispatcher.IsWaiting(waitingTicket), Is.True);

            Assert.That(dispatcher.Cancel(waitingTicket), Is.True, "work that is still waiting comes back to the caller");
            Assert.That(dispatcher.IsWaiting(waitingTicket), Is.False);
            Assert.That(dispatcher.WaitingCount, Is.Zero);

            Assert.That(dispatcher.Cancel(startedTicket), Is.False, "a started job is never interrupted");
            Assert.That(dispatcher.IsScheduled(startedTicket), Is.True);
            Assert.That(dispatcher.Cancel(new WorkTicket(999)), Is.False, "and a ticket that names nothing cancels nothing");

            // the cancelled work is never started, and the started one still comes back exactly once
            started.finished = true;
            dispatcher.Dispatch();
            Assert.That(waiting.scheduleCount, Is.Zero, "the cancelled work was never started");
            Assert.That(waiting.collectCount, Is.Zero, "nor collected");
            Assert.That(started.collectCount, Is.EqualTo(1));

            dispatcher.Dispatch();
            Assert.That(started.collectCount, Is.EqualTo(1), "and is not collected a second time");
        }

        /// <summary>
        /// A product whose premise went stale is still collected, exactly once — the dispatcher does not judge it, and
        /// the caller's own check inside Collect decides that it is reclaimed rather than adopted.
        /// </summary>
        [Test]
        public void AStaleProduct_IsCollectedOnce_AndReclaimedByTheCallersOwnJudgement()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher();
            bool premiseStillHolds = true;
            var work = new SyntheticWork("speculative", null) { adopt = () => premiseStillHolds };
            EnqueueOrFail(dispatcher, WorkPriority.Speculative, work);
            dispatcher.Dispatch();

            // the premise is lost while the work runs; it is not interrupted
            premiseStillHolds = false;
            work.finished = true;

            dispatcher.Dispatch();
            Assert.That(work.collectCount, Is.EqualTo(1), "collected once");
            Assert.That(work.adopted, Is.False, "and reclaimed, not adopted");
            Assert.That(dispatcher.TotalCollected, Is.EqualTo(1));

            dispatcher.Dispatch();
            Assert.That(work.collectCount, Is.EqualTo(1), "with no second collection");
            Assert.That(dispatcher.TotalCollected, Is.EqualTo(1));
        }

        /// <summary>
        /// A full queue refuses the work and changes nothing about the admitted cut it belongs to: the ledger, its
        /// budget and its anchors are untouched, the caller keeps the work, and a later opportunity takes it.
        /// </summary>
        [Test]
        public void AFullQueue_LeavesTheAdmittedCutAndItsAnchorsAlone_AndTheWorkWithTheCaller()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId cut = AdmitAndPrepare(ledger, source);

            // two places for work below physics, and both are taken
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 3, reservedForPhysics: 1, maxConcurrent: 4, frameBudget: 8);
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, new SyntheticWork("other-1", null));
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, new SyntheticWork("other-2", null));

            var geometryWork = new SyntheticWork("geometry-for-cut", null);
            Assert.That(dispatcher.TryEnqueue(WorkPriority.HitGeometryFinish, geometryWork, out WorkTicket refused), Is.False, "there is no place this priority may take");
            Assert.That(refused.IsSet, Is.False);

            // nothing about the admitted cut moved
            Assert.That(ledger.TryGetOperation(cut, out LogicalCutOperation operation), Is.True);
            Assert.That(operation.state, Is.EqualTo(LogicalCutOperationState.Admitted), "the cut is still admitted");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "its place in the shared budget is untouched");
            Assert.That(ledger.IsCurrentTarget(source), Is.True, "the source was not retired");
            Assert.That(AnchorsOf(ledger, source), Is.EqualTo(new List<float3> { k_high, k_low }), "and its anchors are unchanged");
            Assert.That(ledger.TryGetPreparedAnchorDistribution(cut, out _), Is.True, "the prepared distribution still stands");
            Assert.That(geometryWork.scheduleCount, Is.Zero, "the work is still entirely the caller's");

            // room appears once the waiting work has been started, and the caller offers the very same work again
            dispatcher.Dispatch();
            Assert.That(dispatcher.WaitingCount, Is.Zero, "the waiting places were freed by starting what was in them");
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, geometryWork);
            dispatcher.Dispatch();
            Assert.That(geometryWork.scheduleCount, Is.EqualTo(1), "and the work the caller kept is taken at a later opportunity");
        }

        /// <summary>
        /// Finishing work in the dispatcher is not what closes a cut's place in the shared incomplete budget: that
        /// still takes the ledger's own completion notice (DESIGN 7.7).
        /// </summary>
        [Test]
        public void CompletingWork_DoesNotReturnTheLedgersIncompleteUnit()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId cut = AdmitAndPrepare(ledger, source);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1));

            SharedWorkDispatcher dispatcher = NewDispatcher();
            var work = new SyntheticWork("geometry", null);
            EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, work);
            dispatcher.Dispatch();
            work.finished = true;
            dispatcher.Dispatch();

            Assert.That(work.collectCount, Is.EqualTo(1), "the work is done and collected");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "but the cut still holds its place");

            Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "publication does not return it either");

            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "only the ledger's own notice does");
        }

        [Test]
        public void TheDispatcherRefusesNonsense_AndNeedsAFrame()
        {
            Assert.That(() => new SharedWorkDispatcher(0, 1, 1, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new SharedWorkDispatcher(4, 0, 1, 1), Throws.TypeOf<ArgumentOutOfRangeException>(), "physics must always keep a place");
            Assert.That(() => new SharedWorkDispatcher(4, 4, 1, 1), Throws.TypeOf<ArgumentOutOfRangeException>(), "and the reservation must leave room for other work");
            Assert.That(() => new SharedWorkDispatcher(4, -1, 1, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new SharedWorkDispatcher(4, 1, 0, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new SharedWorkDispatcher(4, 1, 1, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new WorkTicket(-1), Throws.TypeOf<ArgumentOutOfRangeException>());

            var dispatcher = new SharedWorkDispatcher(4, 1, 2, 4);
            Assert.That(() => dispatcher.Dispatch(), Throws.InvalidOperationException, "a frame must be open first");
            Assert.That(() => dispatcher.TryEnqueue(WorkPriority.HitPhysics, null, out _), Throws.ArgumentNullException);

            // a priority that is not one of the five is refused at the entrance, and nothing is taken
            dispatcher.BeginFrame(1);
            Assert.That(
                () => dispatcher.TryEnqueue((WorkPriority)7, new SyntheticWork("bogus", null), out _),
                Throws.TypeOf<ArgumentOutOfRangeException>(),
                "an undefined priority is not a priority");
            Assert.That(
                () => dispatcher.TryEnqueue((WorkPriority)(-1), new SyntheticWork("bogus", null), out _),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(dispatcher.WaitingCount, Is.Zero, "and nothing was queued by either attempt");
        }

        // ----- re-entry, and callbacks that throw ----------------------------------------------------------------

        /// <summary>A work that tries to drive the dispatcher again from inside its own collection.</summary>
        private sealed class ReentrantWork : IDispatchWork
        {
            public SharedWorkDispatcher dispatcher;
            public IDispatchWork follower;

            public bool finished;
            public int collectCount;
            public Exception dispatchError;
            public Exception beginFrameError;
            public bool enqueueSucceeded;
            public int budgetSeenInside;

            /// <summary>How many times the offered work had been started, as seen from inside the collection.</summary>
            public int followerStartsSeenInside;

            /// <summary>Reads the offered work's start count, so it can be observed from inside Collect.</summary>
            public Func<int> startsOfFollower;

            public void Schedule()
            {
            }

            public bool IsComplete => finished;

            public void Collect()
            {
                collectCount++;

                try
                {
                    dispatcher.Dispatch();
                }
                catch (Exception error)
                {
                    dispatchError = error;
                }

                try
                {
                    dispatcher.BeginFrame(999);
                }
                catch (Exception error)
                {
                    beginFrameError = error;
                }

                // offering more work, on the other hand, is the ordinary thing to do here
                enqueueSucceeded = dispatcher.TryEnqueue(WorkPriority.HitPhysics, follower, out _);
                budgetSeenInside = dispatcher.RemainingBudget;

                // what the refused inner dispatch did not do: by the time this returns, the new work is still waiting
                followerStartsSeenInside = startsOfFollower();
            }
        }

        [Test]
        public void ACallbackMayOfferMoreWork_ButCannotDispatchOrOpenAFrameFromInside()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 4, frameBudget: 6);
            var follower = new SyntheticWork("follower", null);
            var work = new ReentrantWork
            {
                dispatcher = dispatcher,
                follower = follower,
                startsOfFollower = () => follower.scheduleCount,
            };

            EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, work);
            dispatcher.Dispatch();
            Assert.That(dispatcher.RemainingBudget, Is.EqualTo(5), "one schedule paid for");

            work.finished = true;
            DispatchProgress progress = dispatcher.Dispatch();

            Assert.That(work.collectCount, Is.EqualTo(1), "the work was collected once");
            Assert.That(work.dispatchError, Is.TypeOf<InvalidOperationException>(), "and its attempt to dispatch again was refused");
            Assert.That(work.beginFrameError, Is.TypeOf<InvalidOperationException>(), "as was its attempt to open a frame");
            Assert.That(work.enqueueSucceeded, Is.True, "while offering more work was allowed");
            Assert.That(work.budgetSeenInside, Is.EqualTo(4), "the budget inside was the outer pass's, one collection down");
            Assert.That(work.followerStartsSeenInside, Is.Zero, "and by the time the collection returned, no inner pass had started the new work");

            Assert.That(progress.collected, Is.EqualTo(1), "the outer pass collected");
            Assert.That(progress.scheduled, Is.EqualTo(1), "and then started the work offered from inside, itself");
            Assert.That(follower.scheduleCount, Is.EqualTo(1), "exactly once");
            Assert.That(dispatcher.RemainingBudget, Is.EqualTo(3), "with no budget refilled underneath the pass");
            Assert.That(dispatcher.TotalCollected, Is.EqualTo(1));
            Assert.That(dispatcher.TotalScheduled, Is.EqualTo(2));
        }

        /// <summary>
        /// A Schedule that throws reaches the caller, and the work stays held as started: it is never scheduled a
        /// second time, and it is still collected once when it reports finished — which is how a job that really was
        /// submitted gets its resources back.
        /// </summary>
        [Test]
        public void AScheduleThatThrows_LeavesTheWorkStarted_AndItIsNeverScheduledAgain()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 4, frameBudget: 8);
            var failing = new SyntheticWork("failing", null) { throwOnSchedule = true };
            WorkTicket ticket = EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, failing);

            Assert.That(() => dispatcher.Dispatch(), Throws.InvalidOperationException, "the exception reaches the caller");
            Assert.That(failing.scheduleCount, Is.EqualTo(1), "Schedule was called once");
            Assert.That(dispatcher.IsScheduled(ticket), Is.True, "and the work is held as started");
            Assert.That(dispatcher.IsWaiting(ticket), Is.False, "not waiting to be started again");
            Assert.That(dispatcher.TotalScheduled, Is.EqualTo(1), "counted once");
            Assert.That(dispatcher.Cancel(ticket), Is.False, "and it cannot be cancelled now");

            // later opportunities neither re-schedule it nor wait for it
            dispatcher.Dispatch();
            Assert.That(failing.scheduleCount, Is.EqualTo(1), "it is never scheduled a second time");
            Assert.That(failing.collectCount, Is.Zero, "and it has not reported finished");

            // when it does report finished, it is collected once and the slot is freed
            failing.finished = true;
            DispatchProgress progress = dispatcher.Dispatch();
            Assert.That(progress.collected, Is.EqualTo(1));
            Assert.That(failing.collectCount, Is.EqualTo(1), "so whatever it took can be released there");
            Assert.That(dispatcher.ScheduledCount, Is.Zero, "and the execution slot came back");
            Assert.That(failing.scheduleCount, Is.EqualTo(1));
        }

        /// <summary>
        /// A Collect that throws reaches the caller too. It was already taken out and paid for, so it is neither
        /// collected again nor re-run: from that moment the work, and whatever it still holds, is the caller's.
        /// </summary>
        [Test]
        public void ACollectThatThrows_IsNotCollectedAgain_AndIsNotReRun()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 4, frameBudget: 8);
            var failing = new SyntheticWork("failing", null) { throwOnCollect = true };
            WorkTicket ticket = EnqueueOrFail(dispatcher, WorkPriority.HitPhysics, failing);
            dispatcher.Dispatch();
            failing.finished = true;

            int budgetBefore = dispatcher.RemainingBudget;
            Assert.That(() => dispatcher.Dispatch(), Throws.InvalidOperationException, "the exception reaches the caller");
            Assert.That(failing.collectCount, Is.EqualTo(1), "Collect was called once");
            Assert.That(dispatcher.RemainingBudget, Is.EqualTo(budgetBefore - 1), "and was paid for");
            Assert.That(dispatcher.IsScheduled(ticket), Is.False, "the work is no longer the dispatcher's");
            Assert.That(dispatcher.ScheduledCount, Is.Zero);
            Assert.That(dispatcher.TotalCollected, Is.EqualTo(1));

            dispatcher.Dispatch();
            Assert.That(failing.collectCount, Is.EqualTo(1), "it is not collected a second time");
            Assert.That(failing.scheduleCount, Is.EqualTo(1), "nor re-run");
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

        /// <summary>A real job behind the same three entrances the dispatcher uses.</summary>
        private sealed class RealJobWork : IDispatchWork, IDisposable
        {
            private NativeReference<int> _result;
            private JobHandle _handle;
            private bool _scheduled;

            public RealJobWork(int input)
            {
                this.input = input;
                _result = new NativeReference<int>(Allocator.Persistent);
            }

            public readonly int input;
            public int collectCount;
            public int collectedValue;

            public void Schedule()
            {
                _handle = new AddOneJob { input = input, result = _result }.Schedule();
                _scheduled = true;
                JobHandle.ScheduleBatchedJobs();
            }

            // Asked without blocking, exactly as the dispatcher requires.
            public bool IsComplete => _scheduled && _handle.IsCompleted;

            public void Collect()
            {
                _handle.Complete();
                collectedValue = _result.Value;
                collectCount++;
            }

            public void Dispose()
            {
                if (_scheduled)
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
        /// The dispatcher starts a real job and collects it at a later frame, without ever waiting for it. Each turn
        /// of the loop yields, so the editor really does get its update between one look and the next; the bound on
        /// the loop is a safety net, not a claim about how quickly the job finishes.
        /// </summary>
        [UnityTest]
        public IEnumerator ARealJob_IsScheduledAndCollected_AcrossRealFrames()
        {
            SharedWorkDispatcher dispatcher = NewDispatcher(waitingCapacity: 4, reservedForPhysics: 1, maxConcurrent: 2, frameBudget: 4);
            var work = new RealJobWork(41);
            try
            {
                EnqueueOrFail(dispatcher, WorkPriority.HitGeometryFinish, work);

                DispatchProgress first = dispatcher.Dispatch();
                Assert.That(first.scheduled, Is.EqualTo(1), "the job was scheduled");
                Assert.That(dispatcher.ScheduledCount, Is.EqualTo(1));

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
                Assert.That(dispatcher.ScheduledCount, Is.Zero, "with nothing left running");
                Assert.That(dispatcher.TotalScheduled, Is.EqualTo(1));
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
