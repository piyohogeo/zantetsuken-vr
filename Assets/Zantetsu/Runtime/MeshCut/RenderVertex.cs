using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// AoS render vertex of the CPU-side vertex-pulling pool (DESIGN 4.5.1). The attribute set and stride are fixed
    /// at this implementation point: position, normal, uv0, tangent (xyz + handedness w), 48 bytes. Every channel is
    /// stored as float, so the negative cap marker of DESIGN 5.3 (<see cref="RenderCutMarker.CapUv"/>) survives in uv0
    /// without a dedicated field, vertex colour or second UV channel. Adding an attribute means updating this struct,
    /// the kernel's interpolation and the vertex-pulling shader together.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenderVertex
    {
        public float3 position;
        public float3 normal;
        public float2 uv0;
        public float4 tangent;

        public const int Stride = 48;
    }

    /// <summary>The fixed cap marker (DESIGN 5.3): every render vertex of a generated cap carries uv0 = (-0.5, 0).</summary>
    public static class RenderCutMarker
    {
        public const float CapUvX = -0.5f;
        public const float CapUvY = 0f;
        public static float2 CapUv => new float2(CapUvX, CapUvY);
    }
}
