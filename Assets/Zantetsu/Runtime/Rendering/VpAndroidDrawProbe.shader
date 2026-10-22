// Opt-in Android draw diagnosis: isolate raster state, instance helpers, packed attributes and lighting.
Shader "Hidden/Zantetsu/Android Draw Probe"
{
    Properties
    {
        [HideInInspector] _VpProbeInstanceOffset("Instance offset", Integer) = 0
        [HideInInspector] _VpProbeCull("Cull", Integer) = 0
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (0, 1, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "VpAndroidDrawProbeForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_VpProbeCull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_local _ VP_PROBE_INDIRECT VP_PROBE_INSTANCING VP_PROBE_PRODUCT_VERTEX VP_PROBE_DECODE VP_PROBE_PRODUCT_DECODE VP_PROBE_LIGHTING
            #pragma multi_compile_instancing
            #include_with_pragmas "VpSurfaceLightingVariants.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "VpCompactVertex.hlsl"

        #if defined(VP_PROBE_PRODUCT_VERTEX) || defined(VP_PROBE_PRODUCT_DECODE)
            #define VP_PROBE_USE_PRODUCT_VERTEX 1
        #endif
        #if defined(VP_PROBE_INDIRECT) || defined(VP_PROBE_USE_PRODUCT_VERTEX)
            #define VP_PROBE_USE_INDIRECT 1
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
        #endif
        #if defined(VP_PROBE_INSTANCING) || defined(VP_PROBE_USE_PRODUCT_VERTEX)
            #define VP_PROBE_USE_INSTANCING 1
        #endif
        #if defined(VP_PROBE_LIGHTING)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VpCutSurfaceShading.hlsl"
        #endif

            StructuredBuffer<VpRenderVertex> _VpVertices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;
            uint _VpProbeInstanceOffset;
            uint _VpInstanceMultiplier;
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            #if defined(VP_PROBE_USE_INSTANCING) && UNITY_ANY_INSTANCING_ENABLED
                UNITY_VERTEX_INPUT_INSTANCE_ID
            #else
                uint instanceID : SV_InstanceID;
            #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float2 rawUv : TEXCOORD3;
            #if defined(VP_PROBE_USE_INSTANCING)
                UNITY_VERTEX_OUTPUT_STEREO
            #endif
            };

            Varyings Vertex(Attributes input)
            {
            #if defined(VP_PROBE_USE_INDIRECT)
                InitIndirectDrawArgs(0);
            #endif
                Varyings output = (Varyings)0;
            #if defined(VP_PROBE_USE_INSTANCING)
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            #endif
                uint instance = input.instanceID + _VpProbeInstanceOffset;
            #if defined(VP_PROBE_USE_INDIRECT)
                instance = GetIndirectInstanceID_Base(input.instanceID) + _VpProbeInstanceOffset;
            #endif
            #if defined(VP_PROBE_USE_PRODUCT_VERTEX)
                // Same NO_CLIP instance selection and rejection as the product forward vertex shader.
            #if !defined(UNITY_STEREO_INSTANCING_ENABLED)
                if (_VpInstanceMultiplier == 2u && (instance & 1u) != 0u)
                {
                    output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                    return output;
                }
            #endif
                instance = _VpInstanceMultiplier == 2u ? instance >> 1 : instance;
            #endif
                VpRenderVertex vertex = _VpVertices[input.vertexID];
                float4x4 objectToWorld = _VpInstanceObjectToWorld[instance];
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = mul((float3x3)objectToWorld, VpDecodeNormal(vertex));
                output.uv = TRANSFORM_TEX(VpDecodeUv(vertex), _BaseMap);
                output.rawUv = VpDecodeUv(vertex);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
            #if defined(VP_PROBE_USE_INSTANCING)
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #endif
            #if defined(VP_PROBE_DECODE) || defined(VP_PROBE_PRODUCT_DECODE)
                // Consume both packed fields so the compiler cannot discard normal/UV decoding.
                return half4(saturate(0.2 + 0.3 * input.normalWS + 0.5 * float3(input.uv, input.rawUv.x)), 1.0);
            #elif defined(VP_PROBE_LIGHTING)
                half3 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                return half4(VpShadeSurface(base, input.normalWS, input.positionWS), 1.0);
            #else
                return half4(0.0, 1.0, 0.0, 1.0);
            #endif
            }
            ENDHLSL
        }
    }
}
