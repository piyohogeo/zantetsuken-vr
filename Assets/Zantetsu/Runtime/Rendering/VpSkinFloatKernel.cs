using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The float-input skinning kernel of the GPU-first route (DESIGN 4.5.15): the arithmetic of the GPU bake
    /// (VpSkinBake.compute) over the shared input's float positions, normals, UV0, effective weights and one fixed
    /// palette, written as 16-byte render vertices with their topology and the bounds of the finite positions. It is the
    /// deferred CPU bake a worker runs (DESIGN 4.3: a Burst direct call over immutable inputs and caller-owned output),
    /// and the synchronous float contrast. The existing 24-byte CPU bake (VpDirectSkinKernel) is not used here: the two
    /// sides of a GPU-first cut begin from the same float numbers, so that the CPU geometry the cut takes matches the
    /// GPU geometry the display showed to within float arithmetic, not to within the 24-byte input's quantisation.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public static unsafe class VpSkinFloatKernel
    {
        /// <summary>
        /// Skins <paramref name="vertexCount"/> vertices: position = sum of w (M p), normal = sum of w (M3 n), L1-normalised
        /// and oct-encoded; UV quantised to bytes; a non-finite result, a zero normal, a bone outside the palette or an
        /// unsupported UV marks the vertex invalid (-128, -128) and is counted. Each vertex's topology id is copied;
        /// <paramref name="bounds"/>[0..1] take the min and max of the finite positions (+inf/-inf when none). Returns the
        /// invalid count. <paramref name="managedMarker"/> is set only by the managed fallback.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static int Skin(float3* positions, float3* normals, float2* uv, int hasUv, int* weightStart, int* weightCount, int* bones, float* weights,
            float4x4* palette, int boneCount, int* topologyIn, VpRenderVertex* output, int* topologyOut, int vertexCount, float3* bounds, int* managedMarker)
        {
            MarkManaged(managedMarker);
            int invalid = 0;
            float3 min = new float3(float.PositiveInfinity), max = new float3(float.NegativeInfinity);
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

                    min = math.min(min, position); max = math.max(max, position);
                }

                output[i] = vertex;
                topologyOut[i] = topologyIn[i];
            }

            bounds[0] = min; bounds[1] = max;
            return invalid;
        }

        // In Burst the call is discarded, so the marker stays 0: it proves the kernel ran through Burst.
        [BurstDiscard]
        private static void MarkManaged(int* marker) { *marker = 1; }
    }
}
