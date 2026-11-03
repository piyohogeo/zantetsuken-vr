using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        /// <summary>
        /// **The skin draw takes a crowd's slot (DESIGN 4.5.12).** With the skin draw asked for by name, a world whose
        /// bodies go through the GPU selection (the Editor: Unity's calls) makes it; a dormant slot is in it no more than
        /// its renderer draws; activated, the slot is an individual of it and its renderer stays off; its palette is
        /// gathered by the frame's update; the hit's cut takes it out in the same call that turns the renderer off (the
        /// cut's display begins), and nothing of it is drawn twice; prepared again and reused, it is taken again; and
        /// the world's end releases the display. With the renderers asked for, nothing of this happens.
        /// </summary>
        [UnityTest]
        public IEnumerator ACrowdSlot_IsDrawnByTheSkinDraw_LeavesItAtItsCut_AndIsTakenAgainWhenReused()
        {
            VpSkinDrawRequest before = VpSkinDrawSetup.Request;
            VpSkinDrawSetup.Request = VpSkinDrawRequest.SkinDraw;
            try
            {
                ColdWorld();
                Assert.That(coldWorld.BodyRoute, Is.EqualTo("VP3C"), coldWorld.BodyRouteReason);
                Assert.That(coldWorld.SkinRoute, Is.EqualTo("SKIN24-UNITY"), coldWorld.SkinRouteReason);
                Assert.That(coldWorld.DrawsSkins, Is.True);
                Assert.That(coldWorld.SkinDrawMaterial, Is.Not.Null);
                VpSkinDisplay skins = coldWorld.SkinDisplay;
                Assert.That(skins.NativeArguments, Is.False, "drawn by Unity's calls in the Editor");
                coldWorld.Driver.RemainingMainSeconds = () => 1.0;
                SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
                ColdTrack(setup);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                SkinnedMeshRenderer skin = slot.Renderer;
                yield return null;
                Assert.That(slot.DrawnBySkinDraw, Is.False, "dormant: in the skin draw no more than its renderer draws");
                Assert.That(skins.IndividualCount, Is.Zero);
                Assert.That(skin.enabled, Is.False);

                ActivateStill(slot);
                Assert.That(slot.DrawnBySkinDraw, Is.True, slot.SkinDrawRefusal);
                Assert.That(skin.enabled, Is.False, "the skin draw draws it: its renderer stays off");
                Assert.That(skins.IndividualCount, Is.EqualTo(1));
                Assert.That(skins.ModelCount, Is.EqualTo(1));
                Assert.That(skins.ModelOf(0).Describes(skin), Is.True, "the model is the renderer's mesh");
                Assert.That(skins.UpdateFrame(false), Is.True, skins.LastFailure);
                Assert.That(skins.PalettesGathered, Is.GreaterThanOrEqualTo(1), "its palette was gathered");
                Assert.That(skins.HistoryResets, Is.GreaterThanOrEqualTo(1), "its history began at the activation");
                yield return null;

                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(slot.Handle);
                List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
                Assert.That(cut.Count, Is.EqualTo(1));
                Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                int accepted = Time.frameCount, left = -1;
                for (int i = 0; i < 120 && left < 0; i++)
                {
                    if (!slot.DrawnBySkinDraw) left = Time.frameCount;
                    else yield return null;
                }

                TestContext.Out.WriteLine("the slot left the skin draw " + (left - accepted) + " frames after the cut was accepted");
                Assert.That(left, Is.GreaterThanOrEqualTo(0), "the cut takes it out of the skin draw (the cut's display draws it)");
                Assert.That(skins.IndividualCount, Is.Zero);
                Assert.That(skin.enabled, Is.False);
                Assert.That(skins.UpdateFrame(false), Is.True);
                yield return UntilCommitted(cut[0].Operation);
                for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
                Assert.That(slot.IsReturnReady, Is.True);
                Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
                yield return UntilPrepared(slot);
                Assert.That(slot.DrawnBySkinDraw, Is.False, "prepared again: dormant");
                yield return null;
                ActivateStill(slot);
                Assert.That(slot.DrawnBySkinDraw, Is.True, slot.SkinDrawRefusal);
                Assert.That(skins.IndividualCount, Is.EqualTo(1), "reused: taken again, the same model");
                Assert.That(skins.ModelCount, Is.EqualTo(1));
                Assert.That(skin.enabled, Is.False);
                Assert.That(SandboxNpcCharacter.SkinRegistrations, Is.GreaterThanOrEqualTo(2));
                Assert.That(SandboxNpcCharacter.SkinRemovals, Is.GreaterThanOrEqualTo(1));

                Object.Destroy(setup);
                yield return null;
                for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
                Assert.That(coldWorld.IsReleased, Is.True);
                Assert.That(coldWorld.SkinDisplay, Is.Null, "released with the world");
                Assert.That(skins.IsDisposed, Is.True);
            }
            finally
            {
                VpSkinDrawSetup.Request = before;
            }
        }

        /// <summary>
        /// **Frames nobody draws, under the worker gather (SKIN-BENCH-3, TL review P2).** With the worker gather asked for, the
        /// world puts its scheduler beside the display; a slot whose pose player writes the bones every frame is dirty every
        /// frame, so the scheduler schedules a job each late update. When no camera updates the display for several frames,
        /// each next schedule completes and drops the job before it (nothing of it is published, no container is written while a
        /// job reads it); the first update then schedules afresh, completes and publishes the current bones.
        /// </summary>
        [UnityTest]
        public IEnumerator TheSkinGather_OfFramesNobodyDraws_IsCollectedByTheNextSchedule()
        {
            VpSkinDrawRequest before = VpSkinDrawSetup.Request;
            VpSkinGatherMode gatherBefore = VpSkinGatherSetup.Mode;
            VpSkinDrawSetup.Request = VpSkinDrawRequest.SkinDraw;
            VpSkinGatherSetup.Mode = VpSkinGatherMode.Worker;
            try
            {
                ColdWorld();
                Assert.That(coldWorld.DrawsSkins, Is.True, coldWorld.SkinRouteReason);
                VpSkinDisplay skins = coldWorld.SkinDisplay;
                Assert.That(skins.GatherModeActual, Is.EqualTo("worker"), skins.WorkerFallbackReason);
                Assert.That(coldWorld.SkinGatherScheduler, Is.Not.Null, "the scheduler stands with the display");
                coldWorld.Driver.RemainingMainSeconds = () => 1.0;
                SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
                ColdTrack(setup);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                Assert.That(slot.Activate(), Is.True);   // its pose player stays on: the bones are written every frame
                Assert.That(slot.DrawnBySkinDraw, Is.True, slot.SkinDrawRefusal);
                SkinnedMeshRenderer skin = slot.Renderer;
                PoseTablePlayer player = slot.CharacterRoot.GetComponent<PoseTablePlayer>();
                int revisionBefore = player.ApplyRevision;

                // Five frames, no update: the scheduler schedules each late update; each schedule after the first drops the one before.
                for (int i = 0; i < 5; i++) yield return null;
                Assert.That(player.ApplyRevision, Is.GreaterThan(revisionBefore), "the pose is written every frame");
                long scheduled = coldWorld.SkinGatherScheduler.Scheduled;
                Assert.That(scheduled, Is.GreaterThanOrEqualTo(4), "scheduled every frame");
                Assert.That(skins.UncollectedJobs, Is.GreaterThanOrEqualTo(scheduled - 1), "every job but the one in flight was completed and dropped");
                Assert.That(skins.FramesUpdated, Is.Zero, "nobody updated");
                Assert.That(skins.PalettesGathered, Is.Zero, "nothing published");
                Assert.That(slot.DrawnBySkinDraw, Is.True, "registered");
                Assert.That(slot.IsDrawnBySkinDraw, Is.False, "registered and never gathered: not drawn yet (TL review 3)");

                // The first update (a camera's beginning): the job in flight is a past frame's -- dropped -- and this frame's
                // job is scheduled, completed and published: the current bones.
                long uncollected = skins.UncollectedJobs;
                Assert.That(skins.UpdateFrame(false), Is.True, skins.LastFailure);
                Assert.That(skins.UncollectedJobs, Is.EqualTo(uncollected + 1));
                Assert.That(skins.LateSchedules, Is.EqualTo(1));
                Assert.That(skins.LastFrameUsedJobs, Is.True);
                Assert.That(skins.LastJobIndividuals, Is.EqualTo(1));
                Assert.That(skins.PalettesGathered, Is.EqualTo(1));
                Assert.That(skins.JobFailures, Is.Zero);
                Assert.That(skins.LateRegathers, Is.Zero);
                Assert.That(skins.TryGetForTest(SlotSkinHandle(slot), out _, out VpSkinRecord record, out Matrix4x4[] palette, out _), Is.True);
                Assert.That(record.flags & VpSkinRecord.ActiveFlag, Is.Not.Zero);
                Assert.That(slot.IsDrawnBySkinDraw, Is.True, "gathered and active: drawn by the skin draw (the check's meaning, TL review 3)");
                Assert.That(skins.IsActive(SlotSkinHandle(slot)), Is.True);
                Assert.That(skins.ModelRegistrations, Is.EqualTo(1));
                Assert.That(skins.ModelRefusals, Is.Zero, skins.LastModelRefusal);
                Assert.That(skins.IndividualRefusals, Is.Zero);
                var expected = new Matrix4x4[skins.ModelOf(0).BoneCount];
                Assert.That(VpSkinPalette.TryGather(skin.transform.worldToLocalMatrix, skin.bones, skins.ModelOf(0), expected), Is.True);
                for (int b = 0; b < expected.Length; b++) for (int k = 0; k < 16; k++) Assert.That(palette[b][k], Is.EqualTo(expected[b][k]).Within(1e-4f), "bone " + b + " [" + k + "]");
                Assert.That(skins.Describe(), Does.Contain("uncollected " + (uncollected + 1)));

                Object.Destroy(setup);
                yield return null;
                for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
                Assert.That(coldWorld.IsReleased, Is.True);
                Assert.That(skins.IsDisposed, Is.True);
            }
            finally
            {
                VpSkinDrawSetup.Request = before;
                VpSkinGatherSetup.Mode = gatherBefore;
            }
        }

        /// <summary>
        /// **The scheduler stands whenever the worker gather is in use (TL review 2, P2).** With a threshold the display's
        /// description reads "worker-from-n"; the world makes the scheduler from the state, not the string, so the frames at or
        /// above the threshold are scheduled at the end of the late update, not at the first camera.
        /// </summary>
        [UnityTest]
        public IEnumerator WithAWorkerThreshold_TheWorldStillMakesTheScheduler()
        {
            VpSkinDrawRequest before = VpSkinDrawSetup.Request;
            VpSkinGatherMode gatherBefore = VpSkinGatherSetup.Mode;
            int fromBefore = VpSkinGatherSetup.WorkerFrom;
            VpSkinDrawSetup.Request = VpSkinDrawRequest.SkinDraw;
            VpSkinGatherSetup.Mode = VpSkinGatherMode.Worker;
            VpSkinGatherSetup.WorkerFrom = 3;
            try
            {
                ColdWorld();
                Assert.That(coldWorld.DrawsSkins, Is.True, coldWorld.SkinRouteReason);
                Assert.That(coldWorld.SkinDisplay.WorkerActive, Is.True, coldWorld.SkinDisplay.WorkerFallbackReason);
                Assert.That(coldWorld.SkinDisplay.GatherModeActual, Is.EqualTo("worker-from-3"));
                Assert.That(coldWorld.SkinDisplay.WorkerFrom, Is.EqualTo(3));
                Assert.That(coldWorld.SkinGatherScheduler, Is.Not.Null, "the scheduler is made from the worker state, whatever the threshold");
                yield return null;
                for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
                Assert.That(coldWorld.IsReleased, Is.True);
            }
            finally
            {
                VpSkinDrawSetup.Request = before;
                VpSkinGatherSetup.Mode = gatherBefore;
                VpSkinGatherSetup.WorkerFrom = fromBefore;
            }
        }

        // The skin handle of a slot, read through its private field (the handle is the display's name for the individual).
        /// <summary>
        /// **A slot the skin draw really draws is cut from the 24-byte input under cpu-float** (CPU-SKIN-FLOAT-1 review 1): the slot's
        /// registration with the world's skin draw sets the handle's flag, the float input is prepared all the same, and the cut's
        /// bake takes the 24-byte input with the reason recorded -- the cut's geometry stays the display's.
        /// </summary>
        [UnityTest]
        public IEnumerator CpuFloatRoute_ASlotTheSkinDrawDraws_IsCutFromThe24ByteInput()
        {
            VpSkinDrawRequest before = VpSkinDrawSetup.Request;
            VpSkinDrawSetup.Request = VpSkinDrawRequest.SkinDraw;
            VpSkinBakeSetup.Mode = VpSkinBakeMode.CpuFloat;
            try
            {
                ColdWorld();
                Assert.That(coldWorld.DrawsSkins, Is.True, coldWorld.SkinRouteReason);
                Assert.That(coldWorld.SkinBakeRoute, Is.EqualTo("cpu-float"));
                coldWorld.Driver.RemainingMainSeconds = () => 1.0;
                SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
                ColdTrack(setup);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                Assert.That(slot.FloatSkinInputRefusal, Is.Null, "the float input was prepared with the handle");
                Assert.That(slot.Handle.SkinModelForWarm.FloatInput, Is.Not.Null);
                yield return null;
                Assert.That(slot.Handle.DisplayedBySkinDraw, Is.False, "dormant: not drawn by the skin draw yet");
                ActivateStill(slot);
                Assert.That(slot.DrawnBySkinDraw, Is.True, slot.SkinDrawRefusal);
                Assert.That(slot.Handle.DisplayedBySkinDraw, Is.True, "the registration told the handle");
                yield return null;
                int cpu = VpSkinBakeStats.CpuSyncs, cpuFloat = VpSkinBakeStats.CpuFloatSyncs, fallbacks = VpSkinBakeStats.Fallbacks;
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(slot.Handle);
                List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
                Assert.That(cut.Count, Is.EqualTo(1));
                Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(VpSkinBakeStats.CpuSyncs - cpu, Is.EqualTo(1), "the 24-byte bake");
                Assert.That(VpSkinBakeStats.CpuFloatSyncs - cpuFloat, Is.Zero);
                Assert.That(VpSkinBakeStats.Fallbacks - fallbacks, Is.EqualTo(1));
                VpSkinBakeStats.Record record = VpSkinBakeStats.Records[VpSkinBakeStats.Records.Count - 1];
                Assert.That(record.route, Is.EqualTo("cpu"));
                Assert.That(record.requestedRoute, Is.EqualTo("cpu-float"));
                Assert.That(record.fallback, Does.Contain("skin draw"));
                int left = -1;
                for (int i = 0; i < 120 && left < 0; i++) { if (!slot.DrawnBySkinDraw) left = Time.frameCount; else yield return null; }
                Assert.That(left, Is.GreaterThanOrEqualTo(0), "the cut takes it out of the skin draw");
                Assert.That(slot.Handle == null || !slot.Handle.DisplayedBySkinDraw, Is.True, "the removal cleared the flag (or the handle ended with its cut)");
                yield return UntilCommitted(cut[0].Operation);
            }
            finally
            {
                VpSkinDrawSetup.Request = before;
                VpSkinBakeSetup.Mode = VpSkinBakeSetup.Default;
            }
        }

        /// <summary>
        /// **A re-prepared slot keeps its model's float input** (CPU-SKIN-FLOAT-DEFAULT-1): under the default (cpu-float) a pooled slot's
        /// preparation makes one float input for the share's model; its cut, its return and its re-preparation make none again -- the
        /// same input object, the same count, and the live accounting says one model and one input.
        /// </summary>
        [UnityTest]
        public IEnumerator Default_ARepreparedSlot_KeepsItsModelsFloatInput_AndMakesNoneAgain()
        {
            VpSkinBakeSetup.ReadArguments(new[] { "player.exe" });
            Assert.That(VpSkinBakeSetup.Mode, Is.EqualTo(VpSkinBakeMode.CpuFloat));
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            int made = VpSkinFloatInput.CreatedCount;
            SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
            ColdTrack(setup);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            Assert.That(slot.FloatSkinInputRefusal, Is.Null);
            VpSkinModel model = slot.Handle.SkinModelForWarm;
            VpSkinFloatInput input = model.FloatInput;
            Assert.That(input, Is.Not.Null, "made by the handle's preparation");
            Assert.That(VpSkinFloatInput.CreatedCount - made, Is.EqualTo(1));
            Assert.That(VpSkinModel.DescribeLive(), Does.Contain("float inputs live").And.Contain("models live"));
            yield return null;
            ActivateStill(slot);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(slot.Handle);
            List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(VpSkinBakeStats.Records[VpSkinBakeStats.Records.Count - 1].route, Is.EqualTo(slot.DrawnBySkinDraw ? "cpu" : "cpu-float"), "a slot the skin draw drew is cut from the 24-byte input; otherwise cpu-float");
            Assert.That(VpSkinFloatInput.CreatedCount - made, Is.EqualTo(1), "the hit made none");
            yield return UntilCommitted(cut[0].Operation);
            for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(slot.IsReturnReady, Is.True);
            Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            Assert.That(ReferenceEquals(slot.Handle.SkinModelForWarm, model), Is.True, "the share's model again");
            Assert.That(ReferenceEquals(model.FloatInput, input), Is.True, "the same float input, not made again");
            Assert.That(input.IsDisposed, Is.False);
            Assert.That(VpSkinFloatInput.CreatedCount - made, Is.EqualTo(1), "re-prepared: nothing made again");

            // The release: the share's model and its float input go with the last slot (the live accounting says so).
            int live = VpSkinModel.LiveCount;
            Object.Destroy(setup);
            yield return null;
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
            Assert.That(input.IsDisposed, Is.True, "the float input went with the share's model");
            Assert.That(VpSkinModel.LiveCount, Is.EqualTo(live - 1), "the share's model left the live accounting");
        }

        private static VpSkinHandle SlotSkinHandle(SandboxNpcCharacter slot)
            => (VpSkinHandle)typeof(SandboxNpcCharacter).GetField("_skinHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(slot);

        [UnityTest]
        public IEnumerator WithTheRenderersAskedFor_AWorldMakesNoSkinDraw_AndASlotDrawsByItsRenderer()
        {
            VpSkinDrawRequest before = VpSkinDrawSetup.Request;
            VpSkinDrawSetup.Request = VpSkinDrawRequest.Renderers;
            try
            {
                ColdWorld();
                Assert.That(coldWorld.SkinRoute, Is.EqualTo("RENDERERS"));
                Assert.That(coldWorld.SkinRouteReason, Does.Contain(VpSkinDrawSetup.RenderersArgument));
                Assert.That(coldWorld.DrawsSkins, Is.False);
                coldWorld.Driver.RemainingMainSeconds = () => 1.0;
                SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
                ColdTrack(setup);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                ActivateStill(slot);
                Assert.That(slot.DrawnBySkinDraw, Is.False);
                Assert.That(slot.SkinDrawRefusal, Is.EqualTo("no skin draw"));
                Assert.That(slot.Renderer.enabled, Is.True, "its renderer draws it, as before");
                Object.Destroy(setup);
                yield return null;
                for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
                Assert.That(coldWorld.IsReleased, Is.True);
            }
            finally
            {
                VpSkinDrawSetup.Request = before;
            }
        }
    }
}
