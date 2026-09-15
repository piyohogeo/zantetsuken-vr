using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// VP Stage 3 draw issue: the Stage 2 batch layout (<see cref="VpIndirectDrawBatch"/>) drawn with indexed indirect
    /// calls. It draws next to Unity mesh renderers in the same camera: depth-tested against them, casting URP main light
    /// shadows onto them and receiving theirs.
    /// Per frame it issues two Graphics.RenderPrimitivesIndexedIndirect calls over the same hardware index buffer, vertex
    /// buffer and instance buffer: the forward call uses the forward material, receives shadows and casts none; the
    /// shadow call uses the shadow-caster-only material, casts shadows and draws no colour. Each command is one
    /// GraphicsBuffer.IndirectDrawIndexedArgs entry in each call's argument buffer: indexCountPerInstance and startIndex
    /// are its range's index count and start, baseVertexIndex is 0 because the indices already hold global vertex
    /// numbers, and startInstance is the start of its transforms. Both calls are culled and sorted as a whole by the union
    /// of all instance bounds; nothing is culled per instance.
    /// <para>
    /// Single Pass Instanced stereo is handled as in Stage 2: for a batch uploaded for it, the forward arguments hold
    /// instanceCount and startInstance times 2, and the forward shader reads logical instance physicalId &gt;&gt; 1,
    /// dropping odd physical IDs in a forward pass that renders a single view. The shadow arguments and the instance buffer
    /// always hold the logical instances.
    /// </para>
    /// <para>
    /// Commands and transforms are uploaded at set-up. Rendering a frame writes no buffer and allocates nothing. The batch
    /// owns its two argument buffers and its instance buffer; after <see cref="Dispose"/>, uploading and rendering throw,
    /// and disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpIndexedIndirectDrawBatch : IDisposable
    {
        /// <summary>One instance record: a float4x4 object-to-world matrix.</summary>
        public const int InstanceStride = 64;

        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpInstanceMultiplier");

        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _forwardArguments;
        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _shadowArguments;
        private readonly GraphicsBuffer _forwardArgumentBuffer;
        private readonly GraphicsBuffer _shadowArgumentBuffer;
        private readonly GraphicsBuffer _instanceBuffer;
        private bool _disposed;

        public VpIndexedIndirectDrawBatch(int commandCapacity, int instanceCapacity)
        {
            if (commandCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCapacity), commandCapacity, "Must be positive.");
            }

            if (instanceCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instanceCapacity), instanceCapacity, "Must be positive.");
            }

            _forwardArguments = new GraphicsBuffer.IndirectDrawIndexedArgs[commandCapacity];
            _shadowArguments = new GraphicsBuffer.IndirectDrawIndexedArgs[commandCapacity];

            GraphicsBuffer forwardArgumentBuffer = null;
            GraphicsBuffer shadowArgumentBuffer = null;
            try
            {
                forwardArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size)
                {
                    name = "VP Indexed Indirect Forward Arguments",
                };
                shadowArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size)
                {
                    name = "VP Indexed Indirect Shadow Arguments",
                };
                _instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceStride)
                {
                    name = "VP Indexed Instance Transforms",
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
        /// Replaces the commands and instance transforms as <see cref="VpIndirectDrawBatch.TryUpload(VpIndirectCommand[], Matrix4x4[], bool)"/>
        /// does, writing indexed arguments. Returns false, changing no count, bounds, buffer or Single Pass Instanced mode,
        /// when there are more commands than the command capacity, an instance count or range value is negative, the
        /// instances exceed the instance capacity, or the transform count is not the sum of the instance counts.
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

            if (instanceTotal > InstanceCapacity || objectToWorlds.Length != instanceTotal)
            {
                return false;
            }

            uint multiplier = singlePassInstanced ? 2u : 1u;
            int startInstance = 0;
            bool anyInstance = false;
            Bounds worldBounds = default;
            for (int c = 0; c < commands.Length; c++)
            {
                VpIndirectCommand command = commands[c];
                _shadowArguments[c] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount,
                    startIndex = (uint)command.range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = (uint)startInstance,
                };
                _forwardArguments[c] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount * multiplier,
                    startIndex = (uint)command.range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = (uint)startInstance * multiplier,
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
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            Camera camera = null)
        {
            ThrowIfDisposed();
            Render(forwardMaterial, shadowMaterial, properties, buffers, layer, 0, CommandCount, camera);
        }

        /// <summary>
        /// Queues the commands [startCommand, startCommand + commandCount) as two Graphics.RenderPrimitivesIndexedIndirect
        /// calls, in order: the forward call, receiving but not casting shadows, then the shadow call, casting shadows.
        /// Issues nothing when <paramref name="commandCount"/> is 0. Throws when the commands are not within the uploaded
        /// ones. <paramref name="properties"/> is reused by the two calls in turn.
        /// </summary>
        public void Render(
            Material forwardMaterial,
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera = null)
        {
            RenderForward(forwardMaterial, properties, buffers, layer, startCommand, commandCount, camera);
            RenderShadows(shadowMaterial, properties, buffers, layer, startCommand, commandCount, camera);
        }

        /// <summary>
        /// The forward call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera)"/> alone:
        /// the colour pass, receiving but not casting shadows, without the shadow caster call, for a caller that does not
        /// want these commands to cast shadows. The command range check and buffer binding are those of Render, and the
        /// batch keeps owning its buffers.
        /// </summary>
        public void RenderForward(
            Material forwardMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
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
            Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, _forwardArgumentBuffer, commandCount, startCommand);
        }

        /// <summary>The shadow call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera)"/> alone.</summary>
        internal void RenderShadows(
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
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
            Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, _shadowArgumentBuffer, commandCount, startCommand);
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
        private bool BindCommands(MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int startCommand, int commandCount)
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
            properties.SetBuffer(InstanceObjectToWorldId, _instanceBuffer);
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpIndexedIndirectDrawBatch));
            }
        }
    }
}
