using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The last colour of DESIGN D-185 / D-186 connected to the display. Of a limit of N colours at most N - 1 are
    /// ordinary; what they cannot take goes, a whole group at a time, to the last colour, which after its initialisation
    /// issues one volume per render fragment of its jobs -- that render fragment's commands, every submesh, with its
    /// body's own clip of every selected face -- and then only those jobs' caps. Nothing is refused for the colour limit.
    /// Each case checks what was actually arranged against the classification the preparation made, colour by colour,
    /// and every volume command against what the test itself registered -- each submesh's draw range, read from the
    /// storage, and the placement given to TryShow -- never against the issued commands. How the last colour looks is not
    /// checked here, only what it issues. Same fixture as the rest of this class.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // ----- one render fragment, ordinary and last ------------------------------------------------------------------

        /// <summary>
        /// T3 on a body of two submeshes, under a limit of 2 -- one ordinary colour and the last -- seen along (1, 1, 1)
        /// from below: A+B+C+'s three caps overlap on the screen, so one of them takes the ordinary colour and the
        /// others go to the last. The ordinary colour's volumes are own-face (one plane); the last colour issues
        /// A+B+C+ once -- two commands, one per submesh -- with its body's three-face clip; every cap is drawn once, in
        /// its own job's colour, and none twice.
        /// </summary>
        [Test]
        public void T3_OrdinaryAndLastInOneRenderFragment_OwnFaceOrEveryFace_EachCapOnce()
        {
            using (Scene scene = T3Scene(2, out int corner))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = T3Camera();
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                CapturedJobs captured = CaptureJobs(display);
                Assert.That(display.TryPrepareCamera(camera), Is.True, "never refused for the colour limit");
                display.CapJobsClassifiedForTest = null;

                int cornerOrdinary = 0;
                int cornerLast = 0;
                foreach (VpCapJob job in captured.jobs)
                {
                    if (job.renderFragment == corner)
                    {
                        cornerOrdinary += job.colour != captured.lastColour ? 1 : 0;
                        cornerLast += job.colour == captured.lastColour ? 1 : 0;
                    }
                }

                Assert.That(cornerOrdinary, Is.GreaterThan(0), "the layout: a corner cap in the ordinary colour");
                Assert.That(cornerLast, Is.GreaterThan(1), "and more than one in the last");
                Assert.That(captured.lastRenderFragments, Does.Contain(corner));
                VpStencilPreparation preparation = AssertArrangementFollowsTheClassification(display, camera, captured, "T3, limit 2");
                Assert.That(preparation.colours, Is.EqualTo(2));
                display.Render(0, camera);
                Assert.That(display.HasDrawnThisFrame, Is.True, "the bodies and the stencil are drawn");
                Read(camera);
            }
        }

        /// <summary>
        /// T3 under a limit of 1: no ordinary colour, every visible, non-empty job in the last colour, every render fragment
        /// of those jobs issued once -- A+B+C+ once for its three jobs -- and nothing refused.
        /// </summary>
        [Test]
        public void T3_WithOneColour_EveryJobIsInTheLastColour_EachRenderFragmentOnce()
        {
            using (Scene scene = T3Scene(1, out int corner))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = T3Camera();
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                CapturedJobs captured = CaptureJobs(display);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                display.CapJobsClassifiedForTest = null;

                Assert.That(captured.ordinaryColours, Is.Zero);
                Assert.That(captured.lastColour, Is.Zero);
                var renderFragments = new HashSet<int>();
                int cornerJobs = 0;
                foreach (VpCapJob job in captured.jobs)
                {
                    Assert.That(job.colour, Is.Zero, "every job in the last colour");
                    renderFragments.Add(job.renderFragment);
                    cornerJobs += job.renderFragment == corner ? 1 : 0;
                }

                Assert.That(cornerJobs, Is.EqualTo(3), "the layout: A+B+C+'s three caps");
                Assert.That(captured.lastRenderFragments.Count, Is.EqualTo(renderFragments.Count), "each render fragment once");
                VpStencilPreparation preparation = AssertArrangementFollowsTheClassification(display, camera, captured, "T3, limit 1");
                Assert.That(preparation.ordinaryVolumeGroups, Is.Zero);
                Assert.That(preparation.lastColourRenderFragments, Is.EqualTo(renderFragments.Count));
                Assert.That(preparation.lastColourCaps, Is.EqualTo(captured.jobs.Count));
                Assert.That(preparation.volumeCommands, Is.EqualTo(2 * renderFragments.Count), "two submeshes, each render fragment once");
                Assert.That(preparation.volumeGroups, Is.EqualTo(captured.groups.Count), "groups are counted apart from render fragments");
                display.Render(0, camera);
                Read(camera);
            }
        }

        /// <summary>
        /// T3 turned and moved -- a placement that is not the identity -- on a body of two submeshes of different index
        /// ranges (the four sides, 24 indices, and the two ends, 12), under a limit of 1. Every render fragment of the
        /// last colour is issued exactly once per submesh: each of the two draw ranges read from the storage once, never
        /// one of them twice and the other left out, each with the placement given to TryShow and the render fragment's
        /// clip of every selected face at its separation.
        /// </summary>
        [Test]
        public void T3_TurnedAndMoved_TwoSubmeshes_TheLastColourIssuesEachRangeOnceWithItsPlacement()
        {
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(0.6f, 0.4f, -0.5f), Quaternion.Euler(0f, 20f, 0f), Vector3.one);
            using (Scene scene = T3Scene(1, out int corner, placement))
            {
                VpLogicalCutDisplay display = scene.display;
                Assert.That(_expectedBodies[0].ranges.Length, Is.EqualTo(2), "the layout: two submeshes");
                Assert.That(_expectedBodies[0].ranges[0].indexCount, Is.Not.EqualTo(_expectedBodies[0].ranges[1].indexCount), "of different ranges");
                Camera camera = T3Camera();
                camera.transform.position += new Vector3(0.6f, 0.4f, -0.5f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                CapturedJobs captured = CaptureJobs(display);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                display.CapJobsClassifiedForTest = null;
                Assert.That(captured.lastRenderFragments, Does.Contain(corner), "the layout: A+B+C+ is in the last colour");
                VpStencilPreparation preparation = AssertArrangementFollowsTheClassification(display, camera, captured, "T3 turned and moved, limit 1");
                Assert.That(preparation.volumeCommands, Is.EqualTo(2 * captured.lastRenderFragments.Count));
                display.Render(0, camera);
                Read(camera);
            }
        }

        // ----- a group over two render fragments (X6) ----------------------------------------------------------------

        /// <summary>
        /// X6's shared top: one volume group of two jobs on two render fragments. In an ordinary colour its volume is
        /// arranged once; under a limit of 1 the group goes whole to the last colour, which issues each of the two render
        /// fragments once, with its own every-face clip -- one volume per render fragment, not one for the group.
        /// </summary>
        [Test]
        public void X6_ASharedGroup_IsOneVolumeWhenOrdinary_AndOnePerRenderFragmentInTheLastColour()
        {
            foreach (int colours in new[] { 4, 1 })
            {
                using (Scene scene = NewScene(colours: colours))
                {
                    LogicalCutLedger ledger = scene.ledger;
                    VpLogicalCutDisplay display = scene.display;
                    LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.5f, 0f), new float3(0.5f, -0.5f, 0f) });
                    VpStoredGeometry cube = AppendCube(scene.storage, false);
                    Assert.That(display.TryShow(root, cube, Matrix4x4.identity), Is.True);
                    ExpectBodies(scene, (cube, Matrix4x4.identity));
                    var (a, _, aMinus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
                    Admit(ledger, aMinus, new float4(1f, 0f, 0f, 0f));
                    Collect(scene);
                    Camera camera = Looking(new Vector3(0f, 0.5f, 0f), Vector3.down, 1.5f);
                    Assert.That(display.TryRegisterCamera(camera), Is.True);
                    if (colours == 4)
                    {
                        SharedTopGroup(display, camera, a, "limit 4");
                        continue;
                    }

                    CapturedJobs captured = CaptureJobs(display);
                    Assert.That(display.TryPrepareCamera(camera), Is.True);
                    display.CapJobsClassifiedForTest = null;
                    var tops = new List<VpCapJob>();
                    foreach (VpCapJob job in captured.jobs)
                    {
                        if (job.boundary.face.operation == a && job.boundary.side < 0f)
                        {
                            tops.Add(job);
                        }
                    }

                    Assert.That(tops.Count, Is.EqualTo(2), "the layout");
                    Assert.That(tops[0].volumeGroup, Is.EqualTo(tops[1].volumeGroup), "one group");
                    Assert.That(captured.groups[tops[0].volumeGroup].inLastColour, Is.True, "sent whole to the last colour");
                    Assert.That(captured.lastRenderFragments, Does.Contain(tops[0].renderFragment));
                    Assert.That(captured.lastRenderFragments, Does.Contain(tops[1].renderFragment));
                    VpStencilPreparation preparation = AssertArrangementFollowsTheClassification(display, camera, captured, "X6, limit 1");
                    Assert.That(preparation.lastColourRenderFragments, Is.EqualTo(captured.lastRenderFragments.Count));
                    Assert.That(preparation.volumeCommands, Is.EqualTo(captured.lastRenderFragments.Count), "one command body: one volume per render fragment");
                    display.Render(0, camera);
                    Read(camera);
                }
            }
        }

        // ----- nothing left, transitions and lifetime ------------------------------------------------------------------

        /// <summary>
        /// X5 under a limit of 2, one camera through one frame: A-B+ alone (ordinary only, no last colour issued), both
        /// tops (one ordinary, one last), looking away (no job, no colour), a refusal for room, an exception partway, and
        /// A-B+ alone again. Every success is uploaded once and arranged as classified; the refusal and the exception
        /// upload nothing and leave the camera unprepared; the shared classification holds nothing after any of them.
        /// </summary>
        [Test]
        public void OrdinaryOnly_ThenLast_ThenNothing_ThenRefusals_ThenOrdinaryAgain()
        {
            using (Scene scene = X5Scene(2))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = Looking(new Vector3(0.5f, 0.25f, 0f), Vector3.down, 0.3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapEye narrow = EyeOf(camera);
                camera.transform.position = new Vector3(0f, 0.25f, 0f);
                camera.orthographicSize = 2f;
                VpCapEye wide = EyeOf(camera);
                camera.transform.SetPositionAndRotation(new Vector3(0f, 0.25f, 50f), Quaternion.LookRotation(Vector3.forward, Vector3.up));
                VpCapEye away = EyeOf(camera);
                CapturedJobs captured = CaptureJobs(display);
                int uploads = display.StencilUploads;

                Assert.That(display.TryPrepareCamera(camera, narrow, narrow), Is.True);
                VpStencilPreparation ordinary = AssertArrangementFollowsTheClassification(display, camera, captured, "ordinary only");
                Assert.That(captured.lastColour, Is.EqualTo(-1), "nothing left: no last colour");
                Assert.That(ordinary.colours, Is.EqualTo(1));
                Assert.That(ordinary.lastColourRenderFragments + ordinary.lastColourCaps, Is.Zero);
                AssertTheClassificationHoldsNothing(display, "ordinary only");

                Assert.That(display.TryPrepareCamera(camera, wide, wide), Is.True, "both tops: not refused");
                VpStencilPreparation withLast = AssertArrangementFollowsTheClassification(display, camera, captured, "with the last colour");
                Assert.That(captured.lastColour, Is.EqualTo(1));
                Assert.That(withLast.colours, Is.EqualTo(2));
                Assert.That(withLast.ordinaryVolumeGroups, Is.GreaterThanOrEqualTo(1));
                Assert.That(withLast.lastColourRenderFragments, Is.GreaterThanOrEqualTo(1));
                Assert.That(withLast.lastColourCaps, Is.GreaterThanOrEqualTo(withLast.lastColourRenderFragments), "at least one cap per render fragment listed");
                var lastRenderFragments = new HashSet<int>();
                foreach (VpCapJob job in captured.jobs)
                {
                    if (job.colour == captured.lastColour)
                    {
                        lastRenderFragments.Add(job.renderFragment);
                    }
                }

                Assert.That(withLast.lastColourRenderFragments, Is.EqualTo(lastRenderFragments.Count), "the render fragments of its jobs, each once");

                Assert.That(display.TryPrepareCamera(camera, away, away), Is.True);
                VpStencilPreparation nothing = AssertArrangementFollowsTheClassification(display, camera, captured, "looking away");
                Assert.That(nothing.jobs + nothing.colours + nothing.volumeCommands + nothing.capsDrawn, Is.Zero);
                Assert.That(display.StencilUploads - uploads, Is.EqualTo(3), "one upload per success");

                // A refusal for room still refuses.
                int records = display.CapRecordCount;
                display.PreparationRecordLimit = records - 1;
                Assert.That(display.TryPrepareCamera(camera, wide, wide), Is.False);
                Assert.That(PreparationOf(display, camera).outcome, Is.EqualTo(VpStencilPreparationOutcome.CapacityExceeded));
                Assert.That(display.StencilUploads - uploads, Is.EqualTo(3), "nothing uploaded");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera));
                AssertTheClassificationHoldsNothing(display, "after a refusal for room");
                display.PreparationRecordLimit = records;

                // An exception partway: nothing uploaded, not prepared, nothing held, the display not stopped.
                display.CapJobs.AfterJobWritten = count => throw new InvalidProgramException("partway");
                Assert.Throws<InvalidProgramException>(() => display.TryPrepareCamera(camera, wide, wide));
                display.CapJobs.AfterJobWritten = null;
                Assert.That(display.StencilUploads - uploads, Is.EqualTo(3), "nothing uploaded");
                Assert.That(PreparationOf(display, camera).outcome, Is.EqualTo(VpStencilPreparationOutcome.None));
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera));
                Assert.That(display.IsHalted || display.IsBroken, Is.False);
                AssertTheClassificationHoldsNothing(display, "after an exception partway");

                Assert.That(display.TryPrepareCamera(camera, narrow, narrow), Is.True, "ordinary only again");
                AssertArrangementFollowsTheClassification(display, camera, captured, "ordinary only again");
                Assert.That(captured.lastColour, Is.EqualTo(-1));
                display.CapJobsClassifiedForTest = null;
                display.Render(0, camera);
                Read(camera);
                AssertTheClassificationHoldsNothing(display, "after drawing");
            }
        }

        // ----- helpers ---------------------------------------------------------------------------------------------------

        /// <summary>How far each piece of an opened T3 stands from where it was cut: the corner ends up at 3.5.</summary>
        private const float T3Apart = 3f;

        /// <summary>
        /// The four pieces of T3 where four owners pushed apart by the cuts would stand: each one <see cref="T3Apart"/>
        /// along the normal of every side it is on, given as base placements (<see cref="VpTestPlacements"/>).
        /// Nothing in the display puts them there.
        /// </summary>
        private VpTestPlacements T3Placements(
            Matrix4x4 at, LogicalFragmentId aMinus, LogicalFragmentId bMinus, LogicalFragmentId cMinus, LogicalFragmentId cPlus)
        {
            Matrix4x4 Away(float x, float y, float z)
            {
                return Matrix4x4.Translate(new Vector3(x, y, z) * T3Apart) * at;
            }

            return Placing(
                (aMinus, Away(0f, -1f, 0f)),
                (bMinus, Away(-1f, 1f, 0f)),
                (cMinus, Away(1f, 1f, -1f)),
                (cPlus, Away(1f, 1f, 1f)));
        }

        /// <summary>
        /// The placements to give the display, kept as this test's own expectation at the same time, so that a volume
        /// can be checked against where the test said its fragment stands.
        /// </summary>
        private VpTestPlacements Placing(params (LogicalFragmentId fragment, Matrix4x4 at)[] placements)
        {
            var given = new VpTestPlacements();
            foreach ((LogicalFragmentId fragment, Matrix4x4 at) in placements)
            {
                given.Put(fragment, at);
                _expectedPlacements[fragment] = at;
            }

            return given;
        }

        /// <summary>
        /// T3 on a body of two submeshes, instance capacity 8 (exactly the four render fragments), at the placement given
        /// (the identity by default). The cuts are in the body's own frame. The four pieces are all at that one
        /// placement, so they close each other's sections; for a case that has to see a cap, use
        /// <see cref="T3SceneOpened"/>.
        /// </summary>
        private Scene T3Scene(int colours, out int corner, Matrix4x4? placement = null)
        {
            Matrix4x4 at = placement ?? Matrix4x4.identity;
            Scene scene = NewScene(instances: 8, colours: colours);
            LogicalCutLedger ledger = scene.ledger;
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            VpStoredGeometry cube = AppendCube(scene.storage, true);
            Assert.That(scene.display.TryShow(root, cube, at), Is.True);
            ExpectBodies(scene, (cube, at));
            var (_, aPlus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (_, bPlus, _) = Cut(ledger, aPlus, new float4(1f, 0f, 0f, 0f));
            var (_, cPlus, _) = Cut(ledger, bPlus, new float4(0f, 0f, 1f, 0f));
            Collect(scene);
            corner = -1;
            for (int r = 0; r < scene.display.RenderFragmentCount; r++)
            {
                scene.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                corner = rf.root == cPlus ? r : corner;
            }

            Assert.That(corner, Is.Not.EqualTo(-1), "the layout: A+B+C+ found by C's positive child");
            return scene;
        }

        /// <summary>
        /// The same T3, with each piece at a base placement of its own (<see cref="T3Placements"/>), so the corner's
        /// three caps face a camera looking along (1, 1, 1) from below.
        /// </summary>
        private Scene T3SceneOpened(int colours, out int corner, Matrix4x4? placement = null)
        {
            Matrix4x4 at = placement ?? Matrix4x4.identity;
            Scene scene = NewScene(instances: 8, colours: colours);
            LogicalCutLedger ledger = scene.ledger;
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            VpStoredGeometry cube = AppendCube(scene.storage, true);
            Assert.That(scene.display.TryShow(root, cube, at), Is.True);
            ExpectBodies(scene, (cube, at));
            var (_, aPlus, aMinus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (_, bPlus, bMinus) = Cut(ledger, aPlus, new float4(1f, 0f, 0f, 0f));
            var (_, cPlus, cMinus) = Cut(ledger, bPlus, new float4(0f, 0f, 1f, 0f));
            scene.display.Placement = T3Placements(at, aMinus, bMinus, cMinus, cPlus);
            Collect(scene);
            corner = -1;
            for (int r = 0; r < scene.display.RenderFragmentCount; r++)
            {
                scene.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                corner = rf.root == cPlus ? r : corner;
            }

            Assert.That(corner, Is.Not.EqualTo(-1), "the layout: A+B+C+ found by C's positive child");
            return scene;
        }

        /// <summary>What the test itself registered, in registration order: each body's draw ranges and placement.</summary>
        private sealed class ExpectedBody
        {
            public VpGeometryRange[] ranges;
            public Matrix4x4 placement;
        }

        private readonly List<ExpectedBody> _expectedBodies = new List<ExpectedBody>();

        /// <summary>
        /// Where the test itself said each fragment stands, for a scene that gives the display placements of its own.
        /// Empty when it gave none, and then a body is expected at the placement it was registered with.
        /// </summary>
        private readonly Dictionary<LogicalFragmentId, Matrix4x4> _expectedPlacements = new Dictionary<LogicalFragmentId, Matrix4x4>();

        /// <summary>
        /// Where one render fragment's volume is expected: the placement this test gave for the fragment it is drawn
        /// for, or the body's registration placement when this test gave none. The fragment's name is read from the
        /// snapshot; where it stands is the test's own and is never read back from what is drawn.
        /// </summary>
        private Matrix4x4 ExpectedPlacementOf(VpLogicalCutDisplay display, int renderFragment, ExpectedBody body)
        {
            if (_expectedPlacements.Count == 0)
            {
                return body.placement;
            }

            Assert.That(display.TryGetRenderFragment(renderFragment, out VpMultiCutRenderFragment rf), Is.True);
            return _expectedPlacements.TryGetValue(rf.root, out Matrix4x4 followed) ? followed : body.placement;
        }

        /// <summary>
        /// Records the bodies a scene registered, in order: each submesh's draw range worked out from the storage -- the
        /// geometry's vertices, and its index range's start plus the submesh's offset and count -- and the placement the
        /// test gave. Never read from the display's commands.
        /// </summary>
        private void ExpectBodies(Scene scene, params (VpStoredGeometry geometry, Matrix4x4 placement)[] bodies)
        {
            _expectedBodies.Clear();
            _expectedPlacements.Clear();
            foreach ((VpStoredGeometry geometry, Matrix4x4 placement) in bodies)
            {
                Assert.That(scene.storage.TryGetIndexState(geometry.indexRange, out _, out int indexStart, out _), Is.True);
                Assert.That(scene.storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
                var ranges = new VpGeometryRange[submeshes.Length];
                for (int s = 0; s < submeshes.Length; s++)
                {
                    ranges[s] = new VpGeometryRange(geometry.vertexStart, geometry.vertexCount, indexStart + submeshes[s].indexOffset, submeshes[s].indexCount);
                }

                _expectedBodies.Add(new ExpectedBody { ranges = ranges, placement = placement });
            }
        }

        private static VpStencilPreparation PreparationOf(VpLogicalCutDisplay display, Camera camera)
        {
            Assert.That(display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _), Is.True);
            return preparation;
        }

        private Camera T3Camera()
        {
            Vector3 centre = new Vector3(3.5f, 3.5f, 3.5f);
            Vector3 along = new Vector3(1f, 1f, 1f).normalized;
            return Looking(centre - (along * 7f), along, 2.5f);
        }

        /// <summary>
        /// What the camera's preparation arranged, colour by colour, against the classification it was made from and the
        /// bodies the test registered. An ordinary colour holds each of its groups' own-face volumes; the last colour
        /// holds each of its render fragments once, clipped by every selected face at its separation. Every volume
        /// command is matched, one expected command consumed at a time, to exactly one of: a submesh's draw range read
        /// from the storage, the placement given to TryShow, and the expected clip -- so a range issued twice and another
        /// left out, a wrong placement or a wrong clip is found. Every colour's caps are exactly its jobs' caps, each
        /// fanned once, and no cap is fanned in two colours. Returns the preparation.
        /// </summary>
        private VpStencilPreparation AssertArrangementFollowsTheClassification(
            VpLogicalCutDisplay display, Camera camera, CapturedJobs captured, string what)
        {
            VpStencilPreparation preparation = PreparationOf(display, camera);
            Assert.That(preparation.outcome, Is.EqualTo(VpStencilPreparationOutcome.Prepared), what);
            Assert.That(preparation.colours, Is.EqualTo(captured.colours), what + ": the colours used");
            Assert.That(preparation.volumeGroups, Is.EqualTo(captured.groups.Count), what);
            Assert.That(preparation.jobs, Is.EqualTo(captured.jobs.Count), what);
            Assert.That(preparation.capsDrawn, Is.EqualTo(captured.jobs.Count), what + ": one cap per job");
            Assert.That(_expectedBodies.Count, Is.EqualTo(display.AdoptedSnapshot.RegistrationCount), what + ": the test recorded every body it registered");

            int ordinaryGroups = 0;
            int lastCaps = 0;
            int volumesSeen = 0;
            var fannedIn = new Dictionary<int, int>();
            for (int c = 0; c < preparation.colours; c++)
            {
                Assert.That(display.TryGetPreparedColor(camera, c, out VpStencilCapColor colour), Is.True, what + ": colour " + c);
                Assert.That(colour.volumeStart, Is.EqualTo(volumesSeen), what + ": colour " + c + "'s volumes follow the one before");
                volumesSeen += colour.volumeCount;
                bool last = c == captured.lastColour;

                // The commands this colour should hold: one per submesh of each volume, with its range, placement and clip.
                var expected = new List<(VpGeometryRange range, Matrix4x4 placement, VpInstanceClip clip, string label)>();
                if (last)
                {
                    foreach (int rf in captured.lastRenderFragments)
                    {
                        Assert.That(display.TryGetRenderFragment(rf, out VpMultiCutRenderFragment fragment), Is.True);
                        VpInstanceClip clip = fragment.clip;
                        AssertEveryFaceClip(display, rf, clip, what + ": last-colour render fragment " + rf);
                        ExpectedBody body = _expectedBodies[fragment.registration];
                        Matrix4x4 at = ExpectedPlacementOf(display, rf, body);
                        foreach (VpGeometryRange range in body.ranges)
                        {
                            expected.Add((range, at, clip, "render fragment " + rf));
                        }
                    }
                }
                else
                {
                    foreach (VpCapVolumeGroup group in captured.groups)
                    {
                        if (group.colour == c)
                        {
                            Assert.That(group.inLastColour, Is.False, what);
                            Assert.That(group.volumeClip.PlaneCount, Is.EqualTo(1), what + ": an ordinary volume is one face");
                            ExpectedBody body = _expectedBodies[group.registration];
                            Matrix4x4 at = ExpectedPlacementOf(display, group.renderFragment, body);
                            foreach (VpGeometryRange range in body.ranges)
                            {
                                expected.Add((range, at, group.volumeClip, "group of render fragment " + group.renderFragment));
                            }

                            ordinaryGroups++;
                        }
                    }
                }

                Assert.That(colour.volumeCount, Is.EqualTo(expected.Count), what + ": colour " + c + (last ? " (last)" : "") + "'s volume commands");
                var consumed = new bool[expected.Count];
                for (int v = colour.volumeStart; v < colour.volumeStart + colour.volumeCount; v++)
                {
                    Assert.That(display.TryGetArrangedVolume(v, out VpIndirectCommand command, out Matrix4x4 transform, out VpInstanceClip clip), Is.True);
                    Assert.That(command.instanceCount, Is.EqualTo(1), what + ": volume " + v + " is one instance");
                    int found = -1;
                    for (int e = 0; e < expected.Count && found < 0; e++)
                    {
                        found = !consumed[e] && SameRange(command.range, expected[e].range) && ExactMatrix(transform, expected[e].placement)
                            && clip.Equals(expected[e].clip) ? e : -1;
                    }

                    Assert.That(found, Is.Not.EqualTo(-1), what + ": volume " + v + " (range index " + command.range.indexStart + "+" + command.range.indexCount
                        + ") matches an expected command not matched yet -- its range, placement and clip");
                    consumed[found] = true;
                }

                for (int e = 0; e < expected.Count; e++)
                {
                    Assert.That(consumed[e], Is.True, what + ": " + expected[e].label + "'s range index " + expected[e].range.indexStart + " issued");
                }

                // Its caps: each of its jobs' caps fanned once, nothing else.
                var fans = new Dictionary<int, int>();
                for (int i = colour.capIndexStart; i < colour.capIndexStart + colour.capIndexCount; i += 3)
                {
                    Assert.That(display.TryGetArrangedCapIndex(i, out int root), Is.True);
                    fans[root] = fans.TryGetValue(root, out int n) ? n + 1 : 1;
                }

                foreach (VpCapJob job in captured.jobs)
                {
                    Assert.That(display.TryGetCapRecord(job.capIndex, out LogicalCutCapRecord record), Is.True);
                    int triangles = fans.TryGetValue(record.vertexStart, out int n) ? n : 0;
                    if (job.colour == c)
                    {
                        Assert.That(triangles, Is.EqualTo(record.vertexCount - 2), what + ": cap " + job.capIndex + " fanned once in colour " + c);
                        Assert.That(fannedIn.ContainsKey(job.capIndex), Is.False, what + ": cap " + job.capIndex + " in no other colour");
                        fannedIn[job.capIndex] = c;
                        lastCaps += last ? 1 : 0;
                    }
                    else
                    {
                        Assert.That(triangles, Is.Zero, what + ": cap " + job.capIndex + " not in colour " + c);
                    }
                }
            }

            Assert.That(volumesSeen, Is.EqualTo(preparation.volumeCommands), what);
            Assert.That(display.ArrangedVolumeCount, Is.EqualTo(preparation.volumeCommands), what);
            Assert.That(fannedIn.Count, Is.EqualTo(captured.jobs.Count), what + ": every job's cap drawn");
            Assert.That(preparation.ordinaryVolumeGroups, Is.EqualTo(ordinaryGroups), what);
            Assert.That(preparation.lastColourRenderFragments, Is.EqualTo(captured.lastRenderFragments.Count), what);
            Assert.That(preparation.lastColourCaps, Is.EqualTo(lastCaps), what);
            return preparation;
        }

        /// <summary>
        /// A last-colour volume's clip, against the render fragment's own boundaries as the display publishes them in its
        /// cap records: one plane per selected boundary, each the boundary's face with its kept side, and the render
        /// fragment's separation.
        /// </summary>
        private static void AssertEveryFaceClip(VpLogicalCutDisplay display, int renderFragment, in VpInstanceClip clip, string what)
        {
            var planes = new List<Vector4>();
            Vector3 offset = default;
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                display.TryGetCapRecord(i, out LogicalCutCapRecord record);
                if (record.renderFragment == renderFragment)
                {
                    planes.Add(record.side > 0f ? record.worldPlane : -record.worldPlane);
                }
            }

            Assert.That(clip.PlaneCount, Is.EqualTo(planes.Count), what + ": one plane per selected boundary");
            Assert.That(clip.PlaneCount, Is.GreaterThan(0), what);
            for (int k = 0; k < clip.PlaneCount; k++)
            {
                bool matched = false;
                foreach (Vector4 plane in planes)
                {
                    matched |= Same(clip.SignedPlane(k), plane);
                }

                Assert.That(matched, Is.True, what + ": plane " + k + " is one of its boundaries with its kept side");
            }

        }

        private static bool ExactMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ExactVector(Vector3 a, Vector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }
    }
}
