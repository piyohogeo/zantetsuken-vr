#ifndef ZANTETSU_VP_SKY_FOG_INCLUDED
#define ZANTETSU_VP_SKY_FOG_INCLUDED

// The linear fog that carries a far Unity mesh into the sky behind it (DESIGN 5.3.1): nothing up to seven tenths of the
// camera's far plane, all of the sky's colour at the far plane, by the depth along the view -- not the distance from
// the eye -- so that it is whole exactly where the far plane clips.
//
//   fog = saturate((viewDepth - 0.7 far) / (0.3 far))        colour = lerp(lit, sky(direction from the eye), fog)
//
// The far plane is the camera's own, as the pipeline sets it for the camera being rendered (_ProjectionParams.z): no
// constant of this file is written per camera, per frame, per object or per material. The sky's colour is one sample
// of a small cubemap baked from the skybox alone (VpSkyFog.cs; no scene, no post-processing), at mip 0, by the
// world direction from the eye to the surface -- not the normal and not the reflection. The cubemap holds linear HDR
// values as they are (RGBAHalf): nothing is decoded and nothing is scaled. The colour is not multiplied by the
// material's colour, lit, shadowed or scaled by the reflection intensity: it is the background's, at the stage the
// background is drawn, and what follows the pass (post-processing) follows both alike.
//
// Called by the pass that wants it, after its lighting ("Zantetsu/VP Mesh Surface"). It is NOT part of the shared
// lighting (VpCutSurfaceShading.hlsl): the cut display's bodies and caps are not fogged.
//
// Under Single Pass Instanced each eye has its own view depth, eye position and far plane through the usual stereo
// constants; the caller sets the eye index up as it does for its lighting.

TEXTURECUBE(_VpSkyFogCube);

// x: 1 when the cubemap is set, 0 when it is not (then nothing is sampled and the colour is left as it is).
float4 _VpSkyFogParams;

// The share of the far plane at which the fog begins. The fog is whole at the far plane itself.
#define VP_SKY_FOG_START 0.7

// The depth along the view of a world position, for the eye being rendered: what the fog goes by. Linear in the
// position, so it may be interpolated from the vertices.
float VpSkyFogViewDepth(float3 positionWS)
{
    return -TransformWorldToView(positionWS).z;
}

float VpSkyFogFactor(float viewDepth)
{
    float far = _ProjectionParams.z;
    return saturate((viewDepth - VP_SKY_FOG_START * far) / ((1.0 - VP_SKY_FOG_START) * far)) * _VpSkyFogParams.x;
}

half3 VpApplySkyFog(half3 lit, float3 positionWS, float viewDepth)
{
    float fog = VpSkyFogFactor(viewDepth);
    if (fog > 0.0)
    {
        float3 fromEye = positionWS - GetCameraPositionWS();
        half3 sky = SAMPLE_TEXTURECUBE_LOD(_VpSkyFogCube, sampler_LinearClamp, fromEye, 0).rgb;
        lit = lerp(lit, sky, fog);
    }

    return lit;
}

#endif
