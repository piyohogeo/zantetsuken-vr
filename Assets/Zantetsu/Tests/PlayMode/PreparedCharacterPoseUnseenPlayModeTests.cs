using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// **An uncut character outside both eyes' view is not posed at all** (TL, 2026-10-07; DESIGN 9, D-213), on the
    /// product's own character -- a crowd slot (<see cref="SandboxNpcCharacter"/>) under the level of detail, its pose
    /// given by a plan's time as the crowd gives it. While neither eye's view holds its bounds no pose is evaluated and
    /// no bone is written, the root goes on being moved and the plan's time goes on; the frame one eye sees it again it
    /// has that frame's pose; a Slash that may meet it out of view is judged and cut with its current pose; and the
    /// slot leaves by its cut and comes back with a new individual as before. With no camera to read, no character is
    /// taken as out of view.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        private const string UnseenBone = "Lent character bone";

        // Two eyes for the level of detail: both at one place looking one way, or each its own.
        private static void Eyes(PoseLodDirector director, Camera left, Camera right, Vector3 leftAt, Vector3 leftForward, Vector3 rightAt, Vector3 rightForward)
        {
            left.transform.SetPositionAndRotation(leftAt, Quaternion.LookRotation(leftForward));
            right.transform.SetPositionAndRotation(rightAt, Quaternion.LookRotation(rightForward));
            director.OverrideEyes(new[] { WorldToClip(left), WorldToClip(right) }, new[] { left.transform.position, right.transform.position });
        }

        // The pose's source as the crowd gives it: the plan's pose at the frame's own time, counted.
        private sealed class PlanTime
        {
            public int calls;
            public double lastTarget = double.NaN;

            public void Attach(PoseTablePlayer player)
            {
                PoseTable table = player.Table;
                player.PlanSource = (double target, out PoseTable planned, out double source) =>
                {
                    calls++;
                    lastTarget = target;
                    planned = table;
                    source = table.ResolveSourceTime(target);
                    return true;
                };
            }
        }

        // Where the plan has the weighted bone at the frame's time.
        private static Vector3 PlannedNow(PoseTablePlayer player)
        {
            var positions = new Vector3[player.BoneCount];
            var rotations = new Quaternion[player.BoneCount];
            Assert.That(player.Table.TryEvaluate(player.Table.ResolveSourceTime(Time.timeAsDouble), positions, rotations), Is.True);
            return positions[0];
        }

        [UnityTest]
        public IEnumerator PoseUnseen_ACharacterNoEyeSees_IsNotPosedWhileItsRootAndItsPlansTimeGoOn_AndTheFrameOneEyeSeesItAgain_ItHasThatFramesPose()
        {
            ColdWorld();
            PoseLodDirector director = ColdTrack(new GameObject("unseen lod")).AddComponent<PoseLodDirector>();
            director.DisplayRateHz = 72f;
            Assert.That(director.StopsOutOfView, Is.True, "the product's setting: on with nothing said");
            Camera left = ReturnCamera("unseen left eye"), right = ReturnCamera("unseen right eye");
            try
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
                SandboxNpcCharacter slot = BonesSlot(true, 1, new Vector3(0f, 0f, 10f), director, out GameObject setup, out Transform _);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                PoseTablePlayer player = slot.CharacterRoot.GetComponent<PoseTablePlayer>();
                Transform bone = slot.CharacterRoot.transform.Find(UnseenBone);
                Assert.That(bone, Is.Not.Null);
                var plan = new PlanTime();
                plan.Attach(player);

                // Both eyes look away from where the character stands.
                Vector3 away = new Vector3(0f, 1f, -40f);
                Eyes(director, left, right, away, Vector3.back, away + new Vector3(0.064f, 0f, 0f), Vector3.back);
                Assert.That(slot.Activate(), Is.True);
                PoseLodCharacter lod = slot.Lod;
                Assert.That(lod, Is.Not.Null);
                Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "taken under the level of detail with this frame's pose on");
                Assert.That(plan.lastTarget, Is.EqualTo(Time.timeAsDouble), "the plan was asked the frame's own time");
                int appliedAtActivation = player.AppliedFrame, callsAtActivation = plan.calls;
                Vector3 stopped = bone.localPosition;
                Quaternion stoppedRotation = bone.localRotation;

                // 1. Out of both eyes' view: nothing is evaluated and no bone is written, frame after frame, while the
                //    root is moved as a route moves it.
                Vector3 rootFrom = slot.CharacterRoot.transform.position;
                int frames = 0;
                bool movedAway = false;
                for (float limit = Time.realtimeSinceStartup + 20f; Time.realtimeSinceStartup < limit; frames++)
                {
                    slot.CharacterRoot.transform.position += new Vector3(0.01f, 0f, 0f);
                    yield return null;
                    Assert.That(lod.ViewStrength, Is.GreaterThan(0), "neither eye sees it");
                    Assert.That(lod.IsStoppedOutOfView, Is.True);
                    Assert.That(player.AppliedFrame, Is.EqualTo(appliedAtActivation), "frame " + frames + " out of view: no pose applied");
                    Assert.That(plan.calls, Is.EqualTo(callsAtActivation), "and none evaluated: the plan is not asked");
                    Assert.That(bone.localPosition, Is.EqualTo(stopped), "the bone as it stopped");
                    Assert.That(new[] { director.UpdatedLastFrame, director.StoppedLastFrame }, Is.EqualTo(new[] { 0, 1 }));
                    // Long enough that the plan's pose now stands well away from the one the bones stopped in.
                    if (frames >= 40 && Mathf.Abs(PlannedNow(player).x - stopped.x) > 0.6f)
                    {
                        movedAway = true;
                        break;
                    }
                }

                Assert.That(movedAway, Is.True, "the plan's pose moved away from the stopped one");
                Assert.That(lod.LastUpdateFrame, Is.EqualTo(-1), "no scheduled update at all since the character was taken");
                Vector3 rootNow = slot.CharacterRoot.transform.position;
                Assert.That(rootNow.x - rootFrom.x, Is.EqualTo(0.01f * (frames + 1)).Within(1e-3f), "the root went on being moved");
                Assert.That(Vector3.Distance(bone.position, rootNow + stopped), Is.LessThan(1e-3f), "the stopped pose is carried by the root");
                Assert.That(bone.localRotation, Is.EqualTo(stoppedRotation));
                TestContext.Out.WriteLine("out of both eyes' view for " + (frames + 1) + " frames: poses applied 0, plan evaluations 0, the root moved " + (rootNow.x - rootFrom.x).ToString("F2")
                                          + " m; the plan has the bone at x " + PlannedNow(player).x.ToString("F3") + " now, the bones stopped at x " + stopped.x.ToString("F3"));

                // 2. One eye sees it again -- the left; the right still looks away. That frame it has the frame's pose,
                //    asked of the plan once, at the frame's own time.
                Vector3 centre = slot.Renderer.bounds.center;
                Eyes(director, left, right, centre + new Vector3(0f, 0f, -4f), Vector3.forward, away, Vector3.back);
                yield return null;
                Assert.That(lod.ViewStrength, Is.EqualTo(0), "seen by one eye is seen");
                Assert.That(lod.IsStoppedOutOfView, Is.False);
                Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "its pose was put on in the frame the eye sees it, before anything is drawn");
                Assert.That(lod.LastUpdateFrame, Is.EqualTo(Time.frameCount));
                Assert.That(plan.calls, Is.EqualTo(callsAtActivation + 1), "the plan asked once: nothing is played to catch up");
                Assert.That(plan.lastTarget, Is.EqualTo(Time.timeAsDouble), "for the frame's own time: nothing was frozen while it stood stopped");
                Assert.That(Vector3.Distance(bone.localPosition, PlannedNow(player)), Is.LessThan(1e-5f), "the bone stands where the plan has it now");
                Assert.That(Mathf.Abs(bone.localPosition.x - stopped.x), Is.GreaterThan(0.5f), "which is not where it stopped");
                TestContext.Out.WriteLine("seen by the left eye alone: pose applied in that frame, the bone at x " + bone.localPosition.x.ToString("F3") + " (the plan's), plan evaluations 1");

                // In view it follows its level's schedule again (level 0 here: every frame).
                yield return null;
                Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "in view: updated by its level's schedule");

                // 3. Both away again: stopped again.
                Eyes(director, left, right, away, Vector3.back, away + new Vector3(0.064f, 0f, 0f), Vector3.back);
                yield return null;
                int appliedBeforeStop = player.AppliedFrame;
                for (int f = 0; f < 10; f++)
                {
                    yield return null;
                    Assert.That(lod.IsStoppedOutOfView, Is.True);
                    Assert.That(player.AppliedFrame, Is.EqualTo(appliedBeforeStop), "out of view again: not posed");
                }

                // 4. No camera to read: the view asks for nothing, and the character is not taken as out of view.
                director.OverrideEyes(null, null);
                director.ViewCamera = null;
                Assert.That(Camera.main, Is.Null, "the layout: no camera the director could read");
                for (int f = 0; f < 6; f++)
                {
                    yield return null;
                    Assert.That(lod.ViewStrength, Is.EqualTo(0));
                    Assert.That(lod.IsStoppedOutOfView, Is.False, "with no camera to read, not stopped");
                    Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "and posed");
                }

                yield return Destroy(setup);
            }
            finally
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
            }
        }

        [UnityTest]
        public IEnumerator PoseUnseen_ASlashThatMayMeetACharacterNoEyeSees_IsJudgedAndCutWithItsCurrentPose_AndTheSlotLeavesAndComesBackAsBefore()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            PoseLodDirector director = ColdTrack(new GameObject("unseen slash lod")).AddComponent<PoseLodDirector>();
            director.DisplayRateHz = 72f;
            Camera left = ReturnCamera("unseen slash left eye"), right = ReturnCamera("unseen slash right eye");
            try
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
                SandboxNpcCharacter slot = BonesSlot(true, 1, Vector3.zero, director, out GameObject setup, out Transform _);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                PoseTablePlayer player = slot.CharacterRoot.GetComponent<PoseTablePlayer>();
                Transform bone = slot.CharacterRoot.transform.Find(UnseenBone);
                var plan = new PlanTime();
                plan.Attach(player);
                Vector3 away = new Vector3(0f, 1f, -40f);
                Eyes(director, left, right, away, Vector3.back, away + new Vector3(0.064f, 0f, 0f), Vector3.back);
                Assert.That(slot.Activate(), Is.True);
                PoseLodCharacter lod = slot.Lod;
                VpPreparedCharacterCut first = slot.Handle;
                Vector3 stopped = bone.localPosition;
                int appliedAtActivation = player.AppliedFrame;

                // Stopped out of view until the plan's pose stands well away from the one the bones stopped in.
                int frames = 0;
                bool movedAway = false;
                for (float limit = Time.realtimeSinceStartup + 20f; Time.realtimeSinceStartup < limit; frames++)
                {
                    yield return null;
                    Assert.That(lod.IsStoppedOutOfView, Is.True);
                    if (frames >= 20 && Mathf.Abs(PlannedNow(player).x - stopped.x) > 0.6f)
                    {
                        movedAway = true;
                        break;
                    }
                }

                Assert.That(movedAway, Is.True, "the plan's pose moved away from the stopped one");
                Assert.That(player.AppliedFrame, Is.EqualTo(appliedAtActivation), "not posed since it was taken");
                Assert.That(bone.localPosition, Is.EqualTo(stopped));

                // A Slash whose sweep may meet its range, though no eye sees it: its whole current pose is put on
                // first, and the hit and the cut read that pose.
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(first, lod);   // as the slot gives it to the scene's detector
                Vector3 planned = PlannedNow(player);
                List<SlashHitConfirmed> hits = Evaluate(detector, CharacterLevel(1, 0.3f, -0.5f, 0.5f), 1);
                Assert.That(lod.ViewStrength, Is.GreaterThan(0), "still out of both eyes' view");
                Assert.That(lod.ForcedCount, Is.EqualTo(1), "the whole current pose was asked for and put on, once");
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount));
                Assert.That(Vector3.Distance(bone.localPosition, planned), Is.LessThan(1e-5f), "the bone at the plan's pose of this frame");
                Assert.That(Mathf.Abs(bone.localPosition.x - stopped.x), Is.GreaterThan(0.5f), "not where it stopped");
                Assert.That(hits.Count, Is.EqualTo(1), "the character out of view is a hit target all the same");
                // Published at once, or Pending under the ordinary budget rule (a fresh session's first evaluation may
                // use the frame up): the same cut, followed to its publication. Its input was taken at the acceptance.
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published).Or.EqualTo(ProvisionalCutAcceptance.Pending), "an ordinary acceptance");
                TestContext.Out.WriteLine("the Slash out of view was accepted as " + hits[0].Acceptance);
                ProvisionalOwnerPair pair = default;
                for (int i = 0; i < 600 && !coldWorld.Owners.TryGetProvisionalOf(first.Source, out pair); i++) yield return null;
                Assert.That(coldWorld.Owners.TryGetProvisionalOf(first.Source, out pair), Is.True, "the accepted cut was published");
                pair.PositiveShape.TryLocalBounds(out float3 plo, out float3 phi);
                pair.NegativeShape.TryLocalBounds(out float3 nlo, out float3 nhi);
                float cutCentre = (math.min(plo.x, nlo.x) + math.max(phi.x, nhi.x)) * 0.5f;
                TestContext.Out.WriteLine("a Slash out of view after " + (frames + 1) + " stopped frames: the bones stopped at x " + stopped.x.ToString("F3") + ", the plan's pose now x " + planned.x.ToString("F3")
                                          + ", the cut's shapes centred at x " + cutCentre.ToString("F3"));
                Assert.That(cutCentre, Is.EqualTo(planned.x).Within(0.2f), "the cut read the current pose, not the stopped one");
                yield return UntilCommitted(hits[0].Operation);

                // The slot leaves by its cut and comes back with a new individual -- still out of view: taken with the
                // frame's pose on, then not posed, and a hit target again.
                Assert.That(first.IsWithdrawn, Is.True);
                Assert.That(slot.LeaveLevelOfDetail(), Is.True, "the withdrawn individual's registration is let go, as the crowd lets it");
                Assert.That(director.CharacterCount, Is.Zero);
                for (int i = 0; i < 120 && !slot.IsReturnReady; i++) yield return null;
                Assert.That(slot.IsReturnReady, Is.True, "the slot can go back");
                Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                Assert.That(slot.Activate(), Is.True);
                Assert.That(slot.Activations, Is.EqualTo(2));
                PoseLodCharacter again = slot.Lod;
                VpPreparedCharacterCut second = slot.Handle;
                Assert.That(again, Is.Not.Null.And.Not.SameAs(lod), "a registration of its own");
                Assert.That(second, Is.Not.SameAs(first));
                Assert.That(director.CharacterCount, Is.EqualTo(1), "the first individual's registration is gone");
                Assert.That(player.AppliedFrame, Is.EqualTo(Time.frameCount), "the new individual is taken with this frame's pose on");
                int appliedAtSecond = player.AppliedFrame;
                for (int f = 0; f < 12; f++)
                {
                    yield return null;
                    Assert.That(again.IsStoppedOutOfView, Is.True, "out of view: not posed");
                    Assert.That(player.AppliedFrame, Is.EqualTo(appliedAtSecond));
                }

                detector.AddCharacter(second, again);
                hits = Evaluate(detector, CharacterLevel(2, 0.3f, -0.5f, 0.5f), 2);
                Assert.That(again.ForcedCount, Is.EqualTo(1));
                // The sweep also meets what the first individual's cut left there; the new individual's own hit is the
                // one on the lineage root its handle was given by it.
                Assert.That(second.Source.IsSet, Is.True, "the new individual is hit out of view too");
                SlashHitConfirmed own = hits.Find(h => h.Fragment == second.Source);
                Assert.That(own.Fragment, Is.EqualTo(second.Source), "its hit is among the " + hits.Count + " of this evaluation");
                Assert.That(own.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published).Or.EqualTo(ProvisionalCutAcceptance.Pending), "an ordinary acceptance");
                yield return UntilCommitted(own.Operation);
                yield return Destroy(setup);
            }
            finally
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
            }
        }
    }
}
