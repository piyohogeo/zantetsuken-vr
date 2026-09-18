// One triangle over the whole viewport that writes nothing but the stencil byte, setting all eight bits of it to the
// base value 128 (DESIGN 5.6: "Rect描画で初期化する場合は Ref 128 と Replace"). It is drawn once at the start of every
// stencil colour, which is what keeps one colour's winding counts out of the next one's.
//
// It must not disturb what is already on screen: no colour is written, no depth is written, and the depth test is not
// consulted, so the scene's own colour and depth survive a re-initialisation untouched.
//
// **Both eyes.** Under Single Pass Instanced the target is a two-slice texture array and a draw reaches the slice its
// stereo eye index names, which is taken from the instance id. So the batch issues this triangle once per eye as two
// instances, and the vertex shader initialises the stereo output from the instance id exactly as the colour pass
// does. Outside stereo instancing the second instance is rejected, so a monoscopic camera is not initialised twice.
// The position needs no matrix: it is already in clip space, the same for either eye.
Shader "Zantetsu/VP Stencil Init"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VpStencilInit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 255
                WriteMask 255
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 2 when the batch was uploaded for Single Pass Instanced, otherwise 1: how many instances stand for one
            // logical draw, one per eye.
            uint _VpInstanceMultiplier;

            #define VP_REJECTED_POSITION_CS float4(2.0, 2.0, 2.0, 1.0)

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            #if UNITY_ANY_INSTANCING_ENABLED
                UNITY_VERTEX_INPUT_INSTANCE_ID
            #else
                uint instanceID : SV_InstanceID;
            #endif
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            bool IsVpSecondCopyInSingleView(Attributes input)
            {
            #if defined(UNITY_STEREO_INSTANCING_ENABLED)
                return false;
            #else
                return _VpInstanceMultiplier == 2u && (input.instanceID & 1u) != 0u;
            #endif
            }

            // The vertices are made from the id alone, so nothing is bound and nothing is transferred for this draw.
            // Three of them cover the viewport.
            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                if (IsVpSecondCopyInSingleView(input))
                {
                    output.positionCS = VP_REJECTED_POSITION_CS;
                    return output;
                }

                float2 corner = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                output.positionCS = float4(corner * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
