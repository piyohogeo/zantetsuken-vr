using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Unit 10-B (2026-10-18): whether the stopping mechanism the Quest suggested holds in the implementation. On the
    /// device after a sleep, one convex-cut attempt was held about two minutes and the whole split stood still; on a
    /// later run one hold was recorded at the CutPublish stage with the seed estimate (3 ms + 1 ms, so a threshold of
    /// 2 + 2 x 4 = 10 ms of an 11.1 ms frame). This is NOT a proof of the device's cause: it only checks that the
    /// mechanism can occur as the code is -- nothing of the budget or the seeds is changed.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>
        /// **An unmeasured CutPublish short of budget in every frame holds the split: no publication, so no measurement
        /// and the seed stays; the attempt stays and no later split is issued. One comparison with room enough -- an
        /// equal one goes on -- lets it publish, and the later split follows.** A budget of 9 ms in every frame lets the
        /// meshes (threshold 3 ms) and the hulls (2.4 ms) through and holds the publication (10 ms). Meanwhile a second
        /// body is cut so that a plain split of it is wanted: nothing is issued for it while the cut attempt stands.
        /// The hold run is recorded with its attempt, its stage, its count and its largest margin.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_AnUnmeasuredPublication_ShortOfBudgetEveryFrame_HoldsTheSplit_AndOneFrameWithRoomLetsItGoOn()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            LogicalFragmentId wrapped = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId other = AddBodyWithAHand(root, new Vector3(8f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, wrapped, sides, 1);
            LogicalFragmentId lower = sides[1];

            double seed = SplitCutOn(12).seedCutPublishSeconds + CutSpatialSplit.SeedCollectAfterSeconds;
            Assert.That(split.ExpectedCutPublishSeconds, Is.EqualTo(seed).Within(1e-12), "the layout: the publication of a cut split is not measured yet -- the seed, 3 ms + 1 ms");
            double threshold = CutWorldRoot.CompactionReserveSeconds + (2.0 * seed);
            Assert.That(threshold, Is.EqualTo(0.010).Within(1e-12), "the layout: 2 ms + 2 x 4 ms");

            // 9 ms in every frame: the meshes and the hulls go through, the publication does not.
            const double nineMs = 0.009;
            remaining[0] = nineMs;
            yield return Until(
                () => split.AttemptCutsConvexes && split.HullsChecked == 2 && split.LastBudgetDecision.Stage == SpatialSplitBudgetStage.CutPublish && split.LastBudgetDecision.Held,
                "the cut attempt reached its publication and was held there");
            long serial = split.AttemptSerial;
            int issued = split.Issued;
            Assert.That(split.AttemptFragment, Is.EqualTo(lower));
            Assert.That(split.CutMeshesMade, Is.EqualTo(2), "the meshes were made under 9 ms");

            // A second body cut while it is held: its lower side is a plain split's candidate from now on.
            var otherSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, other, otherSides);
            Assert.That(split.Examine(otherSides[1]), Is.EqualTo(SpatialSplitPass.Candidate), "the layout: the other body's lower side is wanted");
            for (int i = 0; i < 120; i++)
            {
                yield return null;
            }

            Assert.That(split.Published, Is.Zero, "nothing was published");
            Assert.That(split.HasAttempt && split.AttemptSerial == serial && split.AttemptFragment == lower, Is.True, "the same attempt still stands");
            Assert.That(split.ExpectedCutPublishSeconds, Is.EqualTo(seed).Within(1e-12), "not published, not measured: the seed stays");
            Assert.That(split.Issued, Is.EqualTo(issued), "nothing else was issued: one attempt at a time over the world");
            Assert.That(root.Ledger.IsCurrentTarget(lower) && root.Ledger.IsCurrentTarget(otherSides[1]), Is.True, "both stand as they were");
            SpatialSplitHoldStreak run = split.CurrentHoldStreak;
            TestContext.Out.WriteLine(run.Describe());
            Assert.That(run.Attempt, Is.EqualTo(serial));
            Assert.That(run.Stage, Is.EqualTo(SpatialSplitBudgetStage.CutPublish));
            Assert.That(run.Ended, Is.False, "still going on");
            Assert.That(run.Holds, Is.GreaterThanOrEqualTo(100), "held in about every frame");
            Assert.That(run.Best.Margin, Is.EqualTo(nineMs - threshold).Within(1e-9), "its largest margin: 9 ms against 10 ms");
            Assert.That(run.Best.Remaining, Is.EqualTo(nineMs).Within(1e-12));
            Assert.That(run.Best.Threshold, Is.EqualTo(threshold).Within(1e-12));
            Assert.That(run.Best.Expected, Is.EqualTo(seed).Within(1e-12));

            // A hair short still holds; exactly the threshold goes on (the refusal is remaining < threshold).
            split.RemainingMainSeconds = () => (CutWorldRoot.CompactionReserveSeconds + 0.0) + (2.0 * split.ExpectedCutPublishSeconds) - 1e-9;
            for (int i = 0; i < 5; i++)
            {
                yield return null;
            }

            Assert.That(split.Published, Is.Zero, "a nanosecond short: still held");
            split.RemainingMainSeconds = () => (CutWorldRoot.CompactionReserveSeconds + 0.0) + (2.0 * split.ExpectedCutPublishSeconds);
            yield return Until(() => split.Published == 1, "an equal comparison let the publication through");
            split.RemainingMainSeconds = () => remaining[0];
            Assert.That(split.PublishedWithCut, Is.EqualTo(1));
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.PreviousHoldStreak.Attempt == serial && split.PreviousHoldStreak.Ended && split.PreviousHoldStreak.EndedByGoingOn, Is.True,
                "the run ended when the same attempt and stage went on");
            Assert.That(split.LongestHoldStreak.Holds, Is.GreaterThanOrEqualTo(run.Holds));
            Assert.That(split.ExpectedCutPublishSeconds, Is.Not.EqualTo(seed).Within(1e-12), "published once: measured, the seed no longer stands");

            // The later split follows under the same 9 ms (a plain publication's threshold is 2 + 2 x 2.5 ms).
            yield return Until(() => !root.Ledger.IsCurrentTarget(otherSides[1]), "the other body's lower side was split");
            Assert.That(split.Published, Is.GreaterThanOrEqualTo(2));
            Assert.That(split.Issued, Is.GreaterThan(issued));
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
