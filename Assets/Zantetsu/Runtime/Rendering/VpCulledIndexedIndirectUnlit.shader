// Stage 3 probe forward shader for the culled forward Graphics.RenderPrimitivesIndexedIndirect call of
// VpCulledInstanceSet. As "Zantetsu/VP Indexed Indirect Unlit", but each command's startInstance is the start of its
// selected instances in _VpVisibleInstances (times 2 for Single Pass Instanced), and the transform of a draw instance is
// _VpInstanceObjectToWorld[_VpVisibleInstances[visible index]]. The colour and shadow sampling are the same.
Shader "Zantetsu/VP Culled Indexed Indirect Unlit"
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

        Pass
        {
            Name "VpCulledIndexedIndirectForward"
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VpCutSurfaceShading.hlsl"

            // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
            #include "VpCompactVertex.hlsl"
            #include "VpPaletteAtlas.hlsl"

            StructuredBuffer<VpRenderVertex> _VpVertices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;
            StructuredBuffer<uint> _VpVisibleInstances;

            // Physical instances per selected instance in the forward arguments: 2 for Single Pass Instanced stereo,
            // otherwise 1.
            uint _VpInstanceMultiplier;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
                float _VpUsePaletteAtlas;
            CBUFFER_END

            // A clip-space position outside the view volume: triangles whose vertices all take it cover no pixel.
            #define VP_REJECTED_POSITION_CS float4(2.0, 2.0, 2.0, 1.0)

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            #if defined(SHADER_API_VULKAN)
                uint drawID : SV_DrawID;
            #endif
            #if UNITY_ANY_INSTANCING_ENABLED
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
                float2 rawUv : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            void InitializeVpIndirectDraw(Attributes input)
            {
            #if defined(SHADER_API_VULKAN)
                InitIndirectDrawArgs(input.drawID);
            #else
                InitIndirectDrawArgs(0);
            #endif
            }

            bool IsVpSecondCopyInSingleView(Attributes input)
            {
            #if defined(UNITY_STEREO_INSTANCING_ENABLED)
                return false;
            #else
                return _VpInstanceMultiplier == 2u && (GetIndirectInstanceID_Base(input.instanceID) & 1u) != 0u;
            #endif
            }

            Varyings Vertex(Attributes input)
            {
                InitializeVpIndirectDraw(input);
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                if (IsVpSecondCopyInSingleView(input))
                {
                    output.positionCS = VP_REJECTED_POSITION_CS;
                    return output;
                }

                VpRenderVertex vertex = _VpVertices[input.vertexID];
                uint physicalInstance = GetIndirectInstanceID_Base(input.instanceID);
                uint visibleIndex = _VpInstanceMultiplier == 2u ? physicalInstance >> 1 : physicalInstance;
                float4x4 objectToWorld = _VpInstanceObjectToWorld[_VpVisibleInstances[visibleIndex]];
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)objectToWorld, VpDecodeNormal(vertex));
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
    }
}
