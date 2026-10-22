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
//
// VP_GPU_CULLED is the variant of a batch that selects its instances on the GPU (VP Stage 3C, DESIGN 4.5.7). The draw's
// instances are the slots of the list the compute pass wrote for this view's casters -- a selection of its own, by the
// shadow splits' culling volumes, not the camera's -- and each slot names the logical instance. The one argument buffer
// is drawn into every slice of the shadow map, so the list holds what any slice needs; the vertex stage then passes over
// an instance whose bounds, as carried by its transform, lie wholly outside the slice being rendered (a directional
// light's, where the projection is a box): every vertex of it is rejected before its clip record or its bias is
// evaluated. Nothing is rejected for a punctual light. _Cull, the clip record and the bias are unchanged.
//
// VP_NATIVE_MULTIDRAW is the variant the Direct3D 12 plugin draws (DESIGN 4.5.8), where one ExecuteIndirect issues a run
// of commands: the command's number arrives as a per-command root constant, and the arguments and the command's bounds
// are read by it (VpNativeMultiDraw.hlsl). No material enables it; the Editor compiles it for the plugin alone.
// VP_NO_CLIP is the ordinary-object variant: Clip records, calculations and SV_ClipDistance outputs are absent.
// VP_CLIP_LIST_SLICES is the variant of the clip list's casters drawn by Unity's own call (DESIGN 4.5.9): no selection
// is read, the draw's commands are the list's entries, and the vertex stage passes over an entry outside the slice
// being rendered by the very judgement of VP_GPU_CULLED, reading the entry's bounds by the entry's number (the batch
// binds the list's bounds as _VpCullCommands). It changes no output, so it is the vertex stage's alone.
// VP_NATIVE_DIRECT is the rare provisional Clip path: CPU direct commands select transform/Clip records from a small
// uniform buffer. It reads neither the indirect arguments nor the GPU selection/instance storage buffers.
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
            #pragma multi_compile _ VP_DIAGNOSTIC_LEGACY32
            #pragma multi_compile_local_vertex _ VP_GPU_CULLED
            #pragma shader_feature_local VP_NATIVE_MULTIDRAW
            // Both stages: the pixel stage's input must not declare the clip distances that a no-clip vertex stage
            // does not write. With the keyword seen by the vertex stage alone, Direct3D 12 refuses the pipeline of
            // Unity's own draw (the two signatures do not link) and nothing is drawn; Direct3D 11 draws all the same.
            #pragma multi_compile_local _ VP_NO_CLIP
            #pragma multi_compile _ VP_PIXEL_CLIP
            #pragma multi_compile_local_vertex _ VP_CLIP_LIST_SLICES
            #pragma shader_feature_local VP_NATIVE_DIRECT
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #if !defined(VP_NATIVE_DIRECT)
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
        #if defined(VP_NATIVE_MULTIDRAW)
            #include "VpNativeMultiDraw.hlsl"
        #endif
        #endif
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Matches Zantetsu.Rendering.VpRenderVertex: 16 bytes.
            #include "VpCompactVertex.hlsl"

            StructuredBuffer<VpRenderVertex> _VpVertices;
        #if !defined(VP_NATIVE_DIRECT)
            StructuredBuffer<float4x4> _VpInstanceObjectToWorld;
        #endif

            // The same per-instance clip record the forward pass reads (DESIGN 5.1, 5.2), so the shadow of a
            // provisionally clipped fragment is the shadow of what the colour pass actually draws: the same eight
            // planes and the same count.
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
            #include "VpClipDistances.hlsl"

        #if defined(VP_GPU_CULLED) && !defined(VP_NATIVE_DIRECT)
            // The instances the compute pass kept as casters for this view, as the forward pass reads its own list.
            StructuredBuffer<uint> _VpVisible;
        #endif

        #if (defined(VP_GPU_CULLED) || defined(VP_CLIP_LIST_SLICES)) && !defined(VP_NATIVE_DIRECT)
            #define VP_SLICE_JUDGEMENT 1

            // Matches Zantetsu.Rendering.VpCullCommand: 48 bytes. The bounds of a command's range in the geometry's
            // own frame, read by the command's number -- for the clip list, the entry's.
            struct VpCullCommand
            {
                uint indexCount;
                uint startIndex;
                uint startInstance;
                uint instanceCount;
                float3 centre;
                float pad0;
                float3 extents;
                float pad1;
            };

            StructuredBuffer<VpCullCommand> _VpCullCommands;

            // 1 when an instance outside the slice being rendered is passed over, 0 when every kept caster is drawn
            // into every slice.
            float _VpShadowSliceSelection;

            // Whether the command's bounds, carried by the instance's transform, lie wholly outside the slice's
            // projection to its left, right, bottom or top. The bias may move a vertex by no more than its two terms,
            // (_ShadowBias, x the depth term and y the normal term, as URP sets them for the slice), which are added to the
            // box's reach. Depth is not asked: a caster nearer the light than the slice's near
            // plane still casts into it.
            bool VpOutsideSlice(VpCullCommand command, float4x4 objectToWorld)
            {
                float3 centreWS = mul(objectToWorld, float4(command.centre, 1.0)).xyz;
                float3 axisX = float3(objectToWorld._m00, objectToWorld._m10, objectToWorld._m20) * command.extents.x;
                float3 axisY = float3(objectToWorld._m01, objectToWorld._m11, objectToWorld._m21) * command.extents.y;
                float3 axisZ = float3(objectToWorld._m02, objectToWorld._m12, objectToWorld._m22) * command.extents.z;
                float4x4 viewProjection = UNITY_MATRIX_VP;
                float bias = abs(_ShadowBias.x) + abs(_ShadowBias.y);
                float4 centreCS = mul(viewProjection, float4(centreWS, 1.0));
                float3 rowX = viewProjection[0].xyz;
                float3 rowY = viewProjection[1].xyz;
                float reachX = abs(dot(rowX, axisX)) + abs(dot(rowX, axisY)) + abs(dot(rowX, axisZ)) + bias * length(rowX);
                float reachY = abs(dot(rowY, axisX)) + abs(dot(rowY, axisY)) + abs(dot(rowY, axisZ)) + bias * length(rowY);
                return abs(centreCS.x) - reachX > centreCS.w || abs(centreCS.y) - reachY > centreCS.w;
            }
        #endif

            // Set by URP's shadow caster setup for the light being rendered.
            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
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
            #if !defined(VP_NO_CLIP)
            #if defined(VP_PIXEL_CLIP)
                // Full-precision, perspective-correct distances; every plane remains independent until the fragment.
                float4 clipDistance0 : TEXCOORD0;
                float4 clipDistance1 : TEXCOORD1;
            #else
                float4 clipDistance0 : SV_ClipDistance0;
                float4 clipDistance1 : SV_ClipDistance1;
            #endif
            #endif
            };

            // The number of the command this draw's instances belong to: Unity's per-draw constant, or the plugin's
            // per-command root constant.
            uint VpCommandId()
            {
            #if defined(VP_NATIVE_DIRECT)
                return 0u; // Direct draws do not read command bounds or an argument buffer.
            #elif defined(VP_NATIVE_MULTIDRAW)
                return VpNativeCommandId();
            #else
                return GetCommandID(0);
            #endif
            }

            ShadowVaryings ShadowVertex(Attributes input)
            {
            #if defined(VP_NATIVE_DIRECT)
                // CPU direct draws select a small uniform record with SV_InstanceID; no indirect arguments.
            #elif defined(VP_NATIVE_MULTIDRAW)
                VpNativeInitIndirectDrawArgs();

            #else
                InitIndirectDrawArgs(0);
            #endif
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);

                VpRenderVertex vertex = _VpVertices[input.vertexID];
                uint logicalInstance = VpPhysicalInstanceID(input.instanceID);
            #if defined(VP_GPU_CULLED) && !defined(VP_NATIVE_DIRECT)
                logicalInstance = _VpVisible[logicalInstance];
            #endif
            #if defined(VP_NATIVE_DIRECT)
                float4x4 objectToWorld = _VpDirectClipRecords[logicalInstance].objectToWorld;
            #else
                float4x4 objectToWorld = _VpInstanceObjectToWorld[logicalInstance];
            #endif
            #if defined(VP_SLICE_JUDGEMENT) && !_CASTING_PUNCTUAL_LIGHT_SHADOW
                // A directional slice projects a box (w is 1 everywhere), which is the only projection the test is
                // written for; anything else draws every kept caster.
                if (_VpShadowSliceSelection > 0.5 && UNITY_MATRIX_P[3][3] == 1.0
                    && VpOutsideSlice(_VpCullCommands[VpCommandId()], objectToWorld))
                {
                    // The position alone rejects the instance; the clip distances stay positive (DESIGN 5.2).
                    output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                #if !defined(VP_NO_CLIP)
                    output.clipDistance0 = 1.0;
                    output.clipDistance1 = 1.0;
                #endif
                    return output;
                }
            #endif
                float3 positionWS = mul(objectToWorld, float4(vertex.position, 1.0)).xyz;

                // As in the forward pass: every plane is tested on the world position the transform leaves, so
                // the caster's silhouette matches what the forward pass keeps and draws.
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
            #if defined(VP_PIXEL_CLIP) && !defined(VP_NO_CLIP)
                VpDiscardOutsideClip(input.clipDistance0, input.clipDistance1);
            #endif
                return 0;
            }
            ENDHLSL
        }
    }
}
