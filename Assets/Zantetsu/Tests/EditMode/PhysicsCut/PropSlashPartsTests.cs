using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The Prop Slash run in four parts (TL, 2026-10-07): scenario, metrics, checks, detailed diagnostics, chosen by the
    /// launch arguments and fixed for the run. Here, what can be held without a Player: the arguments' reading and the
    /// launch record's line; the cuts the run's end waits for, followed apart from the checks' records -- only the
    /// unfinished ones looked at, the same end as a pass over every accepted cut gave; and the run's result, which
    /// tells a completed run from passed checks and writes checks that were not run as not run.
    /// The run itself -- the script, the replay, the end and frames.csv with the checks left out -- is a Player's.
    /// </summary>
    public class PropSlashPartsTests
    {
        private static readonly string[] k_cityWalk =
        {
            "Player.exe", "-zantetsuPropSlash", "C:/run", "-zantetsuSlashInput", "input.csv", "-zantetsuPropLight", "-zantetsuMobPlan", "script.txt", "-zantetsuCityWalk",
            "-zantetsuCityWalkBudget", "570",
        };

        private static string[] With(string[] arguments, params string[] more) => arguments.Concat(more).ToArray();

        [Test]
        public void TheParts_AreAllOnByDefault_EachLeftOutByItsArgument_AndTheLaunchRecordSaysWhichRun()
        {
            PropSlashParts all = SandboxPropSlashPlayerCheck.PartsFor(k_cityWalk);
            Assert.That((all.scenario, all.metrics, all.checks, all.detail, all.note), Is.EqualTo((true, true, true, true, (string)null)), "a functional run: every part on, nothing asked");

            // The ordinary measurement: the scenario and the metrics, without the checks and the detailed diagnostics.
            string[] measurement = With(k_cityWalk, "-zantetsuPropChecks", "off", "-zantetsuPropDetail", "OFF", "-zantetsuDisableNumericDiagnostics");
            PropSlashParts m = SandboxPropSlashPlayerCheck.PartsFor(measurement);
            Assert.That((m.scenario, m.metrics, m.checks, m.detail, m.note), Is.EqualTo((true, true, false, false, (string)null)), "the measurement's parts");
            Assert.That(m.Watching, Is.False, "nothing watches the hits, the pieces or the characters");
            Assert.That(m.Describe(), Is.EqualTo("scenario=on metrics=on checks=off detailed diagnostics=off"));

            // Each apart.
            PropSlashParts checksOnly = SandboxPropSlashPlayerCheck.PartsFor(With(k_cityWalk, "-zantetsuPropDetail", "off"));
            Assert.That((checksOnly.checks, checksOnly.detail, checksOnly.Watching), Is.EqualTo((true, false, true)), "the detailed diagnostics left out, the checks run");
            PropSlashParts detailOnly = SandboxPropSlashPlayerCheck.PartsFor(With(k_cityWalk, "-zantetsuPropChecks", "off"));
            Assert.That((detailOnly.checks, detailOnly.detail, detailOnly.Watching), Is.EqualTo((false, true, true)), "the checks left out, the detailed diagnostics run");
            PropSlashParts noMetrics = SandboxPropSlashPlayerCheck.PartsFor(With(k_cityWalk, "-zantetsuPropChecks", "off", "-zantetsuPropDetail", "off", "-zantetsuPropMetrics", "off"));
            Assert.That((noMetrics.scenario, noMetrics.metrics, noMetrics.checks, noMetrics.detail), Is.EqualTo((true, false, false, false)), "the scenario alone");

            // What is not as asked says why.
            PropSlashParts needsMetrics = SandboxPropSlashPlayerCheck.PartsFor(With(k_cityWalk, "-zantetsuPropMetrics", "off"));
            Assert.That(needsMetrics.metrics, Is.True, "the checks read the timeline: the metrics stay");
            Assert.That(needsMetrics.note, Does.Contain("metrics on"));
            string[] box = { "Player.exe", "-zantetsuPropSlash", "C:/run", "-zantetsuSlashInput", "input.csv", "-zantetsuPropChecks", "off", "-zantetsuPropDetail", "off" };
            PropSlashParts elsewhere = SandboxPropSlashPlayerCheck.PartsFor(box);
            Assert.That((elsewhere.checks, elsewhere.detail), Is.EqualTo((true, true)), "outside a scripted city walk the checks and the detailed diagnostics stay on");
            Assert.That(elsewhere.note, Does.Contain("scripted city walk only"));
            PropSlashParts live = SandboxPropSlashPlayerCheck.PartsFor(new[] { "Player.exe", "-zantetsuPropSlash", "C:/run", "-zantetsuMobPlan", "live", "-zantetsuCityWalk", "-zantetsuPropChecks", "off" });
            Assert.That((live.checks, live.detail), Is.EqualTo((true, true)), "a person's walk is not the script's");
            PropSlashParts unreadable = SandboxPropSlashPlayerCheck.PartsFor(With(k_cityWalk, "-zantetsuPropChecks", "maybe"));
            Assert.That(unreadable.checks, Is.True);
            Assert.That(unreadable.note, Does.Contain("neither on nor off"));
            PropSlashParts none = SandboxPropSlashPlayerCheck.PartsFor(new[] { "Player.exe", "-zantetsuPropChecks", "off" });
            Assert.That((none.scenario, none.metrics, none.checks, none.detail), Is.EqualTo((false, false, false, false)), "no Prop Slash run at all");
            Assert.That(none.note, Does.Contain("no -zantetsuPropSlash"));

            // The numeric diagnosis is another switch: neither reads the other's argument.
            Assert.That(SandboxPropSlashPlayerCheck.PartsFor(With(k_cityWalk, "-zantetsuDisableNumericDiagnostics")).Describe(), Is.EqualTo(all.Describe()), "the parts do not read the numeric diagnosis's argument");
            Assert.That(Zantetsu.MeshCut.VpNumericDiagnosis.EnabledFor(With(k_cityWalk, "-zantetsuPropChecks", "off", "-zantetsuPropDetail", "off")), Is.True, "the numeric diagnosis does not read the parts' arguments");
            Assert.That(Zantetsu.MeshCut.VpNumericDiagnosis.EnabledFor(measurement), Is.False);

            string line = SandboxLaunchRecord.Describe(measurement);
            TestContext.Out.WriteLine(line);
            Assert.That(line, Does.Contain("Prop Slash parts: scenario=on metrics=on checks=off detailed diagnostics=off"), "the launch record carries the parts in effect");
            Assert.That(SandboxLaunchRecord.Describe(k_cityWalk), Does.Contain("Prop Slash parts: scenario=on metrics=on checks=on detailed diagnostics=on"));
        }

        [Test]
        public void DetailedPicturesLeftOut_DoNotPrepareACaptureOrHoldTheScenario()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            Type walkType = typeof(SandboxPropSlashPlayerCheck).GetNestedType("Walk", System.Reflection.BindingFlags.NonPublic);
            var owner = new UnityEngine.GameObject("parts-capture-test");
            try
            {
                var walk = owner.AddComponent(walkType);
                walkType.GetField("runParts", flags).SetValue(walk, new PropSlashParts(true, true, false, false, null));
                // No script, directory, camera or world: leaving pictures out must return before touching any of them.
                var pictures = (System.Collections.IEnumerator)walkType.GetMethod("CityWalkWalkShots", flags).Invoke(walk, new object[] { null });
                Assert.That(pictures.MoveNext(), Is.False);
                Assert.That(walkType.GetMethod("CityWalkHoldBeforeSlash", flags).Invoke(walk, new object[] { null }), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        // A cut of the reference: when the world publishes and commits it, and what the run's records say of it.
        private sealed class Cut
        {
            public int id, publishAt, commitAt;
            public bool hasOperation, waitedFor;
            public int publishedFrame = -1, committedFrame = -1;       // as the followed cuts stamp them
            public int oldPublishedFrame = -1, oldCommittedFrame = -1;   // as a pass over every accepted cut stamped them
        }

        [Test]
        public void TheCutsTheEndWaitsFor_AreFollowedOnlyWhileUnfinished_AndTheEndIsWhatAPassOverEveryAcceptedCutGave()
        {
            var random = new System.Random(20261007);
            long visitsInAll = 0, oldVisitsInAll = 0;
            for (int round = 0; round < 40; round++)
            {
                var open = new OpenCuts<Cut>();
                var accepted = new List<Cut>();
                var order = new List<int>();
                int frame = 0;
                OpenCuts<Cut>.Progress Follow(Cut c)
                {
                    order.Add(c.id);
                    var progress = OpenCuts<Cut>.Progress.None;
                    if (c.publishedFrame < 0 && frame >= c.publishAt) c.publishedFrame = frame;
                    if (c.committedFrame < 0 && frame >= c.commitAt)
                    {
                        c.committedFrame = frame;
                        if (c.waitedFor) progress |= OpenCuts<Cut>.Progress.WaitedForCommitted;
                    }

                    if (c.publishedFrame >= 0 && c.committedFrame >= 0) progress |= OpenCuts<Cut>.Progress.Finished;
                    return progress;
                }

                int cuts = random.Next(0, 30), last = 0, endedAt = -1, oldEndedAt = -1;
                for (frame = 0; frame < 400; frame++)
                {
                    // Hits of this frame: cuts with an operation of their own, a few with none (a hull's, a fusion's),
                    // a few waited for with none (never committed: the end is then never reached, by either rule).
                    while (accepted.Count < cuts && random.Next(0, 6) == 0)
                    {
                        int publish = frame + random.Next(0, 20);
                        var c = new Cut { id = accepted.Count, publishAt = publish, commitAt = publish + random.Next(0, 60), hasOperation = random.Next(0, 10) != 0 };
                        c.waitedFor = c.hasOperation ? random.Next(0, 8) != 0 : round % 9 == 8 && random.Next(0, 3) == 0;
                        accepted.Add(c);
                        open.Add(c, c.hasOperation, c.waitedFor);
                        last = Math.Max(last, c.commitAt);
                    }

                    // The pass over every accepted cut, as it was.
                    foreach (Cut c in accepted)
                    {
                        if (!c.hasOperation) continue;
                        oldVisitsInAll++;
                        if (c.oldPublishedFrame < 0 && frame >= c.publishAt) c.oldPublishedFrame = frame;
                        if (c.oldCommittedFrame < 0 && frame >= c.commitAt) c.oldCommittedFrame = frame;
                    }

                    order.Clear();
                    int openBefore = open.Open;
                    long visitsBefore = open.Visits;
                    open.Advance(Follow);
                    Assert.That(open.Visits - visitsBefore, Is.EqualTo(openBefore), "round " + round + " frame " + frame + ": only the cuts still followed are looked at");
                    Assert.That(order, Is.Ordered, "in the order they were accepted");
                    bool oldSettled = accepted.TrueForAll(c => !c.waitedFor || c.oldCommittedFrame >= 0);
                    Assert.That(open.Waiting == 0, Is.EqualTo(oldSettled), "round " + round + " frame " + frame + ": the same answer to whether the cuts are settled");
                    Assert.That(open.Open, Is.EqualTo(accepted.Count(c => c.hasOperation && (c.publishedFrame < 0 || c.committedFrame < 0))), "a finished cut is let go");
                    if (accepted.Count == cuts && oldSettled && oldEndedAt < 0) oldEndedAt = frame;
                    if (accepted.Count == cuts && open.Waiting == 0 && endedAt < 0) endedAt = frame;
                }

                foreach (Cut c in accepted)
                {
                    Assert.That((c.publishedFrame, c.committedFrame), Is.EqualTo((c.oldPublishedFrame, c.oldCommittedFrame)), "round " + round + " cut " + c.id + ": published and committed at the same frames");
                }

                Assert.That(endedAt, Is.EqualTo(oldEndedAt), "round " + round + ": the end is reached at the same frame (or never, alike)");
                Assert.That(open.Added, Is.EqualTo(accepted.Count));
                visitsInAll += open.Visits;
            }

            TestContext.Out.WriteLine("cuts looked at over 40 runs of 400 frames: following the unfinished ones " + visitsInAll + "; a pass over every accepted cut " + oldVisitsInAll);
            Assert.That(visitsInAll, Is.LessThan(oldVisitsInAll / 4), "far fewer looks than a pass over every accepted cut");
        }

        /// <summary>
        /// The timeline's room, made once before the first row: how many rows it is made for, and what it takes. The
        /// city walk's script ends at 480 s and the Simulator's display runs at 72 Hz; the walks so far wrote 28,557
        /// and 29,315 rows (relV, slotT).
        /// </summary>
        [Test]
        public void TheTimelinesRoom_IsMadeForTheScriptsOwnLengthAtTheDisplaysRate_AndItsBytesAreMeasured()
        {
            Assert.That(SandboxPropSlashPlayerCheck.TimelineSecondsFor(480.0, true, true, 0f), Is.EqualTo(530.0), "the script's 480 s, 30 s for its cuts, 20 s for what follows (a light run observes nothing)");
            int rows = SandboxPropSlashPlayerCheck.TimelineRowsFor(480.0, true, true, 0f, 72f);
            Assert.That(rows, Is.EqualTo(40068), "530 s at 72 Hz and a twentieth more");
            Assert.That(rows, Is.GreaterThan(29315 * 5 / 4), "a quarter more than the longest walk so far wrote");
            Assert.That(SandboxPropSlashPlayerCheck.TimelineRowsFor(480.0, true, false, 5f, 72f), Is.EqualTo((int)Math.Ceiling(555.0 * 72.0 * 1.05)), "an observed run with a hold: 20 s and the hold more");
            Assert.That(SandboxPropSlashPlayerCheck.TimelineRowsFor(0.0, false, true, 0f, 90f), Is.EqualTo((int)Math.Ceiling(140.0 * 90.0 * 1.05)), "not a scripted run: 120 s for the replay");
            Assert.That(SandboxPropSlashPlayerCheck.TimelineRowsFor(4000.0, true, true, 0f, 120f), Is.EqualTo(SandboxPropSlashPlayerCheck.TimelineRoomCap), "never past the cap");

            // Observe the process-wide retained-heap difference. Other garbage may be collected across this window;
            // it is not an allocation counter and cannot be a per-row byte gate.
            const int markers = 35, sample = 2000;
            long bytes = SandboxPropSlashPlayerCheck.TimelineRoomBytesForTest(sample, markers);
            double each = bytes / (double)sample;
            TestContext.Out.WriteLine("timeline room: " + sample + " rows with " + markers + " markers; retained managed heap difference " + bytes
                                      + " B in the Editor (" + each.ToString("F0") + " B per row, not bytes allocated)");
        }

        [Test]
        public void TheRunsResult_TellsACompletedRunFromPassedChecks_AndChecksNotRunAreNotRun()
        {
            // The checks run: the code is what it always was.
            Assert.That(PropSlashRunResult.CodeOf(true, 0, true, false), Is.EqualTo(0));
            Assert.That(PropSlashRunResult.CodeOf(true, 3, true, false), Is.EqualTo(14), "a failed expectation");
            Assert.That(PropSlashRunResult.CodeOf(true, 0, true, true), Is.EqualTo(18), "the replay began before ready");
            Assert.That(PropSlashRunResult.CodeOf(true, 1, false, true), Is.EqualTo(14));
            Assert.That(PropSlashRunResult.CodeOf(true, 0, false, false), Is.EqualTo(CheckEnding.CodeOf(0, false)), "with the checks run, the code is the failures' alone, as before");

            // The checks left out: the code speaks of the run alone.
            Assert.That(PropSlashRunResult.CodeOf(false, 0, true, false), Is.EqualTo(0), "completed");
            Assert.That(PropSlashRunResult.CodeOf(false, 0, false, false), Is.EqualTo(14), "not completed: a deadline reached is not lost with the checks");
            Assert.That(PropSlashRunResult.CodeOf(false, 2, true, false), Is.EqualTo(14), "a closing step failed");
            Assert.That(PropSlashRunResult.CodeOf(false, 0, true, true), Is.EqualTo(18));

            var completedUnchecked = new PropSlashRunResult(true, null, false, 0, 0, 7, 0);
            TestContext.Out.WriteLine(completedUnchecked.RunLine + " | " + completedUnchecked.ChecksLine);
            Assert.That(completedUnchecked.RunLine, Does.StartWith("run: completed"));
            Assert.That(completedUnchecked.ChecksLine, Does.StartWith("checks: not run").And.Contain("neither passed nor failed").And.Contain("7 expectations reached and not judged"));
            Assert.That(completedUnchecked.ChecksLine, Does.Not.Contain("passed (").And.Not.Contain("held)"), "never written as passed");

            var cutShort = new PropSlashRunResult(false, "deadline reached: script and the cuts' completion", false, 0, 0, 0, 14);
            Assert.That(cutShort.RunLine, Is.EqualTo("run: NOT completed (deadline reached: script and the cuts' completion)"));
            Assert.That(cutShort.ChecksLine, Does.StartWith("checks: not run"));

            var passed = new PropSlashRunResult(true, null, true, 1120, 0, 0, 0);
            Assert.That(passed.ChecksLine, Is.EqualTo("checks: passed (1120 expectations held)"));
            var failed = new PropSlashRunResult(true, null, true, 1120, 1, 0, 14);
            TestContext.Out.WriteLine(failed.RunLine + " | " + failed.ChecksLine);
            Assert.That(failed.RunLine, Does.StartWith("run: completed"), "the run went through");
            Assert.That(failed.ChecksLine, Is.EqualTo("checks: FAILED (1 failed, 1120 held)"), "and a check failed: two different things");
        }
    }
}
