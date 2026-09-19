using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The cap-job preparation of DESIGN 5.6 / D-183 connected to the display: every camera's stencil work comes from
    /// <see cref="VpCapJobClassification"/> over the adopted snapshot and the draw-range table adopted with it. A job's
    /// volume is clipped by its own face alone while the body keeps every selected face; identical volumes are issued
    /// once; a colour limit that cannot be met refuses one camera's preparation and nothing else; and the table follows
    /// the snapshot through a registration let go and through an adoption refused. Same fixture as the rest of this
    /// class: the cube [-1, 1]^3, test capacities, a coarse pixel answer.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // ----- one face per volume ---------------------------------------------------------------------------------------

        /// <summary>
        /// The corner piece of three cuts (T3): A at y = 0 with the anchor below, B at x = 0 and C at z = 0 above it, both
        /// sides free, on a body of two submeshes. Seen along (1, 1, 1) from below, A+B+C+ shows its three caps: the
        /// body is clipped by all three faces, while each job's volume -- and every volume command arranged -- is clipped
        /// by one face only, the job's own, one command per submesh. The instance capacity is exactly what the four
        /// render fragments take (eight); nine caps of two submeshes would need eighteen volume commands, more than the
        /// instances, and the collection is still adopted: the volume room is eight per instance.
        /// </summary>
        [Test]
        public void ThreeFacesOfOneRenderFragment_EachVolumeIsClippedByItsOwnFace_TheBodyByAll()
        {
            using (Scene scene = NewScene(instances: 8))
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(root, AppendCube(scene.storage, true), Matrix4x4.identity), Is.True);
                var (_, aPlus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
                var (_, bPlus, _) = Cut(ledger, aPlus, new float4(1f, 0f, 0f, 0f));
                Cut(ledger, bPlus, new float4(0f, 0f, 1f, 0f));
                Collect(scene);
                Assert.That(display.RenderFragmentCount, Is.EqualTo(4), "the layout: A-, A+B-, A+B+C-, A+B+C+");
                Assert.That(display.CapRecordCount, Is.EqualTo(9), "one, two, three and three caps");
                Assert.That(display.SideCount, Is.EqualTo(8), "the instance capacity, exactly");

                int corner = -1;
                for (int r = 0; r < display.RenderFragmentCount; r++)
                {
                    display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                    if (rf.conditionCount == 3 && rf.offset.x > 0f && rf.offset.z > 0f)
                    {
                        corner = r;
                    }
                }

                Assert.That(corner, Is.Not.EqualTo(-1), "the layout: A+B+C+");
                display.TryGetRenderFragment(corner, out VpMultiCutRenderFragment cornerFragment);
                Assert.That(cornerFragment.clip.PlaneCount, Is.EqualTo(3), "the body is clipped by all three faces");
                for (int i = 0; i < display.SideCount; i++)
                {
                    display.TryGetSide(i, out LogicalCutDisplaySide side);
                    if (side.renderFragment == corner)
                    {
                        Assert.That(side.clip.PlaneCount, Is.EqualTo(3), "every instance of the body keeps all three");
                    }
                }

                Vector3 centre = new Vector3(3.5f, 3.5f, 3.5f);
                Vector3 along = new Vector3(1f, 1f, 1f).normalized;
                Camera camera = Looking(centre - (along * 7f), along, 2.5f);
                CapturedJobs captured = CaptureJobs(display);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                display.CapJobsClassifiedForTest = null;

                int cornerJobs = 0;
                foreach (VpCapJob job in captured.jobs)
                {
                    VpCapVolumeGroup group = captured.groups[job.volumeGroup];
                    Assert.That(job.volumeClip.PlaneCount, Is.EqualTo(1), "a job's volume clip is one face");
                    Assert.That(group.volumeClip.Equals(job.volumeClip), Is.True, "and its group's is the same");
                    Assert.That(Same(job.volumeClip.SignedPlane(0), job.signedPlane), Is.True, "the job's own face and side");
                    if (job.renderFragment == corner)
                    {
                        cornerJobs++;
                    }
                }

                Assert.That(cornerJobs, Is.EqualTo(3), "the layout: A+B+C+'s three caps are seen");
                display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _);
                Assert.That(preparation.volumeCommands, Is.EqualTo(2 * preparation.volumeGroups), "one command per submesh per group");
                Assert.That(display.ArrangedVolumeCount, Is.EqualTo(preparation.volumeCommands));
                var cornerFaces = new HashSet<Vector4>();
                for (int v = 0; v < display.ArrangedVolumeCount; v++)
                {
                    Assert.That(display.TryGetArrangedVolume(v, out _, out _, out VpInstanceClip clip), Is.True);
                    Assert.That(clip.PlaneCount, Is.EqualTo(1), "volume command " + v + ": one face, never the body's three");
                    foreach (VpCapJob job in captured.jobs)
                    {
                        if (job.renderFragment == corner && clip.Equals(job.volumeClip))
                        {
                            cornerFaces.Add(job.signedPlane);
                        }
                    }
                }

                Assert.That(cornerFaces.Count, Is.EqualTo(3), "A+B+C+'s three faces each have their own volume");
                display.Render(0, camera);
                Read(camera);
            }
        }

        // ----- sharing and separation (X6, X5) ---------------------------------------------------------------------------

        /// <summary>
        /// X6: A at y = 0 with anchors below on both sides of x = 0, so A- is fixed and A+ moved away; B at x = 0 on A-,
        /// an anchor on each side, so both parts stay. A-B- and A-B+ close the same face on the same side, with the same
        /// draw ranges, placement and (zero) separation: their two top caps are two jobs of one volume group, whose
        /// volume is arranged once. Both halves of the top are capped. B pending and then published, and a change of
        /// separation (which moves only A+), keep the same sharing and the same volume clip.
        /// </summary>
        [Test]
        public void X6_IdenticalVolumes_AreIssuedOnce_ThroughPublicationAndASeparationChange()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.5f, 0f), new float3(0.5f, -0.5f, 0f) });
                Assert.That(display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                var (a, _, aMinus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
                CutOperationId b = Admit(ledger, aMinus, new float4(1f, 0f, 0f, 0f));
                Collect(scene);

                Camera camera = Looking(new Vector3(0f, 0.5f, 0f), Vector3.down, 1.5f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapVolumeGroup pending = SharedTopGroup(display, camera, a, "B pending");
                display.Render(0, camera);
                Color32[] image = Read(camera);
                Assert.That(IsRed(At(image, camera, new Vector3(-0.5f, 0f, 0.5f))), Is.True, "A-B- is capped");
                Assert.That(IsRed(At(image, camera, new Vector3(0.5f, 0f, -0.5f))), Is.True, "and A-B+");

                Assert.That(ledger.Publish(b, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Collect(scene);
                VpCapVolumeGroup published = SharedTopGroup(display, camera, a, "B published");
                Assert.That(published.volumeClip.Equals(pending.volumeClip), Is.True, "publication changes no volume");

                display.Separation = WideSeparation * 2f;
                Collect(scene);
                VpCapVolumeGroup moved = SharedTopGroup(display, camera, a, "separation changed");
                Assert.That(moved.volumeClip.Equals(pending.volumeClip), Is.True, "the fixed parts do not move");
                display.Render(0, camera);
                Read(camera);
            }
        }

        /// <summary>
        /// The one group of A-B- and A-B+'s top caps (face <paramref name="a"/>, the kept side below): two jobs, one
        /// volume -- one volume command for this one-command body, arranged once.
        /// </summary>
        private VpCapVolumeGroup SharedTopGroup(VpLogicalCutDisplay display, Camera camera, CutOperationId a, string what)
        {
            CapturedJobs captured = CaptureJobs(display);
            Assert.That(display.TryPrepareCamera(camera), Is.True, what + ": prepared");
            display.CapJobsClassifiedForTest = null;
            var tops = new List<VpCapJob>();
            foreach (VpCapJob job in captured.jobs)
            {
                if (job.boundary.face.operation == a && job.boundary.side < 0f)
                {
                    tops.Add(job);
                }
            }

            Assert.That(tops.Count, Is.EqualTo(2), what + ": the layout: both top caps are seen");
            Assert.That(tops[0].renderFragment, Is.Not.EqualTo(tops[1].renderFragment), what + ": of two render fragments");
            Assert.That(tops[0].volumeGroup, Is.EqualTo(tops[1].volumeGroup), what + ": one volume group");
            VpCapVolumeGroup group = captured.groups[tops[0].volumeGroup];
            Assert.That(group.jobCount, Is.EqualTo(2), what);
            int arranged = 0;
            for (int v = 0; v < display.ArrangedVolumeCount; v++)
            {
                display.TryGetArrangedVolume(v, out _, out _, out VpInstanceClip clip);
                arranged += clip.Equals(group.volumeClip) ? 1 : 0;
            }

            Assert.That(arranged, Is.EqualTo(1), what + ": its volume is arranged once, not once per job");
            display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _);
            Assert.That(preparation.volumeGroups, Is.LessThan(preparation.jobs), what + ": fewer groups than jobs");
            return group;
        }

        /// <summary>
        /// X5: the same cuts, but B's minus side has no anchor and moves along -x. A-B-'s and A-B+'s top caps close the
        /// same face on the same side, at different separations: two volume groups. Their drawing polygons are apart
        /// on the screen, but the initial sections -- the whole square of each, moved -- overlap, so the groups take two
        /// colours. Under a limit of one there is no colour for the second: the preparation is refused.
        /// </summary>
        [Test]
        public void X5_DifferentSeparations_AreSeparateGroups_ColouredByTheirInitialSections()
        {
            foreach (int colours in new[] { 4, 1 })
            {
                using (Scene scene = X5Scene(colours))
                {
                    VpLogicalCutDisplay display = scene.display;
                    Camera camera = Looking(new Vector3(0f, 0.25f, 0f), Vector3.down, 2f);
                    Assert.That(display.TryRegisterCamera(camera), Is.True);
                    CapturedJobs captured = CaptureJobs(display);
                    bool prepared = display.TryPrepareCamera(camera);
                    display.CapJobsClassifiedForTest = null;
                    display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _);
                    if (colours == 1)
                    {
                        Assert.That(prepared, Is.False, "limit 1: refused");
                        Assert.That(preparation.outcome, Is.EqualTo(VpStencilPreparationOutcome.ColorLimitExceeded));
                        Assert.Throws<InvalidOperationException>(() => display.Render(0, camera));
                        continue;
                    }

                    Assert.That(prepared, Is.True);
                    var tops = new List<VpCapJob>();
                    foreach (VpCapJob job in captured.jobs)
                    {
                        if (job.boundary.side < 0f && Mathf.Abs(job.signedPlane.y) > 0.99f)
                        {
                            tops.Add(job);
                        }
                    }

                    Assert.That(tops.Count, Is.EqualTo(2), "the layout: both top caps are seen");
                    Assert.That(tops[0].volumeGroup, Is.Not.EqualTo(tops[1].volumeGroup), "different separations: two groups");
                    Assert.That(tops[0].colour, Is.Not.EqualTo(tops[1].colour), "overlapping initial sections: two colours");
                    display.Render(0, camera);
                    Color32[] image = Read(camera);
                    Assert.That(IsRed(At(image, camera, new Vector3(0.5f, 0f, 0.5f))), Is.True, "A-B+ is capped");
                    Assert.That(IsRed(At(image, camera, new Vector3(-1f, 0f, 0.5f))), Is.True, "and A-B-, moved");
                    Assert.That(IsRed(At(image, camera, new Vector3(-0.25f, 0f, 0.5f))), Is.False, "the gap between them is not");
                }
            }
        }

        /// <summary>
        /// X5's layout at separation 0.5: A-B+ fixed at x in [0, 1], A-B- moved to x in [-1.5, -0.5]; A+ moved up to
        /// y 0.5 and above.
        /// </summary>
        private Scene X5Scene(int colours)
        {
            Scene scene = NewScene(colours: colours);
            LogicalCutLedger ledger = scene.ledger;
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0.5f, -0.5f, 0f) });
            Assert.That(scene.display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
            scene.display.Separation = 0.5f;
            var (_, _, aMinus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            Cut(ledger, aMinus, new float4(1f, 0f, 0f, 0f));
            Collect(scene);
            return scene;
        }

        // ----- the colour limit, per camera ----------------------------------------------------------------------------

        /// <summary>
        /// Under a limit of one on X5: prepared looking at A-B+ alone; refused as ColorLimitExceeded looking at both --
        /// nothing uploaded or written, the camera not prepared and its draw refused, the display not stopped; prepared
        /// again once the view moves back. The shared classification holds nothing after any of them.
        /// </summary>
        [Test]
        public void TheColourLimit_RefusesOneView_AndAnotherViewIsPreparedAgain()
        {
            using (Scene scene = X5Scene(1))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = Looking(new Vector3(0.5f, 0.25f, 0f), Vector3.down, 0.3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapEye narrow = EyeOf(camera);
                camera.transform.position = new Vector3(0f, 0.25f, 0f);
                camera.orthographicSize = 2f;
                VpCapEye wide = EyeOf(camera);

                Assert.That(display.TryPrepareCamera(camera, narrow, narrow), Is.True, "one group seen: one colour");
                AssertTheClassificationHoldsNothing(display, "after a success");
                display.TryGetCameraStencil(camera, out VpStencilPreparation first, out VpStencilCameraCounts before);
                Assert.That(first.outcome, Is.EqualTo(VpStencilPreparationOutcome.Prepared));
                Assert.That(before.preparedNow, Is.True);

                Assert.That(display.TryPrepareCamera(camera, wide, wide), Is.False, "two overlapping groups under a limit of one");
                AssertTheClassificationHoldsNothing(display, "after a refusal");
                display.TryGetCameraStencil(camera, out VpStencilPreparation refused, out VpStencilCameraCounts after);
                Assert.That(refused.outcome, Is.EqualTo(VpStencilPreparationOutcome.ColorLimitExceeded), "told apart from room");
                Assert.That(refused.capRecords, Is.EqualTo(display.CapRecordCount));
                Assert.That(refused.colours + refused.volumeCommands + refused.capsDrawn, Is.Zero, "nothing arranged is reported");
                Assert.That(after.uploads, Is.EqualTo(before.uploads), "nothing uploaded");
                Assert.That(after.bufferWrites, Is.EqualTo(before.bufferWrites), "nothing written");
                Assert.That(after.preparedNow, Is.False, "the earlier preparation is voided");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera), "its draw is refused");
                Assert.That(display.HasDrawnThisFrame, Is.False, "before anything is registered");
                Assert.That(display.IsHalted || display.IsBroken, Is.False, "not a stop");

                Assert.That(display.TryPrepareCamera(camera, narrow, narrow), Is.True, "the view moved back: prepared again");
                display.TryGetCameraStencil(camera, out VpStencilPreparation again, out VpStencilCameraCounts last);
                Assert.That(again.outcome, Is.EqualTo(VpStencilPreparationOutcome.Prepared));
                Assert.That(last.uploads, Is.EqualTo(before.uploads + 1), "one upload for the new success");
                display.Render(0, camera);
                Read(camera);
                AssertTheClassificationHoldsNothing(display, "after drawing");
            }
        }

        /// <summary>
        /// Two cameras both prepared and both registered before either is rendered, under a limit of one on X5: the one
        /// looking at A-B+ alone is prepared and draws its cap; the one seeing both is refused and draws nothing, and
        /// the first is untouched by it. The refused camera, not yet drawn, is prepared from another view and draws.
        /// </summary>
        [Test]
        public void TwoCameras_OneRefusedForColours_TheOtherDrawsItsOwn()
        {
            using (Scene scene = X5Scene(1))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera narrow = Looking(new Vector3(0.5f, 0.25f, 0f), Vector3.down, 0.3f);
                Camera wide = Looking(new Vector3(0f, 0.25f, 0f), Vector3.down, 2f);
                Assert.That(display.TryRegisterCamera(narrow), Is.True);
                Assert.That(display.TryRegisterCamera(wide), Is.True);
                Assert.That(display.TryPrepareCamera(narrow), Is.True);
                Assert.That(display.TryPrepareCamera(wide), Is.False, "refused for colours");
                display.TryGetCameraStencil(narrow, out VpStencilPreparation narrowPreparation, out VpStencilCameraCounts narrowCounts);
                Assert.That(narrowPreparation.outcome, Is.EqualTo(VpStencilPreparationOutcome.Prepared), "the other camera is untouched");
                Assert.That(narrowCounts.preparedNow, Is.True);

                display.Render(0, narrow);
                Assert.Throws<InvalidOperationException>(() => display.Render(0, wide), "the refused camera draws nothing");
                Color32[] narrowImage = Read(narrow);
                Assert.That(IsRed(At(narrowImage, narrow, new Vector3(0.5f, 0f, 0f))), Is.True, "the prepared camera caps A-B+");

                VpCapEye aside = EyeOf(narrow);
                Assert.That(display.TryPrepareCamera(wide, aside, aside), Is.True, "not drawn yet: prepared from another view");
                display.Render(0, wide);
                Read(wide);
                Assert.That(display.TryPrepareCamera(narrow), Is.False, "a drawn camera is not prepared again");
                Assert.That(display.TryUnregisterCamera(narrow), Is.False, "nor let go this frame");
            }
        }

        // ----- the draw-range table follows the snapshot -------------------------------------------------------------------

        /// <summary>
        /// Two registrations of different geometry; the first is let go (its fragment retired by an aborted cut). The
        /// collection that lets it go still builds a snapshot of two registrations, the first drawn as nothing, and its
        /// table has two entries in that order; the next collection has one. Every time, the table has one entry per
        /// registration of the adopted snapshot, and every volume arranged draws the second body's own ranges.
        /// </summary>
        [Test]
        public void LettingTheFirstRegistrationGo_KeepsTheLaterRegistrationsRanges()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                var below = new List<float3> { new float3(0f, -0.5f, 0f) };
                LogicalFragmentId first = ledger.AddFragment(below);
                LogicalFragmentId second = ledger.AddFragment(below);
                Assert.That(display.TryShow(first, AppendCube(scene.storage, true), Matrix4x4.Translate(new Vector3(-3f, 0f, 0f))), Is.True);
                Assert.That(display.TryShow(second, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                display.GeometryTableRoomForTest(out object roomA, out object roomB, out int capacity, out _);
                Assert.That(capacity, Is.EqualTo(16), "the table's room is the instance capacity");
                Admit(ledger, second, new float4(0f, 1f, 0f, 0f));
                Collect(scene);
                AssertTableRoom(display, roomA, roomB, "both registered");
                Assert.That(display.AdoptedGeometries.Count, Is.EqualTo(2));
                Camera camera = Looking(new Vector3(0f, 0.5f, 0f), Vector3.down, 1.5f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                AssertVolumesDraw(display, display.AdoptedGeometries[1], "both registered");

                CutOperationId retiring = Admit(ledger, first, new float4(1f, 0f, 0f, 0f));
                Assert.That(ledger.Abort(retiring), Is.EqualTo(LogicalCutResultOutcome.Applied), "the first is retired");
                Collect(scene);
                Assert.That(display.ShownCount, Is.EqualTo(1), "the first is let go");
                AssertTableRoom(display, roomA, roomB, "the first let go");
                Assert.That(display.AdoptedSnapshot.RegistrationCount, Is.EqualTo(2), "the snapshot it was let go in has both");
                Assert.That(display.AdoptedGeometries.Count, Is.EqualTo(2), "and so does its table, in its order");
                Assert.That(display.AdoptedGeometries[0].ranges.Count, Is.EqualTo(2), "the first body's two submeshes");
                Assert.That(display.AdoptedGeometries[1].ranges.Count, Is.EqualTo(1), "the second body's one");
                AssertSecondRangesAreDrawn(display);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                AssertVolumesDraw(display, display.AdoptedGeometries[1], "the first let go");

                Collect(scene);
                AssertTableRoom(display, roomA, roomB, "the next collection");
                Assert.That(display.AdoptedSnapshot.RegistrationCount, Is.EqualTo(1));
                Assert.That(display.AdoptedGeometries.Count, Is.EqualTo(1), "one registration left, at index 0");
                Assert.That(display.AdoptedGeometries[0].ranges.Count, Is.EqualTo(1));
                AssertSecondRangesAreDrawn(display);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                AssertVolumesDraw(display, display.AdoptedGeometries[0], "the next collection");
                display.Render(0, camera);
                Read(camera);
            }
        }

        /// <summary>Every draw command is the one registration left: the table's last entry holds exactly its ranges.</summary>
        private static void AssertSecondRangesAreDrawn(VpLogicalCutDisplay display)
        {
            IReadOnlyList<VpCapJobGeometry> table = display.AdoptedGeometries;
            VpArrayRange<VpGeometryRange> ranges = table[table.Count - 1].ranges;
            Assert.That(display.DrawCommandCount, Is.EqualTo(ranges.Count));
            for (int c = 0; c < ranges.Count; c++)
            {
                display.TryGetDrawCommand(c, out VpIndirectCommand command);
                Assert.That(SameRange(command.range, ranges[c]), Is.True, "command " + c);
            }
        }

        private static void AssertVolumesDraw(VpLogicalCutDisplay display, VpCapJobGeometry geometry, string what)
        {
            Assert.That(display.ArrangedVolumeCount, Is.GreaterThan(0), what + ": the layout: a volume is arranged");
            for (int v = 0; v < display.ArrangedVolumeCount; v++)
            {
                display.TryGetArrangedVolume(v, out VpIndirectCommand command, out _, out _);
                Assert.That(SameRange(command.range, geometry.ranges[v % geometry.ranges.Count]), Is.True, what + ": volume " + v);
            }
        }

        private static bool SameRange(VpGeometryRange a, VpGeometryRange b)
        {
            return a.vertexStart == b.vertexStart && a.vertexCount == b.vertexCount && a.indexStart == b.indexStart
                && a.indexCount == b.indexCount;
        }

        /// <summary>
        /// A registration is added and the next collection is refused for room: the snapshot adopted before -- one
        /// registration -- keeps drawing with the table adopted with it, one entry, not the candidate's two; the camera
        /// is prepared from them and draws.
        /// </summary>
        [Test]
        public void AnAdoptionRefusedAfterAddingARegistration_PreparesTheOldSnapshotWithItsOwnTable()
        {
            using (Scene scene = NewScene(instances: 4))
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                var below = new List<float3> { new float3(0f, -0.5f, 0f) };
                LogicalFragmentId first = ledger.AddFragment(below);
                Assert.That(display.TryShow(first, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                display.GeometryTableRoomForTest(out object roomA, out object roomB, out int capacity, out _);
                Assert.That(capacity, Is.EqualTo(4), "the table's room is the instance capacity");
                var (_, plus, _) = Cut(ledger, first, new float4(0f, 1f, 0f, 0f));
                Collect(scene);
                AssertTableRoom(display, roomA, roomB, "one registration");
                Assert.That(display.SideCount, Is.EqualTo(2), "the layout: two instances");

                // A settled frame takes no new body: the next one does.
                _frame++;
                LogicalFragmentId second = ledger.AddFragment(below);
                Assert.That(display.TryShow(second, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(0f, 0f, 3f))), Is.True);
                Admit(ledger, plus, new float4(1f, 0f, 0f, 0f));
                Admit(ledger, second, new float4(0f, 1f, 0f, 0f));
                Assert.That(display.TryBeginFrame(), Is.False, "three render fragments and two more: five instances, room for four");
                Assert.That(display.IsHalted, Is.False, "an ordinary refusal");
                Assert.That(display.AdoptedSnapshot.RegistrationCount, Is.EqualTo(1), "the old snapshot");
                Assert.That(display.AdoptedGeometries.Count, Is.EqualTo(1), "with its own table");
                AssertTableRoom(display, roomA, roomB, "an adoption refused after a registration was added");

                Camera camera = Looking(new Vector3(0f, 0.5f, 0f), Vector3.down, 1.5f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True, "prepared from the old snapshot and the old table");
                AssertVolumesDraw(display, display.AdoptedGeometries[0], "the old snapshot");
                display.Render(0, camera);
                Color32[] image = Read(camera);
                Assert.That(IsRed(At(image, camera, new Vector3(0.5f, 0f, 0.5f))), Is.True, "the first body's cap");

                // A frame with room again: the second cut on the second body is dropped by aborting it, and both
                // registrations are adopted into the same room.
                _frame++;
                LogicalCutOperation pendingOnSecond = default;
                for (int position = 0; ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
                {
                    pendingOnSecond = operation.source == second ? operation : pendingOnSecond;
                }

                Assert.That(ledger.Abort(pendingOnSecond.id), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Assert.That(display.TryBeginFrame(), Is.True, "adopted: the second body is let go, the first is split in three");
                AssertTableRoom(display, roomA, roomB, "adopted after the refusal");
                Assert.That(display.AdoptedGeometries.Count, Is.EqualTo(display.AdoptedSnapshot.RegistrationCount));
            }
        }

        /// <summary>
        /// The two draw-range tables are the ones the display was made with -- in either place, never replaced -- at
        /// their fixed room; the adopted one has one entry per registration of the adopted snapshot, and no slot past
        /// either table's count still refers to a range.
        /// </summary>
        private static void AssertTableRoom(VpLogicalCutDisplay display, object roomA, object roomB, string what)
        {
            display.GeometryTableRoomForTest(out object adopted, out object candidate, out int capacity, out int heldPastCount);
            bool same = (ReferenceEquals(adopted, roomA) && ReferenceEquals(candidate, roomB))
                || (ReferenceEquals(adopted, roomB) && ReferenceEquals(candidate, roomA));
            Assert.That(same, Is.True, what + ": the same two tables, never replaced");
            Assert.That(display.AdoptedGeometries.Count, Is.LessThanOrEqualTo(capacity), what + ": within the fixed room");
            Assert.That(display.AdoptedGeometries.Count, Is.EqualTo(display.AdoptedSnapshot.RegistrationCount), what + ": one entry per registration");
            Assert.That(heldPastCount, Is.Zero, what + ": nothing held past either count");
        }

        // ----- lifetime ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The shared classification is nobody's result: after a camera is let go, and after the display is disposed,
        /// it holds no look, no result and no ledger reference.
        /// </summary>
        [Test]
        public void LettingACameraGoOrDisposing_LeavesNothingInTheSharedClassification()
        {
            Scene scene = X5Scene(4);
            VpLogicalCutDisplay display = scene.display;
            try
            {
                Camera camera = Looking(new Vector3(0f, 0.25f, 0f), Vector3.down, 2f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                AssertTheClassificationHoldsNothing(display, "prepared");
                Assert.That(display.TryUnregisterCamera(camera), Is.True);
                AssertTheClassificationHoldsNothing(display, "let go");
            }
            finally
            {
                scene.Dispose();
            }

            AssertTheClassificationHoldsNothing(display, "disposed");
            Assert.That(display.AdoptedGeometries.Count, Is.Zero, "and the table is empty");
        }

        private static void AssertTheClassificationHoldsNothing(VpLogicalCutDisplay display, string what)
        {
            VpCapJobClassification classification = display.CapJobs;
            Assert.That(classification.IsClassified, Is.False, what + ": no readable result");
            Assert.That(classification.HeldViews, Is.Zero, what + ": no look");
            Assert.That(classification.LedgerReferencesHeld, Is.Zero, what + ": no ledger reference");
            Assert.That(classification.IsFor(display.AdoptedSnapshot), Is.False, what);
        }

        private static VpCapEye EyeOf(Camera camera)
        {
            return new VpCapEye(camera.transform.position, camera.projectionMatrix * camera.worldToCameraMatrix);
        }
    }
}
