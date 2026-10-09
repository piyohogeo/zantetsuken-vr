using System;
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
    /// The clip list's own selection as the display makes it (DESIGN 4.5.9): judged for each camera when that camera's
    /// GPU selection is issued, from the list alone; handed to whoever draws the list; written into Unity's entries of
    /// that camera's view. And what a display does with a route that cannot draw clipped instances apart: made for
    /// such a route it draws as before the separation, materials and selection alike; given one afterwards it refuses.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private const string CasterShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";

        // A display that selects on the GPU, with both caster materials, over a cube cut once and not yet committed:
        // its two sides are the clip list's two entries, the positive one carried away along the plane's normal.
        private Scene NewClippedScene(
            Action<VpGpuCullSetup> setup, out LogicalFragmentId positive, out LogicalFragmentId negative, out VpTestPlacements placements,
            bool cut = true)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                endFrame = () => _frame++,
            };
            scene.table = new VpGeometryReferenceTable(scene.storage, 16, 32);
            VpStencilSettings stencil = VpStencilTestSettings.Create(4);
            Assert.That(VpGpuCullSetup.TryCreate(stencil.cameraCapacity, out VpGpuCullSetup culling, out string failure), Is.True, failure);
            setup?.Invoke(culling);
            Shader caster = Shader.Find(CasterShaderName);
            Assert.That(caster, Is.Not.Null, "the VP shadow caster shader");
            Material shadow = Track(new Material(caster));
            Material provisional = Track(new Material(caster));
            provisional.SetFloat("_Cull", 0f);
            Assert.That(
                VpLogicalCutDisplay.TryCreateCore(
                    scene.storage, scene.table, scene.ledger, Materials(), shadow, provisional, 16, 16,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, stencil,
                    () => _frame, scene.storage.CommittedVertexCapacity, scene.storage.CommittedIndexCapacity, out scene.display,
                    default, null, culling),
                Is.True, "the display is made: " + VpLogicalCutDisplay.LastCreationFailure);

            LogicalFragmentId root = scene.ledger.AddFragment(new List<float3> { new float3(0.9f, -0.9f, -0.5f), new float3(0f, 0f, -0.5f) });
            Assert.That(scene.display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
            placements = new VpTestPlacements();
            scene.display.Placement = placements;
            positive = default;
            negative = default;
            if (cut)
            {
                float4 diagonal = Normalized(new float4(1f, 1f, 1f, 0f));
                (_, positive, negative) = Cut(scene.ledger, root, diagonal);
                placements.Put(positive, Matrix4x4.Translate(new Vector3(diagonal.x, diagonal.y, diagonal.z) * CarriedAway)).Static(negative);
            }
            else
            {
                placements.Static(root);
            }

            Collect(scene);
            return scene;
        }

        private static VpCullConditions ConditionsOf(Camera camera)
        {
            var conditions = new VpCullConditions();
            conditions.KeepEverything();
            conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
            conditions.eyeCount = 1;
            return conditions;
        }

        // One camera's draws registered for the frame that is open.
        private static void RegisterDraws(VpLogicalCutDisplay display, Camera camera)
        {
            Assert.That(display.TryPrepareCamera(camera), Is.True, "prepared");
            display.Render(0, camera);
        }

        // casterVolume: the one split's volume, a box of that half size about that centre; null gives no split (every
        // caster is then kept).
        private static bool IssueSelection(VpLogicalCutDisplay display, Camera camera, Bounds? casterVolume = null)
        {
            using (var commands = new CommandBuffer { name = "VP Clip Selection Display Test" })
            {
                VpCullConditions conditions = ConditionsOf(camera);
                if (casterVolume.HasValue)
                {
                    Vector3 centre = casterVolume.Value.center, half = casterVolume.Value.extents;
                    conditions.SetShadowPlane(0, 0, new Plane(Vector3.right, -(centre.x - half.x)));
                    conditions.SetShadowPlane(0, 1, new Plane(Vector3.left, centre.x + half.x));
                    conditions.SetShadowPlane(0, 2, new Plane(Vector3.forward, -(centre.z - half.z)));
                    conditions.SetShadowPlane(0, 3, new Plane(Vector3.back, centre.z + half.z));
                    conditions.SetShadowPlane(0, 4, new Plane(Vector3.up, -(centre.y - half.y)));
                    conditions.SetShadowPlane(0, 5, new Plane(Vector3.down, centre.y + half.y));
                    conditions.shadowPlaneCounts = new Vector4(6f, 0f, 0f, 0f);
                    conditions.shadowSplitCount = 1;
                }

                bool issued = display.IssueCull(commands, camera, conditions);
                Graphics.ExecuteCommandBuffer(commands);
                return issued;
            }
        }

        private static int[] EntryCounts(GraphicsBuffer buffer, int count)
        {
            var read = new GraphicsBuffer.IndirectDrawIndexedArgs[count];
            buffer.GetData(read, 0, 0, count);
            var counts = new int[count];
            for (int i = 0; i < count; i++) counts[i] = (int)read[i].instanceCount;
            return counts;
        }

        private static int SelectionKeptBodies(VpLogicalCutDisplay display, Camera camera)
        {
            VpIndexedIndirectDrawBatch batch = display.BodyBatchForTest;
            var read = new GraphicsBuffer.IndirectDrawIndexedArgs[batch.CommandCount];
            batch.CullForwardArguments(display.CullViewForTest(camera)).GetData(read, 0, 0, read.Length);
            int kept = 0;
            for (int i = 0; i < read.Length; i++) kept += (int)read[i].instanceCount;
            return kept;
        }

        /// <summary>
        /// **The clip list is judged for each camera, when that camera's selection is issued, and Unity's entries are
        /// written for that camera alone.** One camera sees the two clipped sides and one looks the other way. Until
        /// a camera's selection is issued nothing is known of its draws (everything is kept); then the seeing camera
        /// keeps both bodies and the other none, whose view's body entries are written zero while the first's stay
        /// whole. The camera turned back has its counts back. A side that only moves (no list is built or sent) out
        /// of the view is judged where it now stands. Before any cut there is no list: nothing is judged, made or sent.
        /// <para>
        /// **A frame whose selection is not issued draws every entry, in the entries themselves** (TL, 2026-10-09): the
        /// body and the caster entries a frame's selection wrote zero are whole again once the next frame's draws are
        /// registered, whether or not a selection then comes -- not only the CPU's judgement says "everything kept".
        /// </para>
        /// </summary>
        [Test]
        public void SeparateClipDraws_TheClipListIsJudgedForEachCamera_WhenItsSelectionIsIssued_AndUnitysEntriesAreThatCamerasOwn()
        {
            // No cut: nothing clipped.
            using (Scene whole = NewClippedScene(null, out _, out _, out _, false))
            {
                Camera camera = Oblique();
                Assert.That(whole.display.TryRegisterCamera(camera), Is.True);
                RegisterDraws(whole.display, camera);
                Assert.That(IssueSelection(whole.display, camera), Is.True);
                Assert.That(whole.display.SeparatesClipDraws, Is.True, whole.display.ClipSeparationRefusal);
                Assert.That(
                    new[] { whole.display.ClipDrawCount, whole.display.ClipCullRuns, whole.display.ClipListUploads, whole.display.ClipArgumentWrites, whole.display.ClipArgumentBytes, whole.display.ClipBodyIssues, whole.display.ClipCasterIssues },
                    Is.EqualTo(new long[7]), "no clipped instance: no list, no judgement, no draw and nothing sent");
                Assert.That(whole.display.ClipSelectionForTest(camera), Is.Null);
                Assert.That(SelectionKeptBodies(whole.display, camera), Is.EqualTo(1), "the whole body is the selection's own");
            }

            using (Scene scene = NewClippedScene(null, out LogicalFragmentId positive, out _, out VpTestPlacements placements))
            {
                VpLogicalCutDisplay display = scene.display;
                Assert.That(display.SeparatesClipDraws, Is.True, display.ClipSeparationRefusal);
                Assert.That(display.ClipDrawCount, Is.EqualTo(2), "the two clipped sides");
                Camera seeing = Oblique();
                Camera away = Oblique();
                Quaternion towards = away.transform.rotation;
                away.transform.rotation = Quaternion.LookRotation(-away.transform.forward, Vector3.up);
                Assert.That(display.TryRegisterCamera(seeing), Is.True);
                Assert.That(display.TryRegisterCamera(away), Is.True);
                int seeingView = display.CullViewForTest(seeing), awayView = display.CullViewForTest(away);
                Assert.That(seeingView, Is.Not.EqualTo(awayView));

                RegisterDraws(display, seeing);
                RegisterDraws(display, away);
                Assert.That(new long[] { display.ClipBodyIssues, display.ClipCasterIssues, display.ClipListUploads }, Is.EqualTo(new long[] { 2, 2, 1 }), "one body run and one caster run a camera; the list sent once");
                VpClipSelection seen = display.ClipSelectionForTest(seeing), unseen = display.ClipSelectionForTest(away);
                Assert.That(new[] { seen != null, unseen != null, !ReferenceEquals(seen, unseen) }, Is.EqualTo(new[] { true, true, true }), "a judgement's slot a camera");
                Assert.That(new[] { seen.Valid, unseen.Valid, unseen.BodyKept(0), unseen.BodyKept(1) }, Is.EqualTo(new[] { false, false, true, true }),
                    "the draws are registered and no selection is issued yet: nothing is known, everything is kept");
                VpIndexedIndirectDrawBatch batch = display.BodyBatchForTest;
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(awayView), 2), Is.EqualTo(new[] { 1, 1 }), "and the entries registered draw every one");

                Assert.That(IssueSelection(display, seeing), Is.True);
                Assert.That(new[] { seen.Valid, unseen.Valid }, Is.EqualTo(new[] { true, false }), "the one camera's selection is the one camera's judgement");
                Assert.That(IssueSelection(display, away), Is.True);
                Assert.That(new[] { seen.Count, seen.BodyKeptCount, seen.CasterKeptCount, unseen.Count, unseen.BodyKeptCount, unseen.CasterKeptCount },
                    Is.EqualTo(new[] { 2, 2, 2, 2, 0, 2 }), "the seeing camera keeps both bodies, the other none; every caster is kept (no split is given)");
                Assert.That(SelectionKeptBodies(display, seeing), Is.Zero, "the GPU selection keeps neither clipped side: they are the list's");
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(seeingView), 2), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(awayView), 2), Is.EqualTo(new[] { 0, 0 }), "the other camera's view alone is written zero");
                Assert.That(EntryCounts(batch.ClipShadowArgumentsForTest(awayView), 2), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(
                    new[] { display.ClipCullRuns, display.ClipCullEntries, display.ClipCullBodyKept, display.ClipCullCasterKept, display.ClipSelectionBytes },
                    Is.EqualTo(new long[] { 2, 4, 2, 4, 2 * VpGpuCullSetup.UnityArgumentStride }), "two judgements of two entries; one write, of the one view's body entries");
                Assert.That(display.ClipCullTicks, Is.GreaterThan(0L), "the time is counted");

                // The same again. The list is not sent. The view whose selection wrote zeros is put back whole when its
                // draws are registered -- no selection has been issued for them yet -- and written zero again by the
                // selection that then comes; the view that held no zero is written nothing at all.
                Collect(scene);
                long bytes = display.ClipSelectionBytes, writes = display.ClipArgumentWrites, uploads = display.ClipListUploads, restored = display.ClipRestoreBytes;
                RegisterDraws(display, seeing);
                Assert.That(new[] { display.ClipArgumentWrites - writes, display.ClipRestoreBytes - restored }, Is.EqualTo(new long[2]), "the seeing camera's entries held no zero: nothing is put back");
                RegisterDraws(display, away);
                Assert.That(new[] { seen.Valid, unseen.Valid }, Is.EqualTo(new[] { false, false }), "a new frame's draws");
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(awayView), 2), Is.EqualTo(new[] { 1, 1 }), "registered, no selection yet: the entries draw every one again");
                Assert.That(new[] { display.ClipArgumentWrites - writes, display.ClipRestoreBytes - restored }, Is.EqualTo(new long[] { 1, 2 * VpGpuCullSetup.UnityArgumentStride }), "one write: the body entries put back");
                Assert.That(IssueSelection(display, seeing) && IssueSelection(display, away), Is.True);
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(awayView), 2), Is.EqualTo(new[] { 0, 0 }), "and the selection that comes writes its zeros again");
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(seeingView), 2), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(
                    new[] { display.ClipSelectionBytes - bytes, display.ClipArgumentWrites - writes, display.ClipListUploads - uploads },
                    Is.EqualTo(new long[] { 2 * VpGpuCullSetup.UnityArgumentStride, 2, 0 }), "the away view: put back, then written by its selection; the seeing view: nothing; no list");
                bytes = display.ClipSelectionBytes;

                // The camera turned back: its counts are back.
                away.transform.rotation = towards;
                Collect(scene);
                restored = display.ClipRestoreBytes;
                RegisterDraws(display, seeing);
                RegisterDraws(display, away);
                Assert.That(IssueSelection(display, seeing) && IssueSelection(display, away), Is.True);
                Assert.That(unseen.BodyKeptCount, Is.EqualTo(2));
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(awayView), 2), Is.EqualTo(new[] { 1, 1 }), "written zero before, drawn again");
                Assert.That(
                    new[] { display.ClipRestoreBytes - restored, display.ClipSelectionBytes - bytes },
                    Is.EqualTo(new long[] { 2 * VpGpuCullSetup.UnityArgumentStride, 0 }), "put back when its draws were registered; its selection, which keeps both, has nothing to write");

                // One side only moves, to behind the cameras: no list is built or sent, and it is judged where it stands.
                long builds = display.ClipDrawListBuildsForTest;
                uploads = display.ClipListUploads;
                placements.Put(positive, Matrix4x4.Translate(new Vector3(2.5f, 5f, -40f)));
                Collect(scene);
                RegisterDraws(display, seeing);
                Assert.That(IssueSelection(display, seeing), Is.True);
                Assert.That(new[] { display.ClipDrawListBuildsForTest - builds, display.ClipListUploads - uploads }, Is.EqualTo(new long[2]), "a move builds and sends no list");
                Assert.That(seen.BodyKeptCount, Is.EqualTo(1), "the side carried behind the camera is not kept");
                int[] moved = EntryCounts(batch.ClipForwardArgumentsForTest(seeingView), 2);
                Array.Sort(moved);
                Assert.That(moved, Is.EqualTo(new[] { 0, 1 }));

                // The casters too: a split's volume far from both sides leaves no caster kept.
                Collect(scene);
                RegisterDraws(display, seeing);
                Assert.That(IssueSelection(display, seeing, new Bounds(new Vector3(0f, 0f, 500f), new Vector3(2f, 2f, 2f))), Is.True);
                Assert.That(new[] { seen.BodyKeptCount, seen.CasterKeptCount }, Is.EqualTo(new[] { 1, 0 }));
                int[] body = EntryCounts(batch.ClipForwardArgumentsForTest(seeingView), 2);
                Array.Sort(body);
                Assert.That(body, Is.EqualTo(new[] { 0, 1 }));
                Assert.That(EntryCounts(batch.ClipShadowArgumentsForTest(seeingView), 2), Is.EqualTo(new[] { 0, 0 }), "no caster is drawn");

                // A camera whose selection is not issued in a frame. Its judgement is not valid: everything is kept --
                // and the entries its draws read say so too: the body entry and the two caster entries written zero by
                // the frame before are whole again when the draws are registered.
                Collect(scene);
                restored = display.ClipRestoreBytes;
                bytes = display.ClipSelectionBytes;
                long runs = display.ClipCullRuns;
                RegisterDraws(display, seeing);
                Assert.That(new[] { seen.Valid, seen.BodyKept(0), seen.BodyKept(1), seen.CasterKept(0), seen.CasterKept(1) }, Is.EqualTo(new[] { false, true, true, true, true }));
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(seeingView), 2), Is.EqualTo(new[] { 1, 1 }), "no selection: every body entry is drawn");
                Assert.That(EntryCounts(batch.ClipShadowArgumentsForTest(seeingView), 2), Is.EqualTo(new[] { 1, 1 }), "and every caster entry");
                Assert.That(
                    new[] { display.ClipRestoreBytes - restored, display.ClipSelectionBytes - bytes, display.ClipCullRuns - runs },
                    Is.EqualTo(new long[] { 2 * 2 * VpGpuCullSetup.UnityArgumentStride, 0, 0 }), "both of the view's buffers put back; no selection was made or written");

                // And the frame after that, still with no selection: nothing is left to put back. The display tells,
                // once, that the camera's last frame was drawn without its selection -- the fault is whoever puts the
                // selection in the frame's; what the entries then draw is everything.
                Collect(scene);
                restored = display.ClipRestoreBytes;
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("without its GPU selection having been issued"));
                long missed = display.CullSelectionsMissed;
                RegisterDraws(display, seeing);
                Assert.That(display.CullSelectionsMissed - missed, Is.EqualTo(1L), "the frame drawn with no selection is counted");
                Assert.That(display.ClipRestoreBytes - restored, Is.Zero);
                Assert.That(EntryCounts(batch.ClipForwardArgumentsForTest(seeingView), 2), Is.EqualTo(new[] { 1, 1 }));
            }
        }

        private sealed class PlainSink : IVpNativeDrawSink
        {
            public readonly List<Material> bodies = new List<Material>();
            public int casters;

            public void AddBody(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer) => bodies.Add(material);

            public void AddCasters(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer) => casters++;

            public void Retire(GraphicsBuffer buffer) => buffer.Dispose();

            public VpNativeDrawRelease.IRingWait IssuedEventsWait() => null;
        }

        private sealed class ListSink : IVpNativeDrawSink, IVpNativeClipListDrawSink
        {
            public readonly List<Material> bodies = new List<Material>();
            public readonly List<(int first, int count, VpClipSelection selection)> clipBodies = new List<(int, int, VpClipSelection)>();
            public readonly List<(int first, int count, VpClipSelection selection)> clipCasters = new List<(int, int, VpClipSelection)>();
            public bool draws;

            public bool DrawsClipLists => draws;

            public void AddBody(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer) => bodies.Add(material);

            public void AddCasters(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer)
            {
            }

            public void AddClipBodyRun(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int firstEntry, int entryCount, int layer, VpClipSelection selection)
                => clipBodies.Add((firstEntry, entryCount, selection));

            public void AddClipCasterRun(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int firstEntry, int entryCount, int layer, VpClipSelection selection)
                => clipCasters.Add((firstEntry, entryCount, selection));

            public void Retire(GraphicsBuffer buffer) => buffer.Dispose();

            public VpNativeDrawRelease.IRingWait IssuedEventsWait() => null;
        }

        /// <summary>
        /// **A display that draws its clipped instances apart refuses a route that cannot draw them** (DESIGN 4.5.9):
        /// its ordinary draws evaluate no clip, so the clipped sides cannot be given back to them. A route that takes
        /// neither a clip list nor direct clip draws, and one whose variants for clip lists are not in the build, are
        /// refused before anything is registered -- told, not drawn wrong. A route that draws clip lists is given the
        /// list's runs with the camera's judgement, which it reads once the selection is issued.
        /// </summary>
        [Test]
        public void SeparateClipDraws_ARouteThatCannotDrawClippedInstances_IsRefused_AndOneThatCanIsGivenTheListWithItsJudgement()
        {
            using (Scene scene = NewClippedScene(null, out _, out _, out _))
            {
                VpLogicalCutDisplay display = scene.display;
                Camera camera = Oblique();
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                Assert.That(display.SeparatesClipDraws, Is.True);
                Assert.That(display.ClipDrawRefusal(null), Is.Null, "Unity's own calls draw the list");

                var plain = new PlainSink();
                Assert.That(display.ClipDrawRefusal(plain), Does.Contain("neither a clip list nor direct clip draws"));
                display.NativeDrawSink = plain;
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => display.Render(0, camera));
                Assert.That(refused.Message, Does.Contain("clipped instances apart").And.Contain("ClipSeparationUnavailable"), "the refusal says what to do");
                Assert.That(new[] { plain.bodies.Count, plain.casters }, Is.EqualTo(new[] { 0, 0 }), "nothing was registered: no body without its clip");

                var list = new ListSink { draws = false };
                Assert.That(display.ClipDrawRefusal(list), Does.Contain("does not draw clip lists"));
                display.NativeDrawSink = list;
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera));
                Assert.That(new[] { list.bodies.Count, list.clipBodies.Count }, Is.EqualTo(new[] { 0, 0 }));

                list.draws = true;
                Assert.That(display.ClipDrawRefusal(list), Is.Null);
                display.Render(0, camera);
                Assert.That(list.bodies.Count, Is.GreaterThan(0));
                foreach (Material material in list.bodies)
                {
                    Assert.That(material.IsKeywordEnabled(VpGpuCullSetup.NoClipKeyword), Is.True, "the selection's own draws are of the variant with no clip");
                }

                VpClipSelection selection = display.ClipSelectionForTest(camera);
                Assert.That(list.clipBodies, Is.EqualTo(new[] { (0, 2, selection) }), "the list's one run of two entries, with the camera's judgement");
                Assert.That(list.clipCasters, Is.EqualTo(new[] { (0, 2, selection) }));
                Assert.That(selection.Valid, Is.False, "not judged until the selection is issued");
                Assert.That(IssueSelection(display, camera), Is.True);
                Assert.That(new[] { selection.Valid, selection.BodyKept(0), selection.BodyKept(1) }, Is.EqualTo(new[] { true, true, true }), "read by the route when it issues");
                Assert.That(SelectionKeptBodies(display, camera), Is.Zero, "the GPU selection leaves the clipped sides out");
            }
        }

        /// <summary>
        /// **A display made for a route that cannot draw clipped instances apart draws as before the separation**
        /// (DESIGN 4.5.9): the setup says so before the display is made, and the display's materials evaluate the clip
        /// record, the GPU selection keeps the clipped sides, no clip list is judged, drawn or sent, and the reason is
        /// kept. Such a display is drawn by a route that takes no clip list at all.
        /// </summary>
        [Test]
        public void ADisplayMadeForARouteThatCannotSeparate_DrawsAsBeforeTheSeparation_MaterialsAndSelectionAlike()
        {
            const string reason = "the test's route draws no clipped instance apart";
            using (Scene scene = NewClippedScene(setup => setup.ClipSeparationUnavailable = reason, out _, out _, out _))
            {
                VpLogicalCutDisplay display = scene.display;
                Assert.That(display.SeparatesClipDraws, Is.False);
                Assert.That(display.ClipSeparationRefusal, Does.StartWith(reason), "the reason is kept for the log");
                Assert.That(display.ClipDrawCount, Is.EqualTo(2), "the layout: two clipped sides");
                Camera camera = Oblique();
                Assert.That(display.TryRegisterCamera(camera), Is.True);
                var plain = new PlainSink();
                Assert.That(display.ClipDrawRefusal(plain), Is.Null, "nothing to refuse: this display's draws evaluate the clip themselves");
                display.NativeDrawSink = plain;
                RegisterDraws(display, camera);
                Assert.That(plain.bodies.Count, Is.GreaterThan(0));
                foreach (Material material in plain.bodies)
                {
                    Assert.That(
                        new[] { material.IsKeywordEnabled(VpGpuCullSetup.Keyword), material.IsKeywordEnabled(VpGpuCullSetup.NoClipKeyword) },
                        Is.EqualTo(new[] { true, false }), "the selection's draws read the selection and evaluate the clip record");
                }

                Assert.That(plain.casters, Is.GreaterThan(0));
                Assert.That(IssueSelection(display, camera), Is.True);
                Assert.That(SelectionKeptBodies(display, camera), Is.EqualTo(2), "the GPU selection keeps the two clipped sides");
                Assert.That(
                    new[] { display.ClipCullRuns, display.ClipListUploads, display.ClipArgumentWrites, display.ClipBodyIssues, display.ClipCasterIssues },
                    Is.EqualTo(new long[5]), "no clip list is judged, sent or drawn");
                Assert.That(display.ClipSelectionForTest(camera), Is.Null);
            }
        }
    }
}
