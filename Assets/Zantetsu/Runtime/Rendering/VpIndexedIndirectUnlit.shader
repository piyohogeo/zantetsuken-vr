// VP Stage 3 forward shader for the forward Graphics.RenderPrimitivesIndexedIndirect call of
// VpIndexedIndirectDrawBatch. Each command draws an index range of the hardware index buffer at a run of instance
// transforms: its startIndex is the range's start and its startInstance the start of its transforms in
// _VpInstanceObjectToWorld. The indices already hold global vertex numbers and baseVertexIndex is 0, so SV_VertexID is
// the global vertex number and addresses _VpVertices directly (checked by the EditMode tests with a range whose index and
// vertex starts differ). The colour matches "Zantetsu/VP Indirect Unlit". There is no ShadowCaster pass; the batch's
// shadow call casts shadows with "Zantetsu/VP Indexed Indirect Shadow Caster".
// A logical instance may also keep one half of a cut plane and be drawn moved apart (DESIGN 5.1): the side is tested
// on the world position the object-to-world transform leaves, with no displacement of the renderer's own, and the plane is
// evaluated with SV_ClipDistance as DESIGN 5.2 requires. An instance whose record has side 0 is drawn exactly as
// before.
// An opaque base texture is sampled with the vertex uv0 and multiplied into the colour. It is the one texture this
// pass has: no normal map, no transparency, no alpha clipping, and no attempt to stand in for an arbitrary URP
// material. Leaving _BaseMap unset gives Unity's default white texture, so a caller that sets only a colour sees
// exactly what it saw before this was added.
// VP_GPU_CULLED is the variant of a batch that selects its instances on the GPU (VP Stage 3C, DESIGN 4.5.7): the draw's
// instances are then the slots of a list the compute pass wrote, and each slot names the logical instance to draw.
// Everything after that -- transform, clip record, shading -- is the same. Without the keyword nothing here changes.
// VP_NATIVE_MULTIDRAW is the variant the Direct3D 12 plugin draws (DESIGN 4.5.8), where one ExecuteIndirect issues a run
// of commands: the command's number arrives as a per-command root constant and the arguments are read by it
// (VpNativeMultiDraw.hlsl). No material enables it; the Editor compiles it for the plugin alone.
// VP_NO_CLIP is the ordinary-object variant: Clip records, calculations and SV_ClipDistance outputs are absent.
// VP_NATIVE_DIRECT is the rare provisional Clip path: CPU direct commands select transform/Clip records from a small
// uniform buffer. It reads neither the indirect arguments nor the GPU selection/instance storage buffers.
Shader "Zantetsu/VP Indexed Indirect Unlit"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [Toggle] _VpUsePaletteAtlas("Use shared Compact16uv palette atlas", Float) = 0
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
            #pragma multi_compile _ VP_DIAGNOSTIC_LEGACY32
            #pragma multi_compile_local_vertex _ VP_GPU_CULLED
            #pragma shader_feature_local VP_NATIVE_MULTIDRAW
            #pragma multi_compile_local_vertex _ VP_NO_CLIP
            #pragma shader_feature_local VP_NATIVE_DIRECT
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            // The variants of the shared lighting (the main light's shadow, its soft sampling, the reflection).
            #include_with_pragmas "VpSurfaceLightingVariants.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #if !defined(VP_NATIVE_DIRECT)
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
        #if defined(VP_NATIVE_MULTIDRAW)
            #include "VpNativeMultiDraw.hlsl"
        #endif
        #endif
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VpCutSurfaceShading.hlsl"

            // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
            #include "VpCompactVertex.hlsl"
            #include "VpPaletteAtlas.hlsl"

            StructuredBuffer<VpRenderVertex> _VpVertices;
        #if !defined(VP_NATIVE_DIRECT)
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;
        #endif

            // Which parts of up to eight cut planes each logical instance keeps, and how far it is moved apart
            // (DESIGN 5.1, 5.2). Matches Zantetsu.Rendering.VpInstanceClip: 144 bytes, the eight signed planes and
            // then the count of valid ones. The shadow caster reads the same record, so a fragment
            // is clipped identically in every pass of the draw.
        #if !defined(VP_NO_CLIP)
            struct VpInstanceClip
            {
                float4 planes[8];      // signed: the half kept is dot(n, x) + d >= 0, for each valid plane
                float planeCount; // how many of the eight planes are valid
                float pad0, pad1, pad2; // 144-byte record; scalar padding avoids float3 alignment differences
            };

        #if !defined(VP_NATIVE_DIRECT)
            StructuredBuffer<VpInstanceClip> _VpInstanceClip;
        #endif
        #endif
            #include "VpNativeDirectClip.hlsl"

        #if defined(VP_GPU_CULLED) && !defined(VP_NATIVE_DIRECT)
            // The instances the compute pass kept for this view's body: each command's in the slots of its own
            // instance run, the kept ones first. The arguments it wrote count and start those slots.
            StructuredBuffer<uint> _VpVisible;
        #endif

            // One clip distance per plane, so the region kept is the intersection of the valid half-spaces: the
            // hardware drops a fragment wherever any component is negative. Past the valid count the component is a
            // positive constant, which is what DESIGN 5.2 asks of the unused components at every vertex, so a record
            // of count zero clips nothing at all. The same eight components serve every count: there is no
            // pixel-shader clip() path and no SV_CullDistance.
        #if !defined(VP_NO_CLIP)
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
        #endif

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
                float _VpUsePaletteAtlas;
            CBUFFER_END

            // A clip-space position outside the view volume: triangles whose vertices all take it cover no pixel.
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

                // DESIGN 5.2 evaluates the selected cut planes with SV_ClipDistance and has no pixel-shader clip()
                // path. All eight components are used, four here and four in SV_ClipDistance1, one per plane of the
                // record; the components past its count are a positive finite value at every vertex.
            #if !defined(VP_NO_CLIP)
                float4 clipDistance0 : SV_ClipDistance0;
                float4 clipDistance1 : SV_ClipDistance1;
            #endif

                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD2;

                // The stored uv0 as it is, before the material's UV transform. The cap marker is read from this and
                // never from `uv`, so _BaseMap_ST cannot turn a cap into a surface or the other way round.
                float2 rawUv : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Reads this draw's indirect arguments; call first in the vertex shader. On D3D11 Unity issues each command
            // as a draw of its own with the command set as the base, so the draw ID is 0. The plugin's variant reads
            // them by the command number its root constant carries.
            void InitializeVpIndirectDraw(Attributes input)
            {
            #if defined(VP_NATIVE_DIRECT)
                // CPU direct draws select a small uniform record with SV_InstanceID; no indirect arguments.
            #elif defined(VP_NATIVE_MULTIDRAW)
                VpNativeInitIndirectDrawArgs();

            #else
                InitIndirectDrawArgs(0);
            #endif
            }

            bool IsVpSecondCopyInSingleView(Attributes input)
            {
            #if defined(UNITY_STEREO_INSTANCING_ENABLED)
                return false;
            #else
                return _VpInstanceMultiplier == 2u && (VpPhysicalInstanceID(input.instanceID) & 1u) != 0u;
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
                #if !defined(VP_NO_CLIP)
                    output.clipDistance0 = 1.0;
                    output.clipDistance1 = 1.0;
                #endif
                    return output;
                }

                VpRenderVertex vertex = _VpVertices[input.vertexID];
                uint physicalInstance = VpPhysicalInstanceID(input.instanceID);
                uint logicalInstance = _VpInstanceMultiplier == 2u ? physicalInstance >> 1 : physicalInstance;
            #if defined(VP_GPU_CULLED) && !defined(VP_NATIVE_DIRECT)
                logicalInstance = _VpVisible[logicalInstance];
            #endif
            #if defined(VP_NATIVE_DIRECT)
                float4x4 objectToWorld = _VpDirectClipRecords[logicalInstance].objectToWorld;
            #else
                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
            #endif
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;

                // DESIGN 5.1: every plane is tested on the world position as the transform leaves it. The renderer
                // adds no displacement of its own, so where a side is drawn is where what it follows puts it; what
            #if !defined(VP_NO_CLIP)
            #if defined(VP_NATIVE_DIRECT)
                VpInstanceClip clipState = VpDirectClipState(logicalInstance);
            #else
                VpInstanceClip clipState = _VpInstanceClip[logicalInstance];
            #endif
                float4 clipDistance0;
                float4 clipDistance1;
                VpClipDistances(clipState, positionWS, clipDistance0, clipDistance1);
                output.clipDistance0 = clipDistance0;
                output.clipDistance1 = clipDistance1;
            #endif

                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = mul((float3x3)objectToWorld, VpDecodeNormal(vertex));
                output.positionWS = positionWS;
                output.uv = TRANSFORM_TEX(VpDecodeUv(vertex), _BaseMap);
                output.rawUv = VpDecodeUv(vertex);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                // The eye this fragment is drawn for, under Single Pass Instanced: the lighting's view direction is that
                // eye's own.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // DESIGN 5.3: the marker is the raw uv0, read before the material's UV transform and before any
                // sampling. A cap takes the cut surface colour and neither the texture nor the material's own colour;
                // everything else is unchanged. The chosen colour then goes through the same shading as before.
                half3 base;
                if (_VpUsePaletteAtlas > 0.0 && _VpPaletteAtlasEnabled > 0.0)
                {
                    base = VpPaletteBase(input.rawUv, input.uv, _BaseColor.rgb);
                }
                else if (VpIsRealCap(input.rawUv))
                {
                    base = _VpCutSurfaceDebug > 0.0 ? _VpCutSurfaceDebugColor.rgb : _VpCutSurfaceColor.rgb;
                }
                else
                {
                    base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                }

                // The shading itself is VpShadeSurface (VpCutSurfaceShading.hlsl), which the stencil cap pass calls
                // too, so a temporary cut face and a real one are shaded by the same code with the same light.
                return half4(VpShadeSurface(base, input.normalWS, input.positionWS), 1.0);
            }
            ENDHLSL
        }

        // Static AppSW re-rasterization, adapted from vp-appsw-probe VpRaster (2026-10-07).
        Pass
        {
            Name "VpXRMotionVectors"
            Tags { "LightMode" = "XRMotionVectors" }

            ColorMask RGBA
            Cull Back
            ZWrite On
            ZTest LEqual

            // Stencil write for obj motion pixels (as URP's own XRMotionVectors passes)
            Stencil
            {
                WriteMask 1
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local_vertex _ VP_GPU_CULLED
            #pragma shader_feature_local VP_NATIVE_MULTIDRAW
            #pragma multi_compile_local_vertex _ VP_NO_CLIP
            #pragma shader_feature_local VP_NATIVE_DIRECT
            #pragma vertex VertexMotion
            #pragma fragment FragmentMotion
            #pragma multi_compile_instancing
            #define APPLICATION_SPACE_WARP_MOTION 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MotionVectorsCommon.hlsl"
        #if !defined(VP_NATIVE_DIRECT)
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
        #if defined(VP_NATIVE_MULTIDRAW)
            #include "VpNativeMultiDraw.hlsl"
        #endif
        #endif
            #include "VpCompactVertex.hlsl"

            StructuredBuffer<VpRenderVertex> _VpVertices;
        #if !defined(VP_NATIVE_DIRECT)
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;
        #endif

        #if !defined(VP_NO_CLIP)
            struct VpInstanceClip
            {
                float4 planes[8];
                float planeCount;
                float pad0, pad1, pad2;
            };

        #if !defined(VP_NATIVE_DIRECT)
            StructuredBuffer<VpInstanceClip> _VpInstanceClip;
        #endif
        #endif
            #include "VpNativeDirectClip.hlsl"

        #if defined(VP_GPU_CULLED) && !defined(VP_NATIVE_DIRECT)
            StructuredBuffer<uint> _VpVisible;
        #endif

            uint _VpInstanceMultiplier;


        #if !defined(VP_NO_CLIP)
            float VpHalfSpaceM(float4 signedPlane, float3 positionWS, uint index, uint count)
            {
                return index < count ? dot(signedPlane.xyz, positionWS) + signedPlane.w : 1.0;
            }
        #endif

            #define VP_REJECTED_POSITION_CS float4(2.0, 2.0, 2.0, 1.0)

            struct AttributesM
            {
                uint vertexID : SV_VertexID;
            #if UNITY_ANY_INSTANCING_ENABLED
                UNITY_VERTEX_INPUT_INSTANCE_ID
            #else
                uint instanceID : SV_InstanceID;
            #endif
            };

            struct VaryingsM
            {
                float4 positionCS : SV_POSITION;
            #if !defined(VP_NO_CLIP)
                float4 clipDistance0 : SV_ClipDistance0;
                float4 clipDistance1 : SV_ClipDistance1;
            #endif
                float4 positionCSNoJitter : TEXCOORD0;
                float4 previousPositionCSNoJitter : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            VaryingsM VertexMotion(AttributesM input)
            {
            #if defined(VP_NATIVE_DIRECT)
                // CPU direct draws select a small uniform record with SV_InstanceID; no indirect arguments.
            #elif defined(VP_NATIVE_MULTIDRAW)
                VpNativeInitIndirectDrawArgs();
            #else
                InitIndirectDrawArgs(0);
            #endif
                VaryingsM output = (VaryingsM)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                bool secondCopy = false;
            #if !defined(UNITY_STEREO_INSTANCING_ENABLED)
                secondCopy = _VpInstanceMultiplier == 2u && (VpPhysicalInstanceID(input.instanceID) & 1u) != 0u;
            #endif
                if (secondCopy)
                {
                    output.positionCS = VP_REJECTED_POSITION_CS;
                #if !defined(VP_NO_CLIP)
                    output.clipDistance0 = 1.0;
                    output.clipDistance1 = 1.0;
                #endif
                    output.positionCSNoJitter = output.positionCS;
                    output.previousPositionCSNoJitter = output.positionCS;
                    return output;
                }

                VpRenderVertex vertex = _VpVertices[input.vertexID];
                uint physicalInstance = VpPhysicalInstanceID(input.instanceID);
                uint logicalInstance = _VpInstanceMultiplier == 2u ? physicalInstance >> 1 : physicalInstance;
            #if defined(VP_GPU_CULLED) && !defined(VP_NATIVE_DIRECT)
                logicalInstance = _VpVisible[logicalInstance];
            #endif
            #if defined(VP_NATIVE_DIRECT)
                float4x4 objectToWorld = _VpDirectClipRecords[logicalInstance].objectToWorld;
            #else
                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
            #endif
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;

            #if !defined(VP_NO_CLIP)
            #if defined(VP_NATIVE_DIRECT)
                VpInstanceClip clipState = VpDirectClipState(logicalInstance);
            #else
                VpInstanceClip clipState = _VpInstanceClip[logicalInstance];
            #endif
                uint count = (uint)clipState.planeCount;
                output.clipDistance0 = float4(
                    VpHalfSpaceM(clipState.planes[0], positionWS, 0, count),
                    VpHalfSpaceM(clipState.planes[1], positionWS, 1, count),
                    VpHalfSpaceM(clipState.planes[2], positionWS, 2, count),
                    VpHalfSpaceM(clipState.planes[3], positionWS, 3, count));
                output.clipDistance1 = float4(
                    VpHalfSpaceM(clipState.planes[4], positionWS, 4, count),
                    VpHalfSpaceM(clipState.planes[5], positionWS, 5, count),
                    VpHalfSpaceM(clipState.planes[6], positionWS, 6, count),
                    VpHalfSpaceM(clipState.planes[7], positionWS, 7, count));
            #endif

                // As URP's ObjectMotionVectors.hlsl under APPLICATION_SPACE_WARP_MOTION, with the instance transform
                // already applied: current and previous clip positions of the same world point (camera-only motion).
                output.positionCSNoJitter = mul(_NonJitteredViewProjMatrix, float4(positionWS, 1.0));
                output.positionCS = output.positionCSNoJitter;
                output.previousPositionCSNoJitter = mul(_PrevViewProjMatrix, float4(positionWS, 1.0));
                return output;
            }

            float4 FragmentMotion(VaryingsM input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return float4(CalcAswNdcMotionVectorFromCsPositions(input.positionCSNoJitter, input.previousPositionCSNoJitter), 1);
            }
            ENDHLSL
        }
    }
}
