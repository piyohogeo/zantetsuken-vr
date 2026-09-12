// Phase 0.11 fixed RGBA -> NV12 conversion, candidate V1.
//
// Three entry points make up the whole pipeline: one fullscreen triangle
// vertex shader, one pixel shader that writes the Y plane, and one that writes
// the UV plane. There is no constant buffer and no sampler: the source is read
// with Texture2D.Load at integer coordinates, so nothing here filters,
// wraps, or scales, and the alpha channel is not read.
//
// Everything is fixed. The source is 1280x720, the Y plane is written at that
// same size with one source pixel per output pixel, and the UV plane is
// written at 640x360 with one output pixel per 2x2 source block. Both planes
// use the top-left origin, so output pixel (0,0) is source pixel (0,0). There
// is no crop, no scale, no flip, and no branch on a colour profile.
//
// The source view is expected to be an sRGB-typed SRV, so Load returns linear
// RGB. This shader applies the BT.709 transfer to that linear light, converts
// the resulting R'G'B' with the BT.709 matrix, and writes limited range: Y in
// 16..235 and U/V in 16..240, clamped before they leave.
//
// The coefficients, the quantisation these produce, and the chroma siting that
// follows from averaging a 2x2 block are a candidate, named V1 for that
// reason. They are not established by this unit; a later decoder comparison is
// what settles whether this candidate is correct.

Texture2D<float4> SourceTexture : register(t0);

struct FullscreenVertex
{
    float4 position : SV_Position;
};

// One triangle that covers the whole target, built from the vertex index
// alone: no vertex buffer, no input layout, no transform. Corner (0,0) is the
// top-left of the target, which is what puts source (0,0) there.
FullscreenVertex FullscreenTriangleVs(uint vertexId : SV_VertexID)
{
    float2 corner = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u));

    FullscreenVertex output;
    output.position = float4(corner.x * 2.0f - 1.0f, 1.0f - corner.y * 2.0f, 0.0f, 1.0f);
    return output;
}

// The BT.709 opto-electronic transfer function, applied per channel to linear
// light: 4.5L below the knee, 1.099 L^0.45 - 0.099 above it. Written without a
// branch so both halves are evaluated and selected, and with a floor under the
// base so the power is never asked about a negative number.
float3 Bt709Oetf(float3 linearRgb)
{
    float3 clamped = saturate(linearRgb);
    float3 belowKnee = 4.5f * clamped;
    float3 aboveKnee = 1.099f * pow(max(clamped, 1e-6f), 0.45f) - 0.099f;
    return lerp(belowKnee, aboveKnee, step(0.018f, clamped));
}

// BT.709 luma from non-linear R'G'B'.
float Bt709Luma(float3 gammaRgb)
{
    return dot(gammaRgb, float3(0.2126f, 0.7152f, 0.0722f));
}

// The Y plane: one source pixel per output pixel, no averaging at all.
float LumaPs(FullscreenVertex input) : SV_Target
{
    int3 sourceTexel = int3(int2(input.position.xy), 0);
    float3 gammaRgb = Bt709Oetf(SourceTexture.Load(sourceTexel).rgb);

    float luma = 16.0f + 219.0f * Bt709Luma(gammaRgb);
    return clamp(luma, 16.0f, 235.0f) / 255.0f;
}

// The UV plane: the 2x2 source block that this chroma pixel covers, averaged
// in the linear light the SRV decoded to, and converted once. Averaging before
// the transfer rather than after is part of what V1 proposes.
float2 ChromaPs(FullscreenVertex input) : SV_Target
{
    int2 sourceOrigin = int2(input.position.xy) * 2;

    float3 sum = SourceTexture.Load(int3(sourceOrigin, 0)).rgb;
    sum += SourceTexture.Load(int3(sourceOrigin + int2(1, 0), 0)).rgb;
    sum += SourceTexture.Load(int3(sourceOrigin + int2(0, 1), 0)).rgb;
    sum += SourceTexture.Load(int3(sourceOrigin + int2(1, 1), 0)).rgb;

    float3 gammaRgb = Bt709Oetf(sum * 0.25f);
    float luma = Bt709Luma(gammaRgb);

    float chromaBlue = 128.0f + 224.0f * ((gammaRgb.b - luma) / 1.8556f);
    float chromaRed = 128.0f + 224.0f * ((gammaRgb.r - luma) / 1.5748f);

    return float2(clamp(chromaBlue, 16.0f, 240.0f),
                  clamp(chromaRed, 16.0f, 240.0f)) / 255.0f;
}
