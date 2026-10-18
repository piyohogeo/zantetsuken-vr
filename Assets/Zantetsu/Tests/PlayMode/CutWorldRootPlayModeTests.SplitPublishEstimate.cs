using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// D-245 (TL 2026-10-18): a publication's measured costs stand in for the seeds only once five were measured, and a
    /// publication whose measured estimate could never fit the frame's whole budget is let through once an attempt,
    /// past the budget, after a long hold. Seen on the Quest 3S: the session's first publication took 6.85 ms; as the
    /// only sample it made the threshold (2 + 2 x 7.16 ms) larger than the whole 11.1 ms budget, and no publication
    /// went through for the rest of the session. These cases use the plain split (no convex cut) of the body with a
    /// detached hand; its seeds are 1.5 ms + 1 ms, so its threshold under them is 2 + 2 x 2.5 = 7 ms.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const double WholeBudgetForCases = 1.0 / 90.0;

        // A world with two bodies, each cut at the waist, the split closed; the case gives the budget.
        private IEnumerator PlainSplitCaseWorld(System.Action<CutWorldRoot, CutSpatialSplit, double[], LogicalFragmentId, LogicalFragmentId> take)
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.VerificationStepMayCome = () => false;
            split.MainBudgetSeconds = () => WholeBudgetForCases;
            LogicalFragmentId first = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId second = AddBodyWithAHand(root, new Vector3(0f, 0f, 12f));
            yield return null;
            var firstSides = new LogicalFragmentId[2];
            var secondSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, first, firstSides);
            yield return CutAtTheWaist(root, second, secondSides);
            take(root, split, remaining, firstSides[1], secondSides[1]);
        }

        /// <summary>
        /// **Fewer than five measured, the seeds stand: one heavy measured publication does not hold the next.** With
        /// one 6.85 ms publication measured (as on the Quest) the estimate is still the seeds', so under 9 ms a frame
        /// (more than the seeds' 7 ms, far less than 2 + 2 x 7.16 ms) the next publication goes through by its ordinary
        /// comparison, nothing retried. With five measured, the estimate is their median.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitPublish_OneHeavyMeasurement_TheSeedsStandUntilFive_AndTheNextPublicationGoesThrough()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId a = default, b = default;
            yield return PlainSplitCaseWorld((r, s, m, x, y) => { root = r; split = s; remaining = m; a = x; b = y; });
            double seeds = SplitOn().seedPublishSeconds + CutSpatialSplit.SeedCollectAfterSeconds;
            Assert.That(split.ExpectedPublishSeconds, Is.EqualTo(seeds).Within(1e-12), "the layout: nothing measured, the seeds");

            split.AddPublicationSamplesForTests(false, 0.00685, 0.000314, 1);
            Assert.That(split.PublishSamples, Is.EqualTo(1));
            Assert.That(split.ExpectedPublishSeconds, Is.EqualTo(seeds).Within(1e-12), "one of five measured: the seeds still stand");

            remaining[0] = 0.009;
            yield return Until(() => split.Published == 1, "the next publication, under 9 ms a frame");
            remaining[0] = 0.0;   // nothing more published here; the collection after it is read by the next turn
            yield return null;
            yield return null;
            Assert.That(split.Published, Is.EqualTo(1));
            Assert.That(split.PublishRetries, Is.Zero, "by its ordinary comparison");
            Assert.That(split.PublishSamples, Is.EqualTo(2), "and it is measured");
            Assert.That(split.CollectAfterSamples, Is.EqualTo(2), "the collection after it too");
            Assert.That(split.ExpectedPublishSeconds, Is.EqualTo(seeds).Within(1e-12), "two of five: the seeds still stand");

            // Five measured: their median, the collection after them as well.
            split.AddPublicationSamplesForTests(false, 0.001, 0.0002, 3);
            Assert.That(split.PublishSamples, Is.EqualTo(5));
            Assert.That(split.CollectAfterSamples, Is.EqualTo(5), "the publication's own collection and the three given");
            Assert.That(split.ExpectedPublishSeconds, Is.LessThan(seeds), "five measured: the median, not the seeds");
            Assert.That(root.GeometryFaults, Is.Zero);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A measured estimate that could never fit the whole budget is retried once an attempt, after the long hold,
        /// and measured again.** Five measured publications of 7 ms (and 0.3 ms collections) make the threshold
        /// 2 + 2 x 7.3 = 16.6 ms: even with the whole 11.1 ms frame left it is held. After 20 budget-short comparisons and
        /// 0.4 s of play (the case's own, shortened) it is published past the budget, said as "never fits", and its
        /// measurement goes into the five.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitPublish_AMeasuredEstimateThatCanNeverFit_IsRetriedOnceAnAttempt_AndMeasuredAgain()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId a = default, b = default;
            yield return PlainSplitCaseWorld((r, s, m, x, y) => { root = r; split = s; remaining = m; a = x; b = y; });
            split.AddPublicationSamplesForTests(false, 0.007, 0.0003, 5);
            Assert.That(split.ExpectedPublishSeconds, Is.EqualTo(0.0073).Within(1e-12), "the layout: five measured, their median");
            split.FrameSeconds = () => 0.025;
            split.RetryAfterHolds = 20;
            split.RetryAfterHeldSeconds = 0.4;
            remaining[0] = WholeBudgetForCases;   // the whole frame left, every frame
            yield return Until(() => split.HasAttempt && split.LastBudgetDecision.Stage == SpatialSplitBudgetStage.Publish && split.LastBudgetDecision.Held,
                "an attempt held at its publication with the whole budget left");
            Assert.That(split.LastBudgetDecision.Threshold, Is.GreaterThan(WholeBudgetForCases), "the layout: its threshold is above the whole budget");
            long serial = split.AttemptSerial;

            yield return Until(() => split.PublishRetries == 1, "the retry");
            SpatialSplitPublishRetry retry = split.LastRetry;
            TestContext.Out.WriteLine(retry.Describe());
            Assert.That(retry.Attempt, Is.EqualTo(serial));
            Assert.That(retry.Reason, Is.EqualTo(SpatialSplitRetryReason.NeverFits));
            Assert.That(retry.Cut, Is.False);
            Assert.That(retry.SamplesBefore, Is.EqualTo(5));
            Assert.That(retry.Holds, Is.EqualTo(20), "after the comparisons asked for, not before");
            Assert.That(retry.Overridden.Held && retry.Overridden.Stage == SpatialSplitBudgetStage.Publish, Is.True);
            Assert.That(split.Published, Is.EqualTo(1), "published in the retry's turn");
            Assert.That(retry.PublishSeconds, Is.GreaterThanOrEqualTo(0.0), "and measured");
            Assert.That(retry.Describe(), Does.Contain("not cutting convexes").And.Contain("could not fit the whole budget"));
            Assert.That(split.PublishSamples, Is.EqualTo(5), "the five kept: the oldest gave way to it");

            // The other body's attempt is its own: held again by the same estimate, retried once in its turn.
            yield return Until(() => split.PublishRetries == 2 || split.Published == 2, "the other body's publication");
            Assert.That(split.Published, Is.EqualTo(2));
            Assert.That(split.LastRetry.Attempt, Is.Not.EqualTo(serial), "a retry once an attempt");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **What could fit a frame is the budget's own answer: never retried.** Five measured publications of 3 ms
        /// (threshold 2 + 2 x 3.3 = 8.6 ms, under the whole budget) held under 8 ms a frame, and the seeds of a plain
        /// publication (7 ms) held under 6 ms: neither is retried, however long.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitPublish_AnEstimateThatCouldFit_OrThePlainSeeds_AreNeverRetried()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId a = default, b = default;
            yield return PlainSplitCaseWorld((r, s, m, x, y) => { root = r; split = s; remaining = m; a = x; b = y; });
            split.FrameSeconds = () => 0.025;
            split.RetryAfterHolds = 20;
            split.RetryAfterHeldSeconds = 0.4;

            // The plain seeds (nothing measured), held under 6 ms.
            remaining[0] = 0.006;
            yield return Until(() => split.HasAttempt && split.LastBudgetDecision.Stage == SpatialSplitBudgetStage.Publish && split.LastBudgetDecision.Held,
                "held at its publication under the seeds");
            int shortBefore = split.HeldFor(SpatialSplitHold.BudgetShort);
            yield return Until(() => split.HeldFor(SpatialSplitHold.BudgetShort) >= shortBefore + 100, "100 more budget-short comparisons");
            Assert.That(split.PublishRetries, Is.Zero, "the plain seeds fit a frame: not retried");
            Assert.That(split.Published, Is.Zero);

            // Five measured of 3 ms, held under 8 ms.
            split.AddPublicationSamplesForTests(false, 0.003, 0.0003, 5);
            remaining[0] = 0.008;
            shortBefore = split.HeldFor(SpatialSplitHold.BudgetShort);
            yield return Until(() => split.HeldFor(SpatialSplitHold.BudgetShort) >= shortBefore + 100, "100 more budget-short comparisons");
            Assert.That(split.LastBudgetDecision.Threshold, Is.EqualTo(0.0086).Within(1e-12));
            Assert.That(split.PublishRetries, Is.Zero, "an estimate that could fit a frame: not retried");
            Assert.That(split.Published, Is.Zero);

            // Room enough: published by the ordinary comparison.
            remaining[0] = 0.009;
            yield return Until(() => split.Published == 1, "published once the frame holds its threshold");
            Assert.That(split.PublishRetries, Is.Zero);
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
