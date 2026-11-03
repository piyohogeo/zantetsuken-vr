using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The bake probe's GPU side (DESIGN 4.5.14): the compute kernels over a prepared mesh input and one fixed palette,
    /// writing 16-byte render vertices into a region of a destination buffer -- the probe's own output buffer (filled
    /// with a poison value, with a guard element before and after each region) or a buffer the drawing route reads --
    /// and the verification readback of such a region through the probe's own AsyncGPUReadback. A pending verification
    /// keeps its CPU reference and its region until the readback has completed; the region is not written again before.
    /// The CPU reference never initialises or overwrites a GPU output.
    /// </summary>
    public sealed class SkinBakeProbeGpuBake : IDisposable
    {
        public const string ShaderResource = "SkinBakeProbe";
        /// <summary>The poison written where nothing was baked: a finite but absurd position (3.39e38) and the invalid normal bytes.</summary>
        public static readonly uint4 Poison = new uint4(0x7F7F7F7Fu, 0x7F7F7F7Fu, 0x7F7F7F7Fu, 0x7F7F8080u);
        public const int GroupSize = 64;

        private readonly ComputeShader _shader;
        private readonly int _bakeKernel, _indexKernel;
        private readonly LocalKeyword _weightsS8, _weightsS16, _weightsS32, _noCounts;
        /// <summary>The verification counts (processed, invalid, bones outside the palette: the kernel's atomics and the flags' zeroing) are taken with each bake. Off for a performance measurement; a verify then judges without them.</summary>
        public bool CountsEnabled { get; set; } = true;
        private GraphicsBuffer _palette, _flags, _dummy, _output, _outputIndices;
        private int _outputCapacity, _outputIndexCapacity;
        private readonly List<Pending> _pending = new List<Pending>();
        private bool _disposed;

        public int Dispatches { get; private set; }
        public int IndexDispatches { get; private set; }
        public long PaletteBytesUploaded { get; private set; }
        public long OwnBytesAllocated { get; private set; }
        /// <summary>Seconds the last bake took on the main thread (palette and flags upload, the dispatch recording).</summary>
        public double LastBakeSeconds { get; private set; }
        public GraphicsBuffer Output => _output;
        public GraphicsBuffer OutputIndices => _outputIndices;

        private sealed class Pending
        {
            public string name;
            public GraphicsBuffer destination;
            public int start, count, guardBefore, guardAfter;
            public VpRenderVertex[] reference;
            public AsyncGPUReadbackRequest vertices, flags;
            public bool flagsRequested;
            public bool countsKnown;
            public int frame;
            public Action<SkinBakeProbeCompare.Result> done;
        }

        private static readonly uint[] s_zeroFlags = { 0, 0, 0, 0 };

        public SkinBakeProbeGpuBake()
        {
            _shader = Resources.Load<ComputeShader>(ShaderResource);
            if (_shader == null) throw new InvalidOperationException("the compute shader '" + ShaderResource + "' is not in the build");
            _bakeKernel = _shader.FindKernel("Bake16");
            _indexKernel = _shader.FindKernel("CopyIndices");
            _weightsS8 = new LocalKeyword(_shader, "WEIGHTS_S8");
            _weightsS16 = new LocalKeyword(_shader, "WEIGHTS_S16");
            _weightsS32 = new LocalKeyword(_shader, "WEIGHTS_S32");
            _noCounts = new LocalKeyword(_shader, "PROBE_NO_COUNTS");
            _flags = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4) { name = "Skin bake probe flags" };
            _dummy = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4) { name = "Skin bake probe dummy stream" };
            _dummy.SetData(new uint[] { 0, 0, 0, 0 });
            _palette = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 64) { name = "Skin bake probe palette" };
            OwnBytesAllocated += 16 + 16 + 64;
        }

        /// <summary>Makes (or remakes, larger) the probe's own output buffers and fills them with the poison.</summary>
        public void EnsureOwnOutput(int vertexCapacity, int indexCapacity)
        {
            ThrowIfDisposed();
            if (_output == null || _outputCapacity < vertexCapacity)
            {
                _output?.Dispose();
                _outputCapacity = Math.Max(vertexCapacity, 1);
                _output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _outputCapacity, 16) { name = "Skin bake probe output" };
                OwnBytesAllocated += (long)_outputCapacity * 16;
            }

            var poison = new uint4[_outputCapacity];
            for (int i = 0; i < poison.Length; i++) poison[i] = Poison;
            _output.SetData(poison);
            if (_outputIndices == null || _outputIndexCapacity < indexCapacity)
            {
                _outputIndices?.Dispose();
                _outputIndexCapacity = Math.Max(indexCapacity, 1);
                _outputIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _outputIndexCapacity, 4) { name = "Skin bake probe output indices" };
                OwnBytesAllocated += (long)_outputIndexCapacity * 4;
            }

            var poisonIndices = new uint[_outputIndexCapacity];
            for (int i = 0; i < poisonIndices.Length; i++) poisonIndices[i] = 0xFFFFFFFFu;
            _outputIndices.SetData(poisonIndices);
        }

        /// <summary>
        /// Dispatches the bake of <paramref name="input"/> by <paramref name="palette"/> into
        /// <paramref name="destination"/> at element <paramref name="start"/> (16-byte elements). The flags are zeroed
        /// first. Nothing is read back here.
        /// </summary>
        public void Bake(SkinBakeProbeMeshInput input, float4x4[] palette, GraphicsBuffer destination, int start)
        {
            ThrowIfDisposed();
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.IsDisposed) throw new ObjectDisposedException(nameof(SkinBakeProbeMeshInput));
            if (palette == null || palette.Length == 0) throw new ArgumentException("a palette of at least one bone", nameof(palette));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.stride != 16) throw new ArgumentException("the destination holds 16-byte elements", nameof(destination));
            if (start < 0 || (long)start + input.VertexCount > destination.count) throw new ArgumentOutOfRangeException(nameof(start), "the region lies outside the destination");
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_palette.count < palette.Length)
            {
                _palette.Dispose();
                _palette = new GraphicsBuffer(GraphicsBuffer.Target.Structured, palette.Length, 64) { name = "Skin bake probe palette" };
                OwnBytesAllocated += (long)palette.Length * 64;
            }

            _palette.SetData(palette, 0, 0, palette.Length);
            PaletteBytesUploaded += (long)palette.Length * 64;
            if (CountsEnabled) _flags.SetData(s_zeroFlags);

            ComputeShader s = _shader;
            s.SetKeyword(_noCounts, !CountsEnabled);
            int k = _bakeKernel;
            for (int stream = 0; stream < 4; stream++)
            {
                GraphicsBuffer b = stream < input.Streams.Length ? input.Streams[stream] : null;
                s.SetBuffer(k, "_Stream" + stream, b ?? _dummy);
            }

            s.SetInts("_Position", input.Position.stream, input.Position.offset, input.Position.Code, input.Position.dimension);
            s.SetInts("_Normal", input.Normal.stream, input.Normal.offset, input.Normal.Code, input.Normal.dimension);
            s.SetInts("_Uv", input.Uv.present ? input.Uv.stream : 0, input.Uv.present ? input.Uv.offset : 0, input.Uv.present ? input.Uv.Code : 0, input.Uv.present ? input.Uv.dimension : 0);
            var strides = new int[4];
            for (int stream = 0; stream < 4 && stream < input.Strides.Length; stream++) strides[stream] = input.Strides[stream];
            s.SetInts("_Strides", strides[0], strides[1], strides[2], strides[3]);
            // The weight reading the input recorded: the variant and the buffer's name in it.
            SkinBakeProbeMeshInput.WeightReadingKind reading = input.WeightReading;
            s.SetKeyword(_weightsS8, reading == SkinBakeProbeMeshInput.WeightReadingKind.Structured8);
            s.SetKeyword(_weightsS16, reading == SkinBakeProbeMeshInput.WeightReadingKind.Structured16);
            s.SetKeyword(_weightsS32, reading == SkinBakeProbeMeshInput.WeightReadingKind.Structured32);
            s.SetBuffer(k, reading == SkinBakeProbeMeshInput.WeightReadingKind.Structured8 ? "_WeightsS8" : reading == SkinBakeProbeMeshInput.WeightReadingKind.Structured16 ? "_WeightsS16"
                : reading == SkinBakeProbeMeshInput.WeightReadingKind.Structured32 ? "_WeightsS32" : "_Weights", input.Weights);
            s.SetInt("_WeightLayout", input.WeightLayout == SkinWeights.OneBone ? 1 : input.WeightLayout == SkinWeights.TwoBones ? 2 : input.WeightLayout == SkinWeights.FourBones ? 4 : 0);
            s.SetBuffer(k, "_WeightRanges", input.WeightRanges ?? _dummy);
            s.SetBuffer(k, "_Palette", _palette);
            s.SetInt("_BoneCount", palette.Length);
            s.SetBuffer(k, "_Output", destination);
            s.SetInt("_OutputStart", start);
            s.SetInt("_Count", input.VertexCount);
            s.SetBuffer(k, "_Flags", _flags);
            s.Dispatch(k, (input.VertexCount + GroupSize - 1) / GroupSize, 1, 1);
            Dispatches++;
            LastBakeSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Dispatches the copy of the mesh's indices into <paramref name="destination"/> at <paramref name="start"/>, each plus <paramref name="baseVertex"/>.</summary>
        public void CopyIndices(SkinBakeProbeMeshInput input, GraphicsBuffer destination, int start, uint baseVertex)
        {
            ThrowIfDisposed();
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.stride != 4) throw new ArgumentException("the destination holds uint indices", nameof(destination));
            if (start < 0 || (long)start + input.IndexCount > destination.count) throw new ArgumentOutOfRangeException(nameof(start));
            ComputeShader s = _shader;
            int k = _indexKernel;
            s.SetBuffer(k, "_Indices", input.Indices);
            s.SetInt("_IndexFormat16", input.IndexFormat == IndexFormat.UInt16 ? 1 : 0);
            s.SetInt("_IndexCount", input.IndexCount);
            s.SetInt("_BaseVertex", (int)baseVertex);
            s.SetBuffer(k, "_OutputIndices", destination);
            s.SetInt("_OutputIndexStart", start);
            s.Dispatch(k, (input.IndexCount + GroupSize - 1) / GroupSize, 1, 1);
            IndexDispatches++;
        }

        /// <summary>
        /// Requests the readback of the region just baked (with <paramref name="guardBefore"/> / <paramref name="guardAfter"/>
        /// extra elements around it, where the destination is the probe's own poisoned buffer) and of the flags; when both
        /// have completed, compares the region with <paramref name="reference"/> and gives the result to <paramref name="done"/>.
        /// The reference array is held, unchanged, until then.
        /// </summary>
        public void Verify(string name, GraphicsBuffer destination, int start, int count, int guardBefore, int guardAfter, VpRenderVertex[] reference, Action<SkinBakeProbeCompare.Result> done)
        {
            ThrowIfDisposed();
            if (!SystemInfo.supportsAsyncGPUReadback) throw new InvalidOperationException("async GPU readback is not supported here");
            int from = start - guardBefore, to = start + count + guardAfter;
            if (from < 0 || to > destination.count) throw new ArgumentOutOfRangeException(nameof(start), "the region with its guards lies outside the destination");
            var pending = new Pending { name = name, destination = destination, start = start, count = count, guardBefore = guardBefore, guardAfter = guardAfter, reference = reference, frame = Time.frameCount, done = done, countsKnown = CountsEnabled };
            pending.vertices = AsyncGPUReadback.Request(destination, (to - from) * 16, from * 16);
            // Without the counts the flags hold nothing of this bake: they are not read, and the result says so.
            if (CountsEnabled) { pending.flags = AsyncGPUReadback.Request(_flags, 16, 0); pending.flagsRequested = true; }
            _pending.Add(pending);
        }

        /// <summary>Requests the readback of indices [start, start + count) of <paramref name="destination"/> and compares them with <paramref name="reference"/> plus <paramref name="baseVertex"/>.</summary>
        public void VerifyIndices(string name, GraphicsBuffer destination, int start, int count, uint[] reference, uint baseVertex, Action<SkinBakeProbeCompare.IndexResult> done)
        {
            ThrowIfDisposed();
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(destination, count * 4, start * 4);
            _pendingIndices.Add((name, request, reference, baseVertex, done, Time.frameCount));
        }

        private readonly List<(string name, AsyncGPUReadbackRequest request, uint[] reference, uint baseVertex, Action<SkinBakeProbeCompare.IndexResult> done, int frame)> _pendingIndices = new List<(string, AsyncGPUReadbackRequest, uint[], uint, Action<SkinBakeProbeCompare.IndexResult>, int)>();

        public int PendingCount => _pending.Count + _pendingIndices.Count;

        /// <summary>Completes what has completed: call it every frame.</summary>
        public void Update()
        {
            if (_disposed) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (!p.vertices.done || (p.flagsRequested && !p.flags.done)) continue;
                _pending.RemoveAt(i);
                SkinBakeProbeCompare.Result result;
                if (p.vertices.hasError || (p.flagsRequested && p.flags.hasError))
                {
                    result = new SkinBakeProbeCompare.Result { name = p.name, readbackError = true, frameRequested = p.frame, frameCompleted = Time.frameCount, countsKnown = p.countsKnown };
                }
                else
                {
                    NativeArray<uint4> data = p.vertices.GetData<uint4>();
                    uint processed = 0, invalid = 0, outside = 0;
                    if (p.flagsRequested) { NativeArray<uint> flags = p.flags.GetData<uint>(); processed = flags[0]; invalid = flags[1]; outside = flags[2]; }
                    result = SkinBakeProbeCompare.Compare(p.name, data, p.guardBefore, p.count, p.guardAfter, p.reference, processed, invalid, outside, Poison);
                    result.countsKnown = p.countsKnown;
                    result.frameRequested = p.frame; result.frameCompleted = Time.frameCount;
                }

                p.done?.Invoke(result);
            }

            for (int i = _pendingIndices.Count - 1; i >= 0; i--)
            {
                var p = _pendingIndices[i];
                if (!p.request.done) continue;
                _pendingIndices.RemoveAt(i);
                var result = new SkinBakeProbeCompare.IndexResult { name = p.name, frameRequested = p.frame, frameCompleted = Time.frameCount };
                if (p.request.hasError) result.readbackError = true;
                else
                {
                    NativeArray<uint> data = p.request.GetData<uint>();
                    result.count = p.reference.Length;
                    for (int k = 0; k < p.reference.Length && k < data.Length; k++)
                    {
                        if (data[k] != p.reference[k] + p.baseVertex) { result.mismatches++; if (result.firstMismatch < 0) result.firstMismatch = k; }
                    }

                    if (data.Length < p.reference.Length) result.mismatches += p.reference.Length - data.Length;
                }

                p.done?.Invoke(result);
            }
        }

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SkinBakeProbeGpuBake)); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Pending p in _pending) { p.vertices.WaitForCompletion(); p.flags.WaitForCompletion(); }
            foreach (var p in _pendingIndices) p.request.WaitForCompletion();
            _pending.Clear(); _pendingIndices.Clear();
            _palette?.Dispose(); _flags?.Dispose(); _dummy?.Dispose(); _output?.Dispose(); _outputIndices?.Dispose();
        }
    }

    /// <summary>The comparison of a baked region with its CPU reference (DESIGN 4.5.14).</summary>
    public static class SkinBakeProbeCompare
    {
        /// <summary>Position differences up to this (metres) are float arithmetic order; above, a defect.</summary>
        public const float PositionTolerance = 1e-4f;
        /// <summary>A packed normal whose two bytes each differ by at most one from the reference, with the decoded angle within this, is a quantisation-boundary difference.</summary>
        public const float BoundaryAngleDegrees = 1.5f;

        public sealed class Result
        {
            public string name;
            public bool readbackError;
            public int frameRequested, frameCompleted;
            public int count, processed, invalidGpu, invalidCpu, invalidDisagree, bonesOutsidePalette;
            /// <summary>False where the bake ran without the verification counts (PROBE_NO_COUNTS): processed, invalidGpu and bonesOutsidePalette are not known, and the invalid marking is judged from the vertices alone.</summary>
            public bool countsKnown = true;
            public bool processedMatches => !countsKnown || processed == count;
            public bool guardBeforeIntact = true, guardAfterIntact = true;
            public float positionMaxError; public int positionMaxErrorVertex = -1; public int positionOverTolerance;
            public int normalExact, normalBoundary, normalOther; public float normalMaxAngleDegrees; public int normalMaxAngleVertex = -1;
            public int uvExact, uvBoundary, uvOther;
            public bool Pass => !readbackError && processedMatches && guardBeforeIntact && guardAfterIntact && positionOverTolerance == 0 && normalOther == 0 && uvOther == 0 && invalidDisagree == 0 && (!countsKnown || bonesOutsidePalette == 0);
            public string Describe()
                => name + ": " + (readbackError ? "READBACK ERROR" : (Pass ? "PASS" : "FAIL")) + "; vertices " + count + ", processed " + (countsKnown ? processed.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a (counts off)") + ", guards before/after " + guardBeforeIntact + "/" + guardAfterIntact
                   + "; position max error " + positionMaxError.ToString("E3", System.Globalization.CultureInfo.InvariantCulture) + " m at " + positionMaxErrorVertex + " (over " + PositionTolerance.ToString("E1", System.Globalization.CultureInfo.InvariantCulture) + ": " + positionOverTolerance + ")"
                   + "; normals exact " + normalExact + ", boundary " + normalBoundary + ", other " + normalOther + ", max angle " + normalMaxAngleDegrees.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " deg at " + normalMaxAngleVertex
                   + "; uv exact " + uvExact + ", boundary " + uvBoundary + ", other " + uvOther + "; invalid gpu/cpu/disagree " + invalidGpu + "/" + invalidCpu + "/" + invalidDisagree + ", bones outside palette " + bonesOutsidePalette
                   + "; frames " + frameRequested + " -> " + frameCompleted;
        }

        public sealed class IndexResult
        {
            public string name; public bool readbackError; public int frameRequested, frameCompleted, count, mismatches, firstMismatch = -1;
            public bool Pass => !readbackError && mismatches == 0;
            public string Describe() => name + ": " + (readbackError ? "READBACK ERROR" : Pass ? "PASS" : "FAIL") + "; indices " + count + ", mismatches " + mismatches + (firstMismatch >= 0 ? " (first at " + firstMismatch + ")" : "");
        }

        /// <summary>Compares <paramref name="data"/> (the guards, then the region) with <paramref name="reference"/>.</summary>
        public static Result Compare(string name, NativeArray<uint4> data, int guardBefore, int count, int guardAfter, VpRenderVertex[] reference, uint processed, uint invalidGpu, uint bonesOutside, uint4 poison)
        {
            var r = new Result { name = name, count = count, processed = (int)processed, invalidGpu = (int)invalidGpu, bonesOutsidePalette = (int)bonesOutside };
            for (int g = 0; g < guardBefore; g++) if (!data[g].Equals(poison)) r.guardBeforeIntact = false;
            for (int g = 0; g < guardAfter; g++) if (!data[guardBefore + count + g].Equals(poison)) r.guardAfterIntact = false;
            for (int i = 0; i < count; i++)
            {
                uint4 d = data[guardBefore + i];
                VpRenderVertex c = reference[i];
                float3 gp = new float3(math.asfloat(d.x), math.asfloat(d.y), math.asfloat(d.z));
                sbyte gnx = (sbyte)(d.w & 0xFF), gny = (sbyte)((d.w >> 8) & 0xFF);
                byte gu = (byte)((d.w >> 16) & 0xFF), gv = (byte)((d.w >> 24) & 0xFF);
                bool cInvalid = !c.HasValidAttributes, gInvalid = gnx == -128 && gny == -128;
                if (cInvalid) r.invalidCpu++;
                if (cInvalid != gInvalid) r.invalidDisagree++;
                float err = math.cmax(math.abs(gp - (float3)c.position));
                if (!(err <= PositionTolerance)) r.positionOverTolerance++;
                if (!(err <= r.positionMaxError)) { r.positionMaxError = float.IsNaN(err) ? float.PositiveInfinity : err; r.positionMaxErrorVertex = i; }
                if (cInvalid || gInvalid) continue;
                int dx = Math.Abs(gnx - c.normalX), dy = Math.Abs(gny - c.normalY);
                if (dx == 0 && dy == 0) r.normalExact++;
                else
                {
                    var g = new VpRenderVertex { normalX = gnx, normalY = gny };
                    float angle = Vector3.Angle(g.normal, c.normal);
                    if (angle > r.normalMaxAngleDegrees) { r.normalMaxAngleDegrees = angle; r.normalMaxAngleVertex = i; }
                    if (dx <= 1 && dy <= 1 && angle <= BoundaryAngleDegrees) r.normalBoundary++; else r.normalOther++;
                }

                int du = Math.Abs(gu - c.u), dv = Math.Abs(gv - c.v);
                if (du == 0 && dv == 0) r.uvExact++; else if (du <= 1 && dv <= 1) r.uvBoundary++; else r.uvOther++;
            }

            return r;
        }
    }
}
