using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The pool replay's shapes and loads (GRAPH-CONNECT-17 §3-E2) on observed works, without Unity: the quest shape's pool has one worker
    /// and room for one; the forward block issues as many long blockers as there is room for at the tick before the work, and the work's OWN
    /// refusals (the dispatcher's work log) fall while a blocker RAN (its begin before the first, its end after the last); the backward
    /// load issues a competitor right after the dispatch that took the work in, which the quest shape refuses while the work holds the one
    /// place, and replaces it while the work is still out; the pc shape's forward block takes both places. The records say the shape, the
    /// load, the work's own refusals and the load's competitors; the options read F, B, the shape and the blocker's amount, and refuse
    /// what does not fit. Nothing here measures a time: the tests say who waited for whom.
    /// </summary>
    public class SpatialSplitReplayPoolLoadTests
    {
        private sealed class ObservedWork : IDispatchWork, IDispatchObserved, SpatialSplitReplayPool.IRunStamped
        {
            public long acceptedAt, beganAt, endedAt, collectedAt;
            public int busyMilliseconds;
            public volatile bool collected;
            public long BeganAt => beganAt;
            public long EndedAt => endedAt;
            public void Begin()
            {
                beganAt = Stopwatch.GetTimestamp();
                if (busyMilliseconds > 0) { long until = Stopwatch.GetTimestamp() + busyMilliseconds * (Stopwatch.Frequency / 1000); while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(100); }
                endedAt = Stopwatch.GetTimestamp();
            }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { collectedAt = Stopwatch.GetTimestamp(); collected = true; }
            public void NoteAccepted(long timestamp) { if (acceptedAt == 0) acceptedAt = timestamp; }
        }

        private bool _logBefore;

        [SetUp]
        public void SetUp()
        {
            DispatchObservation.Enabled = true;
            CutSpatialSplit.TimelineObserve = true;
            _logBefore = DispatchObservation.WorkLog;
            DispatchRecord.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            DispatchObservation.Enabled = true;
            CutSpatialSplit.TimelineObserve = true;
            DispatchObservation.WorkLog = _logBefore;
            DispatchRecord.ResetForTests();
        }

        [Test]
        public void TheQuestShape_HasOneWorkerAndRoomForOne_AndThePcShapeIsTheProductsPool()
        {
            using (var quest = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Quest, SpatialSplitReplayPoolLoad.Level, 0, 1000, 1000))
            {
                Assert.That(quest.Background.Workers.Count, Is.EqualTo(1));
                Assert.That(quest.Background.Capacity, Is.EqualTo(1));
                Assert.That(quest.Capacity, Is.EqualTo(1));
                StringAssert.Contains("shape quest, load L0", quest.Describe());
            }
            using (var pc = new SpatialSplitReplayPool(0, 1000))
            {
                Assert.That(pc.Background.Workers.Count, Is.EqualTo(WorkerPoolExecutor.DefaultBackgroundWorkerCount));
                Assert.That(pc.Background.Capacity, Is.EqualTo(SpatialSplitReplayPool.PoolCapacity));
                StringAssert.Contains("shape pc, load L0", pc.Describe());
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Quest, SpatialSplitReplayPoolLoad.Level, 2, 1000, 1000), "L2 is above the quest shape's room");
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Pc, SpatialSplitReplayPoolLoad.Forward, 1, 1000, 1000), "a forward load has no level");
        }

        [Test]
        public void TheForwardBlock_InTheQuestShape_TheWorkIsRefusedItselfWhileTheBlockerRuns()
        {
            // a blocker of 60 M steps (tens of ms): issued at the tick before the work, it holds the one place when the work comes
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Quest, SpatialSplitReplayPoolLoad.Forward, 0, 1000, 60000000))
            {
                var work = new ObservedWork { busyMilliseconds = 1 };
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "D", "1/7", null);
                Assert.That(work.collected, Is.True);
                Assert.That(run.blockers.Length, Is.EqualTo(1), "one blocker: the quest shape's room");
                SpatialSplitReplayPool.CompetitorWork blocker = pool.Competitors[run.blockers[0]];
                Assert.That(blocker.role, Is.EqualTo("blocker"));
                Assert.That(run.ownRefused.HasValue, Is.True, "the work log was on: its own refusals were read");
                Assert.That(run.ownRefused.Value, Is.GreaterThanOrEqualTo(1), "the work itself met the full pool");
                Assert.That(blocker.beganAt, Is.GreaterThan(0L).And.LessThanOrEqualTo(run.firstRefusal), "the first refusal fell after the blocker began");
                Assert.That(run.lastRefusal, Is.LessThan(blocker.endedAt), "and the last before it ended: refused while the blocker RAN");
                Assert.That(work.acceptedAt, Is.GreaterThanOrEqualTo(blocker.endedAt), "the work was taken in only after the blocker ended");
                Assert.That(run.backward.Length, Is.Zero);
                Assert.That(run.drained, Is.True);
                string line = pool.CompetitorLines().First();
                StringAssert.Contains("\"role\":\"blocker\"", line);
                StringAssert.Contains("\"steps\":60000000", line);
                StringAssert.Contains("\"refused\":0", line);
            }
        }

        [Test]
        public void TheForwardBlock_InThePcShape_TakesBothPlaces()
        {
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Pc, SpatialSplitReplayPoolLoad.Forward, 0, 1000, 60000000))
            {
                var work = new ObservedWork { busyMilliseconds = 1 };
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "C", "1/8", null);
                Assert.That(run.blockers.Length, Is.EqualTo(2), "two blockers: the pc shape's room");
                Assert.That(run.ownRefused.Value, Is.GreaterThanOrEqualTo(1), "both places taken: the work itself was refused");
                long firstEnd = Math.Min(pool.Competitors[run.blockers[0]].endedAt, pool.Competitors[run.blockers[1]].endedAt);
                Assert.That(run.lastRefusal, Is.LessThan(Math.Max(pool.Competitors[run.blockers[0]].endedAt, pool.Competitors[run.blockers[1]].endedAt)));
                Assert.That(run.firstRefusal, Is.LessThan(firstEnd + Stopwatch.Frequency), "refused while the blockers ran");
            }
        }

        [Test]
        public void TheBackwardLoad_InTheQuestShape_TheCompetitorWaitsForTheWork_AndIsReplacedWhileTheWorkIsOut()
        {
            // the work runs 40 ms: a competitor issued right after the work was taken in is refused while the work holds the one place
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Quest, SpatialSplitReplayPoolLoad.Backward, 0, 1000, 1000))
            {
                var work = new ObservedWork { busyMilliseconds = 40 };
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "D", "1/9", null);
                Assert.That(work.collected, Is.True);
                Assert.That(run.backward.Length, Is.GreaterThanOrEqualTo(1), "a backward competitor was issued");
                SpatialSplitReplayPool.CompetitorWork first = pool.Competitors[run.backward[0]];
                Assert.That(first.role, Is.EqualTo("backward"));
                Assert.That(first.enqueuedAt, Is.GreaterThanOrEqualTo(work.acceptedAt), "issued after the work was taken in");
                Assert.That(first.enqueuedAt, Is.LessThan(work.endedAt), "while the work still ran");
                Assert.That(pool.Dispatcher.TryFindWorkLog(first.ticket, out DispatchWorkRecord r), Is.True);
                Assert.That(r.refused, Is.GreaterThanOrEqualTo(1), "the one place was the work's: the competitor was refused");
                Assert.That(first.beganAt, Is.GreaterThanOrEqualTo(work.endedAt), "it began only after the work ended");
                Assert.That(run.ownRefused.Value, Is.Zero, "the work itself was not refused: the backward load comes after it");
                Assert.That(run.blockers.Length, Is.Zero);
                Assert.That(run.drained, Is.True);
                StringAssert.Contains("\"role\":\"backward\"", pool.CompetitorLines().First());
            }
        }

        [Test]
        public void TheBackwardLoad_AfterTheWorkWasCollected_IssuesNothingMore()
        {
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Quest, SpatialSplitReplayPoolLoad.Backward, 0, 1000, 1000))
            {
                var work = new ObservedWork();   // ends at once: it may be collected before any competitor is issued
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "C", "1/10", null);
                foreach (int i in run.backward)
                {
                    Assert.That(pool.Competitors[i].enqueuedAt, Is.LessThan(work.collectedAt), "no competitor after the work's collection");
                }
                Assert.That(run.drained, Is.True);
            }
        }

        [Test]
        public void WithTheNewObservationOff_TheOwnRefusalsAreNotMeasured_Null()
        {
            DispatchRecord.Off = true;
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Quest, SpatialSplitReplayPoolLoad.Forward, 0, 1000, 20000000))
            {
                Assert.That(DispatchObservation.WorkLog, Is.False, "kept off by -zantetsuDispatchObserveOff");
                var work = new ObservedWork();
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "D", "1/11", null);
                Assert.That(run.ownRefused.HasValue, Is.False, "not measured: null, never a 0");
                var record = new SpatialSplitPoolRecord { ownRefused = run.ownRefused, blockers = run.blockers, backward = run.backward, load = "F", shape = "quest" };
                string json = record.Json(0, System.Globalization.CultureInfo.InvariantCulture);
                StringAssert.Contains("\"ownRefused\":null", json);
                StringAssert.Contains("\"workLogObserved\":false", json);
                StringAssert.Contains("\"observed\":{\"dispatch\":", json);
                StringAssert.Contains("\"refused\":null", pool.CompetitorLines().First());
            }
        }

        [Test]
        public void TheOptions_ReadTheShapeTheLoadsAndTheBlockersAmount_AndRefuseWhatDoesNotFit()
        {
            string[] Args(params string[] a) => new[] { "player", "-in", "span" }.Concat(a).ToArray();
            bool Read(string[] a, out SpatialSplitReplayOptions o, out string why) =>
                SpatialSplitReplayOptions.TryRead(a, "-in", "-graph", "-list", "-bent", "-order", "-pool", "-work", "-shape", "-blocker", out o, out why);
            Assert.That(Read(Args("-pool", "F", "-shape", "quest", "-blocker", "123"), out SpatialSplitReplayOptions f, out string w1), Is.True, w1);
            Assert.That(f.pool && f.questShape && f.poolLoad == SpatialSplitReplayPoolLoad.Forward && f.blockerWork == 123, Is.True);
            Assert.That(f.PoolName, Is.EqualTo("F (quest shape)"));
            Assert.That(Read(Args("-pool", "B", "-shape", "pc"), out SpatialSplitReplayOptions b, out string w2), Is.True, w2);
            Assert.That(b.poolLoad == SpatialSplitReplayPoolLoad.Backward && !b.questShape, Is.True);
            Assert.That(Read(Args("-pool", "L1"), out SpatialSplitReplayOptions l1, out string w3), Is.True, w3);
            Assert.That(l1.poolLoad == SpatialSplitReplayPoolLoad.Level && l1.competitorLevel == 1 && !l1.questShape, Is.True);
            Assert.That(Read(Args("-pool", "L2", "-shape", "quest"), out _, out string w4), Is.False);
            StringAssert.Contains("above the quest shape's room", w4);
            Assert.That(Read(Args("-pool", "L0", "-blocker", "5"), out _, out string w5), Is.False);
            StringAssert.Contains("needs -pool F", w5);
            Assert.That(Read(Args("-shape", "quest"), out _, out string w6), Is.False);
            StringAssert.Contains("needs -pool", w6);
            Assert.That(Read(Args("-pool", "F", "-shape", "mobile"), out _, out string w7), Is.False);
            StringAssert.Contains("takes pc or quest", w7);
            Assert.That(Read(Args("-pool", "X"), out _, out string w8), Is.False);
            StringAssert.Contains("L0, L1 or L2, F or B", w8);
            // the old reading (GRAPH-CONNECT-14's callers): F and B are not its values
            Assert.That(SpatialSplitReplayOptions.TryRead(Args("-pool", "F"), "-in", "-graph", "-list", "-bent", "-order", "-pool", "-work", out _, out string w9), Is.False);
            StringAssert.Contains("takes L0, L1 or L2, not 'F'", w9);
        }
    }
}
