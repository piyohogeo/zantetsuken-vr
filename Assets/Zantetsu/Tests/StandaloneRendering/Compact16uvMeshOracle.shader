// Test-only ordinary Mesh vertex fetch, independent of the VP structured-buffer reader.
Shader "Hidden/Zantetsu/Compact16uv Mesh Oracle"
{
    Properties { _BaseMap("Source palette", 2D) = "white" {} _UseSourcePalette("Source palette control", Float) = 0 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Cull Back ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Assets/Zantetsu/Runtime/Rendering/VpCutSurfaceShading.hlsl"
            #include "Assets/Zantetsu/Runtime/Rendering/VpPaletteAtlas.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
            float _UseSourcePalette;
            CBUFFER_END
            struct Input { float3 position:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct Output { float4 position:SV_POSITION; float3 world:TEXCOORD0; float3 normal:TEXCOORD1; float2 uv:TEXCOORD2; };
            Output Vertex(Input i)
            {
                Output o;
                o.world=TransformObjectToWorld(i.position);
                o.position=TransformWorldToHClip(o.world);
                o.normal=mul((float3x3)unity_ObjectToWorld,i.normal);
                o.uv=i.uv;
                return o;
            }
            half4 Fragment(Output i):SV_Target
            {
                half3 colour=_UseSourcePalette>0 ? SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).rgb : VpPaletteBase(i.uv,i.uv,half3(1,1,1));
                return half4(VpShadeSurface(colour,i.normal,i.world),1);
            }
            ENDHLSL
        }
    }
}
