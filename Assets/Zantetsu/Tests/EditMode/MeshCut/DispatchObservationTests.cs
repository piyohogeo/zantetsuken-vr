using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The observation of the shared dispatch (GRAPH-CONNECT-14 §2, §5 a-c) on a real Background pool: the acceptance stamp a pool
    /// gives an observed work inside its accept lock is never later than the work's own Begin (t1 ≤ t2), even for a work that begins
    /// and ends at once; the dispatcher's counts by purpose and its capacity-blocked opportunities; the workers' busy time; and the
    /// clocks' switch, which leaves no stamp and no busy time. The capacity wait is MADE here with gated works (the instrument's test
    /// of §3-1-1), not expected from a load.
    /// </summary>
    public class DispatchObservationTests
    {
        private sealed class ObservedWork : IDispatchWork, IDispatchObserved
        {
            public long enqueuedAt, acceptedAt, beganAt, endedAt, collectedAt;
            public ManualResetEventSlim gate;
            public volatile bool collected;
            public volatile bool began;

            public void Begin()
            {
                beganAt = Stopwatch.GetTimestamp();
                began = true;
                gate?.Wait();
                endedAt = Stopwatch.GetTimestamp();
            }

            public bool IsComplete => true;

            public WorkOutcome lastOutcome;

            public void Collect(WorkCompletion completion)
            {
                collectedAt = Stopwatch.GetTimestamp();
                lastOutcome = completion.outcome;
                collected = true;
            }

            public void NoteAccepted(long timestamp)
            {
                if (acceptedAt == 0) acceptedAt = timestamp;
            }
        }

        /// <summary>A destination that takes nothing (the Geometry pool is not this test's).</summary>
        private sealed class IdleExecutor : IWorkExecutor
        {
            public IdleExecutor(WorkDestination destination) { Destination = destination; }
            public WorkDestination Destination { get; }
            public int Capacity => 1;
            public int Held => 0;
            public bool CanAccept => false;
            public bool TryAccept(IDispatchWork work) => false;
            public void BeginAccepted(IDispatchWork work) { }
            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion) { work = null; completion = default; return false; }
            public void CloseForNewWork() { }
            public bool StopAndConfirm(int timeoutMilliseconds) => true;
        }

        private WorkerPoolExecutor _pool;
        private UnityJobWorkExecutor _job;
        private SharedWorkDispatcher _dispatcher;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            DispatchObservation.Enabled = true;
            _pool = WorkerPoolExecutor.BackgroundPool(2);
            Assert.That(_pool.WaitUntilStartupFinished(5000), Is.True, "the pool's workers started");
            _job = new UnityJobWorkExecutor(1);
            _dispatcher = new SharedWorkDispatcher(8, 2, 16, _job, new IdleExecutor(WorkDestination.GeometryPool), _pool);
            _frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            DispatchObservation.Enabled = true;
            try { _dispatcher?.Shutdown(5000); } catch (Exception) { }
            _pool?.Dispose();
        }

        private void Pump()
        {
            _dispatcher.BeginFrame(++_frame);
            _dispatcher.Dispatch();
        }

        private void PumpUntil(Func<bool> done, string what, int milliseconds = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.ElapsedMilliseconds > milliseconds) Assert.Fail(what + ": not within " + milliseconds + " ms");
                Pump();
                Thread.Sleep(1);
            }
        }

        private static void WaitUntil(Func<bool> done, string what, int milliseconds = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.ElapsedMilliseconds > milliseconds) Assert.Fail(what + ": not within " + milliseconds + " ms");
                Thread.Sleep(1);
            }
        }

        private double BusySum()
        {
            double sum = 0.0;
            foreach (WorkerPoolExecutor.WorkerObservation w in _pool.Workers) sum += w.BusySeconds;
            return sum;
        }

        [Test]
        public void AnImmediateWork_IsStampedInOrder_t0_t1_t2_t3_t4_AndTheCountsFollowIt()
        {
            var work = new ObservedWork { enqueuedAt = Stopwatch.GetTimestamp() };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out _), Is.True);
            PumpUntil(() => work.collected, "the work collected");
            Assert.That(work.acceptedAt, Is.GreaterThan(0), "the pool stamped the acceptance");
            Assert.That(work.enqueuedAt, Is.LessThanOrEqualTo(work.acceptedAt), "t0 <= t1");
            Assert.That(work.acceptedAt, Is.LessThanOrEqualTo(work.beganAt), "t1 <= t2: the stamp inside the accept lock, before the queue has the work (TL P1)");
            Assert.That(work.beganAt, Is.LessThanOrEqualTo(work.endedAt), "t2 <= t3");
            Assert.That(work.endedAt, Is.LessThanOrEqualTo(work.collectedAt), "t3 <= t4");
            Assert.That(_dispatcher.SubmittedByPurpose(WorkPurpose.Maintenance), Is.EqualTo(1));
            Assert.That(_dispatcher.CollectedByPurpose(WorkPurpose.Maintenance), Is.EqualTo(1));
            Assert.That(_dispatcher.SubmittedByPurpose(WorkPurpose.Speculative), Is.Zero);
            Assert.That(_dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool), Is.Zero, "nothing was in the way");
            Assert.That(BusySum(), Is.GreaterThan(0.0), "the worker's busy time grew by the work's Begin");
        }

        [Test]
        public void GatedWorks_FillThePool_TheWaitingWork_IsBlockedByTheCapacity_ItsStampsStayInOrder_AndTheWorkersBusyTimeIsTheGatesHold()
        {
            // the pool's two places taken by works that wait at a gate inside Begin: the capacity wait is made, not hoped for
            var gate = new ManualResetEventSlim(false);
            var first = new ObservedWork { gate = gate, enqueuedAt = Stopwatch.GetTimestamp() };
            var second = new ObservedWork { gate = gate, enqueuedAt = Stopwatch.GetTimestamp() };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, first, out _), Is.True);
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, second, out _), Is.True);
            Pump();
            WaitUntil(() => first.began && second.began, "both gated works began on the two workers");
            Assert.That(_pool.Held, Is.EqualTo(2), "the pool is full");
            Assert.That(first.acceptedAt, Is.GreaterThan(0).And.LessThanOrEqualTo(first.beganAt));
            Assert.That(second.acceptedAt, Is.GreaterThan(0).And.LessThanOrEqualTo(second.beganAt));
            // the observed work waits in the dispatcher: no room in the pool
            var waiting = new ObservedWork { enqueuedAt = Stopwatch.GetTimestamp() };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, waiting, out _), Is.True);
            for (int i = 0; i < 3; i++) Pump();
            Assert.That(waiting.acceptedAt, Is.Zero, "not accepted while the pool is full");
            Assert.That(_dispatcher.WaitingCountFor(WorkDestination.BackgroundPool), Is.EqualTo(1));
            Assert.That(_dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool), Is.GreaterThanOrEqualTo(3), "each opportunity that could not submit it was counted");
            Thread.Sleep(20);   // the gates' hold: the workers' busy time
            gate.Set();
            PumpUntil(() => first.collected && second.collected && waiting.collected, "everything collected after the gate opened");
            Assert.That(waiting.acceptedAt, Is.GreaterThan(waiting.enqueuedAt), "W1 > 0: it waited for the room");
            Assert.That(waiting.acceptedAt, Is.LessThanOrEqualTo(waiting.beganAt), "t1 <= t2 after the wait too");
            Assert.That(waiting.beganAt, Is.LessThanOrEqualTo(waiting.endedAt));
            Assert.That(waiting.endedAt, Is.LessThanOrEqualTo(waiting.collectedAt));
            Assert.That(_dispatcher.SubmittedByPurpose(WorkPurpose.Speculative), Is.EqualTo(2));
            Assert.That(_dispatcher.SubmittedByPurpose(WorkPurpose.Maintenance), Is.EqualTo(1));
            Assert.That(_dispatcher.CollectedByPurpose(WorkPurpose.Speculative) + _dispatcher.CollectedByPurpose(WorkPurpose.Maintenance), Is.EqualTo(3));
            Assert.That(BusySum(), Is.GreaterThanOrEqualTo(0.030), "two workers held at the gate for 20 ms or more each");
            gate.Dispose();
        }

        [Test]
        public void WithTheObservationOff_NoStampNoBusyTimeAndNoNewCount_WhileTheExistingCountsStand()
        {
            DispatchObservation.Enabled = false;
            var work = new ObservedWork { enqueuedAt = Stopwatch.GetTimestamp() };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out _), Is.True);
            PumpUntil(() => work.collected, "the work collected");
            Assert.That(work.acceptedAt, Is.Zero, "no stamp: the pool read no clock for it");
            Assert.That(BusySum(), Is.Zero, "no busy time taken");
            Assert.That(work.beganAt, Is.GreaterThan(0), "the work's own clocks are its own");
            Assert.That(_dispatcher.SubmittedByPurpose(WorkPurpose.Maintenance), Is.Zero, "the new counts are not taken while off (TL P2-2)");
            Assert.That(_dispatcher.CollectedByPurpose(WorkPurpose.Maintenance), Is.Zero);
            Assert.That(_dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool), Is.Zero);
            Assert.That(_dispatcher.TotalSubmitted, Is.EqualTo(1), "the existing counts stand"); Assert.That(_dispatcher.TotalCollected, Is.EqualTo(1));
        }

        [Test]
        public void TheShutdown_CollectsAnAcceptedWork_AndCountsItByPurpose_WhileAWaitingWorkIsCancelledNotCollected()
        {
            var gate = new ManualResetEventSlim(false);
            var running = new ObservedWork { gate = gate, enqueuedAt = Stopwatch.GetTimestamp() };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, running, out _), Is.True);
            Pump();
            WaitUntil(() => running.began, "the gated work began on a worker");
            var waiting = new ObservedWork { enqueuedAt = Stopwatch.GetTimestamp() };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, waiting, out _), Is.True);   // never dispatched: it waits
            Assert.That(_dispatcher.WaitingCount, Is.EqualTo(1));
            var opener = new Thread(() => { Thread.Sleep(50); gate.Set(); });
            opener.Start();
            DispatchShutdownResult result = _dispatcher.Shutdown(5000);
            opener.Join();
            Assert.That(result.workersStopped, Is.True, "the worker finished after the gate opened and stopped");
            Assert.That(result.collected, Is.EqualTo(1), "the running work was collected by the stop");
            Assert.That(result.cancelled, Is.EqualTo(1), "the waiting work was cancelled, never submitted");
            Assert.That(running.collected, Is.True); Assert.That(running.lastOutcome, Is.EqualTo(WorkOutcome.Finished));
            Assert.That(waiting.collected, Is.True); Assert.That(waiting.lastOutcome, Is.EqualTo(WorkOutcome.Cancelled));
            Assert.That(_dispatcher.CollectedByPurpose(WorkPurpose.Speculative), Is.EqualTo(1), "the stop's collection counts by purpose as the frame's does (TL P2-5)");
            Assert.That(_dispatcher.CollectedByPurpose(WorkPurpose.Maintenance), Is.Zero, "a cancellation is not a collection");
            Assert.That(_dispatcher.TotalCollected, Is.EqualTo(_dispatcher.CollectedByPurpose(WorkPurpose.Speculative) + _dispatcher.CollectedByPurpose(WorkPurpose.Maintenance)), "the total and the purposes agree");
            gate.Dispose();
        }
    }
}
