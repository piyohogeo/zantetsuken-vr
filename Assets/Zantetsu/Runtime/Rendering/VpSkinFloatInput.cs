using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One vertex of a model's CPU-only float input (CPU-SKIN-FLOAT-1): the bind position and normal as the mesh holds them,
    /// the four influences' float weights and palette bone numbers, and the UV bytes of the 16-byte vertex (encoded once,
    /// at the preparation: nothing of the UV depends on the pose). 48 bytes, read by <see cref="VpSkinFloatInputKernel"/>
    /// alone; nothing of the GPU reads it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpSkinFloatVertex
    {
        public const int Stride = 48;

        public float3 position;
        public float3 normal;
        public float4 weights;
        public byte bone0, bone1, bone2, bone3;
        public byte u, v;
        public ushort pad;
    }

    /// <summary>
    /// A model's CPU-only float input for the synchronous CPU bake (CPU-SKIN-FLOAT-1, <c>-zantetsuSkinBake cpu-float</c>): the
    /// source mesh's float positions, float normals and float weights with their bone numbers, and the 16-byte vertex's UV bytes,
    /// one immutable native array a model, shared by every individual of the model and freed with it. It is made cold, once,
    /// only when the launch asks for the route (<see cref="VpSkinModel.TryPrepareFloatInput"/>), from a readable mesh, with
    /// the checks of the model (the counts, finite non-negative weights summing to one, valid bone numbers, supported
    /// attributes) and one more: the positions must be the model's own, so that the two inputs describe one mesh. It
    /// touches no Raw target, no GPU buffer, no snapshot and no palette buffer; the kernel reads it and the captured palette
    /// alone. The topology, the indices and the bind poses stay the model's (not copied here).
    /// <para>
    /// **What differs from the 24-byte bake.** The 24-byte input quantizes the normal to two octahedral bytes and each
    /// weight to an integer of 255 at the preparation; this input keeps the floats, so the bake skins the mesh's own
    /// normal and weights and quantizes only the output (the 16-byte vertex's normal). The two bakes therefore differ by
    /// those quantizations, never by the arithmetic's order. The 24-byte input stays the model's (the skin draw's
    /// vertex stage reads it, and a body the skin draw shows is cut from it): this input is beside it, 48 bytes a vertex.
    /// </para>
    /// </summary>
    public sealed unsafe class VpSkinFloatInput : IDisposable
    {
        private NativeArray<VpSkinFloatVertex> _vertices;
        private bool _disposed;

        /// <summary>How many float inputs were made in this process (measurement and tests).</summary>
        public static int CreatedCount { get; private set; }

        /// <summary>Tests only: when not null, every preparation is refused with this reason (a model whose float input cannot be made, under the default route).</summary>
        public static string RefuseForTest;

        private VpSkinFloatInput(NativeArray<VpSkinFloatVertex> vertices, long began)
        {
            // The persistent array, filled by TryCreate straight from the mesh's arrays (no managed copy of the vertices: DEFAULT-1).
            _vertices = vertices;
            VertexCount = vertices.Length;
            // The preparation's time ends after the persistent array is allocated and filled (review 1, P3).
            PreparationSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            CreatedCount++;
        }

        public int VertexCount { get; }

        /// <summary>The main-thread time the preparation took (the mesh read, the checks, the persistent array's allocation and copy), in seconds.</summary>
        public double PreparationSeconds { get; }

        public bool IsDisposed => _disposed;

        /// <summary>The bytes this input holds natively (48 a vertex); 0 once disposed.</summary>
        public long NativeBytes => _disposed ? 0 : (long)_vertices.Length * VpSkinFloatVertex.Stride;

        /// <summary>The vertices; a view, valid until the input is disposed.</summary>
        public NativeArray<VpSkinFloatVertex>.ReadOnly Vertices { get { ThrowIfDisposed(); return _vertices.AsReadOnly(); } }

        internal VpSkinFloatVertex* VertexPointer { get { ThrowIfDisposed(); return (VpSkinFloatVertex*)_vertices.GetUnsafeReadOnlyPtr(); } }

        /// <summary>
        /// Cold only. The float input of <paramref name="model"/>'s source mesh: false, with the reason and nothing made, when
        /// the mesh is not readable, its arrays are not of the model's length, a position is not the model's, a normal is not
        /// finite or zero, a UV is not supported, or a weight is not finite, negative, or the four do not sum to one.
        /// </summary>
        public static bool TryCreate(VpSkinModel model, out VpSkinFloatInput input, out string refusal)
        {
            input = null;
            refusal = null;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (model == null || model.IsDisposed) { refusal = "no model"; return false; }
            if (RefuseForTest != null) { refusal = RefuseForTest; return false; }
            Mesh mesh = model.SourceMesh;
            if (mesh == null) { refusal = "the model has no source mesh"; return false; }
            if (!mesh.isReadable) { refusal = "the mesh is not readable"; return false; }
            Vector3[] p = mesh.vertices;
            Vector3[] n = mesh.normals;
            Vector2[] tex = mesh.uv;
            BoneWeight[] w = mesh.boneWeights;
            int count = model.VertexCount;
            if (p.Length != count || n.Length != count || tex.Length != count || w.Length != count)
            {
                refusal = "the mesh's positions, normals, UVs and weights are not of the model's length (" + count + ")";
                return false;
            }

            // The persistent array is filled straight from the mesh's arrays: no managed copy of the vertices and no array a vertex
            // (the mono heap grew by the garbage of 49 models' preparation before, DEFAULT-1); a refusal frees it again.
            var vertices = new NativeArray<VpSkinFloatVertex>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            refusal = Fill(model, p, n, tex, w, vertices);
            if (refusal != null) { vertices.Dispose(); return false; }
            input = new VpSkinFloatInput(vertices, began);
            return true;
        }

        // Converts and checks every vertex into the array given; the refusal, or null when every vertex was taken.
        private static string Fill(VpSkinModel model, Vector3[] p, Vector3[] n, Vector2[] tex, BoneWeight[] w, NativeArray<VpSkinFloatVertex> vertices)
        {
            int count = vertices.Length;
            int boneCount = model.BoneCount;
            NativeArray<VpSkinVertex>.ReadOnly packed = model.Vertices;
            for (int i = 0; i < count; i++)
            {
                VpSkinVertex q = packed[i];
                if (!p[i].Equals(q.position)) return "vertex " + i + "'s position is not the model's";
                float3 normal = n[i];
                if (!math.all(math.isfinite(normal)) || math.lengthsq(normal) <= 0f) return "vertex " + i + " has a normal that is not finite or zero";
                if (!VpRenderVertex.IsSupportedUv(tex[i])) return "vertex " + i + " has a UV out of the supported range";
                BoneWeight bw = w[i];
                float4 weights = new float4(bw.weight0, bw.weight1, bw.weight2, bw.weight3);
                if (!math.all(math.isfinite(weights)) || math.any(weights < 0f)) return "vertex " + i + " has a weight that is not finite or negative";
                float sum = math.csum(weights);
                if (!math.isfinite(sum) || math.abs(sum - 1f) > VpSkinWeights.SumTolerance) return "vertex " + i + "'s weights sum to " + sum + ", not one";
                int4 ids = new int4(bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3);
                int4 bones = int4.zero;
                for (int j = 0; j < 4; j++)
                {
                    if (weights[j] > 0f)
                    {
                        if (ids[j] < 0 || ids[j] >= boneCount) return "vertex " + i + " is weighted to bone " + ids[j] + " of " + boneCount;
                        bones[j] = ids[j];
                    }
                    else
                    {
                        // A place of no weight contributes nothing whatever bone it names: a valid number, so that the kernel reads a matrix that exists.
                        bones[j] = ids[j] >= 0 && ids[j] < boneCount ? ids[j] : 0;
                    }
                }

                // The UV bytes are the model's (the same encoding as the 16-byte vertex's; the 24-byte input made them once).
                vertices[i] = new VpSkinFloatVertex
                {
                    position = p[i], normal = normal, weights = weights,
                    bone0 = (byte)bones.x, bone1 = (byte)bones.y, bone2 = (byte)bones.z, bone3 = (byte)bones.w, u = q.u, v = q.v,
                };
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_vertices.IsCreated) _vertices.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VpSkinFloatInput));
        }
    }

    /// <summary>
    /// The CPU bake of the float input (CPU-SKIN-FLOAT-1): the mesh's float position, normal and weights skinned by the captured
    /// palette -- four terms, in influence order, every term added whatever its weight, as the 24-byte bake adds them -- into
    /// 16-byte vertices, the normal L1-normalised and octahedrally encoded at the output, the UV bytes copied; the topology
    /// copied, the bounds and the validity as <see cref="VpDirectSkinKernel.Skin"/> gives them. Burst, synchronous, over
    /// immutable inputs and the caller's output. Not the experimental float route's kernel (<see cref="VpSkinFloatKernel"/>,
    /// which reads the GPU bake's shared input and variable influence lists): this one reads the model's own array.
    /// </summary>
    [BurstCompile]
    internal static class VpSkinFloatInputKernel
    {
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static unsafe void Skin(VpSkinFloatVertex* input, float4x4* matrices, int* topology, int count,
            VpRenderVertex* output, int* outputTopology, float3* bounds, int* valid)
        {
            float3 min = new float3(float.PositiveInfinity), max = new float3(float.NegativeInfinity);
            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                VpSkinFloatVertex v = input[i];
                float4 p = new float4(v.position, 1f);
                float3 n = v.normal;
                float4 w = v.weights;
                float4x4 m0 = matrices[v.bone0], m1 = matrices[v.bone1], m2 = matrices[v.bone2], m3 = matrices[v.bone3];
                float3 position = (math.mul(m0, p) * w.x + math.mul(m1, p) * w.y + math.mul(m2, p) * w.z + math.mul(m3, p) * w.w).xyz;
                float3 normal = math.mul((float3x3)m0, n) * w.x + math.mul((float3x3)m1, n) * w.y
                    + math.mul((float3x3)m2, n) * w.z + math.mul((float3x3)m3, n) * w.w;
                float sum = math.csum(math.abs(normal));
                bool finite = math.all(math.isfinite(position)) && math.all(math.isfinite(normal)) && math.isfinite(sum) && sum > 0f;
                ok &= finite;
                float2 e = normal.xy / (finite ? sum : 1f);
                float2 folded = (1f - math.abs(e.yx)) * math.select(new float2(-1f), new float2(1f), e >= 0f);
                e = math.select(folded, e, normal.z >= 0f);
                int2 oct = (int2)math.round(math.clamp(e, -1f, 1f) * 127f);
#if VP_DIAGNOSTIC_LEGACY32
                output[i] = new VpRenderVertex { position = position, normal = normal, uv0 = new Vector2((v.u + .5f) / 256f, (v.v + .5f) / 256f) };
#else
                output[i] = new VpRenderVertex { position = position, normalX = (sbyte)oct.x, normalY = (sbyte)oct.y, u = v.u, v = v.v };
#endif
                outputTopology[i] = topology[i]; min = math.min(min, position); max = math.max(max, position);
            }

            bounds[0] = min; bounds[1] = max; valid[0] = ok ? 1 : 0;
        }
    }
}
