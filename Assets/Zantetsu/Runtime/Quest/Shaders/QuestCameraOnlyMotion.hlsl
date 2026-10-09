// Quest replay visuals: current geometry is re-rasterized into AppSW depth; only camera movement contributes to MV.
#ifndef ZANTETSU_QUEST_CAMERA_ONLY_MOTION_INCLUDED
#define ZANTETSU_QUEST_CAMERA_ONLY_MOTION_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MotionVectorsCommon.hlsl"
#if defined(LOD_FADE_CROSSFADE)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct QuestCameraMotionAttributes
{
    float3 positionOS : POSITION;
#if defined(_ALPHATEST_ON)
    float2 uv : TEXCOORD0;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct QuestCameraMotionVaryings
{
    float4 positionCS : SV_POSITION;
    float4 currentPositionCS : TEXCOORD0;
    float4 previousPositionCS : TEXCOORD1;
#if defined(_ALPHATEST_ON)
    float2 uv : TEXCOORD2;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

QuestCameraMotionVaryings QuestCameraMotionVertex(QuestCameraMotionAttributes input)
{
    QuestCameraMotionVaryings output = (QuestCameraMotionVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float4 currentWS = float4(TransformObjectToWorld(input.positionOS), 1.0);
    output.currentPositionCS = mul(_NonJitteredViewProjMatrix, currentWS);
    output.previousPositionCS = mul(_PrevViewProjMatrix, currentWS);
    output.positionCS = output.currentPositionCS;
#if defined(_ALPHATEST_ON)
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
#endif
    return output;
}

float4 QuestCameraMotionFragment(QuestCameraMotionVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
#if defined(_ALPHATEST_ON)
    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
#endif
#if defined(LOD_FADE_CROSSFADE)
    LODFadeCrossFade(input.positionCS);
#endif
    return float4(CalcAswNdcMotionVectorFromCsPositions(input.currentPositionCS, input.previousPositionCS), 1.0);
}

#endif
