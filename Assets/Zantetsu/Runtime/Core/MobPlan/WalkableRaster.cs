using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// Diagnostic counters for static-map queries (non-atomic; read from the planner thread or after a run).
    /// Clearance bins: [0] < 1 m, [1] 1-2 m, [2] 2-4 m, [3] 4-8 m, [4] >= 8 m (value returned by ClearanceAt).
    /// </summary>
    public static class MapQueryStats
    {
        [ThreadStatic] public static long ClearanceCalls;
        [ThreadStatic] private static long[] clearanceBins;
        public static long[] ClearanceBins => clearanceBins ?? (clearanceBins = new long[5]);
        [ThreadStatic] public static long SegmentCalls;
        [ThreadStatic] public static long SegmentFastAccepts;
        [ThreadStatic] public static long ClipChecks;
        [ThreadStatic] public static long ClipCircleAccepts;

        public static void Reset()
        {
            ClearanceCalls = 0; SegmentCalls = 0; SegmentFastAccepts = 0; ClipChecks = 0; ClipCircleAccepts = 0;
            for (var i = 0; i < ClearanceBins.Length; i++) ClearanceBins[i] = 0;
        }

        public static void RecordClearance(float value)
        {
            ClearanceCalls++;
            ClearanceBins[value < 1f ? 0 : value < 2f ? 1 : value < 4f ? 2 : value < 8f ? 3 : 4]++;
        }

        public static string Describe()
        {
            var b = ClearanceBins;
            return $"clearance {ClearanceCalls:N0} (<1 m {b[0]:N0}, 1-2 {b[1]:N0}, 2-4 {b[2]:N0}, 4-8 {b[3]:N0}, >=8 {b[4]:N0}) segment {SegmentCalls:N0} (fast {SegmentFastAccepts:N0}) clip {ClipChecks:N0} (circle {ClipCircleAccepts:N0})";
        }
    }

    /// <summary>
    /// Static walkability the planner queries: a conservative clearance (metres to the nearest obstacle, 0 when
    /// blocked, capped by the implementation) at any world point, plus the derived segment / clip checks. Outside the
    /// rectangle [OriginX, MaxX] x [OriginZ, MaxZ] is blocked. Implementations: <see cref="WalkableRaster"/> (grid + EDT,
    /// the synthetic presets) and <see cref="PolygonWalkable"/> (convex obstacle polygons from a Unity scene, lazily
    /// rasterised tiles).
    /// </summary>
    public abstract class WalkableMap
    {
        public float OriginX;      // world X of the left edge
        public float OriginZ;      // world Z of the bottom edge
        public float WorldWidth;
        public float WorldHeight;
        public string Description = string.Empty;

        [ThreadStatic] private static Stack<(float ax, float az, float bx, float bz)> scratchStack;

        public float MaxX => OriginX + WorldWidth;
        public float MaxZ => OriginZ + WorldHeight;
        public float CenterX => SampleOriginX + SampleWidth * 0.5f;
        public float CenterZ => SampleOriginZ + SampleHeight * 0.5f;

        // Scenario sampling window (starts, goals, crossing circle): the whole map unless the implementation narrows it
        // (a scene window keeps a walkable margin outside it so the window edge is not a wall through a street).
        public virtual float SampleOriginX => OriginX;
        public virtual float SampleOriginZ => OriginZ;
        public virtual float SampleWidth => WorldWidth;
        public virtual float SampleHeight => WorldHeight;

        /// <summary>Conservative clearance at a world point (lower bound of the distance to the nearest obstacle, 0 when blocked).</summary>
        public abstract float ClearanceAt(float x, float z);

        /// <summary>Whether the point itself is on walkable ground (no clearance requirement).</summary>
        public abstract bool IsWalkableAt(float x, float z);

        public bool PointSafe(float x, float z, float required) => ClearanceAt(x, z) >= required;

        /// <summary>Line of sight: true when an obstacle lies on the segment (sampled every 0.25 m; thin props may be missed).</summary>
        public virtual bool SegmentBlocked(float x0, float z0, float x1, float z1)
        {
            var dx = x1 - x0; var dz = z1 - z0;
            var length = (float)Math.Sqrt(dx * dx + dz * dz);
            var steps = Math.Max(1, (int)Math.Ceiling(length / 0.25f));
            for (var i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                if (!IsWalkableAt(x0 + dx * t, z0 + dz * t)) return true;
            }

            return false;
        }

        /// <summary>
        /// Adaptive segment check (Section 8.4): a segment of length L with midpoint clearance >= required + L/2 is safe;
        /// otherwise it is bisected down to minLength and then treated as unsafe.
        /// </summary>
        public bool SegmentSafe(float ax, float az, float bx, float bz, float required, float minLength)
        {
            // Fast path without allocation: the whole segment is proven by its midpoint.
            MapQueryStats.SegmentCalls++;
            {
                var dx = bx - ax;
                var dz = bz - az;
                var length = (float)Math.Sqrt(dx * dx + dz * dz);
                if (ClearanceAt((ax + bx) * 0.5f, (az + bz) * 0.5f) >= required + length * 0.5f) { MapQueryStats.SegmentFastAccepts++; return true; }
                if (length <= minLength) return false;
            }

            var stack = scratchStack ??= new Stack<(float ax, float az, float bx, float bz)>();
            stack.Clear();
            stack.Push((ax, az, bx, bz));
            var guard = 0;
            while (stack.Count > 0)
            {
                if (++guard > 4096) return false;
                var (x0, z0, x1, z1) = stack.Pop();
                var dx = x1 - x0;
                var dz = z1 - z0;
                var length = (float)Math.Sqrt(dx * dx + dz * dz);
                var mx = (x0 + x1) * 0.5f;
                var mz = (z0 + z1) * 0.5f;
                var clearance = ClearanceAt(mx, mz);
                if (clearance >= required + length * 0.5f) continue;
                if (length <= minLength) return false;
                stack.Push((x0, z0, mx, mz));
                stack.Push((mx, mz, x1, z1));
            }

            return true;
        }

        /// <summary>
        /// Static check of one clip placed at `start` (the single source of truth for planner and validator):
        /// accepted at once when the start clearance covers the clip's bounding radius, otherwise every dataset
        /// sample chord is checked with SegmentSafe. Returns the index of the first unsafe sample (-1 when safe).
        /// </summary>
        public int ClipUnsafeSample(ClipDataset dataset, int clip, Pose2 start, float required, float minSegment, bool boundingCircle, out bool circleAccepted)
        {
            var c = dataset.Clips[clip];
            MapQueryStats.ClipChecks++;
            circleAccepted = boundingCircle && ClearanceAt(start.X, start.Z) >= required + c.MaxRadius;
            if (circleAccepted) { MapQueryStats.ClipCircleAccepts++; return -1; }
            var cosYaw = (float)Math.Cos(start.YawDeg * Pose2.Deg2Rad);
            var sinYaw = (float)Math.Sin(start.YawDeg * Pose2.Deg2Rad);
            var previousX = 0f; var previousZ = 0f;
            for (var i = 0; i < c.SampleCount; i++)
            {
                var index = c.SampleOffset + i;
                var lx = dataset.X[index]; var lz = dataset.Z[index];
                var wx = start.X + lx * cosYaw + lz * sinYaw;
                var wz = start.Z - lx * sinYaw + lz * cosYaw;
                if (i == 0 && ClearanceAt(wx, wz) < required) return 0;
                if (i > 0 && !SegmentSafe(previousX, previousZ, wx, wz, required, minSegment)) return i;
                previousX = wx; previousZ = wz;
            }

            return -1;
        }

        /// <summary>Deterministic random walkable point with at least the given clearance (null when none found).</summary>
        public bool TryRandomPoint(Random random, float requiredClearance, out float x, out float z, int attempts = 500)
        {
            for (var i = 0; i < attempts; i++)
            {
                x = SampleOriginX + (float)random.NextDouble() * SampleWidth;
                z = SampleOriginZ + (float)random.NextDouble() * SampleHeight;
                if (ClearanceAt(x, z) >= requiredClearance) return true;
            }

            x = 0f; z = 0f;
            return false;
        }
    }

    /// <summary>
    /// Binary walkable grid with a conservative clearance lower bound (LOCOMOTION_SEARCH_PLAN.md Section 8).
    /// walkable[cell] means the whole cell square is walkable; outside the map is blocked. The clearance lower
    /// bound at a cell centre is the Euclidean distance to the nearest blocked cell centre minus the half diagonal.
    /// </summary>
    public sealed class WalkableRaster : WalkableMap
    {
        public float CellSize;
        public int Width;
        public int Height;
        public bool[] Walkable;
        public float[] ClearanceLB;   // per cell centre, metres, 0 for blocked cells

        public WalkableRaster(int width, int height, float cellSize, float originX, float originZ)
        {
            Width = width; Height = height; CellSize = cellSize; OriginX = originX; OriginZ = originZ;
            WorldWidth = width * cellSize; WorldHeight = height * cellSize;
            Walkable = new bool[width * height];
            ClearanceLB = new float[width * height];
            for (var i = 0; i < Walkable.Length; i++) Walkable[i] = true;
        }

        /// <summary>
        /// Reads a raster exported by Tools/SceneWalkableExport/WalkableMapExporter.cs (walkable.bin):
        /// int32 width, int32 height, float32 cell, float32 originX, float32 originZ, then width*height bytes (1 = walkable), z rows.
        /// Clearance is not computed here; call ComputeClearance() afterwards.
        /// </summary>
        public static WalkableRaster LoadBinary(string absolutePath)
        {
            using var stream = new System.IO.FileStream(absolutePath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            using var reader = new System.IO.BinaryReader(stream);
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var cell = reader.ReadSingle();
            var originX = reader.ReadSingle();
            var originZ = reader.ReadSingle();
            if (width <= 0 || height <= 0 || cell <= 0f) throw new System.IO.InvalidDataException($"bad walkable header {width}x{height} cell {cell}");
            var map = new WalkableRaster(width, height, cell, originX, originZ);
            var bytes = reader.ReadBytes(width * height);
            if (bytes.Length != width * height) throw new System.IO.InvalidDataException($"walkable.bin truncated: {bytes.Length} of {width * height} cells");
            for (var i = 0; i < bytes.Length; i++) map.Walkable[i] = bytes[i] != 0;
            map.Description = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(absolutePath)) + $" {width}x{height}/{cell:0.##}";
            return map;
        }

        public int CellX(float x) => (int)Math.Floor((x - OriginX) / CellSize);
        public int CellZ(float z) => (int)Math.Floor((z - OriginZ) / CellSize);
        public bool InBounds(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Width && cz < Height;
        public bool IsWalkableCell(int cx, int cz) => InBounds(cx, cz) && Walkable[cz * Width + cx];
        public override bool IsWalkableAt(float x, float z) => IsWalkableCell(CellX(x), CellZ(z));

        public void Block(int cx, int cz)
        {
            if (InBounds(cx, cz)) Walkable[cz * Width + cx] = false;
        }

        /// <summary>Blocks every cell that intersects the world rectangle [x0, x1] × [z0, z1].</summary>
        public void BlockRect(float x0, float z0, float x1, float z1)
        {
            var cx0 = Math.Max(0, CellX(Math.Min(x0, x1)));
            var cz0 = Math.Max(0, CellZ(Math.Min(z0, z1)));
            var cx1 = Math.Min(Width - 1, (int)Math.Ceiling((Math.Max(x0, x1) - OriginX) / CellSize) - 1);
            var cz1 = Math.Min(Height - 1, (int)Math.Ceiling((Math.Max(z0, z1) - OriginZ) / CellSize) - 1);
            for (var cz = cz0; cz <= cz1; cz++)
                for (var cx = cx0; cx <= cx1; cx++)
                    Walkable[cz * Width + cx] = false;
        }

        /// <summary>Blocks every cell whose centre is within radius of (x, z), plus cells the circle edge crosses (conservative).</summary>
        public void BlockCircle(float x, float z, float radius)
        {
            var r = radius + CellSize * 0.71f;
            var cx0 = Math.Max(0, CellX(x - r));
            var cz0 = Math.Max(0, CellZ(z - r));
            var cx1 = Math.Min(Width - 1, CellX(x + r));
            var cz1 = Math.Min(Height - 1, CellZ(z + r));
            for (var cz = cz0; cz <= cz1; cz++)
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    var px = OriginX + (cx + 0.5f) * CellSize - x;
                    var pz = OriginZ + (cz + 0.5f) * CellSize - z;
                    if (px * px + pz * pz <= r * r) Walkable[cz * Width + cx] = false;
                }
        }

        /// <summary>Blocks the outermost ring of cells (thickness in cells) so the map is enclosed.</summary>
        public void BlockBorder(int thicknessCells)
        {
            for (var cz = 0; cz < Height; cz++)
                for (var cx = 0; cx < Width; cx++)
                    if (cx < thicknessCells || cz < thicknessCells || cx >= Width - thicknessCells || cz >= Height - thicknessCells)
                        Walkable[cz * Width + cx] = false;
        }

        /// <summary>Exact squared Euclidean distance transform (Felzenszwalb–Huttenlocher) to the nearest blocked cell centre.</summary>
        public void ComputeClearance()
        {
            var inf = (float)(Width * Width + Height * Height + 10);
            var f = new float[Width * Height];
            for (var i = 0; i < f.Length; i++) f[i] = Walkable[i] ? inf : 0f;
            // Cells outside the map are blocked: emulate by treating the border as distance to the edge.
            var column = new float[Math.Max(Width, Height)];
            var output = new float[Math.Max(Width, Height)];
            // Pass 1: columns (along z).
            for (var cx = 0; cx < Width; cx++)
            {
                for (var cz = 0; cz < Height; cz++) column[cz] = f[cz * Width + cx];
                Transform1D(column, Height, output);
                for (var cz = 0; cz < Height; cz++) f[cz * Width + cx] = output[cz];
            }

            // Pass 2: rows (along x).
            for (var cz = 0; cz < Height; cz++)
            {
                for (var cx = 0; cx < Width; cx++) column[cx] = f[cz * Width + cx];
                Transform1D(column, Width, output);
                for (var cx = 0; cx < Width; cx++) f[cz * Width + cx] = output[cx];
            }

            var halfDiagonal = CellSize * 0.70710678f;
            for (var cz = 0; cz < Height; cz++)
            {
                for (var cx = 0; cx < Width; cx++)
                {
                    var index = cz * Width + cx;
                    if (!Walkable[index]) { ClearanceLB[index] = 0f; continue; }
                    var distance = (float)Math.Sqrt(f[index]) * CellSize;
                    // Outside the map is blocked too: distance from the cell centre to the nearest map edge.
                    var edge = Math.Min(Math.Min(cx + 0.5f, Width - cx - 0.5f), Math.Min(cz + 0.5f, Height - cz - 0.5f)) * CellSize;
                    distance = Math.Min(distance, edge + halfDiagonal);
                    ClearanceLB[index] = Math.Max(0f, distance - halfDiagonal);
                }
            }
        }

        private static void Transform1D(float[] f, int n, float[] d)
        {
            var v = new int[n];
            var z = new float[n + 1];
            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (var q = 1; q < n; q++)
            {
                float s;
                while (true)
                {
                    s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
                    if (s <= z[k]) { k--; if (k < 0) { k = 0; break; } }
                    else break;
                }

                if (k == 0 && s <= z[0]) s = float.NegativeInfinity;
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                var dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        /// <summary>Conservative clearance at a world point: max over the 4 nearest cell centres of (lb(centre) - |p - centre|), clamped at 0.</summary>
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
                    if (!InBounds(cx, cz)) continue;
                    var lb = ClearanceLB[cz * Width + cx];
                    if (lb <= 0f) continue;
                    var px = (cx + 0.5f) * CellSize + OriginX - x;
                    var pz = (cz + 0.5f) * CellSize + OriginZ - z;
                    var value = lb - (float)Math.Sqrt(px * px + pz * pz);
                    if (value > best) best = value;
                }
            }

            MapQueryStats.RecordClearance(best);
            return best;
        }
    }
}
