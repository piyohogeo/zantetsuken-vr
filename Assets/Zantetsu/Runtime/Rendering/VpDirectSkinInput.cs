using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Opt-in synchronous current-pose producer for a cold-validated, immutable, single-submesh 4-weight mesh.
    /// The renderer frame must be rigid/unit-scale (use VpFixedScaleSkinInput for fixed import scale).
    /// The caller must Dispose/recreate before changing mesh contents, bone bindings, or topology. No hot hash scan.
    /// Source rig/mesh are borrowed; native bind input belongs to this instance. Dispose before unloading the rig.
    /// Does not switch renderers, register bodies, upload, or publish Provisional Physics. Main thread only.
    /// </summary>
    public sealed class VpDirectSkinInput : IDisposable
    {
        readonly SkinnedMeshRenderer renderer;
        readonly Mesh mesh;
        readonly Transform[] bones;
        readonly Matrix4x4[] bindposes;
        NativeArray<float3> positions, normals;
        NativeArray<BoneWeight> weights;
        NativeArray<DirectUv> uv;
        NativeArray<int> topology;
        NativeArray<uint> indices;
        NativeArray<float4x4> matrices;
        NativeArray<float3> bounds;
        NativeArray<int> valid;
        bool disposed;
        public int VertexCount { get; }
        public int IndexCount { get; }
        public int TopologyCount { get; }
        internal bool IsAlive => !disposed;

        static bool FourWeights(SkinnedMeshRenderer r) => r.quality == SkinQuality.Bone4
            || (r.quality == SkinQuality.Auto && QualitySettings.skinWeights == SkinWeights.FourBones);

        static bool Rigid(Transform t)
        {
            Matrix4x4 actual = t.localToWorldMatrix;
            Matrix4x4 expected = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            for (int i = 0; i < 16; i++)
                if (!float.IsFinite(actual[i]) || Mathf.Abs(actual[i] - expected[i]) > 1e-5f) return false;
            return true;
        }

        /// <summary>Cold only. Unsupported/invalid input returns false without changing the renderer or storage.</summary>
        public static bool TryCreate(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            out VpDirectSkinInput input)
        {
            input = null;
            if (VpRenderVertex.Stride != 16 || renderer == null || renderer.sharedMesh == null
                || topology == null || topologyCount <= 0 || !FourWeights(renderer) || !Rigid(renderer.transform)) return false;
            Mesh mesh = renderer.sharedMesh;
            if (!mesh.isReadable || mesh.blendShapeCount != 0 || mesh.subMeshCount != 1
                || mesh.GetTopology(0) != MeshTopology.Triangles || mesh.vertexCount == 0) return false;
            using (var counts = mesh.GetBonesPerVertex())
            {
                if (counts.Length != mesh.vertexCount) return false;
                foreach (byte count in counts) if (count == 0 || count > 4) return false;
            }
            var p = mesh.vertices; var n = mesh.normals; var tex = mesh.uv; var w = mesh.boneWeights;
            var sourceBones = renderer.bones; var binds = mesh.bindposes; var signedIndices = mesh.triangles;
            if (p.Length != n.Length || p.Length != tex.Length || p.Length != w.Length
                || p.Length != topology.Length || sourceBones.Length != binds.Length || signedIndices.Length == 0) return false;
            var used = new SortedSet<int>();
            for (int i = 0; i < w.Length; i++)
            {
                var value = w[i];
                int[] ids = { value.boneIndex0, value.boneIndex1, value.boneIndex2, value.boneIndex3 };
                float[] ws = { value.weight0, value.weight1, value.weight2, value.weight3 };
                if (ws.Any(x => !float.IsFinite(x) || x < 0) || Mathf.Abs(ws.Sum() - 1f) > 1e-5f) return false;
                for (int j = 0; j < 4; j++) if (ws[j] > 0)
                {
                    int bone = ids[j];
                    if (bone < 0 || bone >= sourceBones.Length || sourceBones[bone] == null) return false;
                    for (int k = 0; k < 16; k++) if (!float.IsFinite(binds[bone][k])) return false;
                    used.Add(bone);
                }
            }
            int[] usedBones = used.ToArray();
            if (usedBones.Length == 0) return false;
            int Remap(int bone, float weight) => weight == 0 ? 0 : Array.IndexOf(usedBones, bone);
            for (int i = 0; i < w.Length; i++)
            {
                var x = w[i]; x.boneIndex0 = Remap(x.boneIndex0, x.weight0); x.boneIndex1 = Remap(x.boneIndex1, x.weight1);
                x.boneIndex2 = Remap(x.boneIndex2, x.weight2); x.boneIndex3 = Remap(x.boneIndex3, x.weight3); w[i] = x;
            }
            var vertices = new VpRenderVertex[p.Length]; var bytes = new DirectUv[p.Length];
            var representatives = Enumerable.Repeat(-1, topologyCount).ToArray();
            for (int i = 0; i < p.Length; i++)
            {
                int id = topology[i];
                if (id < 0 || id >= topologyCount) return false;
                int previous = representatives[id];
                if (previous < 0) representatives[id] = i;
                else if (!p[previous].Equals(p[i]) || !w[previous].Equals(w[i])) return false;
                vertices[i] = new VpRenderVertex { position = p[i], normal = n[i], uv0 = tex[i] };
                if (!vertices[i].HasValidAttributes) return false;
                bytes[i] = new DirectUv { x = vertices[i].u, y = vertices[i].v };
            }
            uint[] ix = new uint[signedIndices.Length]; var referenced = new bool[p.Length];
            for (int i = 0; i < ix.Length; i++)
            {
                int v = signedIndices[i]; if (v < 0 || v >= p.Length) return false;
                ix[i] = (uint)v; referenced[v] = true;
            }
            if (referenced.Any(x => !x) || !VpCutInputGate.Check(vertices, ix, topology, topologyCount,
                    new[] { new VpGeometrySubmesh(0, ix.Length, 0) }).Accepted) return false;
            input = new VpDirectSkinInput(renderer, mesh, usedBones.Select(i => sourceBones[i]).ToArray(),
                usedBones.Select(i => binds[i]).ToArray(), p, n, w, bytes, topology, topologyCount, ix);
            return true;
        }

        VpDirectSkinInput(SkinnedMeshRenderer renderer, Mesh mesh, Transform[] bones, Matrix4x4[] bindposes,
            Vector3[] p, Vector3[] n, BoneWeight[] w, DirectUv[] tex, int[] topo, int topologyCount, uint[] ix)
        {
            this.renderer = renderer; this.mesh = mesh; this.bones = bones; this.bindposes = bindposes;
            VertexCount = p.Length; IndexCount = ix.Length; TopologyCount = topologyCount;
            try
            {
                positions = new NativeArray<float3>(p.Select(x => (float3)x).ToArray(), Allocator.Persistent);
                normals = new NativeArray<float3>(n.Select(x => (float3)x).ToArray(), Allocator.Persistent);
                weights = new NativeArray<BoneWeight>(w, Allocator.Persistent);
                uv = new NativeArray<DirectUv>(tex, Allocator.Persistent);
                topology = new NativeArray<int>(topo, Allocator.Persistent);
                indices = new NativeArray<uint>(ix, Allocator.Persistent);
                matrices = new NativeArray<float4x4>(bones.Length, Allocator.Persistent);
                bounds = new NativeArray<float3>(2, Allocator.Persistent);
                valid = new NativeArray<int>(1, Allocator.Persistent);
                Warm();
            }
            catch { Dispose(); throw; }
        }

        unsafe void Warm()
        {
            VpDirectSkinKernel.Skin(null, null, null, null, null, null, 0, null, null,
                (float3*)bounds.GetUnsafePtr(), (int*)valid.GetUnsafePtr());
            VpDirectSkinKernel.CopyIndices(null, null, 0, 0);
        }

        /// <summary>
        /// Current bone transforms -> private matrices -> reserved 16B storage, synchronously.
        /// False is no publication (unsupported changed frame/quality, invalid generated pose, or storage capacity).
        /// A source changed in place must have been invalidated by Dispose; no per-hit mesh or binding scan occurs.
        /// </summary>
        public bool TryAppendTo(VpCpuGeometryStorage storage, out VpStoredGeometry geometry)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpDirectSkinInput));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            geometry = default;
            if (renderer == null || mesh == null || renderer.sharedMesh != mesh
                || !FourWeights(renderer) || !Rigid(renderer.transform)) return false;
            Matrix4x4 inverse = renderer.transform.worldToLocalMatrix;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) return false;
                Matrix4x4 m = inverse * bones[i].localToWorldMatrix * bindposes[i];
                var value = new float4x4(m.GetColumn(0), m.GetColumn(1), m.GetColumn(2), m.GetColumn(3));
                if (!math.all(math.isfinite(value.c0)) || !math.all(math.isfinite(value.c1))
                    || !math.all(math.isfinite(value.c2)) || !math.all(math.isfinite(value.c3))) return false;
                matrices[i] = value;
            }
            return storage.TryAppendDirectSkin(this, out geometry);
        }

        public bool IsDisposed => disposed;

        /// <summary>Same synchronous output, with an unforgeable producer/storage receipt for cold display preparation.</summary>
        public bool TryAppendForDisplay(VpCpuGeometryStorage storage, out VpDirectSkinOutput output)
        {
            output = default;
            if (!TryAppendTo(storage, out var geometry)) return false;
            output = new VpDirectSkinOutput(this, storage, geometry);
            return true;
        }

        internal unsafe bool Write(NativeArray<VpRenderVertex> output, NativeArray<int> outputTopology,
            NativeArray<uint> outputIndices, int baseVertex, out float3 min, out float3 max)
        {
            VpDirectSkinKernel.Skin((float3*)positions.GetUnsafeReadOnlyPtr(), (float3*)normals.GetUnsafeReadOnlyPtr(),
                (BoneWeight*)weights.GetUnsafeReadOnlyPtr(), (DirectUv*)uv.GetUnsafeReadOnlyPtr(),
                (float4x4*)matrices.GetUnsafeReadOnlyPtr(), (int*)topology.GetUnsafeReadOnlyPtr(), VertexCount,
                (VpRenderVertex*)output.GetUnsafePtr(), (int*)outputTopology.GetUnsafePtr(),
                (float3*)bounds.GetUnsafePtr(), (int*)valid.GetUnsafePtr());
            min = bounds[0]; max = bounds[1];
            if (valid[0] == 0 || !math.all(math.isfinite(min)) || !math.all(math.isfinite(max)) || !math.all(min <= max)) return false;
            VpDirectSkinKernel.CopyIndices((uint*)indices.GetUnsafeReadOnlyPtr(), (uint*)outputIndices.GetUnsafePtr(),
                IndexCount, (uint)baseVertex);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (positions.IsCreated) positions.Dispose(); if (normals.IsCreated) normals.Dispose();
            if (weights.IsCreated) weights.Dispose(); if (uv.IsCreated) uv.Dispose();
            if (topology.IsCreated) topology.Dispose(); if (indices.IsCreated) indices.Dispose();
            if (matrices.IsCreated) matrices.Dispose(); if (bounds.IsCreated) bounds.Dispose();
            if (valid.IsCreated) valid.Dispose();
        }
    }

    internal struct DirectUv { public byte x, y; }

    [BurstCompile]
    internal static class VpDirectSkinKernel
    {
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static unsafe void Skin(float3* bind, float3* normals, BoneWeight* weights, DirectUv* uv,
            float4x4* matrices, int* topology, int count, VpRenderVertex* output, int* outputTopology,
            float3* bounds, int* valid)
        {
            float3 min = new float3(float.PositiveInfinity), max = new float3(float.NegativeInfinity);
            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                BoneWeight w = weights[i]; float4 p = new float4(bind[i], 1); float3 n = normals[i];
                float3 position = (math.mul(matrices[w.boneIndex0], p) * w.weight0 + math.mul(matrices[w.boneIndex1], p) * w.weight1
                    + math.mul(matrices[w.boneIndex2], p) * w.weight2 + math.mul(matrices[w.boneIndex3], p) * w.weight3).xyz;
                float3 normal = math.mul((float3x3)matrices[w.boneIndex0], n) * w.weight0 + math.mul((float3x3)matrices[w.boneIndex1], n) * w.weight1
                    + math.mul((float3x3)matrices[w.boneIndex2], n) * w.weight2 + math.mul((float3x3)matrices[w.boneIndex3], n) * w.weight3;
                float sum = math.csum(math.abs(normal));
                bool finite = math.all(math.isfinite(position)) && math.all(math.isfinite(normal)) && math.isfinite(sum) && sum > 0;
                ok &= finite;
                float2 e = normal.xy / (finite ? sum : 1f);
                float2 folded = (1 - math.abs(e.yx)) * math.select(new float2(-1), new float2(1), e >= 0);
                e = math.select(folded, e, normal.z >= 0);
                int2 oct = (int2)math.round(math.clamp(e, -1, 1) * 127);
#if VP_DIAGNOSTIC_LEGACY32
                // Keeps the old comparison build compilable; TryCreate refuses its 32B stride.
                output[i] = new VpRenderVertex { position = position, normal = normal, uv0 = new Vector2((uv[i].x + .5f) / 256f, (uv[i].y + .5f) / 256f) };
#else
                output[i] = new VpRenderVertex { position = position, normalX = (sbyte)oct.x, normalY = (sbyte)oct.y, u = uv[i].x, v = uv[i].y };
#endif
                outputTopology[i] = topology[i]; min = math.min(min, position); max = math.max(max, position);
            }
            bounds[0] = min; bounds[1] = max; valid[0] = ok ? 1 : 0;
        }

        [BurstCompile(CompileSynchronously = true)]
        public static unsafe void CopyIndices(uint* source, uint* output, int count, uint baseVertex)
        { for (int i = 0; i < count; i++) output[i] = source[i] + baseVertex; }
    }
}
