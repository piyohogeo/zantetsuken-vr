using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The provisional caps drawn from the ledger's own state: <see cref="VpLogicalCutDisplay"/> connected to
    /// <see cref="VpStencilCapBatch"/>, single body, single cut, fixed transform, no XR, MSAA off. Every image is
    /// read back from an orthographic camera placed between the two halves once the free side has been moved far
    /// apart, looking along the cut normal at the fixed side's cap.
    /// <para>
    /// The body is a truncated pyramid, wider at the bottom than at the top, so that its real cross-section at the
    /// cut is smaller than the cross-section of its bounds box: the Cap Bounds Polygon overhangs the body, and the
    /// stencil is what has to keep that overhang from being drawn.
    /// </para>
    /// </summary>
    public class VpLogicalCutDisplayStencilTests
    {
        private const int ControlPoints = 8;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const float Epsilon = 0.01f;
        private const int Size = 128;

        // Bottom square [-1, 1]^2 at y = 0, top square [-0.5, 0.5]^2 at y = 2: at the cut y = 1 the body is
        // [-0.75, 0.75]^2 while its bounds box is [-1, 1]^2. Faces wound outward under the clockwise-front rule.
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
        private static readonly float3 k_highAnchor = new float3(0f, 1.8f, 0f);

        // Far enough that the moved side is out of the camera's way entirely.
        private const float WideSeparation = 3f;

        private readonly List<Object> _objects = new List<Object>();
        private int _frame;
        private Camera _between;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
            _between = null;
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

        // ---------------------------------------------------------------------------------------------------------

        [Test]
        public void TheParent_HasNoCap_AndThePreparedSplit_CapsTheFixedSide_OnlyInsideTheBody()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();

                // The low anchor fixes the negative (bottom) side; the top moves up and out of the way.
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);

                    // The whole parent: nothing is capped and the stencil batch holds no group.
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Color32[] whole = Draw(display, LookingDownFromBetween());
                    Assert.That(Count(whole, IsRed), Is.Zero, "the whole body has no cut and no cap");
                    Assert.That(PreparationOf(display, LookingDownFromBetween()).colours, Is.Zero, "no cap, no colour");

                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "one cap per side, not per submesh");
                    Assert.That(display.DrawCommandCount, Is.EqualTo(2), "one command per submesh, as before the split");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "the split transfers no vertices");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "and no indices; the stencil reads the same buffers");

                    int initsBefore = display.StencilInitIssues;
                    int volumesBefore = display.StencilVolumeIssues;
                    int capsBefore = display.StencilCapIssues;
                    Color32[] split = Draw(display, LookingDownFromBetween());

                    // The moved top side's cap is at y = 4, above this camera at 2.5 and so behind it: it is no job.
                    // The fixed side's cap faces the camera: one job, one volume group, one colour, drawn once -- its
                    // volume one command per submesh.
                    VpStencilPreparation preparation = PreparationOf(display, LookingDownFromBetween());
                    Assert.That(preparation.capRecords, Is.EqualTo(2), "both caps are asked about");
                    Assert.That(preparation.hiddenCaps, Is.EqualTo(1), "the side behind the camera is left out");
                    Assert.That(preparation.jobs, Is.EqualTo(1));
                    Assert.That(preparation.volumeGroups, Is.EqualTo(1));
                    Assert.That(preparation.colours, Is.EqualTo(1));
                    Assert.That(preparation.volumeCommands, Is.EqualTo(2), "one volume command per submesh");
                    Assert.That(display.StencilInitIssues - initsBefore, Is.EqualTo(1), "one initialisation per colour");
                    Assert.That(display.StencilVolumeIssues - volumesBefore, Is.EqualTo(1), "one volume issue per colour");
                    Assert.That(display.StencilCapIssues - capsBefore, Is.EqualTo(1), "one cap issue per colour, two submeshes notwithstanding");

                    // Red inside the real cross-section, none on the polygon's overhang, none outside the bounds.
                    Assert.That(IsRed(At(split, World(0f, 0f))), Is.True, "the middle of the cut is capped");
                    Assert.That(IsRed(At(split, World(0.6f, 0.6f))), Is.True, "inside the real cross-section");
                    Assert.That(IsRed(At(split, World(0.9f, 0.9f))), Is.False, "the polygon overhangs here, and the stencil keeps it out");
                    Assert.That(IsRed(At(split, World(0.9f, 0f))), Is.False, "overhang on the axis too");
                    Assert.That(IsRed(At(split, World(1.5f, 1.5f))), Is.False, "outside the bounds");
                    Assert.That(Count(split, IsRed), Is.GreaterThan(Size * Size / 20), "a real area is capped");
                }
            }
        }

        [Test]
        public void Publishing_CarriesTheCapToTheChildren_WithoutDrawingItTwice()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Color32[] before = Draw(display, LookingDownFromBetween());
                    int redBefore = Count(before, IsRed);
                    Assert.That(redBefore, Is.GreaterThan(0));

                    Assert.That(ledger.Publish(cut, out LogicalFragmentId child0, out LogicalFragmentId child1), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "still two caps, now the children's");
                    Assert.That(display.TryGetCapRecord(0, out LogicalCutCapRecord record), Is.True);
                    Assert.That(record.published, Is.True);
                    Assert.That(record.fragment, Is.EqualTo(child0));

                    int capsBefore = display.StencilCapIssues;
                    Color32[] after = Draw(display, LookingDownFromBetween());
                    Assert.That(PreparationOf(display, LookingDownFromBetween()).colours, Is.EqualTo(1), "the same one colour");
                    Assert.That(display.StencilCapIssues - capsBefore, Is.EqualTo(1), "one cap issue per colour, not doubled by publication");
                    Assert.That(Count(after, IsRed), Is.EqualTo(redBefore), "the same cap, in the same place");
                    AssertSamePixels(before, after, "publication changes what is drawn");
                }
            }
        }

        [Test]
        public void PublishingWithNoDisplayUpdateInBetween_StillCapsTheChildren()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    // Admitted, prepared and published with no collection between.
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Color32[] image = Draw(display, LookingDownFromBetween());
                    Assert.That(IsRed(At(image, World(0f, 0f))), Is.True, "the children's cap is there in one step");
                    Assert.That(IsRed(At(image, World(0.9f, 0.9f))), Is.False);
                }
            }
        }

        [Test]
        public void AbortAndStale_RemoveTheCapWithTheSplit()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    // Stale: the result is not shown; the body goes back to whole and the cap goes with the split.
                    CutOperationId first = Admit(ledger, source);
                    Prepare(ledger, first);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(Count(Draw(display, LookingDownFromBetween()), IsRed), Is.GreaterThan(0), "capped while prepared");
                    ledger.NoteOwnershipChanged(source);
                    Assert.That(ledger.Publish(first, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.Zero);
                    Assert.That(Count(Draw(display, LookingDownFromBetween()), IsRed), Is.Zero, "no old cap survives the stale result");
                    Assert.That(PreparationOf(display, LookingDownFromBetween()).colours, Is.Zero, "no split, no colour");

                    // A second cut on the same body, then aborted: the retired source and its cap are both gone.
                    CutOperationId second = Admit(ledger, source);
                    Prepare(ledger, second);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(Count(Draw(display, LookingDownFromBetween()), IsRed), Is.GreaterThan(0), "capped again");
                    Assert.That(ledger.Abort(second), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.ShownCount, Is.Zero, "the retired source is dropped");
                    Assert.That(Count(Draw(display, LookingDownFromBetween()), IsRed), Is.Zero, "and nothing of its cap is left");
                    Assert.That(PreparationOf(display, LookingDownFromBetween()).colours, Is.Zero);
                }
            }
        }

        /// <summary>
        /// A collection that cannot be settled keeps the body and its caps together and keeps drawing them, and the
        /// next collection after the obstacle is gone updates both.
        /// <para>
        /// The obstacle is the display instance capacity, occupied from outside: the split needs a second instance
        /// per body, and with none free the collection is refused before anything is uploaded. Nothing is injected
        /// into the display to make it fail -- the table is the real resource and the test simply holds it.
        /// </para>
        /// </summary>
        [Test]
        public void ARefusedCollection_KeepsTheBodyAndItsCaps_AndTheNextOneUpdatesBoth()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                // Two instance slots: one for the body, and one that this test can hold.
                var table = new VpGeometryReferenceTable(storage, 8, 2);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                VpStoredGeometry spare = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the whole body settles");
                    Color32[] whole = Draw(display, LookingDownFromBetween());
                    Assert.That(Count(whole, IsRed), Is.Zero, "no cut yet");

                    // The one free instance slot goes to this test, so the split cannot take its second instance.
                    Assert.That(
                        table.TryRegisterGeometryWithDisplayInstance(spare, out VpGeometryReference held, out VpDisplayInstanceReference heldInstance),
                        Is.True, "the test holds the last instance slot");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(table.DisplayInstanceCapacity), "no slot is free");

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    int uploads = display.CommandUploads;
                    int stencilUploads = display.StencilUploads;
                    int capRecords = display.CapRecordCount;
                    int sides = display.SideCount;
                    NextFrame();

                    // Refused: the latest logical state cannot be settled.
                    Assert.That(display.TryBeginFrame(), Is.False, "no instance is free for the split");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "the display batch was not written");
                    Assert.That(display.StencilUploads, Is.EqualTo(stencilUploads), "and no stencil arrangement either");
                    Assert.That(display.CapRecordCount, Is.EqualTo(capRecords), "the caps are the ones it had");
                    Assert.That(display.SideCount, Is.EqualTo(sides), "and so are the sides");

                    // And the frame it refused still draws what was adopted before.
                    // Compared as pixels, not as "red or not": the kept snapshot has no cap either way, so a body
                    // that had vanished would pass a red-only comparison.
                    Color32[] stillWhole = Draw(display, LookingDownFromBetween());
                    Assert.That(Count(stillWhole, IsRed), Is.Zero, "the old snapshot, which has no cap");
                    Assert.That(PreparationOf(display, LookingDownFromBetween()).colours, Is.Zero, "prepared from the kept snapshot");
                    AssertSamePixels(whole, stillWhole, "the kept snapshot");
                    int stencilUploadsKept = display.StencilUploads;

                    // The slot goes back, and the next collection updates the body and its caps together.
                    Assert.That(table.TryRetireDisplayInstance(heldInstance), Is.True);
                    Assert.That(table.TryRetireGeometry(held), Is.True);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True, "with a slot free the split settles");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads + 1));
                    Assert.That(display.StencilUploads, Is.EqualTo(stencilUploadsKept), "collecting writes no stencil arrangement");
                    Assert.That(display.SideCount, Is.EqualTo(4), "two sides per command now");
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "and a cap for each side");
                    Assert.That(
                        CountsOf(display, LookingDownFromBetween()).preparedNow, Is.False,
                        "the camera's preparation was for the snapshot just replaced");

                    Color32[] split = Draw(display, LookingDownFromBetween());
                    Assert.That(display.StencilUploads, Is.EqualTo(stencilUploadsKept + 1), "one preparation, one upload");
                    Assert.That(PreparationOf(display, LookingDownFromBetween()).colours, Is.EqualTo(1));
                    Assert.That(IsRed(At(split, World(0f, 0f))), Is.True, "the opening is capped after the recovery");
                    Assert.That(IsRed(At(split, World(0.9f, 0.9f))), Is.False, "and the overhang is still kept out");
                }
            }
        }

        /// <summary>
        /// The stencil batch's non-writing judgement is the judgement the uploads make. What it accepts, both
        /// uploads accept; what it refuses, the draw batch refuses too -- a negative index range, a count past the
        /// capacity, or a transform or clip count that does not match the instances. Asking changes nothing.
        /// </summary>
        [Test]
        public void TheStencilPrecheck_AgreesWithTheUploads_AndChangesNothing()
        {
            // No geometry and no buffers: neither the judgement nor the argument uploads read them, so the range
            // here is a plain description and the test is about the numbers alone.
            {
                using (var draws = new VpIndexedIndirectDrawBatch(2, 2))
                using (var stencil = new VpStencilCapBatch(2, 2, 2, 16, 32))
                {
                    var range = new VpGeometryRange(0, 8, 0, 36);
                    var bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
                    var good = new[] { new VpIndirectCommand(range, bounds, 1) };
                    var transforms = new[] { Matrix4x4.identity };
                    var clips = new[] { VpInstanceClip.Keep(new Vector4(0f, 1f, 0f, -1f), 1f, Vector3.zero) };
                    Vector3[] capVertices =
                    {
                        new Vector3(-1f, 1f, -1f), new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, -1f),
                    };
                    int[] capIndices = { 0, 1, 2, 0, 2, 3 };
                    var colours = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };

                    // Accepted, and asking leaves the batch exactly as it was.
                    int uploads = stencil.Uploads;
                    int writes = stencil.BufferWrites;
                    int colourCount = stencil.ColorCount;
                    Assert.That(
                        stencil.CanUpload(good, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.True, "an ordinary arrangement is accepted");
                    Assert.That(stencil.Uploads, Is.EqualTo(uploads), "asking is not uploading");
                    Assert.That(stencil.BufferWrites, Is.EqualTo(writes), "and writes nothing");
                    Assert.That(stencil.ColorCount, Is.EqualTo(colourCount), "and adopts nothing");
                    Assert.That(draws.CanUpload(good, transforms, clips), Is.True, "the draw batch agrees");
                    Assert.That(
                        stencil.TryUpload(good, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.True, "and the upload it predicted succeeds");
                    Assert.That(draws.TryUpload(good, transforms, clips, false), Is.True);

                    // A negative index range: refused by both, and by the same code.
                    var negativeStart = new[]
                    {
                        new VpIndirectCommand(
                            new VpGeometryRange(range.vertexStart, range.vertexCount, -1, range.indexCount), bounds, 1),
                    };
                    var negativeCount = new[]
                    {
                        new VpIndirectCommand(
                            new VpGeometryRange(range.vertexStart, range.vertexCount, range.indexStart, -1), bounds, 1),
                    };
                    foreach (VpIndirectCommand[] bad in new[] { negativeStart, negativeCount })
                    {
                        Assert.That(
                            stencil.CanUpload(bad, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                            Is.False, "a negative index range is refused before the upload");
                        Assert.That(draws.CanUpload(bad, transforms, clips), Is.False);
                        Assert.That(draws.TryUpload(bad, transforms, clips, false), Is.False, "as the upload itself would");
                    }

                    // Past the command capacity.
                    var tooMany = new[]
                    {
                        new VpIndirectCommand(range, bounds, 1), new VpIndirectCommand(range, bounds, 1),
                        new VpIndirectCommand(range, bounds, 1),
                    };
                    var threeTransforms = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
                    Assert.That(
                        stencil.CanUpload(tooMany, threeTransforms, null, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.False, "more commands than the capacity");
                    Assert.That(draws.CanUpload(tooMany, threeTransforms, null), Is.False);

                    // Past the instance capacity.
                    var tooManyInstances = new[] { new VpIndirectCommand(range, bounds, 3) };
                    Assert.That(
                        stencil.CanUpload(tooManyInstances, threeTransforms, null, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.False, "more instances than the capacity");
                    Assert.That(draws.CanUpload(tooManyInstances, threeTransforms, null), Is.False);

                    // Transform and clip counts that do not match the instances.
                    Assert.That(
                        stencil.CanUpload(good, threeTransforms, null, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.False, "one transform per instance, no more");
                    Assert.That(draws.CanUpload(good, threeTransforms, null), Is.False);
                    Assert.That(draws.TryUpload(good, threeTransforms, null, false), Is.False);
                    var twoClips = new[] { clips[0], clips[0] };
                    Assert.That(
                        stencil.CanUpload(good, transforms, twoClips, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.False, "one clip record per instance, no more");
                    Assert.That(draws.CanUpload(good, transforms, twoClips), Is.False);
                    Assert.That(draws.TryUpload(good, transforms, twoClips, false), Is.False);

                    // A missing array is refused rather than thrown: asking is not uploading.
                    Assert.That(
                        stencil.CanUpload(good, null, clips, capVertices, capVertices.Length, capIndices, capIndices.Length, colours, 1),
                        Is.False);
                    Assert.That(draws.CanUpload(good, null, clips), Is.False);

                    // None of the refusals changed what the batches hold.
                    Assert.That(stencil.ColorCount, Is.EqualTo(1), "the accepted arrangement is still the adopted one");
                    Assert.That(draws.CommandCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void TheCapsFollowTheOffsets_OnOneSideBothSidesAndNeither()
        {
            AssertCapsFollow(new List<float3> { k_lowAnchor }, positiveFixed: false, negativeFixed: true);
            AssertCapsFollow(new List<float3> { k_highAnchor }, positiveFixed: true, negativeFixed: false);
            AssertCapsFollow(new List<float3> { k_lowAnchor, k_highAnchor }, positiveFixed: true, negativeFixed: true);
            AssertCapsFollow(null, positiveFixed: false, negativeFixed: false);
        }

        private void AssertCapsFollow(List<float3> anchors, bool positiveFixed, bool negativeFixed)
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = anchors == null ? ledger.AddFragment() : ledger.AddFragment(anchors);
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = 0.4f;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);

                    // The stencil arrangement's caps are the records' polygons at the records' offsets: what is
                    // counted and what is drawn were placed by the same offsets as the sides.
                    Assert.That(display.CapRecordCount, Is.EqualTo(2));
                    for (int r = 0; r < 2; r++)
                    {
                        Assert.That(display.TryGetCapRecord(r, out LogicalCutCapRecord record), Is.True);
                        bool fixedSide = record.side > 0f ? positiveFixed : negativeFixed;
                        Assert.That(record.fixedByAnchors, Is.EqualTo(fixedSide));
                        Vector3 expected = fixedSide ? Vector3.zero : new Vector3(0f, record.side * 0.4f, 0f);
                        Assert.That((record.offset - expected).magnitude, Is.LessThan(1e-5f), "the record's offset");
                        for (int v = 0; v < record.vertexCount; v++)
                        {
                            Assert.That(display.TryGetCapVertex(r, v, out Vector3 world), Is.True);
                            Assert.That(Mathf.Abs(world.y - (1f + expected.y)), Is.LessThan(1e-4f),
                                "cap vertex " + v + " of side " + record.side + " lies in the moved cut plane");
                        }
                    }

                    // The body and its cap are placed by the same offset: each side's record carries the offset the
                    // side itself was drawn with, so what is counted and what is capped moved together.
                    for (int r = 0; r < 2; r++)
                    {
                        Assert.That(display.TryGetCapRecord(r, out LogicalCutCapRecord record), Is.True);
                        bool found = false;
                        for (int i = 0; i < display.SideCount; i++)
                        {
                            Assert.That(display.TryGetSide(i, out LogicalCutDisplaySide side), Is.True);
                            if (side.side == record.side)
                            {
                                Assert.That((side.offset - record.offset).magnitude, Is.LessThan(1e-6f),
                                    "the side and its cap share one offset");
                                found = true;
                            }
                        }

                        Assert.That(found, Is.True, "every cap has a side");
                    }

                    // Note: a camera whose far plane cuts through the body cannot be used to read the caps, because the
                    // stencil volumes are frustum-clipped like any draw and a body partly beyond the far plane counts
                    // wrongly -- a property of the method, and why every camera here reaches past the whole body.
                }
            }
        }

        [Test]
        public void TheCap_IsDepthTestedAgainstOrdinaryGeometry()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);

                    // An ordinary quad above the cut over its left half, between the camera and the cap: the cap must
                    // lose to it there and win everywhere else.
                    GameObject quad = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
                    Object.DestroyImmediate(quad.GetComponent<Collider>());
                    quad.transform.position = new Vector3(-0.375f, 1.5f, 0f);
                    quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    quad.transform.localScale = new Vector3(0.75f, 1.5f, 1f);
                    Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                    var grey = Track(new Material(unlit));
                    grey.SetColor("_BaseColor", new Color(0.3f, 0.3f, 0.3f));
                    quad.GetComponent<MeshRenderer>().sharedMaterial = grey;

                    Color32[] image = Draw(display, LookingDownFromBetween());
                    Assert.That(IsRed(At(image, World(0.4f, 0f))), Is.True, "the right half of the cap is seen");
                    Assert.That(IsRed(At(image, World(-0.4f, 0f))), Is.False, "the left half is behind the quad");
                }
            }
        }

        /// <summary>
        /// The stereo condition given to this display reaches the body's surfaces and the stencil work as one: both
        /// report the condition of the upload that wrote them, never one of them alone. And it is read where the
        /// arrangement uploads, so an adopted snapshot keeps the condition it was written with until the next
        /// collection settles -- setting the property invalidates nothing by itself.
        /// </summary>
        [Test]
        public void TheStereoCondition_ReachesTheBodyAndTheStencilWork_FromTheOneUpload()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    // Monoscopic is what a display is made as, and what every existing caller keeps.
                    Assert.That(display.SinglePassInstanced, Is.False, "made monoscopic");
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Camera camera = LookingDownFromBetween();
                    Prepare(display, camera);
                    Assert.That(display.DrawsSinglePassInstanced, Is.False, "the body was written monoscopic");
                    Assert.That(CountsOf(display, camera).singlePassInstanced, Is.False, "and so was the stencil work");

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);

                    // Asked for between two collections: the snapshot on the GPU is not touched by the asking.
                    display.SinglePassInstanced = true;
                    Assert.That(display.DrawsSinglePassInstanced, Is.False, "the adopted body is still as written");
                    Prepare(display, camera);
                    Assert.That(
                        CountsOf(display, camera).singlePassInstanced, Is.False,
                        "and a camera prepared from it now takes the body's condition, not the property");

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Prepare(display, camera);
                    Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(1), "the split is shown");
                    Assert.That(display.DrawsSinglePassInstanced, Is.True, "the body took the condition");
                    Assert.That(
                        CountsOf(display, camera).singlePassInstanced, Is.True,
                        "and the initialisation, the volumes and the caps took the same one");

                    // And back: nothing latches, and the two still move together.
                    display.SinglePassInstanced = false;
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Prepare(display, camera);
                    Assert.That(display.DrawsSinglePassInstanced, Is.False);
                    Assert.That(CountsOf(display, camera).singlePassInstanced, Is.False);
                }
            }
        }

        /// <summary>
        /// An arrangement uploaded for Single Pass Instanced draws the same image to a monoscopic camera, pixel for
        /// pixel -- body, initialisation, volumes and caps together -- because each shader's single-view variant
        /// rejects the second eye's copy. What this establishes is that the non-XR path is unchanged by asking for
        /// stereo; that both eyes are actually drawn is not decided by a monoscopic camera and is not claimed here.
        /// </summary>
        [Test]
        public void AnArrangementUploadedForSinglePassInstanced_DrawsTheSameToAMonoscopicCamera()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Color32[] monoscopic = Draw(display, LookingDownFromBetween());
                    Assert.That(IsRed(At(monoscopic, World(0f, 0f))), Is.True, "the opening is capped");
                    int polygons = display.CapPolygonBuilds;

                    // The very same logical state, collected again with nothing else changed.
                    display.SinglePassInstanced = true;
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.DrawsSinglePassInstanced, Is.True);
                    Assert.That(
                        display.CapPolygonBuilds, Is.EqualTo(polygons),
                        "the same box, face and placement: no polygon was taken again");

                    Color32[] stereo = Draw(display, LookingDownFromBetween());
                    Assert.That(CountsOf(display, LookingDownFromBetween()).singlePassInstanced, Is.True, "the stencil work too");
                    AssertSamePixels(monoscopic, stereo, "the same arrangement uploaded for stereo");
                }
            }
        }

        /// <summary>
        /// Drawing writes nothing, and preparing writes one arrangement. A camera's preparation is one upload of its
        /// own batch, whatever its colour count; drawing it then leaves the command uploads, the vertex and index
        /// transfers and every stencil buffer write where they were, and issues each stencil step once per colour. A
        /// second camera in the same frame prepares into its own batch and leaves the first camera's untouched.
        /// </summary>
        [Test]
        public void Drawing_AddsNoTransferAndNoPerColourUpload()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.SinglePassInstanced = true;
                    display.Separation = WideSeparation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);

                    Camera first = LookingDownFromBetween();
                    int uploadsBefore = display.StencilUploads;
                    Prepare(display, first);
                    Assert.That(display.StencilUploads, Is.EqualTo(uploadsBefore + 1), "one preparation, one upload");
                    VpStencilCameraCounts firstPrepared = CountsOf(display, first);

                    int uploads = display.CommandUploads;
                    int stencilUploads = display.StencilUploads;
                    int writes = display.StencilBufferWrites;
                    int vertices = display.VertexTransfers;
                    int indices = display.IndexTransfers;
                    int init = display.StencilInitIssues;
                    int volumes = display.StencilVolumeIssues;
                    int caps = display.StencilCapIssues;
                    int colours = PreparationOf(display, first).colours;

                    display.Render(0, first);
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "drawing uploaded no commands");
                    Assert.That(display.StencilUploads, Is.EqualTo(stencilUploads), "nor any arrangement");
                    Assert.That(display.StencilBufferWrites, Is.EqualTo(writes), "and wrote no buffer");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertices), "no vertices went again");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indices), "no indices went again");
                    Assert.That(display.StencilInitIssues, Is.EqualTo(init + colours), "one initialisation per colour");
                    Assert.That(display.StencilVolumeIssues, Is.EqualTo(volumes + colours), "one volume call per colour");
                    Assert.That(display.StencilCapIssues, Is.EqualTo(caps + colours), "one cap call per colour");
                    Read(first);

                    // A second camera of the same frame: its own preparation and its own upload, and the first
                    // camera's batch -- which its registered draws read -- is not written.
                    Camera second = LookingDown(2.5f, 3f, 2.5f);
                    Prepare(display, second);
                    Assert.That(display.StencilUploads, Is.EqualTo(stencilUploads + 1), "the second camera's own upload");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "and no body upload");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertices), "and no geometry");
                    VpStencilCameraCounts firstAfter = CountsOf(display, first);
                    Assert.That(firstAfter.uploads, Is.EqualTo(firstPrepared.uploads), "the first camera's batch was not uploaded again");
                    Assert.That(firstAfter.bufferWrites, Is.EqualTo(firstPrepared.bufferWrites), "nor written");
                    Draw(display, second);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }

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

        // The display path's own shader, so the bodies actually draw through the indexed indirect batch.
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

        private bool TryCreate(
            VpCpuGeometryStorage storage, VpGeometryReferenceTable table, LogicalCutLedger ledger,
            out VpLogicalCutDisplay display, int commandCapacity = 16, int instanceCapacity = 16)
        {
            return VpLogicalCutDisplay.TryCreate(
                storage, table, ledger, Materials(), null, null, commandCapacity, instanceCapacity,
                VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(), () => _frame, out display);
        }

        private void NextFrame()
        {
            _frame++;
        }

        private static LogicalCutLedger NewLedger(int limit = 4)
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(limit));
        }

        private static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId source)
        {
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId operation), Is.EqualTo(LogicalCutAdmission.Admitted));
            return operation;
        }

        private static void Prepare(LogicalCutLedger ledger, CutOperationId operation)
        {
            Assert.That(ledger.PrepareAnchorDistribution(operation, Epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
        }

        // ---- drawing ------------------------------------------------------------------------------------------------

        /// <summary>
        /// An orthographic camera at the given height looking straight down, seeing x and z in [-size, size], with
        /// the given reach below it. The default sits between the halves once the top has moved far up, and reaches
        /// past the fixed bottom half's cut at y = 1.
        /// </summary>
        private Camera LookingDown(float height, float reach, float size = 2f)
        {
            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32)
            {
                depthStencilFormat = VpStencilAttachment.EightBitStencilFormat,
                antiAliasing = 1,
            });
            target.Create();
            Camera camera = Track(new GameObject("Logical Stencil Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = size;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = reach;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, height, 0f);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            return camera;
        }

        /// <summary>One camera per test for the default view, so that it is registered once and not per draw.</summary>
        private Camera LookingDownFromBetween()
        {
            if (_between == null)
            {
                _between = LookingDown(2.5f, 3f);
            }

            return _between;
        }

        /// <summary>What the camera's last preparation made.</summary>
        private static VpStencilPreparation PreparationOf(VpLogicalCutDisplay display, Camera camera)
        {
            Assert.That(
                display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out _), Is.True,
                "the camera is registered");
            return preparation;
        }

        private static VpStencilCameraCounts CountsOf(VpLogicalCutDisplay display, Camera camera)
        {
            Assert.That(display.TryGetCameraStencil(camera, out _, out VpStencilCameraCounts counts), Is.True);
            return counts;
        }

        /// <summary>Registers the camera when it is not yet, and prepares it for this frame's snapshot.</summary>
        private static void Prepare(VpLogicalCutDisplay display, Camera camera)
        {
            display.TryRegisterCamera(camera);
            Assert.That(display.TryPrepareCamera(camera), Is.True, "the camera is prepared");
        }

        private Color32[] Draw(VpLogicalCutDisplay display, Camera camera)
        {
            Prepare(display, camera);
            display.Render(0, camera);
            return Read(camera);
        }

        /// <summary>Renders the camera, drawing what was registered for it, and reads its target back.</summary>
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

        /// <summary>The pixel under world (x, z) for the default camera: size 2 covers [-2, 2] on both axes.</summary>
        private static int World(float x, float z)
        {
            int px = Mathf.Clamp(Mathf.RoundToInt((x + 2f) / 4f * (Size - 1)), 0, Size - 1);

            // With the camera's up along +z, world +z is up the image, and rows count from the bottom.
            int py = Mathf.Clamp(Mathf.RoundToInt((z + 2f) / 4f * (Size - 1)), 0, Size - 1);
            return py * Size + px;
        }

        private static Color32 At(Color32[] pixels, int index)
        {
            return pixels[index];
        }

        /// <summary>
        /// Every channel of every pixel, so that a body which stopped drawing is a failure rather than another image
        /// with no red in it.
        /// </summary>
        private static void AssertSamePixels(Color32[] expected, Color32[] actual, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what + ": the same number of pixels");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].r != actual[i].r || expected[i].g != actual[i].g
                    || expected[i].b != actual[i].b || expected[i].a != actual[i].a)
                {
                    Assert.Fail(
                        what + ": pixel " + i + " went from " + expected[i] + " to " + actual[i]);
                }
            }
        }

        private static bool IsRed(Color32 c)
        {
            return c.r > 180 && c.g < 80 && c.b < 80;
        }

        private static int Count(Color32[] pixels, System.Func<Color32, bool> test)
        {
            int n = 0;
            foreach (Color32 p in pixels)
            {
                if (test(p))
                {
                    n++;
                }
            }

            return n;
        }
    }
}
