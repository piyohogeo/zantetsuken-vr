using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The fixed cap marker (DESIGN 5.3): every render vertex of a generated cap carries uv0 = (-0.5, 0). The render
    /// vertex itself is the one common type <see cref="Zantetsu.Rendering.VpRenderVertex"/> (position, normal, uv0,
    /// 32 bytes), so the negative marker survives in uv0 without a dedicated field or a second UV channel.
    /// </summary>
    public static class RenderCutMarker
    {
        public const float CapUvX = -0.5f;
        public const float CapUvY = 0f;
        public static float2 CapUv => new float2(CapUvX, CapUvY);
    }
}
