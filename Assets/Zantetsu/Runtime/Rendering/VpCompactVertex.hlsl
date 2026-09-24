#ifndef ZANTETSU_VP_COMPACT_VERTEX_INCLUDED
#define ZANTETSU_VP_COMPACT_VERTEX_INCLUDED
// One CPU/GPU ABI: float3 position + signed oct8x2 + uint8x2 UV, 16 bytes.
struct VpRenderVertex
{
    float3 position;
    uint packedAttributes;
};
float3 VpDecodeNormal(VpRenderVertex vertex)
{
    uint p = vertex.packedAttributes;
    int x = (int)(p & 255u), y = (int)((p >> 8) & 255u);
    x = x > 127 ? x - 256 : x;
    y = y > 127 ? y - 256 : y;
    float2 e = float2(x, y) / 127.0;
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * float2(e.x >= 0 ? 1 : -1, e.y >= 0 ? 1 : -1);
    return normalize(n);
}
float2 VpDecodeUv(VpRenderVertex vertex)
{
    return (float2((vertex.packedAttributes >> 16) & 255u, vertex.packedAttributes >> 24) + 0.5) / 256.0;
}
bool VpIsRealCap(float2 rawUv)
{
    return all(abs(rawUv - (247.5 / 256.0)) < (0.25 / 256.0));
}
#endif
