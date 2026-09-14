using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Stage 2 draw issue (DESIGN 4.5.5): one Graphics.RenderPrimitivesIndirect call for a material-compatible batch of
    /// geometry ranges, each drawn at its own run of instance transforms. Each command is one
    /// GraphicsBuffer.IndirectDrawArgs entry whose startVertex is its range's index start and whose startInstance is the
    /// start of its transforms in the instance buffer; the Indirect shader reads both, so there is no separate command
    /// descriptor buffer. Instances carry only an object-to-world matrix and share the material's colour. The call is
    /// culled and sorted as a whole by the union of all instance bounds; nothing is culled per instance.
    /// <para>
    /// Commands and transforms are uploaded at set-up. Rendering a frame writes no buffer and allocates nothing. The
    /// batch owns its argument and instance buffers; after <see cref="Dispose"/>, uploading and rendering throw, and
    /// disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpIndirectDrawBatch : IDisposable
    {
        /// <summary>One instance record: a float4x4 object-to-world matrix.</summary>
        public const int InstanceStride = 64;

        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int IndicesId = Shader.PropertyToID("_VpIndices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");

        private readonly GraphicsBuffer.IndirectDrawArgs[] _arguments;
        private readonly GraphicsBuffer _argumentBuffer;
        private readonly GraphicsBuffer _instanceBuffer;
        private bool _disposed;

        public VpIndirectDrawBatch(int commandCapacity, int instanceCapacity)
        {
            if (commandCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCapacity), commandCapacity, "Must be positive.");
            }

            if (instanceCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instanceCapacity), instanceCapacity, "Must be positive.");
            }

            _arguments = new GraphicsBuffer.IndirectDrawArgs[commandCapacity];
            _argumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawArgs.size)
            {
                name = "VP Indirect Arguments",
            };
            try
            {
                _instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceStride)
                {
                    name = "VP Instance Transforms",
                };
            }
            catch
            {
                _argumentBuffer.Dispose();
                throw;
            }

            CommandCapacity = commandCapacity;
            InstanceCapacity = instanceCapacity;
        }

        public int CommandCapacity { get; }

        public int InstanceCapacity { get; }

        public int CommandCount { get; private set; }

        public int InstanceCount { get; private set; }

        /// <summary>The union of the world bounds of every uploaded instance: the culling and sorting bounds of the whole call.</summary>
        public Bounds WorldBounds { get; private set; }

        /// <summary>The indirect argument buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer ArgumentBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _argumentBuffer;
            }
        }

        /// <summary>
        /// Replaces the commands and instance transforms. Command i draws its range at its instanceCount transforms, which
        /// follow those of the earlier commands in <paramref name="objectToWorlds"/>. Returns false, changing nothing, when
        /// there are more commands than the command capacity, an instance count or range value is negative, the instances
        /// exceed the instance capacity, or the transform count is not the sum of the instance counts.
        /// </summary>
        public bool TryUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds)
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

            int startInstance = 0;
            bool anyInstance = false;
            Bounds worldBounds = default;
            for (int c = 0; c < commands.Length; c++)
            {
                VpIndirectCommand command = commands[c];
                _arguments[c] = new GraphicsBuffer.IndirectDrawArgs
                {
                    vertexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount,
                    startVertex = (uint)command.range.indexStart,
                    startInstance = (uint)startInstance,
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
                _argumentBuffer.SetData(_arguments, 0, 0, commands.Length);
            }

            if (instanceTotal > 0)
            {
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)instanceTotal);
            }

            CommandCount = commands.Length;
            InstanceCount = (int)instanceTotal;
            WorldBounds = worldBounds;
            return true;
        }

        /// <summary>
        /// Queues every uploaded command in one Graphics.RenderPrimitivesIndirect call for this frame's cameras, or only
        /// <paramref name="camera"/> when given. Issues nothing when no command is uploaded.
        /// </summary>
        public void Render(Material material, MaterialPropertyBlock properties, VpGpuGeometryBuffers buffers, int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            Render(material, properties, buffers, layer, 0, CommandCount, camera);
        }

        /// <summary>
        /// Queues the commands [startCommand, startCommand + commandCount) in one Graphics.RenderPrimitivesIndirect call,
        /// culled by <see cref="WorldBounds"/>. Issues nothing when <paramref name="commandCount"/> is 0. Throws when the
        /// commands are not within the uploaded ones.
        /// </summary>
        public void Render(
            Material material,
            MaterialPropertyBlock properties,
            VpGpuGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera = null)
        {
            ThrowIfDisposed();
            if (startCommand < 0 || commandCount < 0 || startCommand > CommandCount - commandCount)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCount), commandCount, "The commands must be within the uploaded ones.");
            }

            if (commandCount == 0)
            {
                return;
            }

            properties.SetBuffer(VerticesId, buffers.VertexBuffer);
            properties.SetBuffer(IndicesId, buffers.IndexBuffer);
            properties.SetBuffer(InstanceObjectToWorldId, _instanceBuffer);
            var renderParams = new RenderParams(material)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = WorldBounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true,
            };
            Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Triangles, _argumentBuffer, commandCount, startCommand);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _argumentBuffer.Dispose();
            _instanceBuffer.Dispose();
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
