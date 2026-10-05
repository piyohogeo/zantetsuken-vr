using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A skinned mesh's bone list made as short as its weights need (DESIGN 9, D-195). A model's preparation -- its
    /// import, once -- finds the bones some vertex is weighted to, and makes a copy of the mesh whose bind poses are
    /// those bones' only and whose weights name them by their places in the shorter list. Nothing is done here per
    /// individual or per cut: the copy and the list are the model's, shared by every individual of it.
    /// <para>
    /// A bone is used when a vertex has a weight greater than zero on it, however small: nothing is rounded away and
    /// no weight is dropped. A vertex keeps as many weights as it has, in the order it has them, with the values it
    /// has: where the mesh keeps its weights as vertex attributes only the bone numbers are rewritten, in place, and
    /// the weights are not touched; otherwise the weights are given back in the mesh's own form (a count of weights a
    /// vertex and the weights in a row), never through the four-weight form. Every other part of the mesh is the
    /// copy's as it was the source's. <see cref="Differences"/> is the check of a copy, to be made before it is used.
    /// </para>
    /// </summary>
    public static class VpSkinBoneCompaction
    {
        /// <summary>
        /// The bones of <paramref name="mesh"/> some vertex is weighted to with a weight above zero, by their indices
        /// in its bind poses, ascending.
        /// </summary>
        /// <exception cref="ArgumentException">A weight is not finite, is negative, or names a bone the mesh has no bind pose for.</exception>
        public static int[] UsedBones(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            int bones = mesh.bindposes.Length;
            var used = new bool[bones];
            using (NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights())
            {
                for (int k = 0; k < weights.Length; k++)
                {
                    BoneWeight1 weight = weights[k];
                    if (!float.IsFinite(weight.weight) || weight.weight < 0f)
                    {
                        throw new ArgumentException("a bone weight is not finite or is negative", nameof(mesh));
                    }

                    if (weight.weight > 0f)
                    {
                        if ((uint)weight.boneIndex >= (uint)bones)
                        {
                            throw new ArgumentException("a weight names bone " + weight.boneIndex + " of " + bones, nameof(mesh));
                        }

                        used[weight.boneIndex] = true;
                    }
                }
            }

            var indices = new List<int>();
            for (int i = 0; i < bones; i++)
            {
                if (used[i]) indices.Add(i);
            }

            return indices.ToArray();
        }

        /// <summary>
        /// A copy of <paramref name="source"/> whose bind poses are those of <paramref name="kept"/> (indices into the
        /// source's bind poses, ascending, every used bone among them) in that order, and whose weights name the bones
        /// by their places in it. The caller owns the copy. A weight of exactly zero on a bone that is not kept -- a
        /// place a vertex holds without using it -- names the first kept bone in the copy, still with a weight of zero.
        /// </summary>
        public static Mesh Compact(Mesh source, int[] kept)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (kept == null || kept.Length == 0) throw new ArgumentException("no bone is kept", nameof(kept));
            Matrix4x4[] binds = source.bindposes;
            var place = new int[binds.Length];
            for (int i = 0; i < place.Length; i++) place[i] = -1;
            for (int c = 0; c < kept.Length; c++)
            {
                if ((uint)kept[c] >= (uint)binds.Length || (c > 0 && kept[c] <= kept[c - 1]))
                {
                    throw new ArgumentException("the kept bones are not ascending indices of the mesh's bind poses", nameof(kept));
                }

                place[kept[c]] = c;
            }

            foreach (int usedBone in UsedBones(source))
            {
                if (place[usedBone] < 0)
                {
                    throw new ArgumentException("a vertex is weighted to bone " + usedBone + ", which is not kept", nameof(kept));
                }
            }

            Mesh copy = UnityEngine.Object.Instantiate(source);
            try
            {
                copy.name = source.name;
                if (!TryRenameInTheVertexData(source, copy, place))
                {
                    RenameThroughTheWeights(source, copy, place);
                }

                var newBinds = new Matrix4x4[kept.Length];
                for (int c = 0; c < kept.Length; c++) newBinds[c] = binds[kept[c]];
                copy.bindposes = newBinds;
                return copy;
            }
            catch
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(copy);
                else UnityEngine.Object.DestroyImmediate(copy);
                throw;
            }
        }

        // The bone numbers of a mesh that keeps its weights as vertex attributes (up to four a vertex: BlendWeight and
        // BlendIndices), renamed where they lie -- in the copy's vertex data, the numbers only. The weights are not read
        // into another form and not written at all, so they are the source's bit for bit. (Mesh.SetBoneWeights is not
        // used for such a mesh: it makes every vertex's weights sum to one again, which changes their last bits.) A
        // number that is not kept -- a place without a weight -- becomes the first kept bone's. False when the mesh
        // does not keep its weights that way.
        private static bool TryRenameInTheVertexData(Mesh source, Mesh copy, int[] place)
        {
            if (!source.HasVertexAttribute(VertexAttribute.BlendIndices) || !source.HasVertexAttribute(VertexAttribute.BlendWeight))
            {
                return false;
            }

            int dimension = source.GetVertexAttributeDimension(VertexAttribute.BlendIndices);
            using (NativeArray<byte> counts = source.GetBonesPerVertex())
            {
                for (int v = 0; v < counts.Length; v++)
                {
                    if (counts[v] > dimension) return false;   // more weights than the attribute holds: kept elsewhere
                }
            }

            int width;
            switch (source.GetVertexAttributeFormat(VertexAttribute.BlendIndices))
            {
                case VertexAttributeFormat.UInt8: case VertexAttributeFormat.SInt8: width = 1; break;
                case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: width = 2; break;
                case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: width = 4; break;
                default: return false;
            }

            int stream = source.GetVertexAttributeStream(VertexAttribute.BlendIndices);
            int offset = source.GetVertexAttributeOffset(VertexAttribute.BlendIndices);
            int stride = source.GetVertexBufferStride(stream);
            int vertices = source.vertexCount;
            using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(source))
            {
                NativeArray<byte> held = data[0].GetVertexData<byte>(stream);
                if (stride <= 0 || held.Length != stride * vertices) return false;
                var bytes = new NativeArray<byte>(held.Length, Allocator.Temp);
                try
                {
                    bytes.CopyFrom(held);
                    for (int v = 0; v < vertices; v++)
                    {
                        for (int d = 0; d < dimension; d++)
                        {
                            int at = v * stride + offset + d * width;
                            int bone = width == 1 ? bytes[at]
                                : width == 2 ? bytes[at] | (bytes[at + 1] << 8)
                                : bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);
                            int to = (uint)bone < (uint)place.Length ? place[bone] : -1;
                            if (to < 0) to = 0;
                            bytes[at] = (byte)to;
                            if (width > 1) bytes[at + 1] = (byte)(to >> 8);
                            if (width > 2)
                            {
                                bytes[at + 2] = (byte)(to >> 16);
                                bytes[at + 3] = (byte)(to >> 24);
                            }
                        }
                    }

                    copy.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                }
                finally
                {
                    bytes.Dispose();
                }
            }

            return true;
        }

        // A mesh that keeps its weights in the other form (a count of weights a vertex, more than four allowed): the
        // weights are given back with the bones renamed. Unity may make them sum to one again in doing so; the check
        // of the copy (Differences) says whether any value changed.
        private static void RenameThroughTheWeights(Mesh source, Mesh copy, int[] place)
        {
            using (NativeArray<byte> counts = source.GetBonesPerVertex())
            using (NativeArray<BoneWeight1> weights = source.GetAllBoneWeights())
            {
                var newCounts = new NativeArray<byte>(counts.Length, Allocator.Temp);
                var newWeights = new NativeArray<BoneWeight1>(weights.Length, Allocator.Temp);
                try
                {
                    newCounts.CopyFrom(counts);
                    for (int k = 0; k < weights.Length; k++)
                    {
                        BoneWeight1 weight = weights[k];
                        int to = (uint)weight.boneIndex < (uint)place.Length ? place[weight.boneIndex] : -1;
                        weight.boneIndex = to < 0 ? 0 : to;
                        newWeights[k] = weight;
                    }

                    copy.SetBoneWeights(newCounts, newWeights);
                }
                finally
                {
                    newCounts.Dispose();
                    newWeights.Dispose();
                }
            }
        }

        /// <summary>
        /// What of <paramref name="compact"/> is not <paramref name="source"/>'s with its bones renamed by
        /// <paramref name="kept"/>: empty when the copy is right. Looked at: the vertices and every vertex attribute
        /// the source has (positions, normals, tangents, colours, the eight UV sets), the index format, the submeshes
        /// and their indices, the blend shapes and their frames, the bounds, the bind poses, and the weights -- every
        /// vertex's count of weights above zero, their values in order, and the source bone each names.
        /// </summary>
        public static List<string> Differences(Mesh source, Mesh compact, int[] kept)
        {
            var differences = new List<string>();
            if (source == null || compact == null || kept == null)
            {
                differences.Add("a mesh or the kept bones are missing");
                return differences;
            }

            if (compact.vertexCount != source.vertexCount) differences.Add("vertex count " + compact.vertexCount + " for " + source.vertexCount);
            if (compact.indexFormat != source.indexFormat) differences.Add("index format");
            if (compact.subMeshCount != source.subMeshCount) differences.Add("submesh count");
            for (int s = 0; s < source.subMeshCount && s < compact.subMeshCount; s++)
            {
                SubMeshDescriptor a = source.GetSubMesh(s), b = compact.GetSubMesh(s);
                if (a.topology != b.topology || a.indexStart != b.indexStart || a.indexCount != b.indexCount || a.baseVertex != b.baseVertex
                    || a.firstVertex != b.firstVertex || a.vertexCount != b.vertexCount || !a.bounds.Equals(b.bounds))
                {
                    differences.Add("submesh " + s);
                }

                if (!Same(source.GetIndices(s, false), compact.GetIndices(s, false))) differences.Add("indices of submesh " + s);
            }

            foreach (VertexAttribute attribute in new[]
                     {
                         VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent, VertexAttribute.Color, VertexAttribute.TexCoord0,
                         VertexAttribute.TexCoord1, VertexAttribute.TexCoord2, VertexAttribute.TexCoord3, VertexAttribute.TexCoord4, VertexAttribute.TexCoord5,
                         VertexAttribute.TexCoord6, VertexAttribute.TexCoord7,
                     })
            {
                bool has = source.HasVertexAttribute(attribute);
                if (has != compact.HasVertexAttribute(attribute))
                {
                    differences.Add("attribute " + attribute + " present " + compact.HasVertexAttribute(attribute) + " for " + has);
                }
                else if (has && (source.GetVertexAttributeDimension(attribute) != compact.GetVertexAttributeDimension(attribute)
                                 || source.GetVertexAttributeFormat(attribute) != compact.GetVertexAttributeFormat(attribute)))
                {
                    differences.Add("attribute " + attribute + " form");
                }
            }

            if (!Same(source.vertices, compact.vertices)) differences.Add("positions");
            if (!Same(source.normals, compact.normals)) differences.Add("normals");
            if (!Same(source.tangents, compact.tangents)) differences.Add("tangents");
            if (!Same(source.colors, compact.colors)) differences.Add("colours");
            for (int channel = 0; channel < 8; channel++)
            {
                var a = new List<Vector4>();
                var b = new List<Vector4>();
                source.GetUVs(channel, a);
                compact.GetUVs(channel, b);
                if (!Same(a, b)) differences.Add("UV set " + channel);
            }

            if (!source.bounds.Equals(compact.bounds)) differences.Add("bounds");
            if (source.blendShapeCount != compact.blendShapeCount)
            {
                differences.Add("blend shape count");
            }
            else
            {
                var deltaA = new Vector3[source.vertexCount];
                var deltaB = new Vector3[source.vertexCount];
                var normalA = new Vector3[source.vertexCount];
                var normalB = new Vector3[source.vertexCount];
                var tangentA = new Vector3[source.vertexCount];
                var tangentB = new Vector3[source.vertexCount];
                for (int shape = 0; shape < source.blendShapeCount; shape++)
                {
                    if (source.GetBlendShapeName(shape) != compact.GetBlendShapeName(shape)
                        || source.GetBlendShapeFrameCount(shape) != compact.GetBlendShapeFrameCount(shape))
                    {
                        differences.Add("blend shape " + shape);
                        continue;
                    }

                    for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                    {
                        source.GetBlendShapeFrameVertices(shape, frame, deltaA, normalA, tangentA);
                        compact.GetBlendShapeFrameVertices(shape, frame, deltaB, normalB, tangentB);
                        if (source.GetBlendShapeFrameWeight(shape, frame) != compact.GetBlendShapeFrameWeight(shape, frame)
                            || !Same(deltaA, deltaB) || !Same(normalA, normalB) || !Same(tangentA, tangentB))
                        {
                            differences.Add("blend shape " + shape + " frame " + frame);
                        }
                    }
                }
            }

            Matrix4x4[] sourceBinds = source.bindposes, compactBinds = compact.bindposes;
            if (compactBinds.Length != kept.Length)
            {
                differences.Add("bind poses " + compactBinds.Length + " for " + kept.Length + " kept bones");
                return differences;
            }

            for (int c = 0; c < kept.Length; c++)
            {
                if ((uint)kept[c] >= (uint)sourceBinds.Length || !SameBits(compactBinds[c], sourceBinds[kept[c]]))
                {
                    differences.Add("bind pose " + c + " is not the source's " + kept[c]);
                    break;
                }
            }

            using (NativeArray<byte> countsA = source.GetBonesPerVertex())
            using (NativeArray<byte> countsB = compact.GetBonesPerVertex())
            using (NativeArray<BoneWeight1> weightsA = source.GetAllBoneWeights())
            using (NativeArray<BoneWeight1> weightsB = compact.GetAllBoneWeights())
            {
                if (countsA.Length != countsB.Length)
                {
                    differences.Add("weighted vertices " + countsB.Length + " for " + countsA.Length);
                    return differences;
                }

                int a = 0, b = 0;
                for (int v = 0; v < countsA.Length; v++)
                {
                    int endA = a + countsA[v], endB = b + countsB[v];
                    int i = a, j = b;
                    bool same = true;
                    while (same)
                    {
                        while (i < endA && weightsA[i].weight == 0f) i++;
                        while (j < endB && weightsB[j].weight == 0f) j++;
                        if (i >= endA || j >= endB) break;
                        BoneWeight1 x = weightsA[i], y = weightsB[j];
                        same = x.weight == y.weight && (uint)y.boneIndex < (uint)kept.Length && kept[y.boneIndex] == x.boneIndex;
                        i++;
                        j++;
                    }

                    while (i < endA && weightsA[i].weight == 0f) i++;
                    while (j < endB && weightsB[j].weight == 0f) j++;
                    if (!same || i < endA || j < endB)
                    {
                        var told = new System.Text.StringBuilder("the weights of vertex " + v + ": the source's");
                        for (int k = a; k < endA; k++) told.Append(' ').Append(weightsA[k].boneIndex).Append(':').Append(weightsA[k].weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        told.Append("; the copy's (by the source's bones)");
                        for (int k = b; k < endB; k++)
                        {
                            told.Append(' ').Append((uint)weightsB[k].boneIndex < (uint)kept.Length ? kept[weightsB[k].boneIndex] : -1).Append(':')
                                .Append(weightsB[k].weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        }

                        differences.Add(told.ToString());
                        break;
                    }

                    a = endA;
                    b = endB;
                }
            }

            return differences;
        }

        private static bool SameBits(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            }

            return true;
        }

        private static bool Same<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : IEquatable<T>
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }

            return true;
        }
    }
}
