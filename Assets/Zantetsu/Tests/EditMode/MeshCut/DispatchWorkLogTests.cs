using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The dispatcher's work log (GRAPH-CONNECT-17 §3-E4, the instrument's tests; development builds): one record a Background work
    /// offered while <see cref="DispatchObservation.WorkLog"/> is on (the other destinations' works are not logged; a work moved out
    /// of Background ends its record as MovedOut) -- its stamps in order (t0 ≤ t1 ≤ t2 ≤ t3 ≤ t4, t1 the pool's own acceptance stamp,
    /// the same an observed work is told),
    /// its worker, its own number, how often an opportunity could not submit it for want of room and when (made here with GATED works,
    /// not expected from a load), and how it ended or where it stands: collected, failed, not run (a stop's cancellation in the pool),
    /// cancelled while waiting, withdrawn, and, read before collection, waiting / accepted / running / finished not collected. Nothing of
    /// it with the log off, and a work left waiting by the frame's budget is not a refusal.
    /// </summary>
    public class DispatchWorkLogTests
    {
        private sealed class Work : IDispatchWork, IDispatchLabeled
        {
            public ManualResetEventSlim gate;
            public volatile bool began, collected;
            public bool fail;
            public long label = -1;
            public WorkOutcome outcome;

            public void Begin()
            {
                began = true;
                gate?.Wait();
                if (fail) throw new InvalidOperationException("the test's failing work");
            }

            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { outcome = completion.outcome; collected = true; }
            public long DispatchLabel => label;
        }

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
        private SharedWorkDispatcher _dispatcher;
        private int _frame;
        private bool _logBefore;

        private void Make(int capacity, int workers, int frameBudget = 16)
        {
            _pool = WorkerPoolExecutor.BackgroundPoolForReplay(capacity, workers);
            Assert.That(_pool.WaitUntilStartupFinished(5000), Is.True, "the pool's workers started");
            _dispatcher = new SharedWorkDispatcher(8, 2, frameBudget, new UnityJobWorkExecutor(1), new IdleExecutor(WorkDestination.GeometryPool), _pool);
            _frame = 0;
        }

        [SetUp]
        public void SetUp()
        {
            _logBefore = DispatchObservation.WorkLog;
            DispatchObservation.WorkLog = true;
        }

        private readonly List<ManualResetEventSlim> _gates = new List<ManualResetEventSlim>();

        // a gate the test holds a work at; every one is opened at the tear-down, so that a failed assertion never leaves a worker
        // held (the pool's stop would wait for it)
        private ManualResetEventSlim Gate()
        {
            var made = new ManualResetEventSlim(false);
            _gates.Add(made);
            return made;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ManualResetEventSlim gate in _gates)
            {
                try { gate.Set(); } catch (ObjectDisposedException) { }
            }
            _gates.Clear();
            DispatchObservation.WorkLog = _logBefore;
            try { _dispatcher?.Shutdown(5000); } catch (Exception) { }
            _pool?.Dispose();
            _dispatcher = null;
            _pool = null;
        }

        private void Pump()
        {
            _dispatcher.BeginFrame(++_frame);
            _dispatcher.Dispatch();
        }

        private void PumpUntil(Func<bool> done, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.ElapsedMilliseconds > 5000) Assert.Fail(what + ": not within 5 s");
                Pump();
                Thread.Sleep(1);
            }
        }

        private static void WaitUntil(Func<bool> done, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.ElapsedMilliseconds > 5000) Assert.Fail(what + ": not within 5 s");
                Thread.Sleep(1);
            }
        }

        private DispatchWorkRecord Record(WorkTicket ticket)
        {
            Assert.That(_dispatcher.TryFindWorkLog(ticket, out DispatchWorkRecord r), Is.True, "the work was logged");
            return r;
        }

        [Test]
        public void WithTheLogOff_NothingIsRecorded()
        {
            DispatchObservation.WorkLog = false;
            Make(2, 2);
            var work = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out WorkTicket ticket), Is.True);
            PumpUntil(() => work.collected, "the work collected");
            Assert.That(_dispatcher.WorkLogCount, Is.Zero);
            Assert.That(_dispatcher.TryFindWorkLog(ticket, out _), Is.False);
            Assert.That(_dispatcher.WorkLogOpportunities, Is.Zero, "no opportunity counted while off");
        }

        [Test]
        public void AnImmediateWork_IsStampedInOrder_WithItsWorkerItsKindAndItsOwnNumber()
        {
            Make(2, 2);
            var work = new Work { label = 4711 };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out WorkTicket ticket), Is.True);
            PumpUntil(() => work.collected, "the work collected");
            DispatchWorkRecord r = Record(ticket);
            Assert.That(r.end, Is.EqualTo(DispatchWorkEnd.Collected));
            Assert.That(r.kind, Is.EqualTo("Work"));
            Assert.That(r.label, Is.EqualTo(4711L), "the work's own number (IDispatchLabeled)");
            Assert.That(r.purpose, Is.EqualTo(WorkPurpose.Maintenance));
            Assert.That(r.destination, Is.EqualTo(WorkDestination.BackgroundPool));
            Assert.That(r.t0, Is.GreaterThan(0L));
            Assert.That(r.t0, Is.LessThanOrEqualTo(r.t1), "t0 <= t1");
            Assert.That(r.t1, Is.LessThanOrEqualTo(r.t2), "t1 <= t2: the pool's acceptance stamp comes before any worker can begin it");
            Assert.That(r.t2, Is.LessThanOrEqualTo(r.t3), "t2 <= t3");
            Assert.That(r.t3, Is.LessThanOrEqualTo(r.t4), "t3 <= t4");
            Assert.That(r.worker, Is.InRange(0, 1));
            Assert.That(r.refused, Is.Zero, "nothing was in the way");
            Assert.That(r.firstRefusal, Is.Zero);
        }

        [Test]
        public void GatedWorks_FillThePool_TheWaitingWork_IsRefusedAtEveryOpportunity_WhileTheyRun_AndItsRefusalsAreItsOwn()
        {
            Make(1, 1);
            var gate = Gate();
            var blocker = new Work { gate = gate };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, blocker, out WorkTicket blockerTicket), Is.True);
            Pump();
            WaitUntil(() => blocker.began, "the blocker began on the one worker");
            var waiting = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, waiting, out WorkTicket waitingTicket), Is.True);
            const int Opportunities = 4;
            for (int i = 0; i < Opportunities; i++) Pump();
            DispatchWorkRecord w = Record(waitingTicket);
            Assert.That(w.refused, Is.EqualTo(Opportunities), "each opportunity that could not submit it for want of room, once each");
            Assert.That(w.t1, Is.Zero, "not accepted while the pool is full");
            Assert.That(w.firstRefusal, Is.GreaterThan(0L).And.LessThanOrEqualTo(w.lastRefusal));
            gate.Set();
            WaitUntil(() => _pool.EndedNotTakenCount == 1, "the blocker ended on its worker (no opportunity in between: no further refusal)");
            PumpUntil(() => blocker.collected && waiting.collected, "both collected after the gate opened");
            DispatchWorkRecord b = Record(blockerTicket);
            w = Record(waitingTicket);
            Assert.That(b.refused, Is.Zero, "the blocker itself was never refused");
            Assert.That(w.refused, Is.EqualTo(Opportunities), "no refusal once the room was free");
            Assert.That(b.t2, Is.LessThanOrEqualTo(w.firstRefusal), "the refusals came while the blocker ran (its begin before the first)");
            Assert.That(w.lastRefusal, Is.LessThan(b.t3), "and before its end");
            Assert.That(w.t1, Is.GreaterThanOrEqualTo(b.t3), "accepted only after the blocker ended");
            Assert.That(w.t1, Is.LessThanOrEqualTo(w.t2));
            gate.Dispose();
        }

        [Test]
        public void AWorkLeftWaitingByTheFramesBudget_IsNotARefusal()
        {
            Make(2, 2, frameBudget: 1);
            var first = new Work();
            var second = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, first, out WorkTicket _), Is.True);
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, second, out WorkTicket secondTicket), Is.True);
            Pump();   // the budget of 1 submits the first; the second waits with room in the pool
            Assert.That(Record(secondTicket).refused, Is.Zero, "room was there; the budget ran out, not the room");
            PumpUntil(() => first.collected && second.collected, "both collected");
            Assert.That(Record(secondTicket).refused, Is.Zero);
        }

        [Test]
        public void AWithdrawnWork_EndsWithdrawn_WithNoAcceptance()
        {
            Make(1, 1);
            var gate = Gate();
            var blocker = new Work { gate = gate };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, blocker, out WorkTicket _), Is.True);
            Pump();
            WaitUntil(() => blocker.began, "the blocker began");
            var waiting = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, waiting, out WorkTicket ticket), Is.True);
            Pump();
            Assert.That(_dispatcher.Cancel(ticket), Is.True);
            DispatchWorkRecord r = Record(ticket);
            Assert.That(r.end, Is.EqualTo(DispatchWorkEnd.Withdrawn));
            Assert.That(r.t1, Is.Zero);
            Assert.That(r.tEnd, Is.GreaterThanOrEqualTo(r.t0));
            gate.Set();
            PumpUntil(() => blocker.collected, "the blocker collected");
            gate.Dispose();
        }

        [Test]
        public void TheLog_ReadBeforeCollection_SaysWaitingAcceptedRunningAndFinishedNotCollected_ThenTheStopsEnds()
        {
            Make(2, 1);   // one worker, room for two: the second accepted work waits in the pool's queue
            var gate = Gate();
            var running = new Work { gate = gate };
            var queued = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, running, out WorkTicket runningTicket), Is.True);
            Pump();
            WaitUntil(() => running.began, "the first work began on the one worker");
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, queued, out WorkTicket queuedTicket), Is.True);
            Pump();   // accepted into the pool's queue behind the running one
            var waiting = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, waiting, out WorkTicket waitingTicket), Is.True);
            Pump();   // the pool is full: it waits in the dispatcher
            DispatchWorkRecord[] snapshot = _dispatcher.SnapshotWorkLog();
            DispatchWorkRecord Of(WorkTicket t) => Array.Find(snapshot, x => x.serial == t.value);
            Assert.That(Of(runningTicket).end, Is.EqualTo(DispatchWorkEnd.Running));
            Assert.That(Of(runningTicket).t2, Is.GreaterThan(0L), "a running work's begin is read from the pool");
            Assert.That(Of(runningTicket).worker, Is.EqualTo(0));
            Assert.That(Of(queuedTicket).end, Is.EqualTo(DispatchWorkEnd.Accepted));
            Assert.That(Of(queuedTicket).t2, Is.Zero);
            Assert.That(Of(waitingTicket).end, Is.EqualTo(DispatchWorkEnd.Waiting));
            // the stop: the waiting work is cancelled in the dispatcher, the queued one never begins (not run), the running one ends
            var opener = new Thread(() => { Thread.Sleep(50); gate.Set(); });
            opener.Start();
            DispatchShutdownResult result = _dispatcher.Shutdown(5000);
            opener.Join();
            Assert.That(result.workersStopped, Is.True);
            Assert.That(Record(waitingTicket).end, Is.EqualTo(DispatchWorkEnd.CancelledWaiting));
            Assert.That(Record(queuedTicket).end, Is.EqualTo(DispatchWorkEnd.NotRun), "accepted, never begun: the stop's cancellation in the pool");
            Assert.That(Record(queuedTicket).t2, Is.Zero, "no begin for a work no worker began");
            Assert.That(Record(queuedTicket).t4, Is.GreaterThan(0L), "collected by the stop");
            Assert.That(Record(runningTicket).end, Is.EqualTo(DispatchWorkEnd.Collected));
            gate.Dispose();
        }

        [Test]
        public void AFinishedWork_ReadBeforeItsCollection_IsFinishedNotCollected_WithItsEnd()
        {
            Make(2, 2);
            var work = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out WorkTicket ticket), Is.True);
            Pump();   // submitted; not collected until the next opportunity
            WaitUntil(() => work.began && _pool.EndedNotTakenCount == 1, "the work ended on its worker");
            DispatchWorkRecord r = Array.Find(_dispatcher.SnapshotWorkLog(), x => x.serial == ticket.value);
            Assert.That(r.end, Is.EqualTo(DispatchWorkEnd.FinishedNotCollected));
            Assert.That(r.t2, Is.GreaterThan(0L));
            Assert.That(r.t3, Is.GreaterThanOrEqualTo(r.t2));
            Assert.That(r.t4, Is.Zero);
            PumpUntil(() => work.collected, "the work collected");
            Assert.That(Record(ticket).end, Is.EqualTo(DispatchWorkEnd.Collected));
        }

        [Test]
        public void AFailingWork_EndsFailed_WithItsStamps()
        {
            Make(2, 2);
            var work = new Work { fail = true };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out WorkTicket ticket), Is.True);
            PumpUntil(() => work.collected, "the work collected");
            DispatchWorkRecord r = Record(ticket);
            Assert.That(r.end, Is.EqualTo(DispatchWorkEnd.Failed));
            Assert.That(r.t2, Is.GreaterThan(0L));
            Assert.That(r.t4, Is.GreaterThanOrEqualTo(r.t3));
        }

        [Test]
        public void AReclassifiedWork_IsLoggedUnderItsNewPurpose()
        {
            Make(1, 1);
            var gate = Gate();
            var blocker = new Work { gate = gate };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, blocker, out WorkTicket _), Is.True);
            Pump();
            WaitUntil(() => blocker.began, "the blocker began");
            var work = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, work, out WorkTicket ticket), Is.True);
            Assert.That(_dispatcher.TryReclassify(ticket, WorkPurpose.Maintenance), Is.True);
            Assert.That(Record(ticket).purpose, Is.EqualTo(WorkPurpose.Maintenance));
            gate.Set();
            PumpUntil(() => blocker.collected && work.collected, "both collected");
            gate.Dispose();
        }

        private sealed class ObservedWork : IDispatchWork, IDispatchObserved
        {
            public volatile bool collected;
            public long accepted;
            public void Begin() { }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { collected = true; }
            public void NoteAccepted(long timestamp) { if (accepted == 0) accepted = timestamp; }
        }

        [Test]
        public void T1_IsThePoolsOwnAcceptanceStamp_TheOneAnObservedWorkIsTold()
        {
            // TL review 8 (P2-3): t1 is the pool's acceptance inside its lock, not the moment before the dispatcher's call (a wait for
            // the pool's lock would otherwise move from W1 into W2); the same stamp as the observed works' acceptedAt, to the tick
            bool observedBefore = DispatchObservation.Enabled;
            DispatchObservation.Enabled = true;
            try
            {
                Make(2, 2);
                var works = new List<(ObservedWork work, WorkTicket ticket)>();
                for (int i = 0; i < 6; i++)
                {
                    var w = new ObservedWork();
                    Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, w, out WorkTicket t), Is.True);
                    works.Add((w, t));
                }
                PumpUntil(() => works.TrueForAll(x => x.work.collected), "every work collected");
                foreach (var (work, ticket) in works)
                {
                    DispatchWorkRecord r = Record(ticket);
                    Assert.That(work.accepted, Is.GreaterThan(0L), "the pool told the observed work its acceptance");
                    Assert.That(r.t1, Is.EqualTo(work.accepted), "t1 = the pool's acceptance stamp, the same tick");
                    Assert.That(r.t0, Is.LessThanOrEqualTo(r.t1));
                    Assert.That(r.t1, Is.LessThanOrEqualTo(r.t2));
                }
            }
            finally { DispatchObservation.Enabled = observedBefore; }
        }

        [Test]
        public void T1_IsStamped_WithTheLogOnAndTheObservationOff()
        {
            bool observedBefore = DispatchObservation.Enabled;
            DispatchObservation.Enabled = false;
            try
            {
                Make(2, 2);
                var work = new ObservedWork();
                Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out WorkTicket ticket), Is.True);
                PumpUntil(() => work.collected, "the work collected");
                DispatchWorkRecord r = Record(ticket);
                Assert.That(work.accepted, Is.Zero, "the observation off: the work is not told");
                Assert.That(r.t1, Is.GreaterThan(0L), "the work log takes the pool's stamp all the same");
                Assert.That(r.t1, Is.InRange(r.t0, r.t2));
            }
            finally { DispatchObservation.Enabled = observedBefore; }
        }

        [Test]
        public void TheOtherDestinationsWorks_AreNotLogged_OnlyBackgrounds()
        {
            // TL review 8 (P2-2): the Unity Job destination has no worker clocks and the other pools are other workers: one row a
            // work put to Background (plan §3-E4), nothing of the others
            Make(2, 2);
            var job = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, job, out WorkTicket jobTicket), Is.True);
            var background = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, background, out WorkTicket backgroundTicket), Is.True);
            PumpUntil(() => job.collected && background.collected, "both collected");
            Assert.That(_dispatcher.TryFindWorkLog(jobTicket, out _), Is.False, "the Unity Job work is not logged");
            DispatchWorkRecord r = Record(backgroundTicket);
            Assert.That(r.destination, Is.EqualTo(WorkDestination.BackgroundPool));
            Assert.That(r.end, Is.EqualTo(DispatchWorkEnd.Collected));
            Assert.That(r.t2, Is.GreaterThan(0L));
            Assert.That(r.t3, Is.GreaterThanOrEqualTo(r.t2));
            Assert.That(_dispatcher.WorkLogCount, Is.EqualTo(1), "one record: the Background work's");
            for (int i = 0; i < _dispatcher.WorkLogCount; i++) Assert.That(_dispatcher.WorkLogAt(i).destination, Is.EqualTo(WorkDestination.BackgroundPool));
        }

        [Test]
        public void AWorkMovedOutOfBackground_EndsItsRecordAsMovedOut_AndOneMovedIn_IsCounted()
        {
            Make(1, 1);
            var gate = Gate();
            var blocker = new Work { gate = gate };
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, blocker, out WorkTicket _), Is.True);
            Pump();
            WaitUntil(() => blocker.began, "the blocker began");
            var leaving = new Work();
            Assert.That(_dispatcher.TryEnqueue(WorkPurpose.Speculative, leaving, out WorkTicket leavingTicket), Is.True);
            Assert.That(_dispatcher.TryReclassify(leavingTicket, WorkPurpose.AdmittedGeometry), Is.True, "moved to the Geometry pool");
            DispatchWorkRecord r = Record(leavingTicket);
            Assert.That(r.end, Is.EqualTo(DispatchWorkEnd.MovedOut));
            Assert.That(r.tEnd, Is.GreaterThanOrEqualTo(r.t0));
            Assert.That(r.t1, Is.Zero);
            // TL review 9 (P2-1): the record stays Background's -- its destination and purpose while it waited there -- and says where it went
            Assert.That(r.destination, Is.EqualTo(WorkDestination.BackgroundPool), "the destination it was logged for");
            Assert.That(r.purpose, Is.EqualTo(WorkPurpose.Speculative), "the purpose it had in Background");
            Assert.That(r.moved, Is.True);
            Assert.That(r.movedTo, Is.EqualTo(WorkDestination.GeometryPool), "where it was moved to");
            Assert.That(_dispatcher.TryReclassify(leavingTicket, WorkPurpose.Maintenance), Is.True, "and back into Background");
            Assert.That(Record(leavingTicket).end, Is.EqualTo(DispatchWorkEnd.MovedOut), "its record is not reopened");
            Assert.That(_dispatcher.WorkLogMovedIn, Is.EqualTo(1L), "the move back in is counted, not logged");
            gate.Set();
            PumpUntil(() => blocker.collected && leaving.collected, "both collected");
            Assert.That(Record(leavingTicket).end, Is.EqualTo(DispatchWorkEnd.MovedOut), "the later run in Background is not on its record");
        }
    }
}
