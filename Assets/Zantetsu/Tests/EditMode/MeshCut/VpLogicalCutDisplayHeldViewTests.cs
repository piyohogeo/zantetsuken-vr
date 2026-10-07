using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Held placements asked only where near **and in view** (TL, 2026-10-07; DESIGN 5.6, D-210). A reference point may
    /// come with its camera, and a registered camera's view -- the eyes it was rendered with -- is noted by the host at
    /// the end of its rendering. A held render fragment a proximity box finds is then asked only if its kept box also
    /// meets that camera's view (either eye's) as noted in the frame before the placement pass. A point with no view
    /// known asks by nearness alone, as before; ordinary targets and the ones told of are asked in view or not;
    /// nothing of the tree is touched for a view. No frame is promised for a target that comes into view: it is asked
    /// when a placement pass next runs -- and none runs in a collection with no new step, nothing told and no
    /// structure change, whatever the view did.
    /// <para>
    /// The bodies are cubes of 2 m at x = 3 i, so a held body's box (margin 0.5 m) is x in [3 i - 1.5, 3 i + 1.5] and
    /// y, z in [-1.5, 1.5]. The reach is set to 7 m: a reference point at x = 6 has a proximity box x in [-1, 13],
    /// which meets the boxes of bodies 0 to 4 and not 5, 6, 7. The views are boxes along the axes (a world-to-clip
    /// matrix that takes the box to the clip cube), so a body is in a view exactly when its box meets the view's box:
    /// x in [2, 10] meets bodies 1, 2, 3 and neither 0 (to 1.5) nor 4 (from 10.5).
    /// </para>
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // The host of these cases: its step counts and, for each reference point, the camera it is the position of.
        private sealed class ViewHost
        {
            public long step = 1, outside;
            public readonly List<Vector3> points = new List<Vector3>();
            public readonly List<Camera> cameras = new List<Camera>();

            public void Attach(VpLogicalCutDisplay display)
            {
                display.PlacementSerial = (out long s, out long o) => { s = step; o = outside; return true; };
                display.PlacementProximityViews = (p, c) => { p.AddRange(points); c.AddRange(cameras); };
                display.PlacementHoldReach = 7f;
            }
        }

        // World to clip for a view that is the box [lo, hi]: the box goes to the clip cube, w stays 1.
        private static Matrix4x4 ViewOf(float x0, float x1, float y = 3f, float z = 3f)
        {
            return Matrix4x4.Scale(new Vector3(2f / (x1 - x0), 1f / y, 1f / z)) * Matrix4x4.Translate(new Vector3(-0.5f * (x0 + x1), 0f, 0f));
        }

        private Camera ViewCamera(VpLogicalCutDisplay display)
        {
            Camera camera = Looking(new Vector3(6f, 4f, -6f), new Vector3(0f, -0.5f, 1f), 6f);
            Assert.That(display.TryRegisterCamera(camera), Is.True, "the camera is registered with the display");
            return camera;
        }

        // The view counts of a pass, in one line.
        private static string ViewLine(in VpHeldPlacementTotals d) =>
            "passes " + (d.passesSelective + d.passesStructure) + ": near candidates " + d.nearCandidates + ", view tests " + d.viewTests + ", asked near and in view " + d.queriedNear
            + ", near but out of view " + d.omittedOutOfView + ", not asked in all " + d.omitted + "; ordinary " + d.queriedOrdinary + ", clipped " + d.queriedSelected + ", told " + d.queriedNotified
            + "; points with a view " + d.pointsWithView + ", without " + d.pointsWithoutView + "; tree boxes taken in " + d.treeInserts + ", given up " + d.treeRemovals
            + "; view ms " + (d.viewSeconds * 1000).ToString("F4") + ", search ms " + (d.searchSeconds * 1000).ToString("F4");

        [Test]
        public void HeldView_ANearHeldOneIsAskedOnlyInView_AFarOneNotAtAll_TheOrdinaryAndTheToldOfAreAskedOutOfView_AndAViewThatMovesTouchesNoBox()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                List<LogicalFragmentId> bodies = twin.kept.bodies;
                var host = new ViewHost();
                Camera camera = ViewCamera(display);
                host.points.Add(new Vector3(6f, 0f, 0f));
                host.cameras.Add(camera);
                host.Attach(display);
                var hostCounts = new HoldHost();   // only the counts of the changes told, for ToldChanges
                var told = new ToldChanges(hostCounts, display);

                // Settling: no view was noted yet, so the point asks by nearness alone -- as before the view was used.
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "settling, step " + i);
                }

                VpHeldPlacementTotals d = Since(display, t);
                AssertTreeHolds(display, 8, "standing over two step results");
                Assert.That(new[] { d.pointsWithView, d.omittedOutOfView, d.viewTests }, Is.EqualTo(new long[] { 0, 0, 0 }), "no view noted: nothing left out for a view");
                Assert.That(d.pointsWithoutView, Is.GreaterThan(0), "the passes had a point with no view");
                Assert.That(d.pickedWithoutView, Is.EqualTo(d.queriedNear), "and every held one asked as near was taken by nearness alone");

                // The view is noted (the host does it at the end of the camera's rendering); the next frame's collection
                // reads it. Of the five near (0..4), the three in view (1, 2, 3) are asked; 0 and 4 are not; 5, 6, 7 are far.
                Matrix4x4 view = ViewOf(2f, 10f);
                Assert.That(display.NoteCameraView(camera, 1, view, view), Is.True);
                host.step++;
                t = display.HeldPlacementTotals;
                long[] work = PlaceWork(display);
                CollectBoth(twin, "near and in view");
                d = Since(display, t);
                TestContext.Out.WriteLine("near and in view: " + ViewLine(d));
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 3, 3, 0 }), "one pass: three placement queries, all three standing");
                Assert.That(
                    new[] { d.passesSelective, d.searches, d.nearCandidates, d.viewTests, d.queriedNear, d.omittedOutOfView, d.omitted, d.pointsWithView, d.pointsWithoutView },
                    Is.EqualTo(new long[] { 1, 1, 5, 5, 3, 2, 5, 1, 0 }),
                    "five near, each tested against the view; three asked; two near but out of view; five not asked in all (those two and the three far)");
                Assert.That(new[] { d.treeInserts, d.treeRemovals, d.promoted, d.demoted }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "nothing of the tree touched");
                Assert.That(d.pickedWithoutView, Is.Zero, "none taken by nearness alone: the point has its view");
                AssertTreeHolds(display, 8, "all held still");

                // Body 4 (near, out of view) and body 6 (far) are moved at a new step, nothing said. Neither is asked:
                // they are drawn where they are held. The display that asks every one writes them.
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[4], Stand(4, 1f)).Put(run.bodies[6], Stand(6, 1f)));
                display.NoteCameraView(camera, 1, view, view);
                long written = display.InstanceRecordsWritten;
                t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True, "the reference collected");
                d = Since(display, t);
                Assert.That(new[] { d.queriedNear, d.omittedOutOfView, d.omitted, d.demoted }, Is.EqualTo(new long[] { 3, 2, 5, 0 }), "the three in view asked; the moved ones not");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "nothing written: both are drawn where they are held");
                foreach (int moved in new[] { 4, 6 })
                {
                    Assert.That(display.AdoptedSnapshot.TryGetRenderFragment(moved, out VpMultiCutRenderFragment held), Is.True);
                    Assert.That(reference.AdoptedSnapshot.TryGetRenderFragment(moved, out VpMultiCutRenderFragment asked), Is.True);
                    Assert.That(new[] { held.geometryLocalToWorld.m13, asked.geometryLocalToWorld.m13 }, Is.EqualTo(new[] { 0f, 1f }),
                        "body " + moved + ": held where it was; the display that asks every one has it where it is");
                }
                AssertTreeHolds(display, 8, "held where they were");

                // Told of body 4, with no step: it is asked though out of view, found moved, ordinary again. Body 6 is not.
                told.Of(bodies[4]);
                host.outside++;
                display.NoteCameraView(camera, 1, view, view);
                t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True);
                d = Since(display, t);
                TestContext.Out.WriteLine("one out of view told of: " + ViewLine(d));
                Assert.That(new[] { d.toldFamilies, d.queriedNotified, d.queriedNear, d.demoted, d.omittedOutOfView, d.invalidations }, Is.EqualTo(new long[] { 1, 1, 3, 1, 1, 0 }),
                    "the one told of asked out of view and found moved; the other near one out of view still not asked");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 1), "and written where it is");
                Assert.That(new[] { d.treeInserts, d.treeRemovals }, Is.EqualTo(new long[] { 0, 1 }), "its box given up");
                AssertTreeHolds(display, 7, "the one told of ordinary");

                // Ordinary now, it moves again at the next step: asked out of view, as every one not held is.
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[4], Stand(4, 2f)));
                display.NoteCameraView(camera, 1, view, view);
                t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True);
                d = Since(display, t);
                Assert.That(new[] { d.queriedOrdinary, d.queriedNear, d.omittedOutOfView }, Is.EqualTo(new long[] { 1, 3, 1 }), "the ordinary one asked out of view");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 2), "and written where it moved to");
                for (int i = 0; i < 2; i++)
                {
                    host.step++;
                    display.NoteCameraView(camera, 1, view, view);
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True, "collected");
                    Assert.That(reference.TryBeginFrame(), Is.True);
                }

                AssertTreeHolds(display, 8, "it stands and is held again, out of view");

                // The view moves and nothing else does: x in [8, 16] meets bodies 3, 4 and 5 -- of the near ones, 3 and 4.
                // Another choice of who is asked; no box taken into the tree or out of it.
                Matrix4x4 turned = ViewOf(8f, 16f);
                display.NoteCameraView(camera, 1, turned, turned);
                host.step++;
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True);
                d = Since(display, t);
                TestContext.Out.WriteLine("the view moved: " + ViewLine(d));
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 2, 2, 0 }), "two asked, both standing");
                Assert.That(new[] { d.nearCandidates, d.queriedNear, d.omittedOutOfView, d.omitted }, Is.EqualTo(new long[] { 5, 2, 3, 6 }), "of the five near, the two in the view now");
                Assert.That(new[] { d.treeInserts, d.treeRemovals, d.promoted, d.demoted, d.structureResets, d.remaps }, Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0 }),
                    "a view that moves takes no box in, gives none up and maps nothing");
                AssertTreeHolds(display, 8, "all held");
            }
        }

        [Test]
        public void HeldView_AViewIsReadByTheCollectionOfTheFrameAfterItWasNoted_AnOlderOneOrOneThatIsNotAViewAsksByNearnessAlone()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                var host = new ViewHost();
                Camera camera = ViewCamera(display);
                host.points.Add(new Vector3(6f, 0f, 0f));
                host.cameras.Add(camera);
                host.Attach(display);
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "settling, step " + i);
                }

                AssertTreeHolds(display, 8, "eight held");
                Matrix4x4 away = ViewOf(2f, 10f), onIt = ViewOf(8f, 16f);

                void Collect(string what)
                {
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True, what + ": collected");
                    Assert.That(reference.TryBeginFrame(), Is.True, what + ": the reference collected");
                }

                // Body 4 moves while near and out of view: not asked.
                display.NoteCameraView(camera, 1, away, away);
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[4], Stand(4, 1f)));
                long written = display.InstanceRecordsWritten;
                Collect("moved out of view");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "not asked, drawn where it is held");

                // The camera turns to it. This frame's collection runs before this frame is rendered: it reads the view
                // of the frame before, in which the body was out of view -- still not asked. The view the frame is then
                // rendered with is noted at the end of it...
                display.NoteCameraView(camera, 1, away, away);   // the frame before, as rendered
                host.step++;
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                Collect("the frame the view turns in");
                VpHeldPlacementTotals d = Since(display, t);
                Assert.That(new[] { d.demoted, d.omittedOutOfView }, Is.EqualTo(new long[] { 0, 2 }), "the collection of the frame the view turns in reads the view before the turn");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "drawn where it is held for that one frame");
                display.NoteCameraView(camera, 1, onIt, onIt);    // this frame, as rendered: the body is in view

                // ...and the next frame's collection reads it: asked, found moved, drawn where it is.
                host.step++;
                t = display.HeldPlacementTotals;
                Collect("the frame after");
                d = Since(display, t);
                Assert.That(new[] { d.queriedNear, d.demoted }, Is.EqualTo(new long[] { 2, 1 }), "bodies 3 and 4 asked; 4 found moved");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 1), "written where it is");
                for (int i = 0; i < 2; i++)
                {
                    display.NoteCameraView(camera, 1, onIt, onIt);
                    host.step++;
                    Collect("standing " + i);
                }

                AssertTreeHolds(display, 8, "held again");

                // No view noted in the last frame (the camera was not rendered in it): the view kept is older than the
                // frame before, and is no view -- the point asks by nearness alone, the five near ones, never every one.
                host.step++;
                t = display.HeldPlacementTotals;
                Collect("a frame after one with no view noted");
                d = Since(display, t);
                Assert.That(new[] { d.pointsWithView, d.pointsWithoutView, d.queriedNear, d.omittedOutOfView, d.omitted, d.viewTests }, Is.EqualTo(new long[] { 0, 1, 5, 0, 3, 0 }),
                    "an old view is not used: the five near ones asked, the three far ones not");

                // A view that is not finite, or with no eye, is kept as not known: nearness alone again.
                Matrix4x4 broken = onIt;
                broken.m00 = float.NaN;
                Assert.That(display.NoteCameraView(camera, 1, broken, broken), Is.True, "taken, as not known");
                host.step++;
                t = display.HeldPlacementTotals;
                Collect("after a view that is not finite");
                d = Since(display, t);
                Assert.That(new[] { d.pointsWithView, d.pointsWithoutView, d.queriedNear, d.omitted }, Is.EqualTo(new long[] { 0, 1, 5, 3 }), "not a view: nearness alone");
                display.NoteCameraView(camera, 0, onIt, onIt);
                host.step++;
                t = display.HeldPlacementTotals;
                Collect("after a view with no eye");
                d = Since(display, t);
                Assert.That(new[] { d.pointsWithView, d.queriedNear }, Is.EqualTo(new long[] { 0, 5 }), "no eye: nearness alone");

                // A camera that is not registered has no view kept at all.
                Camera stranger = Looking(new Vector3(0f, 4f, -6f), new Vector3(0f, -0.5f, 1f), 6f);
                Assert.That(display.NoteCameraView(stranger, 1, onIt, onIt), Is.False, "not registered: nothing kept");
                AssertTreeHolds(display, 8, "all held through it");
            }
        }

        [Test]
        public void HeldView_EitherEyeAsks_EachCameraNeedsItsOwnBoxAndItsOwnView_AndAPointWithNoCameraAsksByNearnessAlone()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new ViewHost();
                Camera first = ViewCamera(display);
                host.points.Add(new Vector3(6f, 0f, 0f));   // near: bodies 0..4
                host.cameras.Add(first);
                host.Attach(display);
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "settling, step " + i);
                }

                AssertTreeHolds(display, 8, "eight held");

                // Two eyes: the left sees x in [2, 4.4] -- body 1 only -- and the right x in [8, 10] -- body 3 only.
                // Each is seen by one eye and both are asked; body 2, between them, is seen by neither.
                Matrix4x4 left = ViewOf(2f, 4.4f), right = ViewOf(8f, 10f);
                display.NoteCameraView(first, 2, left, right);
                host.step++;
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                CollectBoth(twin, "two eyes");
                VpHeldPlacementTotals d = Since(display, t);
                TestContext.Out.WriteLine("two eyes: " + ViewLine(d));
                Assert.That(new[] { d.nearCandidates, d.queriedNear, d.omittedOutOfView, d.omitted, d.pointsWithView }, Is.EqualTo(new long[] { 5, 2, 3, 6, 1 }),
                    "the one the left eye sees and the one the right eye sees; the three seen by neither not asked");
                Assert.That(display.HeldPlacementsForTest.IsPickedForTest(1) && display.HeldPlacementsForTest.IsPickedForTest(3), Is.True, "bodies 1 and 3");
                Assert.That(display.HeldPlacementsForTest.IsPickedForTest(2), Is.False, "not body 2");

                // A second camera at x = 22: its proximity box, x in [15, 29], meets bodies 5, 6, 7. Its view is x in
                // [8, 10] -- body 3, which is near the FIRST camera and not in the first camera's view (now the left
                // eye's alone). Body 3 is asked for neither: a camera asks what is in its own box and its own view.
                Camera second = ViewCamera(display);
                host.points.Add(new Vector3(22f, 0f, 0f));
                host.cameras.Add(second);
                display.NoteCameraView(first, 1, left, left);
                display.NoteCameraView(second, 1, right, right);
                host.step++;
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "two cameras, each with a part");
                d = Since(display, t);
                TestContext.Out.WriteLine("two cameras: " + ViewLine(d));
                Assert.That(new[] { d.searches, d.nearCandidates, d.viewTests, d.queriedNear, d.omittedOutOfView, d.omitted, d.pointsWithView }, Is.EqualTo(new long[] { 2, 8, 8, 1, 7, 7, 2 }),
                    "eight near one camera or the other; only body 1 is in the box and the view of one camera");
                Assert.That(display.HeldPlacementsForTest.IsPickedForTest(1), Is.True, "body 1, by the first camera");
                Assert.That(display.HeldPlacementsForTest.IsPickedForTest(3), Is.False, "body 3: in the first camera's box and the second camera's view -- neither camera's both");

                // The second camera's view turns to x in [17, 19]: body 6, which is in its box too. Asked beside body 1.
                Matrix4x4 onSix = ViewOf(17f, 19f);
                display.NoteCameraView(first, 1, left, left);
                display.NoteCameraView(second, 1, onSix, onSix);
                host.step++;
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "the second camera sees one of its own");
                d = Since(display, t);
                Assert.That(new[] { d.queriedNear, d.omittedOutOfView, d.treeInserts, d.treeRemovals }, Is.EqualTo(new long[] { 2, 6, 0, 0 }), "body 1 by the first camera, body 6 by the second");
                Assert.That(display.HeldPlacementsForTest.IsPickedForTest(6), Is.True);

                // The second point is no camera's any more (null): it asks by its box alone -- bodies 5, 6, 7 -- while the
                // first still asks by its box and view. Never every one: bodies 0, 2, 3, 4 are not asked.
                host.cameras[1] = null;
                display.NoteCameraView(first, 1, left, left);
                host.step++;
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "a point with no camera");
                d = Since(display, t);
                TestContext.Out.WriteLine("a point with no camera: " + ViewLine(d));
                Assert.That(new[] { d.pointsWithView, d.pointsWithoutView, d.queriedNear, d.omittedOutOfView, d.omitted, d.viewTests }, Is.EqualTo(new long[] { 1, 1, 4, 4, 4, 5 }),
                    "one point by its box and view, one by its box alone");
                Assert.That(d.pickedWithoutView, Is.EqualTo(3), "of the four asked, the three near the point with no camera were taken by nearness alone");
                AssertTreeHolds(display, 8, "all held, nothing touched");
            }
        }

        [Test]
        public void HeldView_ThroughACutItsCommitARetirementAndACollectionNotAdopted_TheUnchangedStayHeldUnasked_AndACollectionWithNoStepStillAsksNothing()
        {
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new ViewHost();
                Camera camera = ViewCamera(display);
                host.points.Add(new Vector3(6f, 0f, 0f));
                host.cameras.Add(camera);
                host.Attach(display);
                Matrix4x4 view = ViewOf(2f, 10f);   // in view: bodies 1, 2, 3; near and out of view: 0, 4; far: 5, 6, 7

                void Step(string what, bool newStep = true)
                {
                    display.NoteCameraView(camera, 1, view, view);
                    if (newStep) host.step++;
                    CollectBoth(twin, what);
                }

                for (int i = 0; i < 4; i++) Step("settling " + i, i > 0);
                AssertTreeHolds(display, 8, "eight held");
                VpHeldPlacementTotals whole = display.HeldPlacementTotals;

                // Body 2 (near, in view) is cut, with no step: its family changed -- its sides start ordinary, its box
                // leaves the tree -- and the seven other families are carried. Asked: bodies 1 and 3 (in view) and the
                // two sides. Not asked: 0 and 4 (out of view) and 5, 6, 7 (far), though the ones after the cut body are
                // built again for their ranges.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 2, plane, Stand(2)));
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                Step("a cut published", false);
                VpHeldPlacementTotals d = Since(display, t);
                TestContext.Out.WriteLine("a cut published: carried " + d.carried + ", fresh " + d.fresh + ", gone " + d.droppedHeld + "; " + ViewLine(d));
                Assert.That(new[] { d.remaps, d.carried, d.fresh, d.droppedHeld, d.treeRemovals, d.treeInserts }, Is.EqualTo(new long[] { 1, 7, 2, 1, 1, 0 }),
                    "seven families carried, the cut body's sides new, its one box given up");
                Assert.That(new[] { d.queriedNear, d.queriedOrdinary + d.queriedSelected, d.omittedOutOfView, d.omitted }, Is.EqualTo(new long[] { 2, 2, 2, 5 }),
                    "the two in view and the two sides asked; the two out of view and the three far not");
                Assert.That(new[] { d.demoted, d.structureResets, d.queriedNotified }, Is.EqualTo(new long[] { 0, 0, 0 }));
                AssertTreeHolds(display, 7, "seven held through the cut");
                for (int i = 0; i < 2; i++)
                {
                    t = display.HeldPlacementTotals;
                    Step("the cut published, step " + i);
                    d = Since(display, t);
                    Assert.That(new[] { d.fresh, d.droppedHeld, d.demoted, d.treeInserts, d.treeRemovals, d.omittedOutOfView, d.omitted }, Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 2, 5 }),
                        "step " + i + ": nothing new or gone; the same five not asked");
                }

                // The commit, with no step, then the sides stand and are held (they are in view and near: asked).
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 2, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                t = display.HeldPlacementTotals;
                Step("the commit", false);
                d = Since(display, t);
                Assert.That(new[] { d.carried, d.droppedHeld, d.demoted, d.treeRemovals }, Is.EqualTo(new long[] { 7, 0, 0, 0 }), "the seven carried through the commit");
                for (int i = 0; i < 3; i++) Step("after the commit, step " + i);
                AssertTreeHolds(display, display.HeldPlacementTotals.heldAtEnd, "after the commit");
                Assert.That(display.HeldPlacementTotals.ordinaryAtEnd, Is.Zero, "every one held");
                int heldAfterCommit = display.HeldPlacements;
                Assert.That(heldAfterCommit, Is.GreaterThanOrEqualTo(9));

                // A collection with no new step and nothing told lets the adopted snapshot stand (D-204): no pass, no
                // search, no view asked.
                t = display.HeldPlacementTotals;
                long reuses = display.PlacementReuses;
                Step("a frame with no step", false);
                d = Since(display, t);
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses + 1), "the adopted snapshot stood");
                Assert.That(new[] { d.passesSelective + d.passesStructure, d.searches, d.viewTests, d.nearCandidates }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "nothing asked of the view either");

                // Body 6 (far) is retired: its box leaves the tree; the others are carried, the far and the out-of-view
                // ones unasked.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[6]), Is.True));
                t = display.HeldPlacementTotals;
                for (int i = 0; i < 3; i++) Step("one retired, " + i, i > 0);
                d = Since(display, t);
                Assert.That(new[] { d.droppedHeld, d.demoted, d.structureResets }, Is.EqualTo(new long[] { 1, 0, 0 }), "the retired one's box given up, nothing else");
                AssertTreeHolds(display, heldAfterCommit - 1, "the others held through the retirement");
                Step("at rest", false);

                // A collection that is refused after its compaction plan: body 1 (near, in view) moved at a new step and
                // is asked in it. It is ordinary from then on, and the collection after the refusal asks it again -- the
                // move is not lost -- while the out-of-view and far ones stay unasked.
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[1], Stand(1, 1f)));
                display.NoteCameraView(camera, 1, view, view);
                Drawn adopted = Capture(display);
                display.FailAfterCompactionPlanForTest = true;
                t = display.HeldPlacementTotals;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                display.FailAfterCompactionPlanForTest = false;
                d = Since(display, t);
                TestContext.Out.WriteLine("the refused collection: " + ViewLine(d));
                Assert.That(new[] { d.demoted, d.omittedOutOfView, d.queriedNotified }, Is.EqualTo(new long[] { 1, 2, 0 }), "the layout: the moved one asked and found moved; the two out of view not asked");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused collection");
                AssertTreeHolds(display, heldAfterCommit - 2, "the moved one ordinary, the rest held");
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                t = display.HeldPlacementTotals;
                Step("the collection after the refusal", false);
                d = Since(display, t);
                Assert.That(d.queriedOrdinary, Is.GreaterThanOrEqualTo(1), "the moved body asked again: the move is not lost");
                Assert.That(d.omittedOutOfView, Is.EqualTo(2), "and the out-of-view ones still not asked");
                Step("the compaction", false);
                Step("at rest", false);
                for (int i = 0; i < 2; i++) Step("the moved body stands, step " + i);
                AssertTreeHolds(display, heldAfterCommit - 1, "all held again");

                d = Since(display, whole);
                Assert.That(new[] { d.structureResets, d.invalidations }, Is.EqualTo(new long[] { 0, 0 }), "through all of it: nothing forgotten whole, never every held one asked");
                Assert.That(d.droppedHeld, Is.EqualTo(2), "two boxes given up for a structure in all: the cut body's and the retired one's");
            }
        }

        /// <summary>
        /// After a turn, in order (TL, 2026-10-07): a held body moved while near and out of view, and the camera then
        /// looks at it. (1) The frame of the turn: its placement pass reads the view of the frame before, so the body
        /// is not asked. (2) Frames with no new step, nothing told and no structure change: the adopted snapshot stands
        /// -- no pass, no search, no view asked -- and what is drawn is what was held, though the view is on the body
        /// now and even moves again. (3) The next step's pass: the body, near and in the view noted the frame before,
        /// is asked and drawn where it is. Through (1) and (2) no box is taken into the tree or given up; the one box
        /// given up in (3) is the body's own, for having moved.
        /// </summary>
        [Test]
        public void HeldView_AfterATurn_TheTurnsFrameReadsTheViewBefore_FramesWithNoStepAskNothingAndKeepWhatIsDrawn_AndTheNextStepsPassAsksAndUpdates()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                var host = new ViewHost();
                Camera camera = ViewCamera(display);
                host.points.Add(new Vector3(6f, 0f, 0f));
                host.cameras.Add(camera);
                host.Attach(display);
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) host.step++;
                    CollectBoth(twin, "settling, step " + i);
                }

                AssertTreeHolds(display, 8, "eight held");
                Matrix4x4 away = ViewOf(2f, 10f), onIt = ViewOf(8f, 16f), elsewhere = ViewOf(-40f, -30f);

                void Collect(string what)
                {
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True, what + ": collected");
                    Assert.That(reference.TryBeginFrame(), Is.True, what + ": the reference collected");
                }

                float HeldLift() => display.AdoptedSnapshot.TryGetRenderFragment(4, out VpMultiCutRenderFragment f) ? f.geometryLocalToWorld.m13 : float.NaN;

                // Body 4 moves at a new step, near and out of view: not asked, drawn where it is held.
                display.NoteCameraView(camera, 1, away, away);
                host.step++;
                Both(twin, run => run.at.Put(run.bodies[4], Stand(4, 1f)));
                long written = display.InstanceRecordsWritten;
                Collect("moved out of view");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "not asked");
                Assert.That(HeldLift(), Is.EqualTo(0f), "drawn where it is held");
                VpHeldPlacementTotals whole = display.HeldPlacementTotals;

                // (1) The frame of the turn, with a new step: the pass reads the view of the frame before (away).
                display.NoteCameraView(camera, 1, away, away);   // the frame before, as it was rendered
                host.step++;
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                Collect("(1) the frame of the turn");
                VpHeldPlacementTotals d = Since(display, t);
                TestContext.Out.WriteLine("(1) the frame of the turn: " + ViewLine(d));
                Assert.That(new[] { d.passesSelective, d.queriedNear, d.omittedOutOfView, d.demoted }, Is.EqualTo(new long[] { 1, 3, 2, 0 }), "a pass ran and read the view before the turn: the body not asked");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "nothing written");
                Assert.That(HeldLift(), Is.EqualTo(0f), "drawn where it is held");
                display.NoteCameraView(camera, 1, onIt, onIt);   // this frame, as rendered: the body is in view now

                // (2) Three frames with no new step, nothing told, no structure change. The view is on the body, moves
                // off it and comes back -- and nothing is asked, searched or tested: the adopted snapshot stands.
                Drawn held = Capture(display);
                long reuses = display.PlacementReuses;
                long[] work = PlaceWork(display);
                t = display.HeldPlacementTotals;
                Matrix4x4[] views = { elsewhere, onIt, onIt };
                for (int i = 0; i < views.Length; i++)
                {
                    Collect("(2) a frame with no step, " + i);
                    display.NoteCameraView(camera, 1, views[i], views[i]);
                }

                d = Since(display, t);
                TestContext.Out.WriteLine("(2) three frames with no step: " + ViewLine(d) + "; collections that let the adopted snapshot stand " + (display.PlacementReuses - reuses));
                Assert.That(display.PlacementReuses, Is.EqualTo(reuses + views.Length), "each let the adopted snapshot stand");
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 0, 0, 0, 0 }), "no pass, no placement asked");
                Assert.That(
                    new[] { d.passesSelective + d.passesStructure + d.passesUnvouched + d.passesOther, d.searches, d.nearCandidates, d.viewTests, d.queriedNear, d.omitted, d.omittedOutOfView },
                    Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0, 0 }), "no search and no view asked, though the view is on the body");
                Assert.That(d.viewSeconds, Is.EqualTo(0.0), "no time of choosing by the view");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written), "nothing written");
                AssertSameShape(held, Capture(display), "what is drawn is what was held");
                Assert.That(HeldLift(), Is.EqualTo(0f), "the body still drawn where it is held");

                // Through the turn and the frames with no step: no box taken in, none given up.
                d = Since(display, whole);
                Assert.That(new[] { d.treeInserts, d.treeRemovals, d.promoted, d.demoted, d.structureResets, d.remaps }, Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0 }),
                    "the view's moves touched no box");
                AssertTreeHolds(display, 8, "all held, the moved one too");

                // (3) The next step. The view noted the frame before is on the body: asked, found moved, drawn where it is.
                host.step++;
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                Collect("(3) the next step");
                d = Since(display, t);
                TestContext.Out.WriteLine("(3) the next step: " + ViewLine(d));
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 2, 1, 1 }), "one pass: bodies 3 and 4 asked, 3 standing, 4 placed anew");
                Assert.That(new[] { d.passesSelective, d.nearCandidates, d.queriedNear, d.omittedOutOfView, d.demoted }, Is.EqualTo(new long[] { 1, 5, 2, 3, 1 }), "near and in view: asked; found moved");
                Assert.That(new[] { d.treeInserts, d.treeRemovals }, Is.EqualTo(new long[] { 0, 1 }), "one box given up: the moved body's own");
                Assert.That(display.InstanceRecordsWritten, Is.EqualTo(written + 1), "written");
                Assert.That(HeldLift(), Is.EqualTo(1f), "drawn where it is");
                Assert.That(reference.AdoptedSnapshot.TryGetRenderFragment(4, out VpMultiCutRenderFragment asked) && asked.geometryLocalToWorld.m13 == 1f, Is.True, "as the display that asks every one has it");
                AssertTreeHolds(display, 7, "the moved one ordinary until it stands over two steps again");
            }
        }
    }
}
