using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The stencil connection of <see cref="VpLogicalCutDisplay"/> with several bodies and several cameras (DESIGN 5.6,
    /// T-066): visibility, compatibility groups and bounded colours made per camera from the adopted snapshot, and
    /// drawn through each camera's own stencil batch. Non-XR, one cut per body, fixed placements.
    /// <para>
    /// The bodies are the truncated pyramid of the single-body stencil tests (bottom [-1, 1]², top [-0.5, 0.5]² at
    /// y = 2), cut at their own y = 1 with the anchor below, so each fixed bottom keeps an opening of [-0.75, 0.75]²
    /// under a Cap Bounds Polygon of [-1, 1]², and each free top is moved far up out of the camera's way. Every
    /// expectation — which caps are seen, which bodies overlap on screen, which pixels are capped — is read off that
    /// layout. The images are read back from orthographic cameras rendered one at a time, which is how this non-XR
    /// check drives the pipeline; the ordinary URP camera loop is not what is exercised here.
    /// </para>
    /// </summary>
    public class VpLogicalCutDisplayColoursTests
    {
        private const int ControlPoints = 8;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int Size = 128;
        private const float WideSeparation = 3f;

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, 1.0f), new float3(-1.0f, 0.0f, 1.0f),
            new float3(-0.5f, 2.0f, -0.5f), new float3(0.5f, 2.0f, -0.5f), new float3(0.5f, 2.0f, 0.5f), new float3(-0.5f, 2.0f, 0.5f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -1f);
        private static readonly float3 k_lowAnchor = new float3(0f, 0.2f, 0f);

        private readonly List<Object> _objects = new List<Object>();
        private int _frame;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
        }

        [TearDown]
        public void DestroyObjects()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] != null)
                {
                    Object.DestroyImmediate(_objects[i]);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        // ----- scenes ------------------------------------------------------------------------------------------------

        /// <summary>A display showing one pyramid per x position, each cut at its y = 1 with the top moved far up.</summary>
        private sealed class Scene : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public LogicalCutLedger ledger;
            public VpLogicalCutDisplay display;
            public Action endFrame;

            /// <summary>
            /// Ends the frame, then disposes. Every test renders the cameras it registers draws for, so the frame has
            /// been drawn by the time it ends.
            /// </summary>
            public void Dispose()
            {
                endFrame?.Invoke();
                display?.Dispose();
                storage?.Dispose();
            }
        }

        private Scene CutPyramids(VpStencilSettings settings, bool withShadows, params float[] xs)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(4096, 16384, 32, 128, 128, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8)),
                endFrame = () => _frame++,
            };
            var table = new VpGeometryReferenceTable(scene.storage, 8, 8);
            Material one = withShadows ? ShadowMaterial(CullMode.Back) : null;
            Material two = withShadows ? ShadowMaterial(CullMode.Off) : null;
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    scene.storage, table, scene.ledger, Materials(), one, two, 16, 16, settings, () => _frame,
                    out scene.display),
                Is.True,
                "create the display");
            scene.display.Separation = WideSeparation;

            var bodies = new List<LogicalFragmentId>();
            foreach (float x in xs)
            {
                LogicalFragmentId body = scene.ledger.AddFragment(new List<float3> { k_lowAnchor });
                Assert.That(scene.display.TryShow(body, Append(scene.storage), Matrix4x4.Translate(new Vector3(x, 0f, 0f))), Is.True);
                bodies.Add(body);
            }

            Assert.That(scene.display.TryBeginFrame(), Is.True, "the whole bodies settle");
            foreach (LogicalFragmentId body in bodies)
            {
                Assert.That(scene.ledger.Admit(body, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
                Assert.That(scene.ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            }

            _frame++;
            Assert.That(scene.display.TryBeginFrame(), Is.True, "the splits settle");
            Assert.That(scene.display.CapRecordCount, Is.EqualTo(2 * xs.Length), "two caps per body");
            return scene;
        }

        private static VpStencilPreparation PreparationOf(VpLogicalCutDisplay display, Camera camera)
        {
            Assert.That(display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _), Is.True);
            return preparation;
        }

        private static VpStencilCameraCounts CountsOf(VpLogicalCutDisplay display, Camera camera)
        {
            Assert.That(display.TryGetCameraStencil(camera, out _, out VpStencilCameraCounts counts), Is.True);
            return counts;
        }

        // ----- colours --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Two pyramids 1.2 apart: their openings overlap on screen (x from -0.75 to 0.75, and from 0.45 to 1.95), and
        /// they are under different cuts, so their caps go to two ordinary colours. Each opening is capped, the overlap
        /// included, and nothing outside the openings is: neither polygon's overhang is drawn, over the other body or
        /// over nothing.
        /// </summary>
        [Test]
        public void OverlappingIncompatibleBodies_AreDrawnInSeparateOrdinaryColours_WithNoCapOutsideTheOpenings()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), false, 0f, 1.2f))
            {
                Camera camera = TopDown(0.6f, 3f);
                Color32[] image = Draw(scene.display, camera);

                VpStencilPreparation preparation = PreparationOf(scene.display, camera);
                Assert.That(preparation.targets, Is.EqualTo(4), "two caps per body");
                Assert.That(preparation.groups, Is.EqualTo(4), "every cap is under its own cut and side");
                Assert.That(preparation.culledGroups, Is.EqualTo(2), "the two moved tops are behind the camera");
                Assert.That(preparation.colours, Is.EqualTo(2), "the two seen caps overlap: two colours");
                Assert.That(preparation.ordinaryColours, Is.EqualTo(2));
                Assert.That(preparation.groupsInLastColour, Is.Zero);
                Assert.That(preparation.volumeTargets, Is.EqualTo(2), "the volumes of the two kept sides");
                Assert.That(preparation.capsDrawn, Is.EqualTo(2), "and their two seen caps");

                Assert.That(IsRed(At(image, camera, 0f, 0f)), Is.True, "the first opening");
                Assert.That(IsRed(At(image, camera, 1.2f, 0f)), Is.True, "the second opening");
                Assert.That(IsRed(At(image, camera, 0.6f, 0f)), Is.True, "where the openings overlap");
                Assert.That(IsRed(At(image, camera, -0.9f, -0.9f)), Is.False, "the first polygon's overhang, over nothing");
                Assert.That(IsRed(At(image, camera, 2.1f, 0.9f)), Is.False, "the second polygon's overhang");
                Assert.That(IsRed(At(image, camera, 0.6f, 0.9f)), Is.False, "both overhangs, outside both openings");
                Assert.That(IsRed(At(image, camera, -2.4f, 0f)), Is.False, "outside everything");
            }
        }

        /// <summary>Two pyramids 2.6 apart do not overlap on screen, so their caps share one ordinary colour.</summary>
        [Test]
        public void BodiesApartOnScreen_ShareAnOrdinaryColour()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), false, -1.3f, 1.3f))
            {
                Camera camera = TopDown(0f, 3f);
                Color32[] image = Draw(scene.display, camera);

                VpStencilPreparation preparation = PreparationOf(scene.display, camera);
                Assert.That(preparation.culledGroups, Is.EqualTo(2));
                Assert.That(preparation.colours, Is.EqualTo(1), "one colour for both");
                Assert.That(preparation.ordinaryColours, Is.EqualTo(1));
                Assert.That(IsRed(At(image, camera, -1.3f, 0f)), Is.True);
                Assert.That(IsRed(At(image, camera, 1.3f, 0f)), Is.True);
                Assert.That(IsRed(At(image, camera, 0f, 0f)), Is.False, "between them");
            }
        }

        /// <summary>
        /// The overlapping pair under a limit of 2 — one ordinary colour — and of 1 — none: what does not fit goes to
        /// the last colour, no cap is lost, and no more colours are drawn than the limit. The last colour's image is
        /// not held to anything beyond that.
        /// </summary>
        [Test]
        public void ASmallLimit_PutsWhatDoesNotFitInTheLastColour_WithinTheLimit()
        {
            foreach (int limit in new[] { 2, 1 })
            {
                using (Scene scene = CutPyramids(VpStencilTestSettings.Create(limit), false, 0f, 1.2f))
                {
                    Camera camera = TopDown(0.6f, 3f);
                    int inits = scene.display.StencilInitIssues;
                    Color32[] image = Draw(scene.display, camera);

                    VpStencilPreparation preparation = PreparationOf(scene.display, camera);
                    Assert.That(preparation.colours, Is.InRange(1, limit), "limit " + limit + ": within the limit");
                    Assert.That(preparation.ordinaryColours, Is.EqualTo(limit - 1), "limit " + limit);
                    Assert.That(preparation.groupsInLastColour, Is.EqualTo(2 - (limit - 1)), "limit " + limit + ": the rest in the last");
                    Assert.That(
                        scene.display.StencilInitIssues - inits, Is.EqualTo(preparation.colours),
                        "limit " + limit + ": one initialisation per colour drawn, none past the limit");
                    Assert.That(IsRed(At(image, camera, 0f, 0f)), Is.True, "limit " + limit + ": the first body is capped");
                    Assert.That(IsRed(At(image, camera, 1.2f, 0f)), Is.True, "limit " + limit + ": and so is the second");
                }
            }
        }

        // ----- the view, and cameras ------------------------------------------------------------------------------------

        /// <summary>
        /// Within one frame, the same camera moved to look away and prepared again drops every cap; put back and
        /// prepared again, it has them again. Only the stencil arrangement is made again: no collection, no body
        /// upload, no geometry transfer, no polygon.
        /// </summary>
        [Test]
        public void AViewChange_IsClassifiedAgain_WithoutCollectingOrTransferring()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), false, -1.3f, 1.3f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(1), "looking down: both caps");

                int collections = display.SettledCollections;
                int commandUploads = display.CommandUploads;
                int vertices = display.VertexTransfers;
                int indices = display.IndexTransfers;
                int polygons = display.CapPolygonBuilds;
                int stencilUploads = display.StencilUploads;

                // Moved 50 along +z and looking further along it: every cap is behind the camera.
                Vector3 position = camera.transform.position;
                Quaternion down = camera.transform.rotation;
                camera.transform.SetPositionAndRotation(new Vector3(0f, 2.5f, 50f), Quaternion.LookRotation(Vector3.forward, Vector3.up));
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                VpStencilPreparation away = PreparationOf(display, camera);
                Assert.That(away.colours, Is.Zero, "looking away from every cap");
                Assert.That(away.culledGroups, Is.EqualTo(4));

                camera.transform.SetPositionAndRotation(position, down);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(1), "looking down again");

                Assert.That(display.SettledCollections, Is.EqualTo(collections), "no collection");
                Assert.That(display.CommandUploads, Is.EqualTo(commandUploads), "no body upload");
                Assert.That(display.VertexTransfers, Is.EqualTo(vertices), "no vertex transfer");
                Assert.That(display.IndexTransfers, Is.EqualTo(indices), "no index transfer");
                Assert.That(display.CapPolygonBuilds, Is.EqualTo(polygons), "no polygon taken again");
                Assert.That(display.StencilUploads, Is.EqualTo(stencilUploads + 2), "one stencil upload per preparation");

                Color32[] image = RenderAndRead(display, camera);
                Assert.That(IsRed(At(image, camera, -1.3f, 0f)), Is.True, "drawn as last prepared");
            }
        }

        /// <summary>
        /// Seen from the side at the height of the bottoms, no cap is seen: the bottoms' caps face up, away from the
        /// camera, and the moved tops' are above the view. Every group is left out, so no stencil work is issued at all,
        /// while the bodies and their shadow casters are drawn as ever.
        /// </summary>
        [Test]
        public void GroupsWithNoCapSeen_IssueNoStencilWork_AndTheBodiesAndShadowsStay()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), true, -1.3f, 1.3f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = FromTheSide();
                int inits = display.StencilInitIssues;
                int volumes = display.StencilVolumeIssues;
                int caps = display.StencilCapIssues;
                int oneSided = display.OneSidedShadowIssues;
                int twoSided = display.TwoSidedShadowIssues;

                Color32[] image = Draw(display, camera);
                VpStencilPreparation preparation = PreparationOf(display, camera);
                Assert.That(preparation.targets, Is.EqualTo(4));
                Assert.That(preparation.culledGroups, Is.EqualTo(preparation.groups), "every group is left out");
                Assert.That(preparation.colours, Is.Zero);
                Assert.That(preparation.volumeTargets, Is.Zero, "no volume");
                Assert.That(preparation.capsDrawn, Is.Zero);
                Assert.That(display.StencilInitIssues, Is.EqualTo(inits), "no initialisation");
                Assert.That(display.StencilVolumeIssues, Is.EqualTo(volumes), "no volume");
                Assert.That(display.StencilCapIssues, Is.EqualTo(caps), "no cap");
                Assert.That(display.TwoSidedShadowIssues, Is.EqualTo(twoSided + 1), "the split bodies still cast");
                Assert.That(display.OneSidedShadowIssues, Is.EqualTo(oneSided), "and nothing is whole");

                Color32 body = AtSide(image, -1.3f, 0.5f);
                Assert.That(IsRed(body), Is.False, "no cap");
                Assert.That(body.r + body.g + body.b, Is.GreaterThan(100), "the first bottom is drawn, grey");
                Color32 second = AtSide(image, 1.3f, 0.5f);
                Assert.That(second.r + second.g + second.b, Is.GreaterThan(100), "and the second");
                Color32 gap = AtSide(image, 0f, 0.5f);
                Assert.That(gap.r + gap.g + gap.b, Is.LessThan(30), "with the background between them");
            }
        }

        /// <summary>
        /// Two cameras in one frame, each with its own classification: one sees both overlapping bodies and gets two
        /// colours, the other is narrowed onto the first body and gets one. Each draws what it was prepared for, into
        /// its own batch. A camera that has drawn is not prepared again, nor let go, in that frame; the next frame it
        /// may be let go.
        /// </summary>
        [Test]
        public void TwoCameras_EachDrawTheirOwnClassification_AndADrawnCameraIsNotPreparedAgain()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera wide = TopDown(0.6f, 3f);
                Camera narrow = TopDown(-0.5f, 0.5f);
                Assert.That(display.TryRegisterCamera(wide), Is.True);
                Assert.That(display.TryRegisterCamera(narrow), Is.True);
                Assert.That(display.TryPrepareCamera(wide), Is.True);
                Assert.That(display.TryPrepareCamera(narrow), Is.True);

                Assert.That(PreparationOf(display, wide).colours, Is.EqualTo(2), "the wide camera sees both overlapping caps");
                VpStencilPreparation narrowed = PreparationOf(display, narrow);
                Assert.That(narrowed.colours, Is.EqualTo(1), "the narrow one sees only the first body's");
                Assert.That(narrowed.culledGroups, Is.EqualTo(3));

                Color32[] wideImage = RenderAndRead(display, wide);
                Color32[] narrowImage = RenderAndRead(display, narrow);
                Assert.That(CountsOf(display, wide).initIssues, Is.EqualTo(2), "the wide camera drew two colours");
                Assert.That(CountsOf(display, narrow).initIssues, Is.EqualTo(1), "the narrow one drew one");
                Assert.That(IsRed(At(wideImage, wide, 1.2f, 0f)), Is.True, "the wide camera caps the second body");
                Assert.That(IsRed(At(narrowImage, narrow, -0.5f, 0f)), Is.True, "the narrow one caps the first");

                int uploads = CountsOf(display, wide).uploads;
                Assert.That(display.TryPrepareCamera(wide), Is.False, "a camera that has drawn is not prepared again this frame");
                Assert.That(CountsOf(display, wide).uploads, Is.EqualTo(uploads), "and its buffers were not written");
                Assert.That(display.TryUnregisterCamera(wide), Is.False, "nor let go while its draws are this frame's");

                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True);
                Assert.That(CountsOf(display, wide).preparedNow, Is.False, "a new frame, not prepared");
                Assert.That(display.TryUnregisterCamera(wide), Is.True, "the next frame it may go");
                Assert.That(display.RegisteredCameraCount, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// A draw that is not prepared for this frame's snapshot registers nothing — not the body, not a shadow, not the
        /// stencil — and says so: a camera never registered, one registered and not prepared, and one prepared in the
        /// previous frame, before a newer snapshot was adopted. No camera at all is refused too.
        /// </summary>
        [Test]
        public void AnUnpreparedDraw_IsRefusedBeforeAnythingIsRegistered()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), true, 0f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera stranger = TopDown(0f, 3f);
                Camera registered = TopDown(0f, 3f);
                Camera stale = TopDown(0f, 3f);
                Assert.That(display.TryRegisterCamera(registered), Is.True);
                Assert.That(display.TryRegisterCamera(stale), Is.True);
                Assert.That(display.TryPrepareCamera(stale), Is.True);

                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "a newer snapshot is adopted");
                int oneSided = display.OneSidedShadowIssues;
                int twoSided = display.TwoSidedShadowIssues;
                int inits = display.StencilInitIssues;

                Assert.Throws<ArgumentNullException>(() => display.Render(0, null), "no camera");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, stranger), "never registered");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, registered), "registered, not prepared");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, stale), "prepared for the earlier snapshot");

                Assert.That(display.HasDrawnThisFrame, Is.False, "nothing was registered");
                Assert.That(display.OneSidedShadowIssues, Is.EqualTo(oneSided));
                Assert.That(display.TwoSidedShadowIssues, Is.EqualTo(twoSided));
                Assert.That(display.StencilInitIssues, Is.EqualTo(inits));

                Assert.That(display.TryPrepareCamera(stale), Is.True, "prepared again for this snapshot");
                Assert.DoesNotThrow(() => RenderAndRead(display, stale));
            }
        }

        /// <summary>
        /// Both cameras prepared, then both cameras' draws registered, and only then each camera rendered and read: the
        /// wide one still draws its two colours and the narrow one its one, so neither camera's registered draws read the
        /// other's stencil buffers.
        /// </summary>
        [Test]
        public void TwoCamerasRegisteredTogether_EachDrawTheirOwnArrangement()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera wide = TopDown(0.6f, 3f);
                Camera narrow = TopDown(-0.5f, 0.5f);
                Assert.That(display.TryRegisterCamera(wide), Is.True);
                Assert.That(display.TryRegisterCamera(narrow), Is.True);
                Assert.That(display.TryPrepareCamera(wide), Is.True);
                Assert.That(display.TryPrepareCamera(narrow), Is.True);

                display.Render(0, wide);
                display.Render(0, narrow);
                Assert.That(display.TryPrepareCamera(wide), Is.False, "registered: not prepared again");
                Assert.That(display.TryPrepareCamera(narrow), Is.False);

                Color32[] wideImage = Read(wide);
                Color32[] narrowImage = Read(narrow);
                Assert.That(PreparationOf(display, wide).colours, Is.EqualTo(2));
                Assert.That(PreparationOf(display, narrow).colours, Is.EqualTo(1));
                Assert.That(CountsOf(display, wide).initIssues, Is.EqualTo(2), "the wide camera's draws: two colours");
                Assert.That(CountsOf(display, narrow).initIssues, Is.EqualTo(1), "the narrow camera's: one");
                Assert.That(IsRed(At(wideImage, wide, 0f, 0f)), Is.True, "the wide camera caps the first body");
                Assert.That(IsRed(At(wideImage, wide, 1.2f, 0f)), Is.True, "and the second");
                Assert.That(IsRed(At(wideImage, wide, -0.9f, -0.9f)), Is.False, "and no overhang");
                Assert.That(IsRed(At(narrowImage, narrow, -0.5f, 0f)), Is.True, "the narrow camera caps the first body");
                Assert.That(IsRed(At(narrowImage, narrow, -0.9f, 0.45f)), Is.False, "and not the overhang it sees");
            }
        }

        /// <summary>
        /// A display whose camera has draws registered in this frame is not disposed: the refusal comes before anything
        /// is let go, so the display is whole and still usable. Once the frame has been drawn and ended, it disposes.
        /// </summary>
        [Test]
        public void Disposing_IsRefusedWhileThisFramesDrawsAreRegistered()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), false, 0f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0f, 3f);
                Draw(display, camera);

                Assert.Throws<InvalidOperationException>(() => display.Dispose(), "draws registered this frame");
                Assert.That(display.IsDisposed, Is.False, "nothing was let go");
                Assert.That(display.RegisteredCameraCount, Is.EqualTo(1), "the camera and its batch are still there");
                Assert.That(display.ShownCount, Is.EqualTo(1), "and so is the body");
                Assert.That(display.TryGetCameraStencil(camera, out _, out _), Is.True);
            }
        }

        /// <summary>
        /// What each record of a group takes, on written-out groups: a group is kept when any of its caps is seen, then
        /// every record in it keeps its volumes while only a seen cap is drawn; a group with no cap seen takes nothing.
        /// The display's own path cannot put two records in one group today — one operation has one source — so the
        /// mixed group is shown on the selection itself, which the preparation uses.
        /// </summary>
        [Test]
        public void AGroupWithSeenAndUnseenCaps_KeepsEveryVolume_AndDrawsOnlyTheSeenCaps()
        {
            bool[] seen = { true, false, false, false };
            int[] groupOfRecord = { 0, 0, 1, 0 };
            var groupKept = new bool[] { false, true };
            var capIssued = new bool[] { true, true, true, true };

            VpLogicalCutDisplay.SelectStencilWork(seen, groupOfRecord, 2, groupKept, capIssued);

            Assert.That(groupKept[0], Is.True, "group 0 has a cap seen");
            Assert.That(groupKept[1], Is.False, "group 1 has none");
            Assert.That(capIssued[0], Is.True, "the seen cap is drawn");
            Assert.That(capIssued[1], Is.False, "an unseen cap of a kept group is not");
            Assert.That(capIssued[3], Is.False, "nor another");
            Assert.That(capIssued[2], Is.False, "nor any cap of a group left out");
            for (int r = 0; r < seen.Length; r++)
            {
                bool volumes = groupKept[groupOfRecord[r]];
                Assert.That(volumes, Is.EqualTo(r != 2), "record " + r + ": volumes kept exactly when its group is");
            }
        }

        /// <summary>
        /// The settings are taken as given: a colour limit past what the stencil materials can order, or below one, a
        /// value that is not a finite length, or no camera room, refuses the display rather than being cut down. A full
        /// set of camera slots refuses one more camera, and a camera is registered once.
        /// </summary>
        [Test]
        public void TheSettingsAreTakenAsGiven_AndTheCameraSlotsAreFixed()
        {
            int ceiling = VpStencilCapMaterials.MaxColors;
            var refused = new[]
            {
                (VpStencilTestSettings.Create(ceiling + 1), "a limit past the materials"),
                (VpStencilTestSettings.Create(0), "no colour"),
                (new VpStencilSettings(4, float.NaN, 1e-4f, 1e-4f, Vector2.zero, 1), "a facing epsilon that is not finite"),
                (new VpStencilSettings(4, 0.01f, -1f, 1e-4f, Vector2.zero, 1), "a negative plane epsilon"),
                (new VpStencilSettings(4, 0.01f, 1e-4f, 1e-4f, new Vector2(0f, float.PositiveInfinity), 1), "an infinite margin"),
                (VpStencilTestSettings.Create(4, 0), "no camera room"),
            };

            using (var storage = new VpCpuGeometryStorage(1024, 4096, 16, 64, 64, Allocator.Persistent))
            {
                // A storage takes one reference table; each refusal leaves it as it was.
                var table = new VpGeometryReferenceTable(storage, 4, 4);
                foreach ((VpStencilSettings settings, string what) in refused)
                {
                    Assert.That(
                        VpLogicalCutDisplay.TryCreate(
                            storage, table, new LogicalCutLedger(new LogicalCutIncompleteBudget(2)),
                            Materials(), null, null, 4, 4, settings, out VpLogicalCutDisplay display),
                        Is.False, what);
                    Assert.That(display, Is.Null, what);
                }
            }

            using (var storage = new VpCpuGeometryStorage(1024, 4096, 16, 64, 64, Allocator.Persistent))
            {
                Assert.That(
                    VpLogicalCutDisplay.TryCreate(
                        storage, new VpGeometryReferenceTable(storage, 4, 4), new LogicalCutLedger(new LogicalCutIncompleteBudget(2)),
                        Materials(), null, null, 4, 4, VpStencilTestSettings.Create(ceiling, 1), out VpLogicalCutDisplay display),
                    Is.True, "the ceiling itself is accepted");
                using (display)
                {
                    Assert.That(display.StencilSettings.maxStencilColors, Is.EqualTo(ceiling), "and kept as given");
                    Camera first = TopDown(0f, 3f);
                    Assert.That(display.TryRegisterCamera(first), Is.True);
                    Assert.That(display.TryRegisterCamera(first), Is.False, "once");
                    Assert.That(display.TryRegisterCamera(TopDown(0f, 3f)), Is.False, "one slot, taken");
                    Assert.That(display.TryUnregisterCamera(first), Is.True, "never drawn, so it may go");
                    Assert.That(display.RegisteredCameraCount, Is.Zero);
                }
            }
        }

        // ----- fixture ---------------------------------------------------------------------------------------------------

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(
                        k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = k_controlPoints[c[k]], normal = n, uv0 = uv[k] });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), ControlPoints, submeshes.ToArray(),
                    out VpStoredGeometry geometry),
                Is.True);
            return geometry;
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null, "the VP unlit shader");
            var side = Track(new Material(shader) { name = "side" });
            var end = Track(new Material(shader) { name = "end" });
            side.SetColor("_BaseColor", new Color(0.6f, 0.6f, 0.6f));
            end.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.5f));
            return new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, end } };
        }

        private Material ShadowMaterial(CullMode cull)
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
            Assert.That(shader, Is.Not.Null, "the VP indexed indirect shadow caster");
            var material = Track(new Material(shader) { name = "colours shadow caster " + cull });
            material.SetFloat("_Cull", (float)cull);
            return material;
        }

        private Camera NewCamera(string name)
        {
            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32)
            {
                depthStencilFormat = VpStencilAttachment.EightBitStencilFormat,
                antiAliasing = 1,
            });
            target.Create();
            Camera camera = Track(new GameObject(name)).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            return camera;
        }

        /// <summary>
        /// An orthographic camera at y = 2.5 looking straight down at x = <paramref name="centreX"/>, seeing
        /// <paramref name="size"/> either side, and reaching to y = -0.5: between the fixed bottoms and the moved tops.
        /// </summary>
        private Camera TopDown(float centreX, float size)
        {
            Camera camera = NewCamera("Colours Top Down Camera");
            camera.orthographicSize = size;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 3f;
            camera.transform.position = new Vector3(centreX, 2.5f, 0f);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            return camera;
        }

        /// <summary>
        /// An orthographic camera at the bottoms' mid height, y = 0.5, in front of them at z = -5, looking along +z and
        /// seeing x from -3 to 3 and y from -2.5 to 3.5 — below both cut planes at y = 1, and short of the moved tops at
        /// y = 4.
        /// </summary>
        private Camera FromTheSide()
        {
            Camera camera = NewCamera("Colours Side Camera");
            camera.orthographicSize = 3f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.transform.position = new Vector3(0f, 0.5f, -5f);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            return camera;
        }

        private Color32[] Draw(VpLogicalCutDisplay display, Camera camera)
        {
            display.TryRegisterCamera(camera);
            Assert.That(display.TryPrepareCamera(camera), Is.True, "the camera is prepared");
            return RenderAndRead(display, camera);
        }

        private Color32[] RenderAndRead(VpLogicalCutDisplay display, Camera camera)
        {
            display.Render(0, camera);
            return Read(camera);
        }

        /// <summary>Renders the camera, drawing whatever was registered for it, and reads its target back.</summary>
        private Color32[] Read(Camera camera)
        {
            RenderTexture target = camera.targetTexture;
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                camera.Render();
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var read = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        /// <summary>The pixel under world (x, z) for a top-down camera: +x to the right, +z up the image.</summary>
        private static Color32 At(Color32[] pixels, Camera camera, float x, float z)
        {
            float size = camera.orthographicSize;
            float cx = camera.transform.position.x;
            int px = Mathf.Clamp(Mathf.RoundToInt((x - cx + size) / (2f * size) * (Size - 1)), 0, Size - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt((z + size) / (2f * size) * (Size - 1)), 0, Size - 1);
            return pixels[py * Size + px];
        }

        /// <summary>The pixel under world (x, y) for the side camera.</summary>
        private static Color32 AtSide(Color32[] pixels, float x, float y)
        {
            int px = Mathf.Clamp(Mathf.RoundToInt((x + 3f) / 6f * (Size - 1)), 0, Size - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt((y - 0.5f + 3f) / 6f * (Size - 1)), 0, Size - 1);
            return pixels[py * Size + px];
        }

        private static bool IsRed(Color32 c)
        {
            return c.r > 180 && c.g < 80 && c.b < 80;
        }
    }
}
