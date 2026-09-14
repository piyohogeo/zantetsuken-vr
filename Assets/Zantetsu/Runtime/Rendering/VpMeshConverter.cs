using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Converts a CPU-readable Unity Mesh into VP vertices and a uint index list (DESIGN 4.5.2). Indices keep the
    /// mesh's own vertex numbers (base vertex applied) and the submeshes are appended in order. The mesh is not
    /// repaired: one without normals is rejected, and a missing uv0 is written as zero. Other attributes are ignored.
    /// </summary>
    public static class VpMeshConverter
    {
        /// <summary>
        /// Writes the mesh from the start of <paramref name="vertices"/> and <paramref name="indices"/>.
        /// <paramref name="vertexCount"/> and <paramref name="indexCount"/> always report what the mesh needs. Returns
        /// false, writing nothing, when the mesh is null (both counts zero), has no normals, has a submesh that is not a
        /// triangle list, or an array is too small.
        /// </summary>
        public static bool TryConvert(
            Mesh mesh,
            NativeArray<VpRenderVertex> vertices,
            NativeArray<uint> indices,
            out int vertexCount,
            out int indexCount)
        {
            if (mesh == null)
            {
                vertexCount = 0;
                indexCount = 0;
                return false;
            }

            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                return TryConvert(dataArray[0], vertices, indices, out vertexCount, out indexCount);
            }
        }

        /// <summary>
        /// The conversion of <see cref="TryConvert(Mesh, NativeArray{VpRenderVertex}, NativeArray{uint}, out int, out int)"/>
        /// from mesh data the caller has already acquired and still owns, with the same results and rejections except
        /// for the null mesh.
        /// </summary>
        internal static bool TryConvert(
            Mesh.MeshData data,
            NativeArray<VpRenderVertex> vertices,
            NativeArray<uint> indices,
            out int vertexCount,
            out int indexCount)
        {
            vertexCount = data.vertexCount;
            indexCount = 0;
            bool triangleLists = true;
            for (int s = 0; s < data.subMeshCount; s++)
            {
                SubMeshDescriptor subMesh = data.GetSubMesh(s);
                triangleLists &= subMesh.topology == MeshTopology.Triangles;
                indexCount += subMesh.indexCount;
            }

            if (!triangleLists
                || !data.HasVertexAttribute(VertexAttribute.Normal)
                || vertexCount > vertices.Length
                || indexCount > indices.Length)
            {
                return false;
            }

            using (var positions = new NativeArray<Vector3>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            using (var normals = new NativeArray<Vector3>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            using (var uvs = new NativeArray<Vector2>(vertexCount, Allocator.Temp))
            {
                data.GetVertices(positions);
                data.GetNormals(normals);
                if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    data.GetUVs(0, uvs);
                }

                for (int v = 0; v < vertexCount; v++)
                {
                    vertices[v] = new VpRenderVertex { position = positions[v], normal = normals[v], uv0 = uvs[v] };
                }
            }

            int written = 0;
            for (int s = 0; s < data.subMeshCount; s++)
            {
                int count = data.GetSubMesh(s).indexCount;
                using (var subMeshIndices = new NativeArray<int>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                {
                    data.GetIndices(subMeshIndices, s, applyBaseVertex: true);
                    for (int i = 0; i < count; i++)
                    {
                        indices[written + i] = (uint)subMeshIndices[i];
                    }
                }

                written += count;
            }

            return true;
        }
    }
}
