// Stage 1 vertex-pulling shader (DESIGN 4.5.5): a Direct non-indexed draw whose SV_VertexID reads the uint index
// buffer, and the index reads the VP vertex buffer. The colour is the base colour shaded by a fixed direction, not by
// scene lights, darkened where the main light's realtime shadow falls; the geometry casts shadows through the
// ShadowCaster pass. No additional lights, soft or screen-space shadow sampling, clipping or stencil.
Shader "Zantetsu/VP Unlit"
{
    Properties
    {
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Matches Zantetsu.Rendering.VpRenderVertex: 32 bytes.
        struct VpRenderVertex
        {
            float3 position;
            float3 normal;
            float2 uv0;
        };

        StructuredBuffer<VpRenderVertex> _VpVertices;
        StructuredBuffer<uint> _VpIndices;
        uint _VpIndexStart;
        float4x4 _VpObjectToWorld;

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
        CBUFFER_END

        struct Attributes
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        // The vertex that this draw's vertex ID addresses through the index range.
        VpRenderVertex FetchVertex(uint vertexID)
        {
            return _VpVertices[_VpIndices[_VpIndexStart + vertexID]];
        }
        ENDHLSL

        Pass
        {
            Name "VpForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            // Main light realtime shadows only: one map or its cascades.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VpRenderVertex vertex = FetchVertex(input.vertexID);
                float3 positionWS = mul(_VpObjectToWorld, float4(vertex.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)_VpObjectToWorld, vertex.normal);
                output.positionWS = positionWS;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                half facing = saturate(dot(normalize(input.normalWS), normalize(float3(0.3, 0.8, -0.5))));
                // The shadow coordinate picks its cascade from the world position, so it is computed per fragment.
                half shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(input.positionWS));
                return half4(_BaseColor.rgb * (0.5 + 0.5 * facing * shadow), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing
            // Directional and punctual light shadows apply the normal bias with different light directions.
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Set by URP's shadow caster setup for the light being rendered.
            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVertex(Attributes input)
            {
                ShadowVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);

                VpRenderVertex vertex = FetchVertex(input.vertexID);
                float3 positionWS = mul(_VpObjectToWorld, float4(vertex.position, 1.0)).xyz;
                float3 normalWS = normalize(mul((float3x3)_VpObjectToWorld, vertex.normal));
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)));
                return output;
            }

            half4 ShadowFragment(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
