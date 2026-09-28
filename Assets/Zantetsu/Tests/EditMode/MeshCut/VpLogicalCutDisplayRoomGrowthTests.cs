using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The display's room grows (<see cref="VpLogicalCutDisplayLimits"/>): a collection that needs more than the room it
    /// was made with grows it, builds again and adopts one whole new snapshot -- the pieces shown before and the new ones
    /// together -- and a need past a limit, or room that cannot be made, is told once and stops the display rather than
    /// leaving an older snapshot drawn. Small first capacities, so a few cuts cross them.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        /// <summary>
        /// Two bodies in a room of two branches, two instances and two commands. Each cut needs more: the collection
        /// grows the room -- the branches, then the instances the render fragments follow -- and adopts in the same
        /// frame, the body already shown and both new sides in the one new snapshot; again for two more cuts. The camera
        /// registered at the small room is prepared and drawn from its new batch, and the objects replaced are released
        /// once their readback has completed.
        /// </summary>
        [Test]
        public void ASmallFirstRoom_Grows_AndTheOldAndNewPiecesAreInOneNewSnapshot()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, VpDisplayTestCapacities.Candidates);
            using (Scene scene = NewGrowableScene(2, 2, 2, limits, 2, 64))
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId a = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                LogicalFragmentId b = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(3f, 0f, 0f))), Is.True);
                Collect(scene);
                Camera camera = Oblique();
                Draw(display, camera);
                Assert.That(display.RoomGrowths, Is.Zero, "the layout: two bodies fit the first room");
                Assert.That(display.RenderFragmentCount, Is.EqualTo(2));

                // A cut: three branches and three render fragments, past the first room of two.
                var (_, plus, minus) = Cut(ledger, a, new float4(0f, 1f, 0f, 0f));
                int uploads = display.CommandUploads;
                Collect(scene);
                Assert.That(display.IsHalted, Is.False);
                Assert.That(display.LastRoomFailure, Is.Null, "nothing was short past a limit");
                Assert.That(display.RoomGrowths, Is.GreaterThan(0), "the room grew");
                Assert.That(display.BranchCapacity, Is.GreaterThanOrEqualTo(3));
                Assert.That(display.InstanceCapacity, Is.GreaterThanOrEqualTo(3));
                Assert.That(display.CommandUploads, Is.EqualTo(uploads + 1), "adopted in the same collection");
                Assert.That(display.RenderFragmentCount, Is.EqualTo(3), "B, and A's two sides");
                RenderFragmentOfRoot(display, b);
                Assert.That(display.StateOf(plus), Is.Not.EqualTo(LogicalCutDisplayState.NotShown));
                Assert.That(display.StateOf(minus), Is.Not.EqualTo(LogicalCutDisplayState.NotShown));
                AssertEveryRenderFragmentHasItsCommand(display);
                Assert.That(display.RetiredGpuObjects, Is.GreaterThan(0), "the body batch and the camera's batch were replaced");
                Draw(display, camera);

                // Two more cuts at once, one of each body: five render fragments.
                Cut(ledger, plus, new float4(1f, 0f, 0f, 0f));
                Cut(ledger, b, new float4(0f, 1f, 0f, 0f));
                Collect(scene);
                Assert.That(display.IsHalted, Is.False);
                Assert.That(display.RenderFragmentCount, Is.EqualTo(5), "A-, A+'s two sides and B's two sides");
                Assert.That(display.InstanceCapacity, Is.GreaterThanOrEqualTo(5));
                Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(5), "the table grew past its first two");
                Assert.That(scene.table.DisplayInstanceCapacity, Is.GreaterThanOrEqualTo(5));
                AssertEveryRenderFragmentHasItsCommand(display);
                Draw(display, camera);
                TestContext.WriteLine(display.DescribeRoom());

                // What was replaced goes once its readback has completed, at a later collection; nothing waited for it.
                AsyncGPUReadback.WaitAllRequests();
                Collect(scene);
                Assert.That(display.RetiredGpuObjects, Is.Zero, "every replaced object released");
                Draw(display, camera);
            }
        }

        /// <summary>
        /// A room of one of everything and a lineage cut five times over before the first collection that sees it:
        /// thirty-two leaves, each with five boundaries. One collection grows the branches, the candidates and the
        /// instances -- more growths than any fixed count of sixteen would have allowed -- and adopts, within the
        /// limits, every leaf in the one new snapshot.
        /// </summary>
        [Test]
        public void ARoomOfOne_GrowsSeveralKindsManyTimesInOneCollection_WithinTheLimits()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, 512);
            using (Scene scene = NewGrowableScene(1, 1, 1, limits, 1, 64, candidates: 1))
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Collect(scene);
                Assert.That(display.RoomGrowths, Is.Zero, "the layout: one body fits a room of one");

                float4[] planes =
                {
                    new float4(1f, 0f, 0f, 0f), new float4(0f, 1f, 0f, 0f), new float4(0f, 0f, 1f, 0f),
                    Normalized(new float4(1f, 1f, 1f, -0.3f)), Normalized(new float4(1f, -1f, 1f, 0.2f)),
                };
                var leaves = new List<LogicalFragmentId> { root };
                foreach (float4 plane in planes)
                {
                    var next = new List<LogicalFragmentId>();
                    foreach (LogicalFragmentId leaf in leaves)
                    {
                        var (_, plus, minus) = Cut(ledger, leaf, plane);
                        next.Add(plus);
                        next.Add(minus);
                    }

                    leaves = next;
                }

                Collect(scene);
                Assert.That(display.IsHalted, Is.False);
                Assert.That(display.LastRoomFailure, Is.Null, display.LastRoomFailure);
                Assert.That(display.RoomGrowths, Is.GreaterThan(16), "more growths in one collection than a fixed sixteen");
                Assert.That(display.RenderFragmentCount, Is.EqualTo(32), "every leaf in the one new snapshot");
                Assert.That(display.BranchCapacity, Is.LessThanOrEqualTo(limits.branches));
                Assert.That(display.CandidateCapacity, Is.LessThanOrEqualTo(limits.candidates));
                Assert.That(display.InstanceCapacity, Is.LessThanOrEqualTo(limits.instances));
                TestContext.WriteLine(display.DescribeRoom());
                Draw(display, Oblique());
            }
        }

        /// <summary>
        /// A side that moves keeps being drawn where it now is after the room has grown: its placement is settled again
        /// by every collection, through the growth and after it, and a body shown before the growth keeps its own.
        /// </summary>
        [Test]
        public void AfterTheRoomGrows_AMovingPieceIsDrawnWhereItNowIs()
        {
            var limits = new VpLogicalCutDisplayLimits(64, 64, 64, VpDisplayTestCapacities.Candidates);
            using (Scene scene = NewGrowableScene(2, 2, 2, limits, 2, 64))
            {
                LogicalCutLedger ledger = scene.ledger;
                VpLogicalCutDisplay display = scene.display;
                LogicalFragmentId a = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                LogicalFragmentId b = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                var placements = new VpTestPlacements().Static(a).Put(b, new Vector3(3f, 0f, 0f));
                display.Placement = placements;
                Collect(scene);

                var (_, plus, minus) = Cut(ledger, a, new float4(0f, 1f, 0f, 0f));
                placements.Put(plus, new Vector3(0f, 1f, 0f)).Static(minus);
                Collect(scene);
                Assert.That(display.RoomGrowths, Is.GreaterThan(0), "the layout: the room grew");
                AssertPlaced(display, a, 1f, new Vector3(0f, 1f, 0f), "at the growth");
                AssertPlaced(display, b, new Vector3(3f, 0f, 0f), "B, shown before the growth");

                // Moved after the growth: drawn where it now is, frame after frame.
                for (int step = 1; step <= 3; step++)
                {
                    placements.Put(plus, new Vector3(0f, 1f + step, 0.5f * step)).Put(b, new Vector3(3f, 0f, step));
                    Collect(scene);
                    AssertPlaced(display, a, 1f, new Vector3(0f, 1f + step, 0.5f * step), "moved, step " + step);
                    AssertPlaced(display, b, new Vector3(3f, 0f, step), "B moved, step " + step);
                }

                // And through a second growth. Every new side stands somewhere said: a side whose placement is not said
                // is not drawn at all.
                var (_, bPlus, bMinus) = Cut(ledger, b, new float4(1f, 0f, 0f, 0f));
                var (_, minusPlus, minusMinus) = Cut(ledger, minus, new float4(0f, 0f, 1f, 0f));
                placements.Put(bPlus, new Vector3(3f, 0f, 3f)).Put(bMinus, new Vector3(3f, 0f, 3f)).Static(minusPlus).Static(minusMinus);
                int growths = display.RoomGrowths;
                placements.Put(plus, new Vector3(0f, 5f, 0f));
                Collect(scene);
                Assert.That(display.RoomGrowths, Is.GreaterThan(growths), "the layout: the room grew again");
                AssertPlaced(display, a, 1f, new Vector3(0f, 5f, 0f), "at the second growth");
                Draw(display, Oblique());
            }
        }

        /// <summary>
        /// A need the room cannot meet -- past a limit, or memory that cannot be had -- is told once to the room failure
        /// handler with what was short, the need, what was held and the limit; the display stops before the frame draws,
        /// stays stopped, and nothing older is left drawing. Disposing gives every reference back. Without a handler the
        /// shortfall is refused as it always was: the previous snapshot keeps drawing and only the record says why.
        /// </summary>
        [Test]
        public void ANeedTheRoomCannotMeet_IsToldOnce_AndStopsTheDisplay()
        {
            var cases = new (string what, VpLogicalCutDisplayLimits limits, bool refuseMemory, bool handled, string said)[]
            {
                ("past the branch limit", new VpLogicalCutDisplayLimits(64, 64, 2, VpDisplayTestCapacities.Candidates), false, true, "branches"),
                ("memory not to be had", new VpLogicalCutDisplayLimits(64, 64, 64, VpDisplayTestCapacities.Candidates), true, true, "refused for a test"),
                ("no handler", new VpLogicalCutDisplayLimits(64, 64, 2, VpDisplayTestCapacities.Candidates), false, false, "branches"),
            };

            foreach ((string what, VpLogicalCutDisplayLimits limits, bool refuseMemory, bool handled, string said) in cases)
            {
                using (Scene scene = NewGrowableScene(2, 2, 2, limits, 2, 64))
                {
                    LogicalCutLedger ledger = scene.ledger;
                    VpLogicalCutDisplay display = scene.display;
                    var told = new List<string>();
                    if (handled)
                    {
                        display.RoomFailureHandler = told.Add;
                    }

                    if (refuseMemory)
                    {
                        display.FailRoomAllocationForTest = () => true;
                    }

                    LogicalFragmentId a = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                    LogicalFragmentId b = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                    Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True, what);
                    Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.Translate(new Vector3(3f, 0f, 0f))), Is.True, what);
                    Collect(scene);
                    Camera camera = Oblique();
                    Draw(display, camera);
                    Drawn before = Capture(display);
                    int uploads = display.CommandUploads;
                    int references = scene.table.LiveDisplayInstanceCount;

                    Cut(ledger, a, new float4(0f, 1f, 0f, 0f));
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.False, what + ": not adopted");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), what + ": nothing uploaded");
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.EqualTo(references), what + ": no reference left taken");
                    Assert.That(display.LastRoomFailure, Is.Not.Null, what + ": recorded");
                    StringAssert.Contains(said, display.LastRoomFailure, what);
                    StringAssert.Contains("needed", display.LastRoomFailure, what);
                    StringAssert.Contains("held", display.LastRoomFailure, what);
                    StringAssert.Contains("limit", display.LastRoomFailure, what);
                    TestContext.WriteLine(what + ": " + display.LastRoomFailure);

                    if (!handled)
                    {
                        Assert.That(display.IsHalted, Is.False, what + ": refused as it always was");
                        AssertSameShape(before, Capture(display), what + ": the previous snapshot, whole");
                        Draw(display, camera);
                        continue;
                    }

                    Assert.That(told.Count, Is.EqualTo(1), what + ": told once");
                    StringAssert.Contains(said, told[0], what);
                    Assert.That(display.IsHalted, Is.True, what + ": stopped");
                    Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RoomNotEstablished), what);
                    Assert.That(display.IsFrameOpen, Is.False, what + ": nothing older is drawn in this frame");
                    Assert.Throws<System.InvalidOperationException>(() => display.TryPrepareCamera(camera), what + ": no preparation");

                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.False, what + ": still stopped");
                    Assert.That(display.TryShow(ledger.AddFragment(), AppendCube(scene.storage, false), Matrix4x4.identity), Is.False, what);
                    Assert.That(told.Count, Is.EqualTo(1), what + ": still told once");

                    _frame++;
                    display.Dispose();
                    Assert.That(scene.table.LiveDisplayInstanceCount, Is.Zero, what + ": every reference back");
                    Assert.That(scene.table.LiveGeometryCount, Is.Zero, what + ": and every registration");
                }
            }
        }

        /// <summary>
        /// A body whose commands would take the display past its instance limit is refused where it is taken in, and the
        /// need is told once.
        /// </summary>
        [Test]
        public void ABodyPastTheInstanceLimit_IsRefused_AndTold()
        {
            // The first body takes one instance and keeps room for a second render fragment: two, the limit.
            var limits = new VpLogicalCutDisplayLimits(64, 2, 64, VpDisplayTestCapacities.Candidates);
            using (Scene scene = NewGrowableScene(2, 2, 2, limits, 2, 64))
            {
                var told = new List<string>();
                scene.display.RoomFailureHandler = told.Add;
                LogicalCutLedger ledger = scene.ledger;
                Assert.That(scene.display.TryShow(ledger.AddFragment(), AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(scene.display.TryShow(ledger.AddFragment(), AppendCube(scene.storage, false), Matrix4x4.identity), Is.False);
                Assert.That(told.Count, Is.EqualTo(1));
                StringAssert.Contains("draw instances", told[0]);
                Assert.That(scene.display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RoomNotEstablished));
            }
        }

        // ----- helpers --------------------------------------------------------------------------------------------------

        /// <summary>A display that starts at the given room and may grow to <paramref name="limits"/>, over a table that grows too.</summary>
        private Scene NewGrowableScene(
            int commands, int instances, int branches, VpLogicalCutDisplayLimits limits, int tableInstances, int tableInstanceLimit,
            int candidates = VpDisplayTestCapacities.Candidates)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                endFrame = () => _frame++,
            };
            scene.table = new VpGeometryReferenceTable(scene.storage, 16, tableInstances, 64, tableInstanceLimit);
            Assert.That(
                VpLogicalCutDisplay.TryCreateCore(
                    scene.storage, scene.table, scene.ledger, Materials(), null, null, commands, instances, branches,
                    candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(4),
                    () => _frame, scene.storage.CommittedVertexCapacity, scene.storage.CommittedIndexCapacity,
                    out scene.display, limits),
                Is.True, "create the display");
            return scene;
        }

        private static void AssertEveryRenderFragmentHasItsCommand(VpLogicalCutDisplay display)
        {
            int instances = 0;
            for (int c = 0; c < display.DrawCommandCount; c++)
            {
                Assert.That(display.TryGetDrawCommand(c, out VpIndirectCommand command), Is.True);
                instances += command.instanceCount;
            }

            Assert.That(instances, Is.EqualTo(display.RenderFragmentCount), "one instance per render fragment of these one-command bodies");
        }

        private static void AssertPlaced(VpLogicalCutDisplay display, LogicalFragmentId source, float side, Vector3 at, string what)
        {
            Assert.That(display.TryGetRenderFragment(RenderFragmentOf(display, source, side), out VpMultiCutRenderFragment rf), Is.True);
            Assert.That(Vector3.Distance(rf.geometryLocalToWorld.GetColumn(3), at), Is.LessThan(Tolerance), what);
        }

        private static void AssertPlaced(VpLogicalCutDisplay display, LogicalFragmentId root, Vector3 at, string what)
        {
            Assert.That(display.TryGetRenderFragment(RenderFragmentOfRoot(display, root), out VpMultiCutRenderFragment rf), Is.True);
            Assert.That(Vector3.Distance(rf.geometryLocalToWorld.GetColumn(3), at), Is.LessThan(Tolerance), what);
        }
    }
}
