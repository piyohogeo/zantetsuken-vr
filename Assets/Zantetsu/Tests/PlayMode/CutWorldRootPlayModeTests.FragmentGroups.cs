using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// DESIGN 7.10's lifetime of cut fragments by the object they came from (<see cref="CutFragmentGroupCollection"/>,
    /// Phase 7.1) through the product's own world: a whole group ended at once and another left, a choice overtaken by
    /// a cut, a placed prop and a building given back their own instance and cut again as uncut ones, what is not
    /// chosen (seen, an original or a shared thing that would be in view, a heavy frame, no budget, no camera), the
    /// farther of the groups a turn looked at ended first however near the others lie, and nothing at all while off.
    /// The world is ended by every case. The placed cases are ignored where the licensed inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutFragmentGroupSettings GroupsOn(int max, int examinePerFrame = 8)
        {
            return new CutFragmentGroupSettings(true, max, examinePerFrame, CutWorldRoot.CompactionReserveSeconds, 0.00005, 0.0002);
        }

        private static CutFragmentGroupSettings GroupsOff()
        {
            return new CutFragmentGroupSettings(false, 0, 8, CutWorldRoot.CompactionReserveSeconds, 0.00005, 0.0002);
        }

        /// <summary>The world's group collection with a budget and a view of the case's own: off until the case switches it on.</summary>
        private CutFragmentGroupCollection GroupsOf(CutWorldRoot root, Vector3 viewer)
        {
            CutFragmentGroupCollection groups = root.Groups;
            groups.RemainingMainSeconds = () => 1.0;
            groups.ViewCamera = LifetimeCamera(viewer);
            groups.Settings = GroupsOff();
            // The empty owners' own collection is not what these cases are about.
            root.EmptyOwners.Settings = new CutEmptyOwnerCollectionSettings(false, 16, 1, CutWorldRoot.CompactionReserveSeconds, 0.0003);
            return groups;
        }

        private static List<LogicalFragmentId> LivePiecesOf(CutWorldRoot root, LogicalFragmentId groupRoot)
        {
            var pieces = new List<LogicalFragmentId>();
            root.Ledger.CopyLiveCutFragmentsOf(groupRoot, pieces);
            return pieces;
        }

        /// <summary>
        /// Puts a piece's owner at <paramref name="at"/>, at rest, and says its placement was changed by hand: where a
        /// case needs a piece to lie, whatever its cut gave it.
        /// </summary>
        private static void PutPiece(CutWorldRoot root, LogicalFragmentId piece, Vector3 at)
        {
            Assert.That(root.Owners.TryGet(piece, out PhysicsFragmentOwner owner) && owner.Body != null, Is.True, "the layout: a piece with a body of its own: " + piece);
            owner.Body.linearVelocity = Vector3.zero;
            owner.Body.angularVelocity = Vector3.zero;
            owner.Root.transform.position = at;
            owner.Body.position = at;
            CutPhysicsStep.NotePlacementInputChanged(piece);
        }

        /// <summary>
        /// Frames until <paramref name="done"/>, every turn among them held against the setting's limits -- no more
        /// groups looked at than the setting says, no more than one ended -- and the watched groups that ended, in the
        /// order they did.
        /// </summary>
        private static IEnumerator TurnsWithinTheLimits(
            CutWorldRoot root, CutFragmentGroupCollection groups, Func<bool> done, (string name, LogicalFragmentId root)[] watched, List<string> ended, string what)
        {
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (!done() && Time.realtimeSinceStartup < deadline)
            {
                int perTurn = groups.Settings.examinePerFrame;
                int walked = groups.WalkedTurns, examined = groups.Examined, retired = groups.GroupsRetired;
                yield return null;
                int turns = groups.WalkedTurns - walked;
                Assert.That(groups.Examined - examined, Is.LessThanOrEqualTo(perTurn * turns), "no more groups looked at in a turn than the setting says (" + perTurn + ")");
                Assert.That(groups.GroupsRetired - retired, Is.LessThanOrEqualTo(turns), "no more than one group ended in a turn");
                foreach ((string name, LogicalFragmentId group) in watched)
                {
                    if (!ended.Contains(name) && root.Ledger.LiveCutCountOf(group) == 0)
                    {
                        ended.Add(name);
                    }
                }
            }

            Assert.That(done(), Is.True, what + ": it had not happened within the deadline");
        }

        /// <summary>What a test gives as an origin: bounds it says, and what was asked of it.</summary>
        private sealed class OriginDouble : ICutGroupOrigin
        {
            public Bounds restore;
            public Bounds? shared;
            public bool mayEnd = true;
            public int ended, returned, rearmed;
            public CutGroupKind Kind => CutGroupKind.Prop;
            public bool TryGetRestoreBounds(out Bounds world) { world = restore; return true; }
            public bool TryGetSharedBounds(out Bounds world) { world = shared ?? default; return shared.HasValue; }
            public bool MayEndNow(out string why) { why = mayEnd ? null : "said busy"; return mayEnd; }
            public void EndShared(string why) => ended++;
            public bool TryReturn() { returned++; return true; }
            public bool TryRearm() { rearmed++; return true; }
        }

        /// <summary>
        /// **A group over the maximum ends whole, at one boundary, and another is left.** Two boxes are cut: one far
        /// behind the view into three pieces, one in view into two. With five live cut fragments against a maximum of
        /// four, the far group's three pieces go in one turn -- fragments, owners, hit shapes, the DAG's records -- and
        /// the group in view stands untouched; under the maximum nothing more is looked at. The far group's vertex room
        /// goes back once the display has let its geometry go.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_AGroupOverTheMaximum_EndsWholeAtOnce_AndAnotherIsLeft()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));
            LogicalFragmentId far = AddBody(root, new Vector3(0f, 0f, -60f));
            LogicalFragmentId seen = AddBody(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId uncut = AddBody(root, new Vector3(4f, 0f, -60f));
            groups.Register(far, null);
            groups.Register(seen, null);
            groups.Register(uncut, null);
            yield return null;
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero, "uncut bodies are no cut fragments");
            var sides = new LogicalFragmentId[2];
            var more = new LogicalFragmentId[2];
            var seenSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, far, new float4(0f, 1f, 0f, 0f), sides);
            yield return CutInTwo(root, sides[0], new float4(1f, 0f, 0f, 0f), more);
            yield return CutInTwo(root, seen, new float4(0f, 1f, 0f, 0f), seenSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(5), "the layout: three pieces and two");
            Assert.That(root.Ledger.LiveCutCountOf(far), Is.EqualTo(3));
            Assert.That(root.Ledger.LiveCutCountOf(seen), Is.EqualTo(2));
            Assert.That(root.Ledger.TryGetRoot(more[1], out LogicalFragmentId rootOf) && rootOf == far, Is.True, "a piece names its group without a walk");
            List<LogicalFragmentId> farPieces = LivePiecesOf(root, far);
            Assert.That(farPieces, Is.EquivalentTo(new[] { sides[1], more[0], more[1] }));
            int owners = root.Owners.Count;
            int released = root.Storage.VertexGroupsReleased;

            groups.Settings = GroupsOn(4);
            yield return Until(() => groups.GroupsRetired == 1, "the far group was collected");
            Assert.That(groups.LastRetiredFragments, Is.EqualTo(3), "every piece of it in the one retirement");
            Assert.That(groups.FragmentsRetired, Is.EqualTo(3));
            Assert.That(groups.NpcsRetired, Is.EqualTo(1));
            foreach (LogicalFragmentId piece in farPieces)
            {
                Assert.That(IsRetired(root, piece), Is.True, "retired: " + piece);
                Assert.That(root.Owners.TryGet(piece, out _) || IsHitTarget(root, piece), Is.False, "no owner and no hit shape: " + piece);
                Assert.That(root.Geometry.TryGetGeometry(piece, out _) || root.Geometry.TryGetGeometryFrame(piece, out _), Is.False, "the DAG keeps nothing: " + piece);
            }

            Assert.That(root.Ledger.LiveCutCountOf(far), Is.Zero);
            Assert.That(root.Owners.Count, Is.EqualTo(owners - 3));
            Assert.That(groups.IsKnown(far), Is.False, "nothing comes back for it: forgotten");

            // The retirement's own frame's collection reflected it (TL, 2026-10-10): the turn runs before that frame's
            // collection, so by the next Update nothing of the group is drawn or held, and the next turn reads that
            // collection as the one that followed the retirement.
            Assert.That(root.Display.HoldsAnythingOf(far), Is.False, "the display holds nothing of the far group already: the retirement's own frame's collection let it go");
            Assert.That(farPieces.Any(p => IsDrawn(root, p)), Is.False, "none of its pieces is drawn any more");
            yield return null;
            Assert.That(groups.CollectionsAfterRetirement, Is.EqualTo(1), "that collection was read as the one that followed the retirement");
            Assert.That(groups.HeldFor(CutGroupHold.StructureChanged), Is.GreaterThanOrEqualTo(1), "and the turn after the retirement was held for the structure it changed");

            // The group in view, and the uncut body, as they were.
            Assert.That(root.Ledger.IsCurrentTarget(seenSides[0]) && root.Ledger.IsCurrentTarget(seenSides[1]), Is.True);
            Assert.That(root.Owners.TryGet(seenSides[0], out _) && root.Owners.TryGet(seenSides[1], out _), Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(uncut) && root.Owners.TryGet(uncut, out _), Is.True, "an uncut body is no group to end");

            // Under the maximum now: nothing more is looked at.
            int examined = groups.Examined;
            for (int i = 0; i < 6; i++) yield return null;
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(2));
            Assert.That(groups.GroupsRetired, Is.EqualTo(1));
            Assert.That(groups.Examined, Is.EqualTo(examined), "no group looked at under the maximum");
            Assert.That(groups.HeldFor(CutGroupHold.UnderTheMaximum), Is.GreaterThan(0));
            Assert.That(IsDrawn(root, seenSides[0]) && IsDrawn(root, seenSides[1]), Is.True, "the group in view is drawn as before");
            Assert.That(root.Display.HoldsAnythingOf(far), Is.False, "the display holds nothing of the far group");

            // Its vertex room: back once nothing reads it.
            yield return Until(() => root.Storage.VertexGroupsReleased > released, "the far group's vertex room went back");
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(groups.Describe());
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A group with a cut under way or a geometry open is not chosen, and a choice overtaken is not applied.**
        /// A group is chosen; one of its pieces is then cut the ordinary way. While that cut is accepted, and while its
        /// geometry is still out, the group cannot be chosen, and the old choice -- made on the family as it was -- is
        /// refused with nothing retired. Once the cut has committed the group is chosen anew and ends whole, its new
        /// pieces with it, and nothing of it comes back afterwards.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_ACutUnderWay_OrAGeometryOpen_IsNotChosen_AndAChoiceOvertaken_IsNotApplied()
        {
            HoldingExecutor unityJob = null, geometry = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : destination == WorkDestination.GeometryPool
                        ? geometry = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                        : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 50f));
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            groups.Register(body, null);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            Assert.That(groups.TryChoose(body, out CutGroupCandidate overtaken), Is.True, "the layout: a group that can be chosen");
            Assert.That(overtaken.Fragments, Is.EqualTo(2));

            // A piece cut the ordinary way: accepted and held at its bake, its geometry held as well.
            unityJob.HoldEverything = true;
            geometry.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(sides[0], new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            Assert.That(root.Ledger.TryGetActiveOperation(sides[0], out _), Is.True, "the layout: a cut of a piece is under way");
            Assert.That(groups.TryChoose(body, out _), Is.False, "a cut under way: not chosen");
            Assert.That(groups.TryApply(in overtaken, out CutGroupPass refusal), Is.False);
            Assert.That(refusal, Is.EqualTo(CutGroupPass.Changed), "the family changed since the choice");
            Assert.That(root.Ledger.LiveCutCountOf(body), Is.EqualTo(2), "nothing was retired");

            // Its physics is published; its geometry is still out.
            unityJob.HoldEverything = false;
            unityJob.ReleaseEverything();
            CutOperationId recut = AdmittedFor(root, new[] { sides[0] })[0];
            yield return Until(
                () => root.Ledger.TryGetOperation(recut, out LogicalCutOperation op) && op.state == LogicalCutOperationState.Published
                      && root.Owners.TryGet(op.positive, out _) && root.Owners.TryGet(op.negative, out _),
                "the cut's physics is published");
            Assert.That(root.Geometry.StageOf(recut), Is.Not.EqualTo(CutGeometryStage.Committed), "the layout: its geometry is held");
            Assert.That(root.Ledger.LiveCutCountOf(body), Is.EqualTo(3), "the layout: three pieces now");
            Assert.That(groups.TryChoose(body, out _), Is.False, "a geometry open: not chosen");
            Assert.That(groups.TryApply(in overtaken, out refusal), Is.False, "and the old choice is not applied to what the group became");
            Assert.That(root.Ledger.LiveCutCountOf(body), Is.EqualTo(3));

            // Committed: chosen anew, and ended whole.
            geometry.HoldEverything = false;
            geometry.ReleaseEverything();
            yield return Until(() => root.Geometry.StageOf(recut) == CutGeometryStage.Committed, "the cut committed");
            yield return null;
            List<LogicalFragmentId> pieces = LivePiecesOf(root, body);
            Assert.That(groups.TryChoose(body, out CutGroupCandidate chosen), Is.True);
            Assert.That(groups.TryApply(in chosen, out refusal), Is.True, "applied: " + refusal);
            Assert.That(pieces.All(p => IsRetired(root, p) && !root.Owners.TryGet(p, out _)), Is.True, "every piece in the one call");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero);
            Assert.That(groups.TryApply(in chosen, out refusal), Is.False, "a group ended is not ended again");
            int operations = root.Ledger.OperationCount;
            ProvisionalCutAsk late = Ask(pieces[0], new float4(0f, 0f, 1f, 0f));
            root.TryAsk(in late);
            for (int i = 0; i < 4; i++) yield return null;
            Assert.That(root.Ledger.OperationCount, Is.EqualTo(operations), "no cut of a collected piece is accepted");
            Assert.That(pieces.Any(p => root.Owners.TryGet(p, out _) || IsDrawn(root, p)), Is.False, "nothing of it came back");
            Assert.That(groups.GroupsRetired, Is.EqualTo(1));
            Assert.That(groups.Refused, Is.EqualTo(3));
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Seen, an original or a shared thing that would be in view, a busy origin, a heavy frame and a short
        /// budget: not ended; off, nothing is looked at. What no eye can see is ended however near, the farther
        /// first.** Groups over the maximum, each failing one condition, stand through the turns. The ones that fail
        /// none are ended one a turn, the farthest of those the turn looked at first -- a group 12 m behind the view
        /// (inside the 20 m once asked) after the one 62 m away although the walk met it first, and then two whose
        /// pieces lie far and whose shared thing, or whose original's place, lies a few metres behind the view: what a
        /// group shares and where its original would stand are part of how far it is. The first one's origin is asked
        /// to end what it shares, to return once the display has let the pieces go, and to be a cut target again -- in
        /// that order, once each.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_SeenOrBusy_IsNotEnded_TheUnseenEndFartherFirstHoweverNear_AndTheOriginIsAskedInOrder()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[] { 1.0 };
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));
            groups.RemainingMainSeconds = () => remaining[0];
            LogicalFragmentId seen = AddBody(root, new Vector3(0f, 0f, 40f));           // in view, 48 m away
            LogicalFragmentId near = AddBody(root, new Vector3(0f, 0f, -20f));          // behind the view, 12 m away: unseen
            LogicalFragmentId backNear = AddBody(root, new Vector3(8f, 0f, -70f));      // far and unseen, its original said to stand in view
            LogicalFragmentId busy = AddBody(root, new Vector3(16f, 0f, -70f));         // far and unseen, its origin busy
            LogicalFragmentId free = AddBody(root, new Vector3(-8f, 0f, -70f));         // far and unseen, nothing against it: 62 m
            LogicalFragmentId sharedSeen = AddBody(root, new Vector3(24f, 0f, -70f));   // far and unseen, what its pieces share said to stand in view
            LogicalFragmentId backClose = AddBody(root, new Vector3(-24f, 0f, -70f));   // far and unseen, its original said to stand 1.5 m behind the view
            LogicalFragmentId sharedClose = AddBody(root, new Vector3(-16f, 0f, -70f)); // far and unseen, what its pieces share said to lie 3.5 m behind the view
            var far = new Bounds(new Vector3(-8f, 0f, -70f), Vector3.one);
            var backNearOrigin = new OriginDouble { restore = new Bounds(new Vector3(4f, 0f, 40f), Vector3.one) };
            var busyOrigin = new OriginDouble { restore = new Bounds(new Vector3(16f, 0f, -70f), Vector3.one), mayEnd = false };
            var freeOrigin = new OriginDouble { restore = far };
            var sharedSeenOrigin = new OriginDouble { restore = new Bounds(new Vector3(24f, 0f, -70f), Vector3.one), shared = new Bounds(new Vector3(-4f, 0f, 40f), Vector3.one) };
            var backCloseOrigin = new OriginDouble { restore = new Bounds(new Vector3(0f, 0f, -10f), Vector3.one) };
            var sharedCloseOrigin = new OriginDouble { restore = new Bounds(new Vector3(-16f, 0f, -70f), Vector3.one), shared = new Bounds(new Vector3(0f, 0f, -12f), Vector3.one) };

            // The walk's order: the near one before the free one, the one whose original is close before the one whose
            // shared thing is. Neither is the order they end in.
            groups.Register(seen, null);
            groups.Register(near, null);
            groups.Register(backNear, backNearOrigin);
            groups.Register(busy, busyOrigin);
            groups.Register(free, freeOrigin);
            groups.Register(sharedSeen, sharedSeenOrigin);
            groups.Register(backClose, backCloseOrigin);
            groups.Register(sharedClose, sharedCloseOrigin);
            yield return null;
            var sides = new LogicalFragmentId[2];
            var freeSides = new LogicalFragmentId[2];
            foreach (LogicalFragmentId body in new[] { seen, near, backNear, busy, sharedSeen, backClose, sharedClose })
            {
                yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            }

            yield return CutInTwo(root, free, new float4(0f, 1f, 0f, 0f), freeSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(16));
            Assert.That(groups.GroupCount, Is.EqualTo(groups.Settings.examinePerFrame), "the layout: every group is within one turn's looking");

            // Off: no turn, nothing looked at.
            for (int i = 0; i < 4; i++) yield return null;
            groups.Step();
            Assert.That(groups.Turns + groups.Examined, Is.Zero, "off: nothing");

            // On, with no budget: no group is looked at at all -- looking costs too.
            remaining[0] = 0.0;
            groups.Settings = GroupsOn(0);
            yield return Until(() => groups.HeldFor(CutGroupHold.BudgetShort) > 0, "a turn was held for the budget");
            for (int i = 0; i < 4; i++) yield return null;
            Assert.That(groups.Examined, Is.Zero, "with nothing of the budget left, no group is looked at");
            Assert.That(groups.GroupsRetired, Is.Zero);

            // With room to look at one group but not to read and retire a group of two: each is passed over for the
            // budget from its count alone, before anything of it is read.
            remaining[0] = CutWorldRoot.CompactionReserveSeconds + (2.0 * groups.ExpectedExamineSeconds(1)) + 1.0e-5;
            Assert.That(remaining[0], Is.LessThan(CutWorldRoot.CompactionReserveSeconds + (2.0 * (groups.ExpectedExamineSeconds(2) + groups.ExpectedSeconds(2)))),
                "the layout: not enough for a group of two");
            yield return Until(() => groups.PassedAs(CutGroupPass.BudgetShort) >= 8, "every group was passed over for the budget");
            Assert.That(groups.PassedAs(CutGroupPass.Seen) + groups.PassedAs(CutGroupPass.SharedBusy) + groups.Candidates, Is.Zero,
                "none of them was read: the budget is asked before a group's pieces are");
            Assert.That(groups.GroupsRetired, Is.Zero);
            remaining[0] = 0.0;
            yield return null;

            // A heavy frame: no group is looked at.
            int examinedBefore = groups.Examined;
            CutWorldFrameNotes.Note(CutWorldFrameNotes.Work.MobPlanPublish);
            groups.Step();
            Assert.That(groups.LastHold, Is.EqualTo(CutGroupHold.HeavyWork));
            Assert.That(groups.Examined, Is.EqualTo(examinedBefore));
            yield return null;

            // With the budget open: the free group ends first, the farthest of the four no eye can see -- although
            // the walk met the near one before it; the others stand, each for its own reason.
            var watched = new[]
            {
                ("seen", seen), ("near", near), ("backNear", backNear), ("busy", busy), ("free", free),
                ("sharedSeen", sharedSeen), ("backClose", backClose), ("sharedClose", sharedClose),
            };
            var ended = new List<string>();
            remaining[0] = 1.0;
            yield return TurnsWithinTheLimits(root, groups, () => groups.GroupsRetired >= 1, watched, ended, "the free group was collected");
            Assert.That(groups.GroupsRetired, Is.EqualTo(1));
            Assert.That(IsRetired(root, freeSides[0]) && IsRetired(root, freeSides[1]), Is.True, "the farthest of those looked at first");
            Assert.That(groups.LastRetiredDistance, Is.InRange(60f, 63f), "how far it was: " + groups.LastRetiredDistance);
            Assert.That(root.Ledger.LiveCutCountOf(near), Is.EqualTo(2), "the near one, met first by the walk, was left for it");
            Assert.That(groups.CandidatesLeft, Is.EqualTo(3), "three that could have been ended were left for the farther one");
            Assert.That(freeOrigin.ended, Is.EqualTo(1), "asked to end what it shares, in the retirement");
            Assert.That(freeOrigin.returned, Is.Zero, "not back in the frame its pieces went: the display still draws them");
            yield return TurnsWithinTheLimits(root, groups, () => freeOrigin.returned == 1, watched, ended, "its original stands again");
            Assert.That(root.Display.HoldsAnythingOf(free), Is.False, "only once the display holds nothing of the group");
            yield return TurnsWithinTheLimits(root, groups, () => freeOrigin.rearmed == 1, watched, ended, "and is a cut target again");
            Assert.That(new[] { freeOrigin.ended, freeOrigin.returned, freeOrigin.rearmed }, Is.EqualTo(new[] { 1, 1, 1 }), "once each");
            Assert.That(groups.IsKnown(free), Is.False, "its next cut is a new group");

            // Then the near one, 12 m behind the view; then the two whose pieces are as far as the free one's and
            // whose shared thing (3.5 m) or original's place (1.5 m) is close: ranked by that, the nearest of all.
            yield return TurnsWithinTheLimits(root, groups, () => groups.GroupsRetired >= 4 && groups.Rearmed >= 3, watched, ended, "every group no eye can see was collected");
            Assert.That(ended, Is.EqualTo(new[] { "free", "near", "sharedClose", "backClose" }), "the farther first, by the nearest bounds of each");
            Assert.That(groups.LastRetiredDistance, Is.InRange(1.4f, 1.6f), "the last one's distance is its original's place's: " + groups.LastRetiredDistance);
            Assert.That(new[] { sharedCloseOrigin.ended, sharedCloseOrigin.returned, backCloseOrigin.ended, backCloseOrigin.returned }, Is.EqualTo(new[] { 1, 1, 1, 1 }));
            for (int i = 0; i < 12; i++) yield return null;
            Assert.That(groups.GroupsRetired, Is.EqualTo(4), "the others stand");
            Assert.That(groups.PassedAs(CutGroupPass.Seen), Is.GreaterThan(0), "one in view, one whose original would come back in view, one whose shared thing is in view");
            Assert.That(groups.PassedAs(CutGroupPass.SharedBusy), Is.GreaterThan(0), "one whose origin is busy");
            Assert.That(new[] { root.Ledger.LiveCutCountOf(seen), root.Ledger.LiveCutCountOf(backNear), root.Ledger.LiveCutCountOf(busy), root.Ledger.LiveCutCountOf(sharedSeen) },
                Is.EqualTo(new[] { 2, 2, 2, 2 }), "in view; its original would come back in view; busy; what it shares is in view");
            Assert.That(new[] { backNearOrigin.ended, busyOrigin.ended, sharedSeenOrigin.ended }, Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(8));
            TestContext.Out.WriteLine(groups.Describe());
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Of the groups one turn looked at, the farther ends first, and a group is as far as its nearest piece.**
        /// Three groups behind the view, none to be seen: one 15 m away (inside the 20 m once asked), one 32 m away,
        /// and one 82 m away with one of its two pieces lying 3 m behind the view. The walk meets them in that order.
        /// With all three inside a turn's looking they end 32 m, 15 m, 3 m: the farther first, the one with a piece at
        /// 3 m last however far its other piece is, and the one at 15 m ended all the same. Before that, with no camera
        /// at all, every group is passed over as not judged and nothing ends.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_OfTheUnseenLookedAtInOneTurn_TheFartherEndsFirst_ByItsNearestPiece()
        {
            yield return FartherFirstCase(8, true, new[] { "middle", "close", "strayed" }, new[] { 32f, 15f, 3f }, 3);
        }

        /// <summary>
        /// **It is the farthest of the groups a turn looked at, not of all groups; the setting's count is kept.** The
        /// same three groups with two looked at a turn: the first turn sees the one 15 m away and the one with a piece
        /// at 3 m, and ends the one at 15 m -- the one 32 m away, farther than both, was not looked at. The walk goes
        /// on: the next turn looks at the one at 32 m and, come round, at the one left, read again, and ends the one
        /// at 32 m; the one left twice ends last. No turn looks at more than two groups or ends more than one.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_TheFarthestOfThoseATurnLookedAt_NotOfAll_WithinTheCountPerTurn()
        {
            yield return FartherFirstCase(2, false, new[] { "close", "middle", "strayed" }, new[] { 15f, 32f, 3f }, 2);
        }

        private IEnumerator FartherFirstCase(int examinePerFrame, bool withoutACameraFirst, string[] order, float[] distances, int left)
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));

            // The view looks along +z from z = -8: everything here lies behind it.
            LogicalFragmentId close = AddBody(root, new Vector3(0f, 0f, -23f));
            LogicalFragmentId strayed = AddBody(root, new Vector3(0f, 0f, -90f));
            LogicalFragmentId middle = AddBody(root, new Vector3(0f, 0f, -40f));
            groups.Register(close, null);
            groups.Register(strayed, null);
            groups.Register(middle, null);
            yield return null;
            var closeSides = new LogicalFragmentId[2];
            var strayedSides = new LogicalFragmentId[2];
            var middleSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, close, new float4(0f, 1f, 0f, 0f), closeSides);
            yield return CutInTwo(root, strayed, new float4(0f, 1f, 0f, 0f), strayedSides);
            yield return CutInTwo(root, middle, new float4(0f, 1f, 0f, 0f), middleSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            PutPiece(root, closeSides[0], new Vector3(0f, 1.5f, -23f));
            PutPiece(root, closeSides[1], new Vector3(0f, -1.5f, -23f));
            PutPiece(root, middleSides[0], new Vector3(0f, 1.5f, -40f));
            PutPiece(root, middleSides[1], new Vector3(0f, -1.5f, -40f));
            PutPiece(root, strayedSides[0], new Vector3(0f, 0f, -90f));
            PutPiece(root, strayedSides[1], new Vector3(0f, 0f, -11f));   // one piece of the far group: 3 m behind the view
            yield return new WaitForFixedUpdate();
            yield return null;
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(6), "the layout: three groups of two");

            if (withoutACameraFirst)
            {
                // No camera, and none to fall back on: nothing is judged, nothing is chosen.
                Camera view = groups.ViewCamera;
                groups.ViewCamera = null;
                Assert.That(Camera.main == null, Is.True, "the layout: no main camera to fall back on");
                groups.Settings = GroupsOn(0, examinePerFrame);
                yield return Until(() => groups.PassedAs(CutGroupPass.NotJudged) >= 6, "every group was passed over for want of a view, twice");
                groups.Settings = GroupsOff();
                Assert.That(groups.Candidates + groups.GroupsRetired, Is.Zero, "without a view nothing is chosen");
                groups.ViewCamera = view;
            }

            var watched = new[] { ("close", close), ("strayed", strayed), ("middle", middle) };
            var ended = new List<string>();
            var were = new List<float>();
            int candidatesWere = groups.Candidates;
            groups.Settings = GroupsOn(0, examinePerFrame);
            for (int n = 1; n <= 3; n++)
            {
                int count = n;
                yield return TurnsWithinTheLimits(root, groups, () => groups.GroupsRetired >= count, watched, ended, "group " + n + " was collected");
                Assert.That(groups.GroupsRetired, Is.EqualTo(n), "one at a time");
                Assert.That(groups.LastRetiredFragments, Is.EqualTo(2), "whole");
                were.Add(groups.LastRetiredDistance);
            }

            TestContext.Out.WriteLine("ended " + string.Join(", ", ended) + " at " + string.Join(", ", were.Select(d => d.ToString("F2"))) + " m; " + groups.Describe());
            Assert.That(ended, Is.EqualTo(order), "the order they ended in");
            for (int i = 0; i < 3; i++)
            {
                Assert.That(were[i], Is.InRange(distances[i] - 2f, distances[i] + 1f), ended[i] + ": as far as its nearest piece");
            }

            Assert.That(groups.CandidatesLeft, Is.EqualTo(left), "groups that could have been ended and were left for a farther one");
            Assert.That(groups.Candidates - candidatesWere, Is.EqualTo(3 + left), "each of them was looked at again before it ended");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero);
            Assert.That(groups.GroupCount, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **One piece in view keeps a whole group however far it is; a group left for a farther one is read again as
        /// it then is.** Three groups: one 62 m behind the view, one 15 m behind it, and one 82 m behind it with one of
        /// its two pieces 158 m ahead, in view. The first turn ends the one at 62 m and leaves the one at 15 m; the one
        /// with a piece in view is passed over as seen. A choice of the ended group kept from before is refused, with
        /// nothing ended twice. The group that was left is then moved: a piece of it into view -- it stands; back out of
        /// view and cut again -- it ends with its three pieces, as it now is. The far group stands through all of it,
        /// whole, and ends once its piece is out of view again.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_OnePieceInViewKeepsAGroupHoweverFar_AndAGroupLeftForAFartherOne_IsReadAgainAsItThenIs()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[] { 0.0 };   // nothing is looked at while this is zero: the case opens and closes the turns by it
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));
            groups.RemainingMainSeconds = () => remaining[0];
            LogicalFragmentId left = AddBody(root, new Vector3(0f, 0f, -23f));
            LogicalFragmentId inView = AddBody(root, new Vector3(0f, 0f, -90f));
            LogicalFragmentId farther = AddBody(root, new Vector3(0f, 0f, -70f));
            groups.Register(left, null);
            groups.Register(inView, null);
            groups.Register(farther, null);
            yield return null;
            var leftSides = new LogicalFragmentId[2];
            var viewSides = new LogicalFragmentId[2];
            var fartherSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, left, new float4(0f, 1f, 0f, 0f), leftSides);
            yield return CutInTwo(root, inView, new float4(0f, 1f, 0f, 0f), viewSides);
            yield return CutInTwo(root, farther, new float4(0f, 1f, 0f, 0f), fartherSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            PutPiece(root, leftSides[0], new Vector3(0f, 1.5f, -23f));
            PutPiece(root, leftSides[1], new Vector3(0f, -1.5f, -23f));
            PutPiece(root, fartherSides[0], new Vector3(0f, 1.5f, -70f));
            PutPiece(root, fartherSides[1], new Vector3(0f, -1.5f, -70f));
            PutPiece(root, viewSides[0], new Vector3(0f, 0f, -90f));
            PutPiece(root, viewSides[1], new Vector3(0f, 0f, 150f));   // one piece of the far group: far ahead, in view
            yield return new WaitForFixedUpdate();
            yield return null;
            Assert.That(groups.TryChoose(farther, out CutGroupCandidate kept), Is.True, "the layout: a choice kept from before the walk");
            Assert.That(float.IsPositiveInfinity(kept.NearestDistance), Is.True, "chosen without a view: no distance");

            // The first turn: the one at 62 m ends, the one at 15 m is left, the one with a piece in view is seen.
            groups.Settings = GroupsOn(0);
            remaining[0] = 1.0;
            yield return Until(() => groups.GroupsRetired >= 1, "the farther group was collected");
            remaining[0] = 0.0;
            Assert.That(groups.GroupsRetired, Is.EqualTo(1));
            Assert.That(new[] { root.Ledger.LiveCutCountOf(farther), root.Ledger.LiveCutCountOf(left), root.Ledger.LiveCutCountOf(inView) }, Is.EqualTo(new[] { 0, 2, 2 }),
                "the farther one ended; the nearer one was left; the one with a piece in view stands");
            Assert.That(groups.LastRetiredDistance, Is.InRange(60f, 63f), "how far it was: " + groups.LastRetiredDistance);
            Assert.That(groups.CandidatesLeft, Is.EqualTo(1), "the nearer one could have been ended and was left for it");
            Assert.That(groups.PassedAs(CutGroupPass.Seen), Is.EqualTo(1), "the far group with a piece in view: seen");

            // The choice kept from before: its group is ended, and is not ended again.
            int refused = groups.Refused;
            Assert.That(groups.TryApply(in kept, out CutGroupPass refusal), Is.False, "a group ended is not ended again");
            Assert.That(refusal, Is.EqualTo(CutGroupPass.NothingToEnd));
            Assert.That(new[] { groups.GroupsRetired, groups.FragmentsRetired, groups.Refused - refused }, Is.EqualTo(new[] { 1, 2, 1 }), "nothing ended twice");

            // The group that was left has a piece in view now: read again with the view as it is, it stands.
            int seen = groups.PassedAs(CutGroupPass.Seen), candidates = groups.Candidates;
            PutPiece(root, leftSides[0], new Vector3(0f, 1.5f, 30f));
            remaining[0] = 1.0;
            yield return Until(() => groups.PassedAs(CutGroupPass.Seen) >= seen + 4, "two turns looked at both groups, each with a piece in view");
            remaining[0] = 0.0;
            Assert.That(groups.GroupsRetired, Is.EqualTo(1), "not ended for having been a candidate before");
            Assert.That(groups.Candidates, Is.EqualTo(candidates), "no candidate while a piece of each is in view");
            Assert.That(root.Ledger.LiveCutCountOf(left), Is.EqualTo(2));

            // Out of view again and cut once more while nothing is looked at: it ends as it now is, three pieces.
            PutPiece(root, leftSides[0], new Vector3(0f, 1.5f, -23f));
            var more = new LogicalFragmentId[2];
            yield return CutInTwo(root, leftSides[0], new float4(1f, 0f, 0f, 0f), more);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the second cut's record is over");
            Assert.That(root.Ledger.LiveCutCountOf(left), Is.EqualTo(3), "the layout: three pieces now");
            Assert.That(groups.GroupsRetired, Is.EqualTo(1), "the layout: nothing was looked at meanwhile");
            remaining[0] = 1.0;
            yield return Until(() => groups.GroupsRetired >= 2, "the group that was left was collected");
            remaining[0] = 0.0;
            Assert.That(groups.GroupsRetired, Is.EqualTo(2));
            Assert.That(groups.LastRetiredFragments, Is.EqualTo(3), "every piece it has now, the new ones with it");
            Assert.That(root.Ledger.LiveCutCountOf(left), Is.Zero);
            Assert.That(IsRetired(root, more[0]) && IsRetired(root, more[1]) && IsRetired(root, leftSides[1]), Is.True);

            // The far group, one piece of it in view all the while: whole, untouched.
            Assert.That(root.Ledger.IsCurrentTarget(viewSides[0]) && root.Ledger.IsCurrentTarget(viewSides[1]), Is.True, "however far, a group with a piece in view stands whole");
            Assert.That(root.Owners.TryGet(viewSides[0], out _) && root.Owners.TryGet(viewSides[1], out _), Is.True);

            // Its piece out of view again: nothing of it can be seen, and it ends.
            PutPiece(root, viewSides[1], new Vector3(0f, 3f, -90f));
            remaining[0] = 1.0;
            yield return Until(() => groups.GroupsRetired >= 3, "the far group was collected once nothing of it was in view");
            Assert.That(groups.LastRetiredDistance, Is.InRange(79f, 83f), "how far it was: " + groups.LastRetiredDistance);
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(groups.Describe());
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A character cut through the product's own path is one group, ended whole, and nothing of it comes back.**
        /// A prepared character -- the entrance an NPC's first cut takes -- is hit through the detector: its first cut
        /// makes its lineage a group without anybody saying so here. Over the maximum its pieces are retired in one
        /// turn; no original returns, the character's own hierarchy stays withdrawn as its cut left it, and a prepared
        /// character beside it that was never hit is untouched and can still be hit.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_ACharacterCutByTheProductsPath_IsOneGroup_EndedWhole_AndNothingComesBack()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, null, BindSourceZero);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 60f));
            var characters = new List<VpPreparedCharacterCut>();
            yield return PrepareBoxCharacter(root, new Vector3(0f, 0f, 0f), characters);
            yield return PrepareBoxCharacter(root, new Vector3(12f, 0f, 0f), characters);
            VpPreparedCharacterCut character = characters[0], other = characters[1];
            var detector = new SlashHitDetector(root, in k_hitSettings);
            detector.AddCharacter(character);
            detector.AddCharacter(other);
            yield return null;
            Assert.That(groups.GroupCount, Is.Zero, "no group before a cut");

            Evaluate(detector, Level(7, 0.2f, -3f, 3f), 7);
            List<SlashHitConfirmed> hits = Hits(detector);
            Assert.That(hits.Count, Is.EqualTo(1), "the sweep met the one character");
            Assert.That(hits[0].Fragment == character.Source && hits[0].Acceptance == ProvisionalCutAcceptance.Published, Is.True, "its cut published: " + hits[0].Acceptance);
            LogicalFragmentId individual = character.Source;
            Assert.That(groups.IsKnown(individual), Is.True, "the product's path made its lineage a group");
            Assert.That(groups.GroupCount, Is.EqualTo(1));
            yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
            yield return Until(() => root.Driver.Transactions.Count == 0, "its record is over");
            yield return Until(() => character.IsWithdrawn, "its own hierarchy has left");
            List<LogicalFragmentId> pieces = LivePiecesOf(root, individual);
            Assert.That(pieces.Count, Is.EqualTo(2), "the layout: two pieces");
            Assert.That(pieces.All(p => root.Owners.TryGet(p, out _)), Is.True);

            groups.Settings = GroupsOn(1);
            yield return Until(() => groups.GroupsRetired == 1, "the individual's group was collected");
            groups.Settings = GroupsOff();
            Assert.That(groups.NpcsRetired, Is.EqualTo(1));
            Assert.That(groups.LastRetiredFragments, Is.EqualTo(2));
            Assert.That(pieces.All(p => IsRetired(root, p) && !root.Owners.TryGet(p, out _) && !IsHitTarget(root, p)), Is.True, "every piece: retired, no owner, no hit shape");
            Assert.That(groups.IsKnown(individual), Is.False, "nothing comes back for an individual: forgotten at once");
            Assert.That(groups.ComingBack, Is.Zero);
            for (int i = 0; i < 8; i++) yield return null;
            Assert.That(groups.Returned + groups.Rearmed, Is.Zero, "no original returned");
            Assert.That(character.IsWithdrawn, Is.True, "the character's own hierarchy stays as its cut left it");
            Assert.That(pieces.Any(p => IsDrawn(root, p)), Is.False);
            Assert.That(root.Display.HoldsAnythingOf(individual), Is.False);
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero);

            // The one never hit: no fragment, no group, and still a target of the next sweep.
            Assert.That(other.Source.IsSet, Is.False);
            Evaluate(detector, Level(8, 0.2f, 9f, 15f), 8);
            List<SlashHitConfirmed> second = Hits(detector);
            Assert.That(second.Count, Is.EqualTo(1), "the other character is met");
            Assert.That(second[0].Fragment == other.Source && second[0].Acceptance == ProvisionalCutAcceptance.Published, Is.True, "and cut: " + second[0].Acceptance);
            Assert.That(other.Source != individual && groups.IsKnown(other.Source), Is.True, "another individual, another group");
            yield return Until(() => root.Geometry.StageOf(second[0].Operation) == CutGeometryStage.Committed, "its cut committed");
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(groups.Describe());
            yield return EndWorld(root);
        }

        /// <summary>
        /// **While the display adopts nothing, nothing comes back.** A group with an origin is ended while the display's
        /// collections are stopped: the frames go by, no collection is adopted, the display still holds the group's
        /// registrations, and the origin is not asked to return. When the collections go on again and one is adopted,
        /// it is asked, once.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_WhileTheDisplayAdoptsNothing_TheOriginalDoesNotComeBack()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 50f));
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            var origin = new OriginDouble { restore = new Bounds(Vector3.zero, Vector3.one) };
            groups.Register(body, origin);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            yield return null;
            Assert.That(IsDrawn(root, sides[0]) && IsDrawn(root, sides[1]), Is.True, "the layout: both pieces drawn");

            // The display collects no more: nothing is adopted from here.
            CutPhysicsStep.Leave(root.Driver);
            try
            {
                long adopted = root.Display.AdoptedCollections;
                Assert.That(groups.TryChoose(body, out CutGroupCandidate candidate), Is.True);
                Assert.That(groups.TryApply(in candidate, out CutGroupPass refusal), Is.True, "ended: " + refusal);
                Assert.That(origin.ended, Is.EqualTo(1));
                for (int i = 0; i < 8; i++) yield return null;
                Assert.That(root.Display.AdoptedCollections, Is.EqualTo(adopted), "the layout: no collection was adopted");
                Assert.That(root.Display.HoldsAnythingOf(body), Is.True, "the display still holds what it drew of the group");
                Assert.That(origin.returned, Is.Zero, "the original is not brought back beside its old pieces");
                Assert.That(groups.Returned, Is.Zero);
                Assert.That(groups.ComingBack, Is.EqualTo(1), "it waits");
            }
            finally
            {
                CutPhysicsStep.Join(root.Driver);
            }

            yield return Until(() => groups.Returned == 1, "the original stands again once a collection was adopted");
            Assert.That(root.Display.HoldsAnythingOf(body), Is.False);
            Assert.That(origin.returned, Is.EqualTo(1), "asked once");
            yield return Until(() => groups.Rearmed == 1, "and is a cut target again");
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A placed prop's pieces end together, its own instance comes back as it stood, and it is cut again as an
        /// uncut one.** Through its registrar and a detector: cut by a hit, its pieces collected in one turn; the
        /// instance's renderers and colliders return only after the display has let the pieces go, the ones that were
        /// off before staying off; then it is a candidate of the detector again with no fragment, and the next hit gives
        /// it a new fragment and cuts it. An uncut prop beside it is untouched throughout.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_APlacedProp_EndsWhole_ComesBackAsItStood_AndIsCutAgainAsANewFragment()
        {
            if (!File.Exists(PlacedPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + PlacedPropInputPath);
            var input = new TextAsset(File.ReadAllText(PlacedPropInputPath)) { name = "bench_001" };
            PlacedCuttableInput data = PlayableCityCuttable.ParsedInput(input);
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                root.Driver.RemainingMainSeconds = () => 1.0;
                CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 60f));
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 collected", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                PlayableCityCuttable c = PlaceRegistrar(root, input, "bench_001 collected (registrar)", Vector3.zero, 0f, Vector3.one);
                c.target = instance.transform;
                c.instanceRenderers = renderers;
                c.instanceColliders = colliders;
                c.deferUntilCut = true;
                c.DetectorForTest = detector;
                GameObject otherInstance = RefusalInstance("bench_001 never hit", at + new Vector3(30f, 0f, 0f), out Renderer[] otherRenderers, out Collider[] otherColliders);
                PlacedCuttableCandidate other = Candidate(root, data, otherInstance, otherRenderers, otherColliders);
                detector.AddPlaced(other);
                yield return UntilWithin(() => c.IsCutTarget || c.Failure != null, 5f, "the cut target made");
                Assert.That(c.Failure, Is.Null);
                PlacedCuttableCandidate candidate = c.Candidate;

                Evaluate(detector, ThroughTheMiddle(1, data, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance);
                LogicalFragmentId first = candidate.Source;
                Assert.That(groups.IsKnown(first), Is.True, "its cut made it a group");
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                yield return Until(() => root.Driver.Transactions.Count == 0, "its record is over");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the layout: the instance has left");
                List<LogicalFragmentId> pieces = LivePiecesOf(root, first);
                Assert.That(pieces.Count, Is.EqualTo(2), "the layout: two pieces");
                Assert.That(pieces.All(p => IsDrawn(root, p)), Is.True, "the layout: both drawn");
                int commandEndFirst = root.Display.DrawCommandEnd;

                // The instance is somewhere else by now (as one with a body of its own that moved on between its cut's
                // acceptance and its leaving): it comes back where its cut began, not where it is.
                Assert.That(candidate.TryGetPoseAtItsCut(out Vector3 cutAt, out Quaternion cutTurned), Is.True);
                Assert.That(cutAt, Is.EqualTo(at), "the layout: the pose recorded when its cut began");
                instance.transform.SetPositionAndRotation(at + new Vector3(3f, 0.5f, -2f), Quaternion.Euler(0f, 40f, 0f));

                // Collected: its pieces in one turn; the instance not yet. The setting is switched off the moment the
                // retirement has started: what was started is finished all the same -- back, and a cut target again.
                groups.Settings = GroupsOn(1);
                yield return Until(() => groups.GroupsRetired == 1, "the prop's group was collected");
                groups.Settings = GroupsOff();
                Assert.That(groups.PropsRetired, Is.EqualTo(1));
                Assert.That(pieces.All(p => IsRetired(root, p) && !root.Owners.TryGet(p, out _) && !IsHitTarget(root, p)), Is.True, "every piece: retired, no owner, no hit shape");
                if (groups.Returned == 0)
                {
                    Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the instance is not back before the display has let its pieces go");
                }

                // Back as it stood: what was off stays off.
                yield return Until(() => groups.Returned == 1, "the instance stands again");
                Assert.That(root.Display.HoldsAnythingOf(first), Is.False, "the display holds nothing of its pieces");
                Assert.That(pieces.Any(p => IsDrawn(root, p)), Is.False);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its renderers and colliders as placed -- the ones that were off still off");
                Assert.That((instance.transform.position - at).magnitude, Is.LessThan(1e-5f), "where it stood when its cut began, not where it had been moved to");
                Assert.That(Quaternion.Angle(instance.transform.rotation, cutTurned), Is.LessThan(0.01f), "and turned as it was then");

                // A cut target again, with no fragment -- with the setting off since the retirement; the next hit gives it a new one.
                Assert.That(groups.Settings.enabled, Is.False, "the layout: off since the retirement started");
                yield return Until(() => groups.Rearmed == 1, "a cut target again, although no new collection may start");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Candidate));
                Assert.That(candidate.Source.IsSet, Is.False, "no fragment until its next hit");
                Assert.That(candidate.IsHitTarget && detector.HasPlaced(candidate), Is.True, "a candidate of the detector again");
                Assert.That(new[] { candidate.Rearms, candidate.RearmsFromKeptMeshes, candidate.RearmBakes }, Is.EqualTo(new[] { 1, 1, 0 }), "its input was made from the meshes kept baked at its first preparation: no cook (DESIGN 7.10)");
                Assert.That(groups.IsKnown(first), Is.False);
                Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero);
                groups.Settings = GroupsOff();

                yield return null;
                Evaluate(detector, ThroughTheMiddle(2, data, at), 2);
                List<SlashHitConfirmed> again = Hits(detector);
                Assert.That(again.Count, Is.EqualTo(1), "one hit again");
                Assert.That(again[0].Acceptance == ProvisionalCutAcceptance.Published || again[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut again: " + again[0].Acceptance + "; " + candidate.LastRefusal);
                Assert.That(candidate.Source.IsSet && candidate.Source != first && candidate.Source.value > first.value, Is.True, "a new fragment, never the old one");
                Assert.That(again[0].Fragment, Is.EqualTo(candidate.Source));
                yield return Until(() => root.Geometry.StageOf(again[0].Operation) == CutGeometryStage.Committed, "the second cut committed");
                List<LogicalFragmentId> second = LivePiecesOf(root, candidate.Source);
                Assert.That(second.Count, Is.EqualTo(2));
                yield return null;
                Assert.That(second.All(p => IsDrawn(root, p)), Is.True, "its new pieces are drawn");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "and the instance has left again");
                Assert.That(pieces.Any(p => root.Ledger.IsCurrentTarget(p) || root.Owners.TryGet(p, out _)), Is.False, "nothing of the first cut came back");
                TestContext.Out.WriteLine("display command slots: taken " + commandEndFirst + " after the first cut, " + root.Display.DrawCommandEnd + " after the second (drawing " + root.Display.CommandCount
                    + ") of " + root.Display.CommandCapacity + "; compactions " + root.Display.Compactions + ", command slots given back " + root.Display.CompactionCommandSlotsGivenBack);
                // A re-cut takes its slots at the end; what the first cut left is given back by a compaction, in a frame
                // the world allows (D-216) -- whether one has run by now is not this case's.
                Assert.That(root.Display.DrawCommandEnd, Is.GreaterThanOrEqualTo(root.Display.CommandCount), "the slots gone through hold the commands that draw");

                // The one never hit, throughout.
                Assert.That(States(otherRenderers, otherColliders), Is.EqualTo(asPlaced));
                Assert.That(other.IsHitTarget && !other.Source.IsSet, Is.True);
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                TestContext.Out.WriteLine(groups.Describe());
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
                DisposeCandidates();
            }
        }

        /// <summary>
        /// **A building ends whole with what its pieces share, comes back as it stood, and is cut again.** In the
        /// always-kinematic mode: cut by a hit into its group; once the group is quiet its display fragments are
        /// retired in one turn and with them the group -- its body, its hull collider, its listing in the trial; the
        /// building's own drawing and colliders return after the display has let the pieces go; and the next hit makes a
        /// new group with a new fragment.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_ABuilding_EndsWholeWithItsSharedPhysics_ComesBack_AndIsCutAgain()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                root.Driver.RemainingMainSeconds = () => 1.0;
                CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 400f));
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 collected", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                int hullGroups = root.Hulls.GroupCount, bodies = Bodies();
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                candidate.Rearmed = again => detector.AddPlaced(again);
                detector.AddPlaced(candidate);

                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "the group took the hit");
                LogicalFragmentId first = candidate.Source;
                HullGroup group = candidate.Registration.Group;
                Assert.That(groups.IsKnown(first), Is.True, "its first cut made it a group");
                yield return Until(() => candidate.Registration != null && !candidate.Registration.Group.AwaitsFirstCut, "the first cut published");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the layout: its own drawing and colliders left at the hand-over");
                yield return Until(() => root.Hulls.IsBuildingQuiet(group, out _), "nothing of the building is under way");
                List<LogicalFragmentId> pieces = LivePiecesOf(root, first);
                Assert.That(pieces.Count, Is.GreaterThanOrEqualTo(2), "the layout: its display fragments");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups + 1));
                GameObject actor = group.Root;
                Assert.That(actor != null && group.Body != null && group.Collider != null, Is.True, "the layout: its shared body and hull collider");
                TestContext.Out.WriteLine("building group: " + pieces.Count + " display fragments, members " + group.MemberCount);

                // The view stands 40 m beyond the hull's far face, looking away: the hull and where the building would
                // come back are behind it, unseen. One display piece is then put ahead of the view, 80 m and more from
                // it (as a drop moves a piece apart from the group's body): it has no shape of its own, and what is
                // drawn of it is what is judged -- in view, so the building is not ended for its hull's sake.
                Bounds hull = group.Collider.bounds;
                groups.ViewCamera.transform.position = new Vector3(hull.center.x, hull.center.y, hull.max.z + 40f);
                Assert.That(root.Owners.TryGet(pieces[0], out PhysicsFragmentOwner pieceOwner) && pieceOwner.IsDisplayOnly, Is.True, "the layout: a display piece without physics of its own");
                Transform pieceRoot = pieceOwner.Root.transform;
                Vector3 pieceWas = pieceRoot.position;
                pieceRoot.position = pieceWas + new Vector3(0f, 0f, hull.size.z + 120f);
                CutPhysicsStep.NotePlacementInputChanged(pieces[0]);
                groups.Settings = GroupsOn(0);
                yield return Until(() => groups.PassedAs(CutGroupPass.Seen) > 0, "the building was passed over for its piece in view");
                for (int f = 0; f < 6; f++) yield return null;
                Assert.That(groups.GroupsRetired, Is.Zero, "not ended while a piece of it is drawn in view");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups + 1));

                // The piece back with its hull: every bounds of the building is unseen, and it ends whole.
                pieceRoot.position = pieceWas;
                CutPhysicsStep.NotePlacementInputChanged(pieces[0]);
                yield return Until(() => groups.GroupsRetired == 1, "the building's group was collected");
                Assert.That(groups.BuildingsRetired, Is.EqualTo(1));
                Assert.That(groups.LastRetiredFragments, Is.EqualTo(pieces.Count), "every display fragment in the one retirement");
                Assert.That(pieces.All(p => IsRetired(root, p) && !root.Owners.TryGet(p, out _)), Is.True);
                Assert.That(group.State, Is.EqualTo(HullGroupState.Gone));
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "its group left the trial");
                Assert.That(root.Hulls.BuildingsEnded, Is.EqualTo(1));
                Assert.That(group.Body == null && group.Collider == null && group.Shape == null && group.MemberCount == 0, Is.True, "nothing of the shared physics is kept");

                yield return Until(() => groups.Returned == 1, "the building stands again");
                Assert.That(actor == null, Is.True, "the group's actor -- body, hull collider, member roots -- is destroyed");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it is left");
                Assert.That(root.Display.HoldsAnythingOf(first), Is.False);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders as placed");
                yield return Until(() => groups.Rearmed == 1, "a cut target again");
                Assert.That(candidate.State == PlacedCuttableCandidate.CandidateState.Candidate && !candidate.Source.IsSet && candidate.Registration == null, Is.True);
                Assert.That(detector.HasPlaced(candidate), Is.True);
                Assert.That(root.Hulls.IsSettled, Is.True, "the trial has nothing of it in flight");
                groups.Settings = GroupsOff();

                yield return null;
                Evaluate(detector, ThroughTheMiddle(2, college, at), 2);
                List<SlashHitConfirmed> again = Hits(detector);
                Assert.That(again.Count, Is.EqualTo(1));
                Assert.That(again[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "a new group took the hit: " + candidate.LastRefusal);
                Assert.That(candidate.Source.IsSet && candidate.Source != first, Is.True, "a new fragment");
                Assert.That(candidate.Registration != null && !ReferenceEquals(candidate.Registration.Group, group), Is.True, "a new group");
                yield return Until(() => candidate.Registration != null && !candidate.Registration.Group.AwaitsFirstCut, "the second first cut published");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "handed over again");
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                TestContext.Out.WriteLine(groups.Describe());
                for (int f = 0; f < 10; f++) yield return null;
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        /// <summary>
        /// A budget that carries a turn's own parts and two and a half pieces by the collection's own expectations, as
        /// they stand when it is asked: a group of three pieces or more does not fit whole, two pieces do.
        /// </summary>
        private static Func<double> TwoAndAHalfPieces(CutFragmentGroupCollection groups)
        {
            return () => groups.Settings.reserveSeconds + (2.0 * groups.ExpectedTurnSeconds) + (5.0 * groups.ExpectedPieceSeconds);
        }

        /// <summary>Cuts a body four times into five pieces, by planes of its own through its centre: across, then each half along, then one quarter through.</summary>
        private static IEnumerator FivePieces(CutWorldRoot root, LogicalFragmentId body)
        {
            var halves = new LogicalFragmentId[2];
            var a = new LogicalFragmentId[2];
            var b = new LogicalFragmentId[2];
            var c = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), halves);
            yield return CutInTwo(root, halves[0], new float4(1f, 0f, 0f, 0f), a);
            yield return CutInTwo(root, halves[1], new float4(1f, 0f, 0f, 0f), b);
            yield return CutInTwo(root, a[0], new float4(0f, 0f, 1f, 0f), c);   // planes are the body's own, through its centre
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
        }

        /// <summary>
        /// **A group whose whole does not fit the frame is ended by its pieces, the last of it whole.** A body far
        /// behind the view is cut into five pieces; the budget carries two and a half pieces. The whole group is passed
        /// over for the budget, unread, and its pieces outside the view are ended instead, never more than two in a
        /// turn and never the group's last: four go so. The one left fits whole and is ended as a group.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_AGroupTooLargeForTheFrame_IsEndedByItsPieces_NoMoreThanTheBudgetCarries_ItsLastWhole()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));
            LogicalFragmentId far = AddBody(root, new Vector3(0f, 0f, -60f));
            groups.Register(far, null);
            yield return null;
            yield return FivePieces(root, far);
            Assert.That(root.Ledger.LiveCutCountOf(far), Is.EqualTo(5), "the layout: five pieces");
            List<LogicalFragmentId> pieces = LivePiecesOf(root, far);

            groups.RemainingMainSeconds = TwoAndAHalfPieces(groups);
            groups.Settings = GroupsOn(0);
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            int piecesBefore = 0, mostAtOnce = 0;
            while (groups.GroupsRetired == 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (groups.PiecesRetired != piecesBefore)
                {
                    mostAtOnce = Math.Max(mostAtOnce, groups.LastRetiredFragments);
                    Assert.That(root.Ledger.LiveCutCountOf(far), Is.GreaterThanOrEqualTo(1), "never the group's last piece by pieces");
                    piecesBefore = groups.PiecesRetired;
                }
            }

            Assert.That(groups.GroupsRetired, Is.EqualTo(1), "the group ended at last");
            Assert.That(groups.PiecesRetired, Is.EqualTo(4), "four of its pieces by themselves");
            Assert.That(mostAtOnce, Is.InRange(1, 2), "no more pieces at once than the budget carries");
            Assert.That(groups.PieceTurns, Is.GreaterThanOrEqualTo(2));
            Assert.That(groups.LastRetiredFragments, Is.EqualTo(1), "the last of it, whole");
            Assert.That(groups.FragmentsRetired, Is.EqualTo(5));
            Assert.That(groups.HeldFor(CutGroupHold.BudgetShort), Is.Zero, "the turns were never held for the budget: the group was");
            foreach (LogicalFragmentId piece in pieces)
            {
                Assert.That(IsRetired(root, piece), Is.True, "retired: " + piece);
                Assert.That(root.Owners.TryGet(piece, out _), Is.False, "no owner: " + piece);
                Assert.That(root.Geometry.TryGetGeometry(piece, out _), Is.False, "the DAG keeps nothing: " + piece);
            }

            Assert.That(groups.IsKnown(far), Is.False, "nothing comes back for it: forgotten");
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(groups.Describe());
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **By its pieces, a piece in view stays; the rest of a small group then waits for the group's own end.** Of a
        /// far body's five pieces one is put before the camera. The pieces outside the view go by themselves, the one
        /// in view never; once two are left the group fits whole and is a whole group again -- one piece in view keeps
        /// it all. Put out of view, the two are ended together.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_ByItsPieces_APieceInViewStays_AndTheSmallRestIsEndedWholeOnceUnseen()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));
            LogicalFragmentId far = AddBody(root, new Vector3(0f, 0f, -60f));
            groups.Register(far, null);
            yield return null;
            yield return FivePieces(root, far);
            List<LogicalFragmentId> pieces = LivePiecesOf(root, far);
            Assert.That(pieces.Count, Is.EqualTo(5), "the layout: five pieces");
            LogicalFragmentId inView = pieces[2];
            PutPiece(root, inView, new Vector3(0f, 0f, 0f));
            for (int i = 0; i < 3; i++) yield return null;

            groups.RemainingMainSeconds = TwoAndAHalfPieces(groups);
            groups.Settings = GroupsOn(0);
            yield return Until(() => groups.PiecesRetired == 3, "the three pieces outside the view went by themselves");
            for (int i = 0; i < 12; i++) yield return null;
            Assert.That(groups.PiecesRetired, Is.EqualTo(3), "and no more: two are left, a group that fits whole");
            Assert.That(groups.PiecesSeen, Is.GreaterThan(0), "the piece in view was looked at and left");
            Assert.That(groups.GroupsRetired, Is.Zero, "one piece in view keeps the small group");
            Assert.That(IsRetired(root, inView), Is.False);
            Assert.That(root.Ledger.LiveCutCountOf(far), Is.EqualTo(2));

            PutPiece(root, inView, new Vector3(0f, 0f, -60f));
            yield return Until(() => groups.GroupsRetired == 1, "out of view, the two ended together");
            Assert.That(groups.LastRetiredFragments, Is.EqualTo(2));
            Assert.That(groups.FragmentsRetired, Is.EqualTo(5));
            Assert.That(root.Ledger.LiveCutCountOf(far), Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            TestContext.Out.WriteLine(groups.Describe());
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **What a structural collection costs is read from any, not only from one a retirement caused.** A cut, with
        /// the collection on, far under its maximum, and nothing retired: the structural collection that follows it is read for the
        /// expectation. Read after retirements alone, an expectation once too high for any retirement to start was
        /// never read again, and nothing was collected any more (a Quest 3S, 2026-10-14).
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_TheExpectedStructuralCollection_IsReadFromACutsOwn_NotOnlyAfterARetirement()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, -8f));
            LogicalFragmentId body = AddBody(root, new Vector3(0f, 0f, -60f));
            groups.Register(body, null);
            groups.Settings = GroupsOn(1000);   // on, so that its turns run, and far under the maximum: nothing is retired
            yield return null;
            int before = groups.StructuralCollectionsRead;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            yield return Until(() => groups.StructuralCollectionsRead > before, "a structural collection read for the expectation");
            Assert.That(groups.CollectionsAfterRetirement, Is.Zero, "with nothing retired");
            Assert.That(groups.GroupsRetired + groups.PiecesRetired, Is.Zero);
            Assert.That(groups.ExpectedCollectAfterSeconds, Is.GreaterThan(0.0));
            TestContext.Out.WriteLine(groups.Describe());
            groups.Settings = GroupsOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Under frame synthesis the collection's budget is the application's own frame.** With the collection's own
        /// budget as the game gives it, a frame of 1/45 s set as the application's adds what it has beyond the Main
        /// budget; a frame shorter than the budget adds nothing; 0 leaves it as it was.
        /// </summary>
        [UnityTest]
        public IEnumerator FragmentGroups_UnderFrameSynthesis_TheBudgetIsTheApplicationsFrame()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            CutFragmentGroupCollection groups = root.Groups;
            for (int i = 0; i < 3; i++) yield return null;
            double main = CutPhysicsStep.MainBudgetSeconds;
            double plain = groups.RemainingMainSeconds();
            Assert.That(double.IsInfinity(plain), Is.False, "inside a frame: a budget to read");
            groups.FrameSeconds = 1.0 / 45.0;
            double synthesised = groups.RemainingMainSeconds();
            Assert.That(synthesised - plain, Is.InRange((1.0 / 45.0) - main - 0.0005, (1.0 / 45.0) - main + 0.0001), "the application's frame beyond the Main budget is added");
            groups.FrameSeconds = 1.0 / 120.0;
            double shorter = groups.RemainingMainSeconds();
            Assert.That(shorter, Is.LessThanOrEqualTo(plain + 0.0001), "a frame shorter than the budget adds nothing");
            groups.FrameSeconds = 0.0;
            Assert.That(groups.RemainingMainSeconds(), Is.LessThanOrEqualTo(plain + 0.0001));
            yield return EndWorld(root);
        }
    }
}
