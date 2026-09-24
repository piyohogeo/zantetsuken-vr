using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// AoS vertex of the vertex-pulling pool (DESIGN 4.5.1). The attribute set and stride are fixed at this
    /// implementation point: float3 position, oct8x2 normal, uint8x2 UV, 16 bytes. Adding an attribute means updating this struct, the mesh
    /// conversion and the vertex-pulling shader together.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpRenderVertex
    {
        public Vector3 position;
        public sbyte normalX, normalY;
        public byte u, v;

        public const int Stride = 16;
        public const float CapUvCentre = 247.5f / 256f;
        // Two float ULPs at 1 accommodate imported endpoint roundoff (Unity Sphere contains 1.00000012).
        public const float UvEndpointTolerance = 2.3841858e-7f;

        // -128 is not emitted by oct encoding. It marks invalid attributes so registration can refuse them.
        public bool HasValidAttributes => normalX != -128 && normalY != -128;
        public Vector3 normal
        {
            get
            {
                if (!HasValidAttributes) return new Vector3(float.NaN, float.NaN, float.NaN);
                float2 e = new float2(normalX, normalY) / 127f;
                float3 n = new float3(e, 1f - math.abs(e.x) - math.abs(e.y));
                if (n.z < 0f) n.xy = (1f - math.abs(n.yx)) * math.select(new float2(-1f), new float2(1f), e >= 0f);
                return math.normalize(n);
            }
            set
            {
                // Invalid is sticky until the whole value is replaced, irrespective of initializer order.
                if (!HasValidAttributes) return;
                float3 n = value;
                float sum = math.csum(math.abs(n));
                if (!math.all(math.isfinite(n)) || !math.isfinite(sum) || sum <= 0f) { normalX = normalY = -128; return; }
                float2 e = n.xy / sum;
                float2 folded = (1f - math.abs(e.yx)) * math.select(new float2(-1f), new float2(1f), e >= 0f);
                e = math.select(folded, e, n.z >= 0f);
                normalX = (sbyte)(int)math.round(math.clamp(e.x, -1f, 1f) * 127f);
                normalY = (sbyte)(int)math.round(math.clamp(e.y, -1f, 1f) * 127f);
            }
        }

        public Vector2 uv0
        {
            get => new Vector2((u + .5f) / 256f, (v + .5f) / 256f);
            set
            {
                // Accept the normalized closed domain, with explicit endpoint saturation to the nearest centre.
                // Out-of-domain inputs are invalid, not wrapped or silently clamped into a supported asset.
                if (!IsSupportedUv(value)) { normalX = normalY = -128; return; }
                u = (byte)(int)math.clamp(math.round(value.x * 256f - .5f), 0f, 255f);
                v = (byte)(int)math.clamp(math.round(value.y * 256f - .5f), 0f, 255f);
            }
        }

        public static bool IsSupportedUv(Vector2 value) => math.all(math.isfinite((float2)value)) && value.x >= -UvEndpointTolerance && value.x <= 1f + UvEndpointTolerance && value.y >= -UvEndpointTolerance && value.y <= 1f + UvEndpointTolerance;
    }
}
