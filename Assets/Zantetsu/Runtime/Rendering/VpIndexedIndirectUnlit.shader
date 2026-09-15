// VP Stage 3 forward shader for the forward Graphics.RenderPrimitivesIndexedIndirect call of
// VpIndexedIndirectDrawBatch. Each command draws an index range of the hardware index buffer at a run of instance
// transforms: its startIndex is the range's start and its startInstance the start of its transforms in
// _VpInstanceObjectToWorld. The indices already hold global vertex numbers and baseVertexIndex is 0, so SV_VertexID is
// the global vertex number and addresses _VpVertices directly (checked by the EditMode tests with a range whose index and
// vertex starts differ). The colour matches "Zantetsu/VP Indirect Unlit". There is no ShadowCaster pass; the batch's
// shadow call casts shadows with "Zantetsu/VP Indexed Indirect Shadow Caster".
Shader "Zantetsu/VP Indexed Indirect Unlit"
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

        Pass
        {
            Name "VpIndexedIndirectForward"
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Matches Zantetsu.Rendering.VpRenderVertex: 32 bytes.
            struct VpRenderVertex
            {
                float3 position;
                float3 normal;
                float2 uv0;
            };

            StructuredBuffer<VpRenderVertex> _VpVertices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;

            // Physical instances per logical instance in the forward arguments, set by the batch: 2 for Single Pass
            // Instanced stereo, otherwise 1.
            uint _VpInstanceMultiplier;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Reads this draw's indirect arguments; call first in the vertex shader. On D3D11 Unity issues each command
            // as a draw of its own with the command set as the base, so the draw ID is 0.
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
                uint logicalInstance = _VpInstanceMultiplier == 2u ? physicalInstance >> 1 : physicalInstance;
                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)objectToWorld, vertex.normal);
                output.positionWS = positionWS;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                half facing = saturate(dot(normalize(input.normalWS), normalize(float3(0.3, 0.8, -0.5))));
                half shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(input.positionWS));
                return half4(_BaseColor.rgb * (0.5 + 0.5 * facing * shadow), 1.0);
            }
            ENDHLSL
        }
    }
}
