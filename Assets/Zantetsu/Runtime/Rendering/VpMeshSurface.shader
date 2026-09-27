// An ordinary Mesh or SkinnedMeshRenderer drawn the way the VP display draws its bodies, so that a character looks the
// same before it is cut (a Unity renderer) and after (the cut display): the same palette atlas lookup and the same
// shading (VpPaletteAtlas.hlsl, VpCutSurfaceShading.hlsl), with the material's base map and colour when the atlas is not
// used. A mesh has no cap, so the cut-surface branch of the VP forward pass is not here.
//
// Single Pass Instanced stereo is supported the standard way (instance ID in, stereo eye out): under SPI one draw reaches
// both eyes. The test-only "Hidden/Zantetsu/Compact16uv Mesh Oracle" stays what it is, an independent reference.
Shader "Zantetsu/VP Mesh Surface"
{
    Properties
    {
        [MainTexture] _BaseMap("Base map", 2D) = "white" {}
        [MainColor] _BaseColor("Base colour", Color) = (1, 1, 1, 1)
        _VpUsePaletteAtlas("Use the shared palette atlas", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "VpCutSurfaceShading.hlsl"
            #include "VpPaletteAtlas.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _VpUsePaletteAtlas;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float2 rawUv : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                // As the VP forward pass and the mesh oracle transform a normal: by the object's rotation and scale.
                output.normalWS = mul((float3x3)GetObjectToWorldMatrix(), input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.rawUv = input.uv;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 base;
                if (_VpUsePaletteAtlas > 0.0 && _VpPaletteAtlasEnabled > 0.0)
                {
                    base = VpPaletteBase(input.rawUv, input.uv, _BaseColor.rgb);
                }
                else
                {
                    base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                }

                return half4(VpShadeSurface(base, input.normalWS, input.positionWS), 1.0);
            }
            ENDHLSL
        }
    }
}
