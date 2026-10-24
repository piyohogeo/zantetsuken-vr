using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    // GRAPH-CONNECT-14 §2, §5 d: the split's timeline in a live world -- an attempt's stamps t0 (enqueue) .. t6 (end on Main) are
    // present and in order on a publication, the intervals are tallied, and the switch leaves the new stamps untaken while the
    // existing clocks stand.
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>
        /// **A published attempt carries every stamp, in order, and is tallied** (GRAPH-CONNECT-14 §2): the trunk and its hand below
        /// the waist, a plane between them -> published; LastTimeline has t0..t6 all present and t0 ≤ t1 ≤ ... ≤ t6; X equals the
        /// worker's run; the description says the timeline is on and counts the attempt.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitTimeline_APublishedAttempt_CarriesEveryStampInOrder_AndIsTallied()
        {
            Assert.That(CutSpatialSplit.TimelineObserve, Is.True, "the timeline is on by default");
            Assert.That(DispatchObservation.Enabled, Is.True);
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOn();
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 2);
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || LowerDone(split, sides[1]), "the lower side ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.DescribeTimeline());
            Assert.That(split.Published, Is.EqualTo(1), "published: " + split.LastEnd + " / " + split.LastRefusal);
            SpatialSplitTimelineRecord r = split.LastTimeline;
            Assert.That(r.end, Is.EqualTo(SpatialSplitEnd.Published));
            for (int i = 0; i <= 6; i++) Assert.That(r.Stamp(i), Is.GreaterThan(0), "stamp t" + i + " present");
            Assert.That(r.InOrder(), Is.True, "t0 <= t1 <= t2 <= t3 <= t4 <= t5 <= t6");
            Assert.That(r.Seconds(SpatialSplitTimelineInterval.WorkerRun), Is.EqualTo(r.workerSeconds).Within(1e-9), "X is the worker's run (the same reading)");
            Assert.That(r.Seconds(SpatialSplitTimelineInterval.DispatcherWait), Is.GreaterThanOrEqualTo(0.0));
            Assert.That(split.TimelineStamped, Is.GreaterThanOrEqualTo(1), "the published attempt was tallied (the one-convex upper side may be another)");
            Assert.That(split.Timeline(SpatialSplitTimelineInterval.WorkerRun).count, Is.EqualTo(split.TimelineStamped), "every stamped attempt had X");
            Assert.That(split.Timeline(SpatialSplitTimelineInterval.HandleSpan).count, Is.EqualTo(split.TimelineStamped), "and S");
            StringAssert.Contains("timeline (on): attempts stamped " + split.TimelineStamped, split.DescribeTimeline());
            StringAssert.Contains("dispatcher submitted/collected by purpose:", split.DescribeTimeline()); StringAssert.Contains("Maintenance 1/1", split.DescribeTimeline());
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The switch leaves the new stamps untaken while the existing clocks stand** (GRAPH-CONNECT-14 §7): with the timeline off and
        /// the pools' clocks off, a publication carries t2 and t3 (the worker's run, an existing reading) and no t0, t1, t4, t5, t6;
        /// nothing is tallied; the description says off. The product's result is the same publication.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitTimeline_Off_TakesNoNewStamp_AndTalliesNothing_WhileTheExistingClocksStand()
        {
            CutWorldRoot root = null;
            try
            {
                CutSpatialSplit.TimelineObserve = false;
                DispatchObservation.Enabled = false;
                root = NewWorld(out Shader _);
                root.Driver.RemainingMainSeconds = () => 1.0;
                var remaining = new double[1];
                CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
                split.Settings = SplitOn();
                LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides, lowerConvexes: 2);
                remaining[0] = 1.0;
                yield return Until(() => split.Published == 1 || LowerDone(split, sides[1]), "the lower side ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.DescribeTimeline());
                Assert.That(split.Published, Is.EqualTo(1), "the publication is the same: " + split.LastEnd);
                SpatialSplitTimelineRecord r = split.LastTimeline;
                Assert.That(r.t0, Is.Zero, "t0 not taken"); Assert.That(r.t1, Is.Zero, "t1 not taken (the pool's clock off)");
                Assert.That(r.t4, Is.Zero, "t4 not taken"); Assert.That(r.t5, Is.Zero, "t5 not taken"); Assert.That(r.t6, Is.Zero, "t6 not taken");
                Assert.That(r.t2, Is.GreaterThan(0), "t2 is the existing clock's reading"); Assert.That(r.t3, Is.GreaterThan(0), "t3 too");
                Assert.That(double.IsNaN(r.Seconds(SpatialSplitTimelineInterval.DispatcherWait)), Is.True, "W1 is absent, not 0");
                Assert.That(r.Seconds(SpatialSplitTimelineInterval.WorkerRun), Is.EqualTo(r.workerSeconds).Within(1e-9));
                Assert.That(split.TimelineStamped, Is.Zero, "nothing tallied");
                StringAssert.Contains("timeline (off", split.DescribeTimeline());
                split.Settings = default;
            }
            finally
            {
                CutSpatialSplit.TimelineObserve = true;
                DispatchObservation.Enabled = true;
            }
            yield return EndWorld(root);
        }

        private static string TimelineField(string line, string name)
        {
            // the JSON field's raw text (a number, null, true/false, a quoted string or a flat object) by its name, or null when absent
            var m = System.Text.RegularExpressions.Regex.Match(line, "\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\":(\\{[^}]*\\}|\"(?:[^\"\\\\]|\\\\.)*\"|[^,}\\]]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// **The pool replay runs each captured sample in both conditions through the real Background pool, with the stamps in order and
        /// the thread's own results** (GRAPH-CONNECT-14 §3-1, §7, §9): a session captured from real bodies (a plane, a bent partition, a
        /// mesh-led cut) replayed four ways -- on this thread as D (span) and as C (span, graph, graph-first), and through the pool at L0 --
        /// gives, per sample and condition, the same result fields from the pool as from the thread (the clocks apart), a pool record whose
        /// stamps t0..t4 are present and in order and whose intervals are non-negative, an emptied pool after each; the L2 run says its
        /// competitors and carries the capacity-blocked count of each attempt (whatever it came to: a load condition, not a verdict); with
        /// the timeline off the pool's stamps are null, not 0, and the results are the same lines.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitTimeline_ThePoolReplay_RunsEachCapturedSampleInBothConditions_WithStampsInOrder_AndTheThreadsResults()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent(cutConvexes: true, minCutSideTriangles: 12);
            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-split-pool-" + Guid.NewGuid().ToString("N"));
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode pool case", null);
            split.Capture = sink;
            try
            {
                LogicalFragmentId withHand = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
                LogicalFragmentId u = AddUWithAPartInside(root, new Vector3(0f, 0f, 12f));
                LogicalFragmentId across = AddBodyWithAHand(root, new Vector3(0f, 0f, 24f), convexAcross: true);
                yield return null;
                var sides = new LogicalFragmentId[3][];
                for (int i = 0; i < 3; i++) sides[i] = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, withHand, sides[0]);
                yield return CutAtTheWaist(root, u, sides[1], lowerConvexes: 5);
                yield return CutAtTheWaist(root, across, sides[2], lowerConvexes: 3);
                remaining[0] = 1.0;
                yield return Until(() => split.Published >= 3 && split.IsIdle && !split.HasAttempt, "the three were tried and published, and the walk stands idle");
                remaining[0] = 0.0;
                Assert.That(sink.Close(20.0), Is.True, "the capture wrote everything");
                split.Capture = null;
                int issued = split.Issued;
                Assert.That(issued, Is.GreaterThanOrEqualTo(3));

                // the thread's own replays, D and C
                string threadD = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "thread-D"), new SpatialSplitReplayOptions { spanInput = true }, out _, out _);
                string threadC = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "thread-C"), new SpatialSplitReplayOptions { spanInput = true, graph = true, graphFirst = true }, out bool threadCAllSame, out _);
                TestContext.Out.WriteLine(threadD);
                TestContext.Out.WriteLine(threadC);
                Assert.That(threadCAllSame, Is.True, "the graph-on verdict: every sample replayed once, the checks passed (the live comparison is not its criterion)");
                StringAssert.Contains("graph on: every listed sample replayed without error", threadC);
                string[] dLines = File.ReadAllLines(Path.Combine(folder, "thread-D", "replay.jsonl"));
                string[] cLines = File.ReadAllLines(Path.Combine(folder, "thread-C", "replay.jsonl"));
                Assert.That(dLines.Length, Is.EqualTo(issued)); Assert.That(cLines.Length, Is.EqualTo(issued));
                // the pool replay at L0
                var l0 = new SpatialSplitReplayOptions { spanInput = true, pool = true, competitorLevel = 0, competitorWork = 1000 };
                string poolSummary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "pool-L0"), l0, out bool poolAllSame, out _);
                TestContext.Out.WriteLine(poolSummary);
                // the verdict (the Player's exit code 0/3): the pool writes two lines a sample, so the count expected is twice the samples -- stage 2's first
                // pool run ended with 3 on this (chain 72, c1-sub-pool-L0-1: replayed 94 of 47 samples, everything else passed)
                Assert.That(poolAllSame, Is.True, "the pool verdict counts both conditions of every sample");
                StringAssert.Contains("pool on: every listed sample replayed in both conditions (D and C) without error", poolSummary);
                StringAssert.EndsWith("(the live comparison is not the criterion here): yes" + Environment.NewLine, poolSummary);
                StringAssert.Contains("pool replay: level L0 (competitor work 1000 steps", poolSummary);
                StringAssert.Contains("attempts " + (2 * issued) + " (warm-up " + (2 * Math.Min(5, issued)) + ",", poolSummary, "every sample in both conditions, and the warm-up of the first ones in both");
                StringAssert.Contains("; timeline on", poolSummary);
                string[] poolLines = File.ReadAllLines(Path.Combine(folder, "pool-L0", "replay.jsonl"));
                Assert.That(poolLines.Length, Is.EqualTo(2 * issued), "one line a sample a condition");
                string[] compared = { "outcome", "meshLed", "bent", "positiveTriangles", "negativeTriangles", "gap", "directionsTried", "planesTested", "triangleVisits", "verdict", "input", "crossingConvexes", "cut", "stagesRan", "graphFirstEffective", "graphOn", "anchorsDistributed", "error" };
                int d = 0, c = 0, stamped = 0;
                foreach (string line in poolLines)
                {
                    string condition = TimelineField(line, "condition");
                    Assert.That(condition, Is.EqualTo("\"D\"").Or.EqualTo("\"C\""), line.Substring(0, 80));
                    bool isC = condition == "\"C\"";
                    if (isC) c++; else d++;
                    Assert.That(TimelineField(line, "error"), Is.EqualTo("\"\""), "replayed without error: " + line.Substring(0, 120));
                    // the pool record: every stamp present and in order, every interval non-negative, the pool emptied
                    double last = double.NegativeInfinity;
                    foreach (string stamp in new[] { "t0Ms", "t1Ms", "t2Ms", "t3Ms", "t4Ms" })
                    {
                        string v = TimelineField(line, stamp);
                        Assert.That(v, Is.Not.Null.And.Not.EqualTo("null"), stamp + " present: " + line.Substring(line.IndexOf("\"pool\"", StringComparison.Ordinal)));
                        double ms = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                        Assert.That(ms, Is.GreaterThanOrEqualTo(last), stamp + " in order (t0 <= t1 <= t2 <= t3 <= t4)");
                        last = ms;
                    }
                    foreach (string interval in new[] { "w1Ms", "w2Ms", "xMs", "w3Ms" })
                    {
                        Assert.That(double.Parse(TimelineField(line, interval), System.Globalization.CultureInfo.InvariantCulture), Is.GreaterThanOrEqualTo(0.0), interval);
                    }
                    Assert.That(TimelineField(line, "drained"), Is.EqualTo("true"), "the pool was emptied after the attempt");
                    Assert.That(TimelineField(line, "warmup"), Is.EqualTo("false"), "the warm-up writes no line");
                    Assert.That(TimelineField(line, "blocked"), Is.EqualTo("0"), "L0: nothing in the way (measured 0)");
                    Assert.That(TimelineField(line, "competitorsDuring"), Is.EqualTo("0"), "L0: measured, none"); Assert.That(TimelineField(line, "overlapMs"), Is.EqualTo("0.000"), "L0: measured 0 overlap (not null)");
                    Assert.That(TimelineField(line, "workerBusyDeltaMs"), Is.Not.EqualTo("null"), "the busy deltas are measured");
                    stamped++;
                    // the same results as the thread's replay of the same sample in the same condition (the clocks apart)
                    string attempt = TimelineField(line, "attempt");
                    string threadLine = Array.Find(isC ? cLines : dLines, l => TimelineField(l, "attempt") == attempt);
                    Assert.That(threadLine, Is.Not.Null, "the thread's line of attempt " + attempt);
                    foreach (string name in compared) Assert.That(TimelineField(line, name), Is.EqualTo(TimelineField(threadLine, name)), name + " of attempt " + attempt + " (" + condition + "): the pool equals the thread");
                }
                Assert.That(d, Is.EqualTo(issued)); Assert.That(c, Is.EqualTo(issued)); Assert.That(stamped, Is.EqualTo(2 * issued));
                Assert.That(TimelineField(poolLines[0], "condition"), Is.EqualTo("\"D\"")); Assert.That(TimelineField(poolLines[1], "condition"), Is.EqualTo("\"C\""));
                Assert.That(TimelineField(poolLines[2], "condition"), Is.EqualTo("\"C\"")); Assert.That(TimelineField(poolLines[3], "condition"), Is.EqualTo("\"D\""));
                Assert.That(Array.Exists(poolLines, l => TimelineField(l, "condition") == "\"C\"" && TimelineField(l, "graphFirstEffective") == "true"), Is.True, "the graph-first order applied to some C attempt (a graph-eligible lower side)");
                Assert.That(File.Exists(Path.Combine(folder, "pool-L0", "competitors.jsonl")), Is.True, "the competitors' file is written even at L0 (empty)");
                // L2: two LONG competitors (30 M steps: tens of ms each) kept outstanding and replaced at the same tick -- the pool is full
                // when every attempt comes (blocked >= 1), and each attempt's run overlaps a competitor's (the CPU shared)
                var l2 = new SpatialSplitReplayOptions { spanInput = true, pool = true, competitorLevel = 2, competitorWork = 30000000 };
                string busySummary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "pool-L2"), l2, out _, out _);
                TestContext.Out.WriteLine(busySummary);
                StringAssert.Contains("pool replay: level L2", busySummary);
                StringAssert.Contains("competitors D:", busySummary); StringAssert.Contains("competitors C:", busySummary); StringAssert.Contains("competitors warm-up:", busySummary);
                int overlapping = 0;
                foreach (string line in File.ReadAllLines(Path.Combine(folder, "pool-L2", "replay.jsonl")))
                {
                    Assert.That(int.Parse(TimelineField(line, "blocked")), Is.GreaterThanOrEqualTo(1), "the full pool blocked the attempt at least once: " + line.Substring(line.IndexOf("\"pool\"", StringComparison.Ordinal)));
                    Assert.That(TimelineField(line, "overlapMs"), Is.Not.EqualTo("null"), "the overlap is measured (the attempt's run stamps present)");
                    if (double.Parse(TimelineField(line, "overlapMs"), System.Globalization.CultureInfo.InvariantCulture) > 0.0) overlapping++;
                    Assert.That(TimelineField(line, "error"), Is.EqualTo("\"\""));
                }
                Assert.That(overlapping, Is.GreaterThanOrEqualTo(1), "some attempt's run (the ms-long ones) overlapped a competitor's; a short attempt may end before the replacement begins, and its 0 is a finding, not an error");
                string[] l2Lines = File.ReadAllLines(Path.Combine(folder, "pool-L2", "replay.jsonl"));
                foreach (string line in l2Lines)
                {
                    string attempt = TimelineField(line, "attempt"), condition = TimelineField(line, "condition");
                    string l0Line = Array.Find(poolLines, l => TimelineField(l, "attempt") == attempt && TimelineField(l, "condition") == condition);
                    foreach (string name in compared) Assert.That(TimelineField(line, name), Is.EqualTo(TimelineField(l0Line, name)), name + ": the same result under load (L2) as at L0");
                }
                string[] competitors = File.ReadAllLines(Path.Combine(folder, "pool-L2", "competitors.jsonl"));
                Assert.That(competitors.Length, Is.GreaterThanOrEqualTo(2 * 2 * issued), "two competitors at least per attempt and condition");
                Assert.That(Array.Exists(competitors, l => l.Contains("\"condition\":\"D\"") && l.Contains("\"warmup\":false")), Is.True);
                Assert.That(Array.Exists(competitors, l => l.Contains("\"condition\":\"C\"") && l.Contains("\"warmup\":false")), Is.True);
                Assert.That(Array.Exists(competitors, l => l.Contains("\"warmup\":true")), Is.True, "the warm-up's competitors are marked");
                foreach (string line in competitors) Assert.That(TimelineField(line, "collected"), Is.EqualTo("true"), "every competitor was collected (the drains)");
                // the timeline off: the pool's stamps and intervals are null (not 0), the summary says off; the results are the same lines
                try
                {
                    CutSpatialSplit.TimelineObserve = false;
                    DispatchObservation.Enabled = false;
                    string offSummary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "pool-off"), l0, out _, out _);
                    StringAssert.Contains("timeline off", offSummary);
                    string[] offLines = File.ReadAllLines(Path.Combine(folder, "pool-off", "replay.jsonl"));
                    Assert.That(offLines.Length, Is.EqualTo(2 * issued));
                    foreach (string line in offLines)
                    {
                        foreach (string name in new[] { "t0Ms", "t1Ms", "t4Ms", "w1Ms", "w2Ms", "w3Ms", "blocked", "competitorsDuring", "overlapMs", "workerBusyDeltaMs" }) Assert.That(TimelineField(line, name), Is.EqualTo("null"), name + " is null with the observation off (never a measured 0)");
                        StringAssert.Contains("\"observed\":{\"dispatch\":false,\"timeline\":false}", line);
                        foreach (string name in new[] { "t2Ms", "t3Ms", "xMs" }) Assert.That(TimelineField(line, name), Is.Not.EqualTo("null"), name + " is the existing clock's and stands");
                        string attempt = TimelineField(line, "attempt"), condition = TimelineField(line, "condition");
                        string onLine = Array.Find(poolLines, l => TimelineField(l, "attempt") == attempt && TimelineField(l, "condition") == condition);
                        foreach (string name in compared) Assert.That(TimelineField(line, name), Is.EqualTo(TimelineField(onLine, name)), name + ": the same result with the timeline off");
                    }
                }
                finally
                {
                    CutSpatialSplit.TimelineObserve = true;
                    DispatchObservation.Enabled = true;
                }
            }
            finally
            {
                split.Capture = null;
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A pool attempt that does not come back holds its input and ends the session** (GRAPH-CONNECT-14, TL P1): with a gate before every
        /// attempt's Begin and short timeouts (3 ticks, a 100 ms stop), the pool replay of a captured session throws the stop exception
        /// (the work not collected, the workers not stopped), writes no line for the sample, and the sample's input stays held (the held
        /// count grows by one; nothing is freed while a worker may read it). The gate is opened afterwards so the worker ends.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitTimeline_APoolAttemptThatDoesNotComeBack_HoldsItsInput_AndEndsTheSession()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOn();
            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-split-pool-hold-" + Guid.NewGuid().ToString("N"));
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode hold case", null);
            split.Capture = sink;
            var gate = new System.Threading.ManualResetEventSlim(false);
            try
            {
                LogicalFragmentId withHand = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, withHand, sides);
                remaining[0] = 1.0;
                yield return Until(() => split.Published >= 1 && split.IsIdle && !split.HasAttempt, "published and idle");
                remaining[0] = 0.0;
                Assert.That(sink.Close(20.0), Is.True);
                split.Capture = null;
                int heldBefore = SpatialSplitReplay.ResourcesHeld;
                SpatialSplitReplay.TestPoolBeginGate = gate;
                SpatialSplitReplay.TestPoolAttemptTicks = 3;
                SpatialSplitReplay.TestPoolStopMilliseconds = 100;
                var opener = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(1500); gate.Set(); });
                opener.Start();
                SpatialSplitReplayPoolStopException stop = null;
                try
                {
                    SpatialSplitReplay.RunSession(session, Path.Combine(folder, "pool-hold"), new SpatialSplitReplayOptions { spanInput = true, pool = true, competitorLevel = 0, competitorWork = 1000 }, out _, out _);
                }
                catch (SpatialSplitReplayPoolStopException e) { stop = e; }
                Assert.That(stop, Is.Not.Null, "the session ended with the stop exception, not an error line");
                Assert.That(stop.WorkCollected, Is.False, "the attempt was still at the gate: not collected");
                Assert.That(stop.WorkersStopped, Is.False, "the workers did not confirm within 100 ms");
                Assert.That(SpatialSplitReplay.ResourcesHeld, Is.EqualTo(heldBefore + 1), "the sample's input and scratch are held, not freed");
                Assert.That(File.Exists(Path.Combine(folder, "pool-hold", "replay.jsonl")), Is.False, "the session ended in its warm-up, before any line was written: no result file pretends otherwise");
                opener.Join();
            }
            finally
            {
                SpatialSplitReplay.TestPoolBeginGate = null;
                SpatialSplitReplay.TestPoolAttemptTicks = SpatialSplitReplayPool.DefaultAttemptTicks;
                SpatialSplitReplay.TestPoolStopMilliseconds = SpatialSplitReplayPool.DefaultStopMilliseconds;
                gate.Set();
                split.Capture = null;
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            yield return EndWorld(root);
        }
    }
}
