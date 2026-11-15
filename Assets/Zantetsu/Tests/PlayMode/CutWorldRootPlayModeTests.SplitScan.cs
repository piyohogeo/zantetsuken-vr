using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// SPLIT-CANDIDATES-2, the review of 2026-11-13: the scan of the several-attempts control -- a valid mark ends a fragment at
    /// the light look and a change of its input or of the search has it looked at again; the display's view is not taken where no
    /// look comes as far as the display; candidates are issued in the frame they are found, and those the budget leaves keep their
    /// order; a settings switch waiting stops new issues until the attempts out have ended; a bake never offered is ended when the
    /// split is switched off; and a candidate that alone needs more bytes than the limit is held with that reason.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator SplitScan_AValidMarkEndsAtTheLightLook_WithoutTheDisplaysView_AndAChangeIsLookedAtAgain()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, CutSpatialSplitSettings.DefaultMinCutSideTriangles);
            SearchAOnePieceGeometryToo();   // this case is about the searches on a one-piece side: not passed over here
            split.MaxAttempts = 4;
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId upper = sides[0], lower = sides[1];

            // Both pieces tried at the default minimum and not split: marked.
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 2 && !split.HasAttempt, "both pieces were tried and not split");
            remaining[0] = 0.0;
            Assert.That(split.MostIssuedInATurn, Is.EqualTo(2), "the two were found and issued in one turn");
            Assert.That(split.SuppressedNow, Is.EqualTo(2));
            CutSpatialSplitSettings settings = split.Settings;

            // 1. The light look: after one detailed look that read the marks, both end at their light mark -- nothing of the owner,
            //    the geometry or the display is read, and the display's view is not taken.
            split.TakeCandidateSample(in settings);
            Assert.That(split.LightMarksNow, Is.EqualTo(2), "a light mark each");
            long fills = split.ViewFills, withoutView = split.ScansWithoutView;
            CutSpatialSplit.CandidateSample lightOnly = split.TakeCandidateSample(in settings);
            Assert.That(lightOnly.owners, Is.EqualTo(2));
            Assert.That(lightOnly.lightExcluded == 2 && lightOnly.detailed == 0 && lightOnly.candidates == 0, Is.True,
                "both at the light look: " + lightOnly.lightExcluded + " light, " + lightOnly.detailed + " detailed, " + lightOnly.candidates + " candidates");
            Assert.That(lightOnly.suppressed, Is.EqualTo(2), "counted as marked");
            Assert.That(lightOnly.viewFilled || lightOnly.usedView, Is.False, "no look came as far as the display");
            Assert.That(split.ViewFills == fills && split.ScansWithoutView == withoutView + 1, Is.True, "the view was not taken");
            Assert.That(CandidatesByExamine(root, split, null), Is.Zero, "as the detailed look says of each");
            split.CheckLightMarks(in settings);
            Assert.That(split.LightChecked >= 2 && split.LightDisagreed == 0, Is.True, "the light marks agree with the detailed look");

            // 2. Another search (the minimum lowered): the marks say nothing of it, and every piece is looked at in detail again.
            split.Settings = SplitCutOn(12);
            settings = split.Settings;
            CutSpatialSplit.CandidateSample other = split.TakeCandidateSample(in settings);
            Assert.That(other.lightExcluded, Is.Zero, "no light mark stands under another search");
            Assert.That(other.detailed, Is.EqualTo(2));
            Assert.That(other.candidates, Is.EqualTo(CandidatesByExamine(root, split, null)));
            Assert.That(other.candidates, Is.GreaterThanOrEqualTo(1), "the lower side is a candidate again");
            Assert.That(other.viewFilled, Is.True, "a look came as far as the display: its view was taken then");

            // 3. Back under the first search, marked again; then one of the two is cut: its mark goes with its owner, the other's
            //    stands, and the pieces of the cut are looked at in detail.
            split.Settings = SplitCutOn(CutSpatialSplitSettings.DefaultMinCutSideTriangles);
            settings = split.Settings;
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 4 && !split.HasAttempt, "both pieces were tried again under the first search");
            remaining[0] = 0.0;
            split.TakeCandidateSample(in settings);
            Assert.That(split.LightMarksNow, Is.EqualTo(2));
            long dropped = split.LightMarksDropped;
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            yield return null;
            Assert.That(split.LightMarksDropped, Is.GreaterThan(dropped), "the cut piece's light mark went with its owner");
            CutSpatialSplit.CandidateSample afterCut = split.TakeCandidateSample(in settings);
            Assert.That(afterCut.lightExcluded, Is.EqualTo(1), "the piece that was not cut ends at its light mark still");
            Assert.That(afterCut.detailed, Is.EqualTo(afterCut.owners - 1), "the cut's pieces are looked at in detail");
            Assert.That(afterCut.candidates, Is.EqualTo(CandidatesByExamine(root, split, null)), "and counted as the detailed look says");
            split.CheckLightMarks(in settings);
            Assert.That(split.LightDisagreed, Is.Zero);
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed));
            TestContext.Out.WriteLine(split.DescribeSeveral());
            split.Settings = default;
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator SplitScan_CandidatesAreIssuedInTheFrameTheyAreFound_AndThoseTheBudgetLeavesKeepTheirOrder()
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
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.MaxAttempts = 4;
            for (int k = 0; k < 3; k++)
            {
                LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f * k), true, handLowX: 0.8f, trunkBottomBone: 1);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
            }

            Assert.That(split.Issued, Is.Zero, "closed: nothing issued");
            int candidates = CandidatesByExamine(root, split, null);
            Assert.That(candidates, Is.GreaterThanOrEqualTo(3), "the layout: a piece of each body");

            // The budget is there for one issue and gone after it: the first candidate is issued in the turn that finds the three,
            // and the others stay, in their order, with the budget as the reason.
            background.HoldEverything = true;
            int issuedAtOpen = split.Issued;
            split.RemainingMainSeconds = () => split.Issued > issuedAtOpen ? 0.0 : 1.0;
            int turns = split.Turns;
            yield return Until(() => split.Issued > issuedAtOpen, "the first candidate was issued");
            Assert.That(split.Issued, Is.EqualTo(issuedAtOpen + 1), "one issue, then the budget was gone");
            Assert.That(split.LastNoIssue, Is.EqualTo(SpatialSplitNoIssue.BudgetShort), "the others wait for the budget");
            Assert.That(split.IssuesHeldByBudget, Is.GreaterThanOrEqualTo(1));
            Assert.That(split.FoundWaiting, Is.EqualTo(candidates - 1), "found, not issued, kept");
            for (int k = 0; k < 4; k++) yield return null;
            Assert.That(split.Issued, Is.EqualTo(issuedAtOpen + 1), "nothing more without the budget");
            Assert.That(split.FoundWaiting, Is.EqualTo(candidates - 1));

            // The budget back: the candidates kept are looked at again (they were found in an earlier frame) and issued together.
            long again = split.FoundLookedAtAgain;
            int before = split.Issued;
            split.RemainingMainSeconds = () => 1.0;
            yield return Until(() => split.Issued >= before + 2, "the candidates kept were issued");
            Assert.That(split.MostIssuedInATurn, Is.GreaterThanOrEqualTo(2), "together, in one turn");
            Assert.That(split.DuplicateIssues, Is.Zero);
            Assert.That(split.FoundLookedAtAgain + split.ScansShared, Is.GreaterThanOrEqualTo(again), "kept candidates are confirmed before their issue");
            Assert.That(split.AttemptsOut, Is.GreaterThanOrEqualTo(3));
            TestContext.Out.WriteLine(split.DescribeSeveral());

            // The world ends here with attempts out on the (holding) executor: the fixture's ending gives everything back.
        }

        [UnityTest]
        public IEnumerator SplitScan_ASettingsSwitchWaiting_StopsNewIssues_UntilTheAttemptsOutHaveEnded()
        {
            ResetGraphTestHooks();
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
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.MaxAttempts = 2;
            for (int k = 0; k < 3; k++)
            {
                LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f * k), true, handLowX: 0.8f, trunkBottomBone: 1);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
            }

            // Two attempts out (the limit), a third piece waiting.
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.AttemptsOut == 2, "two attempts are out");
            int issued = split.Issued;
            Assert.That(split.GraphEffective, Is.False, "the layout: the graph stage is off");

            // The graph stage asked for while they are out: the switch waits for them. A place is made free, the budget is open and a
            // candidate waits -- and nothing is issued.
            split.Settings = SplitOnWithGraph();
            Assert.That(split.SettingsSwitchPending, Is.True, "the switch waits for the attempts out");
            split.MaxAttempts = 4;
            long heldBySettings = split.IssuesHeldBySettings;
            for (int k = 0; k < 6; k++) yield return null;
            Assert.That(split.Issued, Is.EqualTo(issued), "no new issue while the switch waits");
            Assert.That(split.LastNoIssue, Is.EqualTo(SpatialSplitNoIssue.SettingsPending));
            Assert.That(split.IssuesHeldBySettings, Is.GreaterThan(heldBySettings));
            Assert.That(split.AttemptsOut, Is.EqualTo(2), "the attempts out go on");
            Assert.That(split.GraphEffective, Is.False, "not applied yet");

            // They come back and end: the switch is applied, and the issues go on under the new settings.
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.SettingsSwitchPending, "the attempts out ended and the switch was applied");
            Assert.That(split.GraphEffective, Is.True, split.GraphRefusal);
            yield return Until(() => split.Issued > issued, "issues go on after the switch");
            Assert.That(split.DuplicateIssues, Is.Zero);
            TestContext.Out.WriteLine(split.DescribeSeveral());
            split.Settings = default;
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator SplitScan_CutShortForTheBudget_GoesOnFromThere_AndTheReasonIsTold()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.MaxAttempts = 4;
            for (int k = 0; k < 3; k++) AddBodyWithAHand(root, new Vector3(0f, 0f, 12f * k));
            yield return null;

            // A turn of the case's own with the budget it gives; the product's own turns stay closed (and scan nothing).
            SpatialSplitHold Turn(System.Func<double> budget)
            {
                split.RemainingMainSeconds = budget;
                split.Step();
                split.RemainingMainSeconds = () => remaining[0];
                return split.LastHold;
            }

            Assert.That(split.Scans, Is.Zero, "closed: no scan");

            // The budget is there for the gate and one detailed look, and gone before the second: the scan stops there.
            // (What the turn said is read right after it: the product's own turn of the next frame, closed, says its own.)
            int asked = 0;
            bool scanned = false;
            SpatialSplitNoIssue reason = SpatialSplitNoIssue.None;
            for (int attempt = 0; attempt < 8 && !scanned; attempt++)
            {
                asked = 0;
                scanned = Turn(() => asked++ == 0 ? 1.0 : 0.0) == SpatialSplitHold.None;
                reason = split.LastNoIssue;
                yield return null;
            }

            Assert.That(scanned, Is.True, "a turn scanned");
            Assert.That(split.LastScan.owners, Is.EqualTo(3));
            Assert.That(split.LastScan.detailed == 1 && !split.LastScan.complete, Is.True, "one look, and the budget was asked before the next");
            Assert.That(split.ScansCutShort == 1 && split.WalksCutShort == 1, Is.True);
            Assert.That(reason, Is.EqualTo(SpatialSplitNoIssue.ScanCutShort), "the reason: the scan is not through");
            Assert.That(split.IsIdle, Is.False, "not idle on a scan that is not whole");
            Assert.That(split.Issued, Is.Zero);

            // The next turn, with the budget: a whole scan, begun where the other stopped; nothing is a candidate (no piece is cut).
            bool whole = false;
            for (int attempt = 0; attempt < 8 && !whole; attempt++)
            {
                whole = Turn(() => 1.0) == SpatialSplitHold.None;
                reason = split.LastNoIssue;
                yield return null;
            }

            Assert.That(whole, Is.True);
            Assert.That(split.LastScan.complete && split.LastScan.detailed == 3, Is.True, "every fragment looked at");
            Assert.That(split.ScansCutShort, Is.EqualTo(1), "the budget held: not stopped again");
            Assert.That(split.PassedAs(SpatialSplitPass.NotCutMade), Is.EqualTo(4), "one look of the first scan, three of the second");
            Assert.That(reason, Is.EqualTo(SpatialSplitNoIssue.NoCandidate), "the reason: a whole round, and nothing to try");
            // The scans' Main time is in the walk's tally (the world's end reports it), and the light part within it.
            Assert.That(split.WalkSeconds, Is.GreaterThan(0.0), "the scans are counted as the walk's Main time");
            Assert.That(split.WalkSeconds, Is.EqualTo(split.ScanSeconds).Within(1e-12), "all of them");
            Assert.That(split.RoundSeconds, Is.GreaterThan(0.0).And.LessThanOrEqualTo(split.WalkSeconds));
            Assert.That(split.IsIdle, Is.True, "the whole scan left nothing to try");
            long scans = split.Scans;
            Turn(() => 1.0);
            Assert.That(split.Scans, Is.EqualTo(scans), "idle: no scan while nothing it read has changed");
            split.Settings = default;
            yield return EndWorld(root);
        }

        private IEnumerator ABakeNeverOfferedIsEndedWhenOff(int maxAttempts)
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            split.MaxAttempts = maxAttempts;
            split.TestRefuseBakeOffer = true;
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];
            int owners = root.Owners.Count;

            // The lower side's attempt cuts its convex: the meshes are made, and their bake finds no place in the queue.
            remaining[0] = 1.0;
            yield return Until(() => split.CutMeshesMade >= 2 && split.QueueFull >= 1 && split.HasAttemptFor(lower), "the meshes were made and their bake found no place");
            for (int k = 0; k < 3; k++) yield return null;
            Assert.That(split.HasAttemptFor(lower), Is.True, "the attempt waits to offer its bake again");
            Assert.That(split.Published, Is.Zero);
            int notRun = split.EndedAs(SpatialSplitEnd.NotRun);

            // Switched off: the attempt is not collected and nothing of it is on a worker -- it is ended there and then.
            split.Settings = default;
            yield return null;
            yield return null;
            Assert.That(split.HasAttemptFor(lower), Is.False, "ended at the switch-off");
            Assert.That(split.HasAttempt, Is.False, "nothing is out");
            Assert.That(split.EndedAs(SpatialSplitEnd.NotRun), Is.EqualTo(notRun + 1), "as not run");
            Assert.That(split.AttemptsHeldBytes, Is.Zero, "its blocks went back");
            Assert.That(split.Published, Is.Zero);
            Assert.That(root.Ledger.IsCurrentTarget(lower), Is.True, "the piece stands as it was");
            Assert.That(root.Owners.Count, Is.EqualTo(owners));
            Assert.That(root.GeometryFaults, Is.Zero);
            TestContext.Out.WriteLine(split.Describe());
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator SplitScan_Off_ABakeNeverOffered_IsEnded_UnderTheControlAsBefore()
        {
            yield return ABakeNeverOfferedIsEndedWhenOff(1);
        }

        [UnityTest]
        public IEnumerator SplitScan_Off_ABakeNeverOffered_IsEnded_WithSeveralAttempts()
        {
            yield return ABakeNeverOfferedIsEndedWhenOff(4);
        }

        [UnityTest]
        public IEnumerator SplitScan_ACandidateNeedingMoreBytesAloneThanTheLimit_IsHeldWithThatReason_UntilTheLimitChanges()
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
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.MaxAttempts = 4;
            for (int k = 0; k < 2; k++)
            {
                LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f * k), true, handLowX: 0.8f, trunkBottomBone: 1);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
            }

            int candidates = CandidatesByExamine(root, split, null);
            Assert.That(candidates, Is.GreaterThanOrEqualTo(2), "the layout: a piece of each body");

            // Nothing is out, and the limit is under what one attempt holds: no candidate can be issued, each for that reason.
            background.HoldEverything = true;
            split.MaxHeldBytes = 1;
            remaining[0] = 1.0;
            yield return Until(() => split.TooLargeNow == candidates, "every candidate was held for its size");
            for (int k = 0; k < 4; k++) yield return null;
            Assert.That(split.Issued, Is.Zero, "nothing issued past the limit");
            Assert.That(split.AttemptsOut, Is.Zero);
            Assert.That(split.AttemptsHeldBytes, Is.Zero, "nothing was taken for them");
            Assert.That(split.TurnsBy(SpatialSplitNoIssue.TooLargeAlone), Is.EqualTo(1), "one turn said so, and the walk has stood idle since");
            Assert.That(split.LastNoIssue, Is.EqualTo(SpatialSplitNoIssue.Idle));
            Assert.That(split.IssuesHeldTooLargeAlone, Is.EqualTo(candidates), "each held once, not asked again every frame");
            Assert.That(split.LastReservationBytes, Is.GreaterThan(1), "what one would hold: " + split.LastReservationBytes);
            Assert.That(split.IsIdle, Is.True, "and the walk stands idle on them");

            // The limit raised: they are looked at again and issued.
            split.MaxHeldBytes = CutSpatialSplit.EvaluationMaxHeldBytes;
            yield return Until(() => split.AttemptsOut == candidates, "issued once the limit allows");
            Assert.That(split.TooLargeNow, Is.Zero);
            Assert.That(split.AttemptsHeldBytes, Is.GreaterThan(0));
            Assert.That(split.AttemptsHeldBytes, Is.LessThanOrEqualTo(split.MaxHeldBytes));
            TestContext.Out.WriteLine(split.DescribeSeveral());

            // The world ends here with attempts out on the (holding) executor: the fixture's ending gives everything back.
        }
    }
}
