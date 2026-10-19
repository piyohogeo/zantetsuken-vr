// Test only (SkinMotionVectorsPlayModeTests, 2026-10-19): the motion pass's depth, copied texel for texel into a float
// colour target so that it can be read back (a depth target cannot be). One full-screen triangle, no matrices.
Shader "Hidden/Zantetsu/Test Skin Motion Depth Copy"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment

            Texture2D<float> _SkinMvTestDepth;

            float4 Vertex(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2.0 - 1.0, 0.5, 1.0);
            }

            float Fragment(float4 position : SV_POSITION) : SV_Target
            {
                return _SkinMvTestDepth.Load(int3(position.xy, 0));
            }
            ENDHLSL
        }
    }
}
