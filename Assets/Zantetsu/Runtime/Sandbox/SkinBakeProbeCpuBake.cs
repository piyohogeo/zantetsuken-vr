using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The bake probe's CPU reference (DESIGN 4.5.14): the float-input kernel of the direct skin input as it stood before
    /// the 24-byte model (f796d782's predecessor, VpDirectSkinKernel.Skin), over the prepared mesh's own attributes, its
    /// effective weights and one fixed palette -- no quantised normal or weight on the way in; the normal and the UV are
    /// encoded once, at the 16-byte output, the way the render vertex encodes them. The attributes are read as the GPU
    /// holds them: an attribute stored as Float16 is rounded through a half here, so that the two sides begin from the
    /// same numbers.
    /// </summary>
    public static class SkinBakeProbeCpuBake
    {
        /// <summary>The mesh's inputs, read once: positions, normals, UV0 (optional), and each vertex's weights in order.</summary>
        public sealed class Inputs
        {
            public int vertexCount;
            public float3[] positions;
            public float3[] normals;
            public float2[] uv;          // null when the mesh has no UV0
            public int[] weightStart;     // into weights, a vertex
            public int[] weightCount;
            public BoneWeight1[] weights; // the mesh's effective weights (GetAllBoneWeights), vertex order
            public int maxInfluences;
            public uint[] indices;        // every submesh's indices in order, mesh-local
            public int[] submeshIndexStart, submeshIndexCount;
        }

        /// <summary>Reads the inputs of <paramref name="input"/>'s mesh (the mesh is readable: the preparation checked).</summary>
        public static Inputs Read(SkinBakeProbeMeshInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            Mesh mesh = input.Mesh;
            var r = new Inputs { vertexCount = mesh.vertexCount };
            Vector3[] p = mesh.vertices, n = mesh.normals;
            Vector2[] uv = input.Uv.present ? mesh.uv : null;
            r.positions = new float3[r.vertexCount];
            r.normals = new float3[r.vertexCount];
            r.uv = uv != null && uv.Length == r.vertexCount ? new float2[r.vertexCount] : null;
            bool halfP = input.Position.format == VertexAttributeFormat.Float16, halfN = input.Normal.format == VertexAttributeFormat.Float16, halfUv = input.Uv.present && input.Uv.format == VertexAttributeFormat.Float16;
            for (int v = 0; v < r.vertexCount; v++)
            {
                r.positions[v] = halfP ? ThroughHalf(p[v]) : (float3)p[v];
                r.normals[v] = halfN ? ThroughHalf(n[v]) : (float3)n[v];
                if (r.uv != null) r.uv[v] = halfUv ? ThroughHalf((float2)uv[v]) : (float2)uv[v];
            }

            using (NativeArray<byte> counts = mesh.GetBonesPerVertex())
            using (NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights())
            {
                r.weightStart = new int[r.vertexCount];
                r.weightCount = new int[r.vertexCount];
                r.weights = weights.ToArray();
                int at = 0;
                for (int v = 0; v < r.vertexCount; v++)
                {
                    r.weightStart[v] = at; r.weightCount[v] = counts[v]; at += counts[v];
                    r.maxInfluences = Math.Max(r.maxInfluences, counts[v]);
                }

                if (at != r.weights.Length) throw new InvalidOperationException("the bones per vertex sum to " + at + " but there are " + r.weights.Length + " weights");
            }

            r.submeshIndexStart = new int[mesh.subMeshCount];
            r.submeshIndexCount = new int[mesh.subMeshCount];
            int total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) { r.submeshIndexStart[s] = total; r.submeshIndexCount[s] = (int)mesh.GetIndexCount(s); total += r.submeshIndexCount[s]; }
            r.indices = new uint[total];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] sub = mesh.GetIndices(s, false);   // mesh-local, no base vertex applied
                for (int k = 0; k < sub.Length; k++) r.indices[r.submeshIndexStart[s] + k] = (uint)sub[k];
            }

            return r;
        }

        private static float3 ThroughHalf(float3 v) => new float3(Mathf.HalfToFloat(Mathf.FloatToHalf(v.x)), Mathf.HalfToFloat(Mathf.FloatToHalf(v.y)), Mathf.HalfToFloat(Mathf.FloatToHalf(v.z)));
        private static float2 ThroughHalf(float2 v) => new float2(Mathf.HalfToFloat(Mathf.FloatToHalf(v.x)), Mathf.HalfToFloat(Mathf.FloatToHalf(v.y)));

        /// <summary>
        /// Skins every vertex by <paramref name="palette"/> (renderer-local: inverse(renderer) x bone x bindPose, one a
        /// bone of the mesh) and writes the 16-byte render vertices: position = sum of w (M p); normal = sum of w (M3 n),
        /// L1-normalised and oct-encoded; UV quantised to bytes; a non-finite position or normal, a zero normal or an
        /// unsupported UV marks the vertex invalid (-128, -128) and is counted. Returns the invalid count; the bounds of
        /// the positions in min/max.
        /// </summary>
        public static int Bake(Inputs inputs, float4x4[] palette, VpRenderVertex[] output, out float3 min, out float3 max)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (output == null || output.Length < inputs.vertexCount) throw new ArgumentException("the output holds fewer than the vertices", nameof(output));
            min = new float3(float.PositiveInfinity); max = new float3(float.NegativeInfinity);
            int invalid = 0;
            for (int i = 0; i < inputs.vertexCount; i++)
            {
                float4 p = new float4(inputs.positions[i], 1f);
                float3 n = inputs.normals[i];
                float3 position = float3.zero, normal = float3.zero;
                int start = inputs.weightStart[i], count = inputs.weightCount[i];
                bool bonesKnown = true;
                for (int k = 0; k < count; k++)
                {
                    BoneWeight1 w = inputs.weights[start + k];
                    if ((uint)w.boneIndex >= (uint)palette.Length) { bonesKnown = false; break; }
                    float4x4 m = palette[w.boneIndex];
                    position += math.mul(m, p).xyz * w.weight;
                    normal += math.mul(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz), n) * w.weight;
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
                    float2 uv = inputs.uv != null ? inputs.uv[i] : new float2(0.5f, 0.5f);
                    if (!VpRenderVertex.IsSupportedUv(uv)) { vertex.normalX = vertex.normalY = -128; invalid++; }
                    else
                    {
                        vertex.u = (byte)(int)math.clamp(math.round(uv.x * 256f - .5f), 0f, 255f);
                        vertex.v = (byte)(int)math.clamp(math.round(uv.y * 256f - .5f), 0f, 255f);
                    }
                }

                output[i] = vertex;
                if (finite) { min = math.min(min, position); max = math.max(max, position); }
            }

            return invalid;
        }

        /// <summary>The renderer-local palette of a drawn SkinnedMeshRenderer, one matrix a bone: inverse(renderer) x bone x bindPose; a missing bone gives the identity (counted).</summary>
        public static float4x4[] Palette(SkinnedMeshRenderer renderer, out int missingBones)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            Transform[] bones = renderer.bones;
            Matrix4x4[] bind = renderer.sharedMesh.bindposes;
            int count = Math.Min(bones.Length, bind.Length);
            var palette = new float4x4[Math.Max(count, 1)];
            Matrix4x4 inverse = renderer.transform.worldToLocalMatrix;
            missingBones = 0;
            for (int b = 0; b < count; b++)
            {
                if (bones[b] == null) { palette[b] = float4x4.identity; missingBones++; continue; }
                palette[b] = inverse * bones[b].localToWorldMatrix * bind[b];
            }

            if (count == 0) palette[0] = float4x4.identity;
            return palette;
        }
    }
}
