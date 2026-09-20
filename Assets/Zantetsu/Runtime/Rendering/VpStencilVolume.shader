// The stencil volume of DESIGN 5.6: the same shared geometry the colour pass draws, clipped by the same planes, used
// to accumulate a signed winding count into the stencil byte so that the cap knows where the body's opening is.
//
// **It is the display geometry itself, not a copy.** The vertices, the indices, the instance transforms and the clip
// records are the ones the indexed indirect batch already holds; nothing is duplicated, re-wound or re-meshed for the
// stencil, and the clip is evaluated exactly as the colour pass evaluates it — on the world position before the
// placement, with no displacement of the renderer's own.
//
// **How the count marks the opening.** Clipped by its plane, a closed body becomes an open shell whose only hole is
// the cut. Along a view ray the front and back crossings of a closed surface cancel; a ray that passes through the
// hole has one crossing left over, and that is the region the cap has to fill. Front faces decrement and back faces
// increment, so a ray entering through the hole and leaving through the far side of the body - a back face - leaves
// W = +1, which is S = 129, which is what the cap draws on. A ray that misses the hole leaves W = 0 and S = 128.
//
// **Both faces, symmetrically.** Culling is off and the depth test is Always for front and back alike, so neither face
// is excluded and neither is counted under a different condition than the other. Depth is not written and colour is
// not written, so the scene's own colour and depth are untouched by counting. Which face is front is the rasterizer's
// ordinary judgement, the same one the colour pass uses: a mirroring transform flips it and nothing here corrects for
// that, as DESIGN 5.6 requires (向きをPositiveへ直す補正や二重補正を行わない).
//
// **Both eyes.** The stereo output is initialised from the instance id exactly as the colour pass does, so under
// Single Pass Instanced each instance counts into the slice of the eye it stands for, and the logical instance -- the
// transform and the clip record -- is the physical one halved. Outside stereo instancing the second copy is rejected
// so a monoscopic camera counts each volume once.
Shader "Zantetsu/VP Stencil Volume"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VpStencilVolume"
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
                CompFront Always
                PassFront DecrWrap
                CompBack Always
                PassBack IncrWrap
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"

            struct VpRenderVertex
            {
                float3 position;
                float3 normal;
                float2 uv0;
            };

            StructuredBuffer<VpRenderVertex> _VpVertices;
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;

            struct VpInstanceClip
            {
                float4 planes[8];
                float planeCount;
            };

            StructuredBuffer<VpInstanceClip> _VpInstanceClip;
            uint _VpInstanceMultiplier;

            // The same half-space evaluation the colour pass uses: an unused component is a positive constant, so it
            // never clips, and there is no pixel-shader clip() path for a cut plane.
            float VpHalfSpace(float4 signedPlane, float3 positionWS, uint index, uint count)
            {
                return index < count ? dot(signedPlane.xyz, positionWS) + signedPlane.w : 1.0;
            }

            void VpClipDistances(VpInstanceClip clipState, float3 positionWS, out float4 first, out float4 second)
            {
                uint count = (uint)clipState.planeCount;
                first = float4(
                    VpHalfSpace(clipState.planes[0], positionWS, 0u, count),
                    VpHalfSpace(clipState.planes[1], positionWS, 1u, count),
                    VpHalfSpace(clipState.planes[2], positionWS, 2u, count),
                    VpHalfSpace(clipState.planes[3], positionWS, 3u, count));
                second = float4(
                    VpHalfSpace(clipState.planes[4], positionWS, 4u, count),
                    VpHalfSpace(clipState.planes[5], positionWS, 5u, count),
                    VpHalfSpace(clipState.planes[6], positionWS, 6u, count),
                    VpHalfSpace(clipState.planes[7], positionWS, 7u, count));
            }

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
                float4 clipDistance0 : SV_ClipDistance0;
                float4 clipDistance1 : SV_ClipDistance1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            #define VP_REJECTED_POSITION_CS float4(2.0, 2.0, 2.0, 1.0)

            bool IsVpSecondCopyInSingleView(Attributes input)
            {
            #if defined(UNITY_STEREO_INSTANCING_ENABLED)
                return false;
            #else
                return _VpInstanceMultiplier == 2u && (GetIndirectInstanceID_Base(input.instanceID) & 1u) != 0u;
            #endif
            }

            void InitializeVpIndirectDraw(Attributes input)
            {
#if defined(SHADER_API_VULKAN)
                InitIndirectDrawArgs(input.drawID);
#else
                InitIndirectDrawArgs(0);
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
                    output.clipDistance0 = 1.0;
                    output.clipDistance1 = 1.0;
                    return output;
                }

                VpRenderVertex vertex = _VpVertices[input.vertexID];
                uint physicalInstance = GetIndirectInstanceID_Base(input.instanceID);
                uint logicalInstance = _VpInstanceMultiplier == 2u ? physicalInstance >> 1 : physicalInstance;

                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;

                VpInstanceClip clipState = _VpInstanceClip[logicalInstance];
                VpClipDistances(clipState, positionWS, output.clipDistance0, output.clipDistance1);

                output.positionCS = TransformWorldToHClip(positionWS);
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
