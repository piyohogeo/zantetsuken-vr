using NUnit.Framework;
using Zantetsu.PhysicsCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The runs of held budget comparisons of the spatial split (unit 10-B diagnosis, 2026-10-18): the run going on,
    /// the last ended and the longest stay consistent when the same attempt and stage go on, when the attempt or the
    /// stage changes with a comparison that goes on or holds, and when a longer run comes later. The count is of
    /// budget comparisons with none going on between, read apart from the span of frames.
    /// </summary>
    public sealed class SpatialSplitHoldRunsTests
    {
        private static SpatialSplitBudgetDecision D(int frame, SpatialSplitBudgetStage stage, double remainingMs, double expectedMs = 4.0)
        {
            double reserve = 0.002, later = 0.0005, expected = expectedMs * 0.001, remaining = remainingMs * 0.001;
            bool held = remaining < reserve + later + (2.0 * expected);
            return new SpatialSplitBudgetDecision(frame, stage, remaining, reserve, later, expected, held);
        }

        [Test]
        public void TheSameAttemptAndStage_HeldThenGoingOn_EndsTheRun_InAllThree()
        {
            var runs = new SpatialSplitHoldRuns();
            runs.Note(3, D(10, SpatialSplitBudgetStage.CutPublish, 9.0));
            runs.Note(3, D(12, SpatialSplitBudgetStage.CutPublish, 9.8));
            runs.Note(3, D(20, SpatialSplitBudgetStage.CutPublish, 9.5));
            Assert.That(runs.Current.Ended, Is.False, "going on");
            Assert.That(runs.Current.Holds, Is.EqualTo(3), "three budget comparisons held");
            Assert.That(runs.Current.FrameSpan, Is.EqualTo(11), "over frames 10..20: a span apart from the count");
            Assert.That(runs.Current.Best.Frame, Is.EqualTo(12), "the largest margin was at frame 12");
            Assert.That(runs.Current.Best.Margin, Is.EqualTo((9.8 - 10.5) * 0.001).Within(1e-12));
            Assert.That(runs.Previous.IsSet, Is.False);
            Assert.That(runs.Longest.IsSameRun(runs.Current) && !runs.Longest.Ended, Is.True, "the longest is this run, going on");

            runs.Note(3, D(21, SpatialSplitBudgetStage.CutPublish, 10.5));   // equal to the threshold: goes on
            Assert.That(runs.Current.Ended && runs.Current.EndedByGoingOn, Is.True, "an equal comparison went on: ended");
            Assert.That(runs.Current.Holds, Is.EqualTo(3));
            Assert.That(runs.Previous.IsSameRun(runs.Current) && runs.Previous.Ended && runs.Previous.EndedByGoingOn, Is.True);
            Assert.That(runs.Longest.IsSameRun(runs.Current) && runs.Longest.Ended && runs.Longest.EndedByGoingOn, Is.True, "the longest shows its ending too");

            runs.Note(3, D(22, SpatialSplitBudgetStage.CutPublish, 11.0));
            Assert.That(runs.Current.Ended && runs.Current.Holds == 3, Is.True, "a further comparison that goes on changes nothing");
        }

        [Test]
        public void AnotherStage_ThatGoesOn_EndsTheOldRun_AndNothingIsLeftGoingOn()
        {
            var runs = new SpatialSplitHoldRuns();
            runs.Note(5, D(10, SpatialSplitBudgetStage.CutApply, 1.0, 0.5));
            runs.Note(5, D(11, SpatialSplitBudgetStage.CutApply, 1.0, 0.5));
            Assume.That(runs.Current.Holds == 2 && !runs.Current.Ended, Is.True);

            runs.Note(5, D(12, SpatialSplitBudgetStage.HullCheck, 9.0, 0.2));   // goes on at the next stage
            Assert.That(runs.Current.Ended, Is.True, "the old run is not left going on");
            Assert.That(runs.Current.EndedByGoingOn, Is.False, "it ended by the change of stage");
            Assert.That(runs.Current.Stage, Is.EqualTo(SpatialSplitBudgetStage.CutApply));
            Assert.That(runs.Previous.IsSameRun(runs.Current) && runs.Previous.Ended && !runs.Previous.EndedByGoingOn, Is.True);
            Assert.That(runs.Longest.IsSameRun(runs.Current) && runs.Longest.Ended && !runs.Longest.EndedByGoingOn, Is.True, "the longest was that run: ended in it too");
        }

        [Test]
        public void AnotherStage_ThatHolds_EndsTheOldRun_AndBeginsANewOne()
        {
            var runs = new SpatialSplitHoldRuns();
            for (int f = 10; f < 15; f++) runs.Note(5, D(f, SpatialSplitBudgetStage.HullCheck, 1.0, 0.2));
            runs.Note(5, D(15, SpatialSplitBudgetStage.CutPublish, 9.0));
            Assert.That(runs.Current.Stage == SpatialSplitBudgetStage.CutPublish && runs.Current.Holds == 1 && !runs.Current.Ended, Is.True, "a new run, going on");
            Assert.That(runs.Previous.Stage == SpatialSplitBudgetStage.HullCheck && runs.Previous.Holds == 5 && runs.Previous.Ended && !runs.Previous.EndedByGoingOn, Is.True);
            Assert.That(runs.Longest.IsSameRun(runs.Previous) && runs.Longest.Ended, Is.True, "the longest is the old run, shown ended");
        }

        [Test]
        public void AnotherAttempt_EndsTheOldRun_AndALaterLongerRun_TakesTheLongestsPlace()
        {
            var runs = new SpatialSplitHoldRuns();
            runs.Note(1, D(10, SpatialSplitBudgetStage.CutPublish, 9.0));
            runs.Note(1, D(11, SpatialSplitBudgetStage.CutPublish, 9.0));
            runs.Note(0, D(30, SpatialSplitBudgetStage.Choose, 9.0, 0.1));   // no attempt: Choose, goes on
            Assert.That(runs.Current.Ended && runs.Current.Attempt == 1 && !runs.Current.EndedByGoingOn, Is.True, "attempt 1's run ended by the change");
            Assert.That(runs.Longest.Attempt == 1 && runs.Longest.Ended, Is.True);

            for (int f = 40; f < 43; f++) runs.Note(2, D(f, SpatialSplitBudgetStage.CutPublish, 9.0));
            Assert.That(runs.Current.Attempt == 2 && runs.Current.Holds == 3 && !runs.Current.Ended, Is.True);
            Assert.That(runs.Longest.IsSameRun(runs.Current) && !runs.Longest.Ended, Is.True, "the longer run is the longest now, going on");
            Assert.That(runs.Previous.Attempt == 1 && runs.Previous.Holds == 2 && runs.Previous.Ended, Is.True, "the one before stays as it ended");

            runs.Note(3, D(50, SpatialSplitBudgetStage.Publish, 9.0, 1.0));   // another attempt that goes on
            Assert.That(runs.Current.Attempt == 2 && runs.Current.Ended && !runs.Current.EndedByGoingOn, Is.True);
            Assert.That(runs.Longest.IsSameRun(runs.Current) && runs.Longest.Ended, Is.True, "the longest's ending followed it");
            Assert.That(runs.Previous.IsSameRun(runs.Current), Is.True);
        }

        [Test]
        public void TheDescription_SaysTheCount_AndTheSpanApart()
        {
            var runs = new SpatialSplitHoldRuns();
            runs.Note(7, D(100, SpatialSplitBudgetStage.CutPublish, 9.0));
            runs.Note(7, D(160, SpatialSplitBudgetStage.CutPublish, 9.2));
            string text = runs.Current.Describe();
            Assert.That(text, Does.Contain("attempt 7 at CutPublish").And.Contain("2 budget comparisons held with none going on between")
                .And.Contain("frames 100..160, a span of 61 frames").And.Contain("going on").And.Contain("largest margin -1.300 ms"));
            Assert.That(new SpatialSplitHoldRuns().Current.Describe(), Is.EqualTo("hold streak: none"));
        }
    }
}
