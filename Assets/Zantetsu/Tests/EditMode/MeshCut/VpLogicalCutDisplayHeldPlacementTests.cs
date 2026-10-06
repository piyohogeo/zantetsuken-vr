using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Held placements (TL, 2026-10-07; DESIGN 5.6, D-205). In a collection that does ask placements, a display whose
    /// host vouches for its step count and names reference points asks the render fragments not yet held and the held
    /// ones near a reference point; the other held ones are drawn where they are held. A render fragment is held once a
    /// query's answer is the adopted placement over two different step results. The cases run that display beside the
    /// same display given nothing -- which asks every one, as before -- and compare what the two hold wherever nothing
    /// held and far was moved.
    /// <para>
    /// The bodies are cubes of 2 m at x = 3 i. With the margin of 0.5 m a held body's box is x in [3 i - 1.5, 3 i + 1.5];
    /// a reference point at x = -18 has a proximity box to x = 2 (20 m), which meets the boxes of bodies 0 and 1 only.
    /// </para>
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private static readonly Vector3 k_byTheFirstTwo = new Vector3(-18f, 0f, 0f);

        // The host of these cases: the steps it simulated, the placement inputs it changed outside a step, its reference points.
        private sealed class HoldHost
        {
            public long step = 1, outside;
            public bool vouches = true;
            public readonly List<Vector3> points = new List<Vector3>();

            public void Attach(VpLogicalCutDisplay display)
            {
                display.PlacementSerial = (out long s, out long o) => { s = step; o = outside; return vouches; };
                display.PlacementProximity = into => into.AddRange(points);
            }
        }

        private static VpHeldPlacementTotals Since(VpLogicalCutDisplay display, in VpHeldPlacementTotals before)
        {
            VpHeldPlacementTotals now = display.HeldPlacementTotals;
            now.Subtract(before);
            return now;
        }

        private static void AssertTreeHolds(VpLogicalCutDisplay display, int held, string what)
        {
            Assert.That(display.HeldPlacements, Is.EqualTo(held), what + ": held");
            Assert.That(display.HeldPlacementsForTest.TreeForTest.Count, Is.EqualTo(held), what + ": boxes in the tree, one a held render fragment");
            Assert.That(display.HeldPlacementsForTest.TreeForTest.Check(), Is.Null, what + ": the tree");
        }

        // Four collections, each at a new step: a structure is gone through once by each of the two snapshots before it
        // is kept, and a kept pass at a later step holds everything that stands. Leaves the host at the last step collected.
        private void SettleHeld(Twin twin, HoldHost host, int expected, string what)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) host.step++;
                CollectBoth(twin, what + ": settling, step " + i);
            }

            AssertTreeHolds(twin.kept.Display, expected, what + ": standing over two step results");
        }

        [Test]
        public void HeldPlacements_AreTakenUpWhenAnotherStepsAnswerIsTheSame_NotWithinOneStep_AndTheFarOnesAreNotAskedWhileAMovingOneIs()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);

                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                CollectBoth(twin, "the bodies");
                host.outside++;   // a pass at the same step: the other snapshot goes through the structure
                CollectBoth(twin, "the bodies, the other snapshot");
                VpHeldPlacementTotals d = Since(display, t);
                Assert.That(new[] { d.passesStructure, d.queriedOrdinary, d.passesSelective, d.fresh, d.carried }, Is.EqualTo(new long[] { 2, 16, 0, 8, 8 }), "two passes through the structure: the bodies new in the first, carried in the second, asked in each");
                Assert.That(display.HeldPlacements, Is.Zero);

                // The same step again (something outside a step is said to have changed): every answer is the adopted
                // placement, and that is one step's result twice -- nothing is held.
                t = display.HeldPlacementTotals;
                host.outside++;
                CollectBoth(twin, "the same step again");
                d = Since(display, t);
                Assert.That(new[] { d.passesSelective, d.queriedOrdinary, d.promoted, d.omitted }, Is.EqualTo(new long[] { 1, 8, 0, 0 }), "asked, standing, not held");
                AssertTreeHolds(display, 0, "one step's result twice");

                // Another step. Body 5 is moving -- at every step from here -- and the others stand: they are held.
                t = display.HeldPlacementTotals;
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[5], Stand(5, 0.25f)));
                CollectBoth(twin, "another step");
                d = Since(display, t);
                Assert.That(new[] { d.queriedOrdinary, d.promoted, d.omitted }, Is.EqualTo(new long[] { 8, 7, 0 }), "all asked; the seven that stand are held");
                AssertTreeHolds(display, 7, "seven standing over two step results");

                // From here a pass asks the two held ones near the reference point and the moving one, however far.
                for (int i = 0; i < 3; i++)
                {
                    t = display.HeldPlacementTotals;
                    long[] before = PlaceWork(display);
                    host.step++;
                    Both(twin, run => run.at.Put(run.bodies[5], Stand(5, 0.5f + 0.25f * i)));
                    CollectBoth(twin, "step " + i + ": the far body still moving");
                    d = Since(display, t);
                    Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 1, 3, 2, 1 }), "one pass: three asked, two kept, the moving one placed anew");
                    Assert.That(new[] { d.passesSelective, d.queriedNear, d.queriedOrdinary, d.omitted, d.searches }, Is.EqualTo(new long[] { 1, 2, 1, 5, 1 }), "two near, the moving one; five held and far not asked");
                    AssertTreeHolds(display, 7, "step " + i);

                    // A collection with no new step lets the adopted snapshot stand (D-204): no pass, nothing counted here.
                    t = display.HeldPlacementTotals;
                    CollectBoth(twin, "step " + i + ": the frame with no step");
                    d = Since(display, t);
                    Assert.That(new[] { d.passesSelective, d.omitted, d.promoted, d.queriedNear }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "a collection that asks nothing counts nothing as held, omitted or standing");
                }

                // The moving one comes to rest: its answer at the next step is the placement adopted from the step before.
                t = display.HeldPlacementTotals;
                host.step++;
                CollectBoth(twin, "the far body at rest");
                d = Since(display, t);
                Assert.That(new[] { d.queriedNear, d.queriedOrdinary, d.promoted, d.omitted }, Is.EqualTo(new long[] { 2, 1, 1, 5 }), "asked once more, standing: held");
                AssertTreeHolds(display, 8, "all held");
                host.step++;
                long[] rest = PlaceWork(display);
                CollectBoth(twin, "all held");
                Assert.That(Minus(PlaceWork(display), rest), Is.EqualTo(new long[] { 1, 2, 2, 0 }), "two asked of eight");

                // A host that does not vouch for its step count, or names no reference points: every one asked, none held.
                host.vouches = false;
                t = display.HeldPlacementTotals;
                rest = PlaceWork(display);
                CollectBoth(twin, "a host that does not vouch");
                d = Since(display, t);
                Assert.That(new[] { d.passesUnvouched, d.queriedUnvouched, d.passesSelective }, Is.EqualTo(new long[] { 1, 8, 0 }), "asked every one");
                Assert.That(Minus(PlaceWork(display), rest)[1], Is.EqualTo(8));
                AssertTreeHolds(display, 0, "nothing held without a step count vouched for");
                host.vouches = true;
                display.PlacementProximity = null;
                for (int i = 0; i < 3; i++)
                {
                    host.step++;
                    CollectBoth(twin, "no reference points named " + i);
                }

                AssertTreeHolds(display, 0, "nothing held with no reference points named");
            }
        }

        [Test]
        public void HeldPlacements_AHeldOneMovedFarAway_IsDrawnWhereItIsHeldThoughInView_UntilAReferencePointComesNear_OrAChangeOutsideAStepIsTold()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                SettleHeld(twin, host, 8, "eight bodies");

                // A camera that sees body 6. It is no reference point: what is near is the host's to say.
                Camera onSix = Looking(new Vector3(18f, 4f, -6f), new Vector3(0f, -0.5f, 1f), 6f);
                Color32[] before = Draw(display, onSix);
                Assert.That(Differing(before, Draw(reference, onSix)), Is.Zero, "before: the two displays draw the same");

                // Body 6 is moved at a new step. It is held and far from the reference point: not asked, drawn where it
                // is held -- in view of the camera. The display that asks every one draws it where it is.
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[6], Stand(6, 1f)));
                long[] work = PlaceWork(display);
                long written = display.InstanceRecordsWritten;
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True, "the reference collected");
                VpHeldPlacementTotals d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 2, 2, 0 }), "the two near ones asked; the moved one not");
                Assert.That(new[] { d.omitted, d.demoted }, Is.EqualTo(new long[] { 6, 0 }));
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "nothing written");
                Color32[] held = Draw(display, onSix);
                Assert.That(Differing(held, before), Is.Zero, "in view, and drawn where it is held");
                Assert.That(Differing(held, Draw(reference, onSix)), Is.GreaterThan(0), "the display that asks every one draws it moved");
                AssertTreeHolds(display, 8, "still held");

                // The reference point comes near it. The next step's pass asks it, finds it moved, and it is ordinary again.
                host.points[0] = new Vector3(18f, 0f, 0f);   // its box reaches x = -2 .. 38: every body is near
                host.step++;
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "the reference point near the moved body");
                d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 8, 7, 1 }), "every body near and asked; the moved one placed anew");
                Assert.That(new[] { d.queriedNear, d.demoted, d.omitted }, Is.EqualTo(new long[] { 8, 1, 0 }));
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 1), "and written");
                Assert.That(Differing(Draw(display, onSix), Draw(reference, onSix)), Is.Zero, "now drawn where it is");
                AssertTreeHolds(display, 7, "the moved one ordinary");
                host.step++;
                CollectBoth(twin, "it stands at the next step");
                AssertTreeHolds(display, 8, "held again, with its box where it stands now");

                // Away again, and body 3 -- held, far -- is put elsewhere outside a step, which the host says (its other
                // count): every held one is asked in the next pass. The moved one is seen and is ordinary; the ones that
                // stand stay held, their boxes untouched.
                host.points[0] = k_byTheFirstTwo;
                host.step++;
                CollectBoth(twin, "the reference point away again");
                Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                host.outside++;
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "a change outside a step, told");
                d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 8, 7, 1 }), "every one asked; the one put elsewhere placed anew");
                Assert.That(new[] { d.invalidations, d.queriedNotified, d.demoted, d.promoted, d.omitted }, Is.EqualTo(new long[] { 1, 8, 1, 0, 0 }));
                AssertTreeHolds(display, 7, "the others held still");
            }
        }

        [Test]
        public void HeldPlacements_ACutAndItsCommitInOneFamily_LeaveTheOtherFamiliesHeld_FarOnesNeitherAskedNorTakenOutOfTheTree()
        {
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                SettleHeld(twin, host, 8, "eight bodies");
                VpHeldPlacementTotals whole = display.HeldPlacementTotals;

                // Body 4 is cut (published, not committed), with no step. Its family changed: its two sides start
                // ordinary and its old box leaves the tree. The seven other families are carried: held as they were.
                // The cut body is clipped and makes conditions, so the render fragments after it are built again for
                // their ranges -- bodies 5, 6 and 7, held and far, without being asked, and they stay held.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 4, plane, Stand(4)));
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                long[] work = PlaceWork(display);
                CollectBoth(twin, "a cut published");
                VpHeldPlacementTotals d = Since(display, t);
                TestContext.Out.WriteLine("a cut published: mappings " + d.remaps + ", carried " + d.carried + ", fresh " + d.fresh + ", held ones gone " + d.droppedHeld + "; asked near " + d.queriedNear
                                          + ", ordinary " + d.queriedOrdinary + ", clipped " + d.queriedSelected + ", not asked " + d.omitted + " (of them built again for their ranges " + d.shiftedRebuilt
                                          + "); became held " + d.promoted + ", made ordinary " + d.demoted);
                Assert.That(new[] { d.remaps, d.carried, d.fresh, d.droppedHeld }, Is.EqualTo(new long[] { 1, 7, 2, 1 }), "seven families carried, the cut body's two sides new, its old box gone");
                Assert.That(new[] { d.queriedNear, d.queriedOrdinary + d.queriedSelected, d.omitted, d.shiftedRebuilt }, Is.EqualTo(new long[] { 2, 2, 5, 3 }),
                    "asked: the two near ones and the cut body's two sides; the five far held ones not, three of them built again for their ranges");
                Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(4), "four placement queries");
                Assert.That(new[] { d.demoted, d.promoted, d.structureResets, d.queriedNotified }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "no held one made ordinary, none taken out and put back");
                AssertTreeHolds(display, 7, "the seven other families held through the cut");

                // The steps go on with the cut published (the other snapshot goes through the structure too: everything
                // carried). Every pass asks the two near ones and the two clipped sides, and nobody else.
                for (int i = 0; i < 3; i++)
                {
                    t = display.HeldPlacementTotals;
                    work = PlaceWork(display);
                    host.step++;
                    CollectBoth(twin, "the cut published, step " + i);
                    d = Since(display, t);
                    Assert.That(new[] { d.fresh, d.droppedHeld, d.demoted, d.promoted }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "step " + i + ": nothing new, nothing gone, nothing changed group");
                    Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(4), "step " + i + ": four queries");
                    Assert.That(new[] { d.omitted, d.shiftedRebuilt, d.shiftedPasses }, Is.EqualTo(new long[] { 5, 3, 1 }), "step " + i + ": five far held ones not asked");
                    AssertTreeHolds(display, 7, "step " + i);
                }

                // The commit, with no step: the cut body's family changes again -- its sides are new once more -- and
                // the others are carried again. Then the two sides stand and are held like any other.
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 4, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "the commit");
                d = Since(display, t);
                Assert.That(new[] { d.carried, d.droppedHeld, d.demoted }, Is.EqualTo(new long[] { 7, 0, 0 }), "the seven carried through the commit");
                Assert.That(d.fresh, Is.GreaterThanOrEqualTo(2), "the committed sides start ordinary");
                AssertTreeHolds(display, 7, "held through the commit");
                for (int i = 0; i < 3; i++)
                {
                    host.step++;
                    CollectBoth(twin, "after the commit, step " + i);
                }

                VpHeldPlacementTotals end = display.HeldPlacementTotals;
                Assert.That(end.ordinaryAtEnd, Is.Zero, "nothing clipped left: every one held");
                AssertTreeHolds(display, end.heldAtEnd, "after the commit");
                Assert.That(end.heldAtEnd, Is.GreaterThanOrEqualTo(9));
                d = Since(display, whole);
                Assert.That(new[] { d.structureResets, d.demoted, d.queriedNotified }, Is.EqualTo(new long[] { 0, 0, 0 }), "through the cut and the commit: nothing forgotten whole, no held one made ordinary");
                Assert.That(d.droppedHeld, Is.EqualTo(1), "one box taken out of the tree in all: the cut body's own");
            }
        }

        [Test]
        public void HeldPlacements_RegistrationsRetiredFromTheFrontAndTheMiddleAndOneAdded_RenumberTheOthers_WhoStayHeldWithTheirBoxes_AndAreFoundByThemWhenAPointComesNear()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                SettleHeld(twin, host, 8, "eight bodies");
                VpHeldPlacementTotals whole = display.HeldPlacementTotals;

                // The first registration retired (near the reference point), then one in the middle: every one after
                // them is numbered anew. Nothing but the retired ones' own boxes leaves the tree; nobody is asked for it.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[0]), Is.True));
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "the first retired, " + i);
                }

                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[3]), Is.True));
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "one in the middle retired, " + i);
                }

                VpHeldPlacementTotals d = Since(display, whole);
                TestContext.Out.WriteLine("two retired: mappings " + d.remaps + ", carried " + d.carried + ", fresh " + d.fresh + ", held ones gone " + d.droppedHeld + "; asked near " + d.queriedNear + ", ordinary "
                                          + d.queriedOrdinary + ", not asked " + d.omitted + "; became held " + d.promoted + ", made ordinary " + d.demoted);
                Assert.That(d.remaps, Is.GreaterThanOrEqualTo(2), "the structure was gone through again");
                Assert.That(new[] { d.droppedHeld, d.fresh, d.demoted, d.promoted, d.queriedOrdinary, d.structureResets }, Is.EqualTo(new long[] { 2, 0, 0, 0, 0, 0 }),
                    "the two retired ones' boxes gone; nobody else taken out, put back, asked as ordinary or forgotten");
                AssertTreeHolds(display, 6, "six left, held all along");

                // A body added: it alone starts ordinary and is held once it stands.
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                CollectBoth(twin, "a body taken in");
                d = Since(display, t);
                Assert.That(new[] { d.carried, d.fresh, d.droppedHeld, d.demoted }, Is.EqualTo(new long[] { 6, 1, 0, 0 }), "six carried, one new");
                AssertTreeHolds(display, 6, "the six held through the addition");
                for (int i = 0; i < 3; i++)
                {
                    host.step++;
                    CollectBoth(twin, "after the addition, step " + i);
                }

                AssertTreeHolds(display, 7, "the new one held too");
                d = Since(display, whole);
                Assert.That(new[] { d.promoted, d.demoted, d.droppedHeld }, Is.EqualTo(new long[] { 1, 0, 2 }), "in all: one became held (the new one), none made ordinary, two boxes gone (the retired)");

                // Renumbered, each box still names its own body: body 6 -- held, far, its number changed twice -- is moved
                // and not asked; when the reference point comes near it, it is the one found, asked and placed anew.
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[6], Stand(6, 1f)));
                long written = display.InstanceRecordsWritten;
                long[] work = PlaceWork(display);
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True, "the reference collected");
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 1, 1, 0 }), "the one near body left (body 1) asked; the moved one not");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "nothing written");
                host.points[0] = new Vector3(18f, 0f, 0f);   // x from -2 to 38: bodies 1, 2, 4, 5, 6, 7; the added one at 60 is far
                host.step++;
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "the reference point near the moved body");
                d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 6, 5, 1 }), "six near and asked; the moved one placed anew");
                Assert.That(new[] { d.queriedNear, d.demoted, d.omitted }, Is.EqualTo(new long[] { 6, 1, 1 }), "found by its own box, made ordinary; the far added one not asked");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 1), "and written: what the display holds is what the one that asks every one holds");
            }
        }

        [Test]
        public void HeldPlacements_ACollectionThatIsNotAdopted_LeavesNothingStale_AndTheMoveItSawIsNotLost()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                SettleHeld(twin, host, 8, "eight bodies");

                // A retirement leaves a hole (a compaction is planned for it); the seven left stay held.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "one retired, " + i);
                }

                AssertTreeHolds(display, 7, "seven left, held");
                CollectBoth(twin, "at rest");
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);

                // A new step, with a change outside a step told as well: every held one is asked. Body 0 moved and is
                // ordinary again; the six others stand and stay held -- all in a collection that is refused right after
                // its compaction plan and so not adopted.
                host.step++;
                host.outside++;
                Both(twin, run => run.at.Put(run.bodies[0], Stand(0, 1f)));
                Drawn adopted = Capture(display);
                display.FailAfterCompactionPlanForTest = true;
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                display.FailAfterCompactionPlanForTest = false;
                VpHeldPlacementTotals d = Since(display, t);
                Assert.That(new[] { d.passesSelective, d.invalidations, d.queriedNotified, d.demoted, d.promoted }, Is.EqualTo(new long[] { 1, 1, 7, 1, 0 }),
                    "the layout: that collection asked every held one; the moved one is ordinary, no box was taken out for the six that stand");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection");
                AssertTreeHolds(display, 6, "the six that stand, held still");

                // The next collection (the same step, not adopted yet) asks the ordinary one: the move is not lost. The
                // six held are far and are not asked; what the display holds is what the one that asks every one holds.
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                long[] work = PlaceWork(display);
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "the collection after the refusal");
                d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 1, 0, 1 }), "the moved body asked and placed anew");
                Assert.That(new[] { d.omitted, d.queriedOrdinary }, Is.EqualTo(new long[] { 6, 1 }));
                CollectBoth(twin, "the compaction");
                CollectBoth(twin, "at rest");
                host.step++;
                CollectBoth(twin, "the moved body stands at the next step");
                AssertTreeHolds(display, 7, "all held");
                host.step++;
                work = PlaceWork(display);
                CollectBoth(twin, "all held, one near");
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 1, 1, 0 }), "the one near body asked, where it was moved to");
            }
        }

        [Test]
        public void HeldPlacements_AStructureGoneThroughInACollectionThatIsNotAdopted_LeavesTheUnchangedHeld_TheRetiredOnesBoxGone_AndTheMoveItSawIsNotLost()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                SettleHeld(twin, host, 8, "eight bodies");
                VpHeldPlacementTotals whole = display.HeldPlacementTotals;

                // Body 5 retired: its box leaves the tree, the seven others stay held. (The retirement also leaves a
                // hole in the draw data, for which a compaction is planned once the draw data stands.)
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[5]), Is.True));
                CollectBoth(twin, "one retired");
                host.step++;
                CollectBoth(twin, "the retirement, a step later");
                CollectBoth(twin, "at rest");
                AssertTreeHolds(display, 7, "seven left, held");
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);

                // A new step in which body 0 -- held, near -- has moved. The snapshot built for this collection is the
                // one that has not gone through the retirement's structure yet: it goes through it now, the held
                // placements are mapped to it, body 0 is asked and made ordinary -- and the collection is refused right
                // after its compaction plan. Not adopted.
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[0], Stand(0, 1f)));
                Drawn adopted = Capture(display);
                display.FailAfterCompactionPlanForTest = true;
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                display.FailAfterCompactionPlanForTest = false;
                VpHeldPlacementTotals d = Since(display, t);
                TestContext.Out.WriteLine("the refused collection: structure passes " + d.passesStructure + ", mappings " + d.remaps + ", carried " + d.carried + ", fresh " + d.fresh + ", held ones gone " + d.droppedHeld
                                          + "; asked near " + d.queriedNear + ", made ordinary " + d.demoted + ", not asked " + d.omitted);
                Assert.That(new[] { d.passesStructure, d.remaps, d.carried, d.fresh, d.droppedHeld }, Is.EqualTo(new long[] { 1, 1, 7, 0, 0 }), "the layout: the refused collection went through the structure, every part carried");
                Assert.That(new[] { d.queriedNear, d.demoted, d.omitted }, Is.EqualTo(new long[] { 2, 1, 5 }), "the two near ones asked, one found moved; the five far ones not asked");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection");
                AssertTreeHolds(display, 6, "the six that did not move, held still");

                // The next collection builds on the structure the refused one went through (the same snapshot, which
                // was not adopted): the six held are as that mapping left them; the moved body is asked and placed
                // anew. What the display holds is what the one that asks every one holds.
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                long[] work = PlaceWork(display);
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "the collection after the refusal");
                d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 2, 1, 1 }), "the moved body asked and placed anew (the move is not lost), the other near one asked");
                Assert.That(new[] { d.fresh, d.droppedHeld, d.omitted, d.demoted, d.structureResets }, Is.EqualTo(new long[] { 0, 0, 5, 0, 0 }), "nothing started anew, nothing taken out, nothing forgotten: the five far ones not asked");
                AssertTreeHolds(display, 6, "after the collection that was adopted");
                CollectBoth(twin, "the compaction");
                CollectBoth(twin, "at rest");
                host.step++;
                CollectBoth(twin, "the moved body stands at the next step");
                AssertTreeHolds(display, 7, "all held");
                d = Since(display, whole);
                Assert.That(new[] { d.droppedHeld, d.promoted, d.demoted, d.structureResets, d.fresh }, Is.EqualTo(new long[] { 1, 1, 1, 0, 0 }),
                    "in all: one box gone (the retired body's); the moved body made ordinary and held again; nothing forgotten whole, nothing started anew");
                host.step++;
                work = PlaceWork(display);
                CollectBoth(twin, "all held, one near");
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 2, 2, 0 }), "the two near ones asked of seven");
            }
        }

        // The changes outside a step as a host tells them with their targets (D-207): the fragments told, in order, read
        // by a cursor; and the count of the changes told with no target. Every telling also counts in the host's
        // count of changes outside a step, which is what keeps a collection from letting its snapshot stand.
        private sealed class ToldChanges
        {
            private readonly HoldHost _host;
            private readonly List<LogicalFragmentId> _log = new List<LogicalFragmentId>();
            private long _untargeted;

            public ToldChanges(HoldHost host, VpLogicalCutDisplay display)
            {
                _host = host;
                display.PlacementChanges = (ref long cursor, List<LogicalFragmentId> into, out long untargeted) =>
                {
                    untargeted = _untargeted;
                    bool kept = cursor >= 0 && cursor <= _log.Count;
                    if (kept) for (int i = (int)cursor; i < _log.Count; i++) into.Add(_log[i]);
                    cursor = _log.Count;
                    return kept;
                };
            }

            public void Of(LogicalFragmentId fragment)
            {
                _log.Add(fragment);
                _host.outside++;
            }

            public void OfNothingNamed()
            {
                _untargeted++;
                _host.outside++;
            }
        }

        [Test]
        public void HeldPlacements_AChangeToldOfAFamily_HasThatFamilysHeldOnesAsked_AndNoOthers_WithNoStep_SeveralAtOnce_ThroughARenumbering_AndInACollectionNotAdopted()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                List<LogicalFragmentId> bodies = twin.kept.bodies;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                var told = new ToldChanges(host, display);
                SettleHeld(twin, host, 8, "eight bodies");
                VpHeldPlacementTotals whole = display.HeldPlacementTotals;

                // Body 6 -- held, far -- is put elsewhere with no step, and that is told of it. The pass asks the two
                // near ones and body 6: nobody else.
                Both(twin, run => run.at.Put(run.bodies[6], Stand(6, 1f)));
                told.Of(bodies[6]);
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                long[] work = PlaceWork(display);
                CollectBoth(twin, "one told, no step");
                VpHeldPlacementTotals d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 3, 2, 1 }), "three asked: the two near ones and the one told of, which is placed anew");
                Assert.That(new[] { d.toldFamilies, d.queriedNotified, d.queriedNear, d.omitted, d.demoted, d.invalidations }, Is.EqualTo(new long[] { 1, 1, 2, 5, 1, 0 }),
                    "one family told: its one held render fragment asked; the five other far ones not; not every held one");
                AssertTreeHolds(display, 7, "the others held, their boxes untouched");

                // Two more at once, still with no step: both are asked and both seen.
                Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                Both(twin, run => run.at.Put(run.bodies[5], Stand(5, 2f)));
                told.Of(bodies[3]);
                told.Of(bodies[5]);
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "two told at once");
                d = Since(display, t);
                Assert.That(new[] { d.toldFamilies, d.queriedNotified, d.demoted, d.invalidations, d.omitted }, Is.EqualTo(new long[] { 2, 2, 2, 0, 3 }), "both asked, both found moved; three far held ones left alone");
                Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(5), "five queries: two near, two told of, and the one made ordinary before");

                // They stand at later steps and are held again. Then a change is told of body 7 that moved nothing (what
                // answers for it changed, say): it is asked, stands where it is held, and stays held -- its box untouched.
                for (int i = 0; i < 2; i++)
                {
                    host.step++;
                    CollectBoth(twin, "standing again " + i);
                }

                AssertTreeHolds(display, 8, "all held again");
                told.Of(bodies[7]);
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "told, and nothing moved");
                d = Since(display, t);
                Assert.That(new[] { d.queriedNotified, d.demoted, d.promoted, d.omitted, d.invalidations }, Is.EqualTo(new long[] { 1, 0, 0, 5, 0 }), "asked once, standing: held as it was");
                AssertTreeHolds(display, 8, "nothing taken out of the tree");

                // A renumbering and a telling in one collection: the first registration is retired -- every other one
                // gets a new number -- and body 4 is moved and told of. It is body 4 that is asked.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[0]), Is.True));
                Both(twin, run => run.at.Put(run.bodies[4], Stand(4, 1f)));
                told.Of(bodies[4]);
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "a retirement and a telling");
                for (int i = 0; i < 2; i++)
                {
                    host.step++;
                    CollectBoth(twin, "after the retirement " + i);
                }

                CollectBoth(twin, "at rest");
                d = Since(display, t);
                TestContext.Out.WriteLine("a retirement and a telling: mappings " + d.remaps + ", carried " + d.carried + ", fresh " + d.fresh + ", held ones gone " + d.droppedHeld + "; told " + d.toldFamilies + ", asked for it "
                                          + d.queriedNotified + ", near " + d.queriedNear + ", ordinary " + d.queriedOrdinary + "; made ordinary " + d.demoted + ", became held " + d.promoted);
                Assert.That(new[] { d.toldFamilies, d.queriedNotified, d.demoted, d.promoted, d.droppedHeld, d.fresh, d.invalidations }, Is.EqualTo(new long[] { 1, 1, 1, 1, 1, 0, 0 }),
                    "the one told of asked under its new number, made ordinary and held again; the retired one's box gone; nobody else touched");
                AssertTreeHolds(display, 7, "seven left, all held");

                // A telling in a collection that is not adopted: body 2 -- held, far -- moved and told of; the collection
                // is refused after its compaction plan. The next one asks it again (it is ordinary now): not lost.
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 1f)));
                told.Of(bodies[2]);
                Drawn adopted = Capture(display);
                display.FailAfterCompactionPlanForTest = true;
                t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                display.FailAfterCompactionPlanForTest = false;
                d = Since(display, t);
                Assert.That(new[] { d.toldFamilies, d.queriedNotified, d.demoted, d.invalidations }, Is.EqualTo(new long[] { 1, 1, 1, 0 }), "the layout: the refused collection asked the one told of");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                work = PlaceWork(display);
                CollectBoth(twin, "the collection after the refusal");
                Assert.That(Minus(PlaceWork(display), work)[3], Is.EqualTo(1), "the moved body placed anew: what was told is not lost with the refused collection");
                CollectBoth(twin, "the compaction");
                host.step++;
                CollectBoth(twin, "a step later");
                AssertTreeHolds(display, 7, "all held");

                // A change told with no target: every held one is asked, as before.
                Both(twin, run => run.at.Put(run.bodies[7], Stand(7, 3f)));
                told.OfNothingNamed();
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "a change that names nothing");
                d = Since(display, t);
                Assert.That(new[] { d.invalidations, d.queriedNotified, d.demoted, d.omitted }, Is.EqualTo(new long[] { 1, 7, 1, 0 }), "every held one asked; the moved one found");
                d = Since(display, whole);
                Assert.That(d.structureResets, Is.Zero, "nothing was forgotten whole in all of this");
            }
        }

        [Test]
        public void HeldPlacementTree_FindsExactlyTheBoxesThatMeetABox_ThroughInsertionsAndRemovals()
        {
            var tree = new VpHeldPlacements.Tree();
            var random = new Unity.Mathematics.Random(20261007);
            const int Items = 600;
            var lo = new float3[Items];
            var hi = new float3[Items];
            var leaf = new int[Items];
            for (int i = 0; i < Items; i++) leaf[i] = -1;
            var bits = new ulong[(Items + 63) / 64];
            int inTree = 0, highest = 0;
            for (int round = 0; round < 4000; round++)
            {
                int i = random.NextInt(Items);
                if (leaf[i] >= 0)
                {
                    tree.Remove(leaf[i]);
                    leaf[i] = -1;
                    inTree--;
                }
                else
                {
                    // Sorted along x now and then (a row of buildings), scattered otherwise.
                    float3 centre = round % 3 == 0 ? new float3(i * 3f, 0f, 0f) : random.NextFloat3(new float3(-200f), new float3(200f));
                    float3 reach = random.NextFloat3(new float3(0.5f), new float3(6f));
                    lo[i] = centre - reach;
                    hi[i] = centre + reach;
                    leaf[i] = tree.Insert(lo[i], hi[i], i);
                    inTree++;
                }

                Assert.That(tree.Count, Is.EqualTo(inTree));
                if (round % 50 != 0) continue;
                Assert.That(tree.Check(), Is.Null, "round " + round);
                highest = math.max(highest, tree.HeightForTest);
                float3 at = random.NextFloat3(new float3(-220f), new float3(220f));
                float3 boxLo = at - 20f, boxHi = at + 20f;
                System.Array.Clear(bits, 0, bits.Length);
                bits[0] = 1UL;   // item 0 marked beforehand: not counted as newly found
                int found = tree.Pick(boxLo, boxHi, bits);
                int expected = 0;
                for (int k = 0; k < Items; k++)
                {
                    bool meets = leaf[k] >= 0 && !(math.any(lo[k] > boxHi) || math.any(hi[k] < boxLo));
                    bool marked = (bits[k >> 6] & (1UL << (k & 63))) != 0;
                    if (k == 0) { Assert.That(marked, Is.True); continue; }
                    Assert.That(marked, Is.EqualTo(meets), "round " + round + ", item " + k);
                    if (meets) expected++;
                }

                Assert.That(found, Is.EqualTo(expected), "round " + round + ": the count of the newly found");
            }

            TestContext.Out.WriteLine("items in the tree at the end " + inTree + "; the tree's greatest height at a check " + highest);
            Assert.That(highest, Is.LessThan(40), "kept in balance (600 items in a row would be 600 high in a tree that is not)");

            // A touching box counts; and the world box of a turned, moved local box with its margin.
            tree.Clear();
            int one = tree.Insert(new float3(0f), new float3(1f), 3);
            System.Array.Clear(bits, 0, bits.Length);
            Assert.That(tree.Pick(new float3(1f, 0f, 0f), new float3(2f, 1f, 1f), bits), Is.EqualTo(1), "touching counts");
            System.Array.Clear(bits, 0, bits.Length);
            Assert.That(tree.Pick(new float3(1.001f, 0f, 0f), new float3(2f, 1f, 1f), bits), Is.Zero, "apart does not");
            tree.Remove(one);
            Assert.That(tree.Check(), Is.Null);
            VpHeldPlacements.WorldBox(new Bounds(new Vector3(1f, 0f, 0f), new Vector3(2f, 4f, 6f)), Matrix4x4.TRS(new Vector3(10f, 20f, 30f), Quaternion.Euler(0f, 90f, 0f), Vector3.one), 0.5f,
                out float3 worldLo, out float3 worldHi);
            Assert.That(math.length(worldLo - new float3(10f - 3f - 0.5f, 20f - 2f - 0.5f, 30f - 1f - 1f - 0.5f)), Is.LessThan(1e-4f), "the low corner: turned a quarter about y, moved, widened");
            Assert.That(math.length(worldHi - new float3(10f + 3f + 0.5f, 20f + 2f + 0.5f, 30f - 1f + 1f + 0.5f)), Is.LessThan(1e-4f), "the high corner");
        }
    }
}
