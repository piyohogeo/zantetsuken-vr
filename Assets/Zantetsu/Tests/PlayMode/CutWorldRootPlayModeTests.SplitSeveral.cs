using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// SPLIT-CANDIDATES-2: several attempts of the additional split out at once -- independent pieces issued together and no piece
    /// twice; a piece re-cut while its attempt is out ended as stale without the others losing their publication; the counts that
    /// hold further issues back (the attempts out, the bytes held); and a world that ends with attempts out.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator SplitSeveral_IndependentPiecesAreOutTogether_AStaleOneIsDropped_AndTheOthersArePublished()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool
                    ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(8)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.MaxAttempts = 4;
            var lowers = new LogicalFragmentId[3];
            for (int k = 0; k < 3; k++)
            {
                LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f * k), true, handLowX: 0.8f, trunkBottomBone: 1);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                lowers[k] = sides[1];
            }

            Assert.That(split.Issued, Is.EqualTo(0), "closed: nothing issued while the budget is none");

            // 1. Three independent pieces: their attempts are out together, each piece once.
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.AttemptsOut >= 3, "three attempts are out at once");
            remaining[0] = 0.0;
            Assert.That(split.BoneIssued, Is.EqualTo(3), "one attempt a piece");
            Assert.That(split.DuplicateIssues, Is.EqualTo(0));
            Assert.That(split.MostAttemptsOut, Is.GreaterThanOrEqualTo(3));
            Assert.That(split.AttemptsHeldBytes, Is.GreaterThan(0), "each holds its blocks");
            for (int k = 0; k < 4; k++) yield return null;
            Assert.That(split.BoneIssued, Is.EqualTo(3), "a piece with an attempt out is not issued again");

            // 2. One of them is cut again while its attempt is out: stale when it comes back; the other two are published all the same.
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lowers[0], new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the re-cut's record is over");
            background.HoldEverything = false;
            background.ReleaseEverything();
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.Stale) >= 1 && split.PublishedBone >= 2, "the stale one was dropped and the two others published");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(1), "only the piece that was cut again");
            Assert.That(split.LightEnds, Is.GreaterThanOrEqualTo(1), "ended in the light stage, not behind a publication");
            Assert.That(root.Ledger.IsCurrentTarget(lowers[1]) || root.Ledger.IsCurrentTarget(lowers[2]), Is.False, "the two pieces were replaced by their children");
            Assert.That(split.DuplicateIssues, Is.EqualTo(0));
            yield return Until(() => !split.HasAttempt, "nothing is out for a moment");
            Assert.That(split.AttemptsHeldBytes, Is.EqualTo(0), "nothing held with nothing out");
            TestContext.Out.WriteLine(split.DescribeSeveral());
        }

        [UnityTest]
        public IEnumerator SplitSeveral_TheCountsHoldFurtherIssuesBack_AndAWorldEndsWithAttemptsOut()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool
                    ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(8)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            for (int k = 0; k < 4; k++)
            {
                LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f * k), true, handLowX: 0.8f, trunkBottomBone: 1);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
            }

            // the attempts out: no more than the limit, however many pieces wait
            split.MaxAttempts = 2;
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.AttemptsOut == 2, "two attempts are out");
            for (int k = 0; k < 5; k++) yield return null;
            Assert.That(split.AttemptsOut == 2 && split.BoneIssued == 2 && split.MostAttemptsOut == 2, Is.True, "held at two: " + split.AttemptsOut + " out, " + split.BoneIssued + " issued");
            Assert.That(split.IssuesHeldByCount, Is.GreaterThan(0));

            // the bytes held: with the limit under what is held, nothing more is issued though the count would allow it
            split.MaxAttempts = 8;
            split.MaxHeldBytes = 1;
            long heldByBytes = split.IssuesHeldByBytes;
            for (int k = 0; k < 5; k++) yield return null;
            Assert.That(split.BoneIssued, Is.EqualTo(2), "no further issue while the attempts out hold more than the limit");
            Assert.That(split.IssuesHeldByBytes, Is.GreaterThan(heldByBytes));

            // the Main budget: none left, nothing issued though the counts allow
            split.MaxHeldBytes = CutSpatialSplit.EvaluationMaxHeldBytes;
            remaining[0] = 0.0;
            for (int k = 0; k < 5; k++) yield return null;
            Assert.That(split.BoneIssued, Is.EqualTo(2), "no issue without the budget");
            remaining[0] = 1.0;
            yield return Until(() => split.AttemptsOut >= 3, "with the budget and the counts, more are issued");
            TestContext.Out.WriteLine(split.DescribeSeveral());

            // The world ends here with attempts out on the (holding) executor: the fixture's ending gives everything back.
        }
    }
}
