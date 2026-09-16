using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Builds a Unity Mesh from one geometry a <see cref="VpCpuGeometryStorage"/> owns, for showing it through the
    /// ordinary Unity display path (DESIGN 4.5.2 / 4.5.5). It is a copy out, not a second owner: the storage's index
    /// range is read under a read lease that is returned before this returns, and the Mesh that comes back shares
    /// nothing with the storage afterwards, so retiring the geometry, reusing its index space or cutting it again
    /// leaves the Mesh exactly as it was. **The caller owns that Mesh and must destroy it.**
    /// <para>
    /// Nothing about the geometry is reinterpreted on the way out. Position, normal and uv0 are carried across as they
    /// are stored; no normal or tangent is recalculated, no vertex is welded, and the corner order of every triangle is
    /// kept, so the winding the cut produced is the winding Unity draws. Two render vertices that share a position but
    /// differ in normal or uv0 are two different global vertex numbers and stay two vertices, which is what keeps an
    /// attribute seam a seam.
    /// </para>
    /// <para>
    /// The numbering is converted explicitly. A stored geometry's vertices are the ordered union of its blocks, which
    /// need not be contiguous and for a cut result are not — the parent's blocks and the block the cut appended — so a
    /// Mesh vertex number is not a global one. Every global number the geometry's indices use is looked up in the
    /// blocks and given a local number, in block order and ascending within a block; a number outside every block is
    /// refused rather than guessed at. Index positions are converted just as carefully: a submesh's
    /// <see cref="VpGeometrySubmesh.indexOffset"/> is relative to the geometry's own published range, which is exactly
    /// the leased view, and the physical position of that range inside the storage's index buffer is never added to it.
    /// </para>
    /// <para>
    /// Materials stay out of the storage, so the caller supplies them: <see cref="TryResolveMaterials"/> resolves each
    /// submesh by the source material index the geometry recorded, never by its submesh ordinal, and refuses a geometry
    /// whose material it was not given rather than falling back to another one. Main thread only, as the storage is.
    /// </para>
    /// </summary>
    public static class VpStoredGeometryMesh
    {
        /// <summary>
        /// Copies the geometry into a new Mesh whose submeshes are the geometry's own, in order. Returns false with a
        /// null Mesh, having created nothing and holding no lease, when the storage is null; when the geometry is not
        /// one the storage owns with readable metadata and a topology mapping; when its index range is not Published,
        /// so that no read lease can be taken; when its submeshes do not cover the leased range once, in order and in
        /// whole triangles; or when an index falls outside the geometry's blocks.
        /// </summary>
        public static bool TryCreateMesh(VpCpuGeometryStorage storage, VpStoredGeometry geometry, out Mesh mesh)
        {
            mesh = null;
            if (storage == null
                || !storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _)
                || !storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes)
                || blocks.Length == 0
                || submeshes.Length == 0)
            {
                return false;
            }

            if (!storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices))
            {
                return false;
            }

            try
            {
                // The lease's view is the geometry's whole published range, which is what the submesh offsets address.
                if (!DoSubmeshesCoverTheView(submeshes, indices.Length))
                {
                    return false;
                }

                // Local numbers per block, -1 until an index actually uses the vertex, so only referenced vertices are
                // copied and a vertex two blocks apart never collapses into one.
                int[][] localOfBlock = MarkUsedVertices(blocks, indices, out int usedVertexCount);
                if (localOfBlock == null)
                {
                    return false;
                }

                Mesh built = Build(storage, blocks, submeshes, indices, localOfBlock, usedVertexCount);
                if (built == null)
                {
                    return false;
                }

                mesh = built;
                return true;
            }
            finally
            {
                // The copy is finished either way: the storage is free to retire or reuse the range from here.
                storage.TryReleaseIndexReadLease(lease);
            }
        }

        /// <summary>
        /// The Material of each submesh of the geometry, in submesh order, taken from
        /// <paramref name="bySourceMaterialIndex"/> by the source material index the submesh recorded. Returns false
        /// with a null array when the storage is null, when the geometry's submeshes cannot be read, when the map is
        /// null, or when it holds no Material — or a null one — for a material index a submesh uses. There is no
        /// fallback: a geometry whose materials are not all supplied is not shown with someone else's.
        /// </summary>
        public static bool TryResolveMaterials(
            VpCpuGeometryStorage storage,
            VpStoredGeometry geometry,
            IReadOnlyDictionary<int, Material> bySourceMaterialIndex,
            out Material[] materials)
        {
            materials = null;
            if (storage == null
                || bySourceMaterialIndex == null
                || !storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes))
            {
                return false;
            }

            var resolved = new Material[submeshes.Length];
            for (int s = 0; s < submeshes.Length; s++)
            {
                if (!bySourceMaterialIndex.TryGetValue(submeshes[s].materialIndex, out Material material) || material == null)
                {
                    return false;
                }

                resolved[s] = material;
            }

            materials = resolved;
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
        /// Gives every global vertex number the indices use a local number, in block order and ascending inside a
        /// block. Returns null when an index is outside every block of the geometry.
        /// </summary>
        private static int[][] MarkUsedVertices(
            NativeArray<VpGeometryVertexBlock>.ReadOnly blocks,
            NativeArray<uint>.ReadOnly indices,
            out int usedVertexCount)
        {
            usedVertexCount = 0;
            var localOfBlock = new int[blocks.Length][];
            for (int b = 0; b < blocks.Length; b++)
            {
                var local = new int[blocks[b].vertexCount];
                for (int v = 0; v < local.Length; v++)
                {
                    local[v] = -1;
                }

                localOfBlock[b] = local;
            }

            for (int i = 0; i < indices.Length; i++)
            {
                if (!TryFindBlock(blocks, indices[i], out int block, out int offset))
                {
                    return null;
                }

                if (localOfBlock[block][offset] < 0)
                {
                    localOfBlock[block][offset] = 0;
                }
            }

            // Numbering comes after marking, so the order is the geometry's own and not the order the indices happen
            // to mention the vertices in.
            int next = 0;
            for (int b = 0; b < localOfBlock.Length; b++)
            {
                int[] local = localOfBlock[b];
                for (int v = 0; v < local.Length; v++)
                {
                    if (local[v] == 0)
                    {
                        local[v] = next++;
                    }
                }
            }

            usedVertexCount = next;
            return usedVertexCount > 0 ? localOfBlock : null;
        }

        private static bool TryFindBlock(NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, uint global, out int block, out int offset)
        {
            for (int b = 0; b < blocks.Length; b++)
            {
                VpGeometryVertexBlock candidate = blocks[b];
                if (global >= (uint)candidate.vertexStart && global < (uint)(candidate.vertexStart + candidate.vertexCount))
                {
                    block = b;
                    offset = (int)(global - (uint)candidate.vertexStart);
                    return true;
                }
            }

            block = -1;
            offset = -1;
            return false;
        }

        /// <summary>Copies the attributes and the triangles into a new Mesh. Returns null when a vertex number is not one this copy gave out.</summary>
        private static Mesh Build(
            VpCpuGeometryStorage storage,
            NativeArray<VpGeometryVertexBlock>.ReadOnly blocks,
            NativeArray<VpGeometrySubmesh>.ReadOnly submeshes,
            NativeArray<uint>.ReadOnly indices,
            int[][] localOfBlock,
            int usedVertexCount)
        {
            NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
            var positions = new Vector3[usedVertexCount];
            var normals = new Vector3[usedVertexCount];
            var uv0 = new Vector2[usedVertexCount];
            for (int b = 0; b < localOfBlock.Length; b++)
            {
                VpGeometryVertexBlock block = blocks[b];
                int[] local = localOfBlock[b];
                for (int v = 0; v < local.Length; v++)
                {
                    int slot = local[v];
                    if (slot < 0)
                    {
                        continue;
                    }

                    int global = block.vertexStart + v;
                    if ((uint)global >= (uint)committed.Length)
                    {
                        return null;
                    }

                    VpRenderVertex vertex = committed[global];

                    // Carried across as stored: no renormalisation, no recalculation, no weld.
                    positions[slot] = vertex.position;
                    normals[slot] = vertex.normal;
                    uv0[slot] = vertex.uv0;
                }
            }

            var triangles = new int[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                if (!TryFindBlock(blocks, indices[i], out int block, out int offset))
                {
                    return null;
                }

                // The corner order of the index list is the corner order of the Mesh: the winding is not touched.
                triangles[i] = localOfBlock[block][offset];
            }

            // From here the Mesh exists, so every step that fills it is guarded: a half-filled Mesh is destroyed
            // rather than left behind, because it would belong to no one once the exception leaves this call.
            var mesh = new Mesh();
            try
            {
                mesh.indexFormat = usedVertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.subMeshCount = submeshes.Length;
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uv0);
                for (int s = 0; s < submeshes.Length; s++)
                {
                    VpGeometrySubmesh submesh = submeshes[s];
                    var submeshTriangles = new int[submesh.indexCount];
                    for (int i = 0; i < submesh.indexCount; i++)
                    {
                        submeshTriangles[i] = triangles[submesh.indexOffset + i];
                    }

                    mesh.SetTriangles(submeshTriangles, s, false);
                }

                mesh.RecalculateBounds();

                // Last: the caller owns the Mesh only once it is whole.
                return mesh;
            }
            catch
            {
                Destroy(mesh);
                throw;
            }
        }

        /// <summary>Destroys a Mesh this call made, whichever way the editor or a player is running it.</summary>
        private static void Destroy(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(mesh);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
