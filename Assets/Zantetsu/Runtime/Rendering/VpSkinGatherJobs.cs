using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Zantetsu.Rendering
{
    /// <summary>One entry of the skin draw's bone TransformAccessArray: whose job (-1: freed), which palette bone, its bind pose.</summary>
    public struct VpSkinBoneEntry
    {
        public int job;
        public int bone;
        public float4x4 bindPose;
    }

    /// <summary>What one individual's job is given for a frame: whether to gather, where the palette goes, and whose it is.</summary>
    public struct VpSkinGatherInput
    {
        public int gather;
        public int target;
        public int id, generation;
    }

    /// <summary>
    /// The frames of the gathered individuals, read on the workers (SKIN-BENCH-3): the matrix, and its inverse, that the
    /// bone job multiplies with. An invalid frame (its Transform gone) fails the individual.
    /// </summary>
    [BurstCompile]
    public struct VpSkinFrameGatherJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<int> frameJob;
        [ReadOnly] public NativeArray<VpSkinGatherInput> inputs;
        [NativeDisableParallelForRestriction] public NativeArray<float4x4> frame;
        [NativeDisableParallelForRestriction] public NativeArray<float4x4> inverse;
        [NativeDisableParallelForRestriction] public NativeArray<int> failures;

        public void Execute(int index, TransformAccess access)
        {
            int job = frameJob[index];
            if (job < 0 || inputs[job].gather == 0) return;
            if (!access.isValid) { failures[job] = 1; return; }
            float4x4 m = access.localToWorldMatrix;
            frame[job] = m;
            inverse[job] = math.inverse(m);
        }
    }

    /// <summary>
    /// The used bones of the gathered individuals, read on the workers: <c>inverse(frame) × bone.localToWorld × bindPose</c>,
    /// as <see cref="VpSkinPalette.TryGather"/> computes it, written into the individual's target palette. A bone gone or
    /// a matrix not finite fails the individual (nothing of it is published). Unused bones have no entry: their identity
    /// was written once at the registration.
    /// </summary>
    [BurstCompile]
    public struct VpSkinBoneGatherJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<VpSkinBoneEntry> entries;
        [ReadOnly] public NativeArray<VpSkinGatherInput> inputs;
        [ReadOnly] public NativeArray<float4x4> inverse;
        [NativeDisableParallelForRestriction] public NativeArray<float4x4> palette;
        [NativeDisableParallelForRestriction] public NativeArray<int> failures;

        public void Execute(int index, TransformAccess access)
        {
            VpSkinBoneEntry entry = entries[index];
            if (entry.job < 0) return;
            VpSkinGatherInput input = inputs[entry.job];
            if (input.gather == 0) return;
            if (!access.isValid) { failures[entry.job] = 1; return; }
            float4x4 m = math.mul(math.mul(inverse[entry.job], (float4x4)access.localToWorldMatrix), entry.bindPose);
            if (!math.all(math.isfinite(m.c0)) || !math.all(math.isfinite(m.c1)) || !math.all(math.isfinite(m.c2)) || !math.all(math.isfinite(m.c3)))
            {
                failures[entry.job] = 1;
                return;
            }

            palette[input.target + entry.bone] = m;
        }
    }

    /// <summary>
    /// Schedules the skin draw's worker gather at the end of the late update (<see cref="VpSkinDisplay.ScheduleGather"/>):
    /// after every writer of the frame's poses (SKIN-BENCH-3 lists them), before the cameras. Put on the world's object
    /// with its display; <see cref="UpdateFrame"/> at the first camera completes and publishes.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    [DisallowMultipleComponent]
    public sealed class VpSkinGatherLateUpdate : MonoBehaviour
    {
        public VpSkinDisplay Display { get; set; }

        /// <summary>Frames in which this scheduled jobs.</summary>
        public long Scheduled { get; private set; }

        private void LateUpdate()
        {
            VpSkinDisplay display = Display;
            if (display == null || display.IsDisposed) return;
            if (display.ScheduleGather()) Scheduled++;
        }
    }
}
