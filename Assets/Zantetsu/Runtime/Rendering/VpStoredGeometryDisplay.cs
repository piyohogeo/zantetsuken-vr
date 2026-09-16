using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A static Stage 3 display of geometries a <see cref="VpCpuGeometryStorage"/> owns: it creates the GPU buffers,
    /// transfers the vertices and the indices once, and then issues the indexed indirect draws every frame the caller
    /// asks for, until the caller releases it. Nothing goes through a Unity Mesh.
    /// <para>
    /// The scope is deliberately narrow, and it is what makes the lifetime here explainable. The geometries and the
    /// buffers are settled before the first draw: while the display is shown, no buffer is updated, replaced, retired
    /// or reused, and no geometry is cut. Release happens at the owner's ordinary teardown, which stops issuing new
    /// draws and then frees each buffer exactly once. Growing capacity, updating during a cut and retiring a shared
    /// buffer are all outside this, and nothing here should be read as a statement about them: in particular, a
    /// completed SetData is not a statement about GPU completion, and this type settles no general question about GPU
    /// resource lifetime.
    /// </para>
    /// <para>
    /// The vertex buffer is the storage's committed vertices at their own global numbers, so a stored index addresses
    /// the right vertex with baseVertexIndex left at 0, however many blocks a geometry is made of. Each geometry's
    /// indices are copied through <see cref="VpStoredGeometryDraw"/> under its read lease and placed at the index base
    /// this display assigns it. One draw command per submesh keeps the submesh ranges, and a command is drawn with the
    /// Material its stored material index resolves to: Unity applies one material per call, so commands that need
    /// different materials are issued as separate calls over their own command ranges, never as one call that somehow
    /// picks a material per command.
    /// </para>
    /// <para>
    /// This object owns the two geometry buffers and the batch, and releases them in <see cref="Dispose"/>; a
    /// construction that fails partway releases whatever it had already taken before the failure propagates. It owns
    /// no Material and no Texture: those stay the caller's. Main thread only.
    /// </para>
    /// </summary>
    public sealed class VpStoredGeometryDisplay : IDisposable
    {
        /// <summary>One geometry of the display: its uploaded indices, its commands and the material of each.</summary>
        public readonly struct Entry
        {
            internal Entry(VpStoredGeometry geometry, int commandStart, int commandCount, int indexBase, Bounds localBounds)
            {
                this.geometry = geometry;
                this.commandStart = commandStart;
                this.commandCount = commandCount;
                this.indexBase = indexBase;
                this.localBounds = localBounds;
            }

            public readonly VpStoredGeometry geometry;

            /// <summary>Where this geometry's commands begin in the batch; its submeshes follow in order.</summary>
            public readonly int commandStart;

            public readonly int commandCount;

            /// <summary>Where this geometry's indices were placed in the index buffer.</summary>
            public readonly int indexBase;

            public readonly Bounds localBounds;
        }

        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly Entry[] _entries;
        private readonly Material[] _commandMaterials;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private bool _disposed;

        private VpStoredGeometryDisplay(
            VpGpuIndexedGeometryBuffers buffers,
            VpIndexedIndirectDrawBatch batch,
            Entry[] entries,
            Material[] commandMaterials)
        {
            _buffers = buffers;
            _batch = batch;
            _entries = entries;
            _commandMaterials = commandMaterials;
        }

        public int EntryCount => _entries.Length;

        public int CommandCount => _commandMaterials.Length;

        public bool IsDisposed => _disposed;

        /// <summary>The geometries of the display, in the order they were given.</summary>
        public Entry GetEntry(int index)
        {
            ThrowIfDisposed();
            return _entries[index];
        }

        /// <summary>The Material each command is drawn with, in command order. The Materials stay the caller's.</summary>
        public Material GetCommandMaterial(int command)
        {
            ThrowIfDisposed();
            return _commandMaterials[command];
        }

        /// <summary>
        /// Builds the display for <paramref name="geometries"/>, all of which must be owned by
        /// <paramref name="storage"/>, drawn at <paramref name="objectToWorld"/> one transform each, with
        /// <paramref name="materialsBySourceIndex"/> answering for every stored material index they use.
        /// <para>
        /// Returns false with a null display, having released every buffer it had taken, when an argument is null or
        /// the counts disagree; when a geometry cannot be prepared (see
        /// <see cref="VpStoredGeometryDraw.TryBuildUpload"/>); when a stored material index has no Material; or when
        /// the vertices or the indices do not fit the buffers it made for them. Every geometry is settled here, before
        /// any draw: this is the only point at which the buffers are written.
        /// </para>
        /// </summary>
        public static bool TryCreate(
            VpCpuGeometryStorage storage,
            IReadOnlyList<VpStoredGeometry> geometries,
            IReadOnlyList<Matrix4x4> objectToWorld,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            out VpStoredGeometryDisplay display)
        {
            display = null;
            if (storage == null
                || geometries == null
                || objectToWorld == null
                || materialsBySourceIndex == null
                || geometries.Count == 0
                || objectToWorld.Count != geometries.Count)
            {
                return false;
            }

            // ---- everything the GPU will need, prepared on the CPU first, so a refusal costs no buffer at all
            var uploads = new VpStoredGeometryUpload[geometries.Count];
            var entries = new Entry[geometries.Count];
            var commands = new List<VpIndirectCommand>();
            var commandMaterials = new List<Material>();
            int indexBase = 0;
            for (int g = 0; g < geometries.Count; g++)
            {
                if (!VpStoredGeometryDraw.TryBuildUpload(storage, geometries[g], indexBase, out VpStoredGeometryUpload upload))
                {
                    return false;
                }

                uploads[g] = upload;
                entries[g] = new Entry(geometries[g], commands.Count, upload.Commands.Length, indexBase, upload.LocalBounds);
                for (int c = 0; c < upload.Commands.Length; c++)
                {
                    if (!materialsBySourceIndex.TryGetValue(upload.MaterialIndices[c], out Material material) || material == null)
                    {
                        return false;
                    }

                    commands.Add(upload.Commands[c]);
                    commandMaterials.Add(material);
                }

                indexBase += upload.Indices.Length;
            }

            NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
            if (vertices.Length == 0 || indexBase == 0)
            {
                return false;
            }

            // ---- from here GPU resources exist, so every failure gives back what it took
            VpGpuIndexedGeometryBuffers buffers = null;
            VpIndexedIndirectDrawBatch batch = null;
            try
            {
                // Every command draws one instance, its geometry's own transform, so the instances number as many as
                // the commands do and not as many as the geometries: a geometry of several submeshes contributes one
                // instance per submesh, each repeating that geometry's transform.
                buffers = new VpGpuIndexedGeometryBuffers(vertices.Length, indexBase);
                batch = new VpIndexedIndirectDrawBatch(commands.Count, commands.Count);

                // The vertices go in at their own global numbers: that is what lets baseVertexIndex stay 0.
                buffers.VertexBuffer.SetData(vertices.ToArray());
                for (int g = 0; g < uploads.Length; g++)
                {
                    VpStoredGeometryUpload upload = uploads[g];
                    buffers.IndexBuffer.SetData(upload.Indices, 0, upload.IndexBase, upload.Indices.Length);
                }

                var transforms = new Matrix4x4[objectToWorld.Count];
                for (int i = 0; i < transforms.Length; i++)
                {
                    transforms[i] = objectToWorld[i];
                }

                // One instance per command, holding the transform of the geometry that command belongs to.
                var commandArray = commands.ToArray();
                var perCommandTransforms = new List<Matrix4x4>();
                for (int g = 0; g < entries.Length; g++)
                {
                    for (int c = 0; c < entries[g].commandCount; c++)
                    {
                        perCommandTransforms.Add(transforms[g]);
                    }
                }

                if (!batch.TryUpload(commandArray, perCommandTransforms.ToArray(), false))
                {
                    return false;
                }

                // Last: once this is assigned, the display owns the buffers and the batch.
                display = new VpStoredGeometryDisplay(buffers, batch, entries, commandMaterials.ToArray());
                return true;
            }
            finally
            {
                // One place for every way out: a refusal and an exception both give back what was taken, and a
                // success gives back nothing because ownership has moved to the display.
                if (display == null)
                {
                    batch?.Dispose();
                    buffers?.Dispose();
                }
            }
        }

        /// <summary>
        /// Issues this frame's draws: one forward call per run of commands that share a Material, in command order, so
        /// that each command is drawn with its own material. Unity applies one Material per call, so a run is exactly
        /// what one call can cover. Issues nothing after disposal.
        /// </summary>
        public void RenderForward(int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            int start = 0;
            while (start < _commandMaterials.Length)
            {
                int end = start + 1;
                while (end < _commandMaterials.Length && ReferenceEquals(_commandMaterials[end], _commandMaterials[start]))
                {
                    end++;
                }

                _batch.RenderForward(_commandMaterials[start], _properties, _buffers, layer, start, end - start, camera);
                start = end;
            }
        }

        /// <summary>
        /// The same commands as a shadow call with <paramref name="shadowMaterial"/>, for a caller that wants these
        /// geometries to cast the main light's shadow. The shadow caster pass draws no colour, so one call covers every
        /// command whatever their materials.
        /// </summary>
        public void RenderShadows(Material shadowMaterial, int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            if (shadowMaterial == null || _commandMaterials.Length == 0)
            {
                return;
            }

            _batch.RenderShadows(shadowMaterial, _properties, _buffers, layer, 0, _commandMaterials.Length, camera);
        }

        /// <summary>
        /// The owner's ordinary teardown: no further draw is issued from here, and the buffers this display made are
        /// released once. The caller is responsible for having stopped calling the render methods; disposing again does
        /// nothing. This says nothing about work already submitted to the GPU, which is why the display is only used
        /// for geometry that is settled before the first draw and untouched until the owner ends.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _batch.Dispose();
            _buffers.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpStoredGeometryDisplay));
            }
        }
    }
}
