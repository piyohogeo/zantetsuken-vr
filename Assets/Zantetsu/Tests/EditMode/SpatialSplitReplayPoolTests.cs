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
    /// The pool replay's harness (GRAPH-CONNECT-14 §3-1, TL review 1 of stage 1) on observed works, without Unity: a work that does not
    /// come back within the attempt timeout stops the harness -- the dispatcher is shut down, and the exception says whether the work was
    /// collected (its input may be freed) or not (it is held) -- with short timeouts and a gate; a competitor collected at a dispatch is
    /// replaced at the same tick and its run overlaps the work's (the evidence of a shared CPU); the competitors carry the condition, the
    /// sample and the warm-up flag; the workers' busy deltas are per run.
    /// </summary>
    public class SpatialSplitReplayPoolTests
    {
        private sealed class ObservedWork : IDispatchWork, IDispatchObserved, SpatialSplitReplayPool.IRunStamped
        {
            public long acceptedAt, beganAt, endedAt, collectedAt;
            public ManualResetEventSlim gate;
            public int busyMilliseconds;   // a run of this length (a spin), so that a competitor can overlap it
            public volatile bool collected;
            public long BeganAt => beganAt;
            public long EndedAt => endedAt;
            public void Begin()
            {
                beganAt = Stopwatch.GetTimestamp();
                gate?.Wait();
                if (busyMilliseconds > 0) { long until = Stopwatch.GetTimestamp() + busyMilliseconds * (Stopwatch.Frequency / 1000); while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(100); }
                endedAt = Stopwatch.GetTimestamp();
            }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { collectedAt = Stopwatch.GetTimestamp(); collected = true; }
            public void NoteAccepted(long timestamp) { if (acceptedAt == 0) acceptedAt = timestamp; }
        }

        [SetUp]
        public void SetUp()
        {
            DispatchObservation.Enabled = true;
            CutSpatialSplit.TimelineObserve = true;
        }

        [TearDown]
        public void TearDown()
        {
            DispatchObservation.Enabled = true;
            CutSpatialSplit.TimelineObserve = true;
        }

        [Test]
        public void AWorkHeldPastTheStopTimeout_StopsTheHarness_AndSaysTheWorkWasNotCollected_SoItsInputIsHeld()
        {
            var gate = new ManualResetEventSlim(false);
            int freed = 0;
            using (var pool = new SpatialSplitReplayPool(0, 1000, attemptTicks: 3, stopMilliseconds: 100))
            {
                var work = new ObservedWork { gate = gate };
                SpatialSplitReplayPoolStopException stop = null;
                try { pool.RunWork(work, () => work.collected, "D", "1/7", null); }
                catch (SpatialSplitReplayPoolStopException e) { stop = e; }
                Assert.That(stop, Is.Not.Null, "the harness stopped instead of returning");
                Assert.That(stop.WorkCollected, Is.False, "the work is still on the worker: not collected");
                Assert.That(stop.WorkersStopped, Is.False, "the workers did not confirm their stop within 100 ms");
                Assert.That(work.beganAt, Is.GreaterThan(0), "it did begin"); Assert.That(work.collected, Is.False);
                Assert.That(pool.Stopped, Is.True);
                if (stop.WorkCollected) freed++;   // the caller's rule: free only when collected
                Assert.That(freed, Is.Zero, "nothing freed while the work is out");
                var again = Assert.Throws<SpatialSplitReplayPoolStopException>(() => pool.RunWork(new ObservedWork(), () => true, "D", "1/8", null), "nothing runs after the stop");
                Assert.That(again.WorkCollected, Is.True, "a work never enqueued may be freed by its caller");
                StringAssert.Contains("stopped by a timeout: YES", pool.Describe());
                gate.Set();   // the worker finishes; the pool's disposal below waits for nothing more than that
                for (int i = 0; i < 200 && work.endedAt == 0; i++) Thread.Sleep(1);
                Assert.That(work.endedAt, Is.GreaterThan(0), "the worker finished after the gate opened");
            }
            gate.Dispose();
        }

        [Test]
        public void AWorkThatComesBackDuringTheStop_IsReportedCollected_SoItsInputIsFreedOnce()
        {
            var gate = new ManualResetEventSlim(false);
            int freed = 0;
            using (var pool = new SpatialSplitReplayPool(0, 1000, attemptTicks: 2, stopMilliseconds: 3000))
            {
                var work = new ObservedWork { gate = gate };
                var opener = new Thread(() => { Thread.Sleep(150); gate.Set(); });
                opener.Start();
                SpatialSplitReplayPoolStopException stop = null;
                try { pool.RunWork(work, () => work.collected, "C", "1/9", null); }
                catch (SpatialSplitReplayPoolStopException e) { stop = e; }
                opener.Join();
                Assert.That(stop, Is.Not.Null, "the attempt timeout (2 ticks) passed before the gate opened: the harness stopped");
                Assert.That(stop.WorkCollected, Is.True, "the stop waited for the worker and collected the work");
                Assert.That(stop.WorkersStopped, Is.True);
                Assert.That(work.collected, Is.True);
                if (stop.WorkCollected) freed++;
                if (stop.WorkCollected) { /* a second look at the same decision frees nothing more */ }
                Assert.That(freed, Is.EqualTo(1), "freed once, after the collection");
            }
            gate.Dispose();
        }

        [Test]
        public void ANormalRun_ReturnsTheRecord_WithTheDrainAndTheWorkersBusyDeltas()
        {
            using (var pool = new SpatialSplitReplayPool(0, 1000))
            {
                var work = new ObservedWork();
                long t0 = 0;
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "D", "1/3", t => t0 = t);
                Assert.That(work.collected, Is.True);
                Assert.That(t0, Is.GreaterThan(0), "t0 handed to the caller");
                Assert.That(t0, Is.LessThanOrEqualTo(work.acceptedAt)); Assert.That(work.acceptedAt, Is.LessThanOrEqualTo(work.beganAt));
                Assert.That(run.drained, Is.True); Assert.That(run.blocked, Is.EqualTo(0), "L0: nothing in the way (measured 0)"); Assert.That(run.competitorsDuring, Is.EqualTo(0)); Assert.That(run.overlapSeconds, Is.EqualTo(0.0), "measured: no overlap");
                Assert.That(run.workerBusyDelta.Length, Is.EqualTo(pool.Background.Workers.Count));
                Assert.That(run.workerBusyDelta.Sum(), Is.GreaterThan(0.0), "the worker's busy time grew by this run");
                Assert.That(pool.Attempts, Is.EqualTo(1)); Assert.That(pool.Stopped, Is.False);
            }
        }

        [Test]
        public void L2_LongCompetitors_FillThePool_TheWorkIsBlocked_ThenRunsBesideAReplacedCompetitor_AndTheCompetitorsCarryTheirCondition()
        {
            // competitors of 30 M steps (tens of ms each): the pool is full when the work comes, a competitor collected at a dispatch is
            // replaced at the same tick, and the work's run overlaps the other worker's competitor
            using (var pool = new SpatialSplitReplayPool(2, 30000000))
            {
                pool.Warmup = true;
                var warm = new ObservedWork();
                pool.RunWork(warm, () => warm.collected, "D", "warm", null);
                pool.Warmup = false;
                var work = new ObservedWork { busyMilliseconds = 5 };   // a run of 5 ms: long enough for the replacement competitor to begin beside it
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "C", "1/5", null);
                Assert.That(work.collected, Is.True);
                Assert.That(run.blocked.Value, Is.GreaterThanOrEqualTo(1), "the pool was full of competitors when the work came: a capacity-blocked opportunity");
                Assert.That(run.competitorsDuring.Value, Is.GreaterThanOrEqualTo(1), "a competitor ran during the work's run");
                Assert.That(run.overlapSeconds.Value, Is.GreaterThan(0.0), "and their runs overlapped: the CPU was shared");
                Assert.That(run.drained, Is.True, "the pool was emptied afterwards");
                Assert.That(pool.Competitors.Count, Is.GreaterThanOrEqualTo(4));
                Assert.That(pool.Competitors.Any(c => c.warmup && c.condition == "D" && c.sample == "warm"), Is.True, "the warm-up's competitors say so");
                Assert.That(pool.Competitors.Any(c => !c.warmup && c.condition == "C" && c.sample == "1/5"), Is.True, "the run's competitors carry its condition and sample");
                Assert.That(pool.Competitors.All(c => c.collectedAt > 0), Is.True, "every competitor was collected (the drain)");
                string[] lines = pool.CompetitorLines().ToArray();
                Assert.That(lines.Length, Is.EqualTo(pool.Competitors.Count));
                Assert.That(lines.Count(l => l.Contains("\"warmup\":true")), Is.GreaterThanOrEqualTo(1));
                Assert.That(lines.Count(l => l.Contains("\"condition\":\"C\",\"sample\":\"1/5\",\"warmup\":false")), Is.GreaterThanOrEqualTo(1));
                string describe = pool.Describe();
                StringAssert.Contains("competitors warm-up:", describe); StringAssert.Contains("competitors C:", describe);
                Assert.That(pool.WarmupBusySeconds, Is.GreaterThan(0.0), "the warm-up's worker busy time is apart");
            }
        }

        [Test]
        public void ADrainThatDoesNotEmptyThePool_StopsTheSession_TheWorkCollectedButAWorkerStillOut_AndTheNextConditionIsNotRun()
        {
            // L1 with a competitor of seconds (600 M steps) and a drain of 3 ticks: the work comes back, the competitor does not within the drain
            using (var pool = new SpatialSplitReplayPool(1, 600000000, stopMilliseconds: 100, drainTicks: 3))
            {
                var work = new ObservedWork { busyMilliseconds = 1 };
                SpatialSplitReplayPoolStopException stop = null;
                try { pool.RunWork(work, () => work.collected, "D", "1/4", null); }
                catch (SpatialSplitReplayPoolStopException e) { stop = e; }
                Assert.That(stop, Is.Not.Null, "the drain's failure stops the session instead of returning drained=false");
                Assert.That(stop.Reason, Is.EqualTo(SpatialSplitReplayPoolStopReason.DrainTimeout));
                Assert.That(stop.WorkCollected, Is.True, "the work itself came back: its input may be freed");
                Assert.That(stop.WorkersStopped, Is.False, "a competitor is still out on a worker: the stop was not confirmed within 100 ms");
                Assert.That(work.collected, Is.True);
                Assert.That(pool.Stopped, Is.True); Assert.That(pool.DrainFailures, Is.EqualTo(1));
                var next = Assert.Throws<SpatialSplitReplayPoolStopException>(() => pool.RunWork(new ObservedWork(), () => true, "C", "1/4", null), "the next condition is not run");
                Assert.That(next.Reason, Is.EqualTo(SpatialSplitReplayPoolStopReason.AlreadyStopped));
                StringAssert.Contains("stopped by a timeout: YES", pool.Describe());
            }
        }

        [Test]
        public void WithTheObservationOff_TheWorkIsStillRun_AndTheStampsThatAreTheObservations_StayAbsent()
        {
            DispatchObservation.Enabled = false;
            CutSpatialSplit.TimelineObserve = false;
            using (var pool = new SpatialSplitReplayPool(1, 1000))
            {
                var work = new ObservedWork();
                long t0 = -1;
                SpatialSplitReplayPool.RunRecord run = pool.RunWork(work, () => work.collected, "D", "1/2", t => t0 = t);
                Assert.That(work.collected, Is.True);
                Assert.That(t0, Is.Zero, "no t0 with the timeline off");
                Assert.That(work.acceptedAt, Is.Zero, "no acceptance stamp with the observation off");
                Assert.That(run.blocked.HasValue, Is.False, "no blocked count is taken: null, not a measured 0");
                Assert.That(run.workerBusyDelta, Is.Null, "no busy time taken: null");
                Assert.That(run.competitorsDuring.HasValue, Is.False, "no overlap measured: null");
                Assert.That(run.overlapSeconds.HasValue, Is.False);
                var record = new SpatialSplitPoolRecord { condition = "D", level = 1, competitorWork = 1000, blocked = run.blocked, competitorsDuring = run.competitorsDuring, overlapSeconds = run.overlapSeconds, workerBusyDeltaSeconds = run.workerBusyDelta, t2 = work.beganAt, t3 = work.endedAt };
                string json = record.Json(0, System.Globalization.CultureInfo.InvariantCulture);
                StringAssert.Contains("\"blocked\":null", json); StringAssert.Contains("\"competitorsDuring\":null", json); StringAssert.Contains("\"overlapMs\":null", json); StringAssert.Contains("\"workerBusyDeltaMs\":null", json);
                StringAssert.Contains("\"observed\":{\"dispatch\":false,\"timeline\":false}", json);
                StringAssert.Contains("observation OFF", pool.Describe()); StringAssert.Contains("timeline OFF", pool.Describe());
                Assert.That(pool.Competitors.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(pool.Competitors.All(c => c.enqueuedAt == 0 && c.acceptedAt == 0 && c.beganAt == 0), Is.True, "the competitors' stamps are absent, not 0 intervals");
                string line = pool.CompetitorLines().First();
                StringAssert.Contains("\"t0Ms\":null", line); StringAssert.Contains("\"waitMs\":null", line);
                Assert.That(pool.Dispatcher.SubmittedByPurpose(WorkPurpose.Maintenance), Is.Zero, "the counts are not taken while off");
                Assert.That(pool.Dispatcher.TotalSubmitted, Is.GreaterThanOrEqualTo(2), "the existing counts stand");
            }
        }
    }
}
