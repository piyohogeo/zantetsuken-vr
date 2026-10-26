Shader "Zantetsu/Android Flat Slash"
{
    Properties { _Color ("Color", Color) = (0.7, 0.95, 1, 1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Pass
        {
            Name "Slash"
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float2 uv:TEXCOORD0; };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS=TransformObjectToHClip(i.positionOS.xyz);
                o.uv=i.uv;
                return o;
            }
            half4 frag(Varyings i):SV_Target
            {
                half edge=1.0h-abs(i.uv.y*2.0h-1.0h);
                half ends=smoothstep(0.0h,0.05h,i.uv.x)*smoothstep(0.0h,0.05h,1.0h-i.uv.x);
                return half4(lerp(_Color.rgb,half3(1,1,1),pow(edge,8)),_Color.a*edge*ends);
            }
            ENDHLSL
        }
    }
}
