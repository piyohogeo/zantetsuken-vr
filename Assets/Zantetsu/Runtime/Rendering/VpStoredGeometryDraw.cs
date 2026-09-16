using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// What one geometry of a <see cref="VpCpuGeometryStorage"/> needs in order to be drawn by the Stage 3 indexed
    /// indirect path: the indices to upload, where they are to sit in the index buffer, one draw command per submesh,
    /// and the source material index of each. It is plain CPU data that owns nothing of the storage and nothing of the
    /// GPU; the caller uploads it and keeps the buffers.
    /// <para>
    /// What it owns is the index copy and the commands, and that is all. **It carries no snapshot of the vertices.**
    /// The values in <see cref="Indices"/> are the storage's global vertex numbers, so they mean nothing on their own:
    /// the vertices must be transferred separately, in a layout that keeps each vertex at its own global number, which
    /// is what lets the Stage 3 shader read them with baseVertexIndex left at 0. Uploading these indices against a
    /// vertex buffer packed or renumbered some other way draws the wrong vertices, and this object cannot tell.
    /// </para>
    /// </summary>
    public sealed class VpStoredGeometryUpload
    {
        internal VpStoredGeometryUpload(
            uint[] indices,
            int indexBase,
            VpIndirectCommand[] commands,
            int[] materialIndices,
            Bounds localBounds,
            int referencedVertexStart,
            int referencedVertexCount)
        {
            Indices = indices;
            IndexBase = indexBase;
            Commands = commands;
            MaterialIndices = materialIndices;
            LocalBounds = localBounds;
            ReferencedVertexStart = referencedVertexStart;
            ReferencedVertexCount = referencedVertexCount;
        }

        /// <summary>
        /// The geometry's indices, copied out of the storage exactly as stored: global vertex numbers, in the order the
        /// submeshes cover them, so the triangle corner order and therefore the winding are unchanged. They are to be
        /// uploaded at <see cref="IndexBase"/> of the index buffer the draw reads.
        /// </summary>
        public uint[] Indices { get; }

        /// <summary>Where <see cref="Indices"/> belongs in the index buffer; the commands already account for it.</summary>
        public int IndexBase { get; }

        /// <summary>
        /// One command per submesh of the geometry, in the geometry's submesh order. Each command's index start is a
        /// position in the index buffer, that is <see cref="IndexBase"/> plus the submesh's offset inside the
        /// geometry's own published range; the two numberings are never added the other way round.
        /// </summary>
        public VpIndirectCommand[] Commands { get; }

        /// <summary>The source material index of each command, in the same order. Resolving it to a Material is the caller's.</summary>
        public int[] MaterialIndices { get; }

        /// <summary>The bounds of the vertices the geometry's indices actually reference, in the storage's own frame.</summary>
        public Bounds LocalBounds { get; }

        /// <summary>The lowest global vertex number the indices reference.</summary>
        public int ReferencedVertexStart { get; }

        /// <summary>How many global vertex numbers the referenced span covers; not every one of them need be used.</summary>
        public int ReferencedVertexCount { get; }
    }

    /// <summary>
    /// Prepares a stored geometry for the Stage 3 indexed indirect draw path (DESIGN 4.5.4 / 4.5.5) without going
    /// through a Unity Mesh: the vertices the shader pulls are the storage's own committed array, and the indices are
    /// copied out under the geometry's read lease. The lease is held only for that copy and returned before this
    /// returns, which is exactly what DESIGN 4.5.3 gives it: it protects a temporary read by the CPU or a transfer
    /// source and says nothing about a submitted draw having finished.
    /// <para>
    /// The numbering needs no remapping here, unlike the Unity Mesh path. Stage 3 binds the whole committed vertex
    /// array as the structured buffer and sets baseVertexIndex to 0, so a stored index, which is already a global
    /// vertex number, addresses the right vertex directly however many blocks the geometry is made of. What does need
    /// care is the index position: a submesh's <see cref="VpGeometrySubmesh.indexOffset"/> is relative to the
    /// geometry's own published range, which is the leased view, and the place that range physically occupies in the
    /// storage's index buffer is a different number that is never added to it. The command's index start is instead
    /// the caller's upload base plus that relative offset.
    /// </para>
    /// <para>
    /// Position, normal, uv0 and the corner order are carried across untouched: nothing is welded, no normal is
    /// recalculated and no triangle is reordered. Submesh order and the stored material index are kept, and no
    /// material of any kind is invented, so a cut geometry's caps keep the submesh and the material of the surface
    /// they belong to. This type creates no GPU resource and takes no ownership in the storage. Main thread only.
    /// </para>
    /// </summary>
    public static class VpStoredGeometryDraw
    {
        /// <summary>
        /// Builds the upload for the geometry, with its indices destined for <paramref name="indexBase"/> of the index
        /// buffer. Returns false with a null upload, having copied nothing and holding no lease, when the storage is
        /// null; when the base is negative; when the base and the geometry's indices together would run past what an
        /// int index position can express, which would otherwise hand back commands starting at a negative position;
        /// when the geometry is not one the storage owns with readable metadata and a topology mapping; when it has no
        /// submesh or no index; when its index range is not Published, so no read lease can be taken; when its
        /// submeshes do not cover the leased range once, in order and in whole triangles; or when an index falls
        /// outside the geometry's vertex blocks.
        /// <para>
        /// The result owns its index copy and its commands but no vertex data; see <see cref="VpStoredGeometryUpload"/>
        /// for what the caller still has to transfer.
        /// </para>
        /// </summary>
        public static bool TryBuildUpload(
            VpCpuGeometryStorage storage,
            VpStoredGeometry geometry,
            int indexBase,
            out VpStoredGeometryUpload upload)
        {
            upload = null;
            if (storage == null
                || indexBase < 0
                || !storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _)
                || !storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes)
                || blocks.Length == 0
                || submeshes.Length == 0)
            {
                return false;
            }

            if (!storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view))
            {
                return false;
            }

            uint[] indices;
            try
            {
                // Checked before the copy, and in 64 bit: the last index of this upload has to stay expressible as an
                // int position, or a command would come back starting at a negative one and be taken for a good draw.
                // Every submesh offset is below the view length, so this one bound covers all the commands as well.
                if (view.Length == 0
                    || (long)indexBase + view.Length > int.MaxValue
                    || !DoSubmeshesCoverTheView(submeshes, view.Length))
                {
                    return false;
                }

                // The whole point of the lease: the transfer source is read here, once, into memory of our own.
                indices = new uint[view.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = view[i];
                }
            }
            finally
            {
                // The CPU copy is complete, so the lease has done its work. It never stood for a draw having finished.
                storage.TryReleaseIndexReadLease(lease);
            }

            if (!TryMeasure(storage, blocks, indices, out Bounds localBounds, out int referencedStart, out int referencedCount))
            {
                return false;
            }

            var commands = new VpIndirectCommand[submeshes.Length];
            var materials = new int[submeshes.Length];
            for (int s = 0; s < submeshes.Length; s++)
            {
                VpGeometrySubmesh submesh = submeshes[s];
                if (!TryMeasureRange(storage, blocks, indices, submesh.indexOffset, submesh.indexCount, localBounds, out Bounds submeshBounds))
                {
                    return false;
                }

                // indexOffset is relative to the geometry's own published range; the upload base is the only thing added.
                var range = new VpGeometryRange(referencedStart, referencedCount, indexBase + submesh.indexOffset, submesh.indexCount);
                commands[s] = new VpIndirectCommand(range, submeshBounds, 1);
                materials[s] = submesh.materialIndex;
            }

            upload = new VpStoredGeometryUpload(indices, indexBase, commands, materials, localBounds, referencedStart, referencedCount);
            return true;
        }

        /// <summary>The submeshes must cover the leased view once, in order, with no gap or overlap, in whole triangles.</summary>
        private static bool DoSubmeshesCoverTheView(NativeArray<VpGeometrySubmesh>.ReadOnly submeshes, int viewLength)
        {
            long covered = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                VpGeometrySubmesh submesh = submeshes[s];
                if (submesh.indexOffset != covered || submesh.indexCount < 0 || submesh.indexCount % 3 != 0)
                {
                    return false;
                }

                covered += submesh.indexCount;
            }

            return covered == viewLength;
        }

        /// <summary>
        /// The bounds and the referenced global vertex span of the whole geometry. Every index must fall inside one of
        /// the geometry's blocks: a number outside them is refused rather than drawn from whatever happens to be there.
        /// </summary>
        private static bool TryMeasure(
            VpCpuGeometryStorage storage,
            NativeArray<VpGeometryVertexBlock>.ReadOnly blocks,
            uint[] indices,
            out Bounds localBounds,
            out int referencedStart,
            out int referencedCount)
        {
            localBounds = default;
            referencedStart = 0;
            referencedCount = 0;
            NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
            long lowest = long.MaxValue;
            long highest = long.MinValue;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            for (int i = 0; i < indices.Length; i++)
            {
                uint global = indices[i];
                if (!IsInABlock(blocks, global) || global >= (uint)committed.Length)
                {
                    return false;
                }

                Vector3 position = committed[(int)global].position;
                if (i == 0)
                {
                    min = position;
                    max = position;
                }
                else
                {
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                lowest = Math.Min(lowest, global);
                highest = Math.Max(highest, global);
            }

            localBounds = new Bounds((min + max) * 0.5f, max - min);
            referencedStart = (int)lowest;
            referencedCount = (int)(highest - lowest + 1);
            return true;
        }

        /// <summary>The bounds of one submesh's own indices; an empty submesh takes the geometry's bounds, drawing nothing.</summary>
        private static bool TryMeasureRange(
            VpCpuGeometryStorage storage,
            NativeArray<VpGeometryVertexBlock>.ReadOnly blocks,
            uint[] indices,
            int offset,
            int count,
            Bounds fallback,
            out Bounds bounds)
        {
            bounds = fallback;
            if (count == 0)
            {
                return true;
            }

            NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                uint global = indices[offset + i];
                if (!IsInABlock(blocks, global) || global >= (uint)committed.Length)
                {
                    return false;
                }

                Vector3 position = committed[(int)global].position;
                if (i == 0)
                {
                    min = position;
                    max = position;
                }
                else
                {
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }
            }

            bounds = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }

        private static bool IsInABlock(NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, uint global)
        {
            for (int b = 0; b < blocks.Length; b++)
            {
                VpGeometryVertexBlock block = blocks[b];
                if (global >= (uint)block.vertexStart && global < (uint)(block.vertexStart + block.vertexCount))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
