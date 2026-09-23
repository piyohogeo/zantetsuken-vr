using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Stage 3 probe: the shadow issue of a <see cref="VpIndexedIndirectDrawBatch"/> split into conservative fixed XZ tiles,
    /// built on the CPU at upload with no GPU culling. Each instance is assigned, by the XZ centre of its world bounds, to one
    /// cell of a tilesX x tilesZ grid spanning the union of all instance bounds. Each non-empty tile becomes a shadow-only
    /// indexed indirect batch holding only the geometry commands that have instances in it, in command order, and those
    /// instances' transforms; its world bounds are the union of its own instances' full bounds, including any part beyond
    /// the cell. Every tile uses the caller's vertex and hardware index buffers.
    /// <para>
    /// A frame issues the caller's single forward batch once and then one shadow call per non-empty tile, with the
    /// shadow-caster-only material; it writes no buffer and allocates nothing. The set owns its tile batches and their
    /// property blocks. Uploading again replaces them only when the upload succeeds. It never owns the forward batch or the
    /// geometry buffers, and the caller keeps the forward batch uploaded with the same commands and transforms. After
    /// <see cref="Dispose"/>, uploading and rendering throw; disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpIndexedShadowTileSet : IDisposable
    {
        private VpIndexedIndirectDrawBatch[] _tiles = Array.Empty<VpIndexedIndirectDrawBatch>();
        private MaterialPropertyBlock[] _tileProperties = Array.Empty<MaterialPropertyBlock>();
        private int[] _tileCells = Array.Empty<int>();
        private bool _disposed;

        public VpIndexedShadowTileSet(int tilesX, int tilesZ)
        {
            if (tilesX <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tilesX), tilesX, "Must be positive.");
            }

            if (tilesZ <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tilesZ), tilesZ, "Must be positive.");
            }

            TilesX = tilesX;
            TilesZ = tilesZ;
        }

        public int TilesX { get; }

        public int TilesZ { get; }

        /// <summary>The number of non-empty tiles: the shadow calls issued per frame.</summary>
        public int TileBatchCount
        {
            get
            {
                ThrowIfDisposed();
                return _tiles.Length;
            }
        }

        /// <summary>The number of logical instances over all tiles.</summary>
        public int InstanceCount { get; private set; }

        /// <summary>A non-empty tile's batch, for reading back inside this assembly. Not to be uploaded or rendered directly.</summary>
        internal VpIndexedIndirectDrawBatch GetTile(int index)
        {
            ThrowIfDisposed();
            return _tiles[index];
        }

        /// <summary>A non-empty tile's grid cell, x + z * <see cref="TilesX"/>.</summary>
        internal int GetTileCell(int index)
        {
            ThrowIfDisposed();
            return _tileCells[index];
        }

        /// <summary>
        /// Rebuilds the tiles from commands and transforms laid out as for <see cref="VpIndexedIndirectDrawBatch.TryUpload"/>.
        /// Returns false, keeping the previous tiles, when an instance count or range value is negative or the transform
        /// count is not the sum of the instance counts. Tile batches created by a call that fails or throws are released
        /// before it returns.
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

            long total = 0;
            foreach (VpIndirectCommand command in commands)
            {
                if (command.instanceCount < 0 || command.range.indexStart < 0 || command.range.indexCount < 0)
                {
                    return false;
                }

                total += command.instanceCount;
            }

            if (objectToWorlds.Length != total)
            {
                return false;
            }

            int instanceTotal = (int)total;
            var instanceBounds = new Bounds[instanceTotal];
            var instanceCommand = new int[instanceTotal];
            Bounds union = default;
            int instance = 0;
            for (int c = 0; c < commands.Length; c++)
            {
                for (int k = 0; k < commands[c].instanceCount; k++, instance++)
                {
                    instanceBounds[instance] = VpDirectDraw.WorldBounds(commands[c].localBounds, objectToWorlds[instance]);
                    instanceCommand[instance] = c;
                    if (instance == 0)
                    {
                        union = instanceBounds[instance];
                    }
                    else
                    {
                        union.Encapsulate(instanceBounds[instance]);
                    }
                }
            }

            int cellCount = TilesX * TilesZ;
            var instanceCell = new int[instanceTotal];
            var cellCommandCounts = new int[cellCount, commands.Length];
            var cellTotals = new int[cellCount];
            for (int i = 0; i < instanceTotal; i++)
            {
                Vector3 centre = instanceBounds[i].center;
                int cell = Cell(centre.x, union.min.x, union.size.x, TilesX) + Cell(centre.z, union.min.z, union.size.z, TilesZ) * TilesX;
                instanceCell[i] = cell;
                cellCommandCounts[cell, instanceCommand[i]]++;
                cellTotals[cell]++;
            }

            var tiles = new List<VpIndexedIndirectDrawBatch>();
            var cells = new List<int>();
            try
            {
                for (int cell = 0; cell < cellCount; cell++)
                {
                    if (cellTotals[cell] == 0)
                    {
                        continue;
                    }

                    var tileCommands = new List<VpIndirectCommand>();
                    for (int c = 0; c < commands.Length; c++)
                    {
                        if (cellCommandCounts[cell, c] > 0)
                        {
                            tileCommands.Add(new VpIndirectCommand(commands[c].range, commands[c].localBounds, cellCommandCounts[cell, c]));
                        }
                    }

                    // Instances are already grouped by command, so their order within the cell is the tile's command order.
                    var transforms = new Matrix4x4[cellTotals[cell]];
                    int written = 0;
                    for (int i = 0; i < instanceTotal; i++)
                    {
                        if (instanceCell[i] == cell)
                        {
                            transforms[written++] = objectToWorlds[i];
                        }
                    }

                    var tile = new VpIndexedIndirectDrawBatch(tileCommands.Count, transforms.Length);
                    tiles.Add(tile);
                    if (!tile.TryUpload(tileCommands.ToArray(), transforms, false))
                    {
                        ReleaseAll(tiles);
                        return false;
                    }

                    cells.Add(cell);
                }
            }
            catch
            {
                ReleaseAll(tiles);
                throw;
            }

            ReleaseAll(_tiles);
            _tiles = tiles.ToArray();
            _tileCells = cells.ToArray();
            _tileProperties = new MaterialPropertyBlock[_tiles.Length];
            for (int t = 0; t < _tiles.Length; t++)
            {
                _tileProperties[t] = new MaterialPropertyBlock();
            }

            InstanceCount = instanceTotal;
            return true;
        }

        /// <summary>
        /// Queues <paramref name="forwardBatch"/>'s forward call over all its commands, then one shadow call per non-empty
        /// tile with <paramref name="shadowMaterial"/>, all for this frame's cameras or only <paramref name="camera"/>.
        /// </summary>
        public void Render(
            VpIndexedIndirectDrawBatch forwardBatch,
            Material forwardMaterial,
            Material shadowMaterial,
            MaterialPropertyBlock forwardProperties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            Camera camera = null)
        {
            ThrowIfDisposed();
            if (forwardBatch == null)
            {
                throw new ArgumentNullException(nameof(forwardBatch));
            }

            forwardBatch.RenderForward(forwardMaterial, forwardProperties, buffers, layer, 0, forwardBatch.CommandCount, camera);
            RenderShadows(shadowMaterial, buffers, layer, camera);
        }

        /// <summary>The per-tile shadow calls of <see cref="Render"/> alone.</summary>
        internal void RenderShadows(Material shadowMaterial, VpGpuIndexedGeometryBuffers buffers, int layer, Camera camera)
        {
            ThrowIfDisposed();
            for (int t = 0; t < _tiles.Length; t++)
            {
                _tiles[t].RenderShadows(shadowMaterial, _tileProperties[t], buffers, layer, 0, _tiles[t].CommandCount, camera);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseAll(_tiles);
            _tiles = Array.Empty<VpIndexedIndirectDrawBatch>();
            _tileProperties = Array.Empty<MaterialPropertyBlock>();
            _tileCells = Array.Empty<int>();
        }

        private static int Cell(float value, float min, float size, int count)
        {
            if (count == 1 || size <= 0f)
            {
                return 0;
            }

            return Mathf.Clamp((int)Math.Floor((value - min) / size * count), 0, count - 1);
        }

        private static void ReleaseAll(IEnumerable<VpIndexedIndirectDrawBatch> tiles)
        {
            foreach (VpIndexedIndirectDrawBatch tile in tiles)
            {
                tile.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpIndexedShadowTileSet));
            }
        }
    }
}
