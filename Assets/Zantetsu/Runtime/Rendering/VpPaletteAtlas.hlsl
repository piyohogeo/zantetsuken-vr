#ifndef ZANTETSU_VP_PALETTE_ATLAS_INCLUDED
#define ZANTETSU_VP_PALETTE_ATLAS_INCLUDED
#include "VpCompactVertex.hlsl"
TEXTURE2D(_VpPaletteSurface); SAMPLER(sampler_VpPaletteSurface);
TEXTURE2D(_VpPaletteCaps); SAMPLER(sampler_VpPaletteCaps);
float _VpPaletteAtlasEnabled;
// Cap UV is constant by contract. Explicit mip 0 avoids tiny-patch dilution at distance and is independent of ST/tint.
half3 VpPaletteCap(bool provisional)
{
    float2 uv = float2(provisional ? 239.5 : 247.5,247.5)/256.0;
    return SAMPLE_TEXTURE2D_LOD(_VpPaletteCaps,sampler_VpPaletteCaps,uv,0).rgb;
}
half3 VpPaletteBase(float2 rawUv,float2 surfaceUv,half3 tint)
{
    if(VpIsRealCap(rawUv)) return VpPaletteCap(false);
    return SAMPLE_TEXTURE2D(_VpPaletteSurface,sampler_VpPaletteSurface,surfaceUv).rgb*tint;
}
#endif
