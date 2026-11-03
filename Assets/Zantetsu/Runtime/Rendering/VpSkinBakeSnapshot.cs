using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One GPU-first request's snapshot (DESIGN 4.5.15): the palette taken once at the hit, the shared input and the
    /// model it skins (both held for the request's life), the room the deferred CPU bake writes into, and the GPU
    /// palette buffer the bake dispatches read. The worker reads the palette, the input and the model's topology, and
    /// writes the vertices, the topology copy and the bounds -- nothing else, and nothing that lives in the scene.
    /// Rented from <see cref="VpSkinBakeResources"/> and given back there; its arrays are reused, never freed per request.
    /// </summary>
    public sealed unsafe class VpSkinBakeSnapshot : IVpSkinBakeDispatch
    {
        internal VpSkinBakeResources owner;
        internal int paletteCapacity, vertexCapacity;
        internal NativeArray<float4x4> palette;
        internal NativeArray<VpRenderVertex> vertices;
        internal NativeArray<int> topology;
        internal NativeArray<float3> boundsOut;   // [0] min, [1] max
        internal NativeArray<int> marker;         // [0] set by the managed fallback only
        internal GraphicsBuffer paletteBuffer;
        internal bool rented;

        /// <summary>The request this snapshot is of (0 while not rented).</summary>
        public long RequestId { get; internal set; }
        public VpSkinModel Model { get; private set; }
        public VpSharedSkinInput Input { get; private set; }
        public int BoneCount { get; private set; }
        public int VertexCount { get; private set; }
        /// <summary>The conservative box of this pose (the model's bind bounds through the palette), in the renderer's frame.</summary>
        public Bounds Conservative { get; private set; }
        /// <summary>Whether the palette has been uploaded to <see cref="PaletteBuffer"/> (once a request).</summary>
        public bool PaletteUploaded { get; private set; }
        public GraphicsBuffer PaletteBuffer => paletteBuffer;

        // The kernel's results.
        public bool KernelRan { get; private set; }
        public bool BurstRan { get; private set; }
        public int Invalid { get; private set; }
        public double KernelSeconds { get; private set; }
        public int Dispatches { get; private set; }

        /// <summary>The 16-byte vertices the kernel wrote (the first <see cref="VertexCount"/> elements).</summary>
        public NativeArray<VpRenderVertex> Vertices => vertices;

        /// <summary>Tests only: the kernel throws (the worker fails) / the output is marked invalid (the resume refuses it).</summary>
        public static bool FailKernelForTest, InvalidateOutputForTest;
        public NativeArray<int> Topology => topology;

        internal void Begin(long requestId, VpSkinModel model, VpSharedSkinInput input, int boneCount)
        {
            RequestId = requestId;
            Model = model; Input = input; BoneCount = boneCount; VertexCount = input.VertexCount;
            model.Hold();
            KernelRan = BurstRan = PaletteUploaded = false; Invalid = 0; KernelSeconds = 0; Dispatches = 0;
            rented = true;
        }

        internal void End()
        {
            Model?.Release();
            Model = null; Input = null;
            RequestId = 0; rented = false;
        }

        /// <summary>
        /// Takes the pose: the renderer-local palette of <paramref name="bones"/> (<see cref="VpSkinPalette.TryGather"/>),
        /// and the conservative box from it. False, with nothing to be trusted, where a used bone is missing or not finite.
        /// </summary>
        public bool TryCapture(Matrix4x4 worldToRenderer, Transform[] bones)
        {
            if (!rented) throw new InvalidOperationException("the snapshot is not rented");
            if (!VpSkinPalette.TryGather(worldToRenderer, bones, Model, (float4x4*)palette.GetUnsafePtr())) return false;
            Conservative = VpSkinConservativeBounds.Of(Model.BindBounds, (float4x4*)palette.GetUnsafeReadOnlyPtr(), BoneCount);
            return true;
        }

        /// <summary>The bind pose (every bone the identity): for a warm of the bake that needs no character (SKIN-GPU-FIRST-STARTUP-1).</summary>
        public void CaptureIdentity()
        {
            if (!rented) throw new InvalidOperationException("the snapshot is not rented");
            var p = (float4x4*)palette.GetUnsafePtr();
            for (int i = 0; i < BoneCount; i++) p[i] = float4x4.identity;
            Conservative = VpSkinConservativeBounds.Of(Model.BindBounds, (float4x4*)palette.GetUnsafeReadOnlyPtr(), BoneCount);
        }

        /// <summary>The same pose from matrices already taken (the direct skin input's private matrices).</summary>
        public void CaptureFrom(float4x4* matrices)
        {
            if (!rented) throw new InvalidOperationException("the snapshot is not rented");
            UnsafeUtility.MemCpy(palette.GetUnsafePtr(), matrices, (long)BoneCount * sizeof(float4x4));
            Conservative = VpSkinConservativeBounds.Of(Model.BindBounds, (float4x4*)palette.GetUnsafeReadOnlyPtr(), BoneCount);
        }

        /// <summary>The pose <paramref name="direct"/>'s <see cref="VpDirectSkinInput.TryCapturePose"/> took, for the same model. False when no pose is taken or the model differs.</summary>
        public bool TryCaptureFrom(VpDirectSkinInput direct)
        {
            if (direct == null || direct.IsDisposed || !direct.HasCapturedPose || !ReferenceEquals(direct.Model, Model)) return false;
            CaptureFrom(direct.CapturedMatrices);
            return true;
        }

        /// <summary>Uploads the palette to the GPU buffer, once a request; a second call does nothing.</summary>
        public void UploadPalette()
        {
            if (PaletteUploaded) return;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            paletteBuffer.SetData(palette, 0, 0, BoneCount);
            PaletteUploaded = true;
            VpSkinBakeMarks.Note("palette-upload", began);
        }

        /// <summary>
        /// The deferred CPU bake: the float kernel over the input, the palette and the model's topology into this
        /// snapshot's room. Any thread; the inputs are immutable and the room is this request's alone. Returns the
        /// invalid count.
        /// </summary>
        public int RunKernel()
        {
            if (!rented) throw new InvalidOperationException("the snapshot is not rented");
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (FailKernelForTest) throw new InvalidOperationException("the skin bake kernel failed (a test asked it to)");
            marker[0] = 0;
            VpSharedSkinInput input = Input;
            int invalid = VpSkinFloatKernel.Skin((float3*)input.positions.GetUnsafeReadOnlyPtr(), (float3*)input.normals.GetUnsafeReadOnlyPtr(),
                (float2*)input.uv.GetUnsafeReadOnlyPtr(), input.HasUv ? 1 : 0,
                (int*)input.weightStart.GetUnsafeReadOnlyPtr(), (int*)input.weightCount.GetUnsafeReadOnlyPtr(), (int*)input.bones.GetUnsafeReadOnlyPtr(), (float*)input.weights.GetUnsafeReadOnlyPtr(),
                (float4x4*)palette.GetUnsafeReadOnlyPtr(), BoneCount, Model.TopologyPointer,
                (VpRenderVertex*)vertices.GetUnsafePtr(), (int*)topology.GetUnsafePtr(), VertexCount, (float3*)boundsOut.GetUnsafePtr(), (int*)marker.GetUnsafePtr());
            Invalid = invalid + (InvalidateOutputForTest ? 1 : 0);
            BurstRan = marker[0] == 0;
            KernelRan = true;
            KernelSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            return invalid;
        }

        /// <summary>
        /// The bounds the kernel measured, when they are a finite ordered box inside the conservative one and no vertex
        /// was invalid: the output is then valid for the storage. False otherwise, with the reason.
        /// </summary>
        public bool TryMeasuredBounds(out Bounds measured, out string refusal)
        {
            measured = default;
            refusal = null;
            if (!KernelRan) { refusal = "the kernel did not run"; return false; }
            if (Invalid != 0) { refusal = Invalid + " vertices invalid"; return false; }
            float3 min = boundsOut[0], max = boundsOut[1];
            if (!math.all(math.isfinite(min)) || !math.all(math.isfinite(max)) || !math.all(min <= max)) { refusal = "the measured bounds are not a finite ordered box"; return false; }
            var box = new Bounds();
            box.SetMinMax(min, max);
            if (!VpSkinConservativeBounds.Contains(Conservative, box)) { refusal = "the measured bounds lie outside the conservative box"; return false; }
            measured = box;
            return true;
        }

        /// <summary>The display's call: the bake into <paramref name="destination"/> at the region (the first dispatch, or one into a replaced buffer).</summary>
        public bool TryDispatch(GraphicsBuffer destination, int vertexStart, int vertexCount)
        {
            if (!rented || owner == null || owner.IsDisposed || owner.Bake == null || Input == null || Input.IsDisposed || vertexCount != VertexCount) return false;
            if (destination == null || vertexStart < 0 || (long)vertexStart + vertexCount > destination.count) return false;
            UploadPalette();
            owner.Bake.Dispatch(Input, paletteBuffer, BoneCount, destination, vertexStart);
            Dispatches++;
            return true;
        }

        internal void Free()
        {
            if (palette.IsCreated) palette.Dispose();
            if (vertices.IsCreated) vertices.Dispose();
            if (topology.IsCreated) topology.Dispose();
            if (boundsOut.IsCreated) boundsOut.Dispose();
            if (marker.IsCreated) marker.Dispose();
            paletteBuffer?.Dispose();
            paletteBuffer = null;
        }
    }

    /// <summary>
    /// A world's resources of the GPU-first route (DESIGN 4.5.15): the compute bake and a bounded pool of snapshots,
    /// each with its own palette buffer. A request rents one at the hit and gives it back when it ends; a palette
    /// buffer given back is not rented again before the next frame, so a dispatch recorded in the frame of the return
    /// never reads a palette the next request writes. Nothing is allocated at a hit once the pool is warm; past the
    /// bound a rent is refused and the request takes the CPU route (recorded by the caller).
    /// </summary>
    public sealed class VpSkinBakeResources : IDisposable
    {
        public const int DefaultCapacity = 8;

        private readonly List<VpSkinBakeSnapshot> _free = new List<VpSkinBakeSnapshot>();
        private readonly List<(VpSkinBakeSnapshot snapshot, int frame)> _returning = new List<(VpSkinBakeSnapshot, int)>();
        private int _made;
        private long _lastRequestId;
        private int _frame = int.MinValue;

        public VpSkinGpuBake Bake { get; }
        public int Capacity { get; }
        public int Rented { get; private set; }
        public int MaxRented { get; private set; }
        public int Refusals { get; private set; }
        public long Rents { get; private set; }
        public bool IsDisposed { get; private set; }
        public long NativeBytes { get; private set; }

        private VpSkinBakeResources(VpSkinGpuBake bake, int capacity)
        {
            Bake = bake;
            Capacity = capacity;
        }

        /// <summary>
        /// The resources: with the GPU bake where <paramref name="withGpu"/> (refused with the reason where it cannot be
        /// made), without it for the synchronous float route (<see cref="Bake"/> is then null and no dispatch is possible).
        /// </summary>
        public static bool TryCreate(int capacity, bool withGpu, out VpSkinBakeResources resources, out string refusal)
        {
            resources = null;
            refusal = null;
            if (capacity < 1) { refusal = "a capacity of at least one"; return false; }
            VpSkinGpuBake bake = null;
            if (withGpu && !VpSkinGpuBake.TryCreate(out bake, out refusal)) return false;
            resources = new VpSkinBakeResources(bake, capacity);
            return true;
        }

        /// <summary>Once a frame, before any rent: the snapshots returned in earlier frames become free again.</summary>
        public void BeginFrame(int frame)
        {
            if (frame == _frame) return;
            _frame = frame;
            for (int i = _returning.Count - 1; i >= 0; i--)
            {
                if (_returning[i].frame < frame) { _free.Add(_returning[i].snapshot); _returning.RemoveAt(i); }
            }
        }

        /// <summary>
        /// Rents a snapshot for a request of <paramref name="model"/> with <paramref name="input"/> (its shared input):
        /// a free one whose room fits, else a new one while the bound allows, else refused with the reason.
        /// </summary>
        public bool TryRent(VpSkinModel model, VpSharedSkinInput input, out VpSkinBakeSnapshot snapshot, out string refusal)
        {
            snapshot = null;
            refusal = null;
            if (IsDisposed) { refusal = "the resources are disposed"; return false; }
            if (model == null || model.IsDisposed || input == null || input.IsDisposed) { refusal = "no model or input"; return false; }
            BeginFrame(Time.frameCount);
            if (input.VertexCount != model.VertexCount) { refusal = "the input and the model count different vertices"; return false; }
            int bones = model.BoneCount, verts = model.VertexCount;
            // A free snapshot with enough room; the smallest that fits.
            int best = -1;
            for (int i = 0; i < _free.Count; i++)
            {
                VpSkinBakeSnapshot s = _free[i];
                if (s.paletteCapacity < bones || s.vertexCapacity < verts) continue;
                if (best < 0 || s.vertexCapacity < _free[best].vertexCapacity) best = i;
            }

            if (best >= 0)
            {
                snapshot = _free[best];
                _free.RemoveAt(best);
            }
            else if (_made < Capacity)
            {
                snapshot = Make(bones, verts);
            }
            else if (_free.Count > 0)
            {
                // Every slot is made and none that is free fits: grow the smallest free one once.
                snapshot = _free[0];
                _free.RemoveAt(0);
                snapshot.Free();
                Allocate(snapshot, Math.Max(bones, snapshot.paletteCapacity), Math.Max(verts, snapshot.vertexCapacity));
            }
            else
            {
                Refusals++;
                refusal = "every one of the " + Capacity + " snapshots is in use (" + Rented + " rented, " + _returning.Count + " returning)";
                return false;
            }

            snapshot.Begin(++_lastRequestId, model, input, bones);
            Rented++; Rents++;
            MaxRented = Math.Max(MaxRented, Rented);
            return true;
        }

        /// <summary>Gives a snapshot back; it is free again from the next frame on.</summary>
        public void Return(VpSkinBakeSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.rented || !ReferenceEquals(snapshot.owner, this)) return;
            snapshot.End();
            Rented--;
            if (IsDisposed) { snapshot.Free(); return; }
            _returning.Add((snapshot, Time.frameCount));
        }

        private VpSkinBakeSnapshot Make(int bones, int verts)
        {
            var s = new VpSkinBakeSnapshot { owner = this };
            Allocate(s, bones, verts);
            _made++;
            return s;
        }

        private void Allocate(VpSkinBakeSnapshot s, int bones, int verts)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            s.paletteCapacity = Math.Max(bones, 1);
            s.vertexCapacity = Math.Max(verts, 1);
            s.palette = new NativeArray<float4x4>(s.paletteCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            s.vertices = new NativeArray<VpRenderVertex>(s.vertexCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            s.topology = new NativeArray<int>(s.vertexCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            s.boundsOut = new NativeArray<float3>(2, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            s.marker = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            s.paletteBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s.paletteCapacity, 64) { name = "Skin bake palette " + _made };
            NativeBytes += (long)s.paletteCapacity * 64 + (long)s.vertexCapacity * 20 + 24 + 4;
            VpSkinBakeMarks.Note("snapshot-allocate", began);
        }

        public string Describe()
            => "skin bake resources: capacity " + Capacity + ", made " + _made + ", rented now " + Rented + " (max " + MaxRented + "), rents " + Rents
               + ", refusals " + Refusals + ", returning " + _returning.Count + ", dispatches " + (Bake != null ? Bake.Dispatches : 0) + ", native " + NativeBytes + " bytes";

        /// <summary>Frees everything: only after every request has given its snapshot back (the driver ends them first).</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            foreach (VpSkinBakeSnapshot s in _free) s.Free();
            foreach (var r in _returning) r.snapshot.Free();
            _free.Clear(); _returning.Clear();
            Bake?.Dispose();
        }
    }
}
