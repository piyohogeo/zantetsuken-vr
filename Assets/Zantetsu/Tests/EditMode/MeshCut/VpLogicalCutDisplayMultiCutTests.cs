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
    /// The multi-cut snapshot connected to the display and its stencil work: several cuts on one lineage, and several
    /// registrations, drawn by the one path the single cut uses. Each test builds its ledger through the ledger's own
    /// calls (admission, preparation, publication, abort, stale) and reads only what the display publishes.
    /// <para>
    /// The body is the cube [-1, 1]^3, one command, or two when <c>twoSubmeshes</c> is asked for. Every capacity is a
    /// test value. Pixels are read from an explicit render request into a small target; they are held to a coarse
    /// answer only -- red inside a cap, not red where there is none.
    /// </para>
    /// </summary>
    public class VpLogicalCutDisplayMultiCutTests
    {
        private const int SideMaterial = 3;
        private const int EndMaterial = 4;
        private const int Size = 128;
        private const float WideSeparation = 3f;
        private const float Tolerance = 1e-4f;

        private static readonly float3[] k_cube =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(-1f, -1f, 1f),
            new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
            new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

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

        // ----- pending, then published ----------------------------------------------------------------------------

        /// <summary>
        /// Two cuts on one lineage, each pending and then published, on a body of two submeshes placed with a rotation:
        /// publishing changes nothing drawn -- every instance's clip record and offset, every cap's plane, normal and
        /// vertices stay exactly as they were, and no section is taken again -- only who the sides and caps belong to.
        /// Each body command is drawn once per render fragment. A change of separation moves the offsets and takes no
        /// section again either.
        /// </summary>
        [Test]
        public void TwoCutsEachPendingThenPublished_ChangeNothingDrawn()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                Quaternion rotation = Quaternion.Euler(0f, 30f, 0f);
                LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(
                    display.TryShow(root, AppendCube(scene.storage, true), Matrix4x4.TRS(new Vector3(0.5f, 0f, 0f), rotation, Vector3.one)),
                    Is.True);
                Collect(scene);
                Assert.That(display.DrawCommandCount, Is.EqualTo(2), "the layout: two submeshes, two commands");

                CutOperationId a = Admit(ledger, root, new float4(0f, 1f, 0f, 0f));
                Collect(scene);
                Assert.That(display.StateOf(root), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                Assert.That(display.RenderFragmentCount, Is.EqualTo(2));
                Assert.That(display.SideCount, Is.EqualTo(4), "each command once per render fragment");
                AssertCommandsDrawEveryRenderFragment(display, 2);
                Drawn pendingA = Capture(display);
                int builds = display.CapPolygonBuilds;

                Assert.That(ledger.Publish(a, out LogicalFragmentId plus, out LogicalFragmentId minus), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Collect(scene);
                AssertSameShape(pendingA, Capture(display), "A published");
                Assert.That(display.CapPolygonBuilds, Is.EqualTo(builds), "no section taken again when A is published");
                Assert.That(display.StateOf(plus), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                Assert.That(display.StateOf(minus), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                AssertSidesName(display, plus, minus);

                CutOperationId b = Admit(ledger, plus, new float4(1f, 0f, 0f, -0.2f));
                Collect(scene);
                Assert.That(display.RenderFragmentCount, Is.EqualTo(3), "A-, and A+ as B's two sides");
                Assert.That(display.SideCount, Is.EqualTo(6));
                AssertCommandsDrawEveryRenderFragment(display, 3);
                Drawn pendingB = Capture(display);
                builds = display.CapPolygonBuilds;

                // The offsets are the lineage's: A+ is free (the anchor is below), and A+ has no anchor for B's sides.
                Vector3 nA = rotation * Vector3.up;
                Vector3 nB = rotation * Vector3.right;
                // Render fragments in walk order: A+ B+, A+ B-, A-.
                AssertOffset(display, 0, (nA + nB) * WideSeparation, "A+ B+");
                AssertOffset(display, 1, (nA - nB) * WideSeparation, "A+ B-");
                AssertOffset(display, 2, Vector3.zero, "A- is fixed");

                Assert.That(ledger.Publish(b, out LogicalFragmentId plusPlus, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Collect(scene);
                AssertSameShape(pendingB, Capture(display), "B published");
                Assert.That(display.CapPolygonBuilds, Is.EqualTo(builds), "no section taken again when B is published");
                Assert.That(display.StateOf(plusPlus), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                Assert.That(display.StateOf(plus), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit), "an intermediate fragment");

                display.Separation = 1f;
                Collect(scene);
                Assert.That(display.CapPolygonBuilds, Is.EqualTo(builds), "a new separation takes no section again");
                AssertOffset(display, 0, nA + nB, "A+ B+ at the new separation");

                Camera camera = Oblique();
                Draw(display, camera);
            }
        }

        // ----- intersecting planes ----------------------------------------------------------------------------------

        /// <summary>
        /// Three planes that meet inside the body: every cap of every render fragment lies on its own plane and inside
        /// every other selected half-space of that render fragment, once its offset is taken back off, and no cap has
        /// more than fourteen vertices. A render fragment has one cap per condition.
        /// </summary>
        [Test]
        public void IntersectingPlanes_KeepEveryCapInsideTheOtherSelectedHalfSpaces()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                var (_, plus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
                var (_, plusPlus, _) = Cut(ledger, plus, new float4(1f, 0f, 0f, 0f));
                Admit(ledger, plusPlus, Normalized(new float4(-0.5f, 0f, 1f, 0f)));
                Collect(scene);

                Assert.That(display.RenderFragmentCount, Is.EqualTo(4));
                int deepest = 0;
                for (int r = 0; r < display.RenderFragmentCount; r++)
                {
                    Assert.That(display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    Assert.That(rf.capCount, Is.EqualTo(rf.conditionCount), "one cap per condition");
                    deepest = Math.Max(deepest, rf.conditionCount);
                }

                Assert.That(deepest, Is.EqualTo(3), "the layout: three planes on the deepest render fragments");
                int nonEmpty = 0;
                for (int i = 0; i < display.CapRecordCount; i++)
                {
                    Assert.That(display.TryGetCapRecord(i, out LogicalCutCapRecord cap), Is.True);
                    Assert.That(cap.vertexCount, Is.InRange(0, 14));
                    nonEmpty += cap.vertexCount > 0 ? 1 : 0;
                    for (int v = 0; v < cap.vertexCount; v++)
                    {
                        Assert.That(display.TryGetCapVertex(i, v, out Vector3 world), Is.True);
                        Vector3 point = world - cap.offset;
                        Assert.That(Evaluate(cap.worldPlane, point), Is.EqualTo(0f).Within(Tolerance), "on its own plane");
                        for (int j = 0; j < display.CapRecordCount; j++)
                        {
                            display.TryGetCapRecord(j, out LogicalCutCapRecord other);
                            if (j == i || other.renderFragment != cap.renderFragment)
                            {
                                continue;
                            }

                            Assert.That(
                                other.side * Evaluate(other.worldPlane, point), Is.GreaterThanOrEqualTo(-Tolerance),
                                "cap " + i + " vertex " + v + " inside the other selected half-space of cap " + j);
                        }
                    }
                }

                Assert.That(nonEmpty, Is.GreaterThan(4), "the layout: most caps have area");
                Draw(display, Oblique());
            }
        }

        // ----- a cap of seven vertices -------------------------------------------------------------------------------

        /// <summary>
        /// The cube cut through its centre along its diagonal gives a hexagonal section; a second cut on the fixed side
        /// takes one corner off it, so that side's cap of the first cut has seven vertices. It is uploaded and drawn: its
        /// record holds seven vertices, the camera's colour ranges carry its fan, and the pixels inside it are capped,
        /// with the corner capped by the other side's triangle. A camera between the fixed part and the moved one looks
        /// into the opening.
        /// </summary>
        [Test]
        public void ASevenVertexCap_IsUploadedAndDrawn()
        {
            using (Scene scene = HexagonScene(out Vector3 n))
            {
                VpLogicalCutDisplay display = scene.display;
                int seven = -1;
                for (int i = 0; i < display.CapRecordCount; i++)
                {
                    display.TryGetCapRecord(i, out LogicalCutCapRecord cap);
                    seven = cap.vertexCount == 7 ? i : seven;
                }

                Assert.That(seven, Is.Not.EqualTo(-1), "a cap of seven vertices");
                Camera camera = Looking(n * 1.5f, -n, 1.6f);
                Color32[] image = Draw(display, camera);
                Assert.That(display.Classification.TryGetCap(seven, out VpMultiCutStencilCap classified), Is.True);
                Assert.That(classified.issued, Is.True, "it is issued");
                Assert.That(display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _), Is.True);
                int fanned = 0;
                for (int c = 0; c < preparation.colours; c++)
                {
                    Assert.That(display.TryGetPreparedColor(camera, c, out VpStencilCapColor colour), Is.True);
                    fanned += colour.capIndexCount;
                }

                Assert.That(fanned, Is.GreaterThanOrEqualTo(3 * 5), "its five triangles are among the uploaded indices");

                // Its render fragment's other cap, on B, faces away from this eye: not seen and not drawn, while the
                // render fragment's volume is issued once for the cap that is seen.
                display.TryGetCapRecord(seven, out LogicalCutCapRecord sevenRecord);
                display.TryGetRenderFragment(sevenRecord.renderFragment, out VpMultiCutRenderFragment sevenFragment);
                Assert.That(sevenFragment.capCount, Is.EqualTo(2));
                int unseen = 0;
                for (int c = sevenFragment.capStart; c < sevenFragment.capStart + sevenFragment.capCount; c++)
                {
                    display.Classification.TryGetCap(c, out VpMultiCutStencilCap other);
                    if (!other.visible)
                    {
                        unseen++;
                        Assert.That(other.issued, Is.False, "an unseen cap of a kept group is not drawn");
                    }
                }

                Assert.That(unseen, Is.EqualTo(1), "the layout: B's cap faces away");
                display.Classification.TryGetRenderFragment(sevenRecord.renderFragment, out VpMultiCutStencilRenderFragment kept);
                Assert.That(kept.volumeIssued, Is.True, "its volume is issued");
                Assert.That(kept.capsComplete, Is.False, "and its caps are not complete for the projection test");
                Assert.That(IsRed(At(image, camera, new Vector3(0f, 0f, 0f))), Is.True, "capped at the centre");
                Assert.That(IsRed(At(image, camera, new Vector3(0.6f, -0.6f, 0f))), Is.True, "capped near the cut corner");
                Assert.That(IsRed(At(image, camera, new Vector3(-0.5f, 0.5f, 0f))), Is.True, "capped across the hexagon");
                Assert.That(IsRed(At(image, camera, new Vector3(0.83f, -0.83f, 0f))), Is.True, "the corner, by the other side");
                Assert.That(IsRed(At(image, camera, new Vector3(0.65f, 0.65f, -1.3f))), Is.False, "nothing capped outside the section");
            }
        }

        // ----- nine cuts ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Nine cuts down one lineage: L9+ and L9- are drawn once, as L8+ whole under its eight selected boundaries; the
        /// ninth boundary makes no clip, offset, volume or cap, and the siblings L1- .. L8- are each drawn once. The
        /// aggregate keeps a cap for every one of its eight boundaries -- its ancestors' opening caps -- and every
        /// branch's candidates and their states are readable.
        /// </summary>
        [Test]
        public void NineCuts_DrawTheShapeBeforeTheNinthOnce_WithItsAncestorsCaps()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                LogicalFragmentId at = root;
                LogicalFragmentId l8Plus = default;
                CutOperationId ninth = default;
                for (int k = 0; k < 9; k++)
                {
                    var cut = Cut(ledger, at, Tilted(k));
                    l8Plus = k == 7 ? cut.positive : l8Plus;
                    ninth = cut.cut;
                    at = cut.positive;
                }

                Collect(scene);
                Assert.That(display.IsHalted, Is.False);
                Assert.That(display.BranchCount, Is.EqualTo(10), "L1- .. L9-, and L9+");
                Assert.That(display.RenderFragmentCount, Is.EqualTo(9), "L1- .. L8-, and L8+ once");
                Assert.That(display.SideCount, Is.EqualTo(9), "each drawn once: no sibling drawn twice");
                Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(9), "one display instance per render fragment");
                Assert.That(display.CapPolygonBuilds, Is.EqualTo(8), "eight faces capped; the Ignored one takes no section");

                int aggregated = 0;
                for (int r = 0; r < display.RenderFragmentCount; r++)
                {
                    display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                    if (!rf.aggregated)
                    {
                        continue;
                    }

                    aggregated++;
                    Assert.That(rf.root, Is.EqualTo(l8Plus), "drawn as L8+");
                    Assert.That(rf.branchCount, Is.EqualTo(2), "for L9+ and L9-");
                    Assert.That(rf.conditionCount, Is.EqualTo(8));
                    Assert.That(rf.capCount, Is.EqualTo(8), "every selected boundary keeps its cap");
                    Assert.That(rf.clip.PlaneCount, Is.EqualTo(8));
                }

                Assert.That(aggregated, Is.EqualTo(1));
                for (int i = 0; i < display.CapRecordCount; i++)
                {
                    display.TryGetCapRecord(i, out LogicalCutCapRecord cap);
                    Assert.That(cap.operation, Is.Not.EqualTo(ninth), "no cap of the Ignored boundary");
                }

                for (int i = 0; i < display.SideCount; i++)
                {
                    display.TryGetSide(i, out LogicalCutDisplaySide side);
                    Assert.That(side.operation, Is.Not.EqualTo(ninth), "no side of the Ignored boundary");
                }

                int ignored = 0;
                for (int b = 0; b < display.BranchCount; b++)
                {
                    Assert.That(display.TryGetBranch(b, out VpMultiCutBranch branch), Is.True);
                    for (int c = 0; c < branch.candidateCount; c++)
                    {
                        Assert.That(display.TryGetCandidate(branch.candidateStart + c, out VpClipCandidate candidate, out VpClipSelectionState state), Is.True);
                        if (state != VpClipSelectionState.Selected)
                        {
                            ignored++;
                            Assert.That(candidate.boundary.face.operation, Is.EqualTo(ninth));
                            Assert.That(state, Is.EqualTo(VpClipSelectionState.IgnoredCapacity));
                        }
                    }
                }

                Assert.That(ignored, Is.EqualTo(2), "the ninth boundary, once on each of its sides' branches");
                Draw(display, Oblique());
            }
        }

        // ----- several registrations -----------------------------------------------------------------------------------

        /// <summary>
        /// Two registrations of one ledger whose openings overlap on screen: they are classified together, their caps
        /// are apart (different faces), and so they take different ordinary colours; both openings and their overlap are
        /// capped, and nothing is capped outside them.
        /// </summary>
        [Test]
        public void OverlappingRegistrations_TakeDifferentColours()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                var below = new List<float3> { new float3(0f, -0.5f, 0f) };
                LogicalFragmentId first = ledger.AddFragment(below);
                LogicalFragmentId second = ledger.AddFragment(below);
                Assert.That(display.TryShow(first, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(display.TryShow(second, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(1f, 0f, 0.5f))), Is.True);
                Admit(ledger, first, new float4(0f, 1f, 0f, 0f));
                Admit(ledger, second, new float4(0f, 1f, 0f, 0f));
                Collect(scene);

                Camera camera = Looking(new Vector3(0.5f, 1.5f, 0.25f), Vector3.down, 2f);
                Color32[] image = Draw(display, camera);
                VpMultiCutStencilClassification classification = display.Classification;
                Assert.That(classification.TargetCount, Is.EqualTo(4), "every registration's render fragments, together");
                int firstBottom = RenderFragmentOf(display, first, -1f);
                int secondBottom = RenderFragmentOf(display, second, -1f);
                classification.TryGetRenderFragment(firstBottom, out VpMultiCutStencilRenderFragment a);
                classification.TryGetRenderFragment(secondBottom, out VpMultiCutStencilRenderFragment b);
                Assert.That(a.volumeIssued && b.volumeIssued, Is.True, "both bottoms are seen");
                Assert.That(a.colour, Is.Not.EqualTo(b.colour), "overlapping and apart: different colours");
                Assert.That(Math.Max(a.colour, b.colour), Is.LessThan(display.StencilSettings.maxStencilColors - 1), "both ordinary");
                Assert.That(IsRed(At(image, camera, new Vector3(-0.5f, 0f, -0.5f))), Is.True, "the first alone");
                Assert.That(IsRed(At(image, camera, new Vector3(1.5f, 0f, 1.2f))), Is.True, "the second alone");
                Assert.That(IsRed(At(image, camera, new Vector3(0.5f, 0f, 0.25f))), Is.True, "the overlap");
                Assert.That(IsRed(At(image, camera, new Vector3(-0.5f, 0f, 1.3f))), Is.False, "outside both");
            }
        }

        /// <summary>
        /// Two cameras on the seven-vertex scene, both prepared and both registered before either is rendered: the wide
        /// one draws both caps of the fixed part, the narrow one only the seven-vertex cap it sees, and each image is its
        /// own -- neither camera's arrangement is drawn with the other's.
        /// </summary>
        [Test]
        public void TwoCamerasRegisteredThenDrawn_KeepTheirOwnArrangements()
        {
            using (Scene scene = HexagonScene(out Vector3 n))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera wide = Looking(n * 1.5f, -n, 1.6f);
                Vector3 narrowCentre = new Vector3(-0.5f, 0.5f, 0f);
                Camera narrow = Looking(narrowCentre + (n * 1.5f), -n, 0.2f);
                Assert.That(display.TryRegisterCamera(wide), Is.True);
                Assert.That(display.TryRegisterCamera(narrow), Is.True);
                Assert.That(display.TryPrepareCamera(wide), Is.True);
                Assert.That(display.TryPrepareCamera(narrow), Is.True);
                display.Render(0, wide);
                display.Render(0, narrow);
                Color32[] wideImage = Read(wide);
                Color32[] narrowImage = Read(narrow);

                display.TryGetCameraStencil(wide, out VpStencilPreparation widePreparation, out VpStencilCameraCounts wideCounts);
                display.TryGetCameraStencil(narrow, out VpStencilPreparation narrowPreparation, out VpStencilCameraCounts narrowCounts);
                Assert.That(narrowPreparation.capsDrawn, Is.LessThan(widePreparation.capsDrawn), "the narrow one sees fewer caps");
                Assert.That(wideCounts.uploads, Is.EqualTo(1));
                Assert.That(narrowCounts.uploads, Is.EqualTo(1));
                Assert.That(IsRed(At(wideImage, wide, new Vector3(0.83f, -0.83f, 0f))), Is.True, "the wide camera caps the corner");
                Assert.That(IsRed(At(wideImage, wide, Vector3.zero)), Is.True, "and the centre");
                Assert.That(IsRed(At(narrowImage, narrow, narrowCentre)), Is.True, "the narrow camera caps what it sees");
            }
        }

        // ----- capacity ------------------------------------------------------------------------------------------------

        /// <summary>
        /// A collection short of room -- draw instances, logical branches, or display instance references -- adopts
        /// nothing: the display keeps drawing its previous snapshot whole, nothing is uploaded, and no reference is left
        /// taken. It is not a stop. With room again the same display adopts, and when fewer render fragments are drawn the
        /// references no longer needed are given back after adoption, each once.
        /// </summary>
        [Test]
        public void AShortfallOfRoom_AdoptsNothing_AndReferencesComeAndGoWithTheRenderFragments()
        {
            var cases = new (string what, int instances, int branches, int references)[]
            {
                ("draw instances", 3, VpDisplayTestCapacities.Branches, 32),
                ("logical branches", 16, 3, 32),
                ("display instance references", 16, VpDisplayTestCapacities.Branches, 3),
            };

            foreach ((string what, int instances, int branches, int references) in cases)
            {
                using (Scene scene = NewScene(instances: instances, branches: branches, tableInstances: references))
                {
                    LogicalCutLedger ledger = scene.ledger;
                    VpLogicalCutDisplay display = scene.display;
                    LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                    Assert.That(display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True, what);
                    var (_, plus, minus) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
                    Collect(scene);
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(2), what + ": two render fragments, two references");

                    // Both children cut at once: four render fragments, four branches, four references wanted.
                    CutOperationId b = Admit(ledger, plus, new float4(1f, 0f, 0f, 0f));
                    CutOperationId c = Admit(ledger, minus, new float4(0f, 0f, 1f, 0f));
                    Drawn before = Capture(display);
                    int uploads = display.CommandUploads;
                    int collections = display.SettledCollections;
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.False, what + ": refused");
                    Assert.That(display.IsHalted, Is.False, what + ": a shortfall is not a stop");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), what + ": nothing uploaded");
                    Assert.That(display.SettledCollections, Is.EqualTo(collections));
                    AssertSameShape(before, Capture(display), what + ": the previous snapshot, whole");
                    AssertSameOwners(before, Capture(display), what);
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(2), what + ": no reference left taken");
                    Draw(display, Oblique());

                    // One of them reclaimed as stale: three render fragments fit, and three references are held.
                    ledger.NoteOwnershipChanged(minus);
                    Assert.That(ledger.Publish(c, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
                    Collect(scene);
                    Assert.That(display.RenderFragmentCount, Is.EqualTo(3), what);
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(3), what + ": one more reference, taken");

                    // The other too: two render fragments again, and the third reference is given back after adoption.
                    ledger.NoteOwnershipChanged(plus);
                    Assert.That(ledger.Publish(b, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
                    Collect(scene);
                    Assert.That(display.RenderFragmentCount, Is.EqualTo(2), what);
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(2), what + ": given back once");
                    Draw(display, Oblique());
                }

                // Every reference came back once, the geometry registration included.
            }
        }

        // ----- a retired fragment inside an aggregate ------------------------------------------------------------------

        /// <summary>
        /// A retired fragment inside what is drawn once stops the display before the frame draws anything: the collection
        /// answers false, the reason is kept, preparing and drawing throw, a new body and a camera are refused, and it
        /// stays stopped in the next frame. It is decided before any room is taken: a display too small for the lineage
        /// -- whose collection is otherwise only refused for room -- stops for the same reason. Disposing gives every
        /// reference back.
        /// </summary>
        [Test]
        public void ARetiredFragmentInsideAnAggregate_StopsTheDisplayBeforeItDraws()
        {
            using (Scene scene = NewScene())
            using (Scene small = NewScene(branches: 4, ledger: scene.ledger))
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(scene.display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);

                // The small display reads the same ledger.
                VpLogicalCutDisplay smallDisplay = small.display;
                VpGeometryReferenceTable smallTable = small.table;
                Assert.That(smallDisplay.TryShow(root, AppendCube(small.storage, false), Matrix4x4.identity), Is.True);

                LogicalFragmentId at = root;
                for (int k = 0; k < 9; k++)
                {
                    at = Cut(ledger, at, Tilted(k)).positive;
                }

                Collect(scene);
                Camera camera = Oblique();
                Draw(scene.display, camera);
                Assert.That(smallDisplay.TryBeginFrame(), Is.False, "the layout: too small, only refused");
                Assert.That(smallDisplay.IsHalted, Is.False);

                CutOperationId tenth = Admit(ledger, at, new float4(0f, 0f, 1f, 0f));
                Assert.That(ledger.Abort(tenth), Is.EqualTo(LogicalCutResultOutcome.Applied), "L9+ retired");

                _frame++;
                VpLogicalCutDisplay display = scene.display;
                int init = display.StencilInitIssues;
                int shadows = display.OneSidedShadowIssues + display.TwoSidedShadowIssues;
                int uploads = display.StencilUploads;
                Assert.That(display.TryBeginFrame(), Is.False);
                Assert.That(display.IsHalted, Is.True);
                Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RetiredInsideAggregate));
                Assert.Throws<InvalidOperationException>(() => display.TryPrepareCamera(camera), "no preparation");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera), "no draw");
                Assert.That(display.TryGetCameraStencil(camera, out _, out VpStencilCameraCounts counts), Is.True);
                Assert.That(counts.preparedNow, Is.False, "the camera is not prepared for this frame");
                Assert.That(display.StencilUploads, Is.EqualTo(uploads), "nothing uploaded");
                Assert.That(display.StencilInitIssues, Is.EqualTo(init), "and nothing of this frame registered");
                Assert.That(display.OneSidedShadowIssues + display.TwoSidedShadowIssues, Is.EqualTo(shadows));
                Assert.That(display.TryShow(ledger.AddFragment(), AppendCube(scene.storage, false), Matrix4x4.identity), Is.False, "no new body");
                Assert.That(display.TryRegisterCamera(Oblique()), Is.False, "no new camera");

                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "still stopped");
                Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RetiredInsideAggregate), "the first reason, kept");
                Assert.Throws<InvalidOperationException>(() => display.TryPrepareCamera(camera));

                Assert.That(smallDisplay.TryBeginFrame(), Is.False);
                Assert.That(smallDisplay.IsHalted, Is.True, "no shortfall of room hides it");
                Assert.That(smallDisplay.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RetiredInsideAggregate));

                _frame++;
                display.Dispose();
                smallDisplay.Dispose();
                Assert.That(scene.table.LiveDisplayInstanceCount, Is.Zero, "every reference back");
                Assert.That(scene.table.LiveGeometryCount, Is.Zero, "and the registration");
                Assert.That(smallTable.LiveDisplayInstanceCount, Is.Zero);
                Assert.That(smallTable.LiveGeometryCount, Is.Zero);
            }
        }

        // ----- a retirement the previous snapshot still draws -------------------------------------------------------

        /// <summary>
        /// Two registrations, A and B, of one ledger. A fragment A draws -- a branch below A's root, or A's root itself --
        /// is retired where nothing is aggregated, while B is cut further than the room allows, so the new snapshot cannot
        /// be adopted. The previous snapshot, which would keep drawing, draws the retired fragment: the display stops before
        /// the frame draws, for a reason of its own, and adopts nothing in part. Without the retirement the same shortfall
        /// is only refused and the previous snapshot keeps drawing. Every reference comes back on Dispose, once.
        /// </summary>
        [Test]
        public void ARetirementOutsideAnAggregate_WithAShortfallElsewhere_StopsTheDisplay()
        {
            var cases = new (string what, int instances, int branches, bool rootWhole)[]
            {
                ("draw instances short, a branch of A retired", 3, VpDisplayTestCapacities.Branches, false),
                ("logical branches short, a branch of A retired", 16, 3, false),
                ("draw instances short, A's root retired", 3, VpDisplayTestCapacities.Branches, true),
            };

            foreach ((string what, int instances, int branches, bool rootWhole) in cases)
            {
                foreach (bool retire in new[] { true, false })
                {
                    string label = what + (retire ? "" : " -- control, nothing retired");
                    using (Scene scene = NewScene(instances: instances, branches: branches))
                    {
                        LogicalCutLedger ledger = scene.ledger;
                        VpLogicalCutDisplay display = scene.display;
                        LogicalFragmentId a = ledger.AddFragment();
                        LogicalFragmentId b = ledger.AddFragment();
                        Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True, label);
                        Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(3f, 0f, 0f))), Is.True, label);
                        LogicalFragmentId drawn = a;
                        if (!rootWhole)
                        {
                            drawn = Cut(ledger, a, new float4(0f, 1f, 0f, 0f)).positive;
                        }

                        Collect(scene);
                        Camera camera = Oblique();
                        Draw(display, camera);
                        int references = scene.table.LiveDisplayInstanceCount;

                        if (retire)
                        {
                            CutOperationId aborted = Admit(ledger, drawn, new float4(1f, 0f, 0f, 0f));
                            Assert.That(ledger.Abort(aborted), Is.EqualTo(LogicalCutResultOutcome.Applied), label + ": the layout");
                        }

                        // B becomes four render fragments and four branches: more than any case has room for.
                        var (_, bPlus, bMinus) = Cut(ledger, b, new float4(0f, 1f, 0f, 0f));
                        Admit(ledger, bPlus, new float4(1f, 0f, 0f, 0f));
                        Admit(ledger, bMinus, new float4(0f, 0f, 1f, 0f));

                        int uploads = display.CommandUploads;
                        int stencilUploads = display.StencilUploads;
                        int init = display.StencilInitIssues;
                        _frame++;
                        Assert.That(display.TryBeginFrame(), Is.False, label + ": refused");
                        Assert.That(display.CommandUploads, Is.EqualTo(uploads), label + ": nothing uploaded");
                        Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(references), label + ": no reference left taken");
                        if (!retire)
                        {
                            Assert.That(display.IsHalted, Is.False, label + ": a shortfall alone is not a stop");
                            Draw(display, camera);
                            continue;
                        }

                        Assert.That(display.IsHalted, Is.True, label + ": stopped");
                        Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RetiredWhileShown), label);
                        Assert.Throws<InvalidOperationException>(() => display.TryPrepareCamera(camera), label + ": no preparation");
                        Assert.Throws<InvalidOperationException>(() => display.Render(0, camera), label + ": no draw");
                        Assert.That(display.StencilUploads, Is.EqualTo(stencilUploads), label);
                        Assert.That(display.StencilInitIssues, Is.EqualTo(init), label + ": nothing of this frame registered");

                        _frame++;
                        Assert.That(display.TryBeginFrame(), Is.False, label + ": still stopped");
                        Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RetiredWhileShown), label + ": the first reason");
                        display.Dispose();
                        Assert.That(scene.table.LiveDisplayInstanceCount, Is.Zero, label + ": every reference back");
                        Assert.That(scene.table.LiveGeometryCount, Is.Zero, label + ": and every registration");
                    }
                }
            }
        }

        // ----- the conservative numeric check ---------------------------------------------------------------------------

        /// <summary>
        /// Nine cuts up one lineage, every side free. At a separation of 3e37 everything is finite: the display with room
        /// adopts, and the one short of branches is only refused and keeps drawing. At 4e37 the ninth separation is past a
        /// float although L9+ is drawn only inside the aggregate L8+, whose offset is finite: both displays -- with room and
        /// without -- stop before the frame draws, as invalid input found by the conservative check, not as a drawn value.
        /// </summary>
        [Test]
        public void TheConservativeNumericCheck_StopsTheDisplay_WhateverTheRoom()
        {
            using (Scene roomy = NewScene())
            using (Scene small = NewScene(branches: 2, ledger: roomy.ledger))
            {
                LogicalCutLedger ledger = roomy.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(roomy.display.TryShow(root, AppendCube(roomy.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(small.display.TryShow(root, AppendCube(small.storage, false), Matrix4x4.identity), Is.True);
                Collect(roomy);
                Assert.That(small.display.TryBeginFrame(), Is.True, "the layout: one branch fits");
                Camera camera = Oblique();
                Draw(small.display, camera);

                LogicalFragmentId at = root;
                for (int k = 0; k < 9; k++)
                {
                    at = Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.15f * k))).positive;
                }

                roomy.display.Separation = 3e37f;
                small.display.Separation = 3e37f;
                Collect(roomy);
                Assert.That(small.display.TryBeginFrame(), Is.False, "finite, but short of branches");
                Assert.That(small.display.IsHalted, Is.False, "a shortfall alone");
                Draw(small.display, camera);

                roomy.display.Separation = 4e37f;
                small.display.Separation = 4e37f;
                _frame++;
                foreach ((VpLogicalCutDisplay display, string what) in new[] { (roomy.display, "with room"), (small.display, "short of room") })
                {
                    Assert.That(display.TryBeginFrame(), Is.False, what);
                    Assert.That(display.IsHalted, Is.True, what + ": stopped");
                    Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.InvalidInput), what);
                    Assert.That(display.HaltInvalidInput, Is.EqualTo(VpMultiCutInvalidInput.ConservativeOffset), what + ": the check, not a drawn value");
                    Assert.Throws<InvalidOperationException>(() => display.TryPrepareCamera(camera), what + ": no preparation");
                }
            }
        }

        /// <summary>
        /// A body whose box, placement and the epsilon it would be built with fail the section bounds is refused where it
        /// is taken in -- a box 1e26 wide, whose derived epsilon squares past a float, and a cube placed 1e38 away -- and
        /// nothing changes: no body, transfer, registration or reference is taken, and the body already shown is drawn as
        /// it was.
        /// </summary>
        [Test]
        public void ARegistrationOutsideTheSectionBounds_IsRefused_AndChangesNothing()
        {
            using (Scene scene = NewScene())
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId shown = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(shown, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Admit(ledger, shown, new float4(0f, 1f, 0f, 0f));
                Collect(scene);
                Camera camera = Oblique();
                Draw(display, camera);
                Drawn before = Capture(display);
                int bodies = display.ShownCount;
                int geometries = scene.table.LiveGeometryCount;
                int references = scene.table.LiveDisplayInstanceCount;
                int vertexTransfers = display.VertexTransfers;
                int indexTransfers = display.IndexTransfers;

                _frame++;
                VpStoredGeometry wide = AppendBox(scene.storage, new float3(-5e25f, -5e25f, -5e25f), new float3(5e25f, 5e25f, 5e25f));
                Assert.That(display.TryShow(ledger.AddFragment(), wide, Matrix4x4.identity), Is.False, "a box whose epsilon squares past a float");
                Assert.That(
                    display.TryShow(ledger.AddFragment(), AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(1e38f, 0f, 0f))),
                    Is.False, "a cube placed 1e38 away");

                Assert.That(display.ShownCount, Is.EqualTo(bodies), "no body taken");
                Assert.That(scene.table.LiveGeometryCount, Is.EqualTo(geometries), "no registration");
                Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(references), "no reference");
                Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "nothing transferred");
                Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers));
                Assert.That(display.TryBeginFrame(), Is.True, "the display goes on");
                Assert.That(display.IsHalted, Is.False);
                AssertSameShape(before, Capture(display), "the body shown is drawn as it was");
                Draw(display, camera);
            }
        }

        // ----- the body's upload ------------------------------------------------------------------------------------------

        /// <summary>
        /// The body's upload refused after its own check with the same counts passed -- reached through the test hook,
        /// since no input can -- is not a shortfall: the display is broken, nothing is adopted, and the collection throws
        /// rather than answering false. The same for an upload that throws. The reference taken for the new render
        /// fragment stays with its registration until Dispose, which gives every reference back once.
        /// </summary>
        [Test]
        public void ABodyUploadRefusedAfterItsCheck_BreaksTheDisplay_AndDisposeGivesEveryReferenceBack()
        {
            foreach (bool throws in new[] { false, true })
            {
                string label = throws ? "an upload that throws" : "an upload refused";
                using (Scene scene = NewScene())
                {
                    LogicalCutLedger ledger = scene.ledger;
                    VpLogicalCutDisplay display = scene.display;
                    LogicalFragmentId root = ledger.AddFragment();
                    Assert.That(display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                    Collect(scene);
                    Camera camera = Oblique();
                    Draw(display, camera);
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(1));
                    int uploads = display.CommandUploads;

                    Admit(ledger, root, new float4(0f, 1f, 0f, 0f));
                    bool asked = false;
                    display.RefuseBodyUploadForTest = () =>
                    {
                        asked = true;
                        if (throws)
                        {
                            throw new TimeoutException("a GPU call that threw");
                        }

                        return true;
                    };
                    _frame++;
                    if (throws)
                    {
                        Assert.Throws<TimeoutException>(() => display.TryBeginFrame(), label);
                    }
                    else
                    {
                        Assert.Throws<InvalidOperationException>(() => display.TryBeginFrame(), label);
                    }

                    Assert.That(asked, Is.True, label + ": the layout: every check before it passed");
                    Assert.That(display.IsBroken, Is.True, label + ": stopped as broken");
                    Assert.That(display.IsHalted, Is.False, label + ": not a halt for a reason of the snapshot");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), label + ": nothing adopted");
                    Assert.That(display.RenderFragmentCount, Is.EqualTo(1), label + ": the snapshot is the earlier one");
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(2), label + ": the reference taken stays held");
                    Assert.Throws<InvalidOperationException>(() => display.TryBeginFrame(), label + ": no more collections");
                    Assert.Throws<InvalidOperationException>(() => display.TryPrepareCamera(camera), label + ": nor preparations");

                    _frame++;
                    display.Dispose();
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.Zero, label + ": every reference back, once");
                    Assert.That(scene.table.LiveGeometryCount, Is.Zero, label);
                }
            }
        }

        // ----- registration ---------------------------------------------------------------------------------------------

        /// <summary>
        /// What the geometry reflects is the caller's statement. A child registered with a geometry that really is its
        /// half of the cube, stated to reflect its boundary, is drawn under no condition; the same child registered with
        /// the whole cube and nothing reflected -- the older spelling -- is clipped by that boundary and capped. This is a
        /// test arrangement of two statements, not a geometry commit. Null is refused, as is a mapping that is not rigid,
        /// and a fragment on the lineage of one already shown.
        /// </summary>
        [Test]
        public void WhatTheGeometryReflects_IsTheCallersStatement()
        {
            using (Scene stated = NewScene())
            using (Scene whole = NewScene(ledger: stated.ledger))
            {
                LogicalCutLedger ledger = stated.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                var (a, plus, _) = Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
                VpLogicalCutDisplay reflecting = stated.display;
                VpLogicalCutDisplay older = whole.display;

                VpStoredGeometry upperHalf = AppendBox(stated.storage, new float3(-1f, 0f, -1f), new float3(1f, 1f, 1f));
                Assert.Throws<ArgumentNullException>(
                    () => reflecting.TryShow(plus, upperHalf, Matrix4x4.identity, Matrix4x4.identity, null));
                Assert.That(
                    reflecting.TryShow(plus, upperHalf, Matrix4x4.identity, Matrix4x4.Scale(Vector3.one * 2f), new VpClipBoundary[0]),
                    Is.False, "a scaled mapping");
                Assert.That(
                    reflecting.TryShow(plus, upperHalf, Matrix4x4.identity, Matrix4x4.identity, new[] { new VpClipBoundary(new VpCapFace(ledger, a), 1f) }),
                    Is.True);
                Assert.That(older.TryShow(plus, AppendCube(whole.storage, false), Matrix4x4.identity), Is.True);
                Collect(stated);
                Collect(whole);

                Assert.That(reflecting.RenderFragmentCount, Is.EqualTo(1));
                reflecting.TryGetRenderFragment(0, out VpMultiCutRenderFragment reflected);
                Assert.That(reflected.conditionCount, Is.Zero, "the stated boundary is not a candidate");
                Assert.That(reflected.clip.PlaneCount, Is.Zero);
                Assert.That(reflecting.CapRecordCount, Is.Zero);

                older.TryGetRenderFragment(0, out VpMultiCutRenderFragment clipped);
                Assert.That(clipped.conditionCount, Is.EqualTo(1), "nothing stated: the boundary above is a candidate");
                Assert.That(clipped.clip.PlaneCount, Is.EqualTo(1));
                Assert.That(older.CapRecordCount, Is.EqualTo(1));

                // A descendant of a fragment shown is refused, by either spelling.
                var (_, plusPlus, _) = Cut(ledger, plus, new float4(1f, 0f, 0f, 0f));
                Assert.That(reflecting.TryShow(plusPlus, AppendCube(stated.storage, false), Matrix4x4.identity), Is.False, "below one shown");
                Assert.That(
                    reflecting.TryShow(plusPlus, AppendCube(stated.storage, false), Matrix4x4.identity, Matrix4x4.identity, new VpClipBoundary[0]),
                    Is.False, "below one shown, the explicit spelling");
            }
        }

        // ----- helpers --------------------------------------------------------------------------------------------------

        private sealed class Scene : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public LogicalCutLedger ledger;
            public VpGeometryReferenceTable table;
            public VpLogicalCutDisplay display;
            public Action endFrame;

            public void Dispose()
            {
                endFrame?.Invoke();
                if (display != null && !display.IsDisposed)
                {
                    display.Dispose();
                }

                storage?.Dispose();
            }
        }

        /// <summary>
        /// A storage, its reference table and a display. A storage takes one reference table, so a second display --
        /// over the same <paramref name="ledger"/>, when one is given -- is a second scene with a storage of its own.
        /// </summary>
        private Scene NewScene(
            int instances = 16, int tableInstances = 32, int branches = VpDisplayTestCapacities.Branches, int colours = 4,
            LogicalCutLedger ledger = null)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
                ledger = ledger ?? new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                endFrame = () => _frame++,
            };
            scene.table = new VpGeometryReferenceTable(scene.storage, 16, tableInstances);
            Dictionary<int, Material> materials = Materials();
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    scene.storage, scene.table, scene.ledger, materials, null, null, 16, instances, branches,
                    VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth,
                    VpStencilTestSettings.Create(colours), () => _frame, out scene.display),
                Is.True, "create the display");
            scene.display.Separation = WideSeparation;
            return scene;
        }

        /// <summary>
        /// The cube cut through its centre by the diagonal plane n = (1, 1, 1) / sqrt 3, with both anchors on its
        /// negative side, so A- is fixed and A+ moved far along n; then A- cut by x - y = 1.5, one anchor on each side,
        /// so both of its parts stay. A-'s cap of the first cut, on B's negative side, is the hexagon less one corner.
        /// </summary>
        private Scene HexagonScene(out Vector3 n)
        {
            Scene scene = NewScene();
            LogicalCutLedger ledger = scene.ledger;
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0.9f, -0.9f, -0.5f), new float3(0f, 0f, -0.5f) });
            Assert.That(scene.display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
            float4 diagonal = Normalized(new float4(1f, 1f, 1f, 0f));
            var (_, _, minus) = Cut(ledger, root, diagonal);
            Admit(ledger, minus, Normalized(new float4(1f, -1f, 0f, -1.5f)));
            Collect(scene);
            Assert.That(ledger.IsFixedOwner(minus), Is.True, "the layout: A- is fixed");
            n = new Vector3(diagonal.x, diagonal.y, diagonal.z);
            return scene;
        }

        private void Collect(Scene scene)
        {
            _frame++;
            Assert.That(scene.display.TryBeginFrame(), Is.True, "collected");
        }

        private static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId source, float4 plane)
        {
            Assert.That(ledger.Admit(source, plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return cut;
        }

        private static (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) Cut(
            LogicalCutLedger ledger, LogicalFragmentId source, float4 plane)
        {
            CutOperationId cut = Admit(ledger, source, plane);
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (cut, positive, negative);
        }

        /// <summary>The k-th of nine planes that climb the cube and turn a little each time, so no two are parallel.</summary>
        private static float4 Tilted(int k)
        {
            float angle = k * 0.35f;
            return Normalized(new float4(0.2f * Mathf.Cos(angle), 1f, 0.2f * Mathf.Sin(angle), 0.8f - (0.15f * k)));
        }

        private static float4 Normalized(float4 plane)
        {
            float length = math.length(plane.xyz);
            return plane / length;
        }

        private static float Evaluate(Vector4 plane, Vector3 point)
        {
            return (plane.x * point.x) + (plane.y * point.y) + (plane.z * point.z) + plane.w;
        }

        private static int RenderFragmentOf(VpLogicalCutDisplay display, LogicalFragmentId source, float side)
        {
            for (int i = 0; i < display.SideCount; i++)
            {
                display.TryGetSide(i, out LogicalCutDisplaySide s);
                if (s.source == source && s.side == side)
                {
                    return s.renderFragment;
                }
            }

            Assert.Fail("no render fragment of that side");
            return -1;
        }

        private static void AssertCommandsDrawEveryRenderFragment(VpLogicalCutDisplay display, int renderFragments)
        {
            for (int c = 0; c < display.DrawCommandCount; c++)
            {
                Assert.That(display.TryGetDrawCommand(c, out VpIndirectCommand command), Is.True);
                Assert.That(command.instanceCount, Is.EqualTo(renderFragments), "command " + c);
            }
        }

        private static void AssertOffset(VpLogicalCutDisplay display, int renderFragment, Vector3 expected, string what)
        {
            display.TryGetRenderFragment(renderFragment, out VpMultiCutRenderFragment rf);
            Assert.That((rf.offset - expected).magnitude, Is.LessThan(Tolerance), what + ": " + rf.offset + " against " + expected);
        }

        private static void AssertSidesName(VpLogicalCutDisplay display, LogicalFragmentId plus, LogicalFragmentId minus)
        {
            for (int i = 0; i < display.SideCount; i++)
            {
                display.TryGetSide(i, out LogicalCutDisplaySide side);
                Assert.That(side.published, Is.True);
                Assert.That(side.fragment, Is.EqualTo(side.side > 0f ? plus : minus));
            }
        }

        /// <summary>Everything the display publishes about what it draws, copied.</summary>
        private sealed class Drawn
        {
            public readonly List<LogicalCutDisplaySide> sides = new List<LogicalCutDisplaySide>();
            public readonly List<LogicalCutCapRecord> caps = new List<LogicalCutCapRecord>();
            public readonly List<Vector3[]> vertices = new List<Vector3[]>();
            public readonly List<VpIndirectCommand> commands = new List<VpIndirectCommand>();
        }

        private static Drawn Capture(VpLogicalCutDisplay display)
        {
            var drawn = new Drawn();
            for (int i = 0; i < display.SideCount; i++)
            {
                display.TryGetSide(i, out LogicalCutDisplaySide side);
                drawn.sides.Add(side);
            }

            for (int i = 0; i < display.CapRecordCount; i++)
            {
                display.TryGetCapRecord(i, out LogicalCutCapRecord cap);
                drawn.caps.Add(cap);
                var polygon = new Vector3[cap.vertexCount];
                for (int v = 0; v < polygon.Length; v++)
                {
                    display.TryGetCapVertex(i, v, out polygon[v]);
                }

                drawn.vertices.Add(polygon);
            }

            for (int c = 0; c < display.DrawCommandCount; c++)
            {
                display.TryGetDrawCommand(c, out VpIndirectCommand command);
                drawn.commands.Add(command);
            }

            return drawn;
        }

        /// <summary>The same drawing, exactly: who the sides and caps belong to is not compared.</summary>
        private static void AssertSameShape(Drawn a, Drawn b, string what)
        {
            Assert.That(b.commands.Count, Is.EqualTo(a.commands.Count), what + ": commands");
            for (int c = 0; c < a.commands.Count; c++)
            {
                Assert.That(b.commands[c].instanceCount, Is.EqualTo(a.commands[c].instanceCount), what + ": command " + c);
                Assert.That(b.commands[c].range.indexStart, Is.EqualTo(a.commands[c].range.indexStart));
            }

            Assert.That(b.sides.Count, Is.EqualTo(a.sides.Count), what + ": sides");
            for (int i = 0; i < a.sides.Count; i++)
            {
                LogicalCutDisplaySide x = a.sides[i];
                LogicalCutDisplaySide y = b.sides[i];
                Assert.That(y.renderFragment, Is.EqualTo(x.renderFragment), what + ": side " + i);
                Assert.That(y.operation, Is.EqualTo(x.operation), what + ": side " + i + " operation");
                Assert.That(y.side, Is.EqualTo(x.side));
                Assert.That(y.fixedByAnchors, Is.EqualTo(x.fixedByAnchors));
                Assert.That(Same(y.offset, x.offset), Is.True, what + ": side " + i + " offset " + x.offset + " then " + y.offset);
                Assert.That(y.clip.PlaneCount, Is.EqualTo(x.clip.PlaneCount), what + ": side " + i + " planes");
                for (int p = 0; p < x.clip.PlaneCount; p++)
                {
                    Assert.That(Same(y.clip.SignedPlane(p), x.clip.SignedPlane(p)), Is.True, what + ": side " + i + " plane " + p);
                }
            }

            Assert.That(b.caps.Count, Is.EqualTo(a.caps.Count), what + ": caps");
            for (int i = 0; i < a.caps.Count; i++)
            {
                LogicalCutCapRecord x = a.caps[i];
                LogicalCutCapRecord y = b.caps[i];
                Assert.That(y.renderFragment, Is.EqualTo(x.renderFragment), what + ": cap " + i);
                Assert.That(y.operation, Is.EqualTo(x.operation));
                Assert.That(y.side, Is.EqualTo(x.side));
                Assert.That(Same(y.offset, x.offset), Is.True, what + ": cap " + i + " offset");
                Assert.That(Same(y.worldPlane, x.worldPlane), Is.True, what + ": cap " + i + " plane");
                Assert.That(Same(y.outwardNormal, x.outwardNormal), Is.True, what + ": cap " + i + " normal");
                Assert.That(b.vertices[i].Length, Is.EqualTo(a.vertices[i].Length), what + ": cap " + i + " vertices");
                for (int v = 0; v < a.vertices[i].Length; v++)
                {
                    Assert.That(Same(b.vertices[i][v], a.vertices[i][v]), Is.True, what + ": cap " + i + " vertex " + v);
                }
            }
        }

        private static void AssertSameOwners(Drawn a, Drawn b, string what)
        {
            for (int i = 0; i < a.sides.Count; i++)
            {
                Assert.That(b.sides[i].published, Is.EqualTo(a.sides[i].published), what);
                Assert.That(b.sides[i].fragment, Is.EqualTo(a.sides[i].fragment), what);
                Assert.That(b.sides[i].source, Is.EqualTo(a.sides[i].source), what);
            }
        }

        private static bool Same(Vector3 a, Vector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }

        private static bool Same(Vector4 a, Vector4 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        private static VpStoredGeometry AppendCube(VpCpuGeometryStorage storage, bool twoSubmeshes)
        {
            return AppendBox(storage, new float3(-1f, -1f, -1f), new float3(1f, 1f, 1f), twoSubmeshes);
        }

        /// <summary>A box from <paramref name="min"/> to <paramref name="max"/>: its sides one submesh, and its ends another when asked.</summary>
        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage, float3 min, float3 max, bool twoSubmeshes = false)
        {
            var corners = new float3[8];
            for (int i = 0; i < 8; i++)
            {
                float3 unit = (k_cube[i] + 1f) * 0.5f;
                corners[i] = min + (unit * (max - min));
            }

            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            int submeshCount = twoSubmeshes ? 2 : 1;
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    int faceSubmesh = twoSubmeshes && f >= 4 ? 1 : 0;
                    if (faceSubmesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f];
                    // The face's normal from the unit cube: the same direction for any box, and no product of a huge box's
                    // edges to overflow.
                    float3 n = math.normalize(math.cross(k_cube[c[1]] - k_cube[c[0]], k_cube[c[2]] - k_cube[c[0]]));
                    uint b = (uint)vertices.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = corners[c[k]], normal = n, uv0 = new float2(0.5f, 0.5f) });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), 8, submeshes.ToArray(), out VpStoredGeometry geometry),
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

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private Camera NewCamera(bool orthographic)
        {
            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32)
            {
                depthStencilFormat = VpStencilAttachment.EightBitStencilFormat,
                antiAliasing = 1,
            });
            target.Create();
            Camera camera = Track(new GameObject("Multi-cut Display Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = orthographic;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            return camera;
        }

        /// <summary>An orthographic camera at <paramref name="position"/> looking along <paramref name="direction"/>.</summary>
        private Camera Looking(Vector3 position, Vector3 direction, float size)
        {
            Camera camera = NewCamera(true);
            camera.orthographicSize = size;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 10f;
            Vector3 up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, up));
            return camera;
        }

        /// <summary>A perspective camera that sees the whole scene from above and in front.</summary>
        private Camera Oblique()
        {
            Camera camera = NewCamera(false);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            var eye = new Vector3(2.5f, 5f, -6f);
            camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(new Vector3(0f, 1f, 0f) - eye, Vector3.up));
            return camera;
        }

        private Color32[] Draw(VpLogicalCutDisplay display, Camera camera)
        {
            display.TryRegisterCamera(camera);
            Assert.That(display.TryPrepareCamera(camera), Is.True, "prepared");
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

        private static Color32 At(Color32[] pixels, Camera camera, Vector3 world)
        {
            Vector3 viewport = camera.WorldToViewportPoint(world);
            int px = Mathf.Clamp(Mathf.RoundToInt(viewport.x * (Size - 1)), 0, Size - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt(viewport.y * (Size - 1)), 0, Size - 1);
            return pixels[py * Size + px];
        }

        private static bool IsRed(Color32 c)
        {
            return c.r > 180 && c.g < 80 && c.b < 80;
        }
    }
}
