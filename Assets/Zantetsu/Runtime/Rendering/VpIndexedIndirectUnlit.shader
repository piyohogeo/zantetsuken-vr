// VP Stage 3 forward shader for the forward Graphics.RenderPrimitivesIndexedIndirect call of
// VpIndexedIndirectDrawBatch. Each command draws an index range of the hardware index buffer at a run of instance
// transforms: its startIndex is the range's start and its startInstance the start of its transforms in
// _VpInstanceObjectToWorld. The indices already hold global vertex numbers and baseVertexIndex is 0, so SV_VertexID is
// the global vertex number and addresses _VpVertices directly (checked by the EditMode tests with a range whose index and
// vertex starts differ). The colour matches "Zantetsu/VP Indirect Unlit". There is no ShadowCaster pass; the batch's
// shadow call casts shadows with "Zantetsu/VP Indexed Indirect Shadow Caster".
// A logical instance may also keep one half of a cut plane and be drawn moved apart (DESIGN 5.1): the side is tested
// on the world position before the offset, the offset is added after the object-to-world transform, and the plane is
// evaluated with SV_ClipDistance as DESIGN 5.2 requires. An instance whose record has side 0 is drawn exactly as
// before.
// An opaque base texture is sampled with the vertex uv0 and multiplied into the colour. It is the one texture this
// pass has: no normal map, no transparency, no alpha clipping, and no attempt to stand in for an arbitrary URP
// material. Leaving _BaseMap unset gives Unity's default white texture, so a caller that sets only a colour sees
// exactly what it saw before this was added.
Shader "Zantetsu/VP Indexed Indirect Unlit"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
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

            // Which parts of up to eight cut planes each logical instance keeps, and how far it is moved apart
            // (DESIGN 5.1, 5.2). Matches Zantetsu.Rendering.VpInstanceClip: 144 bytes, the eight signed planes and
            // then the offset with the count of valid ones. The shadow caster reads the same record, so a fragment
            // is clipped and offset identically in every pass of the draw.
            struct VpInstanceClip
            {
                float4 planes[8];      // signed: the half kept is dot(n, x) + d >= 0, for each valid plane
                float4 offsetAndCount; // xyz: world offset added after the transform; w: how many planes are valid
            };

            StructuredBuffer<VpInstanceClip> _VpInstanceClip;

            // One clip distance per plane, so the region kept is the intersection of the valid half-spaces: the
            // hardware drops a fragment wherever any component is negative. Past the valid count the component is a
            // positive constant, which is what DESIGN 5.2 asks of the unused components at every vertex, so a record
            // of count zero clips nothing at all. The same eight components serve every count: there is no
            // pixel-shader clip() path and no SV_CullDistance.
            float VpHalfSpace(float4 signedPlane, float3 positionWS, uint index, uint count)
            {
                return index < count ? dot(signedPlane.xyz, positionWS) + signedPlane.w : 1.0;
            }

            void VpClipDistances(VpInstanceClip clipState, float3 positionWS, out float4 first, out float4 second)
            {
                uint count = (uint)clipState.offsetAndCount.w;
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

            // Physical instances per logical instance in the forward arguments, set by the batch: 2 for Single Pass
            // Instanced stereo, otherwise 1.
            uint _VpInstanceMultiplier;

            // The cut surface colour (DESIGN 5.3), set globally for every VP draw at once rather than per material:
            // the debug switch is one switch for everything, not one per object. They are declared outside
            // UnityPerMaterial because they are global constants, not material properties.
            half4 _VpCutSurfaceColor;
            half4 _VpCutSurfaceDebugColor;
            float _VpCutSurfaceDebug;

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
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

                // DESIGN 5.2 evaluates the selected cut planes with SV_ClipDistance and has no pixel-shader clip()
                // path. All eight components are used, four here and four in SV_ClipDistance1, one per plane of the
                // record; the components past its count are a positive finite value at every vertex.
                float4 clipDistance0 : SV_ClipDistance0;
                float4 clipDistance1 : SV_ClipDistance1;

                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD2;

                // The stored uv0 as it is, before the material's UV transform. The cap marker is read from this and
                // never from `uv`, so _BaseMap_ST cannot turn a cap into a surface or the other way round.
                float2 rawUv : TEXCOORD3;
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
                    // The position alone rejects this copy; the clip distances stay positive so that every vertex
                    // this shader emits carries the positive finite unused components DESIGN 5.2 asks for.
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

                // DESIGN 5.1: every plane is tested on the world position before the separation is added, so moving
                // a fragment apart never changes which part of it survives; the one offset the record carries is
                // then added after the object-to-world transform, never before it, and only once.
                VpInstanceClip clipState = _VpInstanceClip[logicalInstance];
                float4 clipDistance0;
                float4 clipDistance1;
                VpClipDistances(clipState, positionWS, clipDistance0, clipDistance1);
                output.clipDistance0 = clipDistance0;
                output.clipDistance1 = clipDistance1;
                positionWS += clipState.offsetAndCount.xyz;

                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)objectToWorld, vertex.normal);
                output.positionWS = positionWS;
                output.uv = TRANSFORM_TEX(vertex.uv0, _BaseMap);
                output.rawUv = vertex.uv0;
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                half facing = saturate(dot(normalize(input.normalWS), normalize(float3(0.3, 0.8, -0.5))));
                half shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(input.positionWS));

                // DESIGN 5.3: the marker is the raw uv0, read before the material's UV transform and before any
                // sampling. A cap takes the cut surface colour and neither the texture nor the material's own colour;
                // everything else is unchanged. The chosen colour then goes through the same shading as before.
                half3 base;
                if (input.rawUv.x < 0.0)
                {
                    base = _VpCutSurfaceDebug > 0.0 ? _VpCutSurfaceDebugColor.rgb : _VpCutSurfaceColor.rgb;
                }
                else
                {
                    base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                }

                return half4(base * (0.5 + 0.5 * facing * shadow), 1.0);
            }
            ENDHLSL
        }
    }
}
