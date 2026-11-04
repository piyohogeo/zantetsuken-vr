using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// A skinned model's bones by vertex (NPC-BONE-SPLIT-1): for every vertex of the model's mesh, the bones of its palette it
    /// has a weight greater than zero on -- read once from the mesh's own weights, however small a positive weight is, never
    /// the largest alone and never through a threshold -- up to four a vertex (the four-weight path of the prepared cut). One
    /// object a mesh, shared by every individual and every lineage of it, held by each and given back by the last.
    /// <para>
    /// Each vertex is one <c>ulong</c>: four 16-bit palette numbers, <see cref="None"/> where there is none. The numbers are the
    /// palette's -- the renderer's own (shortened, DESIGN 9 D-195) bone list -- which is the index space of the NPC's bone rig
    /// for every weighted bone (<see cref="NpcBoneRig"/>).
    /// </para>
    /// </summary>
    public sealed unsafe class NpcVertexBones
    {
        /// <summary>The entry of an influence that names no bone.</summary>
        public const ushort None = 0xFFFF;

        private static readonly Dictionary<Mesh, NpcVertexBones> s_ofMesh = new Dictionary<Mesh, NpcVertexBones>();

        private NativeArray<ulong> _bones;
        private readonly Mesh _key;
        private int _holds;

        private NpcVertexBones(NativeArray<ulong> bones, int paletteCount, Mesh key)
        {
            _bones = bones;
            PaletteCount = paletteCount;
            _key = key;
        }

        /// <summary>How many vertices the model has.</summary>
        public int VertexCount => _bones.IsCreated ? _bones.Length : 0;

        /// <summary>How many bones the palette has (the mesh's bind poses).</summary>
        public int PaletteCount { get; }

        /// <summary>The native bytes held (8 a vertex).</summary>
        public long NativeBytes => _bones.IsCreated ? 8L * _bones.Length : 0;

        /// <summary>How many models' tables are held now (accounting).</summary>
        public static int LiveCount => s_ofMesh.Count;

        internal ulong* Pointer => (ulong*)_bones.GetUnsafeReadOnlyPtr();

        /// <summary>The four palette numbers of one vertex (<see cref="None"/> where there is none).</summary>
        public ushort Bone(int vertex, int influence) => (ushort)((_bones[vertex] >> (16 * influence)) & 0xFFFF);

        /// <summary>Four palette numbers (or <see cref="None"/>) as one entry.</summary>
        public static ulong Pack(ushort b0, ushort b1, ushort b2, ushort b3) =>
            b0 | ((ulong)b1 << 16) | ((ulong)b2 << 32) | ((ulong)b3 << 48);

        /// <summary>
        /// The table of <paramref name="mesh"/>, held: the one made already when there is one, made here otherwise (cold
        /// preparation, the main thread). False, with the reason, for weights that are not finite or negative, a weight on a bone
        /// the mesh has no bind pose for, or a vertex with more than four positive weights.
        /// </summary>
        public static bool TryAcquire(Mesh mesh, out NpcVertexBones table, out string refusal)
        {
            table = null;
            refusal = null;
            if (mesh == null)
            {
                refusal = "no mesh";
                return false;
            }

            if (s_ofMesh.TryGetValue(mesh, out NpcVertexBones made))
            {
                made._holds++;
                table = made;
                return true;
            }

            int vertexCount = mesh.vertexCount;
            int palette = mesh.bindposeCount;
            if (palette <= 0 || palette >= None)
            {
                refusal = "the mesh has " + palette + " bind poses";
                return false;
            }

            var bones = new NativeArray<ulong>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                using (NativeArray<byte> perVertex = mesh.GetBonesPerVertex())
                using (NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights())
                {
                    if (perVertex.Length != vertexCount)
                    {
                        refusal = "the mesh has bone counts for " + perVertex.Length + " of its " + vertexCount + " vertices";
                        bones.Dispose();
                        return false;
                    }

                    int at = 0;
                    for (int v = 0; v < vertexCount; v++)
                    {
                        int count = perVertex[v];
                        ulong entry = Pack(None, None, None, None);
                        int kept = 0;
                        for (int k = 0; k < count; k++, at++)
                        {
                            BoneWeight1 w = weights[at];
                            if (!float.IsFinite(w.weight) || w.weight < 0f)
                            {
                                refusal = "a weight of vertex " + v + " is not finite or is negative";
                                bones.Dispose();
                                return false;
                            }

                            if (!(w.weight > 0f))
                            {
                                continue;   // a place the vertex holds without using it
                            }

                            if ((uint)w.boneIndex >= (uint)palette)
                            {
                                refusal = "vertex " + v + " is weighted to bone " + w.boneIndex + " of " + palette;
                                bones.Dispose();
                                return false;
                            }

                            if (kept == 4)
                            {
                                refusal = "vertex " + v + " has more than four positive weights";
                                bones.Dispose();
                                return false;
                            }

                            entry &= ~(0xFFFFUL << (16 * kept));
                            entry |= (ulong)(ushort)w.boneIndex << (16 * kept);
                            kept++;
                        }

                        bones[v] = entry;
                    }
                }
            }
            catch
            {
                if (bones.IsCreated) bones.Dispose();
                throw;
            }

            made = new NpcVertexBones(bones, palette, mesh) { _holds = 1 };
            s_ofMesh[mesh] = made;
            table = made;
            return true;
        }

        /// <summary>A table of the caller's entries (tests and fixtures that have no skinned mesh), held once, shared with nobody.</summary>
        internal static NpcVertexBones FromEntries(ulong[] entries, int paletteCount)
        {
            var bones = new NativeArray<ulong>(entries, Allocator.Persistent);
            return new NpcVertexBones(bones, paletteCount, null) { _holds = 1 };
        }

        /// <summary>One more holder.</summary>
        public void Hold()
        {
            if (!_bones.IsCreated) throw new ObjectDisposedException(nameof(NpcVertexBones));
            _holds++;
        }

        /// <summary>One holder fewer; the last gives the table back.</summary>
        public void Release()
        {
            if (_holds <= 0) return;
            if (--_holds > 0) return;
            if (_key != null && s_ofMesh.TryGetValue(_key, out NpcVertexBones made) && ReferenceEquals(made, this)) s_ofMesh.Remove(_key);
            if (_bones.IsCreated) _bones.Dispose();
            _bones = default;
        }

        public bool IsReleased => !_bones.IsCreated;
    }

    /// <summary>
    /// One prepared NPC's bones for the bone-aware split (NPC-BONE-SPLIT-1), made once in its cold preparation from its
    /// renderer, its model and its convexes' bones -- the only place a Transform of the rig is read for it. The rig's index
    /// space: the palette's bones first (0 .. P-1, the renderer's bone list, the numbers the vertex weights use), then the
    /// convexes' bones the palette does not list. A bone of the palette no vertex is weighted to and no convex belongs to is
    /// not in the rig (<see cref="Present"/> false). Each bone's parent is its nearest ancestor Transform in the rig.
    /// <para>
    /// A cut reads the pose it was accepted at -- the convexes' bone matrices and the skin palette the hit captured -- for
    /// each bone's joint (<see cref="TryJoints"/>): nothing of the rig's Transforms is read again after this, so the deferred
    /// split never sees a reused or animated rig.
    /// </para>
    /// </summary>
    public sealed class NpcBoneRig : IDisposable
    {
        internal int NodeCount { get; private set; }
        internal int PaletteCount { get; private set; }
        internal int[] Parent { get; private set; }
        internal bool[] Present { get; private set; }

        /// <summary>The rig bone of each prepared convex, in convex order.</summary>
        internal int[] ConvexNode { get; private set; }

        // Per rig bone: a convex of that bone (its joint is read from the convex's bone matrix), or -1; per palette bone, the
        // bone's origin in the mesh's bind space (the joint by the palette matrix).
        private int[] _jointConvex;
        private float3[] _bindJoint;

        /// <summary>The model's bones by vertex, held for as long as the rig.</summary>
        internal NpcVertexBones Vertices { get; private set; }

        private NpcBoneRig()
        {
        }

        /// <summary>
        /// The rig of a prepared NPC (cold, main thread): the palette of <paramref name="renderer"/> (its bones, the model's
        /// used bones and bind poses), the bones of the convexes in convex order, and the model's bones by vertex. False with
        /// the reason when a part is missing or inconsistent; the NPC is then cut as before and its pieces are not split.
        /// </summary>
        public static bool TryCreate(SkinnedMeshRenderer renderer, VpSkinModel model, IReadOnlyList<Transform> convexBones,
            out NpcBoneRig rig, out string refusal)
        {
            rig = null;
            refusal = null;
            if (renderer == null || model == null || model.IsDisposed || convexBones == null || convexBones.Count == 0)
            {
                refusal = "no renderer, model or convex bones";
                return false;
            }

            Transform[] palette = renderer.bones;   // once: each read copies the list
            int p = model.BoneCount;
            if (palette == null || palette.Length < p)
            {
                refusal = "the renderer lists " + (palette == null ? 0 : palette.Length) + " bones for the model's " + p;
                return false;
            }

            var nodeOf = new Dictionary<Transform, int>();
            var transforms = new List<Transform>(p + convexBones.Count);
            var present = new List<bool>(p + convexBones.Count);
            for (int c = 0; c < p; c++)
            {
                transforms.Add(palette[c]);
                bool used = model.IsBoneUsed(c) && palette[c] != null;
                present.Add(used);
                if (palette[c] != null && !nodeOf.ContainsKey(palette[c])) nodeOf[palette[c]] = c;
            }

            var convexNode = new int[convexBones.Count];
            for (int i = 0; i < convexBones.Count; i++)
            {
                Transform bone = convexBones[i];
                if (bone == null)
                {
                    refusal = "convex " + i + " has no bone";
                    return false;
                }

                if (!nodeOf.TryGetValue(bone, out int node))
                {
                    node = transforms.Count;
                    transforms.Add(bone);
                    present.Add(true);
                    nodeOf[bone] = node;
                }

                present[node] = true;   // a palette bone no vertex uses is in the rig when a convex belongs to it
                convexNode[i] = node;
            }

            int n = transforms.Count;
            var jointConvex = new int[n];
            for (int k = 0; k < n; k++) jointConvex[k] = -1;
            for (int i = convexNode.Length - 1; i >= 0; i--) jointConvex[convexNode[i]] = i;   // the first convex of each bone
            var bindJoint = new float3[p];
            for (int c = 0; c < p; c++)
            {
                if (!present[c] || jointConvex[c] >= 0) continue;
                Matrix4x4 inverse = model.BindPose(c).inverse;
                Vector4 origin = inverse.GetColumn(3);
                bindJoint[c] = new float3(origin.x, origin.y, origin.z);
                if (!math.all(math.isfinite(bindJoint[c])))
                {
                    refusal = "the bind pose of bone " + c + " has no finite inverse";
                    return false;
                }
            }

            // Each bone's parent: its nearest ancestor that is in the rig.
            var parent = new int[n];
            for (int k = 0; k < n; k++)
            {
                parent[k] = -1;
                if (!present[k]) continue;
                for (Transform t = transforms[k].parent; t != null; t = t.parent)
                {
                    if (nodeOf.TryGetValue(t, out int up) && present[up] && up != k)
                    {
                        parent[k] = up;
                        break;
                    }
                }
            }

            if (!NpcVertexBones.TryAcquire(model.SourceMesh, out NpcVertexBones vertices, out string why))
            {
                refusal = "the model's weights: " + why;
                return false;
            }

            if (vertices.VertexCount != model.VertexCount || vertices.PaletteCount != p)
            {
                vertices.Release();
                refusal = "the model's weights describe " + vertices.VertexCount + " vertices of " + vertices.PaletteCount + " bones, the model "
                    + model.VertexCount + " of " + p;
                return false;
            }

            rig = new NpcBoneRig
            {
                NodeCount = n, PaletteCount = p, Parent = parent, Present = present.ToArray(), ConvexNode = convexNode,
                _jointConvex = jointConvex, _bindJoint = bindJoint, Vertices = vertices,
            };
            return true;
        }

        /// <summary>
        /// A rig of the caller's numbers (tests and fixtures without a skinned renderer): bones, parents, which are present, the
        /// convexes' bones and the bones by vertex (held by the rig; the caller's own hold stays the caller's).
        /// </summary>
        internal static NpcBoneRig FromNumbers(int[] parent, bool[] present, int[] convexNode, NpcVertexBones vertices)
        {
            vertices.Hold();
            var jointConvex = new int[parent.Length];
            for (int k = 0; k < jointConvex.Length; k++) jointConvex[k] = -1;
            return new NpcBoneRig
            {
                NodeCount = parent.Length, PaletteCount = vertices.PaletteCount, Parent = (int[])parent.Clone(), Present = (bool[])present.Clone(),
                ConvexNode = (int[])convexNode.Clone(), _jointConvex = jointConvex, _bindJoint = new float3[vertices.PaletteCount], Vertices = vertices,
            };
        }

        /// <summary>
        /// Every rig bone's joint at the pose a cut was accepted at, in the renderer's frame (the cut's base geometry's
        /// coordinates): from the convexes' bone matrices where a convex belongs to the bone, from the captured skin palette
        /// otherwise. NaN for a bone not in the rig. False, with the reason, for a joint that is not finite.
        /// </summary>
        internal bool TryJoints(VpDirectSkinInput palette, float4x4[] boneToOwner, out float3[] joints, out string why)
        {
            why = null;
            joints = new float3[NodeCount];
            for (int k = 0; k < NodeCount; k++)
            {
                if (!Present[k])
                {
                    joints[k] = new float3(float.NaN);
                    continue;
                }

                int convex = _jointConvex[k];
                if (convex >= 0)
                {
                    if (boneToOwner == null || convex >= boneToOwner.Length)
                    {
                        why = "no matrix for the bone of convex " + convex;
                        return false;
                    }

                    joints[k] = boneToOwner[convex].c3.xyz;
                }
                else if (k < PaletteCount && palette != null && palette.TryGetCapturedMatrix(k, out float4x4 matrix))
                {
                    joints[k] = math.transform(matrix, _bindJoint[k]);
                }
                else
                {
                    why = "no pose for bone " + k;
                    return false;
                }

                if (!math.all(math.isfinite(joints[k])))
                {
                    why = "the joint of bone " + k + " is not finite";
                    return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            Vertices?.Release();
            Vertices = null;
        }
    }

    /// <summary>One cut's new vertices and where each came from, as a decision reads them (immutable once made).</summary>
    internal unsafe struct NpcVertexChunk
    {
        internal uint2* origins;
        internal uint start;
        internal int count;

        /// <summary>The first of these vertices' numbers in the lineage's own (dense) vertex numbering.</summary>
        internal int localBase;
    }

    /// <summary>
    /// What one NPC's cut lineage keeps for the bone-aware split (NPC-BONE-SPLIT-1), from its first cut on: the rig's bones and
    /// parents, each bone's joint at the pose that cut was accepted at (the base geometry's coordinates, which every piece of
    /// the lineage keeps), the base geometry's vertices -- the model's, one to one -- and, a cut after another, where the new
    /// vertices of each came from (<see cref="NpcVertexChunk"/>). Held by every fragment record of the lineage and by every
    /// attempt reading it; the last holder gives its native parts back.
    /// </summary>
    internal sealed unsafe class NpcBoneLineage
    {
        private readonly List<NativeArray<uint2>> _owned = new List<NativeArray<uint2>>();
        private int _holds;

        internal NpcBoneLineage(int nodeCount, int[] parent, bool[] present, float3[] joints, NpcVertexBones vertices, uint baseStart, int baseCount, string insufficient)
        {
            NodeCount = nodeCount;
            Parent = parent;
            Present = present;
            Joints = joints;
            Vertices = vertices;
            vertices?.Hold();
            BaseStart = baseStart;
            BaseCount = baseCount;
            Insufficient = insufficient;
            LocalCount = baseCount;
            Chunks = Array.Empty<NpcVertexChunk>();
        }

        internal int NodeCount { get; }
        internal int[] Parent { get; }
        internal bool[] Present { get; }
        internal float3[] Joints { get; }
        internal NpcVertexBones Vertices { get; private set; }
        internal uint BaseStart { get; }
        internal int BaseCount { get; }

        /// <summary>Why no split of this lineage can be decided (the rig, the pose); null when one can.</summary>
        internal string Insufficient { get; }

        /// <summary>The chunks so far: a new array each time one is added, so an attempt's copy of the reference never changes.</summary>
        internal NpcVertexChunk[] Chunks { get; private set; }

        /// <summary>The lineage's vertices so far: the base geometry's and every recorded cut's new ones.</summary>
        internal int LocalCount { get; private set; }

        internal long OriginBytes { get; private set; }

        internal bool IsReleased { get; private set; }

        internal void Hold()
        {
            if (IsReleased) throw new ObjectDisposedException(nameof(NpcBoneLineage));
            _holds++;
        }

        internal void Release()
        {
            if (_holds <= 0) return;
            if (--_holds > 0) return;
            IsReleased = true;
            for (int i = 0; i < _owned.Count; i++)
            {
                if (_owned[i].IsCreated) _owned[i].Dispose();
            }

            _owned.Clear();
            Chunks = Array.Empty<NpcVertexChunk>();
            Vertices?.Release();
            Vertices = null;
        }

        /// <summary>
        /// One cut's new vertices from <paramref name="start"/> on, <paramref name="count"/> of them, and their origins (the first
        /// <paramref name="count"/> entries of <paramref name="origins"/>, copied into an array of exactly that length; the
        /// given array is disposed here). Main thread.
        /// </summary>
        internal void AddChunk(NativeArray<uint2> origins, uint start, int count)
        {
            try
            {
                if (IsReleased || count <= 0 || !origins.IsCreated || origins.Length < count) return;
                var kept = new NativeArray<uint2>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NativeArray<uint2>.Copy(origins, kept, count);
                _owned.Add(kept);
                var chunks = new NpcVertexChunk[Chunks.Length + 1];
                Array.Copy(Chunks, chunks, Chunks.Length);
                chunks[Chunks.Length] = new NpcVertexChunk
                {
                    origins = (uint2*)kept.GetUnsafeReadOnlyPtr(), start = start, count = count, localBase = LocalCount,
                };
                Chunks = chunks;
                LocalCount += count;
                OriginBytes += 8L * count;
            }
            finally
            {
                if (origins.IsCreated) origins.Dispose();
            }
        }
    }

    /// <summary>
    /// One fragment of an NPC's lineage as the bone-aware split reads it: its lineage, and which bone-tree connections its
    /// cuts so far have taken off (bit k: the connection of bone k to its parent, taken off where a cut's plane put the two
    /// joints on different sides). Never written after it is made: a commit and a split make new records.
    /// </summary>
    internal sealed class NpcBoneFragment
    {
        internal NpcBoneFragment(NpcBoneLineage lineage, NpcBonePiece piece)
        {
            Lineage = lineage;
            Piece = piece;
        }

        internal NpcBoneLineage Lineage { get; }

        /// <summary>The part of the rig this fragment holds (NPC-BONE-ACTIVE-2); never changed once made (each cut and split makes new ones).</summary>
        internal NpcBonePiece Piece { get; }
    }

    /// <summary>
    /// The part of an NPC's rig one fragment holds (NPC-BONE-ACTIVE-2), in the coordinates of the lineage's joints (the base
    /// geometry's, at the pose its first cut was accepted at -- the cut planes' frame; no Transform is read). A bone's own range is
    /// its joint and its segments, one to each child's joint (what the bone moves: the skin weighted to it, the convex made for it);
    /// segment c is the one from the joint of c's parent to the joint of c. The piece keeps, for every joint, whether it is on the
    /// fragment's side of every cut so far, and for every segment the part [lo, hi] of it (0 at the parent's joint, 1 at the child's)
    /// on that side -- empty when lo &gt; hi. A bone is present in the piece when its joint is kept or a part of one of its segments is.
    /// The decision holds a bone where the piece keeps a convex of it or its joint, and uses a parent-child connection where the
    /// segment has a part here and both bones are held (NpcBoneSplitSearch, NPC-BONE-HELD-1); this record holds the joints and the
    /// segment parts only, never the convexes, and the decision's holding never writes it. Each side is closed: a joint or a part
    /// exactly on a cut's plane stays on both sides. A commit cuts the source's piece by the plane for each side; a split keeps on each
    /// side the joints of the bones and the segment parts of the connections its decision put there. A split the method as before made
    /// after the bones said connected (NPC-BONE-FALLBACK-1) is followed by its own plane where it had one (a commit's cut), and otherwise
    /// by the convexes: each side keeps the joints of the bones with a convex on it and the segments between two of them. Nothing taken
    /// off comes back: every new piece is a part of the one it was made from.
    /// </summary>
    internal sealed class NpcBonePiece
    {
        private NpcBonePiece(ulong[] jointKept, float[] lo, float[] hi, int[] parent, bool[] rig)
        {
            JointKept = jointKept;
            Lo = lo;
            Hi = hi;
            int n = rig.Length;
            var present = new bool[n];
            for (int k = 0; k < n; k++) present[k] = rig[k] && IsKept(jointKept, k);
            for (int c = 0; c < n; c++)
            {
                int p = parent[c];
                if (p >= 0 && p < n && rig[p] && lo[c] <= hi[c]) present[p] = true;
            }

            Present = present;
            for (int k = 0; k < n; k++) if (present[k]) PresentCount++;
        }

        /// <summary>Bit k: joint k is on this fragment's side of every cut (and its bone was not given to the other side by a split).</summary>
        internal ulong[] JointKept { get; }

        /// <summary>The part of segment c (from the joint of c's parent, 0, to the joint of c, 1) kept; empty when Lo[c] &gt; Hi[c].</summary>
        internal float[] Lo { get; }
        internal float[] Hi { get; }

        /// <summary>Bone k present: its joint or a part of one of its segments kept.</summary>
        internal bool[] Present { get; }
        internal int PresentCount { get; }

        internal bool IsJointKept(int k) => IsKept(JointKept, k);

        private static bool IsKept(ulong[] bits, int k) => (bits[k >> 6] & (1UL << (k & 63))) != 0;

        private static bool Segment(int[] parent, bool[] rig, int c) => parent[c] >= 0 && parent[c] < rig.Length && rig[c] && rig[parent[c]];

        /// <summary>The whole rig: every joint and every segment of the rig's bones.</summary>
        internal static NpcBonePiece Whole(int[] parent, bool[] rig)
        {
            int n = rig.Length;
            var kept = new ulong[Math.Max(1, (n + 63) >> 6)];
            var lo = new float[n];
            var hi = new float[n];
            for (int k = 0; k < n; k++)
            {
                if (rig[k]) kept[k >> 6] |= 1UL << (k & 63);
                if (Segment(parent, rig, k)) { lo[k] = 0f; hi[k] = 1f; } else { lo[k] = 1f; hi[k] = 0f; }
            }

            return new NpcBonePiece(kept, lo, hi, parent, rig);
        }

        /// <summary>
        /// This piece cut by <paramref name="plane"/> (the joints' frame), the side's part: the positive side d &gt;= 0, the negative d &lt;= 0
        /// (closed: what lies on the plane stays on both). A segment the plane crosses keeps its part on each side.
        /// </summary>
        internal NpcBonePiece Cut(float4 plane, bool positive, int[] parent, bool[] rig, float3[] joints)
        {
            int n = rig.Length;
            var kept = (ulong[])JointKept.Clone();
            var lo = (float[])Lo.Clone();
            var hi = (float[])Hi.Clone();
            bool On(float d) => positive ? d >= 0f : d <= 0f;
            for (int k = 0; k < n; k++)
            {
                if (IsKept(kept, k) && !On(math.dot(plane.xyz, joints[k]) + plane.w)) kept[k >> 6] &= ~(1UL << (k & 63));
            }

            for (int c = 0; c < n; c++)
            {
                if (lo[c] > hi[c] || !Segment(parent, rig, c)) continue;
                float da = math.dot(plane.xyz, joints[parent[c]]) + plane.w;
                float db = math.dot(plane.xyz, joints[c]) + plane.w;
                float t0 = lo[c], t1 = hi[c];
                float d0 = ((1f - t0) * da) + (t0 * db), d1 = ((1f - t1) * da) + (t1 * db);
                bool in0 = On(d0), in1 = On(d1);
                if (in0 && in1) continue;
                if (!in0 && !in1) { lo[c] = 1f; hi[c] = 0f; continue; }
                float t = math.clamp(t0 + ((t1 - t0) * (d0 / (d0 - d1))), t0, t1);
                if (in0) hi[c] = t; else lo[c] = t;
            }

            return new NpcBonePiece(kept, lo, hi, parent, rig);
        }

        /// <summary>
        /// NPC-BONE-FALLBACK-1 review 1: this piece's part on one side of a split by no plane (the bent partition's, the graph's): the
        /// joints of the bones with a convex on the side (<paramref name="bones"/>), and the segments whose two bones both have one --
        /// everything else taken off. A bone with no convex on either side keeps its joint on neither: no position is read.
        /// </summary>
        internal NpcBonePiece KeepBones(bool[] bones, int[] parent, bool[] rig)
        {
            int n = rig.Length;
            var kept = (ulong[])JointKept.Clone();
            var lo = (float[])Lo.Clone();
            var hi = (float[])Hi.Clone();
            bool Has(int k) => k >= 0 && k < bones.Length && bones[k];
            for (int k = 0; k < n; k++)
            {
                if (!Has(k)) kept[k >> 6] &= ~(1UL << (k & 63));
                if (!(Segment(parent, rig, k) && Has(k) && Has(parent[k]))) { lo[k] = 1f; hi[k] = 0f; }
            }

            return new NpcBonePiece(kept, lo, hi, parent, rig);
        }

        /// <summary>
        /// This piece's part on one side of a split (<paramref name="sides"/>, 2n bytes: the decision's side of each joint, then of each
        /// segment's part; 1 positive, 0 negative, anything else neither): the joints and the parts of the side are kept, every other is
        /// taken off -- so a bone in parts apart keeps on each side the parts its decision put there.
        /// </summary>
        internal NpcBonePiece Keep(byte[] sides, byte side, int[] parent, bool[] rig)
        {
            int n = rig.Length;
            var kept = (ulong[])JointKept.Clone();
            var lo = (float[])Lo.Clone();
            var hi = (float[])Hi.Clone();
            for (int k = 0; k < n; k++)
            {
                if (!(k < sides.Length && sides[k] == side)) kept[k >> 6] &= ~(1UL << (k & 63));
                if (!(n + k < sides.Length && sides[n + k] == side)) { lo[k] = 1f; hi[k] = 0f; }
            }

            return new NpcBonePiece(kept, lo, hi, parent, rig);
        }
    }

    /// <summary>
    /// The world's record of which fragments are an NPC's and what the bone-aware split reads of each (NPC-BONE-SPLIT-1): the
    /// side data of the cut DAG (<see cref="ICutGeometrySideData"/>), so that it follows the geometry exactly -- a fragment's
    /// record goes to its two sides when its cut's geometry is committed (with the bone-tree connections the plane took off,
    /// and where the new vertices came from), to its two fragments when a split replaces it, and away when it is forgotten.
    /// Main thread only.
    /// </summary>
    public sealed class NpcBoneSplitRegistry : ICutGeometrySideData, IDisposable
    {
        private readonly LogicalCutLedger _ledger;
        private readonly Dictionary<LogicalFragmentId, NpcBoneFragment> _of = new Dictionary<LogicalFragmentId, NpcBoneFragment>();
        private int _sweepAt = 64;

        public NpcBoneSplitRegistry(LogicalCutLedger ledger)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        /// <summary>How many lineages were begun (an NPC's first cut each), how many of them could not be decided from the start.</summary>
        public int Roots { get; private set; }
        public int RootsInsufficient { get; private set; }

        /// <summary>Commits followed; those of a decidable lineage whose new vertices came without origins; origins recorded (vertices).</summary>
        public int Commits { get; private set; }
        public int CommitsWithoutOrigins { get; private set; }
        public long OriginsRecorded { get; private set; }

        /// <summary>Splits followed, fragments forgotten.</summary>
        public int Splits { get; private set; }
        public int Forgets { get; private set; }

        /// <summary>The fragments recorded now.</summary>
        public int Count => _of.Count;

        /// <summary>Whether <paramref name="fragment"/> is an NPC's (its lineage began at a prepared character's cut).</summary>
        public bool Knows(LogicalFragmentId fragment) => _of.ContainsKey(fragment);

        internal bool TryGet(LogicalFragmentId fragment, out NpcBoneFragment record) => _of.TryGetValue(fragment, out record);

        /// <summary>
        /// The root of a lineage: <paramref name="fragment"/>, whose base geometry is <paramref name="lineage"/>'s, with no
        /// connection taken off yet. The lineage's first hold is this record's.
        /// </summary>
        internal void RegisterRoot(LogicalFragmentId fragment, NpcBoneLineage lineage)
        {
            if (!fragment.IsSet || lineage == null) return;
            if (_of.Count >= _sweepAt) Sweep();
            Remove(fragment);
            lineage.Hold();
            _of[fragment] = new NpcBoneFragment(lineage, NpcBonePiece.Whole(lineage.Parent, lineage.Present));
            Roots++;
            if (lineage.Insufficient != null) RootsInsufficient++;
        }

        public bool WantsVertexOrigins(LogicalFragmentId source) =>
            _of.TryGetValue(source, out NpcBoneFragment record) && record.Lineage.Insufficient == null;

        public void Committed(in CutGeometryCommit commit, float4 geometryPlane, NativeArray<uint2> origins, uint originsBase, int originCount)
        {
            if (!_of.TryGetValue(commit.source, out NpcBoneFragment source))
            {
                if (origins.IsCreated) origins.Dispose();
                return;
            }

            _of.Remove(commit.source);
            NpcBoneLineage lineage = source.Lineage;
            Commits++;
            if (origins.IsCreated)
            {
                OriginsRecorded += Math.Max(0, originCount);
                lineage.AddChunk(origins, originsBase, originCount);   // disposes the given array
            }
            else if (lineage.Insufficient == null)
            {
                CommitsWithoutOrigins++;   // its new vertices are of no known origin: a decision over them refuses
            }

            // Each side's part of the source's rig, cut by the plane (the lineage's joints are in the geometry's frame, as the plane).
            bool cuttable = lineage.Insufficient == null && lineage.Joints != null && lineage.Joints.Length >= lineage.NodeCount;
            NpcBonePiece up = cuttable ? source.Piece.Cut(geometryPlane, true, lineage.Parent, lineage.Present, lineage.Joints) : source.Piece;
            NpcBonePiece down = cuttable ? source.Piece.Cut(geometryPlane, false, lineage.Parent, lineage.Present, lineage.Joints) : source.Piece;
            Add(commit.positiveFragment, lineage, up);
            Add(commit.negativeFragment, lineage, down);
            lineage.Release();   // the source's hold
        }

        /// <summary>
        /// The bone-aware decision's side of each bone for the split about to be published of <paramref name="source"/> (the main thread,
        /// right before the geometry is handed on): its <see cref="Split"/> keeps each side's bones by it. Without it a split hands the
        /// source's piece to both (no bone-aware split publishes without it).
        /// </summary>
        internal void NoteSplitSides(LogicalFragmentId source, byte[] boneSides)
        {
            if (source.IsSet && boneSides != null) _splitSides[source] = boneSides;
        }

        private readonly Dictionary<LogicalFragmentId, byte[]> _splitSides = new Dictionary<LogicalFragmentId, byte[]>();

        /// <summary>
        /// NPC-BONE-FALLBACK-1 review 1: the split about to be published of <paramref name="source"/> was made by the method as before
        /// along <paramref name="geometryPlane"/> (the geometry's frame, the joints'; its positive side the positive fragment): its
        /// <see cref="Split"/> cuts the source's piece by it for each side, as a commit does.
        /// </summary>
        internal void NoteSplitPlane(LogicalFragmentId source, float4 geometryPlane)
        {
            if (source.IsSet) _splitPlanes[source] = geometryPlane;
        }

        /// <summary>
        /// NPC-BONE-FALLBACK-1 review 1: the split about to be published of <paramref name="source"/> was made by the method as before
        /// with no plane (the bent partition, the graph): its <see cref="Split"/> keeps on each side the bones with a convex there.
        /// </summary>
        internal void NoteSplitConvexBones(LogicalFragmentId source, bool[] positiveBones, bool[] negativeBones)
        {
            if (source.IsSet && positiveBones != null && negativeBones != null) _splitConvexBones[source] = (positiveBones, negativeBones);
        }

        private readonly Dictionary<LogicalFragmentId, float4> _splitPlanes = new Dictionary<LogicalFragmentId, float4>();
        private readonly Dictionary<LogicalFragmentId, (bool[] positive, bool[] negative)> _splitConvexBones = new Dictionary<LogicalFragmentId, (bool[], bool[])>();

        public void Split(LogicalFragmentId source, LogicalFragmentId positive, LogicalFragmentId negative)
        {
            bool sided = _splitSides.TryGetValue(source, out byte[] sides);
            bool planed = _splitPlanes.TryGetValue(source, out float4 plane);
            bool byConvexes = _splitConvexBones.TryGetValue(source, out (bool[] positive, bool[] negative) convexBones);
            _splitSides.Remove(source);
            _splitPlanes.Remove(source);
            _splitConvexBones.Remove(source);
            if (!_of.TryGetValue(source, out NpcBoneFragment record)) return;
            _of.Remove(source);
            Splits++;
            NpcBoneLineage lineage = record.Lineage;
            NpcBonePiece up = record.Piece, down = record.Piece;
            if (sided)
            {
                up = record.Piece.Keep(sides, 1, lineage.Parent, lineage.Present);
                down = record.Piece.Keep(sides, 0, lineage.Parent, lineage.Present);
            }
            else if (planed && lineage.Insufficient == null && lineage.Joints != null && lineage.Joints.Length >= lineage.NodeCount)
            {
                up = record.Piece.Cut(plane, true, lineage.Parent, lineage.Present, lineage.Joints);
                down = record.Piece.Cut(plane, false, lineage.Parent, lineage.Present, lineage.Joints);
                SplitsByPlane++;
            }
            else if (byConvexes)
            {
                up = record.Piece.KeepBones(convexBones.positive, lineage.Parent, lineage.Present);
                down = record.Piece.KeepBones(convexBones.negative, lineage.Parent, lineage.Present);
                SplitsByConvexes++;
            }
            else
            {
                SplitsWithoutSides++;
            }

            Add(positive, lineage, up);
            Add(negative, lineage, down);
            lineage.Release();
        }

        /// <summary>Splits followed without the decision's sides, a plane or the convexes' bones (each side kept the source's whole piece).</summary>
        public int SplitsWithoutSides { get; private set; }

        /// <summary>NPC-BONE-FALLBACK-1 review 1: fallback splits followed by their plane, and by the convexes' bones.</summary>
        public int SplitsByPlane { get; private set; }
        public int SplitsByConvexes { get; private set; }

        public void Forgotten(LogicalFragmentId fragment)
        {
            _splitSides.Remove(fragment);
            _splitPlanes.Remove(fragment);
            _splitConvexBones.Remove(fragment);
            if (Remove(fragment)) Forgets++;
        }

        private void Add(LogicalFragmentId fragment, NpcBoneLineage lineage, NpcBonePiece piece)
        {
            if (!fragment.IsSet) return;
            Remove(fragment);
            lineage.Hold();
            _of[fragment] = new NpcBoneFragment(lineage, piece);
        }

        private bool Remove(LogicalFragmentId fragment)
        {
            if (!_of.TryGetValue(fragment, out NpcBoneFragment record)) return false;
            _of.Remove(fragment);
            record.Lineage.Release();
            return true;
        }

        /// <summary>Lets go of the records of fragments the ledger retired without their geometry being forgotten here.</summary>
        private void Sweep()
        {
            List<LogicalFragmentId> gone = null;
            foreach (KeyValuePair<LogicalFragmentId, NpcBoneFragment> entry in _of)
            {
                if (!_ledger.TryGetFragmentState(entry.Key, out LogicalFragmentState state) || state == LogicalFragmentState.Retired)
                {
                    (gone ??= new List<LogicalFragmentId>()).Add(entry.Key);
                }
            }

            if (gone != null)
            {
                foreach (LogicalFragmentId fragment in gone) Remove(fragment);
            }

            _sweepAt = Math.Max(64, 2 * _of.Count);
        }

        /// <summary>The record line (the world's end and the city walk's summary).</summary>
        public string Describe()
        {
            var lineages = new HashSet<NpcBoneLineage>();
            long originBytes = 0;
            foreach (NpcBoneFragment record in _of.Values)
            {
                if (lineages.Add(record.Lineage)) originBytes += record.Lineage.OriginBytes;
            }

            return "NPC bone records: fragments " + _of.Count + " of " + lineages.Count + " lineages (origins " + originBytes + " bytes), model weight tables "
                + NpcVertexBones.LiveCount + "; lineages begun " + Roots + " (" + RootsInsufficient + " undecidable from the start), commits followed " + Commits
                + " (" + CommitsWithoutOrigins + " without origins), new vertices' origins recorded " + OriginsRecorded + ", splits followed " + Splits
                + " (" + SplitsWithoutSides + " without the decision's sides, a plane or the convexes' bones; fallbacks by their plane " + SplitsByPlane
                + ", by the convexes' bones " + SplitsByConvexes + ")"
                + ", forgotten " + Forgets;
        }

        public void Dispose()
        {
            foreach (NpcBoneFragment record in _of.Values) record.Lineage.Release();
            _of.Clear();
        }
    }
}
