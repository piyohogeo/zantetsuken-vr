#ifndef ZANTETSU_VP_SURFACE_LIGHTING_VARIANTS_INCLUDED
#define ZANTETSU_VP_SURFACE_LIGHTING_VARIANTS_INCLUDED

// The variants the shared VP lighting (VpCutSurfaceShading.hlsl) is compiled in, declared once for every pass that
// calls it (#include_with_pragmas). They are the ones of URP/Lit's forward pass that this lighting uses:
//   - the main light's shadow map, single or in cascades;
//   - its soft sampling, at the quality the pipeline asset sets;
//   - how the environment reflection is taken (blended probes, box projection), as URP/Lit takes it.
// Not declared, on purpose (DESIGN 5.3): additional lights and their shadows, screen-space shadows and occlusion,
// light cookies, light layers, lightmaps and shadow masks, probe volumes, the clustered (Forward+) loop, decals, fog.
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
#pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
#pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
#pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION

#endif
