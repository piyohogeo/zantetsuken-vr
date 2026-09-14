using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Stage 1 draw issue (DESIGN 4.5.5): one Direct non-indexed draw of an index range, read by the VP shader through
    /// the structured vertex and index buffers. No hardware index buffer, shadows or batching.
    /// </summary>
    public static class VpDirectDraw
    {
        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int IndicesId = Shader.PropertyToID("_VpIndices");
        private static readonly int IndexStartId = Shader.PropertyToID("_VpIndexStart");
        private static readonly int ObjectToWorldId = Shader.PropertyToID("_VpObjectToWorld");

        /// <summary>
        /// Queues the range for this frame's cameras, or only <paramref name="camera"/> when given, through
        /// Graphics.RenderPrimitives. The range's indices address the buffers' global vertex numbers.
        /// <paramref name="worldBounds"/> is used for culling. An empty range queues nothing.
        /// </summary>
        public static void Render(
            Material material,
            MaterialPropertyBlock properties,
            VpGpuGeometryBuffers buffers,
            VpGeometryRange range,
            Matrix4x4 objectToWorld,
            Bounds worldBounds,
            int layer,
            Camera camera = null)
        {
            if (range.indexCount == 0)
            {
                return;
            }

            properties.SetBuffer(VerticesId, buffers.VertexBuffer);
            properties.SetBuffer(IndicesId, buffers.IndexBuffer);
            properties.SetInteger(IndexStartId, range.indexStart);
            properties.SetMatrix(ObjectToWorldId, objectToWorld);
            var renderParams = new RenderParams(material)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = worldBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, range.indexCount);
        }
    }
}
