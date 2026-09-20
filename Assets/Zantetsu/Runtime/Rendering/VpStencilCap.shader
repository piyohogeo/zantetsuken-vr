// The provisional cap of DESIGN 5.2 and 5.6: the finite Cap Bounds Polygon, drawn only where the stencil says the
// body's opening is.
//
// **Only S > 128.** `Ref 128 / Comp Less / ReadMask 255` passes exactly where the stored value is above the base, which
// is where the volume left a positive winding count. The stencil write mask is zero, so a cap never changes what the
// counter accumulated. A sample that fails writes neither colour nor depth: the colour mask and the depth write are
// the pipeline's ordinary ones, and a failed stencil test rejects the fragment before either happens. That is what
// keeps the polygon - which is the cross-section of the body's bounds box and far larger than the cut - from showing
// outside the body.
//
// **It is not drawn two-sided.** The polygon is wound to agree with the outward normal of the side it closes, so the
// ordinary front-face rule is what decides it is seen, and the cap of the other side faces the other way. Culling is
// not turned off to paper over a winding that disagrees.
//
// The vertices arrive already in world space, so this transforms and does nothing
// else to them.
//
// **Shading (DESIGN 5.3).** When the caller has bound this material a normal for every cap vertex and turned
// `_VpCapShaded` on, the fragment is the base colour through `VpShadeSurface` -- the same function, light and shadow
// the body and the real caps are drawn with -- using the cap's own outward normal in world space. Caps of different
// sides therefore take different shades within one colour. With nothing bound the material draws the flat base colour
// as it always has; the normals are the caller's to keep in step with the vertices it uploaded.
//
// **Both eyes.** The vertices do not depend on the instance, so under Single Pass Instanced the batch issues the caps
// as two instances, one per eye, and the stereo output is initialised from the instance id as the colour pass does;
// each instance is then transformed by its own eye's matrix into its own slice. Outside stereo instancing the second
// instance is rejected so a monoscopic camera draws each cap once.
Shader "Zantetsu/VP Stencil Cap"
{
    Properties
    {
        _BaseColor ("Base Colour", Color) = (1, 0, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VpStencilCap"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            Stencil
            {
                Ref 128
                ReadMask 255
                WriteMask 0
                Comp Less
                Pass Keep
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "VpCutSurfaceShading.hlsl"

            StructuredBuffer<float4> _VpCapVertices;

            // One outward normal in world space per cap vertex, in the same order, bound by the caller together with
            // _VpCapShaded. Read only where _VpCapShaded is on.
            StructuredBuffer<float4> _VpCapNormals;

            // 2 when the batch was uploaded for Single Pass Instanced, otherwise 1.
            uint _VpInstanceMultiplier;

            #define VP_REJECTED_POSITION_CS float4(2.0, 2.0, 2.0, 1.0)

            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;

            // 1 when the caller has bound a normal per cap vertex and wants the shared shading; 0 (the default) draws
            // the flat base colour, which is what this pass did before.
            float _VpCapShaded;
            CBUFFER_END

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
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
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

            // The index buffer carries global vertex numbers, as it does for the indexed indirect draws, so the vertex
            // id indexes the shared cap vertex buffer directly.
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

                float3 positionWS = _VpCapVertices[input.vertexID].xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;

                // The cap's own outward normal, per vertex: one colour may hold caps of several sides, so a single
                // normal for the colour would shade them alike.
                output.normalWS = _VpCapShaded > 0.0 ? _VpCapNormals[input.vertexID].xyz : float3(0.0, 1.0, 0.0);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                if (_VpCapShaded > 0.0)
                {
                    return half4(VpShadeSurface(_BaseColor.rgb, input.normalWS, input.positionWS), _BaseColor.a);
                }

                return _BaseColor;
            }
            ENDHLSL
        }
    }
}
