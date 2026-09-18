// One triangle over the whole viewport that writes nothing but the stencil byte, setting all eight bits of it to the
// base value 128 (DESIGN 5.6: "Rect描画で初期化する場合は Ref 128 と Replace"). It is drawn once at the start of every
// stencil colour, which is what keeps one colour's winding counts out of the next one's.
//
// It must not disturb what is already on screen: no colour is written, no depth is written, and the depth test is not
// consulted, so the scene's own colour and depth survive a re-initialisation untouched.
Shader "Zantetsu/VP Stencil Init"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VpStencilInit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 255
                WriteMask 255
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            // The vertices are made from the id alone, so nothing is bound and nothing is transferred for this draw.
            // Three of them cover the viewport.
            Varyings Vertex(uint vertexID : SV_VertexID)
            {
                Varyings output;
                float2 corner = float2((vertexID << 1) & 2, vertexID & 2);
                output.positionCS = float4(corner * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
