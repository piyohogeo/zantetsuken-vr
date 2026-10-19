using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.PhysicsCut;
using Zantetsu.Quest;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The shared Main budget (DESIGN 4.4, 2026-10-19): the application's target frame period less one common margin,
    /// less the time since the frame's start -- one start and one period for every reader of a frame, the period from
    /// the host's source (the Quest's frame synthesis) or one 90 Hz frame, never an extended budget for want of an answer.
    /// </summary>
    public sealed class SharedMainBudgetTests
    {
        [TearDown]
        public void TearDown()
        {
            CutPhysicsStep.SetApplicationFrameSource(null);
            CutPhysicsStep.SettleFramePeriod();
        }

        [Test]
        public void TheBudget_IsTheApplicationFrameLessTheCommonMargin_At45And90Hz()
        {
            Assert.That(CutPhysicsStep.CommonMarginSeconds, Is.EqualTo(0.002), "2 ms, provisional");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 45.0) * 1000.0, Is.EqualTo(22.2222 - 2.0).Within(1e-3), "an application at 45 Hz: 22.2 ms less the margin");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 90.0) * 1000.0, Is.EqualTo(11.1111 - 2.0).Within(1e-3), "at 90 Hz: 11.1 ms less the margin");
            Assert.That(CutPhysicsStep.DefaultMainBudgetSeconds, Is.EqualTo(CutPhysicsStep.BudgetFor(CutPhysicsStep.DefaultApplicationFrameSeconds)));
        }

        [Test]
        public void ReadersOfOneFrame_ReadOneRemainder_FromOneStart_GoingDown_AndANegativeOneStands()
        {
            long f = Stopwatch.Frequency, start = 1_000_000;
            double frame = 1.0 / 45.0;
            double first = CutPhysicsStep.RemainingAt(frame, start, start + (f / 1000), f);        // 1 ms in
            double second = CutPhysicsStep.RemainingAt(frame, start, start + (5 * f / 1000), f);   // 5 ms in: a later reader
            Assert.That(first, Is.EqualTo(CutPhysicsStep.BudgetFor(frame) - 0.001).Within(1e-9));
            Assert.That(second, Is.EqualTo(first - 0.004).Within(1e-9), "the same start: no reader gets a frame of its own");
            double past = CutPhysicsStep.RemainingAt(frame, start, start + (25 * f / 1000), f);
            Assert.That(past, Is.LessThan(0.0), "past the budget: a shortfall as it stands, not clamped");
            Assert.That(past, Is.EqualTo(CutPhysicsStep.BudgetFor(frame) - 0.025).Within(1e-9));
        }

        [Test]
        public void AnAnswer_IsTakenOnlyWhenItIsAFramePeriod_OtherwiseTheDefault90HzFrame()
        {
            foreach (double usable in new[] { 1.0 / 45.0, 1.0 / 90.0, 1.0 / 72.0, 1.0 / 120.0 })
            {
                Assert.That(CutPhysicsStep.AcceptedFrameSeconds(usable, out bool from), Is.EqualTo(usable));
                Assert.That(from, Is.True);
            }

            foreach (double unusable in new[] { double.NaN, 0.0, -1.0 / 45.0, double.PositiveInfinity, 1.0 / 200.0, 1.0 / 10.0 })
            {
                Assert.That(CutPhysicsStep.AcceptedFrameSeconds(unusable, out bool from), Is.EqualTo(CutPhysicsStep.DefaultApplicationFrameSeconds), unusable.ToString());
                Assert.That(from, Is.False, "never an extended budget from " + unusable);
            }
        }

        [Test]
        public void TheSource_IsReadAtTheFrameStart_AndCleared_ThrowingOrSilent_LeavesNoExtendedBudget()
        {
            double answer = 1.0 / 45.0;
            Func<double> source = () => answer;
            CutPhysicsStep.SetApplicationFrameSource(source);
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(1.0 / 45.0));
            Assert.That(CutPhysicsStep.ApplicationFrameFromSource, Is.True);
            Assert.That(CutPhysicsStep.MainBudgetSeconds, Is.EqualTo(CutPhysicsStep.BudgetFor(1.0 / 45.0)));

            answer = 1.0 / 90.0;   // the application back at 90 Hz (AppSW off): the next frame's budget follows
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(1.0 / 90.0), "no stale 45 Hz budget after the switch");

            answer = double.NaN;   // no answer (state not read)
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(new object[] { CutPhysicsStep.ApplicationFrameSeconds, CutPhysicsStep.ApplicationFrameFromSource },
                Is.EqualTo(new object[] { CutPhysicsStep.DefaultApplicationFrameSeconds, false }));

            answer = 1.0 / 45.0;
            CutPhysicsStep.ClearApplicationFrameSource(() => 1.0 / 45.0);   // another host leaving: not this source
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(1.0 / 45.0), "another's leaving does not clear this source");
            CutPhysicsStep.ClearApplicationFrameSource(source);   // the host gone
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(CutPhysicsStep.DefaultApplicationFrameSeconds), "no source: the default from the next frame");

            CutPhysicsStep.SetApplicationFrameSource(() => throw new InvalidOperationException("fixture source"));
            LogAssert.Expect(LogType.Exception, new Regex("fixture source"));
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(CutPhysicsStep.DefaultApplicationFrameSeconds), "a source that throws has no answer");
        }

        [Test]
        public void TheFramesStart_IsTakenBeforeTheSource_SoTheSourcesTime_IsTakenFromTheRemainder()
        {
            // A source that takes 5 ms (the Quest's XR and OpenXR queries, or an interruption in them).
            const int sourceMs = 5;
            CutPhysicsStep.SetApplicationFrameSource(() => { System.Threading.Thread.Sleep(sourceMs); return 1.0 / 45.0; });
            long before = Stopwatch.GetTimestamp();
            CutPhysicsStep.BeginFrame();
            long after = Stopwatch.GetTimestamp();
            long start = CutPhysicsStep.FrameStartTimestamp;
            long f = Stopwatch.Frequency;
            Assert.That(start, Is.GreaterThanOrEqualTo(before));
            Assert.That((after - start) / (double)f, Is.GreaterThanOrEqualTo((sourceMs - 0.5) / 1000.0),
                "the start was taken before the source was asked: its time is inside the frame");
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(1.0 / 45.0), "and the period is the source's answer");
            double remaining = CutPhysicsStep.RemainingAt(CutPhysicsStep.ApplicationFrameSeconds, start, after, f);
            Assert.That(remaining, Is.LessThanOrEqualTo(CutPhysicsStep.BudgetFor(1.0 / 45.0) - ((sourceMs - 0.5) / 1000.0)),
                "what the source took is taken from the shared remainder");
        }

        [Test]
        public void TheQuestsSource_AnswersOnlyUnderFrameSynthesis_FromTheRuntimesRates()
        {
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 45f, true, 90f), Is.EqualTo(1.0 / 45.0), "AppSW applied, 45 of 90 Hz");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 44.99932f, true, 90f), Is.EqualTo(1.0 / 45.0), "the reported rate rounded");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 90f, true, 90f), Is.NaN, "the application at the display's rate: the default");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(false, true, true, 45f, true, 90f), Is.NaN, "AppSW not applied");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, false, true, 45f, true, 90f), Is.NaN, "AppSW off");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, false, 45f, true, 90f), Is.NaN, "the rate not known");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 45f, false, 90f), Is.NaN, "the display's rate not known");
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 0f, true, 90f), Is.NaN);
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, float.NaN, true, 90f), Is.NaN);
            Assert.That(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 45f, true, float.PositiveInfinity), Is.NaN);
            // Through the budget's acceptance: no answer is the default 90 Hz frame, never 45 Hz's.
            Assert.That(CutPhysicsStep.AcceptedFrameSeconds(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, false, true, 45f, true, 90f), out _),
                Is.EqualTo(CutPhysicsStep.DefaultApplicationFrameSeconds));
        }

        [Test]
        public void TheCollectionsAddNoFrameOfTheirOwn_AndNoReserveTakesTheMarginAgain()
        {
            Assert.That(typeof(CutFragmentGroupCollection).GetProperty("FrameSeconds"), Is.Null,
                "the fragment groups' addition of the application's frame (D-233) is gone: the shared budget holds it");
            Assert.That(CutWorldRoot.CompactionReserveSeconds, Is.Zero,
                "the compaction's, the collections' and the split's reserve was the same room the common margin leaves now");
            Assert.That(CutWorldProfile.BeyondCommonMargin(0.004), Is.EqualTo(0.002).Within(1e-12), "a 4 ms floor keeps its own 2 ms beyond the margin");
            Assert.That(CutWorldProfile.BeyondCommonMargin(0.001), Is.Zero, "never below 0");
            var profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                Assert.That(profile.PieceLifetime.minRemainingMainSeconds, Is.EqualTo(0.002).Within(1e-9), "the piece lifetime's 4 ms less the margin");
                Assert.That(profile.FragmentGroupCollection.reserveSeconds, Is.Zero);
                Assert.That(profile.EmptyOwnerCollection.reserveSeconds, Is.Zero);
                Assert.That(profile.SpatialSplit.reserveSeconds, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }
    }
}
