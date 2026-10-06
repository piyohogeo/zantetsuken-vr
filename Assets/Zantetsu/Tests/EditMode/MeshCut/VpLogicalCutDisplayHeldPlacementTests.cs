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
                Assert.That(new[] { d.passesStructure, d.queriedStructure, d.passesSelective }, Is.EqualTo(new long[] { 2, 16, 0 }), "two passes through the structure: every body asked in each");
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
                // count): every held one is asked in the next pass, the moved one is seen, and the ones that stand are
                // held again at once (their adopted placements are of earlier steps).
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
                Assert.That(new[] { d.invalidations, d.queriedOrdinary, d.promoted, d.omitted }, Is.EqualTo(new long[] { 1, 8, 7, 0 }));
                AssertTreeHolds(display, 7, "the others held again");
            }
        }

        [Test]
        public void HeldPlacements_ARegistrationARetirementACutAndACommit_ForgetWhatWasHeld_AndAClippedOneIsNeverHeld()
        {
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                SettleHeld(twin, host, 8, "eight bodies");

                // A body taken in, with no step: the structure is gone through again and nothing is held.
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                CollectBoth(twin, "a body taken in");
                Assert.That(Since(display, t).structureResets, Is.EqualTo(1));
                AssertTreeHolds(display, 0, "a body taken in: everything forgotten");
                host.step++;
                SettleHeld(twin, host, 9, "nine bodies");
                host.step++;
                long[] work = PlaceWork(display);
                CollectBoth(twin, "nine held");
                Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(2), "the two near ones asked of nine");

                // A retirement: the same.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                CollectBoth(twin, "one retired");
                AssertTreeHolds(display, 0, "a retirement: everything forgotten");
                host.step++;
                SettleHeld(twin, host, 8, "eight left");
                host.step++;
                work = PlaceWork(display);
                CollectBoth(twin, "eight held");
                Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(1), "the one near body left is asked: no box of the retired one answers");

                // A cut published: forgotten again. While it is published the cut body is clipped -- it has selected
                // boundaries and is never held -- and every render fragment after it has its ranges begin elsewhere,
                // so it is asked and built again in every pass, as before, and is not held either.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 4, plane, Stand(4)));
                CollectBoth(twin, "a cut published");
                AssertTreeHolds(display, 0, "a publication: everything forgotten");
                host.step++;
                CollectBoth(twin, "the published cut, the other snapshot");
                for (int i = 0; i < 3; i++)
                {
                    t = display.HeldPlacementTotals;
                    work = PlaceWork(display);
                    host.step++;
                    CollectBoth(twin, "the cut published, step " + i);
                    VpHeldPlacementTotals d = Since(display, t);
                    VpHeldPlacementTotals all = display.HeldPlacementTotals;
                    TestContext.Out.WriteLine("the cut published, step " + i + ": held " + all.heldAtEnd + ", ordinary " + all.ordinaryAtEnd + "; this pass: omitted " + d.omitted + ", asked near " + d.queriedNear
                                              + ", ordinary " + d.queriedOrdinary + ", clipped " + d.queriedSelected + ", after shifted ranges though held " + d.queriedShifted + "; passes with shifted ranges " + d.shiftedPasses);
                    Assert.That(d.shiftedPasses, Is.EqualTo(1), "the clipped render fragment's ranges shift the ones after it");
                    Assert.That(d.queriedSelected, Is.GreaterThanOrEqualTo(1), "the clipped one is asked in every pass");
                    Assert.That(d.omitted + d.queriedNear + d.queriedOrdinary + d.queriedSelected + d.queriedShifted, Is.EqualTo(all.heldAtEnd + all.ordinaryAtEnd), "every render fragment is omitted or asked, once");
                    Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(d.queriedNear + d.queriedOrdinary + d.queriedSelected + d.queriedShifted), "and the queries are the ones counted as asked");
                    Assert.That(all.ordinaryAtEnd, Is.GreaterThanOrEqualTo(2), "the clipped one and those after it stay ordinary");
                    Assert.That(display.HeldPlacementsForTest.TreeForTest.Check(), Is.Null);
                }

                // The commit: forgotten again, and with nothing clipped left every render fragment is held once it stands.
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 4, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                CollectBoth(twin, "the commit");
                AssertTreeHolds(display, 0, "a commit: everything forgotten");
                host.step++;
                CollectBoth(twin, "the commit, the other snapshot");
                host.step++;
                CollectBoth(twin, "the commit, another step");
                VpHeldPlacementTotals end = display.HeldPlacementTotals;
                Assert.That(end.ordinaryAtEnd, Is.Zero, "nothing clipped: all held");
                AssertTreeHolds(display, end.heldAtEnd, "after the commit");
                Assert.That(end.heldAtEnd, Is.GreaterThanOrEqualTo(9), "the two sides of the cut body among them");
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

                // A retirement leaves a hole (a compaction is planned for it) and forgets what was held; the seven left
                // are held again once they stand.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                CollectBoth(twin, "one retired");
                AssertTreeHolds(display, 0, "after the retirement");
                host.step++;
                SettleHeld(twin, host, 7, "seven left");
                CollectBoth(twin, "at rest");
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);

                // A new step, with a change outside a step told as well: every held one is asked. Body 0 moved; the six
                // others stand and are held again -- all in a collection that is refused right after its compaction plan
                // and so not adopted.
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
                Assert.That(new[] { d.passesSelective, d.invalidations, d.queriedOrdinary, d.promoted }, Is.EqualTo(new long[] { 1, 1, 7, 6 }), "the layout: that collection asked every one and held the six that stand");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection");
                AssertTreeHolds(display, 6, "held in a collection that was not adopted: they do stand where the adopted snapshot has them");

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
