using System;
using NUnit.Framework;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The arithmetic of the manual physics update of DESIGN 4.4: the fixed step, real time owed and kept up to a cap
    /// of two steps (what is owed beyond it is dropped and counted, never simulated later), at most one step per
    /// decision, the budget, the pause, and that only a real step moves the physics time and the step id.
    /// </summary>
    public class ManualPhysicsClockTests
    {
        private const long TicksPerSecond = 1_000_000;

        /// <summary>Timestamps are whole ticks, so a time given in seconds is exact only to one tick.</summary>
        private const double Tick = 1.0 / TicksPerSecond;

        private static long Seconds(double seconds)
        {
            return (long)Math.Round(seconds * TicksPerSecond);
        }

        [Test]
        public void TheStep_IsTheInverseOf45Or90Hz_AndNoOtherFrequencyIsAccepted()
        {
            Assert.That(new ManualPhysicsClock(45, TicksPerSecond).StepSeconds, Is.EqualTo(1.0 / 45.0));
            Assert.That(new ManualPhysicsClock(90, TicksPerSecond).StepSeconds, Is.EqualTo(1.0 / 90.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ManualPhysicsClock(60, TicksPerSecond));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ManualPhysicsClock(50, TicksPerSecond));
        }

        [Test]
        public void TheFirstInterval_OnlySetsWhereTimeIsCountedFrom()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(Seconds(100.0));
            Assert.That(clock.UnsimulatedSeconds, Is.Zero, "the start is not owed");
            clock.Accumulate(Seconds(100.0 + 0.5 / 45.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(0.5 / 45.0).Within(Tick), "counted from the start, under the cap");
            Assert.That(clock.DroppedSeconds, Is.Zero);
        }

        [Test]
        public void ADecision_TakesAtMostOneStep_AndOnlyARealStepConsumesItAndMovesTheId()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(0);
            clock.Accumulate(Seconds(1.5 / 45.0));

            Assert.That(clock.ShouldStep(0.0, 1.0), Is.True, "a step and a half are owed");
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.True, "asking again changes nothing");
            Assert.That(clock.StepId, Is.Zero, "a decision is not a step");
            Assert.That(clock.PhysicsSeconds, Is.Zero);

            clock.Stepped();
            Assert.That(clock.StepId, Is.EqualTo(1));
            Assert.That(clock.PhysicsSeconds, Is.EqualTo(1.0 / 45.0).Within(1e-12));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(0.5 / 45.0).Within(Tick), "exactly one step was consumed");
        }

        [Test]
        public void LessThanAStepOwed_IsANop_AndTheTimeStays()
        {
            var clock = new ManualPhysicsClock(90, TicksPerSecond);
            clock.Accumulate(0);
            clock.Accumulate(Seconds(0.5 / 90.0));
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.False);
            clock.Accumulate(Seconds(1.5 / 90.0));
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.True, "the half step left before is added to, not dropped");
        }

        [Test]
        public void AStepThatDoesNotFitTheBudget_IsNotTaken_AndWhatWasOwedIsKeptForLater_UpToTheCap()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(0);
            clock.Accumulate(Seconds(1.8 / 45.0));

            Assert.That(clock.ShouldStep(0.004, 0.003), Is.False, "expected 4 ms, 3 ms left");
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(1.8 / 45.0).Within(Tick), "nothing under the cap is dropped for want of budget");
            Assert.That(clock.StepId, Is.Zero);

            Assert.That(clock.ShouldStep(0.004, 0.005), Is.True, "a later frame with room steps");
            clock.Stepped();
            Assert.That(clock.ShouldStep(0.004, 0.005), Is.False, "the 0.8 step left is under a step: no catch-up beyond what is owed");
            clock.Accumulate(Seconds(2.2 / 45.0));
            Assert.That(clock.ShouldStep(0.004, 0.005), Is.True, "and the rest can be caught up when there is room");
            clock.Stepped();
            Assert.That(clock.StepId, Is.EqualTo(2));
        }

        /// <summary>The cap (DESIGN 4.4, 2026-09-30): at most two steps are ever owed; a long frame owes two steps and the rest is dropped and counted, so a stall never stores a catch-up.</summary>
        [Test]
        public void ALongFrame_OwesAtMostTwoSteps_TheRestIsDroppedAndCounted_AndStillStepsOnlyOncePerDecision()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            double cap = ManualPhysicsClock.MaxOwedSteps * clock.StepSeconds;
            Assert.That(ManualPhysicsClock.MaxOwedSteps, Is.EqualTo(2));
            clock.Accumulate(0);
            clock.Accumulate(Seconds(1.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(cap).Within(Tick), "two steps are owed, not the second");
            Assert.That(clock.DroppedSeconds, Is.EqualTo(1.0 - cap).Within(Tick), "the rest of the second was dropped and counted");
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.True);
            clock.Stepped();
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(cap - clock.StepSeconds).Within(Tick), "one step, not a catch-up");
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.True, "the second owed step");
            clock.Stepped();
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.False, "and no more: nothing of the long frame remains");
            Assert.That(clock.StepId, Is.EqualTo(2));

            // A frame under the cap drops nothing more; a frame over it drops only what is over.
            clock.Accumulate(Seconds(1.0 + 1.5 / 45.0));
            Assert.That(clock.DroppedSeconds, Is.EqualTo(1.0 - cap).Within(Tick), "under the cap: nothing dropped");
            clock.Accumulate(Seconds(1.0 + 3.0 / 45.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(cap).Within(Tick));
            Assert.That(clock.DroppedSeconds, Is.EqualTo(1.0 - cap + 1.0 / 45.0).Within(Tick), "over the cap: the step over it dropped");
        }

        [Test]
        public void TimeWhilePaused_IsNotOwed_WhatWasOwedBeforeIsKept_AndResumingStartsANewInterval()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(0);
            clock.Accumulate(Seconds(0.01));
            double before = clock.UnsimulatedSeconds;

            clock.SetPaused(true);
            clock.Accumulate(Seconds(5.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(before), "the pause is not owed");
            Assert.That(clock.DroppedSeconds, Is.Zero, "and not dropped either: it was never counted");

            clock.SetPaused(false);
            clock.Accumulate(Seconds(10.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(before), "the first call after the resume only realigns");
            clock.Accumulate(Seconds(10.02));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(before + 0.02).Within(Tick));
            Assert.That(clock.DroppedSeconds, Is.Zero);
        }

        [Test]
        public void TimeLeftOverForWantOfBudget_IsNotSimulatedWhilePaused_AndIsKeptForTheResume()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(0);
            clock.Accumulate(Seconds(1.8 / 45.0));
            Assert.That(clock.ShouldStep(0.004, 0.001), Is.False, "a frame without room leaves the step owed");

            clock.SetPaused(true);
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.False, "paused: no step, however much is owed and however much room");
            clock.Accumulate(Seconds(1.0));
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.False);
            Assert.That(clock.StepId, Is.Zero);
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(1.8 / 45.0).Within(Tick), "what was owed is kept, and nothing added");

            clock.SetPaused(false);
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.True, "after the resume the time left over is simulated");
            clock.Stepped();
            Assert.That(clock.ShouldStep(0.0, 1.0), Is.False, "the 0.8 step left waits for more time");
            Assert.That(clock.StepId, Is.EqualTo(1));
        }

        [Test]
        public void AClockThatGoesBackwards_AddsNothing()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(Seconds(1.0));
            clock.Accumulate(Seconds(0.5));
            Assert.That(clock.UnsimulatedSeconds, Is.Zero);
            clock.Accumulate(Seconds(0.5 + 0.5 / 45.0));
            Assert.That(clock.UnsimulatedSeconds, Is.EqualTo(0.5 / 45.0).Within(Tick), "counted from where it stood (the backward point)");
        }
    }
}
