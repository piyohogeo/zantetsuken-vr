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

        /// <summary>
        /// **The margin is 9 % of the target period** (TL, 2026-10-20): PCVR 90 Hz 1.00 ms (budget 10.11 ms), PCVR 72 Hz
        /// 1.25 ms (12.64 ms), Quest under AppSW at 45 Hz 2.00 ms (20.22 ms, as with the fixed 2 ms). The fixed 2 ms of
        /// 2026-10-19 stays for comparison.
        /// </summary>
        [Test]
        public void TheBudget_IsTheTargetPeriodLess9Percent_At90_72And45Hz_AndTheFixed2MsIsKeptForComparison()
        {
            const MainMarginKind nine = MainMarginKind.PeriodFraction, two = MainMarginKind.Fixed2Ms;
            Assert.That(CutPhysicsStep.MarginFraction, Is.EqualTo(0.09));
            Assert.That(CutPhysicsStep.MarginFor(1.0 / 90.0, nine) * 1000.0, Is.EqualTo(1.00).Within(1e-9), "PCVR 90 Hz: 1.00 ms");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 90.0, nine) * 1000.0, Is.EqualTo(10.1111).Within(1e-3), "budget ~10.11 ms");
            Assert.That(CutPhysicsStep.MarginFor(1.0 / 72.0, nine) * 1000.0, Is.EqualTo(1.25).Within(1e-9), "PCVR 72 Hz: 1.25 ms");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 72.0, nine) * 1000.0, Is.EqualTo(12.6389).Within(1e-3), "budget ~12.64 ms");
            Assert.That(CutPhysicsStep.MarginFor(1.0 / 45.0, nine) * 1000.0, Is.EqualTo(2.00).Within(1e-9), "Quest AppSW 45 Hz: 2.00 ms, as before");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 45.0, nine) * 1000.0, Is.EqualTo(20.2222).Within(1e-3), "budget ~20.22 ms, as before");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 45.0, nine), Is.EqualTo(CutPhysicsStep.BudgetFor(1.0 / 45.0, two)).Within(1e-12), "at 45 Hz the two margins agree");

            foreach (double frame in new[] { 1.0 / 90.0, 1.0 / 72.0, 1.0 / 45.0 })
            {
                Assert.That(CutPhysicsStep.MarginFor(frame, two), Is.EqualTo(0.002), "the fixed 2 ms whatever the period");
                Assert.That(CutPhysicsStep.BudgetFor(frame, two), Is.EqualTo(frame - 0.002).Within(1e-15));
            }

            Assert.That(CutPhysicsStep.MarginKind, Is.EqualTo(nine), "the default kind (no argument)");
            Assert.That(CutPhysicsStep.BudgetFor(1.0 / 90.0), Is.EqualTo(CutPhysicsStep.BudgetFor(1.0 / 90.0, nine)), "the session's kind is the default");
            Assert.That(CutPhysicsStep.DefaultMainBudgetSeconds, Is.EqualTo(CutPhysicsStep.BudgetFor(CutPhysicsStep.DefaultApplicationFrameSeconds)));
        }

        /// <summary>
        /// **The margin is taken once, from the period alone.** The remainder is the budget less the time since the frame's
        /// start -- the margin not taken again -- and nothing of the step enters it: a double step's frame has the same
        /// budget as any other.
        /// </summary>
        [Test]
        public void TheMargin_IsTakenOnce_AndTheDoubleStepDoesNotEnlargeTheBudget()
        {
            long f = Stopwatch.Frequency, start = 5_000_000;
            foreach (double frame in new[] { 1.0 / 90.0, 1.0 / 72.0, 1.0 / 45.0 })
            {
                double remaining = CutPhysicsStep.RemainingAt(frame, start, start + (2 * f / 1000), f);
                Assert.That(remaining, Is.EqualTo(frame - (0.09 * frame) - 0.002).Within(1e-9), "period, less 9 % once, less 2 ms spent");
            }
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
            // Quest under AppSW at 45 Hz, through its source: the 9 % margin is 2.00 ms and the budget ~20.22 ms, as with the fixed 2 ms.
            double quest = CutPhysicsStep.AcceptedFrameSeconds(QuestNpcSlashDemo.FrameSecondsUnderSynthesis(true, true, true, 44.99932f, true, 90f), out bool fromQuest);
            Assert.That(fromQuest, Is.True);
            Assert.That(CutPhysicsStep.MarginFor(quest, MainMarginKind.PeriodFraction) * 1000.0, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(CutPhysicsStep.BudgetFor(quest, MainMarginKind.PeriodFraction) * 1000.0, Is.EqualTo(20.2222).Within(1e-3));
        }

        [Test]
        public void TheCollectionsAddNoFrameOfTheirOwn_AndNoReserveTakesTheMarginAgain()
        {
            Assert.That(typeof(CutFragmentGroupCollection).GetProperty("FrameSeconds"), Is.Null,
                "the fragment groups' addition of the application's frame (D-233) is gone: the shared budget holds it");
            Assert.That(CutWorldRoot.CompactionReserveSeconds, Is.Zero,
                "the compaction's, the collections' and the split's reserve was the same room the common margin leaves now");
            Assert.That(CutWorldProfile.ReserveFrameTailSeconds, Is.EqualTo(0.002), "the part D-251 took over: 2 ms, fixed");
            Assert.That(CutWorldProfile.BeyondFrameTail(0.004), Is.EqualTo(0.002).Within(1e-12), "a 4 ms floor keeps its own 2 ms");
            Assert.That(CutWorldProfile.BeyondFrameTail(0.001), Is.Zero, "never below 0");
            // No offsetting (TL, 2026-10-20): at PCVR 90 Hz the margin is 1 ms, and a reserve is not given that 1 ms back.
            Assert.That(CutWorldProfile.BeyondFrameTail(0.004), Is.Not.EqualTo(0.004 - CutPhysicsStep.MarginFor(1.0 / 90.0, MainMarginKind.PeriodFraction)).Within(1e-9),
                "not the reserve less the frame's margin (that would be 3 ms at 90 Hz)");
            var profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                Assert.That(profile.PieceLifetime.minRemainingMainSeconds, Is.EqualTo(0.002).Within(1e-9), "the piece lifetime's 4 ms less the fixed 2 ms: 2 ms at every rate");
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
