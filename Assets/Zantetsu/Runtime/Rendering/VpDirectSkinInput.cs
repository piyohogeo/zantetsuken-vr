using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Opt-in synchronous current-pose producer for a cold-validated, immutable, single-submesh 4-weight mesh: the CPU
    /// bake of the cut's display input (DESIGN 4.5.12). It reads the model's shared 24-byte input
    /// (<see cref="VpSkinModel"/>: bind positions, oct normals, quantized weights -- the very bytes the skin draw's
    /// vertex stage reads), skins it on the CPU by a captured pose, and writes 16-byte geometry into the storage. The
    /// renderer frame must be rigid/unit-scale (use VpFixedScaleSkinInput for fixed import scale).
    /// The caller must Dispose/recreate before changing mesh contents, bone bindings, or topology. No hot hash scan.
    /// Source rig/mesh are borrowed; a model made here is owned, a model given is borrowed. Dispose before unloading the rig.
    /// Does not switch renderers, register bodies, upload, or publish Provisional Physics. Main thread only.
    /// <para>
    /// **What a worker will be given later (DESIGN 4.5.12, the GPU bake's stage).** The three things are apart already:
    /// the immutable model input, the pose captured once by <see cref="TryCapturePose"/> into this input's own
    /// matrices, and the renderer and bone Transforms, which only the capture reads. The kernel reads the model and the
    /// matrices alone -- no live Transform, no palette being written.
    /// </para>
    /// </summary>
    public sealed unsafe class VpDirectSkinInput : IDisposable
    {
        readonly SkinnedMeshRenderer renderer;
        readonly Mesh mesh;
        readonly Transform[] bones;
        readonly VpSkinModel model;
        readonly bool ownsModel;

        // One Persistent block owns the pose and the kernel's results; the regions below are borrowed views into it,
        // valid exactly as long as the block, and nothing else frees them. Every byte a read reaches is written first:
        // the matrices by TryCapturePose for every bone before Write reads any, bounds and valid by the kernel before
        // they are read. So the block is taken uninitialized.
        NativeArray<byte> block;
        float4x4* matrices;
        float3* bounds;
        int* valid;
        bool disposed;

        /// <summary>Tests only. Negative: the block as it comes, which is the product. 0-255: every byte is set to this.</summary>
        internal static int Fill = -1;

        /// <summary>Where each region starts in the block, and the block's size, all in bytes.</summary>
        internal readonly struct Layout
        {
            public readonly long matrices, bounds, valid, bytes;

            internal Layout(long matrices, long bounds, long valid, long bytes)
            {
                this.matrices = matrices; this.bounds = bounds; this.valid = valid; this.bytes = bytes;
            }
        }

        static long Align16(long bytes) => (bytes + 15) & ~15L;

        /// <summary>
        /// The regions for this many palette bones, each starting on 16 bytes, widest element first. False when the
        /// count is negative or the block would not fit one NativeArray.
        /// </summary>
        internal static bool TryLayout(int boneCount, out Layout layout)
        {
            layout = default;
            if (boneCount < 0) return false;
            long at = 0;
            long m = at; at += Align16((long)boneCount * sizeof(float4x4));
            long b = at; at += Align16(2L * sizeof(float3));
            long v = at; at += Align16(sizeof(int));
            if (at > int.MaxValue) return false;
            layout = new Layout(m, b, v, at);
            return true;
        }

        /// <summary>The block's size in bytes while it is held, else 0. Diagnostic and tests.</summary>
        internal int NativeBytes => block.IsCreated ? block.Length : 0;
        public int VertexCount => model.VertexCount;
        public int IndexCount => model.IndexCount;
        public int TopologyCount => model.TopologyCount;
        internal bool IsAlive => !disposed && !model.IsDisposed;

        /// <summary>The shared input this producer skins (the model it was made with, or made from the renderer).</summary>
        public VpSkinModel Model => model;

        // ----- lent by an owner to one prepared cut at a time -----------------------------------------------------------
        // An owner that keeps this input for the life of one character (a crowd's prepared slot) lends it to each prepared
        // cut it makes for that character in turn, and disposes it once, at its own end. A pose taken for one borrower is
        // never used by the next: lending and returning both forget it, so the next append needs a new capture.

        /// <summary>The prepared cut this input is lent to now, or null.</summary>
        public object Borrower { get; private set; }

        /// <summary>The renderer and mesh this input was made from (an owner compares them before lending again).</summary>
        public SkinnedMeshRenderer SourceRenderer => renderer;

        public Mesh SourceMesh => mesh;

        /// <summary>Whether a pose is taken and not yet appended.</summary>
        public bool HasCapturedPose => captured;

        /// <summary>The pose <see cref="TryCapturePose"/> took (one matrix a palette bone), for the GPU-first snapshot (DESIGN 4.5.15); valid while a pose is taken.</summary>
        internal float4x4* CapturedMatrices => matrices;

        /// <summary>How many inputs were made in this process (measurement and tests).</summary>
        public static int CreatedCount { get; private set; }

        /// <summary>Lends this input to <paramref name="borrower"/>, forgetting any pose taken before. False while it is lent or disposed.</summary>
        public bool TryLend(object borrower)
        {
            if (disposed || borrower == null || Borrower != null) return false;
            Borrower = borrower;
            captured = false;
            return true;
        }

        /// <summary>
        /// Takes this input back from <paramref name="borrower"/>, forgetting its pose; when its owner has ended in the
        /// meantime (<see cref="DisposeWhenReturned"/>), frees it now, once. Nothing when another holds it.
        /// </summary>
        public void Return(object borrower)
        {
            if (borrower == null || !ReferenceEquals(Borrower, borrower)) return;
            Borrower = null;
            captured = false;
            if (disposeOnReturn) Dispose();
        }

        // The owner has ended while the input was lent: it is freed when it comes back, and by nothing else.
        bool disposeOnReturn;

        /// <summary>Whether the owner has ended and the input waits to be freed on its return.</summary>
        public bool IsDisposeRequested => disposeOnReturn && !disposed;

        /// <summary>
        /// The owner's end: frees the input now if it is not lent; if it is, leaves that to its return -- it is freed
        /// once, when the borrower gives it back, never under the borrower. True when freed now.
        /// </summary>
        public bool DisposeWhenReturned()
        {
            if (disposed) return true;
            if (Borrower == null)
            {
                Dispose();
                return true;
            }

            disposeOnReturn = true;
            return false;
        }

        static bool FourWeights(SkinnedMeshRenderer r) => r.quality == SkinQuality.Bone4
            || (r.quality == SkinQuality.Auto && QualitySettings.skinWeights == SkinWeights.FourBones);

        static bool Rigid(Transform t)
        {
            Matrix4x4 actual = t.localToWorldMatrix;
            Matrix4x4 expected = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            for (int i = 0; i < 16; i++)
                if (!float.IsFinite(actual[i]) || Mathf.Abs(actual[i] - expected[i]) > 1e-5f) return false;
            return true;
        }

        // The cold creation's stages, for measurement: each Enter ends the stage before; the end of TryCreate ends the last.
        static readonly Unity.Profiling.ProfilerMarker s_createModel = new Unity.Profiling.ProfilerMarker("Zantetsu.DirectSkin.Create.Model");
        static readonly Unity.Profiling.ProfilerMarker s_createBuild = new Unity.Profiling.ProfilerMarker("Zantetsu.DirectSkin.Create.Build");

        /// <summary>Cold only. Unsupported/invalid input returns false without changing the renderer or storage.</summary>
        public static bool TryCreate(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            out VpDirectSkinInput input)
            => TryCreate(renderer, topology, topologyCount, null, out input);

        /// <summary>
        /// The same, with the connectivity its owner has verified before (see <see cref="VpCutInputConnectivity"/>): the
        /// input gate still checks this renderer's own values, and skips only the edge and fan checks of indices and a
        /// topology identical to a verified one. The model made here is this input's own.
        /// </summary>
        public static bool TryCreate(SkinnedMeshRenderer renderer, int[] topology, int topologyCount,
            VpCutInputConnectivity verified, out VpDirectSkinInput input)
        {
            input = null;
            VpSkinModel model;
            using (s_createModel.Auto())
            {
                if (!VpSkinModel.TryCreate(renderer, topology, topologyCount, verified, out model, out _)) return false;
            }

            if (TryCreate(renderer, model, true, out input)) return true;
            model.Dispose();
            return false;
        }

        /// <summary>
        /// The same producer over a model shared with others (a crowd's slots of one model, the skin draw): the model is
        /// borrowed, and must be the renderer's mesh's. False, changing nothing, when it is not, or the renderer's frame
        /// or bones are not supported.
        /// </summary>
        public static bool TryCreate(SkinnedMeshRenderer renderer, VpSkinModel shared, out VpDirectSkinInput input)
            => TryCreate(renderer, shared, false, out input);

        static bool TryCreate(SkinnedMeshRenderer renderer, VpSkinModel model, bool owned, out VpDirectSkinInput input)
        {
            using var stage = s_createBuild.Auto();
            input = null;
            if (model == null || model.IsDisposed || renderer == null || !model.Describes(renderer)
                || !FourWeights(renderer) || !Rigid(renderer.transform)) return false;
            Transform[] sourceBones = renderer.bones;
            for (int b = 0; b < sourceBones.Length; b++)
            {
                if (model.IsBoneUsed(b) && sourceBones[b] == null) return false;
            }

            if (!TryLayout(model.BoneCount, out Layout layout)) return false;
            input = new VpDirectSkinInput(renderer, model, owned, sourceBones, layout);
            CreatedCount++;
            return true;
        }

        VpDirectSkinInput(SkinnedMeshRenderer renderer, VpSkinModel model, bool owned, Transform[] bones, Layout layout)
        {
            this.renderer = renderer; mesh = renderer.sharedMesh; this.model = model; ownsModel = owned; this.bones = bones;
            try
            {
                // A borrowed model is held until this input ends: its owner's end then frees nothing under a bake (review
                // 1, item 2). Taken first, inside the try: a failure below ends in Dispose, whose release then gives back
                // this hold and not another reader's (review 2, item 2).
                if (!owned) model.Hold();
                block = new NativeArray<byte>((int)layout.bytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                byte* at = (byte*)block.GetUnsafePtr();
                if (Fill >= 0) UnsafeUtility.MemSet(at, (byte)Fill, layout.bytes);
                matrices = (float4x4*)(at + layout.matrices);
                bounds = (float3*)(at + layout.bounds);
                valid = (int*)(at + layout.valid);
                Warm();
            }
            catch { Dispose(); throw; }
        }

        void Warm()
        {
            VpDirectSkinKernel.Skin(null, null, null, 0, null, null, bounds, valid);
            VpDirectSkinKernel.CopyIndices(null, null, 0, 0);
        }

        /// <summary>
        /// Current bone transforms -> private matrices -> reserved 16B storage, synchronously.
        /// False is no publication (unsupported changed frame/quality, invalid generated pose, or storage capacity).
        /// A source changed in place must have been invalidated by Dispose; no per-hit mesh or binding scan occurs.
        /// </summary>
        public bool TryAppendTo(VpCpuGeometryStorage storage, out VpStoredGeometry geometry)
        {
            geometry = default;
            return TryCapturePose() && TryAppendCaptured(storage, out geometry);
        }

        // Whether the matrices hold a pose taken by TryCapturePose that no append has used yet.
        bool captured;

        /// <summary>
        /// Takes the current bone transforms into this input's private matrices, renderer-local, and nothing else: the
        /// pose a later <see cref="TryAppendCapturedForDisplay"/> skins, however the bones have moved by then. False,
        /// with no pose taken, for the same reasons <see cref="TryAppendTo"/> gives for its first half.
        /// </summary>
        public bool TryCapturePose()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpDirectSkinInput));
            captured = false;
            if (model.IsDisposed || renderer == null || mesh == null || renderer.sharedMesh != mesh
                || !FourWeights(renderer) || !Rigid(renderer.transform)) return false;
            Matrix4x4 inverse = renderer.transform.worldToLocalMatrix;
            if (!VpSkinPalette.TryGather(inverse, bones, model, matrices)) return false;
            captured = true;
            return true;
        }

        private bool TryAppendCaptured(VpCpuGeometryStorage storage, out VpStoredGeometry geometry)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpDirectSkinInput));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            geometry = default;
            if (!captured) return false;
            if (!storage.TryAppendDirectSkin(this, out geometry)) return false;
            captured = false;
            return true;
        }

        /// <summary>
        /// Appends the pose <see cref="TryCapturePose"/> took, with the receipt <see cref="TryAppendForDisplay"/> gives.
        /// A refusal leaves the pose taken, so the same pose can be appended at a later opportunity.
        /// </summary>
        public bool TryAppendCapturedForDisplay(VpCpuGeometryStorage storage, out VpDirectSkinOutput output)
        {
            output = default;
            if (!TryAppendCaptured(storage, out var geometry)) return false;
            output = new VpDirectSkinOutput(this, storage, geometry);
            return true;
        }

        public bool IsDisposed => disposed;

        /// <summary>
        /// Measurement only (SKIN-BAKE-PROBE-3): the pose <see cref="TryCapturePose"/> took, baked by the product's kernel
        /// into the caller's arrays -- the 16-byte vertices and every vertex's topology, the kernel's own bounds in min/max
        /// -- without appending to a storage; the pose stays taken for another call. The kernel's verdict (every vertex
        /// finite, the bounds finite and ordered) in the return. Nothing of the bake is rewritten here: it is the call
        /// <see cref="Write"/> makes, without the index copy (<see cref="TryCopyIndices"/> makes that one).
        /// </summary>
        public bool TryBakeCaptured(NativeArray<VpRenderVertex> output, NativeArray<int> outputTopology, out float3 min, out float3 max)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpDirectSkinInput));
            min = default; max = default;
            if (!captured || !output.IsCreated || !outputTopology.IsCreated || output.Length < VertexCount || outputTopology.Length < VertexCount) return false;
            VpDirectSkinKernel.Skin(model.VertexPointer, matrices, model.TopologyPointer, VertexCount,
                (VpRenderVertex*)output.GetUnsafePtr(), (int*)outputTopology.GetUnsafePtr(), bounds, valid);
            min = bounds[0]; max = bounds[1];
            return valid[0] != 0 && math.all(math.isfinite(min)) && math.all(math.isfinite(max)) && math.all(min <= max);
        }

        /// <summary>Measurement only (SKIN-BAKE-PROBE-3): the model's indices copied with <paramref name="baseVertex"/> added, by the product's kernel; false when the output is too short.</summary>
        public bool TryCopyIndices(NativeArray<uint> outputIndices, int baseVertex)
        {
            if (disposed) throw new ObjectDisposedException(nameof(VpDirectSkinInput));
            if (!outputIndices.IsCreated || outputIndices.Length < IndexCount) return false;
            VpDirectSkinKernel.CopyIndices(model.IndexPointer, (uint*)outputIndices.GetUnsafePtr(), IndexCount, (uint)baseVertex);
            return true;
        }

        /// <summary>Same synchronous output, with an unforgeable producer/storage receipt for cold display preparation.</summary>
        public bool TryAppendForDisplay(VpCpuGeometryStorage storage, out VpDirectSkinOutput output)
        {
            output = default;
            if (!TryAppendTo(storage, out var geometry)) return false;
            output = new VpDirectSkinOutput(this, storage, geometry);
            return true;
        }

        internal bool Write(NativeArray<VpRenderVertex> output, NativeArray<int> outputTopology,
            NativeArray<uint> outputIndices, int baseVertex, out float3 min, out float3 max)
        {
            VpDirectSkinKernel.Skin(model.VertexPointer, matrices, model.TopologyPointer, VertexCount,
                (VpRenderVertex*)output.GetUnsafePtr(), (int*)outputTopology.GetUnsafePtr(), bounds, valid);
            min = bounds[0]; max = bounds[1];
            if (valid[0] == 0 || !math.all(math.isfinite(min)) || !math.all(math.isfinite(max)) || !math.all(min <= max)) return false;
            VpDirectSkinKernel.CopyIndices(model.IndexPointer, (uint*)outputIndices.GetUnsafePtr(), IndexCount, (uint)baseVertex);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            // A lent input goes back to its owner first: freeing it under a borrower would be an early release.
            if (Borrower != null) throw new InvalidOperationException("The direct skin input is still lent to a prepared cut.");
            disposed = true;
            // The views go first, so nothing can reach the block once it is given back.
            matrices = null; bounds = null; valid = null;
            if (block.IsCreated) block.Dispose();
            if (ownsModel) model.Dispose(); else model.Release();
        }
    }

    /// <summary>
    /// The palette of a model's bones for one individual: renderer-local matrices, one a palette bone, in palette order
    /// -- <c>inverse(renderer) * bone * bindPose</c>, the frame of <see cref="VpDirectSkinInput"/>'s matrices, which the
    /// skin draw's vertex stage and the CPU bake read alike. The root matrix, the bind pose and the fixed import scale
    /// (already in the prepared mesh and its bind poses) are each applied once, here, and nowhere else.
    /// </summary>
    public static unsafe class VpSkinPalette
    {
        /// <summary>
        /// Writes the palette of <paramref name="bones"/> (the renderer's own list, in palette order) into
        /// <paramref name="into"/> (at least the model's bone count): a bone no vertex uses takes the identity and is not
        /// read. False, with nothing to be trusted in <paramref name="into"/>, for a used bone that is missing or a
        /// matrix that is not finite.
        /// </summary>
        public static bool TryGather(Matrix4x4 worldToRenderer, Transform[] bones, VpSkinModel model, float4x4* into)
        {
            if (bones == null || model == null || model.IsDisposed || bones.Length < model.BoneCount) return false;
            for (int i = 0; i < model.BoneCount; i++)
            {
                if (!model.IsBoneUsed(i))
                {
                    into[i] = float4x4.identity;
                    continue;
                }

                Transform bone = bones[i];
                if (bone == null) return false;
                Matrix4x4 m = worldToRenderer * bone.localToWorldMatrix * model.BindPose(i);
                var value = new float4x4(m.GetColumn(0), m.GetColumn(1), m.GetColumn(2), m.GetColumn(3));
                if (!math.all(math.isfinite(value.c0)) || !math.all(math.isfinite(value.c1))
                    || !math.all(math.isfinite(value.c2)) || !math.all(math.isfinite(value.c3))) return false;
                into[i] = value;
            }

            return true;
        }

        /// <summary>The same, into a managed array (tests, checks).</summary>
        public static bool TryGather(Matrix4x4 worldToRenderer, Transform[] bones, VpSkinModel model, Matrix4x4[] into)
        {
            if (into == null || into.Length < model.BoneCount) return false;
            var temporary = new NativeArray<float4x4>(model.BoneCount, Allocator.Temp);
            try
            {
                if (!TryGather(worldToRenderer, bones, model, (float4x4*)temporary.GetUnsafePtr())) return false;
                for (int i = 0; i < model.BoneCount; i++)
                {
                    float4x4 v = temporary[i];
                    into[i] = new Matrix4x4(v.c0, v.c1, v.c2, v.c3);
                }

                return true;
            }
            finally
            {
                temporary.Dispose();
            }
        }
    }

    [BurstCompile]
    internal static class VpDirectSkinKernel
    {
        /// <summary>
        /// The CPU bake: the model's 24-byte vertices (bind position, oct normal, quantized weights) skinned by the
        /// palette <paramref name="matrices"/> -- four terms, in influence order, every term added whatever its weight,
        /// as the skin draw's vertex stage adds them -- into 16-byte vertices. The normal is decoded from its octahedral
        /// bytes, skinned, and encoded again; the UV bytes are copied.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static unsafe void Skin(VpSkinVertex* input, float4x4* matrices, int* topology, int count,
            VpRenderVertex* output, int* outputTopology, float3* bounds, int* valid)
        {
            float3 min = new float3(float.PositiveInfinity), max = new float3(float.NegativeInfinity);
            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                VpSkinVertex v = input[i];
                float4 p = new float4(v.position, 1);
                float3 n = DecodeNormal(v.normalX, v.normalY);
                float4 w = new float4(v.weight0, v.weight1, v.weight2, v.weight3) / 255f;
                float4x4 m0 = matrices[v.bone0], m1 = matrices[v.bone1], m2 = matrices[v.bone2], m3 = matrices[v.bone3];
                float3 position = (math.mul(m0, p) * w.x + math.mul(m1, p) * w.y + math.mul(m2, p) * w.z + math.mul(m3, p) * w.w).xyz;
                float3 normal = math.mul((float3x3)m0, n) * w.x + math.mul((float3x3)m1, n) * w.y
                    + math.mul((float3x3)m2, n) * w.z + math.mul((float3x3)m3, n) * w.w;
                float sum = math.csum(math.abs(normal));
                bool finite = math.all(math.isfinite(position)) && math.all(math.isfinite(normal)) && math.isfinite(sum) && sum > 0;
                ok &= finite;
                float2 e = normal.xy / (finite ? sum : 1f);
                float2 folded = (1 - math.abs(e.yx)) * math.select(new float2(-1), new float2(1), e >= 0);
                e = math.select(folded, e, normal.z >= 0);
                int2 oct = (int2)math.round(math.clamp(e, -1, 1) * 127);
#if VP_DIAGNOSTIC_LEGACY32
                // Keeps the old comparison build compilable; TryCreate refuses its 32B stride.
                output[i] = new VpRenderVertex { position = position, normal = normal, uv0 = new Vector2((v.u + .5f) / 256f, (v.v + .5f) / 256f) };
#else
                output[i] = new VpRenderVertex { position = position, normalX = (sbyte)oct.x, normalY = (sbyte)oct.y, u = v.u, v = v.v };
#endif
                outputTopology[i] = topology[i]; min = math.min(min, position); max = math.max(max, position);
            }
            bounds[0] = min; bounds[1] = max; valid[0] = ok ? 1 : 0;
        }

        // The octahedral decoding of VpCompactVertex.hlsl / VpRenderVertex.normal, in Burst.
        static float3 DecodeNormal(sbyte x, sbyte y)
        {
            float2 e = new float2(x, y) / 127f;
            float3 n = new float3(e, 1f - math.abs(e.x) - math.abs(e.y));
            if (n.z < 0f) n.xy = (1f - math.abs(n.yx)) * math.select(new float2(-1f), new float2(1f), e >= 0f);
            return math.normalize(n);
        }

        [BurstCompile(CompileSynchronously = true)]
        public static unsafe void CopyIndices(uint* source, uint* output, int count, uint baseVertex)
        { for (int i = 0; i < count; i++) output[i] = source[i] + baseVertex; }
    }
}
