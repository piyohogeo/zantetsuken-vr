using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The coexistence check's required re-cut during a drop (Sandbox/HullMidDropSection, TL 2026-10-01), on an
    /// always-kinematic building with the cut limit on: run to its end it passes, both hits followed by their Slash ids,
    /// the first drop stopped part way, its frames recorded from its first; and a required section never started, with no
    /// target, run out of time, ended part way or overlapping the external replay is a failure that gives the check's
    /// ending a non-zero code. The two cuts count towards n like any other.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private sealed class SectionJudgement
        {
            public int failures;
            public readonly List<string> lines = new List<string>();
            public void Expect(bool held, string what) { if (!held) failures++; lines.Add((held ? "ok: " : "FAILED: ") + what); }
            public void Log(string line) => lines.Add(line);
            public int Code => CheckEnding.CodeOf(failures, false);
            public void Write(string what) { TestContext.Out.WriteLine(what + ":"); foreach (string l in lines) TestContext.Out.WriteLine("  " + l); }
        }

        private static IEnumerator RunSection(HullMidDropSection section, CutWorldRoot root, SlashHitDetector detector, int building, System.Func<string> external, List<int> frames, SectionJudgement j,
            System.Func<double> clock = null)
        {
            long next = 900000;
            return section.Run(root, detector, building, () => ++next, external, f => frames.Add(f), null, null, op => false, j.Log, clock ?? (() => CutPhysicsStep.Clock.PhysicsSeconds));
        }

        // The drops' clock, advanced once a physics step and only by the step (10 ms a step): the same value in every
        // frame between two steps. The drops are placed with the steps (2026-10-07), so this is the time they are placed at.
        private static System.Func<double> StepClock(BuildingHullFusion h)
        {
            long from = CutPhysicsStep.Clock.StepId;
            System.Func<double> clock = () => (CutPhysicsStep.Clock.StepId - from) * 0.01;
            h.RealSecondsForTest = clock;
            return clock;
        }

        /// <summary>**Run to its end: passed, both hits followed by their Slash ids, the first drop stopped part way, its frames recorded, n counted on; the code 0.**</summary>
        [UnityTest]
        public IEnumerator HullMidDropSection_RunToItsEnd_Passes_TheCutsCount_AndTheCodeIsZero()
        {
            CutWorldRoot root = NewLimitWorld(16);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            System.Func<double> clock = StepClock(h);
            var section = new HullMidDropSection(true, "test");
            var j = new SectionJudgement();
            var frames = new List<int>();
            int firstFrame = Time.frameCount;
            yield return RunSection(section, root, detector, building.Building, () => null, frames, j, clock);
            section.Judge(j.Expect, j.Log);
            j.Write("run to its end");
            Assert.That(section.Outcome, Is.EqualTo(HullMidDropSection.Result.Passed), section.Describe());
            Assert.That(section.Started && section.Completed, Is.True);
            Assert.That(section.FirstHit.slashId == section.FirstSlash && section.SecondHit.slashId == section.SecondSlash, Is.True, "each hit found by its Slash id");
            Assert.That(section.FirstHit.outcome == "Published" && section.SecondHit.outcome == "Published", Is.True);
            Assert.That(section.FirstDropRunningAtSecond, Is.True);
            Assert.That(section.FirstProgress, Is.GreaterThan(0.0).And.LessThan(1.0), "the first drop seen moved part way before the second Slash");
            Assert.That(section.Events.Count, Is.GreaterThanOrEqualTo(5), "publication, the first move, the second's acceptance, its preparation and the stop recorded");
            Assert.That(section.StopFraction, Is.GreaterThan(0.0).And.LessThan(1.0), "the first drop stopped part way");
            Assert.That(section.FramesNotOneEach, Is.Zero);
            Assert.That(frames.Count, Is.EqualTo(section.Frames).And.GreaterThan(1), "each frame of the section recorded once");
            Assert.That(frames[0], Is.EqualTo(firstFrame), "from its first frame");
            Assert.That(section.GeometriesAtStart, Is.EqualTo(1));
            Assert.That(section.SecondHit.geometriesBefore, Is.EqualTo(section.FirstHit.geometriesAfter), "the second went on from the first's settled n");
            Assert.That(h.CountDisplayGeometries(building.Building), Is.EqualTo(section.GeometriesAtEnd).And.GreaterThan(2), "the two cuts count: nothing reset");
            Assert.That(h.IsCutStopped(building.Building), Is.False, "below N");
            Assert.That(j.failures, Is.Zero);
            Assert.That(j.Code, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The display not moving (its clock held): no second Slash is given, and the section runs out of time, a failure
        /// with a non-zero code.** The first cut is published and its drop registered, but nothing is placed away from where
        /// it stood while the clock does not move.
        /// </summary>
        [UnityTest]
        public IEnumerator HullMidDropSection_TheDisplayNotMoving_GivesNoSecondSlash_AndFails()
        {
            CutWorldRoot root = NewLimitWorld(16);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.RealSecondsForTest = () => 0.0;   // held: a drop registered never moves
            var section = new HullMidDropSection(true, "test") { publishSeconds = 1f };
            var j = new SectionJudgement();
            yield return RunSection(section, root, detector, building.Building, () => null, new List<int>(), j, () => 0.0);
            section.Judge(j.Expect, j.Log);
            j.Write("the display not moving");
            Assert.That(section.Outcome, Is.EqualTo(HullMidDropSection.Result.TimedOut), section.Describe());
            Assert.That(section.FirstHit != null && section.FirstHit.outcome == "Published", Is.True, "the first cut was published");
            Assert.That(h.AnimationsRunning, Is.EqualTo(1), "its drop registered");
            Assert.That(section.FirstProgress, Is.Not.GreaterThan(0.0), "and never moved");
            Assert.That(section.SecondHit, Is.Null, "no second Slash given");
            Assert.That(h.Hits.Count, Is.EqualTo(1));
            Assert.That(j.failures, Is.EqualTo(1));
            Assert.That(j.Code, Is.Not.Zero);
            StepClock(h);   // moving again: the drop ends, the world settles
            yield return UntilHull(root, () => Quiet(h), 30f, "the first cut ended");
            yield return EndWorld(root);
        }

        /// <summary>**Required but never started: a failure at the end, a non-zero code. Not required: no judgement.**</summary>
        [UnityTest]
        public IEnumerator HullMidDropSection_RequiredButNeverRun_FailsWithANonZeroCode()
        {
            var section = new HullMidDropSection(true, "test");
            var j = new SectionJudgement();
            section.Judge(j.Expect, j.Log);
            j.Write("never run");
            Assert.That(section.Outcome, Is.EqualTo(HullMidDropSection.Result.NotStarted));
            Assert.That(j.failures, Is.EqualTo(1));
            Assert.That(j.Code, Is.Not.Zero);

            var optional = new HullMidDropSection(false, "test");
            var k = new SectionJudgement();
            optional.Judge(k.Expect, k.Log);
            Assert.That(k.failures, Is.Zero, "a section not required is not judged");
            yield return null;
        }

        /// <summary>**No target: no building found, or one that is not there, is a failure with a non-zero code.**</summary>
        [UnityTest]
        public IEnumerator HullMidDropSection_NoTarget_FailsWithANonZeroCode()
        {
            CutWorldRoot root = NewLimitWorld(16);
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            foreach (int building in new[] { -1, 99 })
            {
                var section = new HullMidDropSection(true, "test");
                var j = new SectionJudgement();
                yield return RunSection(section, root, detector, building, () => null, new List<int>(), j);
                section.Judge(j.Expect, j.Log);
                j.Write("building " + building);
                Assert.That(section.Outcome, Is.EqualTo(HullMidDropSection.Result.NoTarget), section.Describe());
                Assert.That(j.failures, Is.EqualTo(1));
                Assert.That(j.Code, Is.Not.Zero);
            }

            Assert.That(root.Hulls.GroupCuts, Is.Zero, "nothing cut");
            yield return EndWorld(root);
        }

        /// <summary>**Run out of time: a failure with a non-zero code, the hits it made followed by the world's own end.**</summary>
        [UnityTest]
        public IEnumerator HullMidDropSection_RunOutOfTime_FailsWithANonZeroCode()
        {
            CutWorldRoot root = NewLimitWorld(16);
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            var section = new HullMidDropSection(true, "test") { publishSeconds = 0f };
            var j = new SectionJudgement();
            yield return RunSection(section, root, detector, building.Building, () => null, new List<int>(), j);
            section.Judge(j.Expect, j.Log);
            j.Write("run out of time");
            Assert.That(section.Outcome, Is.EqualTo(HullMidDropSection.Result.TimedOut), section.Describe());
            Assert.That(j.failures, Is.EqualTo(1));
            Assert.That(j.Code, Is.Not.Zero);
            yield return UntilHull(root, () => Quiet(root.Hulls), 30f, "the cut it asked ended");
            yield return EndWorld(root);
        }

        /// <summary>**Ended part way (started, never ended) or overlapping the external replay: a failure with a non-zero code.**</summary>
        [UnityTest]
        public IEnumerator HullMidDropSection_EndedPartWay_OrOverlappingTheReplay_FailsWithANonZeroCode()
        {
            CutWorldRoot root = NewLimitWorld(16);
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;

            // Started and left part way: two steps of it, then never again.
            var partWay = new HullMidDropSection(true, "test");
            var j = new SectionJudgement();
            IEnumerator run = RunSection(partWay, root, detector, building.Building, () => null, new List<int>(), j);
            run.MoveNext();
            yield return null;
            run.MoveNext();
            partWay.Judge(j.Expect, j.Log);
            j.Write("ended part way");
            Assert.That(partWay.Started && !partWay.Completed && partWay.Outcome == HullMidDropSection.Result.Running, Is.True, partWay.Describe());
            Assert.That(j.failures, Is.EqualTo(1));
            Assert.That(j.lines.Exists(l => l.Contains("Interrupted")), Is.True, "named as interrupted");
            Assert.That(j.Code, Is.Not.Zero);
            yield return UntilHull(root, () => Quiet(root.Hulls), 30f, "what it asked ended");

            // The external replay seen during the section.
            var overlap = new HullMidDropSection(true, "test");
            var k = new SectionJudgement();
            yield return RunSection(overlap, root, detector, building.Building, () => "the head moved (test)", new List<int>(), k);
            overlap.Judge(k.Expect, k.Log);
            k.Write("overlapping the replay");
            Assert.That(overlap.Outcome, Is.EqualTo(HullMidDropSection.Result.ExternalOverlap), overlap.Describe());
            Assert.That(k.failures, Is.EqualTo(1));
            Assert.That(k.Code, Is.Not.Zero);
            yield return EndWorld(root);
        }
    }
}
