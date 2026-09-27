using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// Passability grid shared by the goal fields of one map and one NPC radius: a cell is passable when the map's
    /// clearance at its centre covers the required clearance. A raster map is used at its own cells (identical to the
    /// original per-cell ClearanceLB test); a polygon map is sampled at a coarse cell (0.5 m by default), which is what
    /// makes fields affordable on a city-sized map (a 60 m window is 14 k cells, the whole tiled-seed2 scene 724 k).
    /// </summary>
    public sealed class FieldGrid
    {
        public readonly WalkableMap Map;
        public readonly float RequiredClearance;
        public readonly float OriginX, OriginZ, CellSize;
        public readonly int Width, Height;
        public readonly bool[] Passable;
        public double BuildMilliseconds { get; }

        public FieldGrid(WalkableMap map, float requiredClearance, float cellSize)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Map = map; RequiredClearance = requiredClearance;
            if (map is WalkableRaster raster)
            {
                OriginX = raster.OriginX; OriginZ = raster.OriginZ; CellSize = raster.CellSize; Width = raster.Width; Height = raster.Height;
                Passable = new bool[Width * Height];
                for (var i = 0; i < Passable.Length; i++) Passable[i] = raster.ClearanceLB[i] >= requiredClearance;
            }
            else
            {
                CellSize = Math.Max(0.1f, cellSize);
                OriginX = map.OriginX; OriginZ = map.OriginZ;
                Width = Math.Max(1, (int)Math.Ceiling(map.WorldWidth / CellSize));
                Height = Math.Max(1, (int)Math.Ceiling(map.WorldHeight / CellSize));
                Passable = new bool[Width * Height];
                for (var cz = 0; cz < Height; cz++)
                    for (var cx = 0; cx < Width; cx++)
                        Passable[cz * Width + cx] = map.ClearanceAt(OriginX + (cx + 0.5f) * CellSize, OriginZ + (cz + 0.5f) * CellSize) >= requiredClearance;
            }

            BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        public int CellX(float x) => (int)Math.Floor((x - OriginX) / CellSize);
        public int CellZ(float z) => (int)Math.Floor((z - OriginZ) / CellSize);
        public bool InBounds(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Width && cz < Height;
    }

    /// <summary>
    /// Cost-to-go field for one goal: Dijkstra over a <see cref="FieldGrid"/> (8-neighbour, metric step costs, no corner
    /// cutting), bounded by a maximum path length. Replaces the straight-line distance in progress cost, cheap rank and
    /// heading so goals around corners (and behind city blocks) are approached along a feasible path. Immutable after Build.
    /// </summary>
    public sealed class GoalField
    {
        public readonly float GoalX;
        public readonly float GoalZ;
        public readonly float RequiredClearance;
        public readonly float MaxPathMeters;
        private readonly FieldGrid grid;
        private readonly float[] distance;     // metres of path to the goal; +inf when unreachable (or beyond MaxPathMeters)
        public double BuildMilliseconds { get; private set; }
        public int ReachableCells { get; private set; }

        public GoalField(FieldGrid grid, float goalX, float goalZ, float maxPathMeters = float.PositiveInfinity)
        {
            this.grid = grid;
            GoalX = goalX; GoalZ = goalZ; RequiredClearance = grid.RequiredClearance; MaxPathMeters = maxPathMeters;
            distance = new float[grid.Width * grid.Height];
            Build();
        }

        /// <summary>Convenience for raster maps (tests and the original call shape).</summary>
        public GoalField(WalkableRaster map, float goalX, float goalZ, float requiredClearance) : this(new FieldGrid(map, requiredClearance, map.CellSize), goalX, goalZ) { }

        private void Build()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var w = grid.Width;
            var h = grid.Height;
            var passable = grid.Passable;
            for (var i = 0; i < distance.Length; i++) distance[i] = float.PositiveInfinity;
            // The goal cell and its ring are seeded whenever they are on walkable ground, so a goal placed slightly inside
            // the margin still has a field.
            var gx = Math.Max(0, Math.Min(w - 1, grid.CellX(GoalX)));
            var gz = Math.Max(0, Math.Min(h - 1, grid.CellZ(GoalZ)));
            var heap = new BinaryHeap();
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var cx = gx + dx; var cz = gz + dz;
                    if (!grid.InBounds(cx, cz)) continue;
                    var px = grid.OriginX + (cx + 0.5f) * grid.CellSize;
                    var pz = grid.OriginZ + (cz + 0.5f) * grid.CellSize;
                    if (!grid.Map.IsWalkableAt(px, pz)) continue;
                    var index = cz * w + cx;
                    distance[index] = (float)Math.Sqrt((px - GoalX) * (px - GoalX) + (pz - GoalZ) * (pz - GoalZ));
                    heap.Push(distance[index], index);
                }
            }

            var straight = grid.CellSize;
            var diagonal = grid.CellSize * 1.41421356f;
            while (heap.Count > 0)
            {
                heap.Pop(out var d, out var index);
                if (d > distance[index]) continue;
                if (d > MaxPathMeters) break;
                ReachableCells++;
                var cx = index % w;
                var cz = index / w;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var nx = cx + dx; var nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h) continue;
                        var next = nz * w + nx;
                        if (!passable[next]) continue;
                        if (dx != 0 && dz != 0)
                        {
                            // No corner cutting between two blocked orthogonal neighbours.
                            if (!passable[cz * w + nx] || !passable[nz * w + cx]) continue;
                        }

                        var candidate = d + (dx != 0 && dz != 0 ? diagonal : straight);
                        if (candidate < distance[next])
                        {
                            distance[next] = candidate;
                            heap.Push(candidate, next);
                        }
                    }
                }
            }

            BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Path distance from a world point along the field, +inf when the point (and its 5x5 cell neighbourhood) has no path.</summary>
        public float PathDistanceAt(float x, float z)
        {
            var cx = grid.CellX(x);
            var cz = grid.CellZ(z);
            if (!grid.InBounds(cx, cz)) return float.PositiveInfinity;
            var d = distance[cz * grid.Width + cx];
            if (!float.IsPositiveInfinity(d)) return d;
            var best = float.PositiveInfinity;
            for (var dz = -2; dz <= 2; dz++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var nx = cx + dx; var nz = cz + dz;
                    if (!grid.InBounds(nx, nz)) continue;
                    var nd = distance[nz * grid.Width + nx];
                    if (float.IsPositiveInfinity(nd)) continue;
                    var px = grid.OriginX + (nx + 0.5f) * grid.CellSize - x;
                    var pz = grid.OriginZ + (nz + 0.5f) * grid.CellSize - z;
                    nd += (float)Math.Sqrt(px * px + pz * pz);
                    if (nd < best) best = nd;
                }
            }

            return best;
        }

        /// <summary>Path distance to the goal from a world point; falls back to the straight line when there is no path.</summary>
        public float DistanceAt(float x, float z)
        {
            var d = PathDistanceAt(x, z);
            if (!float.IsPositiveInfinity(d)) return d;
            var ex = x - GoalX; var ez = z - GoalZ;
            return (float)Math.Sqrt(ex * ex + ez * ez);
        }

        /// <summary>A point about `lookAhead` metres down the field from (x, z): the neighbour direction with the steepest descent, else the goal itself.</summary>
        public void Waypoint(float x, float z, float lookAhead, out float wx, out float wz)
        {
            var bestX = 0f; var bestZ = 0f;
            var bestSlope = 0f;
            var here = DistanceAt(x, z);
            var step = Math.Max(grid.CellSize * 3f, 0.3f);
            for (var k = 0; k < 8; k++)
            {
                var a = k * Math.PI / 4.0;
                var dx = (float)Math.Sin(a); var dz = (float)Math.Cos(a);
                var nx = x + dx * step; var nz = z + dz * step;
                var ncx = grid.CellX(nx); var ncz = grid.CellZ(nz);
                if (!grid.InBounds(ncx, ncz)) continue;
                var nd = distance[ncz * grid.Width + ncx];
                if (float.IsPositiveInfinity(nd)) continue;
                var slope = here - nd;
                if (slope > bestSlope) { bestSlope = slope; bestX = dx; bestZ = dz; }
            }

            if (bestSlope <= 0f || here <= lookAhead) { wx = GoalX; wz = GoalZ; return; }
            wx = x + bestX * lookAhead;
            wz = z + bestZ * lookAhead;
        }

        private sealed class BinaryHeap
        {
            private float[] keys = new float[1024];
            private int[] values = new int[1024];
            public int Count { get; private set; }

            public void Push(float key, int value)
            {
                if (Count == keys.Length) { Array.Resize(ref keys, Count * 2); Array.Resize(ref values, Count * 2); }
                var i = Count++;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (keys[parent] <= key) break;
                    keys[i] = keys[parent]; values[i] = values[parent];
                    i = parent;
                }

                keys[i] = key; values[i] = value;
            }

            public void Pop(out float key, out int value)
            {
                key = keys[0]; value = values[0];
                Count--;
                if (Count == 0) return;
                var lastKey = keys[Count]; var lastValue = values[Count];
                var i = 0;
                while (true)
                {
                    var left = 2 * i + 1;
                    if (left >= Count) break;
                    var right = left + 1;
                    var child = right < Count && keys[right] < keys[left] ? right : left;
                    if (keys[child] >= lastKey) break;
                    keys[i] = keys[child]; values[i] = values[child];
                    i = child;
                }

                keys[i] = lastKey; values[i] = lastValue;
            }
        }
    }
}
