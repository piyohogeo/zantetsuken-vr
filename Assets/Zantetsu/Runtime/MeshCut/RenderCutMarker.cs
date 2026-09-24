using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Compact16uv real-cap slot (247,247), shared by CPU pool and GPU. No negative UV or extra field.
    /// </summary>
    public static class RenderCutMarker
    {
        public const float CapUvX = Zantetsu.Rendering.VpRenderVertex.CapUvCentre;
        public const float CapUvY = Zantetsu.Rendering.VpRenderVertex.CapUvCentre;
        public static float2 CapUv => new float2(CapUvX, CapUvY);
    }
}
