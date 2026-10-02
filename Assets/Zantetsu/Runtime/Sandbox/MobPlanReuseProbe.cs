using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// CHECK ONLY (the reused-slot check of the MobPlan Player check, TL 2026-10-01): which live individual on a reused
    /// crowd slot stands as a hit target, and one small synthetic Slash aimed at its current shape. Nothing here cuts,
    /// moves, retires or publishes anything: the sweep is the caller's to give to the ordinary detector's Evaluate.
    /// </summary>
    public static class MobPlanReuseProbe
    {
        /// <summary>
        /// Why the slot's current individual is not a reused individual standing as a hit target, or null when it is.
        /// Reuse is read from the slot's use history and the individual's generation, never from a replacement flag: the
        /// slot carried <paramref name="individualsOnSlot"/> individuals in turn, this one is the slot's
        /// <paramref name="generation"/>-th activation (its own, not a later one), the slot holds this individual's own
        /// prepared cut (<paramref name="handle"/>, not the previous individual's), and it is not retired, is drawn, has
        /// been posed since its activation and is a candidate of the detector whose handle takes hits now. Whether its
        /// fragment exists yet (Source, made at its first hit) is no condition.
        /// </summary>
        public static string WhyNotTarget(
            SandboxNpcCharacter slot, int individualsOnSlot, int generation, VpPreparedCharacterCut handle,
            VpPreparedCharacterCut previousHandle, bool retired, int activatedFrame, SlashHitDetector detector)
        {
            if (slot == null || slot.CharacterRoot == null) return "no slot";
            if (individualsOnSlot < 2 || generation < 2) return "not reused (individuals on the slot " + individualsOnSlot + ", generation " + generation + ")";
            if (slot.Activations != generation) return "the slot was activated again since (activations " + slot.Activations + ", this individual's generation " + generation + ")";
            if (generation != individualsOnSlot) return "generation " + generation + " is not its turn on the slot (" + individualsOnSlot + " individuals seen)";
            if (handle == null || !ReferenceEquals(slot.Handle, handle)) return "the slot's prepared cut is not this individual's";
            if (previousHandle == null || ReferenceEquals(previousHandle, handle)) return "no previous individual's prepared cut, or the same one";
            if (retired) return "retired";
            if (!slot.IsTarget) return "the slot is not active";
            SkinnedMeshRenderer r = slot.Renderer;
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return "not drawn";
            PoseTablePlayer pose = slot.CharacterRoot.GetComponent<PoseTablePlayer>();
            if (pose == null || pose.AppliedFrame < activatedFrame) return "not posed since its activation (applied at frame " + (pose != null ? pose.AppliedFrame : -1) + ", activated at " + activatedFrame + ")";
            if (handle.IsDisposed || !handle.IsHitTarget || detector == null || !detector.HasCharacter(handle)) return "not a hit target of the detector";
            return null;
        }

        /// <summary>
        /// One small level Slash through the centre of one convex of the handle's hit shape, as its bones stand now: the
        /// convexes tried largest first, each by the same query the detector makes (the plane in the convex's bone frame
        /// against the convex), and the first one met is aimed at. <paramref name="pose"/>, when live, puts this frame's
        /// whole pose on the bones first (as the detector does before it reads them). False when no convex is met.
        /// </summary>
        public static bool TryAim(
            VpPreparedCharacterCut handle, IPoseOnDemand pose, Vector3 facing, long slashId, double at, float halfSpan,
            out SlashSweep sweep, out string aimed)
        {
            sweep = default;
            aimed = null;
            if (handle == null || handle.IsDisposed) { aimed = "no handle"; return false; }
            bool applied = pose != null && pose.IsLive && pose.EnsureCurrentFullPose();
            VpCharacterHitShape shape = handle.HitShape;
            var order = new List<(int k, float volume)>();
            for (int k = 0; k < shape.ConvexCount; k++)
            {
                shape.Bounds(k, out float3 lo, out float3 hi);
                float3 e = math.max(hi - lo, 0f);
                order.Add((k, e.x * e.y * e.z));
            }

            order.Sort((a, b) => b.volume.CompareTo(a.volume));
            Vector3 forward = Vector3.ProjectOnPlane(facing, Vector3.up);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            var section = new float3[64];
            foreach ((int k, float volume) in order)
            {
                shape.Bounds(k, out float3 lo, out float3 hi);
                float4x4 boneToWorld = (float4x4)handle.ConvexBone(k).localToWorldMatrix;
                Vector3 c = (Vector3)math.transform(boneToWorld, (lo + hi) * 0.5f);
                var candidate = new SlashSweep(slashId, at, false, new Plane(Vector3.up, c), forward, right,
                    c - forward * halfSpan - right * halfSpan, c - forward * halfSpan + right * halfSpan,
                    c + forward * halfSpan - right * halfSpan, c + forward * halfSpan + right * halfSpan);
                float4x4 worldToBone = math.inverse(boneToWorld);
                float4 plane = math.mul(math.transpose(boneToWorld), new float4((float3)Vector3.up, -c.y));
                plane /= math.length(plane.xyz);
                bool met = SlashSweepConvexQuery.Intersects(plane,
                    math.transform(worldToBone, (float3)candidate.PreviousA), math.transform(worldToBone, (float3)candidate.PreviousB),
                    math.transform(worldToBone, (float3)candidate.CurrentA), math.transform(worldToBone, (float3)candidate.CurrentB),
                    shape.Bank, shape.Convex(k), ref section);
                if (!met) continue;
                sweep = candidate;
                aimed = "convex " + k + " of " + shape.ConvexCount + " (bone " + handle.ConvexBone(k).name + ", local bounds volume " + volume.ToString("G4") + " m3) at its centre "
                    + c.ToString("F3") + ", level plane, span and travel " + (2f * halfSpan).ToString("F2") + " m; whole pose applied for this " + (applied ? "by this call" : "already, or not on demand");
                return true;
            }

            aimed = "no convex of " + shape.ConvexCount + " met by a level sweep through its centre (whole pose applied " + applied + ")";
            return false;
        }
    }
}
