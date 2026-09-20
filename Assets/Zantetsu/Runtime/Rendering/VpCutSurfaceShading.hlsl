#ifndef ZANTETSU_VP_CUT_SURFACE_SHADING_INCLUDED
#define ZANTETSU_VP_CUT_SURFACE_SHADING_INCLUDED

// The one shading every VP surface is drawn with: the body, the real caps committed into the geometry, and the
// temporary caps the stencil draws (DESIGN 5.3). It is the shading the indexed indirect pass has always used, moved
// here unchanged so that the cap pass calls the same code rather than a copy of it: the same fixed light direction,
// the same half-lambert term and the same main light shadow. Only the base colour differs between the callers, which
// is how the cut surface colour and its debug colour reach the same shading.
//
// This is not the common toon shading of DESIGN 5.3 -- the shading steps, the outline and the light response it
// describes are still to come. Nothing here is a new look; it is where the current one lives.
//
// The caller includes the URP Core and Shadows libraries before this file: the shading reads the main light shadow of
// the world position it is given.

// The fixed direction the shading faces towards, in world space. Not a scene light: the same for every VP surface.
#define VP_SHADING_LIGHT_DIRECTION float3(0.3, 0.8, -0.5)

half3 VpShadeSurface(half3 base, float3 normalWS, float3 positionWS)
{
    half facing = saturate(dot(normalize(normalWS), normalize(VP_SHADING_LIGHT_DIRECTION)));
    half shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(positionWS));
    return base * (0.5 + 0.5 * facing * shadow);
}

#endif
