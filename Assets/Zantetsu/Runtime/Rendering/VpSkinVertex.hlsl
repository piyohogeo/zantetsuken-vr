#ifndef ZANTETSU_VP_SKIN_VERTEX_INCLUDED
#define ZANTETSU_VP_SKIN_VERTEX_INCLUDED

// The skin draw's vertex and records (DESIGN 4.5.12). Matches Zantetsu.Rendering.VpSkinVertex, 24 bytes: the bind
// position, the attributes of VpRenderVertex (oct8x2 normal, uint8x2 UV), four bone numbers and four weights of 255.
// Declared as six 32-bit words, not a float3 and three uints: a structure holding a float3 takes a 16-byte alignment
// under Vulkan's buffer layout rules, which would widen the stride to 32 and read every vertex but the first wrong.
// The CPU bake (VpDirectSkinKernel) reads the same bytes and the same weights, in the same order.
//
// Requires VpCompactVertex.hlsl (VpRenderVertex, VpDecodeNormal, VpDecodeUv) included before it.

struct VpSkinVertex
{
    uint positionX;
    uint positionY;
    uint positionZ;
    uint packedAttributes;   // as VpRenderVertex.packedAttributes
    uint bones;              // bone0 | bone1 << 8 | bone2 << 16 | bone3 << 24
    uint weights;            // weight0 | weight1 << 8 | weight2 << 16 | weight3 << 24, of 255
};

// Matches Zantetsu.Rendering.VpSkinRecord, 32 bytes: one a logical instance slot.
struct VpSkinRecord
{
    uint paletteStart;
    uint previousPaletteStart;
    uint boneCount;
    uint boneBoxStart;
    uint flags;
    uint pad0, pad1, pad2;
};

#define VP_SKIN_RECORD_ACTIVE 1u

float3 VpSkinBindPosition(VpSkinVertex vertex)
{
    return float3(asfloat(vertex.positionX), asfloat(vertex.positionY), asfloat(vertex.positionZ));
}

// The 16-byte attributes of the vertex, for the shared decoding of the normal and the UV.
VpRenderVertex VpSkinAttributes(VpSkinVertex vertex)
{
    VpRenderVertex attributes;
    attributes.position = VpSkinBindPosition(vertex);
    attributes.packedAttributes = vertex.packedAttributes;
    return attributes;
}

float4 VpSkinWeightsOf(VpSkinVertex vertex)
{
    uint w = vertex.weights;
    return float4(w & 255u, (w >> 8u) & 255u, (w >> 16u) & 255u, w >> 24u) / 255.0;
}

uint4 VpSkinBonesOf(VpSkinVertex vertex)
{
    uint b = vertex.bones;
    return uint4(b & 255u, (b >> 8u) & 255u, (b >> 16u) & 255u, b >> 24u);
}

// Linear blend skinning of the bind position and normal by the four influences, from palette entries
// palette[start + bone]: the same four terms in the same order as the CPU bake adds them, every term added whatever
// its weight (a weight of zero adds zero). The palette holds the model's bones as renderer-local matrices
// (inverse(renderer) * bone * bindPose), so the result is in the instance's own frame. The normal is made a unit
// vector a vertex, as the CPU bake's octahedral encoding leaves it: a blended normal shorter where bones disagree
// would weigh less in the triangle's interpolation than the bake's (review 1, item 4).
void VpSkinPositionNormal(VpSkinVertex vertex, StructuredBuffer<float4x4> palette, uint start, out float3 position, out float3 normal)
{
    float4 p = float4(VpSkinBindPosition(vertex), 1.0);
    float3 n = VpDecodeNormal(VpSkinAttributes(vertex));
    float4 w = VpSkinWeightsOf(vertex);
    uint4 b = VpSkinBonesOf(vertex);
    float4x4 m0 = palette[start + b.x];
    float4x4 m1 = palette[start + b.y];
    float4x4 m2 = palette[start + b.z];
    float4x4 m3 = palette[start + b.w];
    position = (mul(m0, p) * w.x + mul(m1, p) * w.y + mul(m2, p) * w.z + mul(m3, p) * w.w).xyz;
    float3 blended = mul((float3x3)m0, n) * w.x + mul((float3x3)m1, n) * w.y + mul((float3x3)m2, n) * w.z + mul((float3x3)m3, n) * w.w;
    float length2 = dot(blended, blended);
    normal = length2 > 0.0 ? blended * rsqrt(length2) : n;
}

// The position alone (the motion pass skins no normal).
float3 VpSkinPosition(VpSkinVertex vertex, StructuredBuffer<float4x4> palette, uint start)
{
    float4 p = float4(VpSkinBindPosition(vertex), 1.0);
    float4 w = VpSkinWeightsOf(vertex);
    uint4 b = VpSkinBonesOf(vertex);
    return (mul(palette[start + b.x], p) * w.x + mul(palette[start + b.y], p) * w.y
        + mul(palette[start + b.z], p) * w.z + mul(palette[start + b.w], p) * w.w).xyz;
}

#endif
