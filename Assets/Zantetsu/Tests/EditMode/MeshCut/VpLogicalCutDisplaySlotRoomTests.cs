using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The room of the draw slots (TL, 2026-10-06; DESIGN 5.6, D-200). The command slots and the instance records are
    /// used up over a display's life -- a slot is taken at the end and never again -- so their whole room, 65536 of
    /// each, is made before play and is never grown during it. What follows from how many render fragments are drawn
    /// at once -- the snapshot's render fragments and caps, the cap records, vertices and indices, the stencil
    /// commands, every camera's stencil batch, the registrations' own tables -- is not raised with them: it has a
    /// count of its own, which starts at 1024 and grows as it did.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private const string SlotRoomProfilePath = "Assets/Zantetsu/Settings/CutWorldSandboxProfile.asset";

        private sealed class MadeRoom
        {
            public Scene scene;
            public VpRoomBytes cpu;
            public long gpu, gpuWithACamera;
            public double preparationMs;
            public Dictionary<string, VpRoomLine> lines;
        }

        // A display with the profile's own counts but for the two given, by the GPU selection or without it, with one
        // camera registered: what its room is, in bytes, and how long it took to make.
        private MadeRoom MakeRoom(CutWorldProfile profile, int commands, int instances, VpLogicalCutDisplayLimits limits, bool selecting)
        {
            var made = new MadeRoom
            {
                scene = new Scene
                {
                    storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
                    ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                    endFrame = () => _frame++,
                },
            };
            made.scene.table = new VpGeometryReferenceTable(made.scene.storage, 16, 1024, 64, 65536);
            VpStencilSettings stencil = profile.StencilSettings;
            VpGpuCullSetup culling = null;
            if (selecting)
            {
                Assert.That(VpGpuCullSetup.TryCreate(stencil.cameraCapacity, out culling, out string failure), Is.True, failure);
            }

            Assert.That(
                VpLogicalCutDisplay.TryCreateCore(
                    made.scene.storage, made.scene.table, made.scene.ledger, Materials(), null, null, commands, instances,
                    profile.BranchCapacity, profile.CandidateCapacity, profile.ChainDepth, stencil, () => _frame,
                    made.scene.storage.CommittedVertexCapacity, made.scene.storage.CommittedIndexCapacity, out made.scene.display,
                    limits, null, culling),
                Is.True, "the display is made: " + VpLogicalCutDisplay.LastCreationFailure);
            VpLogicalCutDisplay display = made.scene.display;
            made.cpu = display.RoomBytes();
            made.gpu = display.RoomGpuBytes;
            made.preparationMs = display.RoomPreparationMilliseconds;
            made.lines = LinesOf(display);
            Assert.That(display.TryRegisterCamera(Oblique()), Is.True, "a camera is registered");
            made.gpuWithACamera = display.RoomGpuBytes;
            return made;
        }

        private static string Describe(string what, MadeRoom made)
        {
            return what + ": CPU numeric rooms in use " + made.cpu.nativeInUse + " B, committed " + made.cpu.nativeCommitted + " B, reserved "
                   + made.cpu.nativeReserved + " B; managed arrays (elements) " + made.cpu.managed + " B; GPU buffers " + made.gpu
                   + " B, a camera's own " + (made.gpuWithACamera - made.gpu) + " B; made in " + made.preparationMs.ToString("F2") + " ms (Editor)";
        }

        [Test]
        public void TheDrawSlotsRoom_IsMadeWholeBeforePlay_AndWhatFollowsFromTheRenderFragmentsIsNotRaisedWithIt()
        {
            CutWorldProfile profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(SlotRoomProfilePath);
            Assert.That(profile, Is.Not.Null, "the shared profile");
            VpLogicalCutDisplayLimits limits = profile.DisplayLimits;
            Assert.That(
                new[] { profile.DrawCommandCapacity, profile.DrawInstanceCapacity, limits.commands, limits.instances },
                Is.EqualTo(new[] { 65536, 65536, 65536, 65536 }), "the draw slots' first room is their limit, 65536 of each");
            Assert.That(
                new[] { profile.RenderFragmentCapacity, limits.firstRenderFragments, limits.renderFragments },
                Is.EqualTo(new[] { 1024, 1024, 65536 }), "the render fragments' own room starts at 1024 and may grow");
            Assert.That(profile.IsUsable(out string reason), Is.True, reason);
            const int slots = 65536, fragments = 1024, eight = VpClipCandidates.Capacity;

            // As the profile has it now, and as it was: 1024 of each, the render fragments following the instances.
            var before = new VpLogicalCutDisplayLimits(limits.commands, limits.instances, limits.branches, limits.candidates);
            foreach (bool selecting in new[] { true, false })
            {
                string route = selecting ? "the GPU selection (" + profile.StencilSettings.cameraCapacity + " views)" : "VP Stage 3";
                MadeRoom now = MakeRoom(profile, profile.DrawCommandCapacity, profile.DrawInstanceCapacity, limits, selecting);
                MadeRoom was = MakeRoom(profile, 1024, 1024, before, selecting);
                using (now.scene)
                using (was.scene)
                {
                    VpLogicalCutDisplay display = now.scene.display;
                    TestContext.Out.WriteLine(Describe(route + ", 65536 slots", now));
                    TestContext.Out.WriteLine(Describe(route + ", 1024 slots (as it was)", was));
                    var names = new List<string>(now.lines.Keys);
                    names.Sort(System.StringComparer.Ordinal);
                    foreach (string name in names)
                    {
                        VpRoomLine a = now.lines[name];
                        bool known = was.lines.TryGetValue(name, out VpRoomLine b);
                        if (!known || a.length != b.length || a.committedBytes != b.committedBytes || a.managedBytes != b.managedBytes)
                        {
                            TestContext.Out.WriteLine(
                                "   " + name + ": " + a.itemBytes + " B an item; items " + (known ? b.length.ToString() : "-") + " -> " + a.length
                                + "; committed " + (known ? b.committedBytes.ToString() : "-") + " -> " + a.committedBytes + " B; managed "
                                + (known ? b.managedBytes.ToString() : "-") + " -> " + a.managedBytes + " B; reserved " + a.reservedBytes + " B");
                        }
                    }

                    // The draw slots: 65536 of each, made and committed whole, with nothing left to grow.
                    Assert.That(
                        new[] { display.CommandCapacity, display.InstanceCapacity, display.RenderFragmentCapacity },
                        Is.EqualTo(new[] { slots, slots, fragments }), route + ": the room");
                    foreach (string name in new[]
                             {
                                 "display.commands", "display.commands'", "display.commandStarts", "display.commandStarts'",
                                 "display.commandProvisional", "display.commandProvisional'", "display.transforms", "display.transforms'",
                                 "display.clips", "display.clips'", "display.sides", "display.sides'",
                             })
                    {
                        VpRoomLine line = now.lines[name];
                        Assert.That(new[] { line.length, line.reservedLength }, Is.EqualTo(new long[] { slots, slots }), route + ": " + name + " holds every slot from the first");
                        Assert.That(line.committedBytes, Is.GreaterThanOrEqualTo((long)slots * line.itemBytes), route + ": " + name + " is committed whole");
                    }

                    VpIndexedIndirectDrawBatch batch = display.BodyBatchForTest;
                    Assert.That(new[] { batch.CommandCapacity, batch.InstanceCapacity }, Is.EqualTo(new[] { slots, slots }), route + ": the body's GPU buffers hold every slot");
                    if (selecting)
                    {
                        Assert.That(
                            batch.CullViewBytes, Is.EqualTo((long)profile.StencilSettings.cameraCapacity * 2L * (20L + 4L) * slots),
                            route + ": a view's two argument buffers and two lists of kept instances, for every view");
                    }

                    // What follows from the render fragments is what it was at 1024 -- by count, and to the byte.
                    foreach ((string name, long items) in new (string, long)[]
                             {
                                 ("display.roots", fragments), ("display.roots'", fragments), ("display.rfCommandStart", fragments),
                                 ("display.rfTransform", fragments), ("display.capRecords", fragments * eight),
                                 ("display.capRecords'", fragments * eight), ("display.stencilCommands", fragments * eight),
                                 ("display.stencilTransforms", fragments * eight), ("display.stencilClips", fragments * eight),
                                 ("display.capNormals", fragments * eight * VpCapPolygonClip.MaxVertices),
                                 ("display.capIndices", fragments * eight * (VpCapPolygonClip.MaxVertices - 2) * 3),
                             })
                    {
                        Assert.That(now.lines[name].length, Is.EqualTo(items), route + ": " + name + " follows the render fragments");
                    }

                    foreach (string name in names)
                    {
                        if (name.StartsWith("snapshot", System.StringComparison.Ordinal) || name.StartsWith("capJobs", System.StringComparison.Ordinal))
                        {
                            Assert.That(
                                new[] { now.lines[name].length, now.lines[name].committedBytes }, Is.EqualTo(new[] { was.lines[name].length, was.lines[name].committedBytes }),
                                route + ": " + name + " is what it was at 1024");
                        }
                    }

                    Assert.That(
                        now.gpuWithACamera - now.gpu, Is.EqualTo(was.gpuWithACamera - was.gpu),
                        route + ": a camera's own GPU buffers are what they were: they follow the render fragments");
                    Assert.That(display.RoomGrowths + display.GpuReplacements, Is.Zero, route + ": nothing grown, nothing replaced");
                }
            }
        }

        [Test]
        public void TheRenderFragmentsRoom_GrowsByItself_WithoutTheDrawSlotsRoom_AndTheBodysGpuBuffersStand()
        {
            // Draw slots fixed at 64 of each; room for two render fragments at first, which may grow to 64.
            var run = new Run();
            run.scene = NewReservedScene(64, 64, 2, 64, new VpLogicalCutDisplayLimits(64, 64, 64, 64, 2, 64), null);
            Assert.That(run.scene, Is.Not.Null, "the display was made");
            using (run.scene)
            {
                VpLogicalCutDisplay display = run.Display;
                display.Placement = run.at;
                Assert.That(
                    new[] { display.CommandCapacity, display.InstanceCapacity, display.RenderFragmentCapacity }, Is.EqualTo(new[] { 64, 64, 2 }),
                    "the layout: 64 slots of each, two render fragments");
                VpIndexedIndirectDrawBatch batch = display.BodyBatchForTest;
                _frame++;
                for (int i = 0; i < 9; i++) AddSlotBody(run, Stand(i), false);
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "nine bodies: collected");
                AssertGpuHoldsWhatIsAdopted(display, "nine bodies");
                Assert.That(display.RenderFragmentCapacity, Is.GreaterThanOrEqualTo(9), "the render fragments' room grew");
                Assert.That(new[] { display.CommandCapacity, display.InstanceCapacity }, Is.EqualTo(new[] { 64, 64 }), "the draw slots' room did not: it was whole from the first");
                Assert.That(display.BodyBatchForTest, Is.SameAs(batch), "and the body's GPU buffers were not replaced");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd, display.DrawInstanceEnd }, Is.EqualTo(new[] { 9, 9, 9 }));

                // A cut: more render fragments, a region at the end of the records, and still the same batch.
                Admit(run.scene.ledger, run.bodies[0], Normalized(new Unity.Mathematics.float4(0f, 1f, 0f, 0f)));
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "a cut: collected");
                AssertGpuHoldsWhatIsAdopted(display, "a cut");
                Assert.That(new[] { display.DrawCommandEnd, display.DrawInstanceEnd, display.SideCount }, Is.EqualTo(new[] { 9, 11, 10 }));
                Assert.That(display.BodyBatchForTest, Is.SameAs(batch), "the same batch");
            }
        }
    }
}
