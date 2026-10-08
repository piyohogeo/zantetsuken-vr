// One slice of a texture array drawn over the whole render target, for VpEyeShot (an eye's picture out of the XR eye
// texture). URP's core blit samples a Texture2D (an array slice came out blank), and a copy of a slice of the eye
// texture ended the Player (natS4, 2026-10-08), so the slice is sampled here, through a point sampler, at level 0.
Shader "Hidden/Zantetsu/VP Eye Shot Slice"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Slice"
            ZWrite Off ZTest Always Blend Off Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            Texture2DArray<float4> _VpEyeSource;
            SamplerState sampler_PointClamp;
            int _VpEyeSlice;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return _VpEyeSource.SampleLevel(sampler_PointClamp, float3(input.uv, _VpEyeSlice), 0);
            }
            ENDHLSL
        }
    }
}
