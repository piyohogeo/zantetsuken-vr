using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The stencil and provisional cap draw of DESIGN 5.2 and 5.6, for a fixed set of stencil colours: the stencil byte
    /// set to 128, the volumes counted into it, and the caps drawn where the count came out positive — in that order,
    /// once per colour.
    /// <para>
    /// **One upload for every colour at once.** The volume commands, their transforms and their clip records go across
    /// in the one upload the indexed indirect batch already does; every colour's cap vertices go into one common vertex
    /// buffer and every colour's cap indices into one common index buffer. A colour then owns nothing but a start and a
    /// count (<see cref="VpStencilCapColor"/>). Every write happens in <see cref="TryUpload"/>, before any draw is
    /// registered: nothing is written to a buffer while the colours are being drawn, and a colour is never the occasion
    /// for a write of its own. <see cref="Uploads"/> counts the calls and <see cref="BufferWrites"/> the writes those
    /// calls actually made, which are not the same number.
    /// </para>
    /// <para>
    /// **One CPU draw per colour per kind.** A colour's volumes, however many commands they are, are issued as a single
    /// <c>RenderPrimitivesIndexedIndirect</c> over that colour's command range — one call on the CPU, one GPU draw per
    /// command inside it. A colour's caps, however many polygons and however many bodies they came from, are one
    /// indexed draw over that colour's index range. The stencil initialisation is a third call, counted apart. There is
    /// no loop over records, bodies or submeshes issuing draws.
    /// </para>
    /// <para>
    /// **The order, and what it rests on.** Within a colour the order is init, then every volume, then every cap, and
    /// the colours follow one another; each of the three draws of each colour has its own render queue value, in that
    /// sequence, so the pipeline draws them in that sequence inside its opaque pass, against the camera's own depth and
    /// stencil attachment. This is the choice made for this fixed-input confirmation, not the only arrangement that
    /// could work.
    /// </para>
    /// <para>
    /// **One batch per camera.** Because the order is the queue, two batches registering draws for the same camera
    /// would interleave at equal queues and their initialisations, volumes and caps would run into one another. This is
    /// for a single aggregate batch per camera, and nothing here reserves or arbitrates queues for anyone else.
    /// </para>
    /// <para>
    /// **What it does not touch.** The volumes reference the shared geometry's own vertex and index buffers; no stencil
    /// copy of a geometry is made and no winding is reversed for one. The caps write no stencil. The initialisation
    /// writes neither colour nor depth. Fixed capacity, whole-buffer writes, main thread only.
    /// </para>
    /// <para>
    /// **A GPU call that throws stops this batch.** What reached the GPU cannot then be established, so new and old
    /// data could be drawn together; the batch refuses to upload or draw again and only <see cref="Dispose"/> is left,
    /// which still gives back everything it owns. This is the same stopping rule the display path uses.
    /// </para>
    /// </summary>
    public sealed class VpStencilCapBatch : IDisposable
    {
        /// <summary>The base value of DESIGN 5.6: the stencil byte is set to this, and a cap draws above it.</summary>
        public const int StencilBase = 128;

        private static readonly int CapVerticesId = Shader.PropertyToID("_VpCapVertices");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpInstanceMultiplier");

        private readonly VpIndexedIndirectDrawBatch _volumes;
        private readonly GraphicsBuffer _capVertexBuffer;
        private readonly GraphicsBuffer _capIndexBuffer;
        private readonly MaterialPropertyBlock _volumeProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _capProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _initProperties = new MaterialPropertyBlock();
        private readonly VpStencilCapColor[] _colors;

        // The cap vertices are sent as float4, so they are staged: in a managed array, or -- for a batch made with a page
        // backing -- in a room on reserved address space made and written at construction. The indices are staged only
        // for an array upload; a native upload sends the caller's own.
        private Vector4[] _capVertices;
        private uint[] _capIndices;
        private readonly VpNumericRoom<Vector4> _capVertexStaging;
        private bool _uploaded;
        private int _colorCount;
        private bool _broken;
        private bool _disposed;

        /// <summary>
        /// The colours, the commands and the cap polygons this can hold. Capacity is fixed: this lane writes everything
        /// every frame and grows, retires and compacts nothing.
        /// <para>
        /// Everything acquired is given back if a later step of the construction fails, so a half-built batch never
        /// leaves a buffer or the volume batch behind.
        /// </para>
        /// </summary>
        public VpStencilCapBatch(int colorCapacity, int commandCapacity, int instanceCapacity, int capVertexCapacity, int capIndexCapacity)
            : this(colorCapacity, commandCapacity, instanceCapacity, capVertexCapacity, capIndexCapacity, null)
        {
        }

        /// <summary>
        /// The same batch with its staging on <paramref name="stagingBacking"/> (reserved and committed whole here, every
        /// page written), for an owner that uploads native views; null keeps the staging in managed arrays.
        /// </summary>
        /// <exception cref="InvalidOperationException">The backing refused the staging's room.</exception>
        public VpStencilCapBatch(
            int colorCapacity, int commandCapacity, int instanceCapacity, int capVertexCapacity, int capIndexCapacity,
            IVpPageBacking stagingBacking)
        {
            if (colorCapacity <= 0 || capVertexCapacity <= 0 || capIndexCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(colorCapacity));
            }

            // The plain arrays first, so that nothing holding a GPU resource can be lost to an allocation failure
            // afterwards. From the first GPU resource onwards everything is inside one recovery.
            _colors = new VpStencilCapColor[colorCapacity];
            VpNumericRoom<Vector4> capVertexStaging = null;
            if (stagingBacking == null)
            {
                _capVertices = new Vector4[capVertexCapacity];
                _capIndices = new uint[capIndexCapacity];
            }
            else if (!VpNumericRoom<Vector4>.TryCreateNative(
                         stagingBacking, capVertexCapacity, capVertexCapacity, out capVertexStaging, out string failure))
            {
                throw new InvalidOperationException("the batch's staging could not be made: " + failure);
            }

            VpIndexedIndirectDrawBatch volumes = null;
            GraphicsBuffer capVertexBuffer = null;
            GraphicsBuffer capIndexBuffer = null;
            try
            {
                volumes = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity, stagingBacking);
                capVertexBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, capVertexCapacity, sizeof(float) * 4);
                capVertexBuffer.name = "VP Stencil Cap Vertices";
                capIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, capIndexCapacity, sizeof(uint));
                capIndexBuffer.name = "VP Stencil Cap Indices";

                ColorCapacity = colorCapacity;
                CapVertexCapacity = capVertexCapacity;
                CapIndexCapacity = capIndexCapacity;
                _volumes = volumes;
                _capVertexBuffer = capVertexBuffer;
                _capIndexBuffer = capIndexBuffer;
                _capVertexStaging = capVertexStaging;
            }
            catch
            {
                capIndexBuffer?.Dispose();
                capVertexBuffer?.Dispose();
                volumes?.Dispose();
                capVertexStaging?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Writes every GPU buffer of this batch once, whole, with zeros, so that each stands on the device before the
        /// first upload of a frame. Only before the first upload; nothing uploaded and no count changes.
        /// </summary>
        /// <exception cref="InvalidOperationException">Something was uploaded already.</exception>
        public void WriteWholeOnce()
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (_uploaded)
            {
                throw new InvalidOperationException("the buffers are written whole only before the first upload");
            }

            _volumes.WriteWholeOnce();

            // Each array is taken inside the protection: one that cannot be had, or a write that throws, leaves none of
            // the others behind.
            NativeArray<Vector4> vertices = default;
            NativeArray<uint> indices = default;
            try
            {
                vertices = VpWholeWrite.Zeros<Vector4>("cap vertices", CapVertexCapacity);
                indices = VpWholeWrite.Zeros<uint>("cap indices", CapIndexCapacity);
                VpWholeWrite.Step("write cap vertices");
                _capVertexBuffer.SetData(vertices, 0, 0, CapVertexCapacity);
                VpWholeWrite.Step("write cap indices");
                _capIndexBuffer.SetData(indices, 0, 0, CapIndexCapacity);
            }
            finally
            {
                VpWholeWrite.Release(ref vertices);
                VpWholeWrite.Release(ref indices);
            }
        }

        /// <summary>The bytes of this batch's GPU buffers: the volumes' four, the cap vertices and the cap indices.</summary>
        public long GpuBytes => _volumes.GpuBytes + ((long)CapVertexCapacity * sizeof(float) * 4) + ((long)CapIndexCapacity * sizeof(uint));

        /// <summary>What this batch's staging is made of, in bytes.</summary>
        public void DescribeStaging(System.Collections.Generic.List<VpRoomLine> into, string owner)
        {
            _volumes.DescribeStaging(into, owner + ".volumes");
            if (_capVertexStaging != null) into.Add(VpRoomLine.Of(owner + ".capVertices", _capVertexStaging));
            if (_capVertices != null) into.Add(VpRoomLine.OfManaged(owner + ".capVertices", _capVertices));
            if (_capIndices != null) into.Add(VpRoomLine.OfManaged(owner + ".capIndices", _capIndices));
            into.Add(VpRoomLine.OfManaged(owner + ".colours", _colors));
        }

        /// <summary>
        /// The counted upload from native views: the first <paramref name="commandCount"/> volume commands and the
        /// instances they name, the first <paramref name="capVertexCount"/> cap vertices and the first
        /// <paramref name="capIndexCount"/> cap indices. The views are the valid part of their owner's room and may be
        /// longer. The transforms, the clips and the indices go to the GPU from the views themselves; the cap vertices
        /// are made float4 in this batch's staging. Accepted and refused as the array upload is, by the same judgement.
        /// </summary>
        public bool TryUpload(
            NativeArray<VpIndirectCommand> commands,
            int commandCount,
            NativeArray<Matrix4x4> objectToWorlds,
            NativeArray<VpInstanceClip> clips,
            NativeArray<Vector3> capVertices,
            int capVertexCount,
            NativeArray<int> capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            bool singlePassInstanced)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (!commands.IsCreated || !objectToWorlds.IsCreated || !clips.IsCreated || !capVertices.IsCreated || !capIndices.IsCreated)
            {
                return false;
            }

            if (!IsWellFormed(
                    commands.AsReadOnlySpan(), commandCount, capVertices.Length, capVertexCount, capIndices.AsReadOnlySpan(),
                    capIndexCount, colors, colorCount, out int volumeWrites))
            {
                return false;
            }

            try
            {
                if (!_volumes.TryUpload(commands, commandCount, objectToWorlds, clips, singlePassInstanced))
                {
                    return false;
                }

                BufferWrites += volumeWrites;
                if (capVertexCount > 0)
                {
                    if (_capVertexStaging != null)
                    {
                        Span<Vector4> staged = _capVertexStaging.AsSpan(0, capVertexCount);
                        for (int i = 0; i < capVertexCount; i++)
                        {
                            Vector3 vertex = capVertices[i];
                            staged[i] = new Vector4(vertex.x, vertex.y, vertex.z, 1f);
                        }

                        _capVertexBuffer.SetData(_capVertexStaging.First(capVertexCount), 0, 0, capVertexCount);
                    }
                    else
                    {
                        for (int i = 0; i < capVertexCount; i++)
                        {
                            Vector3 vertex = capVertices[i];
                            _capVertices[i] = new Vector4(vertex.x, vertex.y, vertex.z, 1f);
                        }

                        _capVertexBuffer.SetData(_capVertices, 0, 0, capVertexCount);
                    }

                    BufferWrites++;
                }

                if (capIndexCount > 0)
                {
                    // The indices were checked to be within the vertices, so none is negative: read as unsigned as they stand.
                    _capIndexBuffer.SetData(capIndices.Reinterpret<uint>(), 0, 0, capIndexCount);
                    BufferWrites++;
                }
            }
            catch
            {
                _broken = true;
                throw;
            }

            Array.Copy(colors, _colors, colorCount);
            _colorCount = colorCount;
            SinglePassInstanced = singlePassInstanced;
            _uploaded = true;
            Uploads++;
            return true;
        }

        /// <summary>Whether the native upload would accept this input, decided without writing anything and by the same judgement.</summary>
        public bool CanUpload(
            NativeArray<VpIndirectCommand> commands,
            int commandCount,
            NativeArray<Matrix4x4> objectToWorlds,
            NativeArray<VpInstanceClip> clips,
            NativeArray<Vector3> capVertices,
            int capVertexCount,
            NativeArray<int> capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (!commands.IsCreated || !objectToWorlds.IsCreated || !clips.IsCreated || !capVertices.IsCreated || !capIndices.IsCreated)
            {
                return false;
            }

            return IsWellFormed(
                       commands.AsReadOnlySpan(), commandCount, capVertices.Length, capVertexCount, capIndices.AsReadOnlySpan(),
                       capIndexCount, colors, colorCount, out _)
                   && _volumes.CanUpload(commands, commandCount, objectToWorlds, clips);
        }

        public int ColorCapacity { get; }

        public int CapVertexCapacity { get; }

        public int CapIndexCapacity { get; }

        /// <summary>
        /// A buffer of this batch for an owner that replaces it to read back asynchronously: once that readback has
        /// completed, the GPU is past every draw that was issued from this batch before it was asked for.
        /// </summary>
        public GraphicsBuffer RetirementFence => _capVertexBuffer;

        /// <summary>The volume commands this batch can hold.</summary>
        public int CommandCapacity => _volumes.CommandCapacity;

        /// <summary>The volume instances this batch can hold.</summary>
        public int InstanceCapacity => _volumes.InstanceCapacity;

        /// <summary>How many colours the last upload settled.</summary>
        public int ColorCount => _colorCount;

        /// <summary>
        /// How many uploads succeeded: the calls, not the writes. One of these settles every colour at once.
        /// </summary>
        public int Uploads { get; private set; }

        /// <summary>
        /// How many buffer writes the uploads so far made. An ordinary upload with volumes and caps is six: the volume
        /// side writes its forward arguments, its shadow arguments, its instance transforms and its clip records, and
        /// the caps write their vertices and their indices. Input with no caps is four, and input with neither is
        /// none. Drawing adds nothing.
        /// <para>
        /// **The two cap writes are counted where they are made; the volume side's four are worked out.** That side's
        /// writes happen inside <see cref="VpIndexedIndirectDrawBatch"/>, which does not report them, so this counts
        /// what its rule gives for the input it was handed — two when there is a command and two more when there is an
        /// instance — and adds them once that upload has succeeded. For an upload that succeeds the number is the
        /// number of writes; for one interrupted partway through, it is not, and nothing here claims otherwise. What it
        /// is for is the question this lane actually asks: that drawing writes nothing and that a colour is never the
        /// occasion for a write.
        /// </para>
        /// </summary>
        public int BufferWrites { get; private set; }

        /// <summary>Stencil initialisation draws issued on the CPU: one per colour drawn.</summary>
        public int StencilInitIssues { get; private set; }

        /// <summary>Volume draws issued on the CPU: one per colour that has any, whatever its command count.</summary>
        public int VolumeIssues { get; private set; }

        /// <summary>Cap draws issued on the CPU: one per colour that has any, whatever its polygon count.</summary>
        public int CapIssues { get; private set; }

        /// <summary>
        /// The GPU draws the volume issues stand for — one per command — which is not the same number as
        /// <see cref="VolumeIssues"/> and is reported apart from it.
        /// </summary>
        public int VolumeGpuDraws { get; private set; }

        /// <summary>
        /// Whether the last upload was for Single Pass Instanced stereo. Then every draw this batch issues stands for
        /// both eyes: the volumes' arguments carry twice the logical instances, and the initialisation and the caps
        /// are issued as two instances, one per eye, with the shaders taking the eye from the instance id. The value
        /// is fixed by the upload, as it is for the display path, and read by <see cref="Render"/>.
        /// </summary>
        public bool SinglePassInstanced { get; private set; }

        /// <summary>Whether a GPU call threw, after which this batch neither uploads nor draws again.</summary>
        public bool IsBroken => _broken;

        /// <summary>The colour ranges of the last upload, read back for confirmation.</summary>
        public bool TryGetColor(int index, out VpStencilCapColor color)
        {
            if (index < 0 || index >= _colorCount)
            {
                color = default;
                return false;
            }

            color = _colors[index];
            return true;
        }

        /// <summary>
        /// Puts every colour's volumes and caps on the GPU and settles the ranges each colour will be drawn from.
        /// Nothing is drawn here and nothing is written anywhere else.
        /// <para>
        /// <paramref name="capVertices"/> are world positions, at the placement the side is drawn at, and
        /// <paramref name="capIndices"/> index them globally, so that one index range is one colour's caps whichever
        /// polygons and bodies they came from. Every check is made before the first write, so input that does not fit
        /// the fixed capacity or does not lie inside what was given leaves the previous colours and the previous
        /// drawing exactly as they were.
        /// </para>
        /// </summary>
        public bool TryUpload(
            VpIndirectCommand[] commands,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount)
        {
            return TryUpload(
                commands, objectToWorlds, clips, capVertices, capVertexCount, capIndices, capIndexCount, colors,
                colorCount, false);
        }

        /// <summary>
        /// The same, saying whether the draws are for Single Pass Instanced stereo. This is the only place the flag
        /// enters: the volume batch doubles its instance arguments on it, and <see cref="Render"/> reads it back for
        /// the initialisation and the caps. Only the volume batch's forward arguments are doubled — its shadow
        /// arguments keep the logical count, and this path issues no shadow draw anyway. The display path passes what
        /// its own caller asked for, so that the body's surfaces and this batch's work share one condition; the plain
        /// overload above keeps monoscopic callers as they were.
        /// </summary>
        public bool TryUpload(
            VpIndirectCommand[] commands,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            bool singlePassInstanced)
        {
            return Upload(
                commands, commands == null ? 0 : commands.Length, objectToWorlds, clips, capVertices, capVertexCount,
                capIndices, capIndexCount, colors, colorCount, singlePassInstanced, true);
        }

        /// <summary>
        /// The same upload of only the first <paramref name="commandCount"/> volume commands and the instances they
        /// name -- the first transforms and clip records -- together with the first <paramref name="capVertexCount"/>
        /// cap vertices, the first <paramref name="capIndexCount"/> cap indices and the first
        /// <paramref name="colorCount"/> colours. The arrays may be longer; what lies past those counts is neither
        /// checked nor transferred nor drawn, so a caller keeping fixed-size arrays filled to a count each time never
        /// sends what an earlier, longer fill left behind. Accepted and refused exactly as
        /// <see cref="CanUpload(VpIndirectCommand[], int, Matrix4x4[], VpInstanceClip[], Vector3[], int, int[], int, VpStencilCapColor[], int)"/>
        /// says, before the first write.
        /// </summary>
        public bool TryUpload(
            VpIndirectCommand[] commands,
            int commandCount,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            bool singlePassInstanced)
        {
            return Upload(
                commands, commandCount, objectToWorlds, clips, capVertices, capVertexCount, capIndices, capIndexCount,
                colors, colorCount, singlePassInstanced, false);
        }

        private bool Upload(
            VpIndirectCommand[] commands,
            int commandCount,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            bool singlePassInstanced,
            bool exactLengths)
        {
            ThrowIfDisposed();
            ThrowIfBroken();

            if (!IsWellFormed(
                    commands, commandCount, capVertices, capVertexCount, capIndices, capIndexCount, colors, colorCount,
                    out int volumeWrites))
            {
                return false;
            }

            // Every check is behind us; from here the buffers are written.
            try
            {
                VpIndirectCommand[] volumeCommands = commands ?? Array.Empty<VpIndirectCommand>();
                bool volumesTaken = exactLengths
                    ? _volumes.TryUpload(volumeCommands, objectToWorlds, clips, singlePassInstanced)
                    : _volumes.TryUpload(volumeCommands, commandCount, objectToWorlds, clips, singlePassInstanced);
                if (!volumesTaken)
                {
                    return false;
                }

                BufferWrites += volumeWrites;

                // An array upload stages in managed arrays; a batch made for native uploads makes them only if asked this way.
                if (_capVertices == null)
                {
                    _capVertices = new Vector4[CapVertexCapacity];
                }

                if (_capIndices == null)
                {
                    _capIndices = new uint[CapIndexCapacity];
                }

                for (int i = 0; i < capVertexCount; i++)
                {
                    Vector3 vertex = capVertices[i];
                    _capVertices[i] = new Vector4(vertex.x, vertex.y, vertex.z, 1f);
                }

                for (int i = 0; i < capIndexCount; i++)
                {
                    _capIndices[i] = (uint)capIndices[i];
                }

                if (capVertexCount > 0)
                {
                    _capVertexBuffer.SetData(_capVertices, 0, 0, capVertexCount);
                    BufferWrites++;
                }

                if (capIndexCount > 0)
                {
                    _capIndexBuffer.SetData(_capIndices, 0, 0, capIndexCount);
                    BufferWrites++;
                }
            }
            catch
            {
                // Part of this arrangement may be on the GPU and part not, and which cannot be established, so this
                // batch draws no more rather than showing the two together.
                _broken = true;
                throw;
            }

            Array.Copy(colors, _colors, colorCount);
            _colorCount = colorCount;
            SinglePassInstanced = singlePassInstanced;
            _uploaded = true;
            Uploads++;
            return true;
        }

        /// <summary>
        /// Whether <see cref="TryUpload(VpIndirectCommand[], Matrix4x4[], VpInstanceClip[], Vector3[], int, int[], int, VpStencilCapColor[], int, bool)"/>
        /// would accept this input, decided without writing anything. It is the same judgement the upload makes
        /// before its first write, offered on its own so that a caller feeding two batches from one arrangement --
        /// the display path's forward batch and this one -- can settle both refusals before either is written, and
        /// never leave the body new and its caps old. The volume side's own capacity is part of the judgement.
        /// <para>
        /// The input arrays here are the ones that would be uploaded; <paramref name="objectToWorlds"/> and
        /// <paramref name="clips"/> are checked for the lengths the volume batch requires.
        /// </para>
        /// </summary>
        public bool CanUpload(
            VpIndirectCommand[] commands,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount)
        {
            return Can(
                commands, commands == null ? 0 : commands.Length, objectToWorlds, clips, capVertices, capVertexCount,
                capIndices, capIndexCount, colors, colorCount, true);
        }

        /// <summary>
        /// Whether the counted <see cref="TryUpload(VpIndirectCommand[], int, Matrix4x4[], VpInstanceClip[], Vector3[], int, int[], int, VpStencilCapColor[], int, bool)"/>
        /// would accept this input, decided without writing anything: the same counts, read over the same ranges.
        /// </summary>
        public bool CanUpload(
            VpIndirectCommand[] commands,
            int commandCount,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount)
        {
            return Can(
                commands, commandCount, objectToWorlds, clips, capVertices, capVertexCount, capIndices, capIndexCount,
                colors, colorCount, false);
        }

        /// <summary>
        /// The colour to hand the cap material so that the shader reads the colour the caller named. A material's Color
        /// property is converted from gamma to linear when the project renders in linear space, while the cut surface
        /// colour of DESIGN 5.3 reaches the body's shader as a global constant, unconverted. Undoing the conversion here
        /// is what lets one named colour shade a temporary cut face and a real one alike. In gamma space nothing is
        /// converted either way, and 0 and 1 are the same under both, so a caller naming black, white or a pure channel
        /// sees no difference.
        /// </summary>
        private static Color AsShaderColour(Color colour)
        {
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? colour.gamma : colour;
        }

        private bool Can(
            VpIndirectCommand[] commands,
            int commandCount,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            bool exactLengths)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (!IsWellFormed(
                    commands, commandCount, capVertices, capVertexCount, capIndices, capIndexCount, colors, colorCount,
                    out _))
            {
                return false;
            }

            // The volume half is the draw batch's own judgement, asked of it rather than repeated here, so the two
            // cannot drift apart: a negative index range, a count over the capacity or a transform or clip count
            // that does not match the instances is refused by the same code that would refuse the upload.
            VpIndirectCommand[] volumeCommands = commands ?? Array.Empty<VpIndirectCommand>();
            return exactLengths
                ? _volumes.CanUpload(volumeCommands, objectToWorlds, clips)
                : _volumes.CanUpload(volumeCommands, commandCount, objectToWorlds, clips);
        }

        /// <summary>
        /// Whether the input can be uploaded at all, and how many writes the volume side of it will make. Decided
        /// before anything is written, and with no addition that could carry past what an int holds: a start is
        /// compared against what is left rather than added to a count.
        /// </summary>
        private bool IsWellFormed(
            VpIndirectCommand[] commands,
            int commandCount,
            Vector3[] capVertices,
            int capVertexCount,
            int[] capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            out int volumeWrites)
        {
            volumeWrites = 0;
            if (capVertices == null || capIndices == null)
            {
                return false;
            }

            return IsWellFormed(
                new ReadOnlySpan<VpIndirectCommand>(commands), commandCount, capVertices.Length, capVertexCount,
                new ReadOnlySpan<int>(capIndices), capIndexCount, colors, colorCount, out volumeWrites);
        }

        // The same judgement for an array upload and a native one: the commands and the indices as they stand, and how
        // many cap vertices were handed over.
        private bool IsWellFormed(
            ReadOnlySpan<VpIndirectCommand> commands,
            int commandCount,
            int capVertexLength,
            int capVertexCount,
            ReadOnlySpan<int> capIndices,
            int capIndexCount,
            VpStencilCapColor[] colors,
            int colorCount,
            out int volumeWrites)
        {
            volumeWrites = 0;
            if (colors == null)
            {
                return false;
            }

            if (colorCount < 0 || colorCount > ColorCapacity || colorCount > colors.Length)
            {
                return false;
            }

            if (capVertexCount < 0 || capVertexCount > CapVertexCapacity || capVertexCount > capVertexLength
                || capIndexCount < 0 || capIndexCount > CapIndexCapacity || capIndexCount > capIndices.Length)
            {
                return false;
            }

            for (int i = 0; i < capIndexCount; i++)
            {
                if (capIndices[i] < 0 || capIndices[i] >= capVertexCount)
                {
                    return false;
                }
            }

            // Only the commands counted are read; a null array holds none.
            if (commandCount < 0 || commandCount > commands.Length)
            {
                return false;
            }

            int commandTotal = commandCount;
            long instanceTotal = 0;
            for (int c = 0; c < commandTotal; c++)
            {
                if (commands[c].instanceCount < 0)
                {
                    return false;
                }

                instanceTotal += commands[c].instanceCount;
            }

            for (int c = 0; c < colorCount; c++)
            {
                VpStencilCapColor color = colors[c];
                if (color.volumeStart < 0 || color.volumeCount < 0
                    || color.volumeStart > commandTotal - color.volumeCount
                    || color.capIndexStart < 0 || color.capIndexCount < 0
                    || color.capIndexStart > capIndexCount - color.capIndexCount
                    || color.capIndexCount % 3 != 0)
                {
                    return false;
                }
            }

            // What the volume side will write: its two argument buffers when there is a command, and its instance and
            // clip buffers when there is an instance.
            volumeWrites = (commandTotal > 0 ? 2 : 0) + (instanceTotal > 0 ? 2 : 0);
            return true;
        }

        /// <summary>
        /// Registers this frame's draws for <paramref name="camera"/>: for each colour in turn the stencil byte is set
        /// to 128, then that colour's volumes are counted into it in one call, then that colour's caps are drawn in one
        /// call. Nothing is written here.
        /// <para>
        /// The three materials must be the ones this batch's queues were assigned to, in the order init, volume, cap,
        /// and one material set per colour, so that the pipeline draws the colours in sequence rather than interleaved.
        /// <see cref="VpStencilCapMaterials"/> is what makes such a set, and only one batch may register draws for one
        /// camera.
        /// </para>
        /// </summary>
        public void Render(
            VpStencilCapMaterials materials,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            Camera camera)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            if (materials == null || buffers == null)
            {
                throw new ArgumentNullException(nameof(materials));
            }

            if (materials.ColorCount < _colorCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(materials), "the material set has fewer colours than the upload settled");
            }

            // Under Single Pass Instanced each issue carries two instances, one per eye; the shaders take the eye
            // from the instance id, and a monoscopic camera's variant rejects the second one. This is still one CPU
            // issue per colour per step.
            int eyeInstances = SinglePassInstanced ? 2 : 1;
            _initProperties.SetInteger(InstanceMultiplierId, eyeInstances);
            _capProperties.SetInteger(InstanceMultiplierId, eyeInstances);

            try
            {
                for (int c = 0; c < _colorCount; c++)
                {
                    VpStencilCapColor color = _colors[c];

                    // 1. The whole stencil byte to 128, for this colour alone. The scene's colour and depth are left.
                    var initParams = new RenderParams(materials.Init(c))
                    {
                        camera = camera,
                        layer = layer,
                        matProps = _initProperties,
                        worldBounds = Everywhere,
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = false,
                    };
                    Graphics.RenderPrimitives(initParams, MeshTopology.Triangles, 3, eyeInstances);
                    StencilInitIssues++;

                    // 2. Every volume of this colour, in one call over this colour's command range.
                    if (color.HasVolumes)
                    {
                        _volumes.RenderForward(
                            materials.Volume(c), _volumeProperties, buffers, layer, color.volumeStart,
                            color.volumeCount, camera);
                        VolumeIssues++;
                        VolumeGpuDraws += color.volumeCount;
                    }

                    // 3. Every cap of this colour, in one call over this colour's index range.
                    if (color.HasCaps)
                    {
                        Material capMaterial = materials.Cap(c);
                        capMaterial.SetColor(BaseColorId, AsShaderColour(color.capColour));
                        _capProperties.SetBuffer(CapVerticesId, _capVertexBuffer);
                        var capParams = new RenderParams(capMaterial)
                        {
                            camera = camera,
                            layer = layer,
                            matProps = _capProperties,
                            worldBounds = Everywhere,
                            shadowCastingMode = ShadowCastingMode.Off,
                            receiveShadows = false,

                            // As the body's draw states them (VpIndexedIndirectDrawBatch): the scene's ambient probe
                            // and default reflection, so that a temporary cap is lit from what the real cap is.
                            lightProbeUsage = LightProbeUsage.Off,
                            reflectionProbeUsage = ReflectionProbeUsage.Off,
                        };
                        Graphics.RenderPrimitivesIndexed(
                            capParams, MeshTopology.Triangles, _capIndexBuffer, color.capIndexCount,
                            color.capIndexStart, eyeInstances);
                        CapIssues++;
                    }
                }
            }
            catch
            {
                // Part of the sequence is registered and part is not, which would draw an order this path does not
                // mean. It stops here rather than carrying on.
                _broken = true;
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _volumes.Dispose();
            _capVertexBuffer.Dispose();
            _capIndexBuffer.Dispose();
            _capVertexStaging?.Dispose();
        }

        // These draws are screen-wide or already in world space and must not be culled away by a bound that does not
        // describe them; the pipeline's own depth and stencil decide what they touch.
        private static Bounds Everywhere => new Bounds(Vector3.zero, Vector3.one * 1e6f);

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpStencilCapBatch));
            }
        }

        private void ThrowIfBroken()
        {
            if (_broken)
            {
                throw new InvalidOperationException(
                    "a GPU call of this batch was interrupted, so what reached the GPU cannot be established; dispose it");
            }
        }
    }
}
