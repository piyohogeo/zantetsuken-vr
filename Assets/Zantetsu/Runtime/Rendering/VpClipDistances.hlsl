#ifndef ZANTETSU_VP_CLIP_DISTANCES_INCLUDED
#define ZANTETSU_VP_CLIP_DISTANCES_INCLUDED

// Include after the pass's VpInstanceClip declaration. All passes evaluate the same signed world planes,
// before any shadow bias or projection. Unused components are positive, so a zero-plane record clips nothing.
#if !defined(VP_NO_CLIP)
float VpHalfSpace(float4 signedPlane, float3 positionWS, uint index, uint count)
{
    return index < count ? dot(signedPlane.xyz, positionWS) + signedPlane.w : 1.0;
}

void VpClipDistances(VpInstanceClip clipState, float3 positionWS, out float4 first, out float4 second)
{
    uint count = (uint)clipState.planeCount;
    first = float4(
        VpHalfSpace(clipState.planes[0], positionWS, 0u, count),
        VpHalfSpace(clipState.planes[1], positionWS, 1u, count),
        VpHalfSpace(clipState.planes[2], positionWS, 2u, count),
        VpHalfSpace(clipState.planes[3], positionWS, 3u, count));
    second = float4(
        VpHalfSpace(clipState.planes[4], positionWS, 4u, count),
        VpHalfSpace(clipState.planes[5], positionWS, 5u, count),
        VpHalfSpace(clipState.planes[6], positionWS, 6u, count),
        VpHalfSpace(clipState.planes[7], positionWS, 7u, count));
}

#if defined(VP_PIXEL_CLIP)
// The vertex stage must pass all eight full-precision distances separately with perspective interpolation.
// Taking their minimum before interpolation changes the intersection. Discard before colour, depth, motion
// or stencil can be written; the boundary itself (distance == 0) is kept, just as by raster clipping.
void VpDiscardOutsideClip(float4 first, float4 second)
{
    clip(first);
    clip(second);
}
#endif
#endif
#endif
