#ifndef ZANTETSU_VP_CUT_SURFACE_SHADING_INCLUDED
#define ZANTETSU_VP_CUT_SURFACE_SHADING_INCLUDED

// The one lighting every VP surface is drawn with (DESIGN 5.3): an ordinary mesh or skinned mesh (VP Mesh Surface), the
// body of the cut display by either route, the real caps committed into the geometry, and the temporary caps the
// stencil draws. Only the base colour differs between the callers -- the surface's texture or palette colour, or the
// cut surface colour -- so a body and its caps, before and after a cut and before and after its geometry is committed,
// are lit by the same code from the same inputs.
//
// It is URP's own lighting, not a formula of this project: the surface is handed to UniversalFragmentPBR exactly as
// URP/Lit hands its own, with the one common material setting (Zantetsu.Rendering.VpSurfaceMaterial -- the values of
// the city's imported Color material: colour multiplier, Metallic, Smoothness; no normal, occlusion or emission map).
// What URP then reads is what it reads for URP/Lit:
//   - the scene's main light -- direction, colour, intensity -- and its realtime shadow, with the cascades, the soft
//     sampling and the distance fade of the pipeline's settings;
//   - the ambient light as spherical harmonics (unity_SHAr ... unity_SHC), so that a face fully in shadow still
//     varies with where it faces;
//   - the environment reflection (unity_SpecCube0 / the pipeline's default reflection) through URP's BRDF, with the
//     highlight of the main light.
// The two environment inputs are per-draw constants. An ordinary renderer is given them by URP; the indirect draws of
// the cut display are given the scene's ambient probe and default reflection by Unity, because their draws leave light
// probe and reflection probe usage off (set in VpIndexedIndirectDrawBatch and VpStencilCapBatch; measured against a
// MeshRenderer on 2026-10-06). Nothing here replaces either with a constant.
//
// Not here: additional lights, screen-space occlusion and shadows, fog, normal maps, transparency, emission. The toon
// shading of DESIGN 5.3 -- its steps, its outline -- is still to come and would take this function's place.
//
// The caller includes URP's Core.hlsl and Lighting.hlsl before this file, declares the variants with
// #include_with_pragmas "VpSurfaceLightingVariants.hlsl", and -- in a pass drawn for both eyes at once -- sets up the
// stereo eye index of the fragment first, so that the view direction is that eye's.

// The common material setting: written once when a session or a domain begins (VpSurfaceMaterial), never per frame.
// Global constants, outside UnityPerMaterial, as the cut surface colours are.
half4 _VpSurfaceColorScale;   // rgb: the multiplier of every base colour, in the active colour space
float4 _VpSurfaceMaterial;    // x: Metallic, y: Smoothness

half3 VpShadeSurface(half3 base, float3 normalWS, float3 positionWS)
{
    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalWS = NormalizeNormalPerPixel(normalWS);
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    // The shadow coordinate picks its cascade from the world position, so it is computed per fragment.
    inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
#else
    inputData.shadowCoord = float4(0.0, 0.0, 0.0, 0.0);
#endif
    inputData.fogCoord = 0.0;
    inputData.vertexLighting = half3(0.0, 0.0, 0.0);
    inputData.bakedGI = SampleSH(inputData.normalWS);
    inputData.normalizedScreenSpaceUV = float2(0.0, 0.0);
    inputData.shadowMask = half4(1.0, 1.0, 1.0, 1.0);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = base * _VpSurfaceColorScale.rgb;
    surfaceData.metallic = _VpSurfaceMaterial.x;
    surfaceData.specular = half3(0.0, 0.0, 0.0);
    surfaceData.smoothness = _VpSurfaceMaterial.y;
    surfaceData.normalTS = half3(0.0, 0.0, 1.0);
    surfaceData.emission = half3(0.0, 0.0, 0.0);
    surfaceData.occlusion = 1.0;
    surfaceData.alpha = 1.0;
    return UniversalFragmentPBR(inputData, surfaceData).rgb;
}

#endif
