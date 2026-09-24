// Stage 3 probe shadow caster for per-cascade culled indexed indirect draws issued by the cascade shadow pass
// (Zantetsu.Rendering.Urp) with CommandBuffer.DrawProceduralIndirect over the hardware index buffer. SV_VertexID is the
// global vertex number. Each draw's arguments have startInstance 0, and the logical instance of instance i is
// _VpVisibleInstances[_VpVisibleOffset + i], where the pass sets _VpVisibleOffset before the draw. The pass leaves the
// camera matrices alone and sets the cascade slice's GPU view-projection as _VpShadowSliceViewProjection, used in place
// of UNITY_MATRIX_VP. A native render pass boundary before the draws establishes the shadow texture's winding, so
// normal rendering uses pass 0 (Cull Back); pass 1 (Cull Front) is a diagnostic control. The bias and light directions
// match "Zantetsu/VP Indexed Indirect Shadow Caster".
Shader "Zantetsu/VP Culled Indexed Shadow Caster"
{
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
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

        // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
        #include "VpCompactVertex.hlsl"

        StructuredBuffer<VpRenderVertex> _VpVertices;
        StructuredBuffer<float4x4> _VpInstanceObjectToWorld;
        StructuredBuffer<uint> _VpVisibleInstances;
        uint _VpVisibleOffset;
        float4x4 _VpShadowSliceViewProjection;

        float3 _LightDirection;
        float3 _LightPosition;

        struct Attributes
        {
            uint vertexID : SV_VertexID;
            uint instanceID : SV_InstanceID;
        };

        struct ShadowVaryings
        {
            float4 positionCS : SV_POSITION;
        };

        ShadowVaryings ShadowVertex(Attributes input)
        {
            ShadowVaryings output = (ShadowVaryings)0;
            VpRenderVertex vertex = _VpVertices[input.vertexID];
            float4x4 objectToWorld = _VpInstanceObjectToWorld[_VpVisibleInstances[_VpVisibleOffset + input.instanceID]];
            float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;
            float3 normalWS = normalize(mul((float3x3)objectToWorld, VpDecodeNormal(vertex)));
        #if _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 lightDirectionWS = normalize(_LightPosition - positionWS);
        #else
            float3 lightDirectionWS = _LightDirection;
        #endif
            output.positionCS = ApplyShadowClamping(mul(_VpShadowSliceViewProjection, float4(ApplyShadowBias(positionWS, normalWS, lightDirectionWS), 1.0)));
            return output;
        }

        half4 ShadowFragment(ShadowVaryings input) : SV_Target
        {
            return 0;
        }
        ENDHLSL

        // Pass 0: normal shadow rendering after the native pass boundary.
        Pass
        {
            Name "ShadowCasterCullBack"
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }

        // Pass 1: deliberately opposite culling for diagnostic comparisons.
        Pass
        {
            Name "ShadowCasterCullFront"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Front
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile _ VP_DIAGNOSTIC_LEGACY32
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
    }
}
