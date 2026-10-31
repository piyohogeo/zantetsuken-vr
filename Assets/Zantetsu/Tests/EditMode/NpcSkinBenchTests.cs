using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>The NPC skin benchmark's settings (DESIGN 3.4, npc-skin-bench): the launch's arguments read, defaults kept, bad values refused.</summary>
    public sealed class NpcSkinBenchSettingsTests
    {
        private static bool Read(out NpcSkinBenchSettings settings, out string failure, params string[] arguments)
        {
            var all = new List<string> { "Player.exe" };
            all.AddRange(arguments);
            return NpcSkinBenchSettings.TryRead(all.ToArray(), out settings, out failure);
        }

        [Test]
        public void NotAskedFor_IsFalse_WithoutAFailure()
        {
            Assert.That(Read(out NpcSkinBenchSettings settings, out string failure, "-zantetsuScenario", "play"), Is.False);
            Assert.That(settings, Is.Null);
            Assert.That(failure, Is.Null);
            Assert.That(NpcSkinBenchSettings.TryRead(null, out _, out _), Is.False);
        }

        [Test]
        public void TheDirectoryAlone_GivesTheDefaults()
        {
            Assert.That(Read(out NpcSkinBenchSettings s, out string failure, NpcSkinBenchSettings.Argument, @"C:\runs\b1"), Is.True, failure);
            Assert.That(s.directory, Is.EqualTo(@"C:\runs\b1"));
            Assert.That(s.count, Is.EqualTo(64));
            Assert.That(s.pose, Is.EqualTo(NpcSkinBenchPose.Update));
            Assert.That(s.shadows, Is.EqualTo(NpcSkinBenchShadows.Cascade4));
            Assert.That(s.families, Is.EqualTo(8));
            Assert.That(s.warmupSeconds, Is.EqualTo(10f));
            Assert.That(s.measureSeconds, Is.EqualTo(20f));
            Assert.That(s.stepSeconds, Is.EqualTo(1.0 / 72.0).Within(1e-12));
            Assert.That(s.profilerFile, Is.Null);
            Assert.That(s.keepOpen, Is.False);
            Assert.That(s.picture, Is.True);
            Assert.That(s.measureFrames, Is.Zero, "by seconds unless frames are asked");
            Assert.That(s.settleFrames, Is.EqualTo(30));
            Assert.That(s.toleranceMetres, Is.EqualTo(0.01f));
            Assert.That(s.toleranceDegrees, Is.EqualTo(1f));
            Assert.That(s.fixedView, Is.False);
            Assert.That(s.paletteJoinGap, Is.Zero);
            Assert.That(s.updateCount, Is.EqualTo(-1), "every individual by default");
            Assert.That(s.Describe(), Does.Contain("update count all"));
            Assert.That(s.captureFrames, Is.Zero);
            Assert.That(s.Validate(), Is.Null);
            Assert.That(s.Describe(), Does.Contain("count 64").And.Contain("pose update").And.Contain("shadows cascade4").And.Contain("warmup 10 s").And.Contain("measure 20 s"));
        }

        [Test]
        public void EveryValue_IsRead()
        {
            Assert.That(Read(out NpcSkinBenchSettings s, out string failure, NpcSkinBenchSettings.Argument, "d", NpcSkinBenchSettings.CountArgument, "256",
                NpcSkinBenchSettings.PoseArgument, "stop", NpcSkinBenchSettings.ShadowsArgument, "off", NpcSkinBenchSettings.FamiliesArgument, "3",
                NpcSkinBenchSettings.WarmupArgument, "2.5", NpcSkinBenchSettings.MeasureArgument, "7", NpcSkinBenchSettings.StepArgument, "0.0125",
                NpcSkinBenchSettings.ProfilerArgument, "p.raw", NpcSkinBenchSettings.KeepOpenArgument, NpcSkinBenchSettings.NoPictureArgument,
                NpcSkinBenchSettings.MeasureFramesArgument, "900", NpcSkinBenchSettings.SettleFramesArgument, "45", NpcSkinBenchSettings.ToleranceMetresArgument, "0.02",
                NpcSkinBenchSettings.ToleranceDegreesArgument, "1.5", NpcSkinBenchSettings.FixedViewArgument, NpcSkinBenchSettings.PaletteJoinGapArgument, "4096", NpcSkinBenchSettings.CaptureArgument, "3",
                NpcSkinBenchSettings.UpdateCountArgument, "8"), Is.True, failure);
            Assert.That(s.measureFrames, Is.EqualTo(900));
            Assert.That(s.settleFrames, Is.EqualTo(45));
            Assert.That(s.toleranceMetres, Is.EqualTo(0.02f));
            Assert.That(s.toleranceDegrees, Is.EqualTo(1.5f));
            Assert.That(s.fixedView, Is.True);
            Assert.That(s.paletteJoinGap, Is.EqualTo(4096));
            Assert.That(s.captureFrames, Is.EqualTo(3));
            Assert.That(s.updateCount, Is.EqualTo(8));
            Assert.That(s.Describe(), Does.Contain("measure 900 frames").And.Contain("FIXED VIEW").And.Contain("palette join gap 4096").And.Contain("GPU CAPTURE 3 frames").And.Contain("update count 8"));
            Assert.That(Read(out NpcSkinBenchSettings u0, out _, NpcSkinBenchSettings.Argument, "d", NpcSkinBenchSettings.UpdateCountArgument, "0") && u0.updateCount == 0, Is.True, "0: no individual posed");
            Assert.That(Read(out NpcSkinBenchSettings uAll, out _, NpcSkinBenchSettings.Argument, "d", NpcSkinBenchSettings.UpdateCountArgument, "-1") && uAll.updateCount == -1, Is.True, "-1 is a value, not the next argument");
            Assert.That(Read(out _, out string tooMany, NpcSkinBenchSettings.Argument, "d", NpcSkinBenchSettings.UpdateCountArgument, "513"), Is.False); Assert.That(tooMany, Is.Not.Null);
            Assert.That(Read(out NpcSkinBenchSettings c1, out _, NpcSkinBenchSettings.Argument, "d", NpcSkinBenchSettings.ShadowsArgument, "cascade1") && c1.shadows == NpcSkinBenchShadows.Cascade1, Is.True);
            Assert.That(NpcSkinBenchSettings.Name(NpcSkinBenchShadows.Cascade1), Is.EqualTo("cascade1"));
            Assert.That(NpcSkinBenchSettings.CascadesOf(NpcSkinBenchShadows.Off), Is.Zero);
            Assert.That(NpcSkinBenchSettings.CascadesOf(NpcSkinBenchShadows.Cascade1), Is.EqualTo(1));
            Assert.That(NpcSkinBenchSettings.CascadesOf(NpcSkinBenchShadows.Cascade4), Is.EqualTo(4));
            Assert.That(s.count, Is.EqualTo(256));
            Assert.That(s.pose, Is.EqualTo(NpcSkinBenchPose.Stop));
            Assert.That(s.shadows, Is.EqualTo(NpcSkinBenchShadows.Off));
            Assert.That(s.families, Is.EqualTo(3));
            Assert.That(s.warmupSeconds, Is.EqualTo(2.5f));
            Assert.That(s.measureSeconds, Is.EqualTo(7f));
            Assert.That(s.stepSeconds, Is.EqualTo(0.0125));
            Assert.That(s.profilerFile, Is.EqualTo("p.raw"));
            Assert.That(s.keepOpen, Is.True);
            Assert.That(s.picture, Is.False);
            Assert.That(s.Describe(), Does.Contain("PROFILER DIAGNOSIS"));
        }

        [TestCase(new[] { "-zantetsuNpcSkinBench" }, "needs a directory")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "-zantetsuNpcSkinBenchCount", "8" }, "needs a directory")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchCount", "0" }, "1..512")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchCount", "513" }, "1..512")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchCount", "many" }, "1..512")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchPose", "walk" }, "stop or update")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchPose" }, "stop or update")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchShadows", "cascade2" }, "off, cascade1 or cascade4")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchMeasureFrames", "-1" }, "0..1000000")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchPoseToleranceDegrees", "181" }, "0..180")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchPaletteJoinGap", "x" }, "0..1000000")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchMeasureSeconds", "0" }, "0.001..3600")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchWarmupSeconds", "-1" }, "0..3600")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchStepSeconds", "2" }, "0.0001..1")]
        [TestCase(new[] { "-zantetsuNpcSkinBench", "d", "-zantetsuNpcSkinBenchFamilies", "65" }, "1..64")]
        public void ABadValue_IsRefused(string[] arguments, string failureText)
        {
            Assert.That(Read(out NpcSkinBenchSettings settings, out string failure, arguments), Is.False);
            Assert.That(settings, Is.Null);
            Assert.That(failure, Does.Contain(failureText));
        }

        [Test]
        public void TheCountLimit_IsTheCrowdsRoom()
        {
            Assert.That(NpcSkinBenchSettings.CountLimit, Is.EqualTo(512), "the world's skin instance room");
            foreach (int count in new[] { 16, 64, 128, 256 })
                Assert.That(Read(out NpcSkinBenchSettings s, out _, NpcSkinBenchSettings.Argument, "d", NpcSkinBenchSettings.CountArgument, count.ToString()) && s.count == count, Is.True);
        }

        [Test]
        public void TheBenchmark_IsAScenario_WithItsOwnArgument()
        {
            Assert.That(SandboxScenario.TryParse("npc-skin-bench", out SandboxScenarioKind kind) && kind == SandboxScenarioKind.NpcSkinBench, Is.True);
            Assert.That(SandboxScenario.Name(SandboxScenarioKind.NpcSkinBench), Is.EqualTo("npc-skin-bench"));
            Assert.That(SandboxScenario.ScenesFor(SandboxScenarioKind.NpcSkinBench), Is.EqualTo(new[] { "NpcSkinBench", "QuestNpcSkinBench" }), "its own scenes alone: no city scene is a fallback");
            string[] With(params string[] a) => new[] { "Player.exe" }.Concat(a).ToArray();
            SandboxScenarioChoice choice = SandboxScenario.Read(With("-zantetsuScenario", "npc-skin-bench", NpcSkinBenchSettings.Argument, "d", "-zantetsuSkinDraw"));
            Assert.That(choice.Kind, Is.EqualTo(SandboxScenarioKind.NpcSkinBench));
            Assert.That(choice.Failure, Is.Null);
            Assert.That(SandboxScenario.Read(With("-zantetsuScenario", "npc-skin-bench")).Failure, Does.Contain("needs " + NpcSkinBenchSettings.Argument));
            Assert.That(SandboxScenario.Read(With("-zantetsuScenario", "npc-skin-bench", NpcSkinBenchSettings.Argument, "d", SandboxPropSlashPlayerCheck.Argument, "e")).Failure, Does.Contain("does not run " + SandboxPropSlashPlayerCheck.Argument));
            Assert.That(SandboxScenario.Read(With("-zantetsuScenario", "npc-skin-bench", NpcSkinBenchSettings.Argument, "d", SandboxDrawComparePlayerCheck.Argument, "e")).Failure, Does.Contain("does not run " + SandboxDrawComparePlayerCheck.Argument));
            Assert.That(SandboxScenario.Read(With(NpcSkinBenchSettings.Argument, "d")).Failure, Does.Contain("names no scenario").And.Contain(NpcSkinBenchSettings.Argument), "the benchmark's argument without its scenario is reported, not run");
            Assert.That(SandboxScenario.Read(With("-zantetsuScenario", "city-walk", SandboxPropSlashPlayerCheck.Argument, "d", SandboxPropSlashPlayerCheck.CityWalkArgument, NpcSkinBenchSettings.Argument, "e")).Failure, Does.Contain("does not run " + NpcSkinBenchSettings.Argument));
        }
    }

    /// <summary>The benchmark's plan: deterministic families, rows inside the view, phases spread over the clip, the step's time.</summary>
    public sealed class NpcSkinBenchPlanTests
    {
        [Test]
        public void TheFamilies_AreTheFirstByName_Distinct()
        {
            string[] chosen = NpcSkinBenchPlan.ChooseFamilies(new[] { "casual-m_2", "casual-f_1", "professional-c_1", "casual-f_1", "", null, "casual-m_1" }, 3);
            Assert.That(chosen, Is.EqualTo(new[] { "casual-f_1", "casual-m_1", "casual-m_2" }));
            Assert.That(NpcSkinBenchPlan.ChooseFamilies(new[] { "b", "a" }, 8), Is.EqualTo(new[] { "a", "b" }), "fewer available than asked: the available ones");
            Assert.That(NpcSkinBenchPlan.ChooseFamilies(new string[0], 8), Is.Empty);
        }

        [Test]
        public void ThePlacement_IsDeterministic_RoundRobin_AndFillsRowsFrontToBack()
        {
            NpcSkinBenchPlan.Placement[] a = NpcSkinBenchPlan.Place(64, 8, 2.0);
            NpcSkinBenchPlan.Placement[] b = NpcSkinBenchPlan.Place(64, 8, 2.0);
            Assert.That(a.Length, Is.EqualTo(64));
            for (int i = 0; i < a.Length; i++)
            {
                Assert.That(a[i].index, Is.EqualTo(i));
                Assert.That(a[i].family, Is.EqualTo(i % 8));
                Assert.That(a[i].position, Is.EqualTo(b[i].position), "the same count places the same");
                Assert.That(a[i].phase, Is.EqualTo(b[i].phase));
                Assert.That(a[i].yaw, Is.EqualTo(180f), "facing the eye");
                Assert.That(a[i].position.z, Is.GreaterThan(0f), "in front of the eye");
                Assert.That(a[i].phase, Is.GreaterThanOrEqualTo(0.0).And.LessThan(2.0));
                if (i > 0) Assert.That(a[i].row, Is.GreaterThanOrEqualTo(a[i - 1].row), "rows fill front to back");
            }

            Assert.That(a.Select(p => p.row).Distinct().Count(), Is.GreaterThan(1), "more than one row for 64");
            // Within a row the individuals are one spacing apart and centred.
            foreach (IGrouping<int, NpcSkinBenchPlan.Placement> row in a.GroupBy(p => p.row))
            {
                float[] xs = row.Select(p => p.position.x).OrderBy(x => x).ToArray();
                for (int i = 1; i < xs.Length; i++) Assert.That(xs[i] - xs[i - 1], Is.EqualTo(NpcSkinBenchPlan.DefaultSpacing).Within(1e-4));
                Assert.That(xs.Average(), Is.EqualTo(0f).Within(1e-4), "centred");
                Assert.That(row.Select(p => p.position.z).Distinct().Count(), Is.EqualTo(1), "one distance a row");
            }

            // The first 16 of 64 are the first 16 of a smaller count too (the plan is a prefix).
            NpcSkinBenchPlan.Placement[] small = NpcSkinBenchPlan.Place(16, 8, 2.0);
            for (int i = 0; i < 16; i++) Assert.That(small[i].family, Is.EqualTo(a[i].family));
        }

        [TestCase(16)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(512)]
        public void EveryCount_StandsInsideTheView(int count)
        {
            NpcSkinBenchPlan.Placement[] plan = NpcSkinBenchPlan.Place(count, 8, 1.5);
            Assert.That(NpcSkinBenchPlan.AllInView(plan, NpcSkinBenchPlan.DefaultHorizontalFovDegrees, 60f, NpcSkinBenchPlan.EyeHeight, 1.9f, 2f, out string outside), Is.True, outside);
            Assert.That(NpcSkinBenchPlan.FarthestDistance(plan), Is.LessThan(50f), "within the shadow distance (50 m)");
        }

        [Test]
        public void TheViewRule_RefusesWhatStandsOutside()
        {
            var outsideHorizontally = new[] { new NpcSkinBenchPlan.Placement(0, 0, 0, new Vector3(10f, 0f, 4f), 180f, 0.0) };
            Assert.That(NpcSkinBenchPlan.AllInView(outsideHorizontally, 70f, 60f, NpcSkinBenchPlan.EyeHeight, 1.9f, 2f, out string why), Is.False);
            Assert.That(why, Does.Contain("off the axis"));
            var behind = new[] { new NpcSkinBenchPlan.Placement(0, 0, 0, new Vector3(0f, 0f, -1f), 180f, 0.0) };
            Assert.That(NpcSkinBenchPlan.AllInView(behind, 70f, 60f, NpcSkinBenchPlan.EyeHeight, 1.9f, 2f, out why), Is.False);
            Assert.That(why, Does.Contain("not in front"));
            var tooClose = new[] { new NpcSkinBenchPlan.Placement(0, 0, 0, new Vector3(0f, 0f, 0.5f), 180f, 0.0) };
            Assert.That(NpcSkinBenchPlan.AllInView(tooClose, 70f, 60f, NpcSkinBenchPlan.EyeHeight, 1.9f, 2f, out why), Is.False, "feet far below the view");
        }

        [Test]
        public void ThePhases_SpreadOverTheClip_AndThePoseTime_IsTheSteps()
        {
            double[] phases = Enumerable.Range(0, 64).Select(i => NpcSkinBenchPlan.PhaseOf(i, 3.0)).ToArray();
            Assert.That(phases[0], Is.EqualTo(0.0));
            Assert.That(phases.All(p => p >= 0.0 && p < 3.0), Is.True);
            Assert.That(phases.Distinct().Count(), Is.EqualTo(64), "no two alike");
            Assert.That(phases.Count(p => p < 1.5), Is.InRange(28, 36), "about half below the middle: spread, not bunched");
            Assert.That(NpcSkinBenchPlan.PhaseOf(5, 0.0), Is.EqualTo(0.0), "a clip of no length has one phase");
            Assert.That(NpcSkinBenchPlan.PoseTime(0, 1.0 / 72.0, 0.25), Is.EqualTo(0.25));
            Assert.That(NpcSkinBenchPlan.PoseTime(72, 1.0 / 72.0, 0.25), Is.EqualTo(1.25).Within(1e-12));
            Assert.That(NpcSkinBenchPlan.RowCapacity(4f, 70f, 0.9f), Is.EqualTo(6), "4 m: 2 x 4 tan(33 deg) / 0.9 + 1, the 2 deg margin kept");
            Assert.That(NpcSkinBenchPlan.RowCapacity(4f, 70f, 0.9f, 0f), Is.EqualTo(7), "without the margin one more");
            Assert.That(NpcSkinBenchPlan.RowCapacity(0f, 70f, 0.9f), Is.EqualTo(1));
        }
    }

    /// <summary>The benchmark's statistics: valid samples alone, no 0 for an absent value; and its files' formatting.</summary>
    public sealed class NpcSkinBenchStatisticsTests
    {
        [Test]
        public void AbsentAndInvalidValues_AreLeftOut_NeverCountedAsZero()
        {
            NpcSkinBenchStatistics s = NpcSkinBenchStatistics.Of(new[] { 3.0, double.NaN, 1.0, 0.0, -2.0, double.PositiveInfinity, 2.0, 4.0 });
            Assert.That(s.count, Is.EqualTo(4));
            Assert.That(s.median, Is.EqualTo(2.5));
            Assert.That(s.least, Is.EqualTo(1.0));
            Assert.That(s.most, Is.EqualTo(4.0));
            Assert.That(s.mean, Is.EqualTo(2.5));
            Assert.That(s.p95, Is.EqualTo(4.0));
            NpcSkinBenchStatistics none = NpcSkinBenchStatistics.Of(new[] { 0.0, double.NaN });
            Assert.That(none.Any, Is.False);
            Assert.That(none.count, Is.Zero);
            Assert.That(double.IsNaN(none.median), Is.True, "no value, not 0");
            Assert.That(none.Json(), Is.EqualTo("{\"count\":0,\"median\":null,\"p95\":null,\"mean\":null,\"min\":null,\"max\":null}"));
            Assert.That(none.Describe(), Is.EqualTo("no valid sample"));
        }

        [Test]
        public void TheMedianAndThe95thPercentile_AreTheSortedValues()
        {
            double[] values = Enumerable.Range(1, 100).Select(i => (double)i).Reverse().ToArray();
            NpcSkinBenchStatistics s = NpcSkinBenchStatistics.Of(values);
            Assert.That(s.median, Is.EqualTo(50.5));
            Assert.That(s.p95, Is.EqualTo(95.0), "nearest rank: the 95th of 100");
            NpcSkinBenchStatistics odd = NpcSkinBenchStatistics.Of(new[] { 5.0, 1.0, 3.0 });
            Assert.That(odd.median, Is.EqualTo(3.0));
            Assert.That(odd.p95, Is.EqualTo(5.0));
            Assert.That(NpcSkinBenchStatistics.Of(new[] { 7.0 }).p95, Is.EqualTo(7.0));
            Assert.That(s.Describe(), Does.StartWith("n 100, median 50.500 ms, p95 95.000 ms"));
        }

        [Test]
        public void ARow_WritesAbsentValuesAsEmptyCells_AndTheColumnsReadOnlyThePresent()
        {
            var row = new NpcSkinBenchRow { frame = 10, phase = 1, step = 3, hasMain = true, ftMainMs = 2.5f, hasGpu = false, ftGpuMs = 0f, playerLoopMs = double.NaN, gfxPresentWaitMs = 1.25, visibleRenderers = -1, gcAllocatedBytes = 1552 };
            string csv = NpcSkinBenchReport.Csv(in row);
            string[] cells = csv.Split(',');
            string[] header = NpcSkinBenchReport.Header.Split(',');
            Assert.That(cells.Length, Is.EqualTo(header.Length), "as many cells as header columns");
            Assert.That(cells[Array.IndexOf(header, "ftMainMs")], Is.EqualTo("2.5"));
            Assert.That(cells[Array.IndexOf(header, "ftGpuMs")], Is.EqualTo(""), "an absent GPU time is an empty cell, never 0");
            Assert.That(cells[Array.IndexOf(header, "playerLoopMs")], Is.EqualTo(""));
            Assert.That(cells[Array.IndexOf(header, "gfxPresentWaitMs")], Is.EqualTo("1.25"));
            Assert.That(cells[Array.IndexOf(header, "visibleRenderers")], Is.EqualTo("-1"));
            var rows = new List<NpcSkinBenchRow>
            {
                new NpcSkinBenchRow { phase = 0, hasGpu = true, ftGpuMs = 9f },
                new NpcSkinBenchRow { phase = 1, hasGpu = true, ftGpuMs = 3f },
                new NpcSkinBenchRow { phase = 1, hasGpu = false, ftGpuMs = 0f },
                new NpcSkinBenchRow { phase = 1, hasGpu = true, ftGpuMs = 5f },
            };
            Assert.That(NpcSkinBenchReport.Column(rows, r => (r.hasGpu, r.ftGpuMs)).ToArray(), Is.EqualTo(new[] { 3.0, 5.0 }), "the measured rows' present values alone");
            Assert.That(NpcSkinBenchReport.Quote("a \"b\"\n"), Is.EqualTo("\"a \\\"b\\\"\\n\""));
            Assert.That(NpcSkinBenchReport.Quote(null), Is.EqualTo("null"));
            Assert.That(NpcSkinBenchReport.Number(double.NaN), Is.EqualTo("null"));
            Assert.That(NpcSkinBenchReport.Object(new[] { new KeyValuePair<string, string>("a", "1"), new KeyValuePair<string, string>("b", "\"x\"") }, false), Is.EqualTo("{\"a\": 1,\"b\": \"x\"}"));
            Assert.That(NpcSkinBenchReport.Array(new[] { "1", "null" }), Is.EqualTo("[1,null]"));
        }
    }
}
