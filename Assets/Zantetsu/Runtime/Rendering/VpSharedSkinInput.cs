using System;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The shared input of one skinned mesh for the GPU bake and the deferred CPU bake (DESIGN 4.5.15): the drawn
    /// SkinnedMeshRenderer's own <see cref="UnityEngine.Mesh"/> GPU buffers, read where they are after
    /// <see cref="GraphicsBuffer.Target.Raw"/> is added to the mesh's vertex buffer target once -- its vertex streams
    /// (<see cref="Mesh.GetVertexBuffer"/>) and its bone weights (<see cref="Mesh.GetBoneWeightBuffer"/>) -- and the
    /// same attributes read once into native memory for the CPU side: float positions, normals, UV0, and each vertex's
    /// effective weights (<see cref="Mesh.GetAllBoneWeights"/>) in buffer order. No 24-byte input is made for the GPU;
    /// the two sides begin from the same numbers. Made once a mesh, at the cold preparation (never at a hit), owned by
    /// the <see cref="VpSkinModel"/> of the mesh and freed with it.
    /// <para>
    /// **What is accepted** is what SKIN-BAKE-PROBE-1..3 verified with real data: one triangle submesh beginning at 0,
    /// positions and normals as Float32 of 3 (normals also 4) components, UV0 absent or Float32 of 2, the FourBones
    /// weight layout (32 bytes a vertex) read Raw (Direct3D 12) or as 32-byte structured elements (Vulkan), 1 to 4
    /// influences a vertex. Anything else is refused with the reason, and the cut takes the synchronous CPU route.
    /// </para>
    /// </summary>
    public sealed class VpSharedSkinInput : IDisposable
    {
        /// <summary>The kernel's reading of the bone weight buffer: Raw where the buffer has that target, else 32-byte structured elements.</summary>
        public enum WeightReadingKind { Raw, Structured32 }

        /// <summary>Where and how an attribute lies in the vertex streams.</summary>
        public struct Attribute
        {
            public bool present;
            public int stream, offset, dimension;
            public VertexAttributeFormat format;
            public override string ToString() => present ? "stream " + stream + " offset " + offset + " " + format + "x" + dimension : "absent";
        }

        public Mesh Mesh { get; private set; }
        public int VertexCount { get; }
        public int IndexCount { get; }
        public IndexFormat IndexFormat { get; }
        public Attribute Position { get; }
        public Attribute Normal { get; }
        public Attribute Uv { get; }
        public bool HasUv => Uv.present;
        /// <summary>The vertex streams holding the attributes read (null where no attribute lies), and every stream's stride in bytes.</summary>
        public GraphicsBuffer[] Streams { get; }
        public int[] Strides { get; }
        public GraphicsBuffer Weights { get; }
        public WeightReadingKind WeightReading { get; }
        public GraphicsBuffer.Target WeightTarget { get; }
        public int MaxInfluences { get; }
        public GraphicsBuffer.Target VertexTargetBefore { get; }
        public GraphicsBuffer.Target VertexTargetAfter { get; }
        /// <summary>Seconds the preparation took (the target, the handles, the layout, the CPU read).</summary>
        public double PrepareSeconds { get; private set; }
        public long NativeBytes { get; private set; }
        public bool IsDisposed { get; private set; }

        // The CPU side, in native memory for the worker's kernel: read once, never written again.
        internal NativeArray<float3> positions, normals;
        internal NativeArray<float2> uv;
        internal NativeArray<int> weightStart, weightCount, bones;
        internal NativeArray<float> weights;

        private VpSharedSkinInput(Mesh mesh, Attribute position, Attribute normal, Attribute uv, GraphicsBuffer[] streams, int[] strides,
            GraphicsBuffer weightsBuffer, WeightReadingKind weightReading, int maxInfluences, int indexCount, IndexFormat indexFormat,
            GraphicsBuffer.Target before, GraphicsBuffer.Target after)
        {
            Mesh = mesh; VertexCount = mesh.vertexCount; IndexCount = indexCount; IndexFormat = indexFormat;
            Position = position; Normal = normal; Uv = uv; Streams = streams; Strides = strides;
            Weights = weightsBuffer; WeightReading = weightReading; WeightTarget = weightsBuffer != null ? weightsBuffer.target : 0; MaxInfluences = maxInfluences;
            VertexTargetBefore = before; VertexTargetAfter = after;
        }

        /// <summary>
        /// Chooses the reading from the weight buffer's target and stride: Raw where the target has Raw; else, with the
        /// Structured target and a stride of 32, the structured reading; else refused with the target and stride named.
        /// </summary>
        public static bool TryChooseWeightReading(GraphicsBuffer.Target target, int bufferStride, out WeightReadingKind reading, out string refusal)
        {
            reading = WeightReadingKind.Raw;
            refusal = null;
            if ((target & GraphicsBuffer.Target.Raw) != 0) return true;
            if ((target & GraphicsBuffer.Target.Structured) == 0) { refusal = "the bone weight buffer has neither the Raw nor the Structured target (" + target + ")"; return false; }
            if (bufferStride != 32) { refusal = "the bone weight buffer's structured stride " + bufferStride + " is not 32 (target " + target + ")"; return false; }
            reading = WeightReadingKind.Structured32;
            return true;
        }

        /// <summary>
        /// Prepares <paramref name="mesh"/> once: Raw added to its vertex buffer target, the buffers taken after that,
        /// the layout read and checked, the CPU attributes read into native memory. False with the reason, changing
        /// nothing but the target, when the mesh is outside the accepted conditions.
        /// </summary>
        public static bool TryPrepare(Mesh mesh, out VpSharedSkinInput input, out string refusal)
        {
            input = null;
            refusal = null;
            if (mesh == null) { refusal = "no mesh"; return false; }
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!mesh.isReadable) { refusal = "the mesh '" + mesh.name + "' is not readable"; return false; }
            if (mesh.subMeshCount != 1) { refusal = "the mesh has " + mesh.subMeshCount + " submeshes (one is accepted)"; return false; }
            SubMeshDescriptor first = mesh.GetSubMesh(0);
            if (first.baseVertex != 0 || first.indexStart != 0) { refusal = "submesh 0 has baseVertex " + first.baseVertex + " and indexStart " + first.indexStart + " (a submesh beginning at 0 is accepted)"; return false; }
            if (mesh.GetTopology(0) != MeshTopology.Triangles) { refusal = "submesh 0 is " + mesh.GetTopology(0) + ", not triangles"; return false; }
            Attribute position = Read(mesh, VertexAttribute.Position), normal = Read(mesh, VertexAttribute.Normal), uv = Read(mesh, VertexAttribute.TexCoord0);
            if (!position.present) { refusal = "the mesh has no positions"; return false; }
            if (!normal.present) { refusal = "the mesh has no normals"; return false; }
            if (!Float32(position, 3, 3, "position", out refusal) || !Float32(normal, 3, 4, "normal", out refusal) || (uv.present && !Float32(uv, 2, 2, "uv0", out refusal))) return false;
            if (mesh.indexFormat != IndexFormat.UInt16 && mesh.indexFormat != IndexFormat.UInt32) { refusal = "index format " + mesh.indexFormat; return false; }
            if (mesh.skinWeightBufferLayout != SkinWeights.FourBones) { refusal = "the weight layout is " + mesh.skinWeightBufferLayout + " (FourBones is accepted)"; return false; }

            GraphicsBuffer.Target before = mesh.vertexBufferTarget;
            if ((before & GraphicsBuffer.Target.Raw) == 0) mesh.vertexBufferTarget = before | GraphicsBuffer.Target.Raw;
            GraphicsBuffer.Target after = mesh.vertexBufferTarget;
            int streamCount = mesh.vertexBufferCount;
            var streams = new GraphicsBuffer[streamCount];
            var strides = new int[streamCount];
            GraphicsBuffer weights = null;
            VpSharedSkinInput made = null;
            try
            {
                for (int s = 0; s < streamCount; s++)
                {
                    strides[s] = mesh.GetVertexBufferStride(s);
                    bool used = position.stream == s || normal.stream == s || (uv.present && uv.stream == s);
                    if (!used) continue;
                    streams[s] = mesh.GetVertexBuffer(s);
                    if (streams[s] == null) { refusal = "the mesh gave no vertex buffer for stream " + s; return false; }
                    if ((streams[s].target & GraphicsBuffer.Target.Raw) == 0) { refusal = "stream " + s + "'s buffer has no Raw target (" + streams[s].target + ")"; return false; }
                }

                weights = mesh.GetBoneWeightBuffer(SkinWeights.FourBones);
                if (weights == null) { refusal = "the mesh gave no bone weight buffer for the FourBones layout"; return false; }
                if (!TryChooseWeightReading(weights.target, weights.stride, out WeightReadingKind reading, out refusal)) return false;
                int indexCount = (int)mesh.GetIndexCount(0);
                int maxInfluences;
                using (NativeArray<byte> counts = mesh.GetBonesPerVertex())
                {
                    if (counts.Length != mesh.vertexCount) { refusal = "bones per vertex has " + counts.Length + " entries for " + mesh.vertexCount + " vertices"; return false; }
                    maxInfluences = 0;
                    for (int v = 0; v < counts.Length; v++)
                    {
                        if (counts[v] == 0 || counts[v] > 4) { refusal = "vertex " + v + " has " + counts[v] + " influences (1 to 4 are accepted)"; return false; }
                        maxInfluences = Math.Max(maxInfluences, counts[v]);
                    }
                }

                made = new VpSharedSkinInput(mesh, position, normal, uv, streams, strides, weights, reading, maxInfluences, indexCount, mesh.indexFormat, before, after);
                streams = null; weights = null;   // owned by the input now
                if (!made.TryReadCpu(out refusal)) { made.Dispose(); made = null; return false; }
                made.PrepareSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
                input = made;
                made = null;
                return true;
            }
            finally
            {
                if (streams != null) foreach (GraphicsBuffer b in streams) b?.Dispose();
                weights?.Dispose();
                made?.Dispose();
            }
        }

        private static Attribute Read(Mesh mesh, VertexAttribute attribute)
        {
            if (!mesh.HasVertexAttribute(attribute)) return default;
            return new Attribute
            {
                present = true, stream = mesh.GetVertexAttributeStream(attribute), offset = mesh.GetVertexAttributeOffset(attribute),
                dimension = mesh.GetVertexAttributeDimension(attribute), format = mesh.GetVertexAttributeFormat(attribute),
            };
        }

        private static bool Float32(Attribute a, int minDimension, int maxDimension, string name, out string why)
        {
            why = null;
            if (a.format != VertexAttributeFormat.Float32) { why = "the " + name + " is " + a.format + " (Float32 is accepted)"; return false; }
            if (a.dimension < minDimension || a.dimension > maxDimension) { why = "the " + name + " has " + a.dimension + " components (" + minDimension + ".." + maxDimension + " are accepted)"; return false; }
            return true;
        }

        // The CPU attributes and effective weights, read once into native memory (Persistent, uninitialized then filled).
        private bool TryReadCpu(out string refusal)
        {
            refusal = null;
            Mesh mesh = Mesh;
            int n = mesh.vertexCount;
            Vector3[] p = mesh.vertices, nrm = mesh.normals;
            Vector2[] tex = HasUv ? mesh.uv : null;
            if (p.Length != n || nrm.Length != n) { refusal = "the mesh's positions or normals are not one a vertex"; return false; }
            if (HasUv && (tex == null || tex.Length != n)) { refusal = "the mesh's UV0 is not one a vertex"; return false; }
            positions = new NativeArray<float3>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            normals = new NativeArray<float3>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            uv = new NativeArray<float2>(HasUv ? Math.Max(n, 1) : 1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            weightStart = new NativeArray<int>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            weightCount = new NativeArray<int>(Math.Max(n, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int v = 0; v < n; v++)
            {
                positions[v] = p[v];
                normals[v] = nrm[v];
                if (HasUv) uv[v] = tex[v];
            }

            using (NativeArray<byte> counts = mesh.GetBonesPerVertex())
            using (NativeArray<BoneWeight1> all = mesh.GetAllBoneWeights())
            {
                int w = all.Length;
                bones = new NativeArray<int>(Math.Max(w, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                weights = new NativeArray<float>(Math.Max(w, 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                int at = 0;
                for (int v = 0; v < n; v++)
                {
                    weightStart[v] = at; weightCount[v] = counts[v]; at += counts[v];
                }

                if (at != w) { refusal = "the bones per vertex sum to " + at + " but there are " + w + " weights"; return false; }
                for (int i = 0; i < w; i++) { bones[i] = all[i].boneIndex; weights[i] = all[i].weight; }
            }

            NativeBytes = (long)positions.Length * 12 + (long)normals.Length * 12 + (long)uv.Length * 8 + (long)weightStart.Length * 4 + (long)weightCount.Length * 4
                + (long)bones.Length * 4 + (long)weights.Length * 4;
            return true;
        }

        public string Describe()
        {
            var t = new StringBuilder();
            t.Append("mesh '").Append(Mesh != null ? Mesh.name : "?").Append("': vertices ").Append(VertexCount).Append(", indices ").Append(IndexCount).Append(' ').Append(IndexFormat)
                .Append("; streams ").Append(Streams.Length).Append(" (strides ").Append(string.Join("/", Strides)).Append(")")
                .Append("; position ").Append(Position).Append("; normal ").Append(Normal).Append("; uv0 ").Append(Uv)
                .Append("; weights FourBones (max influences ").Append(MaxInfluences).Append(") read as ").Append(WeightReading).Append(" (target ").Append(WeightTarget).Append(')')
                .Append("; vertex target ").Append(VertexTargetBefore).Append(" -> ").Append(VertexTargetAfter)
                .Append("; cpu float inputs ").Append(NativeBytes).Append(" bytes; prepared in ").Append((PrepareSeconds * 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            return t.ToString();
        }

        /// <summary>Releases the GraphicsBuffer handles (the mesh keeps its GPU data) and the native inputs. Only when no dispatch and no worker reads them.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (Streams != null) foreach (GraphicsBuffer b in Streams) b?.Dispose();
            Weights?.Dispose();
            if (positions.IsCreated) positions.Dispose();
            if (normals.IsCreated) normals.Dispose();
            if (uv.IsCreated) uv.Dispose();
            if (weightStart.IsCreated) weightStart.Dispose();
            if (weightCount.IsCreated) weightCount.Dispose();
            if (bones.IsCreated) bones.Dispose();
            if (weights.IsCreated) weights.Dispose();
            Mesh = null;
        }
    }
}
