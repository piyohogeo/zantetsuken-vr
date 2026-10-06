using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Placements asked only when they can have changed (TL, 2026-10-07; DESIGN 5.6, D-204). A display whose host
    /// counts the physics steps simulated and the placement inputs changed outside a step lets its adopted snapshot
    /// stand for a collection that finds both counts as they were at the last adopted placement pass and the structure
    /// as it was: no placement is asked, no clip or cap made again. Every case runs that display beside the same display
    /// given no counts -- which asks every time, as before -- and, wherever the two are told the same things, compares
    /// what they hold after every collection (sides, clips, caps, commands, transforms, each one's GPU buffers).
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // What the Place passes did so far, both kinds together: passes, queries, kept as settled, placed anew.
        private static long[] PlaceWork(VpLogicalCutDisplay display)
        {
            VpPlaceCounts only = PlacementOnlyOf(display, out VpPlaceCounts structural);
            return new[]
            {
                only.passes + structural.passes, only.queries + structural.queries, only.keptAsSettled + structural.keptAsSettled, only.placedAnew + structural.placedAnew,
            };
        }

        private static long[] Minus(long[] a, long[] b)
        {
            var d = new long[a.Length];
            for (int i = 0; i < a.Length; i++) d[i] = a[i] - b[i];
            return d;
        }

        [Test]
        public void PlaceReuse_WithoutANewStep_NoPlacementIsAsked_AndEveryNewStepIsReflected_AndAReuseIsNoEvidenceOfRest()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                long step = 1, outside = 0;
                display.PlacementSerial = (out long s, out long o) => { s = step; o = outside; return true; };
                CollectBoth(twin, "the bodies");
                Assert.That(display.PlacementReuses, Is.Zero, "the first collection asks: nothing was adopted yet");
                long[] before = PlaceWork(display);
                long history = display.HistoryRegistrationWrites;
                CollectBoth(twin, "no new step");
                Assert.That(display.PlacementReuses, Is.EqualTo(1), "the counts are as they were: the adopted snapshot stands");
                Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 0, 0, 0, 0 }), "no pass, no query -- and nothing counted as kept or placed");
                Assert.That(display.PlacementQueriesOmitted, Is.EqualTo(8), "eight queries not made");

                // A body in motion: every step puts it elsewhere; between two steps there is a collection with no step.
                for (int i = 0; i < 4; i++)
                {
                    step++;
                    float lift = 0.25f * (i + 1);
                    Both(twin, run => run.at.Put(run.bodies[2], Stand(2, lift)));
                    before = PlaceWork(display);
                    long reuses = display.PlacementReuses;
                    Kept kept = Kept.Of(display);
                    CollectBoth(twin, "step " + i + ": the body moved");
                    Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "a new step: asked");
                    Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 1, 8, 7, 1 }), "one pass, eight queries, seven kept, the moved one placed anew");
                    Assert.That(Kept.Of(display).Since(kept).written, Is.EqualTo(1), "and its record written");

                    before = PlaceWork(display);
                    history = display.HistoryRegistrationWrites;
                    kept = Kept.Of(display);
                    CollectBoth(twin, "step " + i + ": the frame with no step");
                    Assert.That(display.PlacementReuses, Is.EqualTo(reuses + 1), "no new step: the adopted snapshot stands");
                    Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 0, 0, 0, 0 }),
                        "nothing asked; and the moving body is not counted as kept -- a collection that asks nothing is no evidence that anything stands still");
                    Assert.That(Kept.Of(display).Since(kept).written, Is.Zero, "nothing written");
                    Assert.That(display.HistoryRegistrationWrites, Is.EqualTo(history), "and no write history of it");
                }

                // A root put somewhere outside a step. Not said: the display, asking nothing, does not see it. Said (the
                // host's other count): the next collection asks, and it is where it was put.
                Slots body5 = SlotsOf(display, twin.kept.bodies[5], "body 5");
                twin.kept.at.Put(twin.kept.bodies[5], Stand(5, 1f));
                twin.everything.at.Put(twin.everything.bodies[5], Stand(5, 1f));
                long written = display.InstanceRecordsWritten;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "a change nobody told of, with no step: not asked, not seen");
                outside++;
                before = PlaceWork(display);
                CollectBoth(twin, "the change outside a step, told");
                Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 1, 8, 7, 1 }), "asked: the body put elsewhere is placed anew");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 1), "and written");
                AssertSlotsStand(display, twin.kept.bodies[5], body5, "its slots stand");
                CollectBoth(twin, "at rest");

                // A host that cannot vouch for its counts is asked every time.
                bool vouches = false;
                display.PlacementSerial = (out long s, out long o) => { s = step; o = outside; return vouches; };
                long reusesNow = display.PlacementReuses;
                for (int f = 0; f < 3; f++) CollectBoth(twin, "a host that does not vouch " + f);
                Assert.That(display.PlacementReuses, Is.EqualTo(reusesNow), "asked every time, as a display given no counts is");
            }
        }

        [Test]
        public void PlaceReuse_AStructuralChangeWithoutAStep_IsPlaced_AndClipsAndCapsStandBetween_WhileACameraThatMovesIsDrawnAnew()
        {
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                display.PlacementSerial = (out long s, out long o) => { s = 7; o = 0; return true; };   // no step ever comes
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Assert.That(display.PlacementReuses, Is.EqualTo(2), "the layout: the first asks, the two after it let it stand");

                // A cut published with no step: the structure changed, so it is built and placed.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 4, plane, Stand(4)));
                long reuses = display.PlacementReuses;
                long[] before = PlaceWork(display);
                CollectBoth(twin, "a cut published, no step");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "a structural change: placed");
                Assert.That(Minus(PlaceWork(display), before)[1], Is.EqualTo(9), "nine render fragments asked");
                Assert.That(SlotsOf(display, twin.kept.bodies[4], "the cut body").renderFragments, Is.EqualTo(2));
                CollectBoth(twin, "the frame after");

                // At rest with the cut published: the clipped sides and their caps stand as adopted -- nothing is asked or
                // made again -- and are what the display that asks every time makes of them (compared by CollectBoth).
                before = PlaceWork(display);
                Kept kept = Kept.Of(display);
                for (int f = 0; f < 3; f++) CollectBoth(twin, "at rest, the cut published " + f);
                Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 0, 0, 0, 0 }), "three collections, no placement asked");
                Kept rest = Kept.Of(display).Since(kept);
                Assert.That(new[] { rest.written, rest.capNormalVertices, rest.capNormalsMade }, Is.EqualTo(new long[] { 0, 0, 0 }), "no record written, no cap normal made or sent again");

                // The camera alone moves: nothing is placed, and each view is prepared and drawn anew -- the same pictures
                // as the display that asks every time, from here and from there.
                Camera here = Looking(new Vector3(12f, 4f, -6f), new Vector3(0f, -0.5f, 1f), 6f);
                Color32[] fromHere = Draw(display, here);
                Assert.That(Differing(fromHere, Draw(reference, here)), Is.Zero, "from here: the two displays draw the same");
                CollectBoth(twin, "between the two views");
                Camera there = Looking(new Vector3(14f, 5f, 6f), new Vector3(-0.2f, -0.6f, -1f), 6f);
                Color32[] fromThere = Draw(display, there);
                Assert.That(Differing(fromThere, Draw(reference, there)), Is.Zero, "from there: the same again");
                Assert.That(Differing(fromHere, fromThere), Is.GreaterThan(0), "and the view did change");
                Assert.That(Minus(PlaceWork(display), before), Is.EqualTo(new long[] { 0, 0, 0, 0 }), "with no placement asked for it");

                // The commit, a body taken in, a retirement: each without a step, each placed.
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 4, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                reuses = display.PlacementReuses;
                CollectBoth(twin, "the commit, no step");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "placed");
                CollectBoth(twin, "the frame after the commit");
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                reuses = display.PlacementReuses;
                CollectBoth(twin, "a body taken in, no step");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "placed");
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                reuses = display.PlacementReuses;
                CollectBoth(twin, "a retirement, no step");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "placed");
                for (int f = 0; f < 4; f++) CollectBoth(twin, "at rest " + f);
                Assert.That(display.PlacementReuses, Is.GreaterThan(reuses), "and standing again once the structure stands");
            }
        }

        [Test]
        public void PlaceReuse_ACollectionThatIsNotAdopted_LosesNoStep_AndARefusedCollectionThatAskedNothingLeavesTheAdoptedSnapshotInPlace()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                long step = 1, outside = 0;
                display.PlacementSerial = (out long s, out long o) => { s = step; o = outside; return true; };
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                CollectBoth(twin, "one retired");
                CollectBoth(twin, "the retirement's second structural collection");
                CollectBoth(twin, "at rest");

                // A new step moves body 3, and the collection that asks it plans a compaction (the hole) and is refused
                // right after the plan: not adopted. The counts adopted are still the older ones.
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                step++;
                Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                Drawn adopted = Capture(display);
                display.FailAfterCompactionPlanForTest = true;
                long reuses = display.PlacementReuses;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "the layout: that collection asked the placements");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection");
                display.FailAfterCompactionPlanForTest = false;

                // The next collection finds the step it has not adopted and asks: body 3 is where the step put it.
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                long[] before = PlaceWork(display);
                CollectBoth(twin, "the collection after the refusal");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses), "asked: the step was not adopted yet");
                Assert.That(Minus(PlaceWork(display), before)[3], Is.EqualTo(1), "the moved body placed anew");
                CollectBoth(twin, "the compaction");
                Assert.That(display.Compactions, Is.EqualTo(1));
                CollectBoth(twin, "at rest");

                // A collection that asks nothing, refused: the adopted snapshot is back in its place, and what is adopted
                // and what the GPU holds are as they were; the next collections go on.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[6]), Is.True));
                CollectBoth(twin, "another retired");
                CollectBoth(twin, "its second structural collection");
                adopted = Capture(display);
                reuses = display.PlacementReuses;
                display.FailAfterCompactionPlanForTest = true;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses + 1), "the layout: that collection let the adopted snapshot stand");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection that asked nothing");
                display.FailAfterCompactionPlanForTest = false;
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                CollectBoth(twin, "the collection after it");
                CollectBoth(twin, "the compaction");
                Assert.That(display.Compactions, Is.EqualTo(2), "a compaction keeps its own chance in a collection that asks nothing");
                step++;
                Both(twin, run => run.at.Put(run.bodies[0], Stand(0, 1f)));
                CollectBoth(twin, "a new step: a body moved");
                CollectBoth(twin, "at rest");
            }
        }
    }
}
