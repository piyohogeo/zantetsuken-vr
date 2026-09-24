using System;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>Offline writer for static BottomHalfUV meshes. Topology IDs are supplied by the authoring pipeline;
    /// never infer them by welding positions. MeshFilter ownership must also be checked by the calling importer.</summary>
    public static class VpStatic16Writer
    {
        public static void Write(Stream destination, Mesh mesh, int[] topology, int topologyCount, int[] materials)
        {
            if (mesh == null || mesh.blendShapeCount != 0 || mesh.bindposeCount != 0
                || mesh.HasVertexAttribute(VertexAttribute.BlendIndices) || mesh.HasVertexAttribute(VertexAttribute.BlendWeight))
                throw new ArgumentException("Static16 requires a static mesh; skinned/blend-shape input is not a frozen-pose fallback.");
            if (materials == null || materials.Length != mesh.subMeshCount) throw new ArgumentException("material map required");
            Vector2[] uv = mesh.uv;
            if (uv.Length != mesh.vertexCount) throw new ArgumentException("BottomHalfUV uv0 required");
            foreach (Vector2 value in uv)
            {
                float u = value.x * 256f - .5f, v = value.y * 256f - .5f;
                if (!VpRenderVertex.IsSupportedUv(value) || u < 0 || u > 255 || v < 0 || v > 127
                    || u != Mathf.Round(u) || v != Mathf.Round(v)) throw new ArgumentException("BottomHalfUV byte centres required");
            }
            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) count = checked(count + (int)mesh.GetIndexCount(i));
            using var vertices = new NativeArray<VpRenderVertex>(mesh.vertexCount, Allocator.Temp);
            using var indices = new NativeArray<uint>(count, Allocator.Temp);
            if (!VpMeshConverter.TryConvert(mesh, vertices, indices, out _, out _)) throw new ArgumentException("mesh conversion refused");
            var submeshes = new VpGeometrySubmesh[mesh.subMeshCount];
            int offset = 0;
            for (int i = 0; i < submeshes.Length; i++)
            {
                int length = (int)mesh.GetIndexCount(i);
                submeshes[i] = new VpGeometrySubmesh(offset, length, materials[i]);
                offset += length;
            }
            var verdict = VpCutInputGate.Check(vertices.ToArray(), indices.ToArray(), topology, topologyCount, submeshes);
            if (!verdict.Accepted) throw new ArgumentException("authoring cut gate: " + verdict);
            // Validation is complete before writing any output bytes.
            using var writer = new BinaryWriter(destination, Encoding.UTF8, true);
            writer.Write(VpStatic16File.Magic); writer.Write(VpStatic16File.Version); writer.Write(VpRenderVertex.Stride);
            writer.Write(vertices.Length); writer.Write(indices.Length); writer.Write(topologyCount); writer.Write(submeshes.Length);
            foreach (var vertex in vertices)
            {
                writer.Write(vertex.position.x); writer.Write(vertex.position.y); writer.Write(vertex.position.z);
                writer.Write(vertex.normalX); writer.Write(vertex.normalY); writer.Write(vertex.u); writer.Write(vertex.v);
            }
            foreach (uint index in indices) writer.Write(index);
            foreach (int id in topology) writer.Write(id);
            foreach (var sub in submeshes) { writer.Write(sub.indexOffset); writer.Write(sub.indexCount); writer.Write(sub.materialIndex); }
        }
    }
}
