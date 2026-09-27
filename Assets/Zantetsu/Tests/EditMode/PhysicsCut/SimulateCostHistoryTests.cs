using System;
using NUnit.Framework;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The expected cost of the next manual physics step (DESIGN 4.4) as the median of the last five real simulations,
    /// with the decision it feeds (<see cref="ManualPhysicsClock.ShouldStep"/>): what the median fixes -- one or two long
    /// simulations among short ones no longer stop the steps for good -- and what it leaves as it was -- a long first
    /// simulation, or long ones making up most of the last five, still hold the steps back, and nothing that does not
    /// simulate changes the prediction.
    /// </summary>
    public sealed class SimulateCostHistoryTests
    {
        private const double Budget = 1.0 / 90.0;
        private const double Ms = 0.001;
        private const long TicksPerSecond = 1_000_000;

        // The remaining Main budget at the decision in these cases: the whole budget less 2 ms already spent in the
        // frame. The real value varies per frame and was not logged in u1; any value from 0.62 ms up gives the same
        // answers below, and none can reach 17.4 ms (the remaining budget never exceeds the budget).
        private const double Remaining = Budget - 2.0 * Ms;

        [Test]
        public void BeforeAnySimulation_TheExpectedCostIsZero_AsTheLastSampleWasBefore()
        {
            var history = new SimulateCostHistory();
            Assert.That(history.Count, Is.Zero);
            Assert.That(history.ExpectedSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void FewerThanFive_UseWhatThereIs_AndAnEvenCountAveragesTheMiddleTwo()
        {
            var history = new SimulateCostHistory();
            history.Add(3.0 * Ms);
            Assert.That(history.ExpectedSeconds, Is.EqualTo(3.0 * Ms).Within(1e-12), "one sample");
            history.Add(1.0 * Ms);
            Assert.That(history.ExpectedSeconds, Is.EqualTo(2.0 * Ms).Within(1e-12), "two: the mean of both");
            history.Add(9.0 * Ms);
            Assert.That(history.ExpectedSeconds, Is.EqualTo(3.0 * Ms).Within(1e-12), "three: the middle one");
            history.Add(5.0 * Ms);
            Assert.That(history.ExpectedSeconds, Is.EqualTo(4.0 * Ms).Within(1e-12), "four: the mean of 3 and 5");
        }

        [Test]
        public void OnlyTheLastFiveCount()
        {
            var history = new SimulateCostHistory();
            foreach (double ms in new[] { 20.0, 20.0, 20.0, 1.0, 1.0, 1.0, 1.0, 1.0 })
            {
                history.Add(ms * Ms);
            }

            Assert.That(history.Count, Is.EqualTo(SimulateCostHistory.Capacity));
            Assert.That(history.ExpectedSeconds, Is.EqualTo(1.0 * Ms).Within(1e-12), "the three long ones have left the window");
        }

        [TestCase(new[] { 0.1, 0.1, 0.1, 0.1, 17.4 })]
        [TestCase(new[] { 0.1, 0.1, 0.1, 6.7, 17.4 })]
        [TestCase(new[] { 0.1, 17.4, 0.1, 6.7, 0.1 })]
        public void OneOrTwoLongSimulationsAmongShortOnes_DoNotHoldTheStepsBack(double[] samplesMs)
        {
            ManualPhysicsClock clock = OwingClock();
            var history = new SimulateCostHistory();
            foreach (double ms in samplesMs) history.Add(ms * Ms);

            Assert.That(clock.ShouldStep(history.ExpectedSeconds, Remaining), Is.True,
                "the median " + history.ExpectedSeconds / Ms + " ms fits");
            double last = samplesMs[samplesMs.Length - 1] * Ms;
            if (last > Remaining)
            {
                Assert.That(clock.ShouldStep(last, Remaining), Is.False, "where the last sample alone would not have");
            }
        }

        [Test]
        public void ALongFirstSimulation_StillHoldsTheStepsBack_AndRefusalsDoNotLowerIt()
        {
            ManualPhysicsClock clock = OwingClock();
            var history = new SimulateCostHistory();
            history.Add(17.4 * Ms);
            for (int frame = 0; frame < 200; frame++)
            {
                clock.Accumulate(TicksAt(frame + 2));
                // The product's order: decide, and add a sample only after a real simulation.
                bool step = clock.ShouldStep(history.ExpectedSeconds, Remaining);
                Assert.That(step, Is.False, "frame " + frame);
            }

            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(history.ExpectedSeconds, Is.EqualTo(17.4 * Ms).Within(1e-12), "refusing never lowered the prediction");
            Assert.That(clock.UnsimulatedSeconds, Is.GreaterThan(1.0), "and the time owed kept growing");
        }

        [TestCase(new[] { 0.1, 0.1, 15.0, 16.0, 17.4 })]
        [TestCase(new[] { 12.0, 0.1, 13.0, 0.1, 14.0 })]
        public void LongSimulationsMakingUpMostOfTheLastFive_StillHoldTheStepsBack(double[] samplesMs)
        {
            ManualPhysicsClock clock = OwingClock();
            var history = new SimulateCostHistory();
            foreach (double ms in samplesMs) history.Add(ms * Ms);

            Assert.That(clock.ShouldStep(history.ExpectedSeconds, Remaining), Is.False,
                "the median " + history.ExpectedSeconds / Ms + " ms does not fit, and without a step nothing changes it");
        }

        [Test]
        public void WhilePaused_NothingSteps_WhateverThePrediction()
        {
            ManualPhysicsClock clock = OwingClock();
            var history = new SimulateCostHistory();
            history.Add(0.1 * Ms);
            clock.SetPaused(true);
            Assert.That(clock.ShouldStep(history.ExpectedSeconds, Remaining), Is.False);
            clock.SetPaused(false);
            Assert.That(clock.ShouldStep(history.ExpectedSeconds, Remaining), Is.True, "and the time owed before the pause is still there");
        }

        /// <summary>
        /// The u1 run's (UncutNpcStereo, non-XR U9, 2026-09-27) last twelve measured simulations before its stop, in order
        /// (the profiler marker of each real step, frames 817..839). Given to the decision rule, not a replay of that
        /// physics: after the 17.376 ms sample the last-sample rule refuses every later frame, the median rule does not.
        /// </summary>
        [Test]
        public void TheU1CostColumn_DoesNotFixTheStopOnThe17MillisecondSample()
        {
            double[] u1Ms = { 0.1091, 0.0962, 0.0784, 0.1154, 0.1045, 0.0957, 0.1057, 0.1048, 0.3360, 6.6955, 0.6165, 17.3760 };
            ManualPhysicsClock clock = OwingClock();
            var history = new SimulateCostHistory();
            foreach (double ms in u1Ms) history.Add(ms * Ms);

            Assert.That(clock.ShouldStep(u1Ms[u1Ms.Length - 1] * Ms, Remaining), Is.False, "the last-sample rule: refused, and for good");
            Assert.That(history.ExpectedSeconds, Is.EqualTo(0.6165 * Ms).Within(1e-9), "the median of 0.1048, 0.3360, 6.6955, 0.6165, 17.3760");
            Assert.That(clock.ShouldStep(history.ExpectedSeconds, Remaining), Is.True, "the median rule: the next frame may step");
        }

        [Test]
        public void AddingAndReadingAllocateNothing()
        {
            var history = new SimulateCostHistory();
            history.Add(1.0 * Ms);
            _ = history.ExpectedSeconds;
            long before = GC.GetAllocatedBytesForCurrentThread();
            double sum = 0;
            for (int i = 0; i < 10_000; i++)
            {
                history.Add((i % 7) * Ms);
                sum += history.ExpectedSeconds;
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.That(after - before, Is.Zero, "bytes allocated over 10,000 samples (sum " + sum + ")");
        }

        [Test]
        public void AMeasurementIsFiniteAndNotNegative()
        {
            var history = new SimulateCostHistory();
            Assert.Throws<ArgumentOutOfRangeException>(() => history.Add(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => history.Add(-1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => history.Add(double.PositiveInfinity));
        }

        private static ManualPhysicsClock OwingClock()
        {
            var clock = new ManualPhysicsClock(45, TicksPerSecond);
            clock.Accumulate(TicksAt(0));
            clock.Accumulate(TicksAt(1));
            Assert.That(clock.UnsimulatedSeconds, Is.GreaterThanOrEqualTo(clock.StepSeconds), "a whole step is owed");
            return clock;
        }

        // Frames 1/30 s apart, so every frame owes more than one 45 Hz step.
        private static long TicksAt(int frame)
        {
            return frame * TicksPerSecond * 3 / 90;
        }
    }
}
