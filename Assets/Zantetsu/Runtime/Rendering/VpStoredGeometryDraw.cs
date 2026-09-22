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
            if (!TryBuild(
                    storage,
                    geometry,
                    indexBase,
                    true,
                    out uint[] indices,
                    out VpIndirectCommand[] commands,
                    out int[] materials,
                    out Bounds localBounds,
                    out int referencedStart,
                    out int referencedCount))
            {
                return false;
            }

            upload = new VpStoredGeometryUpload(indices, indexBase, commands, materials, localBounds, referencedStart, referencedCount);
            return true;
        }

        /// <summary>
        /// The commands alone, for a caller whose transfer reads the storage where the indices lie instead of taking a
        /// copy of them: the same commands, source material indices and bounds as <see cref="TryBuildUpload"/>, built
        /// under the same read lease and refused under the same conditions, but **with no index array made at all**.
        /// <para>
        /// The caller is then responsible for getting those indices to the GPU by some other route, at the very
        /// positions the commands name. <see cref="VpStoredGeometryTransfer"/> is that route: it transfers the storage's
        /// own memory to the position it already occupies, which is what <paramref name="indexBase"/> has to be for the
        /// commands to address it.
        /// </para>
        /// </summary>
        public static bool TryBuildCommands(
            VpCpuGeometryStorage storage,
            VpStoredGeometry geometry,
            int indexBase,
            out VpIndirectCommand[] commands,
            out int[] materialIndices,
            out Bounds localBounds)
        {
            return TryBuild(
                storage,
                geometry,
                indexBase,
                false,
                out _,
                out commands,
                out materialIndices,
                out localBounds,
                out _,
                out _);
        }

        /// <summary>
        /// The one build both entry points use. The extent -- the referenced vertex span and the bounds, per submesh
        /// and whole -- is read from the storage's record, which every producer writes; nothing is measured from the
        /// leased view here, and a geometry without a record is refused. The lease protects the index view, which
        /// is copied into an array only for a caller that asked for one.
        /// </summary>
        private static bool TryBuild(
            VpCpuGeometryStorage storage,
            VpStoredGeometry geometry,
            int indexBase,
            bool copyIndices,
            out uint[] indices,
            out VpIndirectCommand[] commands,
            out int[] materialIndices,
            out Bounds localBounds,
            out int referencedStart,
            out int referencedCount)
        {
            indices = null;
            commands = null;
            materialIndices = null;
            localBounds = default;
            referencedStart = 0;
            referencedCount = 0;
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

            try
            {
                // Checked first, and in 64 bit: the last index of this upload has to stay expressible as an int
                // position, or a command would come back starting at a negative one and be taken for a good draw.
                // Every submesh offset is below the view length, so this one bound covers all the commands as well.
                if (view.Length == 0
                    || (long)indexBase + view.Length > int.MaxValue
                    || !DoSubmeshesCoverTheView(submeshes, view.Length))
                {
                    return false;
                }

                // What the geometry's producer recorded is taken as it stands: the same positions, measured once
                // where the indices were written, by the producer that validated them. Nothing walks the indices
                // here; what is checked is that the record is this geometry's (generation) and covers its submeshes.
                if (!storage.TryGetPublishedExtent(geometry, out referencedStart, out referencedCount, out localBounds)
                    || !storage.TryGetSubmeshBounds(geometry, out NativeArray<VpGeometryBounds>.ReadOnly recordedSubmeshBounds)
                    || recordedSubmeshBounds.Length != submeshes.Length)
                {
                    return false;
                }

                var builtCommands = new VpIndirectCommand[submeshes.Length];
                var builtMaterials = new int[submeshes.Length];
                for (int s = 0; s < submeshes.Length; s++)
                {
                    VpGeometrySubmesh submesh = submeshes[s];

                    // A submesh with no index draws nothing; its command carries the geometry's bounds.
                    Bounds submeshBounds = submesh.indexCount > 0 ? recordedSubmeshBounds[s].ToBounds() : localBounds;

                    // indexOffset is relative to the geometry's own published range; the upload base is the only thing added.
                    var range = new VpGeometryRange(referencedStart, referencedCount, indexBase + submesh.indexOffset, submesh.indexCount);
                    builtCommands[s] = new VpIndirectCommand(range, submeshBounds, 1);
                    builtMaterials[s] = submesh.materialIndex;
                }

                if (copyIndices)
                {
                    // The other use of the lease: the transfer source is read here, once, into memory of our own.
                    var copied = new uint[view.Length];
                    for (int i = 0; i < copied.Length; i++)
                    {
                        copied[i] = view[i];
                    }

                    indices = copied;
                }

                commands = builtCommands;
                materialIndices = builtMaterials;
                return true;
            }
            finally
            {
                // The reading is complete, so the lease has done its work. It never stood for a draw having finished.
                storage.TryReleaseIndexReadLease(lease);
            }
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
    }
}
