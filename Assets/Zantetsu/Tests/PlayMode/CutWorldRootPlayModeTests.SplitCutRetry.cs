using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The retry of an unmeasured cut publication (unit 10-B, 2026-10-18: a measure against a long hold by an unmeasured
    /// budget comparison; not a statement of the Quest's v32 cause). A cut split's publication held short of budget
    /// while its cost is the seeds' -- until five were measured (D-245) -- is let through once an attempt, after enough
    /// budget-short comparisons and play time, so that it is measured; no other stage or reason of a hold is bypassed,
    /// and not in a frame whose physics decision may take its verification step. Each case gives 7 ms of budget a
    /// frame: the meshes (3 ms) and the hulls (2.4 ms) go through, the unmeasured publication (2 x 4 ms) does not.
    /// (9 ms and 2 + 2 x 4 ms before 2026-10-19, when the split's reserve of 2 ms became the shared budget's common
    /// margin: the same layout.) The plain publications and the estimates that could never fit are SplitPublishEstimate's.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const double RetryCaseBudget = 0.007;

        // A world with the wrapped body cut at the waist and a second body (with a hand) beside it; the split closed.
        private IEnumerator RetryCaseWorld(System.Action<CutWorldRoot, CutSpatialSplit, double[], LogicalFragmentId, LogicalFragmentId> take)
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            split.VerificationStepMayCome = () => false;
            LogicalFragmentId wrapped = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId other = AddBodyWithAHand(root, new Vector3(8f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, wrapped, sides, 1);
            take(root, split, remaining, sides[1], other);
        }

        private static IEnumerator UntilHeldAtPublication(CutSpatialSplit split)
        {
            yield return Until(
                () => split.AttemptCutsConvexes && split.HullsChecked == 2 && split.LastBudgetDecision.Stage == SpatialSplitBudgetStage.CutPublish,
                "the cut attempt reached its publication");
        }

        /// <summary>
        /// **Short of budget every frame, the unmeasured publication is retried once -- after 180 budget-short
        /// comparisons and 4 s of play, not before -- and it is published and measured, the collection after it too;
        /// the later split follows.** Play time is given as 25 ms a frame (180 x 25 ms = 4.5 s).
        /// </summary>
        [UnityTest]
        public IEnumerator SplitRetry_ShortOfBudgetEveryFrame_TheUnmeasuredPublicationIsRetriedOnce_MeasuredAndTheLaterSplitFollows()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId lower = default, other = default;
            yield return RetryCaseWorld((r, s, b, l, o) => { root = r; split = s; remaining = b; lower = l; other = o; });
            split.FrameSeconds = () => 0.025;
            Assert.That(split.RetryAfterHolds, Is.EqualTo(CutSpatialSplit.DefaultRetryAfterHolds).And.EqualTo(180));
            Assert.That(split.RetryAfterHeldSeconds, Is.EqualTo(CutSpatialSplit.DefaultRetryAfterHeldSeconds).And.EqualTo(4.0));
            remaining[0] = RetryCaseBudget;
            yield return UntilHeldAtPublication(split);
            long serial = split.AttemptSerial;
            Assert.That(split.CutPublishMeasured, Is.False, "the layout: unmeasured");

            yield return Until(() => split.PublishRetries == 1 || split.CurrentHoldStreak.Holds > 400, "the retry");
            Assert.That(split.PublishRetries, Is.EqualTo(1));
            SpatialSplitPublishRetry retry = split.LastRetry;
            TestContext.Out.WriteLine(retry.Describe());
            Assert.That(retry.Attempt, Is.EqualTo(serial));
            Assert.That(retry.Holds, Is.EqualTo(180), "not before 180 budget-short comparisons (4.5 s by then: the time was met first)");
            Assert.That(retry.HeldSeconds, Is.EqualTo(180 * 0.025).Within(1e-9));
            Assert.That(retry.Overridden.Held && retry.Overridden.Stage == SpatialSplitBudgetStage.CutPublish, Is.True, "it overrode a held comparison at the publication");
            Assert.That(retry.Overridden.Remaining, Is.EqualTo(RetryCaseBudget).Within(1e-12));
            Assert.That(retry.Reason == SpatialSplitRetryReason.Seeded && retry.Cut && retry.SamplesBefore == 0, Is.True, "a cut publication on the seeds, none measured");
            Assert.That(split.Published, Is.EqualTo(1), "published in the retry's turn");
            Assert.That(split.PublishedWithCut, Is.EqualTo(1));
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.CutPublishMeasured, Is.True, "the publication is measured now");
            Assert.That(retry.PublishSeconds, Is.GreaterThanOrEqualTo(0.0), "its turn's time was kept");

            // The size of what it published, as kept.
            Assert.That(retry.Convexes, Is.EqualTo(1), "the wrapped body's lower side: one convex");
            Assert.That(retry.ConvexesCut, Is.EqualTo(1));
            Assert.That(retry.Triangles, Is.GreaterThan(0));
            Assert.That(retry.PositiveTriangles + retry.NegativeTriangles, Is.EqualTo(retry.Triangles), "every triangle on a side");
            Assert.That(retry.DisplayShown, Is.GreaterThan(0));
            Assert.That(retry.FrameBeforeSeconds, Is.EqualTo(0.025).Within(1e-12), "the frame before it, as this case gives play time");

            // The collection after it is read by the next turn, and the frame after it by the one after.
            yield return null;
            yield return null;
            yield return null;
            Assert.That(split.CutCollectAfterMeasured, Is.True, "the collection after it is measured too");
            Assert.That(split.LastRetry.CollectAfterMissed, Is.False);
            Assert.That(double.IsNaN(split.LastRetry.CollectAfterSeconds), Is.False);
            Assert.That(double.IsNaN(split.LastRetry.FrameSeconds) || double.IsNaN(split.LastRetry.FrameAfterSeconds), Is.False, "its own frame's length and the next one's were read");
            Assert.That(split.LastRetry.FrameBeforeOf, Is.EqualTo(retry.Frame - 1), "the frames named: the one before");
            Assert.That(split.LastRetry.FrameOf, Is.EqualTo(retry.Frame), "the retry's own");
            Assert.That(split.LastRetry.FrameAfterOf, Is.EqualTo(retry.Frame + 1), "the one after: consecutive");
            Assert.That(split.LastRetry.Describe(), Does.Contain("size: convexes 1 (cut 1)").And.Contain("frames: before frame " + (retry.Frame - 1) + " 25.000 ms").And.Contain(", its own frame " + retry.Frame + " "));
            Assert.That(split.CutPublishSamples, Is.EqualTo(1));
            Assert.That(split.ExpectedCutPublishSeconds, Is.EqualTo(0.004).Within(1e-12), "one of five measured: the seeds still stand (D-245)");

            // The later split follows, and nothing is retried again.
            var otherSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, other, otherSides);
            yield return Until(() => !root.Ledger.IsCurrentTarget(otherSides[1]), "the other body's lower side was split");
            Assert.That(split.PublishRetries, Is.EqualTo(1), "one retry in all");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The two conditions are both needed, and holds for other reasons count for neither.** With heavy work noted
        /// in every frame the publication is held for that, not for the budget: no budget-short comparison counts and
        /// nothing is retried. Then with play time of 0 a frame, 180 budget-short comparisons pass and nothing is
        /// retried; with 100 ms a frame (40 frames make 4 s) nothing is retried before the 180th comparison either.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitRetry_OtherHolds_AndOneConditionAlone_RetryNothing()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId lower = default, other = default;
            yield return RetryCaseWorld((r, s, b, l, o) => { root = r; split = s; remaining = b; lower = l; other = o; });
            split.FrameSeconds = () => 0.025;
            remaining[0] = RetryCaseBudget;
            yield return UntilHeldAtPublication(split);
            int budgetShortBefore = split.HeldFor(SpatialSplitHold.BudgetShort);

            // Heavy work in every frame: held for it, before any budget comparison.
            for (int i = 0; i < 300; i++)
            {
                CutWorldFrameNotes.Note(CutWorldFrameNotes.Work.NpcRefill);
                yield return null;
            }

            Assert.That(split.HeldFor(SpatialSplitHold.HeavyWork), Is.GreaterThanOrEqualTo(250), "held for the heavy work");
            Assert.That(split.HeldFor(SpatialSplitHold.BudgetShort), Is.EqualTo(budgetShortBefore), "and not for the budget");
            Assert.That(split.PublishRetries, Is.Zero, "another reason of a hold is never bypassed");
            Assert.That(split.Published, Is.Zero);

            // No play time: the count alone is not enough.
            split.FrameSeconds = () => 0.0;
            yield return Until(() => split.HeldFor(SpatialSplitHold.BudgetShort) >= budgetShortBefore + 200, "200 more budget-short comparisons with no play time");
            Assert.That(split.PublishRetries, Is.Zero, "the time alone was not met");

            // Plenty of time a frame (clamped to 0.1 s): the time is met at once, the count is still short.
            split.RetryAfterHolds = split.HeldFor(SpatialSplitHold.BudgetShort) - budgetShortBefore + 50;   // 50 comparisons from here
            split.FrameSeconds = () => 10.0;   // a frame after a sleep: counted as 0.1 s at most
            for (int i = 0; i < 40; i++)
            {
                yield return null;
            }

            Assert.That(split.PublishRetries, Is.Zero, "the count alone was not met");
            yield return Until(() => split.PublishRetries == 1, "the retry once both are met");
            Assert.That(split.LastRetry.HeldSeconds, Is.LessThanOrEqualTo(4.0 + (split.LastRetry.Holds * CutSpatialSplit.MaxHeldFrameSeconds)), "a long frame counts as 0.1 s at most");
            Assert.That(split.Published, Is.EqualTo(1));
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Not in a frame whose physics decision may take its verification step, and once an attempt.** While the
        /// physics' verification step may come in the frame, the retry is put off frame after frame; once it may not,
        /// it is taken once; the attempt is never retried again.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitRetry_PutOffForThePhysicsVerificationStep_AndTakenOnceAnAttempt()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId lower = default, other = default;
            yield return RetryCaseWorld((r, s, b, l, o) => { root = r; split = s; remaining = b; lower = l; other = o; });
            split.FrameSeconds = () => 0.025;
            split.RetryAfterHolds = 20;
            split.RetryAfterHeldSeconds = 0.4;
            bool verification = true;
            split.VerificationStepMayCome = () => verification;
            remaining[0] = RetryCaseBudget;
            yield return UntilHeldAtPublication(split);
            yield return Until(() => split.PublishRetriesPutOff >= 30, "put off for the physics verification step");
            Assert.That(split.PublishRetries, Is.Zero);
            Assert.That(split.Published, Is.Zero);

            verification = false;
            yield return Until(() => split.PublishRetries == 1, "taken once the step may not come");
            int putOff = split.PublishRetriesPutOff;
            for (int i = 0; i < 30; i++)
            {
                yield return null;
            }

            Assert.That(split.PublishRetries, Is.EqualTo(1), "once an attempt");
            Assert.That(split.PublishRetriesPutOff, Is.EqualTo(putOff), "nothing more to put off");
            Assert.That(split.Published, Is.EqualTo(1));
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A collection after the retried publication that is not read leaves its seed, and says so.** The game is
        /// paused in the frame after the retry: no turn reads that collection; it is counted as not read, the retry
        /// says so, and the collection's estimate is still the seed (the publication's own is measured).
        /// </summary>
        [UnityTest]
        public IEnumerator SplitRetry_TheCollectionAfterItNotRead_LeavesItsSeed_AndSaysSo()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId lower = default, other = default;
            yield return RetryCaseWorld((r, s, b, l, o) => { root = r; split = s; remaining = b; lower = l; other = o; });
            split.FrameSeconds = () => 0.025;
            split.RetryAfterHolds = 20;
            split.RetryAfterHeldSeconds = 0.4;
            remaining[0] = RetryCaseBudget;
            yield return UntilHeldAtPublication(split);
            yield return Until(() => split.PublishRetries == 1, "the retry");
            try
            {
                Zantetsu.Core.GamePause.SetManual(true);   // the frame after: no turn
                yield return null;
                yield return null;
            }
            finally
            {
                Zantetsu.Core.GamePause.SetManual(false);
            }

            yield return null;
            yield return null;
            Assert.That(split.CutPublishMeasured, Is.True, "the publication itself is measured");
            Assert.That(split.CutCollectAfterMeasured, Is.False, "the collection after it is not");
            Assert.That(split.CollectionsAfterPublicationMissed, Is.GreaterThanOrEqualTo(1));
            Assert.That(split.LastRetry.CollectAfterMissed, Is.True, "and the retry says so");
            Assert.That(split.LastRetry.Describe(), Does.Contain("NOT READ (its seed stands)"));
            Assert.That(split.LastRetry.FrameOf == int.MinValue && split.LastRetry.FrameAfterOf == int.MinValue, Is.True, "the paused frames' lengths were not read: missing, not guessed");
            Assert.That(split.LastRetry.Describe(), Does.Contain("its own not read, after not read"));
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>The device check's argument: two numbers after it, or it is refused and the product's stand.</summary>
        [Test]
        public void SplitRetry_TheArgument_GivesBothConditions_OrIsRefused()
        {
            string a = CutWorldRoot.SpatialSplitRetryAfterArgument;
            Assert.That(CutWorldRoot.TryReadRetryAfter(new[] { "p", a, "20", "0.5" }, out int h, out double s, out string refusal) && h == 20 && s == 0.5 && refusal == null, Is.True);
            Assert.That(CutWorldRoot.TryReadRetryAfter(new[] { "p" }, out _, out _, out refusal) || refusal != null, Is.False, "not given: nothing refused");
            foreach (string[] bad in new[] { new[] { "p", a }, new[] { "p", a, "20" }, new[] { "p", a, "0", "1" }, new[] { "p", a, "20", "-1" }, new[] { "p", a, "x", "1" } })
            {
                Assert.That(CutWorldRoot.TryReadRetryAfter(bad, out _, out _, out refusal), Is.False, string.Join(" ", bad));
                Assert.That(refusal, Does.Contain("not followed by"), string.Join(" ", bad));
            }
        }

        /// <summary>
        /// **The diagnosis's reset (the load check's preparation, same process): after a measured cut publication, the
        /// measurement is forgotten -- the seeds stand again -- and the next cut publication held short of budget is
        /// retried, once, under the ordinary conditions.**
        /// </summary>
        [UnityTest]
        public IEnumerator SplitRetry_AfterThePreparationsMeasurementIsForgotten_TheNextCutPublicationIsRetried()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId lower = default, other = default;
            yield return RetryCaseWorld((r, s, b, l, o) => { root = r; split = s; remaining = b; lower = l; other = o; });
            LogicalFragmentId second = AddWrappedBody(root, new Vector3(-8f, 0f, 0f));
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1, "the preparation: a cut publication within budget");
            Assert.That(split.CutPublishMeasured, Is.True, "the layout: measured by the preparation");
            yield return null;
            yield return null;
            remaining[0] = 0.0;

            string forgotten = split.ForgetCutPublicationMeasurementForDiagnosis();
            TestContext.Out.WriteLine(forgotten);
            Assert.That(forgotten, Does.Contain("forgotten").And.Contain("4.000 ms (the seeds)"));
            Assert.That(split.CutPublishMeasured || split.CutCollectAfterMeasured, Is.False, "nothing measured any more");
            Assert.That(split.ExpectedCutPublishSeconds, Is.EqualTo(SplitCutOn(12).seedCutPublishSeconds + CutSpatialSplit.SeedCollectAfterSeconds).Within(1e-12), "the seeds stand again");

            // The next cut publication, held short of budget: retried once under the ordinary conditions (shortened here).
            split.FrameSeconds = () => 0.025;
            split.RetryAfterHolds = 20;
            split.RetryAfterHeldSeconds = 0.4;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, second, sides, 1);
            remaining[0] = RetryCaseBudget;
            yield return Until(() => split.PublishRetries == 1, "the retry of the next cut publication");
            Assert.That(split.LastSource, Is.EqualTo(sides[1]));
            Assert.That(split.LastRetry.Holds, Is.EqualTo(20));
            Assert.That(split.LastRetry.Overridden.Expected, Is.EqualTo(0.004).Within(1e-12), "it overrode the seeds' comparison");
            Assert.That(split.CutPublishMeasured, Is.True);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>**Within budget nothing changes: the cut split is published by its ordinary comparison, and nothing is retried or counted.**</summary>
        [UnityTest]
        public IEnumerator SplitRetry_WithinBudget_NothingChanges()
        {
            CutWorldRoot root = null; CutSpatialSplit split = null; double[] remaining = null; LogicalFragmentId lower = default, other = default;
            yield return RetryCaseWorld((r, s, b, l, o) => { root = r; split = s; remaining = b; lower = l; other = o; });
            int shortBefore = split.HeldFor(SpatialSplitHold.BudgetShort);   // the closed budget of the layout's own frames
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1, "published within budget");
            Assert.That(split.PublishedWithCut, Is.EqualTo(1));
            Assert.That(split.PublishRetries, Is.Zero);
            Assert.That(split.PublishRetriesPutOff, Is.Zero);
            Assert.That(split.LastRetry.IsSet, Is.False);
            Assert.That(split.HeldFor(SpatialSplitHold.BudgetShort), Is.EqualTo(shortBefore), "never short of budget once it was open");
            Assert.That(split.CutPublishMeasured, Is.True, "measured by its ordinary publication");
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
