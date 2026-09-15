using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Stage 2 draw issue (DESIGN 4.5.5): a material-compatible batch of geometry ranges, each drawn at its own run of
    /// instance transforms, issued per frame as two Graphics.RenderPrimitivesIndirect calls over the same vertex, index
    /// and instance buffers. The forward call uses the forward material, receives shadows and casts none; the shadow call
    /// uses the shadow-caster-only material, casts shadows and draws no colour. Each command is one
    /// GraphicsBuffer.IndirectDrawArgs entry in each call's argument buffer, whose startVertex is its range's index start
    /// and whose startInstance is the start of its transforms; the shaders read both, so there is no separate command
    /// descriptor buffer. Instances carry only an object-to-world matrix and share the forward material's colour. Both
    /// calls are culled and sorted as a whole by the union of all instance bounds; nothing is culled per instance.
    /// <para>
    /// Single Pass Instanced stereo sends physical instance IDs 2k and 2k+1 to the left and right eye, and Unity does not
    /// multiply the instance count of an indirect draw. For a batch uploaded for Single Pass Instanced, the forward
    /// arguments hold physical values, instanceCount and startInstance times 2, and the forward shader reads logical
    /// instance physicalId &gt;&gt; 1, dropping odd physical IDs before reading any buffer in a forward pass that renders a
    /// single view. A shadow map renders a single view, so the shadow arguments always hold the logical values. The
    /// instance buffer always holds one transform per logical instance.
    /// </para>
    /// <para>
    /// Commands and transforms are uploaded at set-up. Rendering a frame writes no buffer and allocates nothing. The batch
    /// owns its two argument buffers and its instance buffer; after <see cref="Dispose"/>, uploading and rendering throw,
    /// and disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpIndirectDrawBatch : IDisposable
    {
        /// <summary>One instance record: a float4x4 object-to-world matrix.</summary>
        public const int InstanceStride = 64;

        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int IndicesId = Shader.PropertyToID("_VpIndices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpInstanceMultiplier");

        private readonly GraphicsBuffer.IndirectDrawArgs[] _forwardArguments;
        private readonly GraphicsBuffer.IndirectDrawArgs[] _shadowArguments;
        private readonly GraphicsBuffer _forwardArgumentBuffer;
        private readonly GraphicsBuffer _shadowArgumentBuffer;
        private readonly GraphicsBuffer _instanceBuffer;
        private readonly long _maxPhysicalInstanceEnd;
        private bool _disposed;

        public VpIndirectDrawBatch(int commandCapacity, int instanceCapacity)
            : this(commandCapacity, instanceCapacity, int.MaxValue)
        {
        }

        /// <summary>A batch whose limit on the end of the physical instance range is lowered, so tests can reach it.</summary>
        internal VpIndirectDrawBatch(int commandCapacity, int instanceCapacity, long maxPhysicalInstanceEnd)
        {
            if (commandCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCapacity), commandCapacity, "Must be positive.");
            }

            if (instanceCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instanceCapacity), instanceCapacity, "Must be positive.");
            }

            if (maxPhysicalInstanceEnd <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPhysicalInstanceEnd), maxPhysicalInstanceEnd, "Must be positive.");
            }

            _forwardArguments = new GraphicsBuffer.IndirectDrawArgs[commandCapacity];
            _shadowArguments = new GraphicsBuffer.IndirectDrawArgs[commandCapacity];
            _maxPhysicalInstanceEnd = maxPhysicalInstanceEnd;

            GraphicsBuffer forwardArgumentBuffer = null;
            GraphicsBuffer shadowArgumentBuffer = null;
            try
            {
                forwardArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawArgs.size)
                {
                    name = "VP Indirect Forward Arguments",
                };
                shadowArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawArgs.size)
                {
                    name = "VP Indirect Shadow Arguments",
                };
                _instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceStride)
                {
                    name = "VP Instance Transforms",
                };
            }
            catch
            {
                forwardArgumentBuffer?.Dispose();
                shadowArgumentBuffer?.Dispose();
                throw;
            }

            _forwardArgumentBuffer = forwardArgumentBuffer;
            _shadowArgumentBuffer = shadowArgumentBuffer;
            CommandCapacity = commandCapacity;
            InstanceCapacity = instanceCapacity;
        }

        public int CommandCapacity { get; }

        public int InstanceCapacity { get; }

        public int CommandCount { get; private set; }

        /// <summary>The number of logical instances, one per uploaded transform.</summary>
        public int InstanceCount { get; private set; }

        /// <summary>Whether the forward arguments hold physical instances for Single Pass Instanced stereo.</summary>
        public bool SinglePassInstanced { get; private set; }

        /// <summary>The union of the world bounds of every uploaded instance: the culling and sorting bounds of both calls.</summary>
        public Bounds WorldBounds { get; private set; }

        /// <summary>The forward call's argument buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer ForwardArgumentBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _forwardArgumentBuffer;
            }
        }

        /// <summary>The shadow call's argument buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer ShadowArgumentBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _shadowArgumentBuffer;
            }
        }

        /// <summary>The instance transform buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer InstanceBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _instanceBuffer;
            }
        }

        /// <summary>
        /// <see cref="TryUpload(VpIndirectCommand[], Matrix4x4[], bool)"/> for rendering without Single Pass Instanced
        /// stereo: both argument buffers hold the logical values.
        /// </summary>
        public bool TryUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds)
        {
            return TryUpload(commands, objectToWorlds, false);
        }

        /// <summary>
        /// Replaces the commands and instance transforms. Command i draws its range at its instanceCount transforms, which
        /// follow those of the earlier commands in <paramref name="objectToWorlds"/>. With
        /// <paramref name="singlePassInstanced"/>, the forward arguments hold twice the logical instanceCount and
        /// startInstance; the shadow arguments and the instance buffer always hold the logical ones. Returns false,
        /// changing no count, bounds or buffer, when there are more commands than the command capacity, an instance count
        /// or range value is negative, the instances exceed the instance capacity, the transform count is not the sum of
        /// the instance counts, or the physical instance range does not fit.
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

            if (commands.Length > CommandCapacity)
            {
                return false;
            }

            long instanceTotal = 0;
            foreach (VpIndirectCommand command in commands)
            {
                if (command.instanceCount < 0 || command.range.indexStart < 0 || command.range.indexCount < 0)
                {
                    return false;
                }

                instanceTotal += command.instanceCount;
            }

            long multiplier = singlePassInstanced ? 2 : 1;
            if (instanceTotal > InstanceCapacity || objectToWorlds.Length != instanceTotal || instanceTotal * multiplier > _maxPhysicalInstanceEnd)
            {
                return false;
            }

            int startInstance = 0;
            bool anyInstance = false;
            Bounds worldBounds = default;
            for (int c = 0; c < commands.Length; c++)
            {
                VpIndirectCommand command = commands[c];
                _shadowArguments[c] = new GraphicsBuffer.IndirectDrawArgs
                {
                    vertexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount,
                    startVertex = (uint)command.range.indexStart,
                    startInstance = (uint)startInstance,
                };
                _forwardArguments[c] = new GraphicsBuffer.IndirectDrawArgs
                {
                    vertexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)(command.instanceCount * multiplier),
                    startVertex = (uint)command.range.indexStart,
                    startInstance = (uint)(startInstance * multiplier),
                };

                for (int i = startInstance; i < startInstance + command.instanceCount; i++)
                {
                    Bounds instanceBounds = VpDirectDraw.WorldBounds(command.localBounds, objectToWorlds[i]);
                    if (anyInstance)
                    {
                        worldBounds.Encapsulate(instanceBounds);
                    }
                    else
                    {
                        worldBounds = instanceBounds;
                        anyInstance = true;
                    }
                }

                startInstance += command.instanceCount;
            }

            if (commands.Length > 0)
            {
                _forwardArgumentBuffer.SetData(_forwardArguments, 0, 0, commands.Length);
                _shadowArgumentBuffer.SetData(_shadowArguments, 0, 0, commands.Length);
            }

            if (instanceTotal > 0)
            {
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)instanceTotal);
            }

            CommandCount = commands.Length;
            InstanceCount = (int)instanceTotal;
            SinglePassInstanced = singlePassInstanced;
            WorldBounds = worldBounds;
            return true;
        }

        /// <summary>
        /// Queues every uploaded command for this frame's cameras, or only <paramref name="camera"/> when given, as a forward
        /// call and a shadow call. Issues nothing when no command is uploaded.
        /// </summary>
        public void Render(
            Material forwardMaterial,
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuGeometryBuffers buffers,
            int layer,
            Camera camera = null)
        {
            ThrowIfDisposed();
            Render(forwardMaterial, shadowMaterial, properties, buffers, layer, 0, CommandCount, camera);
        }

        /// <summary>
        /// Queues the commands [startCommand, startCommand + commandCount) as two Graphics.RenderPrimitivesIndirect calls,
        /// in order: the forward call with <paramref name="forwardMaterial"/>, receiving but not casting shadows, then the
        /// shadow call with the shadow-caster-only <paramref name="shadowMaterial"/>, casting shadows. Both are culled by
        /// <see cref="WorldBounds"/>. Issues nothing when <paramref name="commandCount"/> is 0. Throws when the commands are
        /// not within the uploaded ones. <paramref name="properties"/> is reused by the two calls in turn.
        /// </summary>
        public void Render(
            Material forwardMaterial,
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera = null)
        {
            RenderForward(forwardMaterial, properties, buffers, layer, startCommand, commandCount, camera);
            RenderShadows(shadowMaterial, properties, buffers, layer, startCommand, commandCount, camera);
        }

        /// <summary>The forward call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuGeometryBuffers, int, int, int, Camera)"/> alone.</summary>
        internal void RenderForward(
            Material forwardMaterial,
            MaterialPropertyBlock properties,
            VpGpuGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera)
        {
            if (!BindCommands(properties, buffers, startCommand, commandCount))
            {
                return;
            }

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
            Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Triangles, _forwardArgumentBuffer, commandCount, startCommand);
        }

        /// <summary>The shadow call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuGeometryBuffers, int, int, int, Camera)"/> alone.</summary>
        internal void RenderShadows(
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera)
        {
            if (!BindCommands(properties, buffers, startCommand, commandCount))
            {
                return;
            }

            var renderParams = new RenderParams(shadowMaterial)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = WorldBounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = false,
            };
            Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Triangles, _shadowArgumentBuffer, commandCount, startCommand);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _forwardArgumentBuffer.Dispose();
            _shadowArgumentBuffer.Dispose();
            _instanceBuffer.Dispose();
        }

        /// <summary>
        /// Checks the command range and binds the shared buffers. False when there is nothing to issue; throws when the
        /// commands are not within the uploaded ones.
        /// </summary>
        private bool BindCommands(MaterialPropertyBlock properties, VpGpuGeometryBuffers buffers, int startCommand, int commandCount)
        {
            ThrowIfDisposed();
            if (startCommand < 0 || commandCount < 0 || startCommand > CommandCount - commandCount)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCount), commandCount, "The commands must be within the uploaded ones.");
            }

            if (commandCount == 0)
            {
                return false;
            }

            properties.SetBuffer(VerticesId, buffers.VertexBuffer);
            properties.SetBuffer(IndicesId, buffers.IndexBuffer);
            properties.SetBuffer(InstanceObjectToWorldId, _instanceBuffer);
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpIndirectDrawBatch));
            }
        }
    }
}
