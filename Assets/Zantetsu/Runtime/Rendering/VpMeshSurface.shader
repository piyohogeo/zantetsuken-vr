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
            #include_with_pragmas "VpSurfaceLightingVariants.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
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

        // The shadow of the uncut body: a closed, Stable mesh, so one-sided (DESIGN 5.4: the Unity renderer path casts
        // with ShadowCastingMode.On and Cull Back). Nothing is clipped here -- a mesh has no cut. The normal bias and the
        // light directions are the VP casters' (VpIndexedIndirectShadowCaster.shader).
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // The forward pass's material buffer, unchanged, so that both passes stay SRP-batcher compatible.
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _VpUsePaletteAtlas;
            CBUFFER_END

            // Set by URP's shadow caster setup for the light being rendered.
            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVertex(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                float3 normalWS = normalize(mul((float3x3)GetObjectToWorldMatrix(), input.normalOS));
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
