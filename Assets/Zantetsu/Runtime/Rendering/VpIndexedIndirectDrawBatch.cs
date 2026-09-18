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

        /// <summary>
        /// One clip record: <see cref="VpInstanceClip"/>, its eight planes and then the offset with the valid
        /// count, nine float4s. Fixed whatever the count is: no instance takes a smaller or a larger record.
        /// </summary>
        public const int InstanceClipStride = 144;

        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");
        private static readonly int InstanceClipId = Shader.PropertyToID("_VpInstanceClip");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpInstanceMultiplier");

        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _forwardArguments;
        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _shadowArguments;
        private readonly GraphicsBuffer _forwardArgumentBuffer;
        private readonly GraphicsBuffer _shadowArgumentBuffer;
        private readonly GraphicsBuffer _instanceBuffer;
        private readonly GraphicsBuffer _instanceClipBuffer;
        private readonly VpInstanceClip[] _instanceClips;
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
            _instanceClips = new VpInstanceClip[instanceCapacity];

            // Each buffer is held in a local the moment it exists and named afterwards, so a failure anywhere in
            // here can release every buffer that was already made. The fields are set only once all four stand.
            GraphicsBuffer forwardArgumentBuffer = null;
            GraphicsBuffer shadowArgumentBuffer = null;
            GraphicsBuffer instanceBuffer = null;
            GraphicsBuffer instanceClipBuffer = null;
            try
            {
                forwardArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                forwardArgumentBuffer.name = "VP Indexed Indirect Forward Arguments";
                shadowArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                shadowArgumentBuffer.name = "VP Indexed Indirect Shadow Arguments";
                instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceStride);
                instanceBuffer.name = "VP Indexed Instance Transforms";
                instanceClipBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceClipStride);
                instanceClipBuffer.name = "VP Indexed Instance Clips";
            }
            catch
            {
                forwardArgumentBuffer?.Dispose();
                shadowArgumentBuffer?.Dispose();
                instanceBuffer?.Dispose();
                instanceClipBuffer?.Dispose();
                throw;
            }

            _forwardArgumentBuffer = forwardArgumentBuffer;
            _shadowArgumentBuffer = shadowArgumentBuffer;
            _instanceBuffer = instanceBuffer;
            _instanceClipBuffer = instanceClipBuffer;
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

        /// <summary>The instance clip buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer InstanceClipBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _instanceClipBuffer;
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
            return TryUpload(commands, objectToWorlds, null, singlePassInstanced);
        }

        /// <summary>
        /// Whether <see cref="TryUpload(VpIndirectCommand[], Matrix4x4[], VpInstanceClip[], bool)"/> would accept
        /// this input, decided without writing anything. It is the very judgement the upload makes — both call
        /// <c>Accepts</c> — so a caller that has to settle two batches before writing either can ask first and know
        /// the answer will not change. Null arrays are refused here rather than thrown, because asking is not
        /// uploading.
        /// </summary>
        public bool CanUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips)
        {
            ThrowIfDisposed();
            return commands != null && objectToWorlds != null && Accepts(commands, objectToWorlds, clips, out _);
        }

        // Every ordinary condition of an upload, in one place: the command count, each command's own numbers, the
        // instance total against the capacity, and one transform -- and one clip record, when clips are given -- per
        // instance. Nothing here writes or reserves anything.
        private bool Accepts(
            VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips, out long instanceTotal)
        {
            instanceTotal = 0;
            if (commands.Length > CommandCapacity)
            {
                return false;
            }

            foreach (VpIndirectCommand command in commands)
            {
                if (command.instanceCount < 0 || command.range.indexStart < 0 || command.range.indexCount < 0)
                {
                    return false;
                }

                instanceTotal += command.instanceCount;
            }

            return instanceTotal <= InstanceCapacity && objectToWorlds.Length == instanceTotal
                && (clips == null || clips.Length == instanceTotal);
        }

        /// <summary>
        /// The same upload, with one <see cref="VpInstanceClip"/> per instance: which parts of up to
        /// <see cref="VpInstanceClip.PlaneCapacity"/> cut planes that instance keeps and how far it is drawn apart,
        /// for the provisional display of DESIGN 5.1. **Null** is how the clips are omitted, and gives the ordinary
        /// display; an array must hold exactly one record per instance, so an empty array is accepted only when
        /// there are no instances. Any other count is rejected, changing nothing, as are the conditions of the other
        /// overload.
        /// <para>
        /// Each record is one fixed-size entry whatever its plane count, so nothing here varies with it: no second
        /// path, keyword or draw, and every instance is uploaded the same way. A count out of range cannot arrive
        /// this far — <see cref="VpInstanceClip.TryKeep"/> refuses more than the capacity when the record is built,
        /// before any update — so what the buffer holds always carries between zero and the capacity planes.
        /// </para>
        /// <para>
        /// The clips go into the fixed-capacity buffer this batch owns, and the culling bounds of the draw are the
        /// instance bounds **moved by the offset**, so a separated fragment is not culled away from where it is
        /// drawn. The bounds stay the parent's, conservatively: what the planes remove is not subtracted from them.
        /// </para>
        /// <para>
        /// The forward and the shadow call bind that one buffer, so within a registration neither can read a
        /// different plane set, count or offset from the other. That is all the binding does: it is **not** a guard
        /// against uploading again between registrations. Finishing every update before the frame is registered
        /// stays the callers responsibility.
        /// </para>
        /// </summary>
        public bool TryUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips, bool singlePassInstanced)
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

            if (!Accepts(commands, objectToWorlds, clips, out long instanceTotal))
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

                    // The shader adds the separation offset after the transform, so the culling bounds must move
                    // with it or a separated fragment can be culled away from where it is actually drawn. The
                    // moved parent bounds are conservative on purpose: what the clip removes is not subtracted.
                    if (clips != null)
                    {
                        instanceBounds.center += clips[i].Offset;
                    }

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

                // No clips given is the ordinary display: every instance takes a record that clips nothing and
                // moves nothing. Each record is written whole, count and all eight planes together, so a record
                // that now carries fewer planes — or none — leaves no plane of an earlier upload in force.
                for (int i = 0; i < instanceTotal; i++)
                {
                    _instanceClips[i] = clips == null ? VpInstanceClip.None : clips[i];
                }

                _instanceClipBuffer.SetData(_instanceClips, 0, 0, (int)instanceTotal);
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

        /// <summary>
        /// The shadow call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera)"/>
        /// alone, over its own range of commands. A caller that must cast one run of commands one-sided and another
        /// two-sided issues the two ranges itself with the material each needs: <c>Cull</c> is a drawing state of the
        /// material, not something an instance carries (DESIGN 5.4).
        /// <para>
        /// The shadow arguments hold the logical instance count whatever the forward ones hold, so a batch uploaded for
        /// Single Pass Instanced casts exactly the instances it would have cast otherwise. The doubling is the forward
        /// call's alone and nothing here multiplies anything.
        /// </para>
        /// </summary>
        public void RenderShadows(
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
            _instanceClipBuffer.Dispose();
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

            // Bound here, where the forward call and the shadow call both pass, so within one registration neither
            // can read a different plane, side or offset from the other. It binds the buffer; it does not stop a
            // caller uploading again between registrations, which is the callers own business to get right.
            properties.SetBuffer(InstanceClipId, _instanceClipBuffer);
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
