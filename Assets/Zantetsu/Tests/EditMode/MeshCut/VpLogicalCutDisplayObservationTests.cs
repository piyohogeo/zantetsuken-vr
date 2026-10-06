using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The observation of a collection by stage (2026-10-07; for a Player without the Profiler's markers): the Place
    /// pass says how many render fragments it placed anew, why -- by the first condition of its keep test that did not
    /// hold -- and what they came out with; the snapshot says what a structure build rebuilt; the display times its
    /// stages with the clock. Counts only: nothing here changes what is drawn (the cases run beside the display that
    /// collects everything, as the others do).
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private static VpPlaceCounts PlacementOnlyOf(VpLogicalCutDisplay display, out VpPlaceCounts structural)
        {
            structural = new VpPlaceCounts();
            var placementOnly = new VpPlaceCounts();
            display.PlaceTotalsForTest(structural, placementOnly);
            return placementOnly;
        }

        [Test]
        public void Observation_ThePlacePass_SaysWhyARenderFragmentIsPlacedAnew_AndWhatItCameOutWith_AndTheStagesAreCountedAndTimed()
        {
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpSnapshotStageTotals stages0 = display.StageTotalsForTest;
                CollectBoth(twin, "the bodies");
                VpSnapshotStageTotals first = display.StageTotalsForTest;
                first.Subtract(stages0);
                Assert.That(new[] { first.walks, first.familiesRebuilt, first.registrationsRebuilt, first.renderFragmentsRebuilt, first.validateCalls, first.collectCalls, first.groupCalls },
                    Is.EqualTo(new long[] { 1, 8, 8, 8, 8, 8, 8 }), "the first build: every family rebuilt, a validation a family, a collection and a grouping a registration");
                Assert.That(first.structureSeconds, Is.GreaterThanOrEqualTo(first.validateSeconds + first.collectSeconds + first.groupSeconds - 1e-9),
                    "a structure build holds its validation, its collections and its groupings");
                CollectBoth(twin, "settling");
                CollectBoth(twin, "settling again");

                // Nothing moves: every render fragment is kept as it was taken over.
                VpPlaceCounts before = PlacementOnlyOf(display, out _);
                CollectBoth(twin, "nothing moves");
                var pass = new VpPlaceCounts();
                pass.AddDifference(PlacementOnlyOf(display, out _), before);
                Assert.That(new[] { pass.passes, pass.renderFragments, pass.keptAsSettled, pass.placedAnew }, Is.EqualTo(new long[] { 1, 8, 8, 0 }), "all kept");

                // One body moves: placed anew because it stands elsewhere; it comes out with neither clip nor cap.
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 1f)));
                before = PlacementOnlyOf(display, out _);
                CollectBoth(twin, "body 2 moved");
                pass.Clear();
                pass.AddDifference(PlacementOnlyOf(display, out _), before);
                Assert.That(new[] { pass.keptAsSettled, pass.placedAnew, pass.anewMoved, pass.anewSelected, pass.anewStartsShifted, pass.anewNotTakenOver, pass.anewCarried },
                    Is.EqualTo(new long[] { 7, 1, 1, 0, 0, 0, 0 }), "one placed anew: it moved");
                Assert.That(new[] { pass.anewClipped, pass.anewCapped, pass.anewClippedAndCapped, pass.anewShiftedPlain }, Is.EqualTo(new long[] { 0, 0, 0, 0 }));

                // Body 4 is cut and the cut left published: it is drawn as two clipped render fragments.
                VpSnapshotStageTotals stagesBeforeCut = display.StageTotalsForTest;
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                Both(twin, run => CutAndPlace(run, 4, plane, Stand(4)));
                CollectBoth(twin, "a cut published");
                VpSnapshotStageTotals cut = display.StageTotalsForTest;
                cut.Subtract(stagesBeforeCut);
                TestContext.Out.WriteLine("the publication: walks " + cut.walks + ", families rebuilt " + cut.familiesRebuilt + " (registrations " + cut.registrationsRebuilt
                    + ", render fragments " + cut.renderFragmentsRebuilt + ", branches " + cut.branchesRebuilt + ", candidates " + cut.candidatesRebuilt + "), registrations taken over "
                    + cut.registrationsReused + "; validations " + cut.validateCalls + ", collections " + cut.collectCalls + ", groupings " + cut.groupCalls);
                Assert.That(new[] { cut.familiesRebuilt, cut.registrationsRebuilt, cut.renderFragmentsRebuilt, cut.registrationsReused }, Is.EqualTo(new long[] { 1, 1, 2, 7 }),
                    "one family rebuilt -- the cut body's, now two render fragments -- and the other seven taken over");
                CollectBoth(twin, "the frame after");
                CollectBoth(twin, "two frames after");

                // A placement-only pass now: the four bodies before the cut one are kept. The cut body's first render
                // fragment is placed anew because it is clipped (it has selected boundaries). Everything after it fails
                // the keep test one condition earlier -- the pass has made conditions and caps by then, so an empty
                // range would begin elsewhere -- and is counted there: the cut body's second render fragment, which is
                // clipped too, and the three bodies after it, which did not move and come out with neither clip nor cap.
                before = PlacementOnlyOf(display, out _);
                CollectBoth(twin, "at rest, a cut published");
                pass.Clear();
                pass.AddDifference(PlacementOnlyOf(display, out _), before);
                TestContext.Out.WriteLine("a placement-only pass with a published cut: " + pass.Describe() + "; placed anew " + pass.placedAnew + " (selected " + pass.anewSelected
                    + ", starts shifted " + pass.anewStartsShifted + " of which plain " + pass.anewShiftedPlain + ", moved " + pass.anewMoved + "); clipped " + pass.anewClipped
                    + ", capped " + pass.anewCapped + ", both " + pass.anewClippedAndCapped);
                Assert.That(new[] { pass.renderFragments, pass.keptAsSettled, pass.placedAnew }, Is.EqualTo(new long[] { 9, 4, 5 }), "nine render fragments: four kept, five placed anew");
                Assert.That(new[] { pass.anewSelected, pass.anewStartsShifted, pass.anewShiftedPlain, pass.anewMoved }, Is.EqualTo(new long[] { 1, 4, 3, 0 }),
                    "one for its selected boundaries; four because the ranges before them are no longer empty, three of them plain");
                Assert.That(pass.anewClipped, Is.EqualTo(2), "the two sides came out clipped");
                Assert.That(pass.anewClippedAndCapped, Is.LessThanOrEqualTo(pass.anewCapped), "one with both is counted in each");

                // The collections of this Editor frame, timed: the stages follow one another inside the whole, and the
                // snapshot's build is inside stage 2.
                Assert.That(display.TryGetFrameCounts(Time.frameCount, out VpLogicalCutDisplay.FrameCounts counts), Is.True);
                VpCollectTimes t = counts.times;
                Assert.That(t.known, Is.True);
                Assert.That(t.collections, Is.EqualTo(counts.collections), "every collection of the frame is in it");
                double byStage = t.s0 + t.s1 + t.s2 + t.s3 + t.s4 + t.s5 + t.s6 + t.s7 + t.s8 + t.s9;
                Assert.That(byStage, Is.GreaterThan(0.0).And.LessThanOrEqualTo(t.collectSeconds + 1e-9), "the stages add up to no more than the whole");
                Assert.That(t.buildCalls, Is.GreaterThanOrEqualTo(t.collections));
                Assert.That(t.buildSeconds, Is.GreaterThan(0.0).And.LessThanOrEqualTo(t.s2 + 1e-9), "the snapshot's build is inside stage 2");
                Assert.That(counts.stages.placeCalls, Is.GreaterThanOrEqualTo(t.collections), "a Place pass a build");
            }
        }
    }
}
