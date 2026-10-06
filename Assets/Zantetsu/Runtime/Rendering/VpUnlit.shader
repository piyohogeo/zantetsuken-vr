// Stage 1 vertex-pulling shader (DESIGN 4.5.5): a Direct non-indexed draw whose SV_VertexID reads the uint index
// buffer, and the index reads the VP vertex buffer. The colour is the base colour through the lighting every VP surface
// shares (VpCutSurfaceShading.hlsl, DESIGN 5.3: URP's own, with the common material setting); the geometry casts
// shadows through the ShadowCaster pass. No additional lights, screen-space shadow sampling, clipping or stencil.
Shader "Zantetsu/VP Unlit"
{
    Properties
    {
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Palette UV transform", 2D) = "white" {}
        [Toggle] _VpUsePaletteAtlas("Use shared Compact16uv palette atlas", Float) = 0
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

        // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
        #include "VpCompactVertex.hlsl"
        #include "VpPaletteAtlas.hlsl"

        StructuredBuffer<VpRenderVertex> _VpVertices;
        StructuredBuffer<uint> _VpIndices;
        uint _VpIndexStart;
        float4x4 _VpObjectToWorld;

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float4 _BaseMap_ST;
            float _VpUsePaletteAtlas;
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
            #pragma multi_compile _ VP_DIAGNOSTIC_LEGACY32
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            // The variants of the shared lighting (the main light's shadow, its soft sampling, the reflection).
            #include_with_pragmas "VpSurfaceLightingVariants.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VpCutSurfaceShading.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 rawUv : TEXCOORD2;
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
                output.normalWS = mul((float3x3)_VpObjectToWorld, VpDecodeNormal(vertex));
                output.positionWS = positionWS;
                output.rawUv = VpDecodeUv(vertex);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 colour = _BaseColor.rgb;
                if (_VpUsePaletteAtlas > 0.0 && _VpPaletteAtlasEnabled > 0.0)
                    colour = VpPaletteBase(input.rawUv, TRANSFORM_TEX(input.rawUv, _BaseMap), _BaseColor.rgb);
                // The shared lighting of every VP surface (VpCutSurfaceShading.hlsl, DESIGN 5.3).
                return half4(VpShadeSurface(colour, input.normalWS, input.positionWS), 1.0);
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
            #pragma multi_compile _ VP_DIAGNOSTIC_LEGACY32
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
                float3 normalWS = normalize(mul((float3x3)_VpObjectToWorld, VpDecodeNormal(vertex)));
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
