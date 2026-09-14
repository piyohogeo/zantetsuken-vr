// Stage 2 vertex-pulling shader (DESIGN 4.5.5) for Graphics.RenderPrimitivesIndirect. Each command of the indirect
// argument buffer draws an index range at a run of instance transforms: its startVertex is the range's start in
// _VpIndices and its startInstance the start of its transforms in _VpInstanceObjectToWorld. The colour and shadows match
// "Zantetsu/VP Unlit": the base colour shaded by a fixed direction, darkened where the main light's realtime shadow
// falls, and a ShadowCaster pass. No additional lights, soft or screen-space shadow sampling, clipping or stencil.
Shader "Zantetsu/VP Indirect Unlit"
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
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
        #include "UnityIndirect.cginc"

        // Matches Zantetsu.Rendering.VpRenderVertex: 32 bytes.
        struct VpRenderVertex
        {
            float3 position;
            float3 normal;
            float2 uv0;
        };

        StructuredBuffer<VpRenderVertex> _VpVertices;
        StructuredBuffer<uint> _VpIndices;
        StructuredBuffer<float4x4> _VpInstanceObjectToWorld;

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
        CBUFFER_END

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

        // Reads this draw's indirect arguments; call first in every vertex shader. On D3D11 Unity issues each command
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
        // command's startVertex and startInstance on every graphics API, which is what addresses the global index and
        // instance buffers here.

        // The vertex addressed through the index buffer by this command's vertex, after InitIndirectDrawArgs.
        VpRenderVertex FetchIndirectVertex(Attributes input)
        {
            uint indirectVertexId = GetIndirectVertexID_Base(input.vertexID);
            return _VpVertices[_VpIndices[indirectVertexId]];
        }

        // The object-to-world transform of this command's instance, after InitIndirectDrawArgs.
        float4x4 FetchIndirectObjectToWorld(Attributes input)
        {
            uint indirectInstanceId = GetIndirectInstanceID_Base(input.instanceID);
            return _VpInstanceObjectToWorld[indirectInstanceId];
        }
        ENDHLSL

        Pass
        {
            Name "VpIndirectForward"
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
                InitializeVpIndirectDraw(input);
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VpRenderVertex vertex = FetchIndirectVertex(input);
                float4x4 objectToWorld = FetchIndirectObjectToWorld(input);
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)objectToWorld, vertex.normal);
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
                InitializeVpIndirectDraw(input);
                ShadowVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);

                VpRenderVertex vertex = FetchIndirectVertex(input);
                float4x4 objectToWorld = FetchIndirectObjectToWorld(input);
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
                float3 normalWS = normalize(mul((float3x3)objectToWorld, vertex.normal));
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
