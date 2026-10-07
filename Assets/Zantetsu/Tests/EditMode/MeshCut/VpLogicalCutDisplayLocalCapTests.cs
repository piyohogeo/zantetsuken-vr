using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The caps' shapes kept with the structure, in the geometry's local frame (TL, 2026-10-07; DESIGN 5.6, D-208): what
    /// is done, counted, when nothing changes, when a body moves, when only the view moves and when a structure changes.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // What was done for the caps so far: shapes made (sections taken, polygons cut), sections looked up, planes
        // carried, the local vertices laid out, the cameras sent them, the records sent, the placed vertices made.
        private struct CapWork
        {
            public long shapes, shapeVertices, sectionsBuilt, sectionLookups, clips, planes, layouts, layoutVertices, cameraUploads, cameraVertices, records, recordSends, placedFills;

            public static CapWork Of(VpLogicalCutDisplay d)
            {
                VpPlaceCounts only = PlacementOnlyOf(d, out VpPlaceCounts structural);
                VpSnapshotStageTotals stages = d.StageTotalsForTest;
                return new CapWork
                {
                    shapes = stages.capShapesBuilt, shapeVertices = stages.capShapeVertices,
                    sectionsBuilt = only.sectionsBuilt + structural.sectionsBuilt,
                    sectionLookups = only.sectionsFoundHere + only.sectionsReused + only.sectionEntriesCompared,
                    clips = only.capClips + structural.capClips, planes = only.planeTransforms + structural.planeTransforms,
                    layouts = d.CapLayouts, layoutVertices = d.CapLayoutVertices, cameraUploads = d.CameraCapLayoutUploads, cameraVertices = d.CameraCapLayoutVertices,
                    records = d.CapNormalVerticesTransferred, recordSends = d.CapNormalTransfers, placedFills = d.PlacedCapFills,
                };
            }

            public CapWork Since(in CapWork b) => new CapWork
            {
                shapes = shapes - b.shapes, shapeVertices = shapeVertices - b.shapeVertices, sectionsBuilt = sectionsBuilt - b.sectionsBuilt,
                sectionLookups = sectionLookups - b.sectionLookups, clips = clips - b.clips, planes = planes - b.planes, layouts = layouts - b.layouts,
                layoutVertices = layoutVertices - b.layoutVertices, cameraUploads = cameraUploads - b.cameraUploads, cameraVertices = cameraVertices - b.cameraVertices,
                records = records - b.records, recordSends = recordSends - b.recordSends, placedFills = placedFills - b.placedFills,
            };

            public override string ToString() =>
                "shapes made " + shapes + " (" + shapeVertices + " local vertices; sections taken " + sectionsBuilt + ", polygon clips " + clips + "), sections looked up in a placement pass " + sectionLookups
                + ", planes carried " + planes + "; local vertices laid out " + layouts + " times (" + layoutVertices + " vertices), sent to cameras " + cameraUploads + " times (" + cameraVertices
                + " vertices); cap records sent " + records + " float4 in " + recordSends + " writes; placed vertices made for a reader " + placedFills + " times";
        }

        [Test]
        public void LocalCaps_AShapeIsMadeWithItsStructureOnly_AMoveSendsItsRecords_AViewNothing_AndAnotherBodysCutLeavesItAlone()
        {
            using (Twin twin = NewTwin(4, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                VpLogicalCutDisplay reference = twin.everything.Display;
                Camera here = Looking(new Vector3(6f, 4f, -6f), new Vector3(0f, -0.5f, 1f), 6f);
                Camera there = Looking(new Vector3(8f, 5f, 6f), new Vector3(-0.2f, -0.6f, -1f), 6f);

                // Body 1 is cut (published, not committed): its two sides are clipped and capped.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) first = default;
                CapWork w = CapWork.Of(display);
                Both(twin, run => first = CutAndPlace(run, 1, plane, Stand(1)));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "the cut published " + f);
                Color32[] before = Draw(display, here);
                Assert.That(Differing(before, Draw(reference, here)), Is.Zero, "the two displays draw the same");
                CapWork made = CapWork.Of(display).Since(w);
                TestContext.Out.WriteLine("a cut published and settled: " + made);
                Assert.That(display.CapRecordCount, Is.EqualTo(2), "the layout: two caps, one a side");
                Assert.That(new[] { made.shapes, made.sectionsBuilt, made.clips }, Is.EqualTo(new long[] { 2, 1, 2 }), "made with the structure: two shapes, the one section their face shares, a polygon cut for each");
                Assert.That(made.sectionLookups, Is.Zero, "and no section looked up in a placement pass");
                Assert.That(made.cameraUploads, Is.GreaterThanOrEqualTo(1), "the camera was sent the local vertices of that layout");
                Assert.That(new[] { display.CameraCapLayoutUploads, display.CameraCapLayoutFirstUploads, display.CameraCapLayoutFirstVertices }, Is.EqualTo(new long[] { 1, 1, 8 }),
                    "and that was the camera's first: it held no cap vertices before");

                // 1. Nothing changes: collections and drawings go on. Nothing of a shape is done, nothing is sent.
                long fillsBefore = display.PlacedCapFills;
                w = CapWork.Of(display);
                for (int f = 0; f < 3; f++)
                {
                    // A frame's record (what the Player's frames.csv is written from; it adds up over the collections of
                    // one Editor frame, which is every collection of this test): the cameras are sent the local vertices
                    // between collections, so the first collection after the drawing above tells that one sending, and
                    // the ones after it tell none.
                    Assert.That(display.TryGetFrameCounts(Time.frameCount, out VpLogicalCutDisplay.FrameCounts toldBefore), Is.True);
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True, "collected");
                    Assert.That(display.TryGetFrameCounts(Time.frameCount, out VpLogicalCutDisplay.FrameCounts told), Is.True);
                    Assert.That(
                        new[]
                        {
                            told.stages.cameraCapUploads - toldBefore.stages.cameraCapUploads, told.stages.cameraCapVertices - toldBefore.stages.cameraCapVertices,
                            told.stages.cameraCapFirstUploads - toldBefore.stages.cameraCapFirstUploads, told.stages.cameraCapFirstVertices - toldBefore.stages.cameraCapFirstVertices,
                        },
                        Is.EqualTo(f == 0 ? new long[] { 1, 8, 1, 8 } : new long[] { 0, 0, 0, 0 }),
                        "the frame's record, collection " + f + ": what the cameras were sent since the collection before, and how much of it was a camera's first");
                    Draw(display, here);
                }

                CapWork still = CapWork.Of(display).Since(w);
                TestContext.Out.WriteLine("three collections and drawings, nothing changed: " + still);
                Assert.That(new[] { still.shapes, still.sectionsBuilt, still.sectionLookups, still.clips, still.layouts, still.cameraUploads, still.records, still.placedFills },
                    Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0, 0, 0 }), "no section looked up or taken, no polygon cut, no vertex laid out or sent, no record sent, no vertex placed");
                Assert.That(still.planes, Is.EqualTo(3 * 2), "what is still done a pass: each clipped side's one plane carried into the world (two a pass)");

                // 2. The body moves and turns: its caps follow, drawn where it is -- by their records. No shape work, no
                //    cap vertex laid out or sent to a camera.
                w = CapWork.Of(display);
                Both(twin, run => run.at.Put(first.positive, Stand(1, 0.75f, 30f)));
                Both(twin, run => run.at.Put(first.negative, Stand(1, -0.25f, 30f)));
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True, "the reference collected");
                Color32[] moved = Draw(display, here);
                CapWork move = CapWork.Of(display).Since(w);
                TestContext.Out.WriteLine("the cut body moved and turned: " + move);
                Assert.That(Differing(moved, before), Is.GreaterThan(0), "the picture followed the move");
                Assert.That(Differing(moved, Draw(reference, here)), Is.Zero, "and is what the display that collects everything draws");
                Assert.That(new[] { move.shapes, move.sectionsBuilt, move.sectionLookups, move.clips, move.layouts, move.cameraUploads, move.placedFills },
                    Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0, 0 }), "no shape work and no cap vertex sent for a move");
                Assert.That(new[] { move.records, move.recordSends }, Is.EqualTo(new long[] { 2 * VpLogicalCutDisplay.CapPlacementStride, 1 }), "the two caps' records sent, in one write");

                // The placed vertices, asked for by a reader (this test): the kept local vertices carried by the placement.
                long fills = display.PlacedCapFills;
                Assert.That(display.TryGetCapVertex(0, 0, out Vector3 world), Is.True);
                display.AdoptedSnapshot.TryGetCap(0, out VpMultiCutCap cap0);
                display.AdoptedSnapshot.TryGetRenderFragment(cap0.renderFragment, out VpMultiCutRenderFragment placed0);
                Assert.That((world - placed0.geometryLocalToWorld.MultiplyPoint3x4(display.AdoptedSnapshot.LocalCapPolygon(0)[0])).magnitude, Is.LessThan(1e-5f), "the kept local vertex, placed");
                Assert.That(display.PlacedCapFills, Is.EqualTo(fills + 1), "made for this reader, once");

                // 3. Only the view moves: judged and drawn anew from the kept shapes, in both displays alike.
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "collected");
                Assert.That(reference.TryBeginFrame(), Is.True, "the reference collected");
                w = CapWork.Of(display);
                Color32[] fromThere = Draw(display, there);
                CapWork view = CapWork.Of(display).Since(w);
                TestContext.Out.WriteLine("another view: " + view);
                Assert.That(Differing(fromThere, moved), Is.GreaterThan(0), "the view did change");
                Assert.That(Differing(fromThere, Draw(reference, there)), Is.Zero, "and the two displays draw it the same");
                Assert.That(new[] { view.shapes, view.sectionsBuilt, view.sectionLookups, view.clips, view.layouts, view.records, view.placedFills },
                    Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0, 0 }), "nothing of a shape, a layout or a record for a view");
                Assert.That(new[] { view.cameraUploads, display.CameraCapLayoutFirstUploads }, Is.EqualTo(new long[] { 1, 2 }),
                    "the other camera was sent the layout once -- its first, the second first sending of this display");

                // 4. Another body is cut: its shapes are made; body 1's are the ones kept (not made again).
                w = CapWork.Of(display);
                var other = Normalized(new float4(1f, 0.3f, 0f, 0.05f));
                Both(twin, run => CutAndPlace(run, 2, other, Stand(2)));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "another body cut " + f);
                Draw(display, here);
                CapWork second = CapWork.Of(display).Since(w);
                TestContext.Out.WriteLine("another body cut: " + second);
                Assert.That(display.CapRecordCount, Is.EqualTo(4), "four caps now");
                Assert.That(new[] { second.shapes, second.sectionsBuilt, second.clips }, Is.EqualTo(new long[] { 2, 1, 2 }), "the new cut's two shapes only: the first body's were not made again");
                Assert.That(second.sectionLookups, Is.Zero);
                Assert.That(second.layouts, Is.GreaterThanOrEqualTo(1), "the local vertices laid out again for the new structure");
                Assert.That(new[] { second.cameraUploads, display.CameraCapLayoutFirstUploads }, Is.EqualTo(new long[] { 1, 2 }),
                    "the camera drawn for was sent the new layout: a sending again, not a first (the other camera, not drawn for, was sent nothing)");

                // The first body's cut commits: its family's structure is settled again -- it has no selected boundary
                // left, so no shape is made -- and the other body's shapes are kept.
                w = CapWork.Of(display);
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 1, first.cut, plane, first.positive, first.negative), Is.True, "committed"));
                for (int f = 0; f < 3; f++) CollectBoth(twin, "the first cut committed " + f);
                Draw(display, here);
                CapWork commit = CapWork.Of(display).Since(w);
                TestContext.Out.WriteLine("the first cut committed: " + commit);
                Assert.That(display.CapRecordCount, Is.EqualTo(2), "the second body's two caps are left");
                Assert.That(new[] { commit.shapes, commit.sectionsBuilt, commit.clips, commit.sectionLookups }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "no shape made: the other body's are the ones kept");
            }
        }

        /// <summary>
        /// What is drawn of a cap is the triangles the arrangement names, of the local vertices laid out, placed by the
        /// cap's record -- its render fragment's placement. Placed so, every one of them goes round the cap's outward
        /// normal (the caps are drawn with their backs culled): under a placement that moves and turns, one that scales
        /// unevenly, and one that mirrors, where the kept order is read the other way round and the shape is not made
        /// again. Two cuts, so that a cap is a section cut by another plane; two cameras opposite one another, so that
        /// every cap faces one of them.
        /// </summary>
        [Test]
        public void LocalCaps_TheTrianglesNamedForDrawing_GoRoundTheOutwardNormal_UnderPlacementsThatScaleAndMirror()
        {
            (Matrix4x4 at, string what)[] cases =
            {
                (Stand(1, 0.2f, 25f), "moved and turned"),
                (Stand(1) * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(15f, 0f, -20f), new Vector3(0.6f, 1.4f, 0.9f)), "scaled unevenly"),
                (Stand(1) * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 30f, 0f), new Vector3(1f, -1.2f, 1f)), "mirrored and scaled"),
                (Stand(1) * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(20f, 0f, 10f), new Vector3(-1f, 1f, 1f)), "mirrored, lengths kept"),
            };

            foreach ((Matrix4x4 at, string what) in cases)
            {
                using (Twin twin = NewTwin(2, 64, true))
                {
                    VpLogicalCutDisplay display = twin.kept.Display;
                    var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                    var along = Normalized(new float4(1f, 0.3f, 0f, 0.2f));
                    Both(twin, run =>
                    {
                        (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) first = CutAndPlace(run, 1, plane, at);
                        (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) next = Cut(run.scene.ledger, first.positive, along);
                        run.at.Put(next.positive, at).Put(next.negative, at);
                    });
                    for (int f = 0; f < 3; f++) CollectBoth(twin, what + ": cut twice " + f);

                    VpMultiCutSnapshot snapshot = display.AdoptedSnapshot;
                    int withArea = 0;
                    for (int c = 0; c < snapshot.CapCount; c++)
                    {
                        Assert.That(snapshot.TryGetCap(c, out VpMultiCutCap cap), Is.True);
                        Assert.That(cap.mirrored, Is.EqualTo(at.determinant < 0f), what + ": the cap says whether its placement mirrors");
                        if (cap.vertexCount >= 3) withArea++;
                    }

                    Assert.That(withArea, Is.EqualTo(5), what + ": the layout -- one cap on the first cut's negative side, two on each side of the second");

                    var seen = new HashSet<int>();
                    int triangles = 0;
                    Camera[] cameras =
                    {
                        Looking(new Vector3(7f, 4f, -4f), new Vector3(-1f, -1f, 1f), 6f),
                        Looking(new Vector3(-1f, -4f, 4f), new Vector3(1f, 1f, -1f), 6f),
                    };
                    foreach (Camera camera in cameras)
                    {
                        Draw(display, camera);
                        Assert.That(display.ArrangedCapIndexCount % 3, Is.Zero, what + ": triangles");
                        for (int i = 0; i < display.ArrangedCapIndexCount; i += 3)
                        {
                            var world = new Vector3[3];
                            int of = -1;
                            VpMultiCutCap cap = default;
                            for (int k = 0; k < 3; k++)
                            {
                                Assert.That(display.TryGetArrangedCapIndex(i + k, out int vertex), Is.True);
                                Vector4 local = display.CapLocalVertexForTest(vertex);
                                int c = (int)(local.w + 0.5f);
                                Assert.That(k == 0 || c == of, Is.True, what + ": a triangle is of one cap");
                                of = c;
                                Assert.That(snapshot.TryGetCap(c, out cap), Is.True);
                                Assert.That(snapshot.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment fragment), Is.True);
                                world[k] = fragment.geometryLocalToWorld.MultiplyPoint3x4(new Vector3(local.x, local.y, local.z));
                            }

                            Vector3 normal = Vector3.Cross(world[1] - world[0], world[2] - world[0]).normalized;
                            Assert.That(Vector3.Dot(normal, cap.outwardNormal), Is.GreaterThan(0.999f), what + ": cap " + of + ", a triangle as drawn goes round its outward normal");
                            seen.Add(of);
                            triangles++;
                        }
                    }

                    TestContext.Out.WriteLine(what + ": " + triangles + " triangles named over two opposite views, of " + seen.Count + " caps");
                    Assert.That(seen.Count, Is.EqualTo(withArea), what + ": every cap with an area was named for one view or the other");
                }
            }
        }
    }
}
