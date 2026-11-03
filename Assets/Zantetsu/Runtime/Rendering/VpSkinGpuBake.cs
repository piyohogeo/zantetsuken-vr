using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The GPU bake's dispatch (DESIGN 4.5.15): the compute kernel of VpSkinBake.compute over a shared mesh input and
    /// one palette buffer, writing 16-byte render vertices into a region of a destination buffer -- the display's vertex
    /// buffer, reached only through the display (<see cref="IVpSkinBakeDispatch"/>). Nothing is read back here, ever.
    /// One of these a world; made where the shader is in the build, else refused with the reason.
    /// </summary>
    public sealed class VpSkinGpuBake : IDisposable
    {
        public const string ShaderResource = "VpSkinBake";
        public const int GroupSize = 64;

        private readonly ComputeShader _shader;
        private readonly int _kernel;
        private readonly LocalKeyword _weightsS32;
        private readonly GraphicsBuffer _dummy;
        private bool _disposed;

        public int Dispatches { get; private set; }
        /// <summary>Seconds the last dispatch took on the main thread (the bindings and the dispatch recording; no upload).</summary>
        public double LastDispatchSeconds { get; private set; }

        private VpSkinGpuBake(ComputeShader shader)
        {
            _shader = shader;
            _kernel = shader.FindKernel("Bake16");
            _weightsS32 = new LocalKeyword(shader, "WEIGHTS_S32");
            _dummy = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4) { name = "Skin bake dummy stream" };
            _dummy.SetData(new uint[] { 0, 0, 0, 0 });
        }

        /// <summary>The bake where the device runs compute shaders and the shader is in the build; else false with the reason.</summary>
        public static bool TryCreate(out VpSkinGpuBake bake, out string refusal)
        {
            bake = null;
            refusal = null;
            if (!SystemInfo.supportsComputeShaders) { refusal = "the device runs no compute shaders"; return false; }
            ComputeShader shader = Resources.Load<ComputeShader>(ShaderResource);
            if (shader == null) { refusal = "the compute shader '" + ShaderResource + "' is not in the build"; return false; }
            if (!shader.HasKernel("Bake16")) { refusal = "the compute shader has no Bake16 kernel"; return false; }
            bake = new VpSkinGpuBake(shader);
            return true;
        }

        /// <summary>
        /// Records the bake of <paramref name="input"/> by the palette in <paramref name="palette"/> (uploaded by the
        /// caller before; <paramref name="boneCount"/> entries) into <paramref name="destination"/> at element
        /// <paramref name="start"/> (16-byte elements). The caller has checked the region lies in the destination.
        /// </summary>
        public void Dispatch(VpSharedSkinInput input, GraphicsBuffer palette, int boneCount, GraphicsBuffer destination, int start)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VpSkinGpuBake));
            if (input == null || input.IsDisposed) throw new ArgumentException("a prepared shared input", nameof(input));
            if (palette == null || boneCount < 1 || boneCount > palette.count) throw new ArgumentException("a palette buffer holding the bones", nameof(palette));
            if (destination == null || destination.stride != 16) throw new ArgumentException("a 16-byte destination", nameof(destination));
            if (start < 0 || (long)start + input.VertexCount > destination.count) throw new ArgumentOutOfRangeException(nameof(start), "the region lies outside the destination");
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            ComputeShader s = _shader;
            int k = _kernel;
            for (int stream = 0; stream < 4; stream++)
            {
                GraphicsBuffer b = stream < input.Streams.Length ? input.Streams[stream] : null;
                s.SetBuffer(k, "_Stream" + stream, b ?? _dummy);
            }

            s.SetInts("_Position", input.Position.stream, input.Position.offset, 0, input.Position.dimension);
            s.SetInts("_Normal", input.Normal.stream, input.Normal.offset, 0, input.Normal.dimension);
            s.SetInts("_Uv", input.Uv.present ? input.Uv.stream : 0, input.Uv.present ? input.Uv.offset : 0, 0, input.Uv.present ? input.Uv.dimension : 0);
            s.SetInts("_Strides", Stride(input, 0), Stride(input, 1), Stride(input, 2), Stride(input, 3));
            bool structured = input.WeightReading == VpSharedSkinInput.WeightReadingKind.Structured32;
            s.SetKeyword(_weightsS32, structured);
            s.SetBuffer(k, structured ? "_WeightsS32" : "_Weights", input.Weights);
            s.SetBuffer(k, "_Palette", palette);
            s.SetInt("_BoneCount", boneCount);
            s.SetBuffer(k, "_Output", destination);
            s.SetInt("_OutputStart", start);
            s.SetInt("_Count", input.VertexCount);
            s.Dispatch(k, (input.VertexCount + GroupSize - 1) / GroupSize, 1, 1);
            Dispatches++;
            LastDispatchSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            VpSkinBakeMarks.Note("dispatch", began);   // the main thread's recording only, never the GPU's execution
        }

        private static int Stride(VpSharedSkinInput input, int stream) => stream < input.Strides.Length ? input.Strides[stream] : 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dummy?.Dispose();
        }
    }

    /// <summary>
    /// What a GPU-baked region's producer does when the display asks it to bake, or to bake again into a replaced
    /// buffer (DESIGN 4.5.15): the display owns the buffer and says where; the producer records the dispatch from its
    /// own snapshot. False when the producer can no longer bake (its snapshot is gone): the display then forgets the region.
    /// </summary>
    public interface IVpSkinBakeDispatch
    {
        bool TryDispatch(GraphicsBuffer destination, int vertexStart, int vertexCount);
    }
}
