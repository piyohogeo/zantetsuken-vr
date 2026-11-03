using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The synchronous Burst version of the bake probe's float-input kernel (DESIGN 4.5.14, SKIN-BAKE-PROBE-2): the
    /// same arithmetic as <see cref="SkinBakeProbeCpuBake.Bake"/> (the reference stays the judge of correctness) over
    /// inputs converted once into native memory -- the prepared mesh's float attributes (through a half where the GPU
    /// holds a Float16), its effective weights, one fixed palette -- written to a pre-allocated output. Called on the
    /// calling thread as a Burst direct call: no job is scheduled or completed, no worker is involved. The conversion,
    /// the output's room and the first compilation belong to the preparation and the warmup, not to a measured call.
    /// <see cref="Run"/> says whether the Burst-compiled entry ran (a managed marker that only the managed fallback sets).
    /// Nothing is converted to the 24-byte input model.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public static unsafe class SkinBakeProbeBurstBake
    {
        /// <summary>The inputs in native memory (Persistent), the palette's room, the output's room, the managed marker.</summary>
        public sealed class Prepared : IDisposable
        {
            public int VertexCount { get; internal set; }
            public int MaxInfluences { get; internal set; }
            public bool HasUv { get; internal set; }
            public int BoneCapacity { get; internal set; }
            /// <summary>Palette entries in use (set by <see cref="SetPalette"/>).</summary>
            public int BoneCount { get; internal set; }
            public long BytesAllocated { get; internal set; }
            public bool IsDisposed { get; private set; }
            internal NativeArray<float3> positions, normals;
            internal NativeArray<float2> uv;
            internal NativeArray<int> weightStart, weightCount, bones, marker;
            internal NativeArray<float> weights;
            internal NativeArray<float4x4> palette;
            internal NativeArray<VpRenderVertex> output;

            /// <summary>The output of the last <see cref="Run"/>: the first <see cref="VertexCount"/> elements.</summary>
            public NativeArray<VpRenderVertex> Output => output;

            public void Dispose()
            {
                if (IsDisposed) return;
                IsDisposed = true;
                if (positions.IsCreated) positions.Dispose();
                if (normals.IsCreated) normals.Dispose();
                if (uv.IsCreated) uv.Dispose();
                if (weightStart.IsCreated) weightStart.Dispose();
                if (weightCount.IsCreated) weightCount.Dispose();
                if (bones.IsCreated) bones.Dispose();
                if (weights.IsCreated) weights.Dispose();
                if (palette.IsCreated) palette.Dispose();
                if (output.IsCreated) output.Dispose();
                if (marker.IsCreated) marker.Dispose();
            }
        }

        /// <summary>Converts <paramref name="inputs"/> (read once from the prepared mesh) into native memory, with room for <paramref name="boneCapacity"/> palette entries.</summary>
        public static Prepared Prepare(SkinBakeProbeCpuBake.Inputs inputs, int boneCapacity)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (boneCapacity < 1) throw new ArgumentOutOfRangeException(nameof(boneCapacity));
            int n = inputs.vertexCount;
            var p = new Prepared { VertexCount = n, MaxInfluences = inputs.maxInfluences, HasUv = inputs.uv != null, BoneCapacity = boneCapacity };
            p.positions = new NativeArray<float3>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            p.normals = new NativeArray<float3>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            p.uv = new NativeArray<float2>(p.HasUv ? Math.Max(n, 1) : 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            p.weightStart = new NativeArray<int>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            p.weightCount = new NativeArray<int>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int w = inputs.weights != null ? inputs.weights.Length : 0;
            p.bones = new NativeArray<int>(Math.Max(w, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            p.weights = new NativeArray<float>(Math.Max(w, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            p.palette = new NativeArray<float4x4>(boneCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            p.output = new NativeArray<VpRenderVertex>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            p.marker = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < n; i++)
            {
                p.positions[i] = inputs.positions[i];
                p.normals[i] = inputs.normals[i];
                if (p.HasUv) p.uv[i] = inputs.uv[i];
                p.weightStart[i] = inputs.weightStart[i];
                p.weightCount[i] = inputs.weightCount[i];
            }

            for (int i = 0; i < w; i++) { p.bones[i] = inputs.weights[i].boneIndex; p.weights[i] = inputs.weights[i].weight; }
            p.BytesAllocated = (long)p.positions.Length * 12 + (long)p.normals.Length * 12 + (long)p.uv.Length * 8 + (long)p.weightStart.Length * 4 + (long)p.weightCount.Length * 4
                + (long)p.bones.Length * 4 + (long)p.weights.Length * 4 + (long)p.palette.Length * 64 + (long)p.output.Length * 16 + 4;
            for (int b = 0; b < boneCapacity; b++) p.palette[b] = float4x4.identity;
            p.BoneCount = 1;
            return p;
        }

        /// <summary>Copies <paramref name="palette"/> into the prepared room (once per fixed pose, not per call).</summary>
        public static void SetPalette(Prepared p, float4x4[] palette)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (palette == null || palette.Length == 0) throw new ArgumentException("a palette of at least one bone", nameof(palette));
            if (palette.Length > p.BoneCapacity) throw new ArgumentOutOfRangeException(nameof(palette), "more bones than the prepared room (" + p.BoneCapacity + ")");
            for (int b = 0; b < palette.Length; b++) p.palette[b] = palette[b];
            p.BoneCount = palette.Length;
        }

        /// <summary>
        /// Runs the kernel synchronously over the prepared inputs and the set palette into the prepared output: the
        /// invalid count; <paramref name="burstRan"/> false where the managed fallback ran instead of the Burst entry.
        /// </summary>
        public static int Run(Prepared p, out bool burstRan)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (p.IsDisposed) throw new ObjectDisposedException(nameof(Prepared));
            p.marker[0] = 0;
            int invalid = Skin((float3*)p.positions.GetUnsafeReadOnlyPtr(), (float3*)p.normals.GetUnsafeReadOnlyPtr(), (float2*)p.uv.GetUnsafeReadOnlyPtr(), p.HasUv ? 1 : 0,
                (int*)p.weightStart.GetUnsafeReadOnlyPtr(), (int*)p.weightCount.GetUnsafeReadOnlyPtr(), (int*)p.bones.GetUnsafeReadOnlyPtr(), (float*)p.weights.GetUnsafeReadOnlyPtr(),
                (float4x4*)p.palette.GetUnsafeReadOnlyPtr(), p.BoneCount, (VpRenderVertex*)p.output.GetUnsafePtr(), p.VertexCount, (int*)p.marker.GetUnsafePtr());
            burstRan = p.marker[0] == 0;
            return invalid;
        }

        /// <summary>The kernel: the reference's arithmetic, in the reference's order, over pointers. Returns the invalid count.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static int Skin(float3* positions, float3* normals, float2* uv, int hasUv, int* weightStart, int* weightCount, int* bones, float* weights,
            float4x4* palette, int boneCount, VpRenderVertex* output, int vertexCount, int* managedMarker)
        {
            MarkManaged(managedMarker);
            int invalid = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                float4 p = new float4(positions[i], 1f);
                float3 n = normals[i];
                float3 position = float3.zero, normal = float3.zero;
                int start = weightStart[i], count = weightCount[i];
                bool bonesKnown = true;
                for (int k = 0; k < count; k++)
                {
                    int bone = bones[start + k];
                    float w = weights[start + k];
                    if ((uint)bone >= (uint)boneCount) { bonesKnown = false; break; }
                    float4x4 m = palette[bone];
                    position += math.mul(m, p).xyz * w;
                    normal += math.mul(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz), n) * w;
                }

                VpRenderVertex vertex = default;
                vertex.position = position;
                float sum = math.csum(math.abs(normal));
                bool finite = bonesKnown && math.all(math.isfinite(position)) && math.all(math.isfinite(normal)) && math.isfinite(sum) && sum > 0f;
                if (!finite)
                {
                    vertex.normalX = vertex.normalY = -128; invalid++;
                }
                else
                {
                    float2 e = normal.xy / sum;
                    float2 folded = (1f - math.abs(e.yx)) * math.select(new float2(-1f), new float2(1f), e >= 0f);
                    e = math.select(folded, e, normal.z >= 0f);
                    int2 oct = (int2)math.round(math.clamp(e, -1f, 1f) * 127f);
                    vertex.normalX = (sbyte)oct.x; vertex.normalY = (sbyte)oct.y;
                    float2 t = hasUv != 0 ? uv[i] : new float2(0.5f, 0.5f);
                    bool uvOk = math.all(math.isfinite(t)) && t.x >= -VpRenderVertex.UvEndpointTolerance && t.x <= 1f + VpRenderVertex.UvEndpointTolerance
                        && t.y >= -VpRenderVertex.UvEndpointTolerance && t.y <= 1f + VpRenderVertex.UvEndpointTolerance;
                    if (!uvOk) { vertex.normalX = vertex.normalY = -128; invalid++; }
                    else
                    {
                        vertex.u = (byte)(int)math.clamp(math.round(t.x * 256f - .5f), 0f, 255f);
                        vertex.v = (byte)(int)math.clamp(math.round(t.y * 256f - .5f), 0f, 255f);
                    }
                }

                output[i] = vertex;
            }

            return invalid;
        }

        // In Burst the call is discarded, so the marker stays 0: it proves the kernel ran through Burst.
        [BurstDiscard]
        private static void MarkManaged(int* marker) { *marker = 1; }
    }
}
