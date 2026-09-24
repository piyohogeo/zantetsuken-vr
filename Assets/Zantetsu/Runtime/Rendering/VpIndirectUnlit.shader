// Stage 2 vertex-pulling forward shader (DESIGN 4.5.5) for the forward Graphics.RenderPrimitivesIndirect call of
// VpIndirectDrawBatch. Each command of the indirect argument buffer draws an index range at a run of instance
// transforms: its startVertex is the range's start in _VpIndices and its startInstance the start of its transforms in
// _VpInstanceObjectToWorld. The colour matches "Zantetsu/VP Unlit": the base colour shaded by a fixed direction,
// darkened where the main light's realtime shadow falls. There is no ShadowCaster pass; the batch's shadow call casts
// shadows with "Zantetsu/VP Indirect Shadow Caster". No additional lights, soft or screen-space shadow sampling,
// clipping or stencil.
Shader "Zantetsu/VP Indirect Unlit"
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
            Name "VpIndirectForward"
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
            // Main light realtime shadows only: one map or its cascades.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
            #include "UnityIndirect.cginc"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
            #include "VpCompactVertex.hlsl"
            #include "VpPaletteAtlas.hlsl"

            StructuredBuffer<VpRenderVertex> _VpVertices;
            StructuredBuffer<uint> _VpIndices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;

            // Physical instances per logical instance in the forward arguments, set by the batch: 2 for Single Pass
            // Instanced stereo, where physical instance IDs 2k and 2k+1 are logical instance k for the left and right eye
            // and the arguments' instanceCount and startInstance are doubled; otherwise 1.
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
                // Vulkan issues the commands as one multi-draw, so each command is identified by its draw ID.
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

            // GetIndirectVertexID and GetIndirectInstanceID count from 0 within each command. The _Base variants add the
            // command's startVertex and startInstance on every graphics API, which is what addresses the global index
            // and instance buffers here.

            // True for the second physical copy of a logical instance when this forward pass renders a single view: only
            // the stereo instancing pass sends the two copies to the two eyes. Checked before reading any buffer.
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

                VpRenderVertex vertex = _VpVertices[_VpIndices[GetIndirectVertexID_Base(input.vertexID)]];
                uint physicalInstance = GetIndirectInstanceID_Base(input.instanceID);
                uint logicalInstance = _VpInstanceMultiplier == 2u ? physicalInstance >> 1 : physicalInstance;
                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)objectToWorld, VpDecodeNormal(vertex));
                output.positionWS = positionWS;
                output.rawUv = VpDecodeUv(vertex);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                half facing = saturate(dot(normalize(input.normalWS), normalize(float3(0.3, 0.8, -0.5))));
                // The shadow coordinate picks its cascade from the world position, so it is computed per fragment.
                half shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(input.positionWS));
                half3 colour = _BaseColor.rgb;
                if (_VpUsePaletteAtlas > 0.0 && _VpPaletteAtlasEnabled > 0.0)
                    colour = VpPaletteBase(input.rawUv, TRANSFORM_TEX(input.rawUv, _BaseMap), _BaseColor.rgb);
                return half4(colour * (0.5 + 0.5 * facing * shadow), 1.0);
            }
            ENDHLSL
        }
    }
}
