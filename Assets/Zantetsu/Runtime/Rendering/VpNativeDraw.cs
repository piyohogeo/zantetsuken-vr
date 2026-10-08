using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Plugin = Zantetsu.Rendering.VpNativeDrawPlugin;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Whether a world draws its bodies through the Direct3D 12 plugin (DESIGN 4.5.8): asked for by a Player argument,
    /// settled at start-up and never switched, as the GPU selection is (D-198). The default is Unity's own route; a
    /// Player started with <see cref="Argument"/> takes the plugin's, and a plugin that cannot be had then is a
    /// failure the world records -- it does not fall back on its own, so that no comparison mixes the two.
    /// </summary>
    public static class VpNativeDrawSetup
    {
        /// <summary>The Player argument that draws the bodies through the plugin.</summary>
        public const string Argument = "-zantetsuVp3cNative";

        private static bool s_read;
        private static bool s_requested;

        public static bool Requested
        {
            get
            {
                Read();
                return s_requested;
            }
            set
            {
                Read();
                s_requested = value;
            }
        }

        /// <summary>What <see cref="Requested"/> answers for a set of arguments. For tests of the reading itself.</summary>
        public static bool Read(string[] arguments)
        {
            if (arguments == null)
            {
                return false;
            }

            for (int i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Read()
        {
            if (!s_read)
            {
                s_read = true;
                s_requested = Read(Environment.GetCommandLineArgs());
            }
        }
    }

    /// <summary>
    /// Where a display whose batch writes the plugin's argument entries hands its draws (DESIGN 4.5.8), in place of
    /// Graphics.RenderPrimitivesIndexedIndirect: one call per run of commands that share a material, as the display
    /// groups them. The sink issues them through the plugin in the camera's frame; the display knows nothing of how.
    /// </summary>
    public interface IVpNativeDrawSink
    {
        /// <summary>The body of commands [startCommand, startCommand + commandCount) for a camera, from the selection of its view.</summary>
        void AddBody(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer);

        /// <summary>The casters of the same commands, with the caster material (one-sided or two-sided) they are cast with.</summary>
        void AddCasters(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer);

        /// <summary>
        /// A buffer the owner is done with (replaced, or the owner ending) that this sink's issued events may still
        /// name: the sink disposes it once no such event can still run, never before. The owner gives up the buffer
        /// here instead of disposing it. An owner that drew through more than one sink in its life (the drawing
        /// component disabled and enabled makes a new route) waits on all of them instead: <see cref="IssuedEventsWait"/>
        /// and <see cref="VpNativeDrawRelease.Retire"/>.
        /// </summary>
        void Retire(GraphicsBuffer buffer);

        /// <summary>
        /// A wait that is over once no event this sink has issued so far can still read anything (consumed, or
        /// confirmed never to run); null when none can now. Disposing the sink does not end the wait: the events it
        /// issued end it.
        /// </summary>
        VpNativeDrawRelease.IRingWait IssuedEventsWait();
    }

    /// <summary>Per-draw constant values the plugin's draws are given as Unity would give them.</summary>
    public static class VpNativeConstants
    {
        /// <summary>
        /// The seven unity_SH* constants of a probe, as Unity packs an L2 probe for its shaders (SHAr/g/b: the linear
        /// terms and the constant term less the z² term; SHBr/g/b: the quadratic terms; SHC: the xy terms, w 1).
        /// </summary>
        public static Vector4[] PackSphericalHarmonics(SphericalHarmonicsL2 sh)
        {
            var packed = new Vector4[7];
            PackSphericalHarmonics(sh, packed);
            return packed;
        }

        /// <summary>The same into an array of seven the caller keeps: nothing is allocated per frame.</summary>
        public static void PackSphericalHarmonics(SphericalHarmonicsL2 sh, Vector4[] into)
        {
            if (into == null || into.Length < 7)
            {
                throw new ArgumentException("seven constants", nameof(into));
            }

            for (int c = 0; c < 3; c++)
            {
                into[c] = new Vector4(sh[c, 3], sh[c, 1], sh[c, 2], sh[c, 0] - sh[c, 6]);
                into[3 + c] = new Vector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]);
            }

            into[6] = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f);
        }
    }

    /// <summary>
    /// The native pointers of the resources a draw binds, taken once and reused (DESIGN 4.5.8): asking Unity for a
    /// native pointer is not a plain read -- it may synchronise with the rendering thread -- so a frame asks here, and
    /// here asks Unity only for a resource not seen before or one whose native object may have changed.
    /// <para>
    /// **When a pointer is taken again (the invalidation of each kind, 2026-10-08).**
    /// A <see cref="GraphicsBuffer"/>'s native buffer is fixed for the object's lifetime: Unity never resizes one in
    /// place, SetData writes into the same buffer (VpNativePointerFactsTests, Direct3D 11 and 12), and the batch
    /// replaces a buffer by a new object. Its pointer is kept while the object is valid and its count and stride are
    /// unchanged, and dropped by <see cref="DropDead"/> once it is disposed (called every frame by the route).
    /// A <see cref="Texture2D"/>, <see cref="Cubemap"/> or other non-render texture is kept while the object lives
    /// and its width, height, mip count, dimension, format and <see cref="Texture.updateCount"/> are unchanged: an
    /// upload (Apply) keeps the native texture and raises the count (asked again, needlessly but safely). A
    /// <see cref="Texture2D.Reinitialize(int, int)"/> of the same size changes the native texture and raises nothing
    /// Unity shows (the facts test prints it): an owner that reinitialises a texture the route binds must call
    /// <see cref="Invalidate(Texture)"/>; the route's textures (the palette atlases, a material's base map, the default
    /// reflection) are not reinitialised by this code base.
    /// A <see cref="RenderTexture"/> is kept while it is created and its size, formats and
    /// <see cref="Texture.updateCount"/> are unchanged: Unity raises the count when the texture is created, so a
    /// release and re-creation of the same size is seen at once (the facts test asserts it on both devices), and a
    /// destroyed one is dropped. Its colour is bound through the colour render buffer and its depth through the depth
    /// render buffer (Unity resolves both to the current resource when the plugin event runs); the handles are what
    /// is kept. No pointer is ever kept for a number of frames as a safety measure.
    /// </para>
    /// </summary>
    public sealed class VpNativePointerCache
    {
        private struct BufferEntry
        {
            public IntPtr pointer;
            public int count, stride;
            public uint generation;
        }

        private struct TextureEntry
        {
            public IntPtr pointer, colourRenderBuffer, depthRenderBuffer;
            public int width, height, mipmapCount;
            public uint updateCount;
            public TextureDimension dimension;
            public GraphicsFormat format, depthFormat;
            public bool renderTexture;
        }

        private readonly Dictionary<GraphicsBuffer, BufferEntry> _buffers = new Dictionary<GraphicsBuffer, BufferEntry>();
        private readonly Dictionary<Texture, TextureEntry> _textures = new Dictionary<Texture, TextureEntry>();
        private readonly List<GraphicsBuffer> _staleBuffers = new List<GraphicsBuffer>();
        private readonly List<Texture> _staleTextures = new List<Texture>();

        /// <summary>How many times Unity was asked for a pointer, and how many times a kept one answered.</summary>
        public long Acquisitions { get; private set; }

        public long Hits { get; private set; }

        /// <summary>How many entries are kept.</summary>
        public int Count => _buffers.Count + _textures.Count;

        /// <summary>A buffer no Unity API writes after its creation (the route's own, written by the GPU or once): kept for the object's life.</summary>
        public IntPtr Buffer(GraphicsBuffer buffer)
        {
            return Buffer(buffer, 0);
        }

        /// <summary>
        /// A buffer with its owner's CPU-write generation: Unity's contract is that writing a buffer's data through
        /// its APIs may change the native buffer ("Call GetNativeBufferPtr to get the new native pointer"), so the
        /// pointer is kept only while the generation the owner raises at each such write is the one it was taken at.
        /// A write by the GPU (a compute pass) raises nothing: it writes into the buffer that is.
        /// </summary>
        public IntPtr Buffer(GraphicsBuffer buffer, uint generation)
        {
            if (buffer == null || !buffer.IsValid())
            {
                throw new ArgumentException("a valid buffer", nameof(buffer));
            }

            if (_buffers.TryGetValue(buffer, out BufferEntry entry) && entry.count == buffer.count && entry.stride == buffer.stride && entry.generation == generation)
            {
                Hits++;
                return entry.pointer;
            }

            Acquisitions++;
            entry = new BufferEntry { pointer = buffer.GetNativeBufferPtr(), count = buffer.count, stride = buffer.stride, generation = generation };
            _buffers[buffer] = entry;
            return entry.pointer;
        }

        /// <summary>
        /// A non-render texture's native texture. A render texture is not taken this way (its native texture changes
        /// when it is made again): bind it through <see cref="ColourRenderBuffer"/>.
        /// </summary>
        public IntPtr Texture(Texture texture)
        {
            if (texture == null)
            {
                throw new ArgumentException("a live texture", nameof(texture));
            }

            if (texture is RenderTexture)
            {
                throw new ArgumentException("a render texture is bound through its render buffer, not its texture pointer", nameof(texture));
            }

            if (TryKept(texture, out TextureEntry entry) && entry.pointer != IntPtr.Zero)
            {
                Hits++;
                return entry.pointer;
            }

            Acquisitions++;
            entry.pointer = texture.GetNativeTexturePtr();
            Store(texture, entry);
            return entry.pointer;
        }

        /// <summary>A render texture's colour, as its render buffer handle (the plugin asks Unity for the resource). Zero while it is not created.</summary>
        public IntPtr ColourRenderBuffer(RenderTexture texture)
        {
            if (texture == null)
            {
                throw new ArgumentException("a live render texture", nameof(texture));
            }

            if (!texture.IsCreated())
            {
                return IntPtr.Zero;
            }

            if (TryKept(texture, out TextureEntry entry) && entry.colourRenderBuffer != IntPtr.Zero)
            {
                Hits++;
                return entry.colourRenderBuffer;
            }

            Acquisitions++;
            entry.colourRenderBuffer = texture.colorBuffer.GetNativeRenderBufferPtr();
            Store(texture, entry);
            return entry.colourRenderBuffer;
        }

        /// <summary>A render texture's depth, as its render buffer handle (the plugin asks Unity for the resource). Zero while it is not created.</summary>
        public IntPtr DepthRenderBuffer(RenderTexture texture)
        {
            if (texture == null)
            {
                throw new ArgumentException("a live render texture", nameof(texture));
            }

            if (!texture.IsCreated())
            {
                return IntPtr.Zero;
            }

            if (TryKept(texture, out TextureEntry entry) && entry.depthRenderBuffer != IntPtr.Zero)
            {
                Hits++;
                return entry.depthRenderBuffer;
            }

            Acquisitions++;
            entry.depthRenderBuffer = texture.depthBuffer.GetNativeRenderBufferPtr();
            Store(texture, entry);
            return entry.depthRenderBuffer;
        }

        /// <summary>Forgets a texture's pointers: for an owner that changed its native texture in a way Unity does not show (Reinitialize).</summary>
        public void Invalidate(Texture texture)
        {
            if (texture != null) _textures.Remove(texture);
        }

        /// <summary>Forgets a buffer's pointer.</summary>
        public void Invalidate(GraphicsBuffer buffer)
        {
            if (buffer != null) _buffers.Remove(buffer);
        }

        /// <summary>Drops every pointer of buffers disposed and textures destroyed. The route calls it once a frame; it costs a pass over the entries.</summary>
        public void DropDead()
        {
            foreach (KeyValuePair<GraphicsBuffer, BufferEntry> pair in _buffers)
            {
                if (pair.Key == null || !pair.Key.IsValid()) _staleBuffers.Add(pair.Key);
            }

            foreach (GraphicsBuffer stale in _staleBuffers) _buffers.Remove(stale);
            _staleBuffers.Clear();
            foreach (KeyValuePair<Texture, TextureEntry> pair in _textures)
            {
                if (pair.Key == null) _staleTextures.Add(pair.Key);
            }

            foreach (Texture stale in _staleTextures) _textures.Remove(stale);
            _staleTextures.Clear();
        }

        public void Clear()
        {
            _buffers.Clear();
            _textures.Clear();
        }

        private bool TryKept(Texture texture, out TextureEntry entry)
        {
            if (!_textures.TryGetValue(texture, out entry))
            {
                entry = default;
                return false;
            }

            if (entry.width != texture.width || entry.height != texture.height || entry.mipmapCount != texture.mipmapCount
                || entry.dimension != texture.dimension || entry.format != texture.graphicsFormat || entry.updateCount != texture.updateCount)
            {
                entry = default;
                return false;
            }

            if (entry.renderTexture)
            {
                var renderTexture = (RenderTexture)texture;
                if (!renderTexture.IsCreated() || entry.depthFormat != renderTexture.depthStencilFormat)
                {
                    entry = default;
                    return false;
                }
            }

            return true;
        }

        private void Store(Texture texture, TextureEntry entry)
        {
            entry.width = texture.width;
            entry.height = texture.height;
            entry.mipmapCount = texture.mipmapCount;
            entry.dimension = texture.dimension;
            entry.format = texture.graphicsFormat;
            entry.updateCount = texture.updateCount;
            if (texture is RenderTexture renderTexture)
            {
                entry.renderTexture = true;
                entry.depthFormat = renderTexture.depthStencilFormat;
            }

            _textures[texture] = entry;
        }
    }

    /// <summary>A display that can hand its draws to a sink: what the route is given to attach itself to.</summary>
    public interface IVpNativeDrawHost
    {
        /// <summary>Whether the display's batch writes the plugin's argument entries, so that only a sink can draw it.</summary>
        bool NativeArguments { get; }

        IVpNativeDrawSink NativeDrawSink { get; set; }
    }

    /// <summary>The draw state of one native pipeline: what the PSO is built with, beside the shaders.</summary>
    public struct VpNativeRenderState : IEquatable<VpNativeRenderState>
    {
        public bool Equals(VpNativeRenderState other)
        {
            return colourFormat == other.colourFormat && depthFormat == other.depthFormat && sampleCount == other.sampleCount && cullMode == other.cullMode
                && frontCounterClockwise == other.frontCounterClockwise && depthFunc == other.depthFunc && depthWrite == other.depthWrite && colourWrite == other.colourWrite
                && depthBias == other.depthBias && slopeScaledDepthBias.Equals(other.slopeScaledDepthBias) && depthBiasClamp.Equals(other.depthBiasClamp);
        }

        public override bool Equals(object obj) => obj is VpNativeRenderState other && Equals(other);

        public override int GetHashCode()
        {
            int hash = (int)colourFormat;
            hash = (hash * 397) ^ (int)depthFormat;
            hash = (hash * 397) ^ sampleCount;
            hash = (hash * 397) ^ (int)cullMode;
            hash = (hash * 397) ^ (frontCounterClockwise ? 1 : 0);
            hash = (hash * 397) ^ (int)depthFunc;
            hash = (hash * 397) ^ (depthWrite ? 1 : 0);
            hash = (hash * 397) ^ (colourWrite ? 1 : 0);
            hash = (hash * 397) ^ depthBias;
            hash = (hash * 397) ^ slopeScaledDepthBias.GetHashCode();
            return (hash * 397) ^ depthBiasClamp.GetHashCode();
        }

        public GraphicsFormat colourFormat; // None for a depth-only pass
        public GraphicsFormat depthFormat;
        public int sampleCount;
        public Plugin.CullMode cullMode;
        public bool frontCounterClockwise;
        public Plugin.Compare depthFunc;
        public bool depthWrite;
        public bool colourWrite;
        public int depthBias;
        public float slopeScaledDepthBias;
        public float depthBiasClamp;

        public override string ToString()
        {
            return "colour " + colourFormat + " depth " + depthFormat + " x" + sampleCount + " cull " + cullMode + (frontCounterClockwise ? " ccw" : " cw")
                + " depth " + depthFunc + (depthWrite ? " write" : "") + (colourWrite ? " colour" : " no colour")
                + " bias " + depthBias + "/" + slopeScaledDepthBias.ToString("G4");
        }
    }

    /// <summary>
    /// One native pipeline (DESIGN 4.5.8): a shader variant the Editor compiled (<see cref="VpNativeShaderBinary"/>)
    /// with one draw state, as the plugin holds it. The bindings are numbered here, in the order they are given to
    /// the plugin, and a draw names what it binds by those numbers (<see cref="VpNativeDrawData"/>). The samplers are
    /// static: a texture's sampler is decided at creation from the texture it will be sampled with, so a pipeline is
    /// made for the material it draws.
    /// </summary>
    public sealed unsafe class VpNativePipeline : IDisposable
    {
        public const string CommandConstantBuffer = "VpNativeCommand";

        public sealed class BindingInfo
        {
            public Plugin.BindingKind Kind;
            public Plugin.Stage Stage;
            public string Name;
            public int Register;
            public VpNativeShaderBinary.ConstantBuffer Layout; // for a constant buffer
            public Plugin.TextureKind TextureKind;             // for a texture
        }

        private readonly List<BindingInfo> _bindings = new List<BindingInfo>();
        private int _id = -1;

        private VpNativePipeline()
        {
        }

        /// <summary>The plugin's number for this pipeline; -1 once disposed.</summary>
        public int Id => _id;

        public string Name { get; private set; }

        public VpNativeShaderBinary Binary { get; private set; }

        public VpNativeRenderState State { get; private set; }

        public IReadOnlyList<BindingInfo> Bindings => _bindings;

        /// <summary>
        /// Makes the pipeline. <paramref name="samplerOf"/> answers, for a texture binding's name, the sampler it is
        /// sampled with; for a sampler the shader declares without a texture of its own (an inline sampler state, whose
        /// name Unity does not report) it is asked with an empty name. False, with the plugin's reason, when the plugin
        /// refuses it.
        /// </summary>
        public static bool TryCreate(
            string name, VpNativeShaderBinary binary, VpNativeRenderState state,
            Func<string, (Plugin.SamplerKind kind, int anisotropy)> samplerOf, out VpNativePipeline pipeline, out string failure)
        {
            pipeline = null;
            failure = null;
            if (binary == null || samplerOf == null)
            {
                failure = "no shader binary or no sampler source";
                return false;
            }

            VpNativeShaderBinary.Stage vertex = binary.FindStage(VpNativeShaderBinary.StageKind.Vertex);
            VpNativeShaderBinary.Stage pixel = binary.FindStage(VpNativeShaderBinary.StageKind.Pixel);
            if (vertex == null || vertex.Bytecode.Length == 0)
            {
                failure = binary.Name + ": no vertex stage";
                return false;
            }

            var result = new VpNativePipeline { Name = name, Binary = binary, State = state };
            result.AddStage(vertex, Plugin.Stage.Vertex, samplerOf);
            if (pixel != null)
            {
                result.AddStage(pixel, Plugin.Stage.Pixel, samplerOf);
            }

            var bindings = new Plugin.Binding[result._bindings.Count];
            for (int i = 0; i < bindings.Length; i++)
            {
                BindingInfo info = result._bindings[i];
                bindings[i].kind = (uint)info.Kind;
                bindings[i].stage = (uint)info.Stage;
                bindings[i].shaderRegister = (uint)info.Register;
                bindings[i].SetName(info.Name);
                switch (info.Kind)
                {
                    case Plugin.BindingKind.ConstantBuffer:
                        bindings[i].extra = (uint)info.Layout.Size;
                        break;
                    case Plugin.BindingKind.Texture:
                        bindings[i].extra = (uint)info.TextureKind;
                        break;
                    case Plugin.BindingKind.Sampler:
                        (Plugin.SamplerKind kind, int anisotropy) = samplerOf(info.Name);
                        bindings[i].extra = (uint)kind;
                        bindings[i].extra2 = (uint)Mathf.Max(1, anisotropy);
                        break;
                }
            }

            byte[] pixelBytes = pixel != null ? pixel.Bytecode : Array.Empty<byte>();
            fixed (byte* vs = vertex.Bytecode)
            fixed (byte* ps = pixelBytes)
            fixed (Plugin.Binding* b = bindings)
            {
                var desc = new Plugin.PipelineDesc
                {
                    version = Plugin.ContractVersion,
                    vertexBytecode = vs,
                    vertexBytecodeSize = (uint)vertex.Bytecode.Length,
                    pixelBytecode = pixelBytes.Length > 0 ? ps : null,
                    pixelBytecodeSize = (uint)pixelBytes.Length,
                    bindings = b,
                    bindingCount = (uint)bindings.Length,
                    renderTargetFormat = state.colourFormat == GraphicsFormat.None ? 0 : Plugin.DxgiFormat(state.colourFormat),
                    depthFormat = state.depthFormat == GraphicsFormat.None ? 0 : Plugin.DxgiDepthFormat(state.depthFormat),
                    sampleCount = (uint)Mathf.Max(1, state.sampleCount),
                    cullMode = (uint)state.cullMode,
                    frontCounterClockwise = state.frontCounterClockwise ? 1u : 0u,
                    depthFunc = (uint)state.depthFunc,
                    depthWrite = state.depthWrite ? 1u : 0u,
                    colourWrite = state.colourWrite ? 1u : 0u,
                    depthBias = state.depthBias,
                    slopeScaledDepthBias = state.slopeScaledDepthBias,
                    depthBiasClamp = state.depthBiasClamp,
                    argumentStride = Plugin.ArgumentStride,
                };
                if (state.colourFormat != GraphicsFormat.None && desc.renderTargetFormat == 0)
                {
                    failure = name + ": the colour format " + state.colourFormat + " has no DXGI format here";
                    return false;
                }

                if (state.depthFormat != GraphicsFormat.None && desc.depthFormat == 0)
                {
                    failure = name + ": the depth format " + state.depthFormat + " has no DXGI format here";
                    return false;
                }

                Plugin.WriteName(desc.name, name);
                result._id = Plugin.CreatePipeline(&desc);
            }

            if (result._id < 0)
            {
                failure = Plugin.LastError();
                return false;
            }

            pipeline = result;
            return true;
        }

        private void AddStage(VpNativeShaderBinary.Stage stage, Plugin.Stage kind, Func<string, (Plugin.SamplerKind, int)> samplerOf)
        {
            foreach (VpNativeShaderBinary.ConstantBuffer buffer in stage.ConstantBuffers)
            {
                _bindings.Add(new BindingInfo
                {
                    Kind = string.Equals(buffer.Name, CommandConstantBuffer, StringComparison.Ordinal) ? Plugin.BindingKind.CommandConstant : Plugin.BindingKind.ConstantBuffer,
                    Stage = kind, Name = buffer.Name, Register = buffer.Register, Layout = buffer,
                });
            }

            foreach (VpNativeShaderBinary.BufferBinding buffer in stage.Buffers)
            {
                _bindings.Add(new BindingInfo { Kind = Plugin.BindingKind.Buffer, Stage = kind, Name = buffer.Name, Register = buffer.Register });
            }

            var samplerRegisters = new HashSet<int>();
            foreach (VpNativeShaderBinary.TextureBinding texture in stage.Textures)
            {
                Plugin.TextureKind textureKind = texture.Dimension == (int)TextureDimension.Cube ? Plugin.TextureKind.Cube
                    : texture.Dimension == (int)TextureDimension.Tex2DArray ? Plugin.TextureKind.Tex2DArray : Plugin.TextureKind.Tex2D;
                _bindings.Add(new BindingInfo { Kind = Plugin.BindingKind.Texture, Stage = kind, Name = texture.Name, Register = texture.Register, TextureKind = textureKind });
                if (texture.SamplerRegister >= 0 && samplerRegisters.Add(texture.SamplerRegister))
                {
                    _bindings.Add(new BindingInfo { Kind = Plugin.BindingKind.Sampler, Stage = kind, Name = texture.Name, Register = texture.SamplerRegister });
                }
            }

            foreach (VpNativeShaderBinary.SamplerBinding sampler in stage.Samplers)
            {
                if (samplerRegisters.Add(sampler.Register))
                {
                    _bindings.Add(new BindingInfo { Kind = Plugin.BindingKind.Sampler, Stage = kind, Name = sampler.Name ?? "", Register = sampler.Register });
                }
            }

            _ = samplerOf;
        }

        /// <summary>The binding's number, or -1 when the stage does not bind it (then nothing need be given for it).</summary>
        public int Find(Plugin.Stage stage, Plugin.BindingKind kind, string name)
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                BindingInfo b = _bindings[i];
                if (b.Stage == stage && b.Kind == kind && string.Equals(b.Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The plugin's own description of the root layout, for a record.</summary>
        public string Describe()
        {
            return _id >= 0 ? Plugin.DescribePipeline(_id) : "(disposed)";
        }

        public void Dispose()
        {
            if (_id >= 0)
            {
                Plugin.ReleasePipeline(_id);
                _id = -1;
            }
        }
    }

    /// <summary>
    /// The bytes of one constant buffer of one stage, written constant by constant at the offsets the compiled shader
    /// uses. A constant the shader does not use has no offset and is not written (<see cref="TrySet"/> says so), which
    /// is how a caller tells "not needed by this variant" from a mistake in a name.
    /// </summary>
    public sealed class VpNativeConstantBlock
    {
        public VpNativeConstantBlock(VpNativeShaderBinary.ConstantBuffer layout)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            Bytes = new byte[layout.Size];
        }

        public VpNativeShaderBinary.ConstantBuffer Layout { get; }

        public byte[] Bytes { get; }

        public bool Uses(string name) => Layout.Find(name) != null;

        public bool TrySet(string name, float value)
        {
            VpNativeShaderBinary.Constant constant = Layout.Find(name);
            if (constant == null) return false;
            WriteFloat(constant.Offset, value);
            return true;
        }

        public bool TrySet(string name, int value)
        {
            VpNativeShaderBinary.Constant constant = Layout.Find(name);
            if (constant == null) return false;
            CheckRoom(constant.Offset, 4);
            unsafe
            {
                fixed (byte* bytes = Bytes)
                {
                    *(int*)(bytes + constant.Offset) = value;
                }
            }

            return true;
        }

        public bool TrySet(string name, Vector4 value)
        {
            VpNativeShaderBinary.Constant constant = Layout.Find(name);
            if (constant == null) return false;
            WriteVector(constant.Offset, value, constant.Columns);
            return true;
        }

        /// <summary>
        /// A matrix as Unity's compiler lays a float4x4 out: column-major, each column one register, which is the
        /// order of <see cref="Matrix4x4"/>'s own fields.
        /// </summary>
        public bool TrySet(string name, Matrix4x4 value)
        {
            VpNativeShaderBinary.Constant constant = Layout.Find(name);
            if (constant == null) return false;
            WriteMatrix(constant.Offset, value);
            return true;
        }

        public bool TrySet(string name, Matrix4x4[] values, int count)
        {
            VpNativeShaderBinary.Constant constant = Layout.Find(name);
            if (constant == null) return false;
            int n = Mathf.Min(count, constant.ArraySize > 0 ? constant.ArraySize : 1);
            for (int i = 0; i < n; i++)
            {
                WriteMatrix(constant.Offset + i * 64, values[i]);
            }

            return true;
        }

        public bool TrySet(string name, Vector4[] values, int count)
        {
            VpNativeShaderBinary.Constant constant = Layout.Find(name);
            if (constant == null) return false;
            int n = Mathf.Min(count, constant.ArraySize > 0 ? constant.ArraySize : 1);
            for (int i = 0; i < n; i++)
            {
                WriteVector(constant.Offset + i * 16, values[i], 4);
            }

            return true;
        }

        // Written in place: no byte array is made for a value.
        private void WriteVector(int offset, Vector4 value, int columns)
        {
            WriteFloat(offset, value.x);
            if (columns > 1) WriteFloat(offset + 4, value.y);
            if (columns > 2) WriteFloat(offset + 8, value.z);
            if (columns > 3) WriteFloat(offset + 12, value.w);
        }

        private void WriteMatrix(int offset, Matrix4x4 value)
        {
            CheckRoom(offset, 64);
            unsafe
            {
                fixed (byte* bytes = Bytes)
                {
                    var floats = (float*)(bytes + offset);
                    for (int i = 0; i < 16; i++)
                    {
                        floats[i] = value[i];
                    }
                }
            }
        }

        private void WriteFloat(int offset, float value)
        {
            CheckRoom(offset, 4);
            unsafe
            {
                fixed (byte* bytes = Bytes)
                {
                    *(float*)(bytes + offset) = value;
                }
            }
        }

        private void CheckRoom(int offset, int length)
        {
            if (offset < 0 || offset + length > Bytes.Length)
            {
                throw new InvalidOperationException("a constant of " + Layout.Name + " lies outside the buffer (" + offset + " + " + length + " > " + Bytes.Length + ")");
            }
        }
    }

    /// <summary>
    /// The event data of the plugin's draws: a ring of unmanaged blocks, each one issue (header, resources, constant
    /// buffer bytes) laid out as the plugin reads it. A block is reused only once no rendering thread can still read
    /// it; when every block is still in flight the caller is told and issues nothing -- nothing waits. The blocks are
    /// allocated once, at the capacity given, and freed on dispose.
    /// <para>
    /// **A block's states, in the order of the code that moves it (TL, 2026-10-08).**
    /// <list type="number">
    /// <item><see cref="BlockState.Prepared"/>: <see cref="Finish"/> closed the issue, inside a pass's Execute. The
    /// block is in no command buffer yet.</item>
    /// <item><see cref="BlockState.Recorded"/>: <see cref="RecordPrepared"/> wrote the event into the pass's command
    /// buffer, at the end of the same Execute. That Execute runs inside URP's RenderSingleCameraInternal, before the
    /// camera's <c>context.Submit()</c> -- so the command is not submitted yet; it will be at that Submit, or never
    /// (the render graph clears the command buffer when a pass throws: RenderGraph.ResetGraphAndLogException).</item>
    /// <item><see cref="BlockState.Submitted"/>: <see cref="SubmitRecorded"/>, called by <see cref="VpNativeDrawRelease"/>
    /// from RenderPipelineManager.endCameraRendering -- which URP raises after RenderSingleCameraInternal returned,
    /// i.e. after that Submit -- stamps every Recorded block with the sentinel the keeper issues there, through the
    /// same ScriptableRenderContext, behind the camera's submission.</item>
    /// <item>Consumed: the plugin ran the event on the rendering thread, read the block, and set its consumed mark
    /// (after a memory barrier). The block is released by that mark alone, in whatever state it was.</item>
    /// <item>Discard confirmed: a Submitted block whose sentinel was consumed without its own mark. The rendering
    /// thread executes a context's submissions in order; the sentinel ran, so every command submitted before it ran
    /// too -- the block's event was not among them: its command buffer was cleared or orphaned, and nothing will run
    /// it. Or a Prepared block the pass never recorded (<see cref="DropPrepared"/>: the Execute ended without
    /// <see cref="RecordPrepared"/>), which was in no command buffer at all.</item>
    /// </list>
    /// A Prepared or Recorded block is never judged by a sentinel: a sentinel issued before the camera's Submit could
    /// run before the event, and that is the overtaking this ordering rules out. The device gone (the plugin then
    /// writes to no block, and the rendering thread had run every earlier event before the shutdown event) releases
    /// everything. No frame count and no wait stands in for any of this.
    /// </para>
    /// </summary>
    public sealed unsafe class VpNativeDrawData : IDisposable
    {
        /// <summary>Where a block is between being taken and being released (see the class remarks).</summary>
        public enum BlockState : byte
        {
            Free = 0,
            Prepared = 1,
            Recorded = 2,
            Submitted = 3,
        }

        private readonly IntPtr[] _blocks;
        private readonly BlockState[] _state;
        private readonly ulong[] _sentinel;      // Submitted: the sentinel serial issued behind the block's camera submission
        private readonly int[] _frame;           // the frame the block was taken in (for the record only)
        private readonly List<int> _prepared = new List<int>(16);
        private ulong _consumedSentinel;
        private bool _deviceGone;

        /// <summary>A name for the record (the journal names rings by it).</summary>
        public string Name { get; set; } = "ring";

        /// <summary>Prepared blocks a pass never recorded, released without any event (counted).</summary>
        public long NeverRecorded { get; private set; }

        /// <summary>Submitted blocks released by their sentinel without their own consumed mark: their commands never ran.</summary>
        public long DiscardsConfirmed { get; private set; }
        private readonly int _blockBytes;
        private readonly int _resourceCapacity;
        private readonly int _constantsCapacity;
        private int _next;
        private byte* _current;
        private int _resourceCount;
        private int _constantsCount;
        private int _dataUsed;
        private uint _serial;
        private bool _disposed;

        /// <summary>How many times a block was asked for while none was free.</summary>
        public long RingFull { get; private set; }

        /// <summary>How many blocks were taken.</summary>
        public long Taken { get; private set; }

        public VpNativeDrawData(int blocks, int resourceCapacity, int constantsCapacity, int constantBytesCapacity)
        {
            if (blocks <= 0 || resourceCapacity <= 0 || constantsCapacity <= 0 || constantBytesCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blocks), "every capacity must be positive");
            }

            _resourceCapacity = resourceCapacity;
            _constantsCapacity = constantsCapacity;
            _blockBytes = HeaderBytes + resourceCapacity * sizeof(Plugin.Resource) + constantsCapacity * sizeof(Plugin.Constants) + constantBytesCapacity;
            _blocks = new IntPtr[blocks];
            _state = new BlockState[blocks];
            _sentinel = new ulong[blocks];
            _frame = new int[blocks];
            for (int i = 0; i < blocks; i++)
            {
                _blocks[i] = Marshal.AllocHGlobal(_blockBytes);
                new Span<byte>((void*)_blocks[i], _blockBytes).Clear();
            }
        }

        private static int HeaderBytes => (sizeof(Plugin.Draw) + 15) / 16 * 16;

        private int ResourcesOffset => HeaderBytes;

        private int ConstantsOffset => HeaderBytes + _resourceCapacity * sizeof(Plugin.Resource);

        private int DataOffset => ConstantsOffset + _constantsCapacity * sizeof(Plugin.Constants);

        /// <summary>
        /// Takes a free block and starts an issue in it. False when every block is still with the plugin (counted).
        /// </summary>
        public bool TryBegin(VpNativePipeline pipeline, GraphicsBuffer arguments, int firstCommand, int commandCount, GraphicsBuffer indexBuffer)
        {
            if (pipeline == null || pipeline.Id < 0)
            {
                throw new ArgumentException("the issue needs a live pipeline");
            }

            return TryBegin(pipeline.Id, arguments, firstCommand, commandCount, indexBuffer);
        }

        /// <summary>The same, by the plugin's pipeline number (for a test of the layout, which has no plugin).</summary>
        public bool TryBegin(int pipelineId, GraphicsBuffer arguments, int firstCommand, int commandCount, GraphicsBuffer indexBuffer)
        {
            if (arguments == null || indexBuffer == null)
            {
                throw new ArgumentException("the issue needs an argument buffer and an index buffer");
            }

            return TryBegin(pipelineId, arguments.GetNativeBufferPtr(), firstCommand, commandCount, indexBuffer.GetNativeBufferPtr(), (long)indexBuffer.count * indexBuffer.stride, indexBuffer.stride);
        }

        /// <summary>
        /// The same with the native pointers the caller already holds (<see cref="VpNativePointerCache"/>): nothing
        /// is asked of Unity here.
        /// </summary>
        public bool TryBegin(int pipelineId, IntPtr arguments, int firstCommand, int commandCount, IntPtr indexBuffer, long indexBufferBytes, int indexStride)
        {
            ThrowIfDisposed();
            if (pipelineId < 0 || arguments == IntPtr.Zero || indexBuffer == IntPtr.Zero)
            {
                throw new ArgumentException("the issue needs a pipeline number, an argument buffer and an index buffer");
            }

            if (_current != null)
            {
                throw new InvalidOperationException("an issue is open; Finish it (or DropPrepared) before taking another block");
            }

            int found = -1;
            for (int i = 0; i < _blocks.Length; i++)
            {
                int candidate = (_next + i) % _blocks.Length;
                if (IsReleased(candidate))
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                RingFull++;
                _current = null;
                return false;
            }

            if (_state[found] != BlockState.Free)
            {
                // Released while taken (consumed, passed by its sentinel, or the device gone): tallied as it goes.
                NoteReleased(found);
            }

            _next = (found + 1) % _blocks.Length;
            _state[found] = BlockState.Prepared;
            _sentinel[found] = 0;
            _frame[found] = Time.frameCount;
            Taken++;
            _current = (byte*)_blocks[found];
            var header = (Plugin.Draw*)_current;
            *header = default;
            header->version = Plugin.ContractVersion;
            header->pipeline = (uint)pipelineId;
            header->argumentBuffer = (void*)arguments;
            header->argumentOffset = (uint)(firstCommand * Plugin.ArgumentStride);
            header->commandCount = (uint)commandCount;
            header->indexBuffer = (void*)indexBuffer;
            header->indexBufferBytes = (uint)indexBufferBytes;
            header->indexFormat = indexStride == 2 ? Plugin.DxgiR16Uint : Plugin.DxgiR32Uint;
            header->resourcesOffset = (uint)ResourcesOffset;
            header->constantsOffset = (uint)ConstantsOffset;
            header->totalBytes = (uint)_blockBytes;
            _resourceCount = 0;
            _constantsCount = 0;
            _dataUsed = 0;
            return true;
        }

        /// <summary>A viewport (and its scissor, the same rectangle) the commands are drawn in; up to four, each an ExecuteIndirect.</summary>
        public void AddViewport(Rect viewport)
        {
            var header = (Plugin.Draw*)Current();
            if (header->viewportCount >= Plugin.MaxViewports)
            {
                throw new InvalidOperationException("at most " + Plugin.MaxViewports + " viewports");
            }

            var port = (Plugin.Viewport*)(header->viewports + header->viewportCount * sizeof(Plugin.Viewport));
            port->x = viewport.x;
            port->y = viewport.y;
            port->width = viewport.width;
            port->height = viewport.height;
            port->minDepth = 0f;
            port->maxDepth = 1f;
            port->scissorLeft = Mathf.FloorToInt(viewport.xMin);
            port->scissorTop = Mathf.FloorToInt(viewport.yMin);
            port->scissorRight = Mathf.CeilToInt(viewport.xMax);
            port->scissorBottom = Mathf.CeilToInt(viewport.yMax);
            header->viewportCount++;
        }

        public void SetBuffer(int binding, GraphicsBuffer buffer, bool raw, int firstElement, int elementCount)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            SetBuffer(binding, buffer.GetNativeBufferPtr(), buffer.stride, raw, firstElement, elementCount);
        }

        /// <summary>A whole buffer: a raw view in 4-byte words, or a structured view of its own stride.</summary>
        public void SetBuffer(int binding, GraphicsBuffer buffer, bool raw)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            SetBuffer(binding, buffer, raw, 0, raw ? (int)((long)buffer.count * buffer.stride / 4) : buffer.count);
        }

        /// <summary>A whole buffer by a pointer the caller holds: a raw view in 4-byte words, or a structured view of the stride.</summary>
        public void SetBuffer(int binding, IntPtr buffer, int count, int stride, bool raw)
        {
            SetBuffer(binding, buffer, stride, raw, 0, raw ? (int)((long)count * stride / 4) : count);
        }

        public void SetBuffer(int binding, IntPtr buffer, int stride, bool raw, int firstElement, int elementCount)
        {
            if (binding < 0) return;
            if (buffer == IntPtr.Zero) throw new ArgumentNullException(nameof(buffer));
            Plugin.Resource* resource = NextResource();
            resource->binding = (uint)binding;
            resource->kind = raw ? (uint)Plugin.ResourceKind.RawBuffer : (uint)Plugin.ResourceKind.StructuredBuffer;
            resource->resource = (void*)buffer;
            resource->firstElement = (uint)firstElement;
            resource->elementCount = (uint)elementCount;
            resource->stride = (uint)stride;
        }

        public void SetTexture(int binding, Texture texture, uint dxgiFormat, Plugin.TextureKind kind)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            SetTexture(binding, texture.GetNativeTexturePtr(), dxgiFormat, kind);
        }

        public void SetTexture(int binding, IntPtr texture, uint dxgiFormat, Plugin.TextureKind kind)
        {
            if (binding < 0) return;
            if (texture == IntPtr.Zero) throw new ArgumentNullException(nameof(texture));
            Plugin.Resource* resource = NextResource();
            resource->binding = (uint)binding;
            resource->kind = (uint)Plugin.ResourceKind.Texture;
            resource->resource = (void*)texture;
            resource->format = dxgiFormat;
            resource->dimension = (uint)kind;
        }

        /// <summary>A render texture's depth, through its render buffer (the plugin asks Unity for the resource).</summary>
        public void SetDepthRenderBuffer(int binding, RenderBuffer depth, uint dxgiFormat)
        {
            SetDepthRenderBuffer(binding, depth.GetNativeRenderBufferPtr(), dxgiFormat);
        }

        /// <summary>
        /// A render texture's colour (or depth) through its render buffer handle: Unity resolves the handle to the
        /// texture's current resource when the event runs, so a texture made again keeps being drawn with.
        /// </summary>
        public void SetRenderBuffer(int binding, IntPtr renderBuffer, uint dxgiFormat, Plugin.TextureKind kind)
        {
            if (binding < 0) return;
            if (renderBuffer == IntPtr.Zero) throw new ArgumentNullException(nameof(renderBuffer));
            Plugin.Resource* resource = NextResource();
            resource->binding = (uint)binding;
            resource->kind = (uint)Plugin.ResourceKind.RenderBuffer;
            resource->resource = (void*)renderBuffer;
            resource->format = dxgiFormat;
            resource->dimension = (uint)kind;
        }

        public void SetDepthRenderBuffer(int binding, IntPtr depthRenderBuffer, uint dxgiFormat)
        {
            if (binding < 0) return;
            if (depthRenderBuffer == IntPtr.Zero) throw new ArgumentNullException(nameof(depthRenderBuffer));
            Plugin.Resource* resource = NextResource();
            resource->binding = (uint)binding;
            resource->kind = (uint)Plugin.ResourceKind.RenderBuffer;
            resource->resource = (void*)depthRenderBuffer;
            resource->format = dxgiFormat;
            resource->dimension = (uint)Plugin.TextureKind.Tex2D;
        }

        public void SetConstants(int binding, VpNativeConstantBlock block)
        {
            if (binding < 0) return;
            if (block == null) throw new ArgumentNullException(nameof(block));
            var header = (Plugin.Draw*)Current();
            if (_constantsCount >= _constantsCapacity)
            {
                throw new InvalidOperationException("more constant buffers than the block has room for (" + _constantsCapacity + ")");
            }

            if (DataOffset + _dataUsed + block.Bytes.Length > _blockBytes)
            {
                throw new InvalidOperationException("more constant bytes than the block has room for");
            }

            var constants = (Plugin.Constants*)(_current + ConstantsOffset) + _constantsCount;
            constants->binding = (uint)binding;
            constants->offset = (uint)(DataOffset + _dataUsed);
            constants->size = (uint)block.Bytes.Length;
            constants->resource = null;
            constants->resourceOffset = 0;
            fixed (byte* source = block.Bytes)
            {
                Buffer.MemoryCopy(source, _current + DataOffset + _dataUsed, _blockBytes - DataOffset - _dataUsed, block.Bytes.Length);
            }

            _dataUsed += block.Bytes.Length;
            _constantsCount++;
            header->constantsCount = (uint)_constantsCount;
        }

        /// <summary>
        /// A constant buffer the GPU wrote (a compute pass assembled it from what Unity set), bound as it is from
        /// <paramref name="byteOffset"/> (a multiple of 256) in <paramref name="buffer"/>; nothing is copied.
        /// </summary>
        public void SetGpuConstants(int binding, GraphicsBuffer buffer, int byteOffset)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            SetGpuConstants(binding, buffer.GetNativeBufferPtr(), byteOffset);
        }

        public void SetGpuConstants(int binding, IntPtr buffer, int byteOffset)
        {
            if (binding < 0) return;
            if (buffer == IntPtr.Zero) throw new ArgumentNullException(nameof(buffer));
            if (byteOffset < 0 || (byteOffset % 256) != 0) throw new ArgumentOutOfRangeException(nameof(byteOffset), "a multiple of 256");
            var header = (Plugin.Draw*)Current();
            if (_constantsCount >= _constantsCapacity)
            {
                throw new InvalidOperationException("more constant buffers than the block has room for (" + _constantsCapacity + ")");
            }

            var constants = (Plugin.Constants*)(_current + ConstantsOffset) + _constantsCount;
            constants->binding = (uint)binding;
            constants->offset = 0;
            constants->size = 0;
            constants->resource = (void*)buffer;
            constants->resourceOffset = (uint)byteOffset;
            _constantsCount++;
            header->constantsCount = (uint)_constantsCount;
        }

        /// <summary>
        /// Closes the issue: the block is <see cref="BlockState.Prepared"/> -- in no command buffer yet -- until
        /// <see cref="RecordPrepared"/> writes its event into the pass's command buffer (or <see cref="DropPrepared"/>
        /// frees it, the pass having ended without recording). The pointer is what the event is issued with.
        /// </summary>
        public IntPtr Finish(out uint serial)
        {
            var header = (Plugin.Draw*)Current();
            if (header->viewportCount == 0)
            {
                throw new InvalidOperationException("an issue needs a viewport");
            }

            header->resourceCount = (uint)_resourceCount;
            header->constantsCount = (uint)_constantsCount;
            header->serial = ++_serial;
            header->result = 0;
            header->consumed = 0;
            serial = header->serial;
            IntPtr block = (IntPtr)_current;
            int index = IndexOf(block);
            _state[index] = BlockState.Prepared;
            _sentinel[index] = 0;
            _prepared.Add(index);
            _current = null;
            VpNativeDrawRelease.Note(this, "prepared", index, serial);
            return block;
        }

        /// <summary>
        /// Records the event of every Prepared block, in the order they were prepared, into <paramref name="commands"/>
        /// -- the pass's command buffer, at the end of its Execute -- and makes them <see cref="BlockState.Recorded"/>.
        /// Null commands record nowhere (a test's stand-in) but move the state the same. Returns how many.
        /// </summary>
        public int RecordPrepared(CommandBuffer commands, IntPtr renderEventFunction, int eventId)
        {
            ThrowIfDisposed();
            int n = _prepared.Count;
            for (int i = 0; i < n; i++)
            {
                int index = _prepared[i];
                if (commands != null)
                {
                    commands.IssuePluginEventAndData(renderEventFunction, eventId, _blocks[index]);
                }

                _state[index] = BlockState.Recorded;
                VpNativeDrawRelease.Note(this, "recorded", index, ((Plugin.Draw*)_blocks[index])->serial);
            }

            _prepared.Clear();
            return n;
        }

        /// <summary>
        /// Frees every Prepared block (and abandons an open issue): a pass's Execute ended without recording them, so
        /// they were in no command buffer and no event of theirs exists. Called at the start of the next Execute, at
        /// the end of a camera's rendering (no Execute is in progress then) and at the route's dispose. Returns how many.
        /// </summary>
        public int DropPrepared()
        {
            if (_disposed) return 0;
            int n = 0;
            if (_current != null)
            {
                int open = IndexOf((IntPtr)_current);
                _state[open] = BlockState.Free;
                _current = null;
                n++;
            }

            for (int i = 0; i < _prepared.Count; i++)
            {
                int index = _prepared[i];
                if (_state[index] == BlockState.Prepared)
                {
                    _state[index] = BlockState.Free;
                    n++;
                    VpNativeDrawRelease.Note(this, "never recorded, freed", index, ((Plugin.Draw*)_blocks[index])->serial);
                }
            }

            _prepared.Clear();
            NeverRecorded += n;
            return n;
        }

        /// <summary>
        /// The camera's rendering has ended (its commands were submitted to the context, or cleared and never will be)
        /// and the keeper issues sentinel <paramref name="sentinel"/> behind it through the same context: every
        /// Recorded block becomes <see cref="BlockState.Submitted"/> under that sentinel. Returns how many.
        /// </summary>
        public int SubmitRecorded(ulong sentinel)
        {
            if (_disposed) return 0;
            int n = 0;
            for (int i = 0; i < _blocks.Length; i++)
            {
                // A Recorded block already consumed by its own mark needs no sentinel (natL1, 2026-10-08: 64 "submitted"
                // of which 49 were consumed; the count must say what is still with the plugin).
                if (_state[i] != BlockState.Recorded || IsReleased(i)) continue;
                _state[i] = BlockState.Submitted;
                _sentinel[i] = sentinel;
                n++;
                VpNativeDrawRelease.Note(this, "submitted under sentinel " + sentinel, i, ((Plugin.Draw*)_blocks[i])->serial);
            }

            return n;
        }

        /// <summary>Whether a Recorded block is not yet consumed: what a sentinel would settle, and what the keeper issues one for.</summary>
        public bool HasRecordedUnconsumed
        {
            get
            {
                for (int i = 0; i < _blocks.Length; i++)
                {
                    if (_state[i] == BlockState.Recorded && !IsReleased(i)) return true;
                }

                return false;
            }
        }

        /// <summary>The serial of the latest sentinel consumed, as the keeper read it: what the release of Submitted blocks without their own mark is judged by.</summary>
        public void SetConsumedSentinel(ulong serial)
        {
            if (serial > _consumedSentinel) _consumedSentinel = serial;
        }

        /// <summary>The device is gone: the plugin touches no block any more, so every block is released.</summary>
        public void DeviceGone()
        {
            _deviceGone = true;
        }

        /// <summary>The serial of the last issue closed (0 before any).</summary>
        public uint LastSerial => _serial;

        public bool IsDisposed => _disposed;

        /// <summary>The state of the block of <paramref name="serial"/> (Free when released, reused or never taken).</summary>
        public BlockState StateOf(uint serial)
        {
            for (int i = 0; i < _blocks.Length; i++)
            {
                if (_state[i] != BlockState.Free && ((Plugin.Draw*)_blocks[i])->serial == serial) return IsReleased(i) ? BlockState.Free : _state[i];
            }

            return BlockState.Free;
        }

        /// <summary>
        /// True when no block taken up to <paramref name="serial"/> can still be read by the rendering thread: what a
        /// buffer named by the events up to then waits on.
        /// </summary>
        public bool AllReleasedUpTo(uint serial, ulong consumedSentinel)
        {
            SetConsumedSentinel(consumedSentinel);
            for (int i = 0; i < _blocks.Length; i++)
            {
                if (_state[i] != BlockState.Free && ((Plugin.Draw*)_blocks[i])->serial <= serial && !IsReleased(i)) return false;
            }

            return true;
        }

        /// <summary>How many blocks may still be read by the rendering thread: taken, not consumed, not settled by a sentinel.</summary>
        public int InFlight
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _blocks.Length; i++)
                {
                    if (_state[i] != BlockState.Free && !IsReleased(i)) n++;
                }

                return n;
            }
        }

        /// <summary>How many of the blocks in flight are in <paramref name="state"/>.</summary>
        public int InFlightIn(BlockState state)
        {
            int n = 0;
            for (int i = 0; i < _blocks.Length; i++)
            {
                if (_state[i] == state && !IsReleased(i)) n++;
            }

            return n;
        }

        /// <summary>The ring's capacity in blocks.</summary>
        public int Blocks => _blocks.Length;

        /// <summary>
        /// True when no block can still be read by the rendering thread: every taken block was consumed by the plugin,
        /// or is Submitted under a sentinel that was consumed (<paramref name="consumedSentinel"/>), or the device is
        /// gone. The condition for freeing the blocks.
        /// </summary>
        public bool AllReleased(ulong consumedSentinel)
        {
            SetConsumedSentinel(consumedSentinel);
            return InFlight == 0;
        }

        private bool IsReleased(int index)
        {
            if (_state[index] == BlockState.Free || _deviceGone) return true;
            var draw = (Plugin.Draw*)_blocks[index];
            if (draw->consumed != 0) return true;
            return _state[index] == BlockState.Submitted && _sentinel[index] != 0 && _consumedSentinel >= _sentinel[index];
        }

        // The tally of how a released block was released, taken when its slot is reused (for the record only).
        private void NoteReleased(int index)
        {
            var draw = (Plugin.Draw*)_blocks[index];
            if (draw->consumed != 0)
            {
                VpNativeDrawRelease.Note(this, "consumed, slot reused", index, draw->serial);
            }
            else if (_deviceGone)
            {
                VpNativeDrawRelease.Note(this, "device gone, slot reused", index, draw->serial);
            }
            else
            {
                DiscardsConfirmed++;
                VpNativeDrawRelease.Note(this, "discard confirmed by sentinel " + _sentinel[index] + ", slot reused", index, draw->serial);
            }
        }

        /// <summary>Whether the block of <paramref name="serial"/> is Submitted and was settled by a consumed sentinel without its own mark (its command never ran).</summary>
        public bool IsDiscardConfirmed(uint serial)
        {
            for (int i = 0; i < _blocks.Length; i++)
            {
                if (_state[i] == BlockState.Submitted && ((Plugin.Draw*)_blocks[i])->serial == serial)
                {
                    return ((Plugin.Draw*)_blocks[i])->consumed == 0 && _sentinel[i] != 0 && _consumedSentinel >= _sentinel[i];
                }
            }

            return false;
        }

        private int IndexOf(IntPtr block)
        {
            for (int i = 0; i < _blocks.Length; i++)
            {
                if (_blocks[i] == block) return i;
            }

            throw new ArgumentException("not a block of this ring");
        }

        /// <summary>What the plugin answered for a block: null while it has not been handled yet.</summary>
        public static Plugin.Result? ResultOf(IntPtr block)
        {
            var header = (Plugin.Draw*)block;
            return header->consumed != 0 ? (Plugin.Result?)(Plugin.Result)header->result : null;
        }

        public static uint SerialOf(IntPtr block)
        {
            return ((Plugin.Draw*)block)->serial;
        }

        private Plugin.Resource* NextResource()
        {
            var header = (Plugin.Draw*)Current();
            if (_resourceCount >= _resourceCapacity)
            {
                throw new InvalidOperationException("more resources than the block has room for (" + _resourceCapacity + ")");
            }

            var resource = (Plugin.Resource*)(_current + ResourcesOffset) + _resourceCount;
            *resource = default;
            _resourceCount++;
            header->resourceCount = (uint)_resourceCount;
            return resource;
        }

        private byte* Current()
        {
            ThrowIfDisposed();
            if (_current == null)
            {
                throw new InvalidOperationException("no issue is open; call TryBegin first");
            }

            return _current;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpNativeDrawData));
            }
        }

        /// <summary>
        /// Frees the blocks. The plugin reads them on the rendering thread, so the owner frees a ring only once
        /// <see cref="AllReleased"/> says so (<see cref="VpNativeDrawRelease"/> keeps a disposed route's ring until
        /// then); freeing earlier is the use-after-free this guards against.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current = null;
            for (int i = 0; i < _blocks.Length; i++)
            {
                Marshal.FreeHGlobal(_blocks[i]);
                _blocks[i] = IntPtr.Zero;
            }
        }
    }
}
