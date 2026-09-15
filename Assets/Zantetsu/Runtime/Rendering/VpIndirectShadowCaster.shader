// Stage 2 vertex-pulling shadow caster (DESIGN 4.5.5) for the shadow Graphics.RenderPrimitivesIndirect call of
// VpIndirectDrawBatch. It has only a ShadowCaster pass, so the call adds the batch's geometry to shadow maps and draws
// no colour. It reads the same vertex, index and instance buffers as "Zantetsu/VP Indirect Unlit". A shadow map renders
// a single view and the shadow arguments always hold logical instances, so the instance ID addresses the transform
// directly. The normal bias and the directional and punctual light directions match "Zantetsu/VP Unlit".
Shader "Zantetsu/VP Indirect Shadow Caster"
{
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
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
            StructuredBuffer<uint> _VpIndices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;

            // Set by URP's shadow caster setup for the light being rendered.
            float3 _LightDirection;
            float3 _LightPosition;

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

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVertex(Attributes input)
            {
                // Reads this draw's indirect arguments. On D3D11 Unity issues each command as a draw of its own with the
                // command set as the base, so the draw ID is 0.
            #if defined(SHADER_API_VULKAN)
                InitIndirectDrawArgs(input.drawID);
            #else
                InitIndirectDrawArgs(0);
            #endif
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                // The _Base variants add the command's startVertex and startInstance on every graphics API.
                VpRenderVertex vertex = _VpVertices[_VpIndices[GetIndirectVertexID_Base(input.vertexID)]];
                float4x4 objectToWorld = _VpInstanceObjectToWorld[GetIndirectInstanceID_Base(input.instanceID)];
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
