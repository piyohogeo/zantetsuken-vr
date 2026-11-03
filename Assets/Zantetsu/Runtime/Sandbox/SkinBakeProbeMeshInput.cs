using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The GPU-side input of one mesh for the bake probe (DESIGN 4.5.14): the drawn SkinnedMeshRenderer's own
    /// <see cref="UnityEngine.Mesh"/> buffers, shared and read where they are -- its vertex streams
    /// (<see cref="Mesh.GetVertexBuffer"/>), its bone weights (<see cref="Mesh.GetBoneWeightBuffer"/>) and its indices
    /// (<see cref="Mesh.GetIndexBuffer"/>) -- after <see cref="GraphicsBuffer.Target.Raw"/> is added to the mesh's
    /// buffer targets once, at the preparation. No vertex data is copied into a buffer of this probe's. The layout is
    /// read from the mesh (stream, offset, format, dimension of each attribute; the weight layout; the index format),
    /// never assumed. One input serves every individual of the mesh (<see cref="Hold"/> / <see cref="Release"/>); the
    /// GraphicsBuffer objects are released only when the last user has let go and no dispatch reads them any more. The
    /// buffers are read only here.
    /// <para>
    /// Supported, for this probe: positions and normals as Float32 or Float16 of 3 (or 4) components, UV0 as Float32 or
    /// Float16 of 2 (or absent), weights in any of Unity's four layouts, 16- or 32-bit indices of a triangle list.
    /// Anything else is refused with the reason.
    /// </para>
    /// </summary>
    public sealed class SkinBakeProbeMeshInput : IDisposable
    {
        /// <summary>The kernel's reading of the bone weight buffer. Raw needs the Raw target; the structured readings need the Structured target and that element size.</summary>
        public enum WeightReadingKind { Raw, Structured8, Structured16, Structured32 }

        /// <summary>
        /// Chooses the reading from the weight buffer's target and stride: Raw where the target has Raw; else, with the
        /// Structured target, the structured element equal to the buffer's stride, which must be the layout's (8, 16 or 32);
        /// else refused with the target and strides named. Nothing is assumed about either.
        /// </summary>
        public static bool TryChooseWeightReading(GraphicsBuffer.Target target, int bufferStride, int layoutStride, out WeightReadingKind reading, out string refusal)
        {
            reading = WeightReadingKind.Raw;
            refusal = null;
            if ((target & GraphicsBuffer.Target.Raw) != 0) return true;
            if ((target & GraphicsBuffer.Target.Structured) == 0) { refusal = "the bone weight buffer has neither the Raw nor the Structured target (" + target + ")"; return false; }
            if (bufferStride != layoutStride) { refusal = "the bone weight buffer's stride " + bufferStride + " is not the layout's " + layoutStride + " (target " + target + ")"; return false; }
            switch (bufferStride)
            {
                case 8: reading = WeightReadingKind.Structured8; return true;
                case 16: reading = WeightReadingKind.Structured16; return true;
                case 32: reading = WeightReadingKind.Structured32; return true;
                default: refusal = "the bone weight buffer's stride " + bufferStride + " has no structured reading here (8, 16 and 32 have; target " + target + ")"; return false;
            }
        }

        /// <summary>Where and how an attribute lies in the vertex streams.</summary>
        public struct Attribute
        {
            public bool present;
            public int stream, offset, dimension;
            public VertexAttributeFormat format;
            /// <summary>The kernel's format code: 0 Float32, 1 Float16.</summary>
            public int Code => format == VertexAttributeFormat.Float16 ? 1 : 0;
            public override string ToString() => present ? "stream " + stream + " offset " + offset + " " + format + "x" + dimension : "absent";
        }

        public Mesh Mesh { get; private set; }
        public int VertexCount { get; }
        public int IndexCount { get; }
        public IndexFormat IndexFormat { get; }
        public int SubmeshCount { get; }
        public Attribute Position { get; }
        public Attribute Normal { get; }
        public Attribute Uv { get; }
        /// <summary>The vertex streams holding the attributes read (null where no attribute lies), and every stream's stride in bytes.</summary>
        public GraphicsBuffer[] Streams { get; }
        public int[] Strides { get; }
        public SkinWeights WeightLayout { get; }
        public GraphicsBuffer Weights { get; }
        /// <summary>How the kernel reads <see cref="Weights"/>: by the buffer's own target (Raw where it has it, else a structured element of its stride).</summary>
        public WeightReadingKind WeightReading { get; }
        /// <summary>The bone weight buffer's target as the mesh gave it (not changed by this probe).</summary>
        public GraphicsBuffer.Target WeightTarget { get; }
        /// <summary>Bytes a vertex (fixed layouts) or an entry (Unlimited: 8, a weight and a bone) in <see cref="Weights"/>.</summary>
        public int WeightStride { get; }
        /// <summary>Unlimited layout only: the start and count of each vertex's entries, made here from the mesh's bones-per-vertex (the one allocation of this input).</summary>
        public GraphicsBuffer WeightRanges { get; }
        public int MaxInfluences { get; }
        public GraphicsBuffer Indices { get; }
        /// <summary>The buffer targets the mesh had before and after Raw was added.</summary>
        public GraphicsBuffer.Target VertexTargetBefore { get; }
        public GraphicsBuffer.Target VertexTargetAfter { get; }
        public GraphicsBuffer.Target IndexTargetBefore { get; }
        public GraphicsBuffer.Target IndexTargetAfter { get; }
        /// <summary>Seconds the preparation took (the targets, the handles, the layout reading).</summary>
        public double PrepareSeconds { get; }
        public long ExtraBytesAllocated { get; }
        public int Users { get; private set; }
        public bool IsDisposed { get; private set; }

        private SkinBakeProbeMeshInput(Mesh mesh, Attribute position, Attribute normal, Attribute uv, GraphicsBuffer[] streams, int[] strides,
            SkinWeights weightLayout, GraphicsBuffer weights, int weightStride, WeightReadingKind weightReading, GraphicsBuffer weightRanges, int maxInfluences,
            GraphicsBuffer indices, IndexFormat indexFormat, int indexCount, int submeshCount,
            GraphicsBuffer.Target vb, GraphicsBuffer.Target va, GraphicsBuffer.Target ib, GraphicsBuffer.Target ia, double prepareSeconds, long extraBytes)
        {
            Mesh = mesh; VertexCount = mesh.vertexCount; Position = position; Normal = normal; Uv = uv; Streams = streams; Strides = strides;
            WeightLayout = weightLayout; Weights = weights; WeightStride = weightStride; WeightReading = weightReading; WeightTarget = weights != null ? weights.target : 0; WeightRanges = weightRanges; MaxInfluences = maxInfluences;
            Indices = indices; IndexFormat = indexFormat; IndexCount = indexCount; SubmeshCount = submeshCount;
            VertexTargetBefore = vb; VertexTargetAfter = va; IndexTargetBefore = ib; IndexTargetAfter = ia; PrepareSeconds = prepareSeconds; ExtraBytesAllocated = extraBytes;
        }

        /// <summary>
        /// Prepares <paramref name="mesh"/>: Raw added to its vertex and index buffer targets, the buffers taken after
        /// that, the layout read and checked. False with the reason, changing nothing but the targets, when the mesh is
        /// not supported here.
        /// </summary>
        public static bool TryPrepare(Mesh mesh, out SkinBakeProbeMeshInput input, out string refusal)
        {
            input = null;
            refusal = null;
            if (mesh == null) { refusal = "no mesh"; return false; }
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!mesh.isReadable) { refusal = "the mesh '" + mesh.name + "' is not readable: the CPU reference needs its attributes and weights"; return false; }
            if (mesh.subMeshCount < 1) { refusal = "the mesh has no submesh"; return false; }
            // One submesh beginning at 0: several submeshes, a base vertex or an index start are not handled here (refused, not assumed).
            if (mesh.subMeshCount != 1) { refusal = "the mesh has " + mesh.subMeshCount + " submeshes: this probe reads one (several are not handled)"; return false; }
            UnityEngine.Rendering.SubMeshDescriptor first = mesh.GetSubMesh(0);
            if (first.baseVertex != 0 || first.indexStart != 0) { refusal = "submesh 0 has baseVertex " + first.baseVertex + " and indexStart " + first.indexStart + ": this probe reads a submesh that begins at 0 (others are not handled)"; return false; }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) { refusal = "submesh " + s + " is " + mesh.GetTopology(s) + ", not triangles"; return false; }
            }

            Attribute position = Read(mesh, VertexAttribute.Position), normal = Read(mesh, VertexAttribute.Normal), uv = Read(mesh, VertexAttribute.TexCoord0);
            if (!position.present) { refusal = "the mesh has no positions"; return false; }
            if (!normal.present) { refusal = "the mesh has no normals"; return false; }
            if (!Supported(position, 3, 3, out string why) || !Supported(normal, 3, 4, out why) || (uv.present && !Supported(uv, 2, 2, out why))) { refusal = why; return false; }
            if (mesh.indexFormat != IndexFormat.UInt16 && mesh.indexFormat != IndexFormat.UInt32) { refusal = "index format " + mesh.indexFormat; return false; }
            SkinWeights layout = mesh.skinWeightBufferLayout;
            int weightStride;
            switch (layout)
            {
                case SkinWeights.OneBone: weightStride = 8; break;
                case SkinWeights.TwoBones: weightStride = 16; break;
                case SkinWeights.FourBones: weightStride = 32; break;
                case SkinWeights.Unlimited: weightStride = 8; break;
                default: refusal = "the mesh has no bone weights (layout " + layout + ")"; return false;
            }

            // The targets: Raw added once; the handles are taken after, so that they name the buffers as they now are.
            GraphicsBuffer.Target vb = mesh.vertexBufferTarget, ib = mesh.indexBufferTarget;
            if ((vb & GraphicsBuffer.Target.Raw) == 0) mesh.vertexBufferTarget = vb | GraphicsBuffer.Target.Raw;
            if ((ib & GraphicsBuffer.Target.Raw) == 0) mesh.indexBufferTarget = ib | GraphicsBuffer.Target.Raw;
            GraphicsBuffer.Target va = mesh.vertexBufferTarget, ia = mesh.indexBufferTarget;

            int streamCount = mesh.vertexBufferCount;
            var streams = new GraphicsBuffer[streamCount];
            var strides = new int[streamCount];
            GraphicsBuffer weights = null, indices = null, ranges = null;
            long extra = 0;
            try
            {
                for (int s = 0; s < streamCount; s++)
                {
                    strides[s] = mesh.GetVertexBufferStride(s);
                    bool used = (position.present && position.stream == s) || (normal.present && normal.stream == s) || (uv.present && uv.stream == s);
                    if (!used) continue;
                    streams[s] = mesh.GetVertexBuffer(s);
                    if (streams[s] == null) { refusal = "the mesh gave no vertex buffer for stream " + s; return false; }
                    if ((streams[s].target & GraphicsBuffer.Target.Raw) == 0) { refusal = "stream " + s + "'s buffer has no Raw target (" + streams[s].target + ")"; return false; }
                }

                weights = mesh.GetBoneWeightBuffer(layout);
                if (weights == null) { refusal = "the mesh gave no bone weight buffer for layout " + layout; return false; }
                if (!TryChooseWeightReading(weights.target, weights.stride, weightStride, out WeightReadingKind weightReading, out refusal)) return false;
                indices = mesh.GetIndexBuffer();
                if (indices == null) { refusal = "the mesh gave no index buffer"; return false; }
                if ((indices.target & GraphicsBuffer.Target.Raw) == 0) { refusal = "the index buffer has no Raw target (" + indices.target + ")"; return false; }

                int maxInfluences;
                using (NativeArray<byte> counts = mesh.GetBonesPerVertex())
                {
                    if (counts.Length != mesh.vertexCount) { refusal = "bones per vertex has " + counts.Length + " entries for " + mesh.vertexCount + " vertices"; return false; }
                    maxInfluences = 0;
                    if (layout == SkinWeights.Unlimited)
                    {
                        // The one allocation of this input: where each vertex's entries begin and how many, as the
                        // Unlimited buffer lists them in vertex order.
                        var span = new uint2[counts.Length];
                        uint at = 0;
                        for (int v = 0; v < counts.Length; v++) { span[v] = new uint2(at, counts[v]); at += counts[v]; maxInfluences = Math.Max(maxInfluences, counts[v]); }
                        ranges = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, counts.Length), 8) { name = "Skin bake probe weight ranges" };
                        ranges.SetData(span);
                        extra += (long)span.Length * 8;
                    }
                    else
                    {
                        for (int v = 0; v < counts.Length; v++) maxInfluences = Math.Max(maxInfluences, counts[v]);
                    }
                }

                int indexCount = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) indexCount += (int)mesh.GetIndexCount(s);
                double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
                input = new SkinBakeProbeMeshInput(mesh, position, normal, uv, streams, strides, layout, weights, weightStride, weightReading, ranges, maxInfluences,
                    indices, mesh.indexFormat, indexCount, mesh.subMeshCount, vb, va, ib, ia, seconds, extra);
                streams = null; weights = null; indices = null; ranges = null;   // owned by the input now
                return true;
            }
            finally
            {
                if (streams != null) foreach (GraphicsBuffer b in streams) b?.Dispose();
                weights?.Dispose(); indices?.Dispose(); ranges?.Dispose();
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

        private static bool Supported(Attribute a, int minDimension, int maxDimension, out string why)
        {
            why = null;
            if (a.format != VertexAttributeFormat.Float32 && a.format != VertexAttributeFormat.Float16) { why = "attribute format " + a.format + " is not read by this probe (Float32 and Float16 are)"; return false; }
            if (a.dimension < minDimension || a.dimension > maxDimension) { why = "attribute dimension " + a.dimension + " (" + minDimension + ".." + maxDimension + " are read)"; return false; }
            return true;
        }

        /// <summary>One more individual of this mesh uses the input.</summary>
        public void Hold() { ThrowIfDisposed(); Users++; }

        /// <summary>One individual lets go; the buffers stay until <see cref="Dispose"/>.</summary>
        public void Release() { if (Users > 0) Users--; }

        public string Describe()
        {
            var t = new StringBuilder();
            t.Append("mesh '").Append(Mesh != null ? Mesh.name : "?").Append("': vertices ").Append(VertexCount).Append(", indices ").Append(IndexCount).Append(' ').Append(IndexFormat)
                .Append(", submeshes ").Append(SubmeshCount).Append("; streams ").Append(Streams.Length).Append(" (strides ").Append(string.Join("/", Strides)).Append(")")
                .Append("; position ").Append(Position).Append("; normal ").Append(Normal).Append("; uv0 ").Append(Uv)
                .Append("; weights ").Append(WeightLayout).Append(" stride ").Append(WeightStride).Append(" (buffer count ").Append(Weights != null ? Weights.count : 0).Append(", stride ").Append(Weights != null ? Weights.stride : 0)
                .Append(", max influences ").Append(MaxInfluences).Append("); weights read as ").Append(WeightReading).Append(" (target ").Append(WeightTarget).Append(')');
            if (WeightRanges != null) t.Append("; weight ranges made (").Append(ExtraBytesAllocated).Append(" bytes)");
            t.Append("; vertex target ").Append(VertexTargetBefore).Append(" -> ").Append(VertexTargetAfter).Append(", index target ").Append(IndexTargetBefore).Append(" -> ").Append(IndexTargetAfter)
                .Append("; prepared in ").Append((PrepareSeconds * 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            return t.ToString();
        }

        private void ThrowIfDisposed() { if (IsDisposed) throw new ObjectDisposedException(nameof(SkinBakeProbeMeshInput)); }

        /// <summary>Releases the GraphicsBuffer objects (the mesh keeps its GPU data). Call it when no dispatch that read them is still outstanding.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            foreach (GraphicsBuffer b in Streams) b?.Dispose();
            Weights?.Dispose();
            Indices?.Dispose();
            WeightRanges?.Dispose();
            Mesh = null;
        }
    }
}
