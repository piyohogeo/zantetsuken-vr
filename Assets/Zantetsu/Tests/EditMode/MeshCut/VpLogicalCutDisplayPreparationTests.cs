using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// A camera's stencil preparation working in the display's own scratch: no managed allocation once warmed up, from
    /// the preparation through the upload, whatever the view, and the same cap jobs, volume groups, colours and uploaded
    /// ranges as a cap-job classification of its own gives when asked apart (D-183). Also the refusal of a preparation with no room, two
    /// cameras prepared in turn, a camera let go and taken back, and a call made from inside a preparation.
    /// <para>
    /// The bodies are the truncated pyramids of the colours tests (bottom [-1, 1]², top [-0.5, 0.5]² at y = 2), cut at
    /// y = 1 with the bottom fixed and the top moved far up. **How allocation is measured:** by the <c>GC.Alloc</c>
    /// samples a profiler recorder filtered to this thread counts, read around a loop of preparations only — the eyes
    /// are made, and the display warmed up, before; nothing is logged, asserted or allocated by the test inside the
    /// loop. The recorder is first shown one allocation through the same window and must count it, so a recorder that
    /// counts nothing cannot pass for zero. Alongside, as an observation only, the difference in the managed heap's
    /// used size (<c>Profiler.GetMonoUsedSizeLong</c>) across the same window is recorded: it is not an allocation
    /// count, it is not filtered to a thread, and it is not what the tests hold to zero.
    /// (<c>GC.GetAllocatedBytesForCurrentThread</c> reads nothing in this editor's runtime, a 4096-byte array included,
    /// and is not used.) What is claimed is only that no managed allocation was observed in this editor, over this window.
    /// </para>
    /// </summary>
    public class VpLogicalCutDisplayPreparationTests
    {
        private const int ControlPoints = 8;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int Size = 128;
        private const float WideSeparation = 3f;
        private const int Warmup = 10;
        private const int Iterations = 100;

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, 1.0f), new float3(-1.0f, 0.0f, 1.0f),
            new float3(-0.5f, 2.0f, -0.5f), new float3(0.5f, 2.0f, -0.5f), new float3(0.5f, 2.0f, 0.5f), new float3(-0.5f, 2.0f, 0.5f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
            new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -1f);
        private static readonly float3 k_lowAnchor = new float3(0f, 0.2f, 0f);

        private readonly List<Object> _objects = new List<Object>();
        private int _frame;
        private Action _duringFrameRead;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
            _duringFrameRead = null;
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

        private int Frame()
        {
            _duringFrameRead?.Invoke();
            return _frame;
        }

        // ----- allocation -----------------------------------------------------------------------------------------------

        /// <summary>
        /// One fixed snapshot and one fixed pair of eyes: after warming up, a hundred preparations -- classification,
        /// arrangement and upload together -- show no GC.Alloc sample on this thread. The monoscopic spelling, which
        /// reads the camera's transform and matrices itself, is measured apart.
        /// </summary>
        [Test]
        public void ASteadyPreparation_ShowsNoManagedAllocation()
        {
            AssertTheMeasuresSeeAllocation();
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0.6f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapEye eye = EyeOf(camera);
                for (int i = 0; i < Warmup; i++)
                {
                    Assert.That(display.TryPrepareCamera(camera, eye, eye), Is.True);
                    Assert.That(display.TryPrepareCamera(camera), Is.True);
                }

                int uploads = display.StencilUploads;
                Measurement stereo = Measure(display, camera, new[] { eye }, false, out bool allPrepared);
                Measurement mono = Measure(display, camera, new[] { eye }, true, out bool allPreparedMono);

                TestContext.WriteLine("steady, eyes given: " + stereo);
                TestContext.WriteLine("steady, camera only: " + mono);
                Assert.That(allPrepared && allPreparedMono, Is.True, "every preparation succeeded");
                Assert.That(display.StencilUploads - uploads, Is.EqualTo(2 * Iterations), "each one uploaded");
                Assert.That(stereo.samples, Is.Zero, "no GC.Alloc sample on this thread, eyes given");
                Assert.That(mono.samples, Is.Zero, "no GC.Alloc sample on this thread, camera only");
                Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(2), "and the result is the known one");
                Assert.That(display.HeldPreparationLooks, Is.Zero, "no look is left after a preparation");
                RenderAndRead(display, camera);
            }
        }

        /// <summary>
        /// Within the same capacity the view moves between seeing both caps, one, and none -- an empty arrangement --
        /// and back, over and over: the counts of seen caps and colours go up and down, and still no GC.Alloc sample is
        /// seen on this thread, so nothing was made again at a new size. Each view keeps its known result.
        /// </summary>
        [Test]
        public void ChangingCountsAndAnEmptyArrangement_ShowNoManagedAllocation()
        {
            AssertTheMeasuresSeeAllocation();
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0.6f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapEye both = EyeOf(camera);
                camera.transform.position = new Vector3(-0.5f, 2.5f, 0f);
                camera.orthographicSize = 0.5f;
                VpCapEye one = EyeOf(camera);
                camera.transform.SetPositionAndRotation(new Vector3(0f, 2.5f, 50f), Quaternion.LookRotation(Vector3.forward, Vector3.up));
                VpCapEye none = EyeOf(camera);

                var views = new[] { both, one, none, one, both, none };
                var expected = new[] { 2, 1, 0, 1, 2, 0 };
                for (int v = 0; v < views.Length; v++)
                {
                    Assert.That(display.TryPrepareCamera(camera, views[v], views[v]), Is.True);
                    Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(expected[v]), "view " + v);
                }

                for (int i = 0; i < Warmup; i++)
                {
                    foreach (VpCapEye eye in views)
                    {
                        display.TryPrepareCamera(camera, eye, eye);
                    }
                }

                Measurement changing = Measure(display, camera, views, false, out bool allPrepared);
                TestContext.WriteLine("changing counts: " + changing);
                Assert.That(allPrepared, Is.True);
                Assert.That(changing.samples, Is.Zero, "no GC.Alloc sample on this thread as the counts change");

                // Once more with the empty view, after a fuller one: nothing an earlier arrangement left is uploaded.
                Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(expected[(Iterations - 1) % views.Length]), "the loop's last view");
                Assert.That(display.TryPrepareCamera(camera, none, none), Is.True);
                Assert.That(PreparationOf(display, camera).colours, Is.Zero);
                Assert.That(CountsOf(display, camera).colours, Is.Zero, "the batch holds no colour");
                Assert.That(display.TryGetPreparedColor(camera, 0, out _), Is.False, "and reads none back");
                RenderAndRead(display, camera);
            }
        }

        /// <summary>
        /// Under a limit of one colour, views that fit (one body seen, or none) alternate with a view that does not (both
        /// overlapping bodies): the job, group and colour counts change, every third preparation is refused as
        /// ColorLimitExceeded, and still no GC.Alloc sample is seen on this thread -- a refusal allocates nothing either.
        /// </summary>
        [Test]
        public void RefusalsForTheColourLimit_AmongSuccesses_ShowNoManagedAllocation()
        {
            AssertTheMeasuresSeeAllocation();
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(1), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0.6f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapEye both = EyeOf(camera);
                camera.transform.position = new Vector3(-0.5f, 2.5f, 0f);
                camera.orthographicSize = 0.5f;
                VpCapEye one = EyeOf(camera);
                camera.transform.SetPositionAndRotation(new Vector3(0f, 2.5f, 50f), Quaternion.LookRotation(Vector3.forward, Vector3.up));
                VpCapEye none = EyeOf(camera);

                var views = new[] { one, both, none };
                var expected = new[] { VpStencilPreparationOutcome.Prepared, VpStencilPreparationOutcome.ColorLimitExceeded, VpStencilPreparationOutcome.Prepared };
                for (int v = 0; v < views.Length; v++)
                {
                    display.TryPrepareCamera(camera, views[v], views[v]);
                    Assert.That(PreparationOf(display, camera).outcome, Is.EqualTo(expected[v]), "view " + v);
                }

                for (int i = 0; i < Warmup; i++)
                {
                    foreach (VpCapEye eye in views)
                    {
                        display.TryPrepareCamera(camera, eye, eye);
                    }
                }

                int uploads = display.StencilUploads;
                int prepared = 0;
                Recorder recorder = Open(out long start);
                for (int i = 0; i < Iterations; i++)
                {
                    VpCapEye eye = views[i % views.Length];
                    prepared += display.TryPrepareCamera(camera, eye, eye) ? 1 : 0;
                }

                Measurement measured = Close(recorder, start, Iterations);
                TestContext.WriteLine("successes and refusals: " + measured + ", " + prepared + " prepared");
                Assert.That(Iterations - prepared, Is.EqualTo(Iterations / views.Length), "every third view refused");
                Assert.That(display.StencilUploads - uploads, Is.EqualTo(prepared), "only the successes uploaded");
                Assert.That(measured.samples, Is.Zero, "no GC.Alloc sample on this thread");
                Assert.That(display.HeldPreparationLooks, Is.Zero);
                Assert.That(display.TryPrepareCamera(camera, one, one), Is.True);
                RenderAndRead(display, camera);
            }
        }

        /// <summary>
        /// The same measure over a multi-cut snapshot: two bodies, each cut and published, then each published top cut
        /// again, pending, and the bottoms cut again too -- render fragments under one and under two boundaries, caps
        /// clipped by another boundary, and two registrations classified together. After warming up, a hundred
        /// preparations over two views show no GC.Alloc sample on this thread; the recorder is shown one allocation first.
        /// </summary>
        [Test]
        public void AMultiCutPreparation_ShowsNoManagedAllocation()
        {
            AssertTheMeasuresSeeAllocation();
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                LogicalCutLedger ledger = scene.ledger;
                var tops = new List<LogicalFragmentId>();
                var bottoms = new List<LogicalFragmentId>();
                for (int position = 0; ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
                {
                    Assert.That(ledger.Publish(operation.id, out LogicalFragmentId top, out LogicalFragmentId bottom), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    tops.Add(top);
                    bottoms.Add(bottom);
                }

                foreach (LogicalFragmentId fragment in tops)
                {
                    Assert.That(ledger.Admit(fragment, new float4(1f, 0f, 0f, 0f), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
                    Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                }

                foreach (LogicalFragmentId fragment in bottoms)
                {
                    Assert.That(ledger.Admit(fragment, new float4(0f, 0f, 1f, 0.3f), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
                    Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                }

                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "the second cuts settle");
                Assert.That(display.RenderFragmentCount, Is.EqualTo(8), "the layout: four render fragments per body");
                Assert.That(display.CapRecordCount, Is.EqualTo(16), "two boundaries each");

                Camera camera = TopDown(0.6f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                VpCapEye above = EyeOf(camera);
                camera.transform.position = new Vector3(-0.5f, 2.5f, 0f);
                camera.orthographicSize = 0.5f;
                VpCapEye narrow = EyeOf(camera);
                var views = new[] { above, narrow };
                for (int i = 0; i < Warmup; i++)
                {
                    foreach (VpCapEye eye in views)
                    {
                        Assert.That(display.TryPrepareCamera(camera, eye, eye), Is.True);
                    }
                }

                int uploads = display.StencilUploads;
                Measurement multi = Measure(display, camera, views, false, out bool allPrepared);
                TestContext.WriteLine("multi-cut, two views: " + multi);
                Assert.That(allPrepared, Is.True, "every preparation succeeded");
                Assert.That(display.StencilUploads - uploads, Is.EqualTo(Iterations), "each one uploaded");
                Assert.That(multi.samples, Is.Zero, "no GC.Alloc sample on this thread");
                Assert.That(display.HeldPreparationLooks, Is.Zero, "no look is left after a preparation");
                Assert.That(display.TryPrepareCamera(camera, above, above), Is.True);
                Assert.That(PreparationOf(display, camera).capRecords, Is.EqualTo(16), "every cap asked about");
                AssertTheCapJobsAgree(display, camera, above, 4, "multi-cut, from above");
                RenderAndRead(display, camera);
            }
        }

        // ----- the same result ------------------------------------------------------------------------------------------

        /// <summary>
        /// Every cap record slot of a display in use -- sixteen one-command bodies, two caps each, overlapping their
        /// neighbours on screen -- under a limit of two colours and of four, from several views: the preparation's
        /// counts, every colour's uploaded volume and cap ranges, and every volume command's range, transform and clip,
        /// are the ones a cap-job classification of the test's own gives for the adopted snapshot and its draw ranges.
        /// </summary>
        [Test]
        public void AtFullCapacity_TheArrangementIsTheCapJobClassificationsOwn()
        {
            const int bodies = 16;
            var xs = new float[bodies];
            for (int i = 0; i < bodies; i++)
            {
                xs[i] = (i - (bodies - 1) / 2f) * 1.2f;
            }

            foreach (int limit in new[] { 2, 4 })
            {
                using (Scene scene = CutPyramids(VpStencilTestSettings.Create(limit), bodies, true, xs))
                {
                    VpLogicalCutDisplay display = scene.display;
                    Assert.That(display.CapRecordCount, Is.EqualTo(2 * bodies), "limit " + limit + ": every record slot in use");
                    Assert.That(display.DrawCommandCount, Is.EqualTo(bodies), "and every command");
                    var views = new (Camera camera, string what)[]
                    {
                        (TopDown(0f, 10f), "every body"),
                        (TopDown(-3f, 2f), "a few"),
                        (FromTheSide(), "none seen"),
                    };

                    foreach ((Camera camera, string what) in views)
                    {
                        Assert.That(display.TryRegisterCamera(camera), Is.True);
                        VpCapEye eye = EyeOf(camera);
                        Assert.That(display.TryPrepareCamera(camera, eye, eye), Is.True);
                        AssertTheCapJobsAgree(display, camera, eye, limit, "limit " + limit + ", " + what);
                    }

                    foreach ((Camera camera, _) in views)
                    {
                        RenderAndRead(display, camera);
                    }
                }
            }
        }

        // ----- refusal ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Instance capacities whose derived sizes do not fit an int -- the caps (eight per render fragment, one render
        /// fragment per instance at most), the stencil volume commands (eight per instance), the cap vertices (fourteen
        /// per cap) and their fanned indices (thirty-six per cap) -- and a branch capacity whose walk does not fit, are refused by TryCreate before any GPU buffer,
        /// material or scratch is made: nothing is created, no material appears, and it answers at once. The largest
        /// instance capacity whose sizes all fit is derived without being made, and the logical capacities are passed
        /// through as given.
        /// </summary>
        [Test]
        public void CapacitiesThatDoNotFitAnInt_AreRefusedBeforeAnythingIsMade()
        {
            int fits = int.MaxValue / 288;
            Assert.That(
                VpLogicalCutDisplay.TryDeriveCapacities(1, fits, 7, 11, 5, out VpLogicalCutDisplay.DerivedCapacities derived),
                Is.True, "every size of " + fits + " instances fits");
            Assert.That(derived.stencilCommands, Is.EqualTo(fits * 8), "eight volume commands per instance at most: one per group, eight groups per render fragment");
            Assert.That(derived.renderFragments, Is.EqualTo(fits));
            Assert.That(derived.caps, Is.EqualTo(fits * 8));
            Assert.That(derived.capVertices, Is.EqualTo(fits * 8 * 14));
            Assert.That(derived.capIndices, Is.EqualTo(fits * 8 * 36));
            Assert.That(derived.branches, Is.EqualTo(7), "the logical room is the caller's");
            Assert.That(derived.candidates, Is.EqualTo(11));
            Assert.That(derived.chainDepth, Is.EqualTo(5));

            var overflowing = new (int instances, int branches, string what)[]
            {
                (fits + 1, 4, "the cap indices, 288 times the instances"),
                (int.MaxValue / 112 + 1, 4, "the cap vertices, 112 times"),
                (int.MaxValue / 8 + 1, 4, "the caps and the volume commands, eight times"),
                (int.MaxValue, 4, "the largest int"),
                (1, int.MaxValue / 2, "the branch walk, twice the branches"),
                (0, 4, "no instance"),
                (1, 0, "no branch"),
            };

            Dictionary<int, Material> materials = Materials();
            using (var storage = new VpCpuGeometryStorage(1024, 4096, 16, 64, 64, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 4, 4);
                foreach ((int instances, int branches, string what) in overflowing)
                {
                    Assert.That(VpLogicalCutDisplay.TryDeriveCapacities(1, instances, branches, 4, 4, out _), Is.False, what + ": not derived");
                    int materialsBefore = Resources.FindObjectsOfTypeAll<Material>().Length;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    bool created = VpLogicalCutDisplay.TryCreate(
                        storage, table, new LogicalCutLedger(new LogicalCutIncompleteBudget(2)), materials, null, null,
                        1, instances, branches, 4, 4, VpStencilTestSettings.Create(4), Frame, out VpLogicalCutDisplay display);
                    clock.Stop();
                    Assert.That(created, Is.False, what + ": refused");
                    Assert.That(display, Is.Null, what);
                    Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materialsBefore), what + ": no material made");
                    Assert.That(clock.ElapsedMilliseconds, Is.LessThan(1000), what + ": answered at once, nothing large made");
                }
            }
        }

        /// <summary>
        /// A snapshot with more cap records than a preparation has room for is refused before anything is uploaded or
        /// written: the camera's uploads and writes, and the adopted snapshot, stay as they were, and the camera --
        /// prepared a moment earlier in the same frame -- is no longer prepared, so its draw is refused. With room
        /// again it is prepared as before.
        /// </summary>
        [Test]
        public void NoRoom_IsRefusedBeforeTheUpload_AndLeavesTheCameraUnprepared()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0.6f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                VpStencilCameraCounts before = CountsOf(display, camera);
                Assert.That(before.preparedNow, Is.True, "the layout: prepared");
                int commands = display.DrawCommandCount;
                int records = display.CapRecordCount;
                int sides = display.SideCount;
                int collections = display.SettledCollections;
                int commandUploads = display.CommandUploads;

                display.PreparationRecordLimit = records - 1;
                Assert.That(display.TryPrepareCamera(camera), Is.False, "one record more than there is room for");
                Assert.That(PreparationOf(display, camera).outcome, Is.EqualTo(VpStencilPreparationOutcome.CapacityExceeded), "told apart from the colour limit");

                VpStencilCameraCounts after = CountsOf(display, camera);
                Assert.That(after.uploads, Is.EqualTo(before.uploads), "nothing uploaded");
                Assert.That(after.bufferWrites, Is.EqualTo(before.bufferWrites), "nothing written");
                Assert.That(after.preparedNow, Is.False, "not prepared: the earlier result is not drawn");
                Assert.That(display.DrawCommandCount, Is.EqualTo(commands), "the snapshot is as it was");
                Assert.That(display.CapRecordCount, Is.EqualTo(records));
                Assert.That(display.SideCount, Is.EqualTo(sides));
                Assert.That(display.SettledCollections, Is.EqualTo(collections));
                Assert.That(display.CommandUploads, Is.EqualTo(commandUploads));
                Assert.That(display.IsBroken, Is.False, "an ordinary refusal");
                Assert.That(display.HeldPreparationLooks, Is.Zero);
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera), "its draw is refused");
                Assert.That(display.HasDrawnThisFrame, Is.False, "and nothing was registered");

                display.PreparationRecordLimit = records;
                Assert.That(display.TryPrepareCamera(camera), Is.True, "with room it is prepared again");
                Assert.That(CountsOf(display, camera).uploads, Is.EqualTo(before.uploads + 1));
                RenderAndRead(display, camera);
            }
        }

        // ----- cameras ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Two cameras prepared in turn, several times over, through the one scratch -- then both registered, and only
        /// then each rendered and read: each draws its own arrangement, the wide one two colours and the narrow one
        /// one, with each one's caps where they belong.
        /// </summary>
        [Test]
        public void TwoCamerasPreparedInTurn_EachDrawTheirOwn()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera wide = TopDown(0.6f, 3f);
                Camera narrow = TopDown(-0.5f, 0.5f);
                Assert.That(display.TryRegisterCamera(wide), Is.True);
                Assert.That(display.TryRegisterCamera(narrow), Is.True);
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(display.TryPrepareCamera(wide), Is.True);
                    Assert.That(display.TryPrepareCamera(narrow), Is.True);
                }

                display.Render(0, wide);
                display.Render(0, narrow);
                Color32[] wideImage = Read(wide);
                Color32[] narrowImage = Read(narrow);

                Assert.That(PreparationOf(display, wide).colours, Is.EqualTo(2));
                Assert.That(PreparationOf(display, narrow).colours, Is.EqualTo(1));
                Assert.That(CountsOf(display, wide).initIssues, Is.EqualTo(2), "the wide camera drew two colours");
                Assert.That(CountsOf(display, narrow).initIssues, Is.EqualTo(1), "the narrow one drew one");
                Assert.That(IsRed(At(wideImage, wide, 0f, 0f)), Is.True, "the wide camera caps the first body");
                Assert.That(IsRed(At(wideImage, wide, 1.2f, 0f)), Is.True, "and the second");
                Assert.That(IsRed(At(wideImage, wide, -0.9f, -0.9f)), Is.False, "and no overhang");
                Assert.That(IsRed(At(narrowImage, narrow, -0.5f, 0f)), Is.True, "the narrow camera caps the first body");
                Assert.That(IsRed(At(narrowImage, narrow, -0.9f, 0.45f)), Is.False, "and not the overhang it sees");
            }
        }

        /// <summary>
        /// A camera let go and taken back starts with a new batch: not prepared, nothing uploaded, its draw refused,
        /// until it is prepared again. No preparation leaves a look behind, and after the display is disposed a
        /// preparation is refused and nothing is kept for the camera.
        /// </summary>
        [Test]
        public void ACameraLetGoAndTakenBack_OrADisposedDisplay_KeepsNoEarlierPreparation()
        {
            Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f);
            VpLogicalCutDisplay display = scene.display;
            Camera camera = TopDown(0.6f, 3f);
            try
            {
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                Assert.That(display.HeldPreparationLooks, Is.Zero, "no look outlives the preparation");

                Assert.That(display.TryUnregisterCamera(camera), Is.True, "not drawn, so it may go");
                Assert.That(display.TryGetCameraStencil(camera, out _, out _), Is.False, "nothing is kept for it");
                Assert.That(display.TryRegisterCamera(camera), Is.True, "taken back");
                VpStencilCameraCounts fresh = CountsOf(display, camera);
                Assert.That(fresh.preparedNow, Is.False, "not prepared");
                Assert.That(fresh.uploads, Is.Zero, "a new batch");
                Assert.That(fresh.colours, Is.Zero);
                Assert.That(PreparationOf(display, camera).colours, Is.Zero, "no earlier preparation carried over");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera));

                Assert.That(display.TryPrepareCamera(camera), Is.True);
                Assert.That(PreparationOf(display, camera).colours, Is.EqualTo(2));
                RenderAndRead(display, camera);
            }
            finally
            {
                scene.Dispose();
            }

            Assert.That(display.IsDisposed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => display.TryPrepareCamera(camera));
            Assert.That(display.TryGetCameraStencil(camera, out _, out _), Is.False, "nothing is kept after disposal");
            Assert.That(display.HeldPreparationLooks, Is.Zero);
        }

        /// <summary>
        /// A call into the display from inside a preparation -- here from the frame counter it reads -- is refused
        /// before it changes anything: preparing again, collecting, showing, registering, letting a camera go,
        /// drawing and disposing all throw, and the preparation that was running finishes with one upload.
        /// </summary>
        [Test]
        public void ACallFromInsideAPreparation_IsRefused()
        {
            using (Scene scene = CutPyramids(VpStencilTestSettings.Create(4), 16, false, 0f, 1.2f))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = TopDown(0.6f, 3f);
                Camera other = TopDown(0f, 3f);
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                int uploads = display.StencilUploads;
                int collections = display.SettledCollections;

                var refused = new List<string>();
                var attempts = new (string what, Action call)[]
                {
                    ("prepare", () => display.TryPrepareCamera(camera)),
                    ("collect", () => display.TryBeginFrame()),
                    ("show", () => display.TryShow(default, default, Matrix4x4.identity)),
                    ("register", () => display.TryRegisterCamera(other)),
                    ("let go", () => display.TryUnregisterCamera(camera)),
                    ("draw", () => display.Render(0, camera)),
                    ("dispose", () => display.Dispose()),
                };
                bool entered = false;
                _duringFrameRead = () =>
                {
                    if (!display.IsPreparing || entered)
                    {
                        return;
                    }

                    entered = true;
                    foreach ((string what, Action call) in attempts)
                    {
                        try
                        {
                            call();
                        }
                        catch (InvalidOperationException)
                        {
                            refused.Add(what);
                        }
                    }
                };

                Assert.That(display.TryPrepareCamera(camera), Is.True, "the running preparation finishes");
                _duringFrameRead = null;

                Assert.That(entered, Is.True, "the layout: a call was made from inside");
                Assert.That(refused, Is.EqualTo(new[] { "prepare", "collect", "show", "register", "let go", "draw", "dispose" }));
                Assert.That(display.StencilUploads, Is.EqualTo(uploads + 1), "one upload, the running one's");
                Assert.That(display.SettledCollections, Is.EqualTo(collections), "no collection");
                Assert.That(display.RegisteredCameraCount, Is.EqualTo(1), "no camera taken or let go");
                Assert.That(display.IsDisposed, Is.False);
                Assert.That(display.HasDrawnThisFrame, Is.False);
                Assert.That(CountsOf(display, camera).preparedNow, Is.True);
                RenderAndRead(display, camera);
            }
        }

        // ----- measurement -----------------------------------------------------------------------------------------------

        private readonly struct Measurement
        {
            public Measurement(int samples, long heapUsedDifference, int preparations)
            {
                this.samples = samples;
                this.heapUsedDifference = heapUsedDifference;
                this.preparations = preparations;
            }

            /// <summary>GC.Alloc samples on this thread: the measure of allocation.</summary>
            public readonly int samples;

            /// <summary>
            /// The managed heap's used size after, less before -- an observation, not a count of bytes allocated, and
            /// not filtered to this thread.
            /// </summary>
            public readonly long heapUsedDifference;

            public readonly int preparations;

            public override string ToString()
            {
                return preparations + " preparations: " + samples + " GC.Alloc samples on this thread; managed heap used-size difference "
                    + heapUsedDifference + " (observation only)";
            }
        }

        /// <summary>
        /// <see cref="Iterations"/> preparations over the eyes given in turn, and nothing else, between the two
        /// readings of each measure.
        /// </summary>
        private static Measurement Measure(
            VpLogicalCutDisplay display, Camera camera, VpCapEye[] eyes, bool cameraOnly, out bool allPrepared)
        {
            bool prepared = true;
            int eyeCount = eyes.Length;
            Recorder recorder = Open(out long start);
            for (int i = 0; i < Iterations; i++)
            {
                VpCapEye eye = eyes[i % eyeCount];
                prepared &= cameraOnly ? display.TryPrepareCamera(camera) : display.TryPrepareCamera(camera, eye, eye);
            }

            Measurement measured = Close(recorder, start, Iterations);
            allPrepared = prepared;
            return measured;
        }

        /// <summary>Starts both measures: the recorder, filtered to this thread, and the heap's used size.</summary>
        private static Recorder Open(out long usedAtStart)
        {
            Recorder recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            usedAtStart = Profiler.GetMonoUsedSizeLong();
            return recorder;
        }

        private static Measurement Close(Recorder recorder, long usedAtStart, int preparations)
        {
            long usedAtEnd = Profiler.GetMonoUsedSizeLong();
            recorder.enabled = false;
            recorder.CollectFromAllThreads();
            return new Measurement(recorder.sampleBlockCount, usedAtEnd - usedAtStart, preparations);
        }

        /// <summary>
        /// The positive control: the recorder is shown one allocation through the same window first, and must count
        /// it, so that its reading zero means something. The heap difference seen alongside is only written down.
        /// </summary>
        private static void AssertTheMeasuresSeeAllocation()
        {
            Recorder recorder = Open(out long start);
            var known = new byte[4096];
            Measurement seen = Close(recorder, start, 0);
            GC.KeepAlive(known);
            TestContext.WriteLine("positive control, one 4096-byte array: " + seen);
            Assert.That(seen.samples, Is.GreaterThanOrEqualTo(1), "the recorder counts a known allocation on this thread");
        }

        private static VpCapEye EyeOf(Camera camera)
        {
            return new VpCapEye(camera.transform.position, camera.projectionMatrix * camera.worldToCameraMatrix);
        }

        // ----- a cap-job classification asked apart ---------------------------------------------------------------

        /// <summary>
        /// A cap-job classification of the test's own, made for the adopted snapshot and the draw-range table adopted
        /// with it, for the same eye and settings; then the arrangement it gives, colour by colour -- each of the colour's
        /// volume groups once, one command per command of its representative render fragment's body, then each of the
        /// colour's jobs fanned -- which the preparation's counts, the uploaded colour ranges and every volume command
        /// the display arranged must be.
        /// </summary>
        private static void AssertTheCapJobsAgree(VpLogicalCutDisplay display, Camera camera, VpCapEye eye, int limit, string what)
        {
            VpStencilSettings settings = display.StencilSettings;
            VpMultiCutSnapshot snapshot = display.AdoptedSnapshot;
            var own = new VpCapJobClassification(snapshot.Capacities);
            Assert.That(
                own.TryClassify(snapshot, display.AdoptedGeometries, eye, eye, settings.facingEpsilon, settings.ndcMargin, limit),
                Is.EqualTo(VpCapJobOutcome.Classified), what + ": classified apart");

            var expected = new List<VpStencilCapColor>();
            var volumes = new List<(int renderFragment, VpInstanceClip clip)>();
            int volume = 0;
            int index = 0;
            int capsDrawn = 0;
            for (int colour = 0; colour < own.ColourCount; colour++)
            {
                own.TryGetColour(colour, out VpCapJobColour range);
                int volumeStart = volume;
                int capStart = index;
                for (int p = range.groupStart; p < range.groupStart + range.groupCount; p++)
                {
                    own.TryGetGroupOfColour(p, out int g);
                    own.TryGetVolumeGroup(g, out VpCapVolumeGroup group);
                    int commands = SidesOf(display, group.renderFragment);
                    for (int c = 0; c < commands; c++)
                    {
                        volumes.Add((group.renderFragment, group.volumeClip));
                    }

                    volume += commands;
                }

                for (int p = range.groupStart; p < range.groupStart + range.groupCount; p++)
                {
                    own.TryGetGroupOfColour(p, out int g);
                    own.TryGetVolumeGroup(g, out VpCapVolumeGroup group);
                    for (int k = group.jobStart; k < group.jobStart + group.jobCount; k++)
                    {
                        own.TryGetJobOfGroup(k, out int j);
                        own.TryGetJob(j, out VpCapJob job);
                        display.TryGetCapRecord(job.capIndex, out LogicalCutCapRecord record);
                        capsDrawn++;
                        index += 3 * Math.Max(0, record.vertexCount - 2);
                    }
                }

                expected.Add(new VpStencilCapColor(volumeStart, volume - volumeStart, capStart, index - capStart, Color.red));
            }

            VpStencilPreparation preparation = PreparationOf(display, camera);
            Assert.That(preparation.outcome, Is.EqualTo(VpStencilPreparationOutcome.Prepared), what);
            Assert.That(preparation.capRecords, Is.EqualTo(display.CapRecordCount), what + ": caps asked about");
            Assert.That(preparation.emptyCaps, Is.EqualTo(own.EmptyCapCount), what + ": empty caps");
            Assert.That(preparation.hiddenCaps, Is.EqualTo(own.HiddenCapCount), what + ": hidden caps");
            Assert.That(preparation.jobs, Is.EqualTo(own.JobCount), what + ": jobs");
            Assert.That(preparation.volumeGroups, Is.EqualTo(own.VolumeGroupCount), what + ": volume groups");
            Assert.That(preparation.colours, Is.EqualTo(expected.Count), what + ": colours");
            Assert.That(preparation.volumeCommands, Is.EqualTo(volume), what + ": volume commands");
            Assert.That(preparation.capsDrawn, Is.EqualTo(capsDrawn), what + ": caps drawn");
            Assert.That(CountsOf(display, camera).colours, Is.EqualTo(expected.Count), what + ": the batch's colours");
            for (int c = 0; c < expected.Count; c++)
            {
                Assert.That(display.TryGetPreparedColor(camera, c, out VpStencilCapColor uploaded), Is.True);
                Assert.That(uploaded.volumeStart, Is.EqualTo(expected[c].volumeStart), what + ": colour " + c + " volume start");
                Assert.That(uploaded.volumeCount, Is.EqualTo(expected[c].volumeCount), what + ": colour " + c + " volumes");
                Assert.That(uploaded.capIndexStart, Is.EqualTo(expected[c].capIndexStart), what + ": colour " + c + " cap start");
                Assert.That(uploaded.capIndexCount, Is.EqualTo(expected[c].capIndexCount), what + ": colour " + c + " cap indices");
            }

            Assert.That(display.TryGetPreparedColor(camera, expected.Count, out _), Is.False, what + ": no colour past those");

            // Every volume command: its render fragment's transform and the group's own-face clip, never the render
            // fragment's clip of every selected face.
            Assert.That(display.ArrangedVolumeCount, Is.EqualTo(volumes.Count), what + ": volume commands arranged");
            for (int v = 0; v < volumes.Count; v++)
            {
                Assert.That(display.TryGetArrangedVolume(v, out VpIndirectCommand command, out Matrix4x4 transform, out VpInstanceClip clip), Is.True);
                display.TryGetRenderFragment(volumes[v].renderFragment, out VpMultiCutRenderFragment rf);
                Assert.That(command.instanceCount, Is.EqualTo(1), what + ": volume " + v + " is one instance");
                Assert.That(transform, Is.EqualTo(rf.geometryLocalToWorld), what + ": volume " + v + " placement");
                Assert.That(clip.Equals(volumes[v].clip), Is.True, what + ": volume " + v + " own-face clip");
                Assert.That(clip.PlaneCount, Is.EqualTo(1), what + ": volume " + v + " one face");
            }

            TestContext.WriteLine(
                what + ": " + own.JobCount + " jobs, " + own.VolumeGroupCount + " groups, " + own.HiddenCapCount + " hidden, "
                + expected.Count + " colours, " + volume + " volume commands, " + capsDrawn + " caps");
        }

        /// <summary>How many instances draw render fragment <paramref name="renderFragment"/>: one per command of its body.</summary>
        private static int SidesOf(VpLogicalCutDisplay display, int renderFragment)
        {
            int n = 0;
            for (int i = 0; i < display.SideCount; i++)
            {
                Assert.That(display.TryGetSide(i, out LogicalCutDisplaySide side), Is.True);
                n += side.renderFragment == renderFragment ? 1 : 0;
            }

            return n;
        }

        // ----- fixture -----------------------------------------------------------------------------------------------

        private sealed class Scene : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public LogicalCutLedger ledger;
            public VpLogicalCutDisplay display;
            public Action endFrame;

            public void Dispose()
            {
                endFrame?.Invoke();
                display?.Dispose();
                storage?.Dispose();
            }
        }

        /// <summary>
        /// A display with room for exactly <paramref name="commandCapacity"/> commands showing one pyramid per x, each
        /// cut at its y = 1 with the top moved far up. One-command bodies (<paramref name="oneCommand"/>) put every
        /// face in one material, so each body fills one command and its two caps fill two record slots.
        /// </summary>
        private Scene CutPyramids(VpStencilSettings settings, int commandCapacity, bool oneCommand, params float[] xs)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                endFrame = () => _frame++,
            };
            var table = new VpGeometryReferenceTable(scene.storage, 64, 64);
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    scene.storage, table, scene.ledger, Materials(), null, null, commandCapacity, commandCapacity * 2,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, settings, Frame, out scene.display),
                Is.True,
                "create the display");
            scene.display.Separation = WideSeparation;

            var bodies = new List<LogicalFragmentId>();
            foreach (float x in xs)
            {
                LogicalFragmentId body = scene.ledger.AddFragment(new List<float3> { k_lowAnchor });
                Assert.That(
                    scene.display.TryShow(body, Append(scene.storage, oneCommand), Matrix4x4.Translate(new Vector3(x, 0f, 0f))),
                    Is.True, "show the body at " + x);
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

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, bool oneCommand)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            int submeshCount = oneCommand ? 1 : 2;
            for (int submesh = 0; submesh < submeshCount; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    int faceSubmesh = oneCommand ? 0 : (f < 4 ? 0 : 1);
                    if (faceSubmesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f];
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
            Camera camera = NewCamera("Preparation Top Down Camera");
            camera.orthographicSize = size;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 3f;
            camera.transform.position = new Vector3(centreX, 2.5f, 0f);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            return camera;
        }

        /// <summary>An orthographic camera at the bottoms' mid height, in front of them, looking along +z: no cap is seen.</summary>
        private Camera FromTheSide()
        {
            Camera camera = NewCamera("Preparation Side Camera");
            camera.orthographicSize = 3f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.transform.position = new Vector3(0f, 0.5f, -5f);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            return camera;
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

        private static bool IsRed(Color32 c)
        {
            return c.r > 180 && c.g < 80 && c.b < 80;
        }
    }
}
