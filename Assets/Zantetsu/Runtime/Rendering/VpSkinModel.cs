using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A skinned model's one input for drawing and for the CPU bake (DESIGN 4.5.12): the 24-byte vertices
    /// (<see cref="VpSkinVertex"/>), the indices, the topology, the bind poses, and the box of every bone. Made once
    /// from a renderer's shared mesh -- cold, with the checks of the direct skin input -- and never written again: the
    /// model's, shared by every individual of it (a crowd's slots of one model read one), and by the prepared cut's
    /// display input (<see cref="VpDirectSkinInput"/>), which skins these very vertices by a captured pose. Nothing of
    /// an individual -- no Transform, no pose, no palette -- is here.
    /// <para>
    /// **Bones.** The palette order is the mesh's bone order: vertex <c>bone</c> numbers name <c>bindposes[bone]</c>, and
    /// a renderer of this mesh lists its bones in that order (D-195 keeps the three one index space). At most 256.
    /// </para>
    /// <para>
    /// **Bone boxes.** Bone <c>b</c>'s box holds every vertex whose <em>source</em> weight on <c>b</c> is above zero,
    /// at the bind pose -- not the vertices whose largest weight it is -- widened by <see cref="BoxMargin"/>. A
    /// linear blend of non-negative weights summing to one lies in the convex hull of the bones' transformed points,
    /// so the union of the transformed boxes holds the skinned vertex (VpSkinBounds.compute). The quantized weights
    /// are a subset of the source influences (a weight may quantize to zero, never from zero), so the boxes hold the
    /// quantized skinning too.
    /// </para>
    /// The caller owns the model and disposes it after everything that reads it.
    /// </summary>
    public sealed unsafe class VpSkinModel : IDisposable
    {
        /// <summary>How much each bone box is widened on every side, in the model's units: room for the skinning's rounding.</summary>
        public const float BoxMargin = 1e-3f;

        private NativeArray<VpSkinVertex> _vertices;
        private NativeArray<uint> _indices;
        private NativeArray<int> _topology;
        private NativeArray<VpSkinBoneBox> _boneBoxes;
        private readonly Matrix4x4[] _bindPoses;
        private readonly bool[] _used;   // managed, so that a reader of a disposed model is told, not crashed
        private bool _disposed;
        private bool _ownerDone;
        private int _holds;

        /// <summary>How many models were made in this process (measurement and tests).</summary>
        public static int CreatedCount { get; private set; }

        private VpSkinModel(Mesh mesh, VpSkinVertex[] vertices, uint[] indices, int[] topology, int topologyCount, VpSkinBoneBox[] boxes,
            Matrix4x4[] bindPoses, int usedBones, Bounds bindBounds)
        {
            SourceMesh = mesh;
            VertexCount = vertices.Length;
            IndexCount = indices.Length;
            TopologyCount = topologyCount;
            BoneCount = bindPoses.Length;
            UsedBoneCount = usedBones;
            BindBounds = bindBounds;
            _bindPoses = bindPoses;
            _used = new bool[boxes.Length];
            for (int b = 0; b < boxes.Length; b++) _used[b] = !boxes[b].IsEmpty;
            _vertices = new NativeArray<VpSkinVertex>(vertices, Allocator.Persistent);
            _indices = new NativeArray<uint>(indices, Allocator.Persistent);
            _topology = new NativeArray<int>(topology, Allocator.Persistent);
            _boneBoxes = new NativeArray<VpSkinBoneBox>(boxes, Allocator.Persistent);
            CreatedCount++;
        }

        /// <summary>The mesh this model was made from (an owner compares it before lending the model to a renderer).</summary>
        public Mesh SourceMesh { get; }

        public int VertexCount { get; }
        public int IndexCount { get; }
        public int TopologyCount { get; }

        /// <summary>The palette's size: every bone the mesh lists a bind pose for.</summary>
        public int BoneCount { get; }

        /// <summary>How many of them some vertex is weighted to.</summary>
        public int UsedBoneCount { get; }

        /// <summary>Every vertex at the bind pose, in the model's own frame.</summary>
        public Bounds BindBounds { get; }

        public bool IsDisposed => _disposed;

        /// <summary>The 24-byte vertices; a view, valid until the model is disposed.</summary>
        public NativeArray<VpSkinVertex>.ReadOnly Vertices { get { ThrowIfDisposed(); return _vertices.AsReadOnly(); } }

        /// <summary>The indices, local to the model (0 .. VertexCount - 1).</summary>
        public NativeArray<uint>.ReadOnly Indices { get { ThrowIfDisposed(); return _indices.AsReadOnly(); } }

        /// <summary>The topology vertex each vertex belongs to.</summary>
        public NativeArray<int>.ReadOnly Topology { get { ThrowIfDisposed(); return _topology.AsReadOnly(); } }

        /// <summary>One box a bone of the palette, in palette order.</summary>
        public NativeArray<VpSkinBoneBox>.ReadOnly BoneBoxes { get { ThrowIfDisposed(); return _boneBoxes.AsReadOnly(); } }

        /// <summary>The bind pose of palette bone <paramref name="bone"/>, as the mesh holds it.</summary>
        public Matrix4x4 BindPose(int bone) => _bindPoses[bone];

        /// <summary>The vertices as the array itself, for a GPU write; never written through (the model is immutable).</summary>
        internal NativeArray<VpSkinVertex> VertexArray { get { ThrowIfDisposed(); return _vertices; } }

        /// <summary>Whether some vertex is weighted to palette bone <paramref name="bone"/> (an unused bone needs no matrix).</summary>
        public bool IsBoneUsed(int bone) => _used[bone];

        internal VpSkinVertex* VertexPointer { get { ThrowIfDisposed(); return (VpSkinVertex*)_vertices.GetUnsafeReadOnlyPtr(); } }
        internal uint* IndexPointer { get { ThrowIfDisposed(); return (uint*)_indices.GetUnsafeReadOnlyPtr(); } }
        internal int* TopologyPointer { get { ThrowIfDisposed(); return (int*)_topology.GetUnsafeReadOnlyPtr(); } }

        /// <summary>The bytes the model holds natively.</summary>
        public long NativeBytes => _disposed ? 0 : (long)_vertices.Length * VpSkinVertex.Stride + (long)_indices.Length * sizeof(uint)
            + (long)_topology.Length * sizeof(int) + (long)_boneBoxes.Length * VpSkinBoneBox.Stride;

        /// <summary>Whether <paramref name="renderer"/> draws this model's mesh with as many bones as the palette holds.</summary>
        public bool Describes(SkinnedMeshRenderer renderer)
        {
            return !_disposed && renderer != null && renderer.sharedMesh == SourceMesh && renderer.bones.Length == BoneCount;
        }

        private static bool FourWeights(SkinnedMeshRenderer r) => r.quality == SkinQuality.Bone4
            || (r.quality == SkinQuality.Auto && QualitySettings.skinWeights == SkinWeights.FourBones);

        /// <summary>
        /// Cold only. The model of <paramref name="renderer"/>'s shared mesh, with the direct skin's checks: a readable
        /// single-submesh triangle mesh of four weights a vertex (1..4 above zero), no blend shape, at most 256 bones,
        /// finite non-negative weights summing to one, finite bind poses, one position and one set of weights per
        /// topology vertex, every vertex referenced, and the cut input gate passed. The renderer's own frame and bone
        /// Transforms are not judged here (they are the individual's). False, with the reason, for anything else:
        /// nothing is dropped or mended.
        /// </summary>
        public static bool TryCreate(SkinnedMeshRenderer renderer, int[] topology, int topologyCount, VpCutInputConnectivity verified,
            out VpSkinModel model, out string refusal)
        {
            model = null;
            refusal = null;
            if (VpRenderVertex.Stride != 16) { refusal = "the 16-byte vertex is not in use"; return false; }
            if (renderer == null || renderer.sharedMesh == null) { refusal = "no renderer or no mesh"; return false; }
            if (topology == null || topologyCount <= 0) { refusal = "no topology"; return false; }
            if (!FourWeights(renderer)) { refusal = "the renderer does not skin four weights a vertex"; return false; }
            Mesh mesh = renderer.sharedMesh;
            if (!mesh.isReadable) { refusal = "the mesh is not readable"; return false; }
            if (mesh.blendShapeCount != 0) { refusal = "the mesh has blend shapes"; return false; }
            if (mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles) { refusal = "the mesh is not one triangle submesh"; return false; }
            if (mesh.vertexCount == 0) { refusal = "the mesh has no vertex"; return false; }
            using (var counts = mesh.GetBonesPerVertex())
            {
                if (counts.Length != mesh.vertexCount) { refusal = "the mesh has no weight count a vertex"; return false; }
                foreach (byte count in counts) if (count == 0 || count > 4) { refusal = "a vertex has " + count + " influences (1 to 4 are supported)"; return false; }
            }

            Vector3[] p = mesh.vertices;
            Vector3[] n = mesh.normals;
            Vector2[] tex = mesh.uv;
            BoneWeight[] w = mesh.boneWeights;
            Matrix4x4[] binds = mesh.bindposes;
            int[] signedIndices = mesh.triangles;
            if (p.Length != n.Length || p.Length != tex.Length || p.Length != w.Length || p.Length != topology.Length)
            {
                refusal = "the mesh's positions, normals, UVs, weights and the topology are not of one length";
                return false;
            }

            if (binds.Length == 0 || binds.Length > VpSkinVertex.MaxBones) { refusal = "the mesh lists " + binds.Length + " bones (1 to " + VpSkinVertex.MaxBones + " are supported)"; return false; }
            if (renderer.bones.Length != binds.Length) { refusal = "the renderer lists " + renderer.bones.Length + " bones for the mesh's " + binds.Length + " bind poses"; return false; }
            if (signedIndices.Length == 0) { refusal = "the mesh has no index"; return false; }
            for (int b = 0; b < binds.Length; b++)
            {
                for (int k = 0; k < 16; k++) if (!float.IsFinite(binds[b][k])) { refusal = "bind pose " + b + " is not finite"; return false; }
            }

            var vertices = new VpSkinVertex[p.Length];
            var attributes = new VpRenderVertex[p.Length];
            var used = new bool[binds.Length];
            var boxMin = new Vector3[binds.Length];
            var boxMax = new Vector3[binds.Length];
            for (int b = 0; b < binds.Length; b++) { boxMin[b] = Vector3.positiveInfinity; boxMax[b] = Vector3.negativeInfinity; }
            Vector3 bindMin = Vector3.positiveInfinity, bindMax = Vector3.negativeInfinity;
            var representatives = new int[topologyCount];
            for (int i = 0; i < representatives.Length; i++) representatives[i] = -1;
            for (int i = 0; i < p.Length; i++)
            {
                BoneWeight value = w[i];
                int[] ids = { value.boneIndex0, value.boneIndex1, value.boneIndex2, value.boneIndex3 };
                float[] ws = { value.weight0, value.weight1, value.weight2, value.weight3 };
                if (!VpSkinWeights.TryQuantize(ws[0], ws[1], ws[2], ws[3], out byte q0, out byte q1, out byte q2, out byte q3))
                {
                    refusal = "vertex " + i + " has weights that are not finite, negative, or not summing to one";
                    return false;
                }

                byte[] qs = { q0, q1, q2, q3 };
                var bones = new byte[4];
                for (int j = 0; j < 4; j++)
                {
                    if (ws[j] > 0f)
                    {
                        if (ids[j] < 0 || ids[j] >= binds.Length) { refusal = "vertex " + i + " is weighted to bone " + ids[j] + " of " + binds.Length; return false; }
                        used[ids[j]] = true;
                        boxMin[ids[j]] = Vector3.Min(boxMin[ids[j]], p[i]);
                        boxMax[ids[j]] = Vector3.Max(boxMax[ids[j]], p[i]);
                        bones[j] = (byte)ids[j];
                    }
                    else
                    {
                        // A place of no weight names a bone all the same: its own when valid, else the first.
                        bones[j] = (byte)(ids[j] >= 0 && ids[j] < binds.Length ? ids[j] : 0);
                    }
                }

                attributes[i] = new VpRenderVertex { position = p[i], normal = n[i], uv0 = tex[i] };
                if (!attributes[i].HasValidAttributes) { refusal = "vertex " + i + " has a normal or a UV out of the supported range"; return false; }
                int id = topology[i];
                if (id < 0 || id >= topologyCount) { refusal = "vertex " + i + " names topology vertex " + id + " of " + topologyCount; return false; }
                vertices[i] = new VpSkinVertex
                {
                    position = p[i], normalX = attributes[i].normalX, normalY = attributes[i].normalY, u = attributes[i].u, v = attributes[i].v,
                    bone0 = bones[0], bone1 = bones[1], bone2 = bones[2], bone3 = bones[3], weight0 = qs[0], weight1 = qs[1], weight2 = qs[2], weight3 = qs[3],
                };
                int previous = representatives[id];
                if (previous < 0) representatives[id] = i;
                else if (!p[previous].Equals(p[i]) || !w[previous].Equals(w[i]))
                {
                    refusal = "vertices " + previous + " and " + i + " share topology vertex " + id + " with different positions or weights";
                    return false;
                }

                bindMin = Vector3.Min(bindMin, p[i]);
                bindMax = Vector3.Max(bindMax, p[i]);
            }

            var indices = new uint[signedIndices.Length];
            var referenced = new bool[p.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                int v = signedIndices[i];
                if (v < 0 || v >= p.Length) { refusal = "index " + i + " names vertex " + v + " of " + p.Length; return false; }
                indices[i] = (uint)v;
                referenced[v] = true;
            }

            for (int i = 0; i < referenced.Length; i++) if (!referenced[i]) { refusal = "vertex " + i + " is referenced by no triangle"; return false; }
            VpCutInputVerdict verdict = VpCutInputGate.Check(attributes, indices, topology, topologyCount, new[] { new VpGeometrySubmesh(0, indices.Length, 0) }, verified);
            if (!verdict.Accepted) { refusal = "the cut input gate refused the mesh: " + verdict; return false; }

            int usedBones = 0;
            var boxes = new VpSkinBoneBox[binds.Length];
            for (int b = 0; b < binds.Length; b++)
            {
                if (!used[b]) { boxes[b] = VpSkinBoneBox.Empty; continue; }
                usedBones++;
                Vector3 centre = (boxMin[b] + boxMax[b]) * 0.5f;
                Vector3 extents = (boxMax[b] - boxMin[b]) * 0.5f + Vector3.one * BoxMargin;
                boxes[b] = new VpSkinBoneBox { centre = centre, extents = extents };
            }

            var copied = new int[topology.Length];
            Array.Copy(topology, copied, topology.Length);
            model = new VpSkinModel(mesh, vertices, indices, copied, topologyCount, boxes, (Matrix4x4[])binds.Clone(), usedBones,
                new Bounds((bindMin + bindMax) * 0.5f, bindMax - bindMin));
            return true;
        }

        /// <summary>
        /// The box of every bone the palette matrices <paramref name="palette"/> (renderer-local, one a bone, in palette
        /// order) carry, united: the bounds the GPU writes for an individual of this pose (VpSkinBounds.compute), made
        /// here on the CPU for a check or a test. Empty when no bone is used.
        /// </summary>
        public VpInstanceBounds BoundsOf(IReadOnlyList<Matrix4x4> palette)
        {
            ThrowIfDisposed();
            if (palette == null || palette.Count < BoneCount) throw new ArgumentException("a matrix a palette bone is needed", nameof(palette));
            bool any = false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int b = 0; b < BoneCount; b++)
            {
                VpSkinBoneBox box = _boneBoxes[b];
                if (box.IsEmpty) continue;
                Bounds carried = VpDirectDraw.WorldBounds(new Bounds(box.centre, box.extents * 2f), palette[b]);
                min = Vector3.Min(min, carried.min);
                max = Vector3.Max(max, carried.max);
                any = true;
            }

            return any ? new VpInstanceBounds { centre = (min + max) * 0.5f, extents = (max - min) * 0.5f } : new VpInstanceBounds { extents = new Vector3(-1f, -1f, -1f) };
        }

        /// <summary>
        /// A reader that needs the model past its owner's end (the skin draw's geometry writes every placed model
        /// again when it grows) holds it: the owner's <see cref="Dispose"/> then frees nothing until the last
        /// <see cref="Release"/>, and a release after the owner's end frees it, once (review 1, item 2).
        /// </summary>
        public void Hold()
        {
            ThrowIfDisposed();
            _holds++;
        }

        public void Release()
        {
            if (_disposed) return;
            if (_holds > 0) _holds--;
            if (_holds == 0 && _ownerDone) Free();
        }

        /// <summary>How many readers hold the model now.</summary>
        public int HoldCount => _holds;

        /// <summary>The owner's end: freed now, or when the last holder releases it.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _ownerDone = true;
            if (_holds > 0) return;
            Free();
        }

        private void Free()
        {
            if (_disposed) return;
            _disposed = true;
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            if (_topology.IsCreated) _topology.Dispose();
            if (_boneBoxes.IsCreated) _boneBoxes.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VpSkinModel));
        }
    }
}
