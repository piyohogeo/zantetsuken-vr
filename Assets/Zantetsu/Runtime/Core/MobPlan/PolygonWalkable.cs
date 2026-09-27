using System;
using System.Collections.Generic;
using System.IO;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// Walkable map defined by convex obstacle polygons (world XZ, the capsule-slab silhouettes of a Unity scene's
    /// convex colliders, written by Tools/SceneWalkableExport/WalkableMapExporter.cs as polygons.bin) inside a
    /// rectangular window. The polygons are the source of truth (a few hundred KB for a city); clearance is served
    /// from lazily built tiles of exact cell-centre distances (0.1 m cells, 8 m tiles), so the hot chord checks stay
    /// four array reads like <see cref="WalkableRaster"/> while memory is spent only where NPCs walk.
    /// Measured 2026-09-26: ClearanceAt runs 60 k - 1.1 M times per planning cycle, 99% of them from the segment fast
    /// path whose threshold is ~0.4 m, and most answers are below 1 m, so a coarser cache would fall through to the
    /// polygon distance on most calls near walls.
    /// </summary>
    public sealed class PolygonWalkable : WalkableMap
    {
        public const int FormatVersion = 1;

        /// <summary>Convex polygon in world XZ, counter-clockwise, with its bounding box.</summary>
        public sealed class Polygon
        {
            public float[] X;
            public float[] Z;
            public float MinX, MinZ, MaxX, MaxZ;
            public string Name = string.Empty;

            public int Count => X.Length;

            public void ComputeBounds()
            {
                MinX = float.PositiveInfinity; MinZ = float.PositiveInfinity; MaxX = float.NegativeInfinity; MaxZ = float.NegativeInfinity;
                for (var i = 0; i < X.Length; i++)
                {
                    if (X[i] < MinX) MinX = X[i]; if (X[i] > MaxX) MaxX = X[i];
                    if (Z[i] < MinZ) MinZ = Z[i]; if (Z[i] > MaxZ) MaxZ = Z[i];
                }
            }

            /// <summary>Exact Euclidean distance from a point to the polygon (0 inside).</summary>
            public float Distance(float px, float pz)
            {
                var n = X.Length;
                var inside = true;
                var best = float.PositiveInfinity;
                for (var i = 0; i < n; i++)
                {
                    var j = i + 1 == n ? 0 : i + 1;
                    var ex = X[j] - X[i]; var ez = Z[j] - Z[i];
                    var vx = px - X[i]; var vz = pz - Z[i];
                    // Counter-clockwise polygon: inside when the point is left of (or on) every edge.
                    if (ex * vz - ez * vx < 0f) inside = false;
                    var len2 = ex * ex + ez * ez;
                    var t = len2 > 0f ? (vx * ex + vz * ez) / len2 : 0f;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    var dx = vx - t * ex; var dz = vz - t * ez;
                    var d2 = dx * dx + dz * dz;
                    if (d2 < best) best = d2;
                }

                return inside ? 0f : (float)Math.Sqrt(best);
            }
        }

        public readonly List<Polygon> Polygons = new List<Polygon>();
        public float SceneMinX, SceneMinZ, SceneMaxX, SceneMaxZ;   // full extent written by the exporter (the window may be smaller)
        private float sampleX0, sampleZ0, sampleW, sampleH;
        public override float SampleOriginX => sampleX0;
        public override float SampleOriginZ => sampleZ0;
        public override float SampleWidth => sampleW;
        public override float SampleHeight => sampleH;

        /// <summary>Restricts scenario sampling (starts, goals) to a rectangle inside the walkable bounds.</summary>
        public void SetSampleWindow(float x0, float z0, float x1, float z1)
        {
            sampleX0 = Math.Max(OriginX, x0); sampleZ0 = Math.Max(OriginZ, z0);
            sampleW = Math.Max(CellSize, Math.Min(MaxX, x1) - sampleX0); sampleH = Math.Max(CellSize, Math.Min(MaxZ, z1) - sampleZ0);
        }
        public readonly float CellSize;
        public readonly float ClearanceCap;
        public readonly int TileCells;
        public int TilesX { get; private set; }
        public int TilesZ { get; private set; }
        public int BuiltTiles { get; private set; }
        public double TileBuildMilliseconds { get; private set; }

        private float[][] tiles;              // [tile] -> TileCells*TileCells distances at cell centres (capped), null until built
        private readonly object buildLock = new object();
        private readonly float halfDiagonal;
        // Spatial hash of polygons by hash cell (HashMeters), each list holding the polygons whose bounds expanded by ClearanceCap reach the cell.
        private const float HashMeters = 8f;
        private int hashX, hashZ;
        private List<int>[] hash;

        /// <param name="cellSize">Tile cell size in metres (0.1 like the synthetic rasters).</param>
        /// <param name="clearanceCap">Distances above this are reported as this (must exceed npc radius + margin + the largest clip bounding radius).</param>
        /// <param name="tileCells">Tile edge in cells.</param>
        public PolygonWalkable(float originX, float originZ, float worldWidth, float worldHeight, float cellSize = 0.1f, float clearanceCap = 12f, int tileCells = 80)
        {
            OriginX = originX; OriginZ = originZ; WorldWidth = Math.Max(cellSize, worldWidth); WorldHeight = Math.Max(cellSize, worldHeight);
            CellSize = cellSize; ClearanceCap = clearanceCap; TileCells = tileCells;
            halfDiagonal = cellSize * 0.70710678f;
            SceneMinX = originX; SceneMinZ = originZ; SceneMaxX = MaxX; SceneMaxZ = MaxZ;
            sampleX0 = OriginX; sampleZ0 = OriginZ; sampleW = WorldWidth; sampleH = WorldHeight;
            ResetTiles();
        }

        private void ResetTiles()
        {
            var tileMeters = TileCells * CellSize;
            TilesX = Math.Max(1, (int)Math.Ceiling(WorldWidth / tileMeters));
            TilesZ = Math.Max(1, (int)Math.Ceiling(WorldHeight / tileMeters));
            tiles = new float[TilesX * TilesZ][];
            BuiltTiles = 0;
            TileBuildMilliseconds = 0;
        }

        /// <summary>Rebuilds the polygon spatial hash; call after adding polygons.</summary>
        public void Finish()
        {
            hashX = Math.Max(1, (int)Math.Ceiling(WorldWidth / HashMeters));
            hashZ = Math.Max(1, (int)Math.Ceiling(WorldHeight / HashMeters));
            hash = new List<int>[hashX * hashZ];
            for (var p = 0; p < Polygons.Count; p++)
            {
                var poly = Polygons[p];
                poly.ComputeBounds();
                var reach = ClearanceCap + halfDiagonal;
                var hx0 = Math.Max(0, (int)Math.Floor((poly.MinX - reach - OriginX) / HashMeters));
                var hz0 = Math.Max(0, (int)Math.Floor((poly.MinZ - reach - OriginZ) / HashMeters));
                var hx1 = Math.Min(hashX - 1, (int)Math.Floor((poly.MaxX + reach - OriginX) / HashMeters));
                var hz1 = Math.Min(hashZ - 1, (int)Math.Floor((poly.MaxZ + reach - OriginZ) / HashMeters));
                for (var hz = hz0; hz <= hz1; hz++)
                    for (var hx = hx0; hx <= hx1; hx++)
                        (hash[hz * hashX + hx] ??= new List<int>()).Add(p);
            }

            ResetTiles();
        }

        /// <summary>Exact distance (capped) from a point to the nearest polygon or window edge; the point itself must be inside the window.</summary>
        public float ExactDistance(float x, float z)
        {
            var best = Math.Min(Math.Min(x - OriginX, MaxX - x), Math.Min(z - OriginZ, MaxZ - z));
            if (best < 0f) return 0f;
            if (best > ClearanceCap) best = ClearanceCap;
            var hx = (int)((x - OriginX) / HashMeters); var hz = (int)((z - OriginZ) / HashMeters);
            if (hx < 0 || hz < 0 || hx >= hashX || hz >= hashZ) return best;
            var list = hash[hz * hashX + hx];
            if (list == null) return best;
            for (var i = 0; i < list.Count; i++)
            {
                var poly = Polygons[list[i]];
                // Bounding-box lower bound first.
                var bx = x < poly.MinX ? poly.MinX - x : x > poly.MaxX ? x - poly.MaxX : 0f;
                var bz = z < poly.MinZ ? poly.MinZ - z : z > poly.MaxZ ? z - poly.MaxZ : 0f;
                if (bx * bx + bz * bz >= best * best) continue;
                var d = poly.Distance(x, z);
                if (d < best) { best = d; if (best <= 0f) return 0f; }
            }

            return best;
        }

        public override bool IsWalkableAt(float x, float z) => ExactDistance(x, z) > 0f;

        private float[] Tile(int tx, int tz)
        {
            var index = tz * TilesX + tx;
            var tile = tiles[index];
            if (tile != null) return tile;
            lock (buildLock)
            {
                tile = tiles[index];
                if (tile != null) return tile;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                tile = new float[TileCells * TileCells];
                var x0 = OriginX + tx * TileCells * CellSize;
                var z0 = OriginZ + tz * TileCells * CellSize;
                for (var cz = 0; cz < TileCells; cz++)
                {
                    var z = z0 + (cz + 0.5f) * CellSize;
                    for (var cx = 0; cx < TileCells; cx++)
                    {
                        var x = x0 + (cx + 0.5f) * CellSize;
                        tile[cz * TileCells + cx] = x >= MaxX || z >= MaxZ ? 0f : ExactDistance(x, z);
                    }
                }

                TileBuildMilliseconds += watch.Elapsed.TotalMilliseconds;
                BuiltTiles++;
                tiles[index] = tile;   // reference write is atomic; readers see a fully built tile
                return tile;
            }
        }

        /// <summary>Distance stored for the cell containing (gx, gz) in cell units, or 0 outside the window.</summary>
        private float CellDistance(int cx, int cz)
        {
            if (cx < 0 || cz < 0) return 0f;
            var tx = cx / TileCells; var tz = cz / TileCells;
            if (tx >= TilesX || tz >= TilesZ) return 0f;
            return Tile(tx, tz)[(cz - tz * TileCells) * TileCells + (cx - tx * TileCells)];
        }

        /// <summary>Conservative clearance: max over the 4 nearest cell centres of (exact distance at centre - |p - centre|), clamped at 0 (Lipschitz bound).</summary>
        public override float ClearanceAt(float x, float z)
        {
            var gx = (x - OriginX) / CellSize - 0.5f;
            var gz = (z - OriginZ) / CellSize - 0.5f;
            var cx0 = (int)Math.Floor(gx);
            var cz0 = (int)Math.Floor(gz);
            var best = 0f;
            for (var dz = 0; dz <= 1; dz++)
            {
                for (var dx = 0; dx <= 1; dx++)
                {
                    var cx = cx0 + dx;
                    var cz = cz0 + dz;
                    var d = CellDistance(cx, cz);
                    if (d <= 0f) continue;
                    var px = (cx + 0.5f) * CellSize + OriginX - x;
                    var pz = (cz + 0.5f) * CellSize + OriginZ - z;
                    var value = d - (float)Math.Sqrt(px * px + pz * pz);
                    if (value > best) best = value;
                }
            }

            MapQueryStats.RecordClearance(best);
            return best;
        }

        /// <summary>Builds every tile now (for measurements); normal use builds them on demand.</summary>
        public void BuildAllTiles()
        {
            for (var tz = 0; tz < TilesZ; tz++)
                for (var tx = 0; tx < TilesX; tx++)
                    Tile(tx, tz);
        }

        // ---- file format (polygons.bin) ----
        // int32 magic 'PWLK' (0x4B4C5750), int32 version, float sceneMinX, sceneMinZ, sceneMaxX, sceneMaxZ,
        // int32 polygonCount, then per polygon: string name (BinaryWriter), int32 n, n x (float x, float z), counter-clockwise.

        public const int Magic = 0x4B4C5750;

        public static void Save(string absolutePath, IReadOnlyList<Polygon> polygons, float minX, float minZ, float maxX, float maxZ)
        {
            using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(FormatVersion);
            writer.Write(minX); writer.Write(minZ); writer.Write(maxX); writer.Write(maxZ);
            writer.Write(polygons.Count);
            foreach (var p in polygons)
            {
                writer.Write(p.Name ?? string.Empty);
                writer.Write(p.Count);
                for (var i = 0; i < p.Count; i++) { writer.Write(p.X[i]); writer.Write(p.Z[i]); }
            }
        }

        /// <summary>
        /// Loads polygons.bin. The sampling window is the scene extent, or the given rectangle clipped to it when
        /// windowWidth > 0; the walkable bounds extend `marginMeters` beyond the window (clipped to the scene) so the
        /// window edge is not a wall across a street. Polygons entirely outside bounds + clearance cap are dropped.
        /// </summary>
        public static PolygonWalkable Load(string absolutePath, float windowCenterX = 0f, float windowCenterZ = 0f, float windowWidth = 0f, float windowHeight = 0f, float cellSize = 0.1f, float clearanceCap = 12f, float marginMeters = 15f)
        {
            return Read(File.ReadAllBytes(absolutePath), windowCenterX, windowCenterZ, windowWidth, windowHeight, cellSize, clearanceCap, marginMeters);
        }
        public static PolygonWalkable Read(byte[] bytes, float windowCenterX, float windowCenterZ, float windowWidth, float windowHeight, float cellSize = .1f, float clearanceCap = 12f, float marginMeters = 15f)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            if (reader.ReadInt32() != Magic) throw new InvalidDataException("not a polygons.bin");
            var version = reader.ReadInt32();
            if (version != FormatVersion) throw new InvalidDataException($"polygons.bin version {version} (expected {FormatVersion})");
            var minX = reader.ReadSingle(); var minZ = reader.ReadSingle(); var maxX = reader.ReadSingle(); var maxZ = reader.ReadSingle();
            var x0 = minX; var z0 = minZ; var x1 = maxX; var z1 = maxZ;   // walkable bounds
            var sx0 = minX; var sz0 = minZ; var sx1 = maxX; var sz1 = maxZ; // sampling window
            if (windowWidth > 0f && windowHeight > 0f)
            {
                sx0 = Math.Max(minX, windowCenterX - windowWidth * 0.5f); sx1 = Math.Min(maxX, windowCenterX + windowWidth * 0.5f);
                sz0 = Math.Max(minZ, windowCenterZ - windowHeight * 0.5f); sz1 = Math.Min(maxZ, windowCenterZ + windowHeight * 0.5f);
                if (sx1 <= sx0 || sz1 <= sz0) throw new ArgumentException($"window ({windowCenterX}, {windowCenterZ}) {windowWidth}x{windowHeight} m lies outside the scene [{minX}, {maxX}] x [{minZ}, {maxZ}]");
                var margin = Math.Max(0f, marginMeters);
                x0 = Math.Max(minX, sx0 - margin); x1 = Math.Min(maxX, sx1 + margin);
                z0 = Math.Max(minZ, sz0 - margin); z1 = Math.Min(maxZ, sz1 + margin);
            }

            var map = new PolygonWalkable(x0, z0, x1 - x0, z1 - z0, cellSize, clearanceCap) { SceneMinX = minX, SceneMinZ = minZ, SceneMaxX = maxX, SceneMaxZ = maxZ };
            map.SetSampleWindow(sx0, sz0, sx1, sz1);
            var count = reader.ReadInt32();
            var total = 0;
            for (var p = 0; p < count; p++)
            {
                var name = reader.ReadString();
                var n = reader.ReadInt32();
                var poly = new Polygon { X = new float[n], Z = new float[n], Name = name };
                for (var i = 0; i < n; i++) { poly.X[i] = reader.ReadSingle(); poly.Z[i] = reader.ReadSingle(); }
                if (n < 3) continue;
                poly.ComputeBounds();
                total++;
                if (poly.MaxX < x0 - clearanceCap || poly.MinX > x1 + clearanceCap || poly.MaxZ < z0 - clearanceCap || poly.MinZ > z1 + clearanceCap) continue;
                map.Polygons.Add(poly);
            }

            map.Finish();
            var stem = "MobPlan";
            map.Description = $"scene {stem} {map.Polygons.Count}/{total} polygons, window {map.SampleWidth:0}x{map.SampleHeight:0} m (+{marginMeters:0} m margin)";
            return map;
        }
    }
}
