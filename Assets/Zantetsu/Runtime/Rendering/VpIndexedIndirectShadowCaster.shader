// VP Stage 3 shadow caster for the shadow Graphics.RenderPrimitivesIndexedIndirect call of VpIndexedIndirectDrawBatch.
// It has only a ShadowCaster pass and reads the same vertex and instance buffers as "Zantetsu/VP Indexed Indirect Unlit";
// SV_VertexID is the global vertex number from the hardware index buffer. A shadow map renders a single view and the
// shadow arguments always hold logical instances. The normal bias and light directions match
// "Zantetsu/VP Indirect Shadow Caster". It reads the same per-instance clip record as the forward shader, so a
// provisionally clipped fragment casts the shadow of what is actually drawn (DESIGN 5.1).
//
// _Cull is what DESIGN 5.4 calls a drawing state rather than a per-instance attribute. A material of this shader is
// either the one-sided caster of ordinary bodies -- Cull Back, the default, and what every existing caller keeps -- or
// the two-sided caster of a provisionally clipped body, Cull Off, where the back of the shell behind the opening is
// what occludes, because no cap is drawn into the shadow map. One shader, two materials, two draws: NOT one draw with
// every caster turned two-sided, which DESIGN 5.4 refuses without measuring the back-face raster it would add.
Shader "Zantetsu/VP Indexed Indirect Shadow Caster"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
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
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull [_Cull]
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
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
            #include "VpCompactVertex.hlsl"

            StructuredBuffer<VpRenderVertex> _VpVertices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;

            // The same per-instance clip record the forward pass reads (DESIGN 5.1, 5.2), so the shadow of a
            // provisionally clipped fragment is the shadow of what the colour pass actually draws: the same eight
            // planes and the same count.
            struct VpInstanceClip
            {
                float4 planes[8];      // signed: the half kept is dot(n, x) + d >= 0, for each valid plane
                float planeCount; // how many of the eight planes are valid
            };

            StructuredBuffer<VpInstanceClip> _VpInstanceClip;

            // As in the forward pass: one clip distance per plane, the region kept is their intersection, and past
            // the valid count the component is a positive constant so an unused plane clips nothing at any vertex.
            float VpHalfSpace(float4 signedPlane, float3 positionWS, uint index, uint count)
            {
                return index < count ? dot(signedPlane.xyz, positionWS) + signedPlane.w : 1.0;
            }

            void VpClipDistances(VpInstanceClip clipState, float3 positionWS, out float4 first, out float4 second)
            {
                uint count = (uint)clipState.planeCount;
                first = float4(
                    VpHalfSpace(clipState.planes[0], positionWS, 0, count),
                    VpHalfSpace(clipState.planes[1], positionWS, 1, count),
                    VpHalfSpace(clipState.planes[2], positionWS, 2, count),
                    VpHalfSpace(clipState.planes[3], positionWS, 3, count));
                second = float4(
                    VpHalfSpace(clipState.planes[4], positionWS, 4, count),
                    VpHalfSpace(clipState.planes[5], positionWS, 5, count),
                    VpHalfSpace(clipState.planes[6], positionWS, 6, count),
                    VpHalfSpace(clipState.planes[7], positionWS, 7, count));
            }

            // Set by URP's shadow caster setup for the light being rendered.
            float3 _LightDirection;
            float3 _LightPosition;

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

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;

                // One component per plane, the components past the record's count positive and finite (DESIGN 5.2).
                float4 clipDistance0 : SV_ClipDistance0;
                float4 clipDistance1 : SV_ClipDistance1;
            };

            ShadowVaryings ShadowVertex(Attributes input)
            {
            #if defined(SHADER_API_VULKAN)
                InitIndirectDrawArgs(input.drawID);
            #else
                InitIndirectDrawArgs(0);
            #endif
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                VpRenderVertex vertex = _VpVertices[input.vertexID];
                uint logicalInstance = GetIndirectInstanceID_Base(input.instanceID);
                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;

                // As in the forward pass: every plane is tested on the world position the transform leaves, so
                // the caster's silhouette matches what the forward pass keeps and draws.
                VpInstanceClip clipState = _VpInstanceClip[logicalInstance];
                float4 clipDistance0;
                float4 clipDistance1;
                VpClipDistances(clipState, positionWS, clipDistance0, clipDistance1);
                output.clipDistance0 = clipDistance0;
                output.clipDistance1 = clipDistance1;

                float3 normalWS = normalize(mul((float3x3)objectToWorld, VpDecodeNormal(vertex)));
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
