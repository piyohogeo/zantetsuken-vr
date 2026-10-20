using System;
using NUnit.Framework;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// PCVR's display step (DESIGN 4.4, 2026-10-20) in the clock's arithmetic: the normal step at 72 or 90 Hz, one
    /// double step (one simulation of 2h) when two normal steps are owed and its own estimate fits, else the normal step
    /// when it fits, else none; the cap of two normal steps applied before the choice; the physics time the sum of what
    /// was really simulated; nothing owed or advanced while paused or on a skip.
    /// </summary>
    public sealed class DisplayStepClockTests
    {
        private const long TicksPerSecond = 1_000_000;
        private const double Tick = 1.0 / TicksPerSecond;

        // Rounded up to a whole tick: a time given as "two steps" is then at least two steps (1/90 s is no whole number of ticks).
        private static long Seconds(double seconds) => (long)Math.Ceiling(seconds * TicksPerSecond);

        private static ManualPhysicsClock Owing(int hz, double steps, bool doubleStep = true)
        {
            var clock = new ManualPhysicsClock(hz, TicksPerSecond, doubleStep);
            clock.Accumulate(0);
            clock.Accumulate(Seconds(steps / hz));
            return clock;
        }

        [TestCase(72)]
        [TestCase(90)]
        public void TheNormalStep_IsTheDisplaysPeriod_AndTheDoubleStepTwiceIt(int hz)
        {
            var clock = new ManualPhysicsClock(hz, TicksPerSecond, doubleStep: true);
            Assert.That(clock.StepSeconds, Is.EqualTo(1.0 / hz));
            Assert.That(clock.DoubleStep, Is.True);
            ManualPhysicsClock owing = Owing(hz, 2.5);
            Assert.That(owing.Choose(0.0, 0.0, 1.0), Is.EqualTo(2.0 / hz).Within(1e-15), hz == 90 ? "1/45 s" : "1/36 s");
            Assert.That(new ManualPhysicsClock(45, TicksPerSecond).DoubleStep, Is.False, "the fixed step of before has none");
            Assert.Throws<ArgumentOutOfRangeException>(() => new ManualPhysicsClock(60, TicksPerSecond, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ManualPhysicsClock(36, TicksPerSecond, true));
        }

        [TestCase(72)]
        [TestCase(90)]
        public void TheCandidate_FollowsTheOwing_AtItsBoundaries(int hz)
        {
            double h = 1.0 / hz;
            Assert.That(Owing(hz, 0.99).Choose(0.0, 0.0, 1.0), Is.Zero, "under one step: no update");
            Assert.That(Owing(hz, 1.0).Choose(0.0, 0.0, 1.0), Is.EqualTo(h), "exactly one step: the normal step");
            Assert.That(Owing(hz, 1.99).Choose(0.0, 0.0, 1.0), Is.EqualTo(h), "under two: the normal step");
            Assert.That(Owing(hz, 2.0).Choose(0.0, 0.0, 1.0), Is.EqualTo(2.0 * h).Within(1e-15), "two steps owed: the double step");
            Assert.That(Owing(hz, 2.0).CandidateStepSeconds(), Is.EqualTo(2.0 * h).Within(1e-15));
            Assert.That(Owing(hz, 1.5).CandidateStepSeconds(), Is.EqualTo(h));
            Assert.That(Owing(hz, 0.5).CandidateStepSeconds(), Is.Zero);
            Assert.That(Owing(hz, 2.0, doubleStep: false).Choose(0.0, 0.0, 1.0), Is.EqualTo(h), "without the double step, one normal step as before");
        }

        /// <summary>
        /// **The cap is on what a decision carries over** (TL review, 2026-10-20): the choice is made from the carried time
        /// (at most 2h) plus the time since; afterwards at most 2h is carried and the rest dropped and counted. A stall adds
        /// at most 2h of catch-up, never more.
        /// </summary>
        [Test]
        public void TheCap_IsOnWhatADecisionCarriesOver_AndAStallAddsAtMostTwoStepsOfCatchUp()
        {
            var clock = new ManualPhysicsClock(90, TicksPerSecond, doubleStep: true);
            double h = clock.StepSeconds;
            clock.Accumulate(0);
            clock.Accumulate(Seconds(1.5 * h));   // 1.5 steps owed
            clock.CarryOver();
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(1.5 * h).Within(4 * Tick), "under the cap: all carried");
            clock.Accumulate(Seconds(2.5 * h));   // 2.5 steps owed at this decision
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(2.5 * h).Within(4 * Tick), "the choice sees what was carried and the time since");
            Assert.That(clock.DroppedSeconds, Is.Zero);
            clock.Stepped(clock.Choose(0.0, 0.0, 1.0));
            clock.CarryOver();
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(0.5 * h).Within(4 * Tick), "one double step; the half step left is kept, not dropped");
            Assert.That(clock.DroppedSeconds, Is.Zero);

            // A skipped decision (no room) with 2.5 steps owed carries two and drops the half.
            clock.Accumulate(Seconds(4.5 * h));
            Assert.That(clock.Choose(1.0, 1.0, 0.0), Is.Zero, "no room: a skip");
            clock.CarryOver();
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(2.0 * h).Within(4 * Tick), "at most two normal steps carried");
            Assert.That(clock.DroppedSeconds, Is.EqualTo(0.5 * h).Within(4 * Tick), "the rest dropped and counted");

            // A one-second stall: one double step, two steps carried, the rest dropped; then nothing more of the stall.
            double before = clock.PhysicsSeconds;
            clock.Accumulate(Seconds(4.5 * h + 1.0));
            clock.Stepped(clock.Choose(0.0, 0.0, 1.0));
            clock.CarryOver();
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(2.0 * h).Within(4 * Tick), "never more than two steps carried");
            Assert.That(clock.DroppedSeconds, Is.EqualTo(0.5 * h + 1.0 - 2.0 * h).Within(4 * Tick), "the stall dropped beyond what is carried");
            clock.Accumulate(Seconds(4.5 * h + 1.0 + h));   // then frames at the display's rate
            clock.Stepped(clock.Choose(0.0, 0.0, 1.0));
            clock.CarryOver();
            clock.Accumulate(Seconds(4.5 * h + 1.0 + 2.0 * h));
            clock.Stepped(clock.Choose(0.0, 0.0, 1.0));
            clock.CarryOver();
            Assert.That(clock.PhysicsSeconds - before, Is.EqualTo(6.0 * h).Within(4 * Tick), "the stall's catch-up: 2h carried, simulated over the next frames, no more");
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(0.0).Within(4 * Tick));
        }

        /// <summary>
        /// **The TL's case (review, 2026-10-20): an application a little faster than half the display's rate.** Frames of
        /// 22.222 ms on a 90 Hz step (27.777 ms on 72 Hz) for 60 s with room for every step: the physics keeps the real
        /// time -- nothing dropped. (With the cap before the choice it went h, 2h, h, 2h and kept 45 s of 60.) Slower than
        /// half the rate (23 ms on 90 Hz) the double step every frame is the most there is: the physics follows at 2h a
        /// frame, the rest dropped, at most 2h carried.
        /// </summary>
        [TestCase(90, 0.022222)]
        [TestCase(72, 0.027777)]
        public void FramesAlittleUnderTwoSteps_KeepTheRealTime_NothingDropped(int hz, double interval)
        {
            var clock = new ManualPhysicsClock(hz, 1_000_000_000L, doubleStep: true);
            double h = clock.StepSeconds;
            long ticks = 0, step = (long)Math.Round(interval * 1e9);
            clock.Accumulate(ticks);
            int frames = (int)(60.0 / interval);
            for (int i = 0; i < frames; i++)
            {
                ticks += step;
                clock.Accumulate(ticks);
                double chosen = clock.Choose(0.0, 0.0, 1.0);
                if (chosen > 0.0) clock.Stepped(chosen);
                clock.CarryOver();
                Assert.That(clock.UnsimulatedSeconds, Is.LessThanOrEqualTo(2.0 * h + 1e-12), "never more than two steps carried");
            }

            double real = ticks / 1e9;
            TestContext.Out.WriteLine(hz + " Hz, " + (interval * 1000.0) + " ms frames: real " + real.ToString("F3") + " s, physics " + clock.PhysicsSeconds.ToString("F3")
                + " s, dropped " + clock.DroppedSeconds.ToString("F6") + " s, steps " + clock.NormalSteps + " normal + " + clock.DoubleSteps + " double");
            Assert.That(real, Is.GreaterThan(59.9));
            Assert.That(clock.DroppedSeconds, Is.Zero, "nothing dropped");
            Assert.That(clock.PhysicsSeconds, Is.EqualTo(real).Within(2.0 * h), "the physics keeps the real time, behind by what is carried");
            Assert.That(clock.PhysicsSeconds + clock.UnsimulatedSeconds + clock.DroppedSeconds, Is.EqualTo(real).Within(1e-9), "every second simulated, carried or dropped");
        }

        [Test]
        public void FramesOverTwoSteps_TakeTheDoubleStepEachFrame_TheRestDropped_AtMostTwoStepsCarried()
        {
            var clock = new ManualPhysicsClock(90, 1_000_000_000L, doubleStep: true);
            double h = clock.StepSeconds;
            long ticks = 0, step = 23_000_000L;   // 23 ms: slower than half of 90 Hz
            clock.Accumulate(ticks);
            for (int i = 0; i < 2000; i++)
            {
                ticks += step;
                clock.Accumulate(ticks);
                double chosen = clock.Choose(0.0, 0.0, 1.0);
                if (i > 0) Assert.That(chosen, Is.EqualTo(2.0 * h).Within(1e-15), "the double step each frame");
                if (chosen > 0.0) clock.Stepped(chosen);
                clock.CarryOver();
                Assert.That(clock.UnsimulatedSeconds, Is.LessThanOrEqualTo(2.0 * h + 1e-12));
            }

            double real = ticks / 1e9;
            Assert.That(clock.PhysicsSeconds, Is.EqualTo(clock.StepId * 2.0 * h).Within(1e-9), "2h a frame: the most there is");
            Assert.That(clock.PhysicsSeconds + clock.UnsimulatedSeconds + clock.DroppedSeconds, Is.EqualTo(real).Within(1e-9));
            Assert.That(clock.DroppedSeconds, Is.GreaterThan(0.0), "the rest dropped, not caught up later");
        }

        [Test]
        public void TheFixedStep_KeepsItsCapAtTheAccumulation_AsBefore()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            double h = clock.StepSeconds;
            clock.Accumulate(0);
            clock.Accumulate(Seconds(2.5 * h));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(2.0 * h).Within(Tick), "the fixed step (Quest) caps when the time is added");
            Assert.That(clock.DroppedSeconds, Is.EqualTo(0.5 * h).Within(Tick));
            clock.CarryOver();
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(2.0 * h).Within(Tick), "the carry-over does nothing on it");
        }

        [Test]
        public void ADoubleStepThatDoesNotFit_FallsBackToTheNormalStep_AndOneThatNeitherFits_IsASkip()
        {
            ManualPhysicsClock clock = Owing(90, 2.0);
            double h = clock.StepSeconds;
            Assert.That(clock.Choose(0.002, 0.004, 0.005), Is.EqualTo(2.0 * h).Within(1e-15), "the double's own estimate fits");
            Assert.That(clock.Choose(0.002, 0.006, 0.005), Is.EqualTo(h), "the double's does not, the normal's does: the normal step");
            Assert.That(clock.Choose(0.006, 0.012, 0.005), Is.Zero, "neither: a skip for the budget");
            Assert.That(clock.Choose(0.0, 0.0, -0.001), Is.Zero, "nothing left: a skip");
            Assert.That(clock.StepId, Is.Zero, "a decision is not a step");
            Assert.That(clock.PhysicsSeconds, Is.Zero);
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(2.0 * h).Within(Tick), "a skip advances nothing and the owing stays");
            Assert.That(Owing(90, 1.5).Choose(0.002, 0.0, 0.001), Is.Zero, "one step owed: only the normal step's estimate is compared");
        }

        [Test]
        public void TheExecutedStep_IsWhatIsConsumedAndAddedToThePhysicsTime_TheIdMovingByOne()
        {
            var clock = new ManualPhysicsClock(72, TicksPerSecond, doubleStep: true);
            double h = clock.StepSeconds;
            clock.Accumulate(0);
            clock.Accumulate(Seconds(2.0 * h));
            clock.Stepped(2.0 * h);
            Assert.That(new object[] { clock.StepId, clock.DoubleSteps, clock.NormalSteps }, Is.EqualTo(new object[] { 1L, 1L, 0L }));
            Assert.That(clock.PhysicsSeconds, Is.EqualTo(2.0 * h).Within(1e-15));
            Assert.That(clock.LastStepSeconds, Is.EqualTo(2.0 * h).Within(1e-15));
            clock.Accumulate(Seconds(3.2 * h));
            clock.Stepped(clock.Choose(0.0, 0.0, 1.0));
            Assert.That(new object[] { clock.StepId, clock.DoubleSteps, clock.NormalSteps }, Is.EqualTo(new object[] { 2L, 1L, 1L }));
            Assert.That(clock.PhysicsSeconds, Is.EqualTo(3.0 * h).Within(1e-15), "the sum of what was simulated");
            Assert.That(clock.PhysicsSeconds, Is.Not.EqualTo(clock.StepId * clock.StepSeconds).Within(1e-9), "not the step id times the normal step");
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(0.2 * h).Within(Tick));

            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Stepped(1.5 * h), "a step is h or 2h");
            Assert.Throws<ArgumentOutOfRangeException>(() => Owing(45, 2.0, doubleStep: false).Stepped(2.0 / 45.0), "no double step on the fixed clock");
        }

        [Test]
        public void AskingTwice_ChangesNothing_SoOneDecisionTakesOneSimulationAtMost()
        {
            ManualPhysicsClock clock = Owing(90, 2.0);
            double first = clock.Choose(0.0, 0.0, 1.0);
            double second = clock.Choose(0.0, 0.0, 1.0);
            Assert.That(second, Is.EqualTo(first), "one length, asked again: the same, nothing consumed");
            clock.Stepped(first);
            Assert.That(clock.Choose(0.0, 0.0, 1.0), Is.Zero, "the double step took both: no second simulation in the frame");
            Assert.That(clock.StepId, Is.EqualTo(1));
        }

        [Test]
        public void WhilePaused_NothingIsOwedOrChosen_AndTheResumeCatchesNothingOfThePauseUp()
        {
            ManualPhysicsClock clock = Owing(90, 1.5);
            double h = clock.StepSeconds;
            double before = clock.UnsimulatedSeconds;
            clock.SetPaused(true);
            Assert.That(clock.Choose(0.0, 0.0, 1.0), Is.Zero, "paused: no step");
            Assert.That(clock.CandidateStepSeconds(), Is.Zero);
            Assert.That(clock.WouldOweADoubleStepAt(Seconds(100.0)), Is.False);
            clock.Accumulate(Seconds(10.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(before), "the pause is not owed");
            Assert.That(clock.DroppedSeconds, Is.Zero, "nor dropped: never counted");

            clock.SetPaused(false);
            clock.Accumulate(Seconds(20.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(before), "the resume only realigns");
            Assert.That(clock.Choose(0.0, 0.0, 1.0), Is.EqualTo(h), "what was owed before the pause, one normal step: no double step for the pause");
        }

        [Test]
        public void WhetherADoubleStepWillBeOwed_IsReadAhead_AsTheDecisionWillFindIt_WithoutChangingAnything()
        {
            var clock = new ManualPhysicsClock(90, TicksPerSecond, doubleStep: true);
            double h = clock.StepSeconds;
            clock.Accumulate(Seconds(1.0));
            Assert.That(clock.WouldOweADoubleStepAt(Seconds(1.0 + 1.5 * h)), Is.False);
            Assert.That(clock.WouldOweADoubleStepAt(Seconds(1.0 + 2.0 * h)), Is.True);
            Assert.That(clock.WouldOweADoubleStepAt(Seconds(3.0)), Is.True, "a stall: two and more");
            Assert.That(clock.UnsimulatedSeconds, Is.Zero, "asking added nothing");
            clock.Accumulate(Seconds(1.0 + 0.5 * h));
            clock.CarryOver();
            Assert.That(clock.WouldOweADoubleStepAt(Seconds(1.0 + 2.0 * h)), Is.True, "half a step carried and 1.5 since");
            Assert.That(clock.WouldOweAStepAt(Seconds(1.0 + 1.0 * h)), Is.True, "half carried and half since");
            Assert.That(new ManualPhysicsClock(90, TicksPerSecond).WouldOweADoubleStepAt(Seconds(3.0)), Is.False, "no double step on the fixed clock");
        }

        /// <summary>
        /// **The double step has its own cost history.** Its estimate is the median of its own real samples; before any,
        /// twice the normal step's. Normal samples never move it once it has its own, and its samples never move the
        /// normal step's.
        /// </summary>
        [Test]
        public void TheDoubleStepsEstimate_IsItsOwn_AndTwiceTheNormalOneBeforeAny()
        {
            var normals = new SimulateCostHistory();
            var doubles = new SimulateCostHistory();
            Assert.That(CutPhysicsStep.ExpectedDoubleFrom(doubles, normals), Is.Zero, "nothing measured: 0, as the normal step's");
            for (int i = 0; i < 5; i++) normals.Add(0.001);
            Assert.That(CutPhysicsStep.ExpectedDoubleFrom(doubles, normals), Is.EqualTo(0.002).Within(1e-15), "no double sample: twice the normal");
            doubles.Add(0.0013);
            Assert.That(CutPhysicsStep.ExpectedDoubleFrom(doubles, normals), Is.EqualTo(0.0013), "one double sample: its own");
            for (int i = 0; i < 5; i++) normals.Add(0.009);
            Assert.That(CutPhysicsStep.ExpectedDoubleFrom(doubles, normals), Is.EqualTo(0.0013), "the normal steps' rise does not move it");
            Assert.That(normals.ExpectedSeconds, Is.EqualTo(0.009), "and its sample did not move theirs");
        }
    }
}
