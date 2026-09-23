using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Stage 3 probe: per-view instance selections of an indexed indirect VP batch, made with one conservative culling
    /// test (<see cref="VpInstanceCulling"/>) for the forward view and for each main light shadow cascade (split). The set
    /// keeps the instances' world bounds on the CPU and their transforms in its own instance buffer. A selection lists the
    /// selected logical instance numbers, grouped by command in upload order, in the visible-instance buffer, and writes
    /// one indexed argument entry per command with a selected instance: indexCountPerInstance and startIndex from the
    /// range, baseVertexIndex 0.
    /// <para>
    /// Layout: split s owns visible entries from s * InstanceCapacity and shadow argument entries from s * CommandCapacity,
    /// with instanceCount the selected count and startInstance 0; the cascade shadow pass passes each draw's visible offset
    /// to the shader. The forward view owns visible entries from MaxSplits * InstanceCapacity and the forward argument
    /// buffer, drawn with one Graphics.RenderPrimitivesIndexedIndirect call ("Zantetsu/VP Culled Indexed Indirect Unlit"):
    /// its startInstance is the command's visible offset and, as in Stage 3, instanceCount and startInstance are doubled
    /// for Single Pass Instanced stereo. The forward view selects the instances that may intersect the frustum of either
    /// eye.
    /// </para>
    /// <para>
    /// Unity computes the main light's shadow caster bounds, and from them each cascade's depth range, from the renderers
    /// it culls; the cascade shadow pass is not one. <see cref="RenderShadowCasterBounds"/> therefore queues one
    /// shadows-only indexed indirect call with zero instances and <see cref="WorldBounds"/>: it registers the bounds and
    /// draws nothing.
    /// </para>
    /// <para>
    /// Selecting and uploading allocate nothing. A selection is compared entry by entry with the copy of what its view's
    /// buffer ranges already hold; uploading writes the selected entries only when one of them differs or was never
    /// uploaded, so an unchanged view uploads nothing (<see cref="ForwardUploadCount"/>, <see cref="ShadowUploadCount"/>).
    /// The set owns its five buffers; after <see cref="Dispose"/>, every operation throws and disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpCulledInstanceSet : IDisposable
    {
        public const int MaxSplits = 4;

        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");
        private static readonly int VisibleInstancesId = Shader.PropertyToID("_VpVisibleInstances");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpInstanceMultiplier");

        private readonly GraphicsBuffer _instanceBuffer;
        private readonly GraphicsBuffer _visibleBuffer;
        private readonly GraphicsBuffer _shadowArgumentBuffer;
        private readonly GraphicsBuffer _forwardArgumentBuffer;
        private readonly GraphicsBuffer _casterBoundsArgumentBuffer;
        private readonly VpIndirectCommand[] _commands;
        private readonly int[] _commandStart;
        private readonly int[] _shadowDrawVisibleOffset;
        private readonly int[] _splitDrawCount = new int[MaxSplits];
        private readonly int[] _splitSelected = new int[MaxSplits];
        // The leading entries of each view's ranges that the GPU holds exactly as the CPU arrays do, and whether the last
        // selection since the view's upload changed or extended them.
        private readonly int[] _splitGpuVisible = new int[MaxSplits];
        private readonly int[] _splitGpuArguments = new int[MaxSplits];
        private readonly bool[] _splitDirty = new bool[MaxSplits];
        private int _forwardGpuVisible;
        private int _forwardGpuArguments;
        private bool _forwardDirty;
        private NativeArray<Bounds> _bounds;
        private NativeArray<uint> _visible;
        private NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs> _shadowArguments;
        private NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs> _forwardArguments;
        private bool _disposed;

        public VpCulledInstanceSet(int commandCapacity, int instanceCapacity)
        {
            if (commandCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCapacity), commandCapacity, "Must be positive.");
            }

            if (instanceCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instanceCapacity), instanceCapacity, "Must be positive.");
            }

            CommandCapacity = commandCapacity;
            InstanceCapacity = instanceCapacity;
            _commands = new VpIndirectCommand[commandCapacity];
            _commandStart = new int[commandCapacity];
            _shadowDrawVisibleOffset = new int[commandCapacity * MaxSplits];
            _bounds = new NativeArray<Bounds>(instanceCapacity, Allocator.Persistent);
            _visible = new NativeArray<uint>(instanceCapacity * (MaxSplits + 1), Allocator.Persistent);
            _shadowArguments = new NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs>(commandCapacity * MaxSplits, Allocator.Persistent);
            _forwardArguments = new NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs>(commandCapacity, Allocator.Persistent);
            try
            {
                _instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, VpIndexedIndirectDrawBatch.InstanceStride) { name = "VP Culled Instances" };
                _visibleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity * (MaxSplits + 1), sizeof(uint)) { name = "VP Culled Visible Instances" };
                _shadowArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity * MaxSplits, GraphicsBuffer.IndirectDrawIndexedArgs.size) { name = "VP Culled Shadow Arguments" };
                _forwardArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size) { name = "VP Culled Forward Arguments" };
                _casterBoundsArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size) { name = "VP Culled Shadow Caster Bounds Arguments" };
                _casterBoundsArgumentBuffer.SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs() });
            }
            catch
            {
                _instanceBuffer?.Dispose();
                _visibleBuffer?.Dispose();
                _shadowArgumentBuffer?.Dispose();
                _forwardArgumentBuffer?.Dispose();
                _casterBoundsArgumentBuffer?.Dispose();
                _bounds.Dispose();
                _visible.Dispose();
                _shadowArguments.Dispose();
                _forwardArguments.Dispose();
                throw;
            }
        }

        public int CommandCapacity { get; }

        public int InstanceCapacity { get; }

        public int CommandCount { get; private set; }

        public int InstanceCount { get; private set; }

        /// <summary>Whether the forward arguments hold physical instances for Single Pass Instanced stereo.</summary>
        public bool SinglePassInstanced { get; private set; }

        /// <summary>The union of the world bounds of every uploaded instance: the forward call's culling and sorting bounds.</summary>
        public Bounds WorldBounds { get; private set; }

        /// <summary>The splits selected since <see cref="BeginShadowFrame"/>: one past the highest selected split.</summary>
        public int SplitCount { get; private set; }

        /// <summary>The forward draws (commands with a selected instance) of the last forward selection.</summary>
        public int ForwardDrawCount { get; private set; }

        /// <summary>The logical instances of the last forward selection.</summary>
        public int ForwardSelectedInstances { get; private set; }

        /// <summary>Diagnostics: the forward uploads that wrote buffer data.</summary>
        public int ForwardUploadCount { get; private set; }

        /// <summary>Diagnostics: the per-split shadow selection uploads that wrote buffer data.</summary>
        public int ShadowUploadCount { get; private set; }

        public GraphicsBuffer InstanceBuffer => Checked(_instanceBuffer);

        public GraphicsBuffer VisibleBuffer => Checked(_visibleBuffer);

        public GraphicsBuffer ShadowArgumentBuffer => Checked(_shadowArgumentBuffer);

        /// <summary>The forward argument buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer ForwardArgumentBuffer => Checked(_forwardArgumentBuffer);

        /// <summary>
        /// Replaces the commands and transforms, laid out as for <see cref="VpIndexedIndirectDrawBatch.TryUpload"/>. Returns
        /// false, changing nothing, when a count exceeds its capacity, an instance count or range value is negative, or
        /// the transform count is not the sum of the instance counts. Clears the forward and shadow selections.
        /// </summary>
        public bool TryUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, bool singlePassInstanced)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            if (objectToWorlds == null)
            {
                throw new ArgumentNullException(nameof(objectToWorlds));
            }

            long total = 0;
            foreach (VpIndirectCommand command in commands)
            {
                if (command.instanceCount < 0 || command.range.indexStart < 0 || command.range.indexCount < 0)
                {
                    return false;
                }

                total += command.instanceCount;
            }

            if (commands.Length > CommandCapacity || total > InstanceCapacity || objectToWorlds.Length != total)
            {
                return false;
            }

            int instance = 0;
            Bounds worldBounds = default;
            for (int c = 0; c < commands.Length; c++)
            {
                _commands[c] = commands[c];
                _commandStart[c] = instance;
                for (int k = 0; k < commands[c].instanceCount; k++, instance++)
                {
                    Bounds bounds = VpDirectDraw.WorldBounds(commands[c].localBounds, objectToWorlds[instance]);
                    _bounds[instance] = bounds;
                    if (instance == 0)
                    {
                        worldBounds = bounds;
                    }
                    else
                    {
                        worldBounds.Encapsulate(bounds);
                    }
                }
            }

            if (total > 0)
            {
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)total);
            }

            CommandCount = commands.Length;
            InstanceCount = (int)total;
            SinglePassInstanced = singlePassInstanced;
            WorldBounds = worldBounds;
            ForwardDrawCount = 0;
            ForwardSelectedInstances = 0;
            BeginShadowFrame();
            return true;
        }

        /// <summary>
        /// Selects the forward view's instances: those that may intersect any of <paramref name="eyeCount"/> eye frusta of
        /// <see cref="VpInstanceCulling.EyePlaneCount"/> planes each (<see cref="VpInstanceCulling.GetEyePlanes"/>).
        /// </summary>
        public void SelectForward(Plane[] eyePlanes, int eyeCount)
        {
            ThrowIfDisposed();
            if (eyePlanes == null)
            {
                throw new ArgumentNullException(nameof(eyePlanes));
            }

            if (eyeCount < 1 || eyePlanes.Length < eyeCount * VpInstanceCulling.EyePlaneCount)
            {
                throw new ArgumentOutOfRangeException(nameof(eyeCount), eyeCount, "Needs at least one eye and its planes.");
            }

            uint multiplier = SinglePassInstanced ? 2u : 1u;
            int visibleBase = MaxSplits * InstanceCapacity;
            int written = 0;
            int draws = 0;
            bool changed = false;
            for (int c = 0; c < CommandCount; c++)
            {
                int first = written;
                int end = _commandStart[c] + _commands[c].instanceCount;
                for (int i = _commandStart[c]; i < end; i++)
                {
                    if (VpInstanceCulling.MayIntersectAnyEye(_bounds[i], eyePlanes, eyeCount))
                    {
                        changed |= Write(_visible, visibleBase + written++, (uint)i);
                    }
                }

                int selected = written - first;
                if (selected == 0)
                {
                    continue;
                }

                changed |= Write(_forwardArguments, draws++, new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)_commands[c].range.indexCount,
                    instanceCount = (uint)selected * multiplier,
                    startIndex = (uint)_commands[c].range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = (uint)(visibleBase + first) * multiplier,
                });
            }

            ForwardDrawCount = draws;
            ForwardSelectedInstances = written;
            _forwardDirty |= changed || written > _forwardGpuVisible || draws > _forwardGpuArguments;
        }

        /// <summary>Uploads the last forward selection, unless the forward buffer ranges already hold it.</summary>
        public void UploadForward()
        {
            ThrowIfDisposed();
            if (!_forwardDirty)
            {
                return;
            }

            int visibleBase = MaxSplits * InstanceCapacity;
            bool wrote = false;
            if (ForwardSelectedInstances > 0)
            {
                _visibleBuffer.SetData(_visible, visibleBase, visibleBase, ForwardSelectedInstances);
                wrote = true;
            }

            if (ForwardDrawCount > 0)
            {
                _forwardArgumentBuffer.SetData(_forwardArguments, 0, 0, ForwardDrawCount);
                wrote = true;
            }

            _forwardGpuVisible = Math.Max(_forwardGpuVisible, ForwardSelectedInstances);
            _forwardGpuArguments = Math.Max(_forwardGpuArguments, ForwardDrawCount);
            _forwardDirty = false;
            ForwardUploadCount += wrote ? 1 : 0;
        }

        /// <summary>
        /// Queues the uploaded forward selection as one Graphics.RenderPrimitivesIndexedIndirect call for this frame's
        /// cameras, or only <paramref name="camera"/> when given: receiving but not casting shadows, culled and sorted by
        /// <see cref="WorldBounds"/>. Issues nothing when no instance is selected.
        /// </summary>
        public void RenderForward(Material forwardMaterial, MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            if (ForwardDrawCount == 0)
            {
                return;
            }

            properties.SetBuffer(VerticesId, buffers.VertexBuffer);
            properties.SetBuffer(InstanceObjectToWorldId, _instanceBuffer);
            properties.SetBuffer(VisibleInstancesId, _visibleBuffer);
            properties.SetInteger(InstanceMultiplierId, SinglePassInstanced ? 2 : 1);
            var renderParams = new RenderParams(forwardMaterial)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = WorldBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true,
            };
            Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, _forwardArgumentBuffer, ForwardDrawCount, 0);
        }

        /// <summary>
        /// Queues a shadows-only indexed indirect call of zero instances with <see cref="WorldBounds"/>, for this frame's
        /// cameras or only <paramref name="camera"/>, so that Unity's shadow caster bounds include the instances the
        /// cascade shadow pass draws. <paramref name="shadowCasterMaterial"/> needs a ShadowCaster pass; nothing of it runs.
        /// Issues nothing when no instance is uploaded.
        /// </summary>
        public void RenderShadowCasterBounds(Material shadowCasterMaterial, VpGpuIndexedGeometryBuffers buffers, int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            if (InstanceCount == 0)
            {
                return;
            }

            var renderParams = new RenderParams(shadowCasterMaterial)
            {
                camera = camera,
                layer = layer,
                worldBounds = WorldBounds,
                shadowCastingMode = ShadowCastingMode.ShadowsOnly,
                receiveShadows = false,
            };
            Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, _casterBoundsArgumentBuffer, 1, 0);
        }

        /// <summary>Diagnostics: whether a logical instance is in the last forward selection.</summary>
        public bool IsForwardSelected(int instance)
        {
            ThrowIfDisposed();
            return Contains(MaxSplits * InstanceCapacity, ForwardSelectedInstances, instance);
        }

        /// <summary>Forgets the shadow selections of the previous frame.</summary>
        public void BeginShadowFrame()
        {
            ThrowIfDisposed();
            SplitCount = 0;
            Array.Clear(_splitDrawCount, 0, MaxSplits);
            Array.Clear(_splitSelected, 0, MaxSplits);
        }

        /// <summary>
        /// Selects split <paramref name="split"/>'s instances against the first <paramref name="planeCount"/> planes; with
        /// no plane, every instance is selected.
        /// </summary>
        public void SelectSplit(int split, Plane[] planes, int planeCount)
        {
            ThrowIfDisposed();
            if (split < 0 || split >= MaxSplits)
            {
                throw new ArgumentOutOfRangeException(nameof(split), split, "Must be below MaxSplits.");
            }

            int visibleBase = split * InstanceCapacity;
            int argumentBase = split * CommandCapacity;
            int written = 0;
            int draws = 0;
            bool changed = false;
            for (int c = 0; c < CommandCount; c++)
            {
                int first = written;
                int end = _commandStart[c] + _commands[c].instanceCount;
                for (int i = _commandStart[c]; i < end; i++)
                {
                    if (planeCount == 0 || VpInstanceCulling.MayIntersect(_bounds[i], planes, planeCount))
                    {
                        changed |= Write(_visible, visibleBase + written++, (uint)i);
                    }
                }

                int selected = written - first;
                if (selected == 0)
                {
                    continue;
                }

                changed |= Write(_shadowArguments, argumentBase + draws, new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)_commands[c].range.indexCount,
                    instanceCount = (uint)selected,
                    startIndex = (uint)_commands[c].range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = 0,
                });
                _shadowDrawVisibleOffset[argumentBase + draws] = visibleBase + first;
                draws++;
            }

            _splitDrawCount[split] = draws;
            _splitSelected[split] = written;
            _splitDirty[split] |= changed || written > _splitGpuVisible[split] || draws > _splitGpuArguments[split];
            SplitCount = Math.Max(SplitCount, split + 1);
        }

        /// <summary>Uploads the shadow selections of the splits selected this frame whose buffer ranges do not already hold them.</summary>
        public void UploadShadowSelections()
        {
            ThrowIfDisposed();
            for (int split = 0; split < SplitCount; split++)
            {
                if (!_splitDirty[split])
                {
                    continue;
                }

                bool wrote = false;
                if (_splitSelected[split] > 0)
                {
                    _visibleBuffer.SetData(_visible, split * InstanceCapacity, split * InstanceCapacity, _splitSelected[split]);
                    wrote = true;
                }

                if (_splitDrawCount[split] > 0)
                {
                    _shadowArgumentBuffer.SetData(_shadowArguments, split * CommandCapacity, split * CommandCapacity, _splitDrawCount[split]);
                    wrote = true;
                }

                _splitGpuVisible[split] = Math.Max(_splitGpuVisible[split], _splitSelected[split]);
                _splitGpuArguments[split] = Math.Max(_splitGpuArguments[split], _splitDrawCount[split]);
                _splitDirty[split] = false;
                ShadowUploadCount += wrote ? 1 : 0;
            }
        }

        /// <summary>The shadow draws (commands with a selected instance) of a split this frame.</summary>
        public int ShadowDrawCount(int split)
        {
            ThrowIfDisposed();
            return _splitDrawCount[split];
        }

        /// <summary>The logical instances selected for a split this frame.</summary>
        public int ShadowSelectedInstances(int split)
        {
            ThrowIfDisposed();
            return _splitSelected[split];
        }

        public void GetShadowDraw(int split, int draw, out int argumentsOffsetBytes, out int visibleOffset)
        {
            ThrowIfDisposed();
            if (draw < 0 || draw >= _splitDrawCount[split])
            {
                throw new ArgumentOutOfRangeException(nameof(draw), draw, "Must be below the split's draw count.");
            }

            int slot = split * CommandCapacity + draw;
            argumentsOffsetBytes = slot * GraphicsBuffer.IndirectDrawIndexedArgs.size;
            visibleOffset = _shadowDrawVisibleOffset[slot];
        }

        /// <summary>Diagnostics: whether a logical instance is in a split's selection this frame.</summary>
        public bool IsShadowSelected(int split, int instance)
        {
            ThrowIfDisposed();
            return Contains(split * InstanceCapacity, _splitSelected[split], instance);
        }

        /// <summary>Diagnostics: an uploaded instance's world bounds.</summary>
        public Bounds InstanceWorldBounds(int instance)
        {
            ThrowIfDisposed();
            if (instance < 0 || instance >= InstanceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(instance), instance, "Must be an uploaded instance.");
            }

            return _bounds[instance];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _instanceBuffer.Dispose();
            _visibleBuffer.Dispose();
            _shadowArgumentBuffer.Dispose();
            _forwardArgumentBuffer.Dispose();
            _casterBoundsArgumentBuffer.Dispose();
            _bounds.Dispose();
            _visible.Dispose();
            _shadowArguments.Dispose();
            _forwardArguments.Dispose();
        }

        /// <summary>Writes a CPU copy entry; true when the value differs from the one it replaces.</summary>
        private static bool Write(NativeArray<uint> entries, int index, uint value)
        {
            if (entries[index] == value)
            {
                return false;
            }

            entries[index] = value;
            return true;
        }

        private static bool Write(NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs> entries, int index, GraphicsBuffer.IndirectDrawIndexedArgs value)
        {
            GraphicsBuffer.IndirectDrawIndexedArgs old = entries[index];
            if (old.indexCountPerInstance == value.indexCountPerInstance && old.instanceCount == value.instanceCount && old.startIndex == value.startIndex
                && old.baseVertexIndex == value.baseVertexIndex && old.startInstance == value.startInstance)
            {
                return false;
            }

            entries[index] = value;
            return true;
        }

        private bool Contains(int visibleBase, int count, int instance)
        {
            for (int i = 0; i < count; i++)
            {
                if (_visible[visibleBase + i] == (uint)instance)
                {
                    return true;
                }
            }

            return false;
        }

        private GraphicsBuffer Checked(GraphicsBuffer buffer)
        {
            ThrowIfDisposed();
            return buffer;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCulledInstanceSet));
            }
        }
    }
}
