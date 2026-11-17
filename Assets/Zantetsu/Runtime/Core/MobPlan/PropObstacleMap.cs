using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// PROP-OBSTACLE-1: the settings of the local map of prop fragments (a felled tree's pieces) the NPC planner takes as a
    /// best-effort dynamic obstacle. The fragments' convexes are sliced by ONE horizontal plane at a representative NPC view
    /// height above the floor; the sections are rasterised into a square window of cells around the player. An approximation:
    /// nothing here is conservative. The values are first comparison values (0.2 m, 250 cells, 200 ms), not adopted ones.
    /// </summary>
    [Serializable]
    public sealed class PropObstacleSettings
    {
        /// <summary>The cell's side (m).</summary>
        public float cellMeters = 0.2f;
        /// <summary>Cells a side: the window is cells x cellMeters square (250 x 0.2 = 50 m).</summary>
        public int cells = 250;
        /// <summary>
        /// The plane's height above the NPC floor reference (planner y = 0): a representative NPC view height. 1.7 m from the
        /// crowd's 49 character models (2026-11-15, bind pose: head bone 1.577 m in all of them, top 1.83-1.95 m, median 1.85 m;
        /// the eyes taken at the middle of the head).
        /// </summary>
        public float planeHeightMeters = 1.7f;
        /// <summary>The aim of the interval between two builds (s); a request made while one is being built replaces the one waiting.</summary>
        public float updateSeconds = 0.2f;
        /// <summary>Distances are kept up to this (m); farther reads as this.</summary>
        public float maxDistanceMeters = 3f;
        /// <summary>The window is centred on the player again when the player is nearer than this to its edge (m). 20 since 2026-11-16 (10 before): collect-2\run-1.</summary>
        public float recentreMarginMeters = 20f;
        /// <summary>
        /// Under a capture only (0: the window alone, as every record before 2026-11-16): the side (m) of the axis-aligned square
        /// about the player whose prop shapes are kept, outside the window too, so that a replay can place another window. 80 m
        /// holds every 50 m window whose centre is within 15 m of the player along each axis (the widest of the rules compared).
        /// </summary>
        public float captureSquareMeters = 0f;

        public PropObstacleSettings Clone() => (PropObstacleSettings)MemberwiseClone();

        public float WindowMeters => cells * cellMeters;

        public string Describe() => FormattableString.Invariant($"cell {cellMeters:F3} m x {cells} ({WindowMeters:F1} m), plane {planeHeightMeters:F3} m above the floor, update {updateSeconds * 1000f:F0} ms, distances to {maxDistanceMeters:F1} m, recentre within {recentreMarginMeters:F1} m of the edge");
    }

    /// <summary>
    /// The numbers of one physics shape of a prop fragment, copied once: per convex its vertices (the shape's local frame) and its
    /// edges (pairs of vertex indices local to the convex), with the convex's local box. Immutable after it is made; a shape that
    /// changes is another PropShape with another id.
    /// </summary>
    public sealed class PropShape
    {
        public readonly int Id;
        /// <summary>x, y, z of every vertex, convex after convex.</summary>
        public readonly float[] Vertices;
        /// <summary>ConvexCount + 1 entries: the first vertex of each convex (in vertices, not floats).</summary>
        public readonly int[] VertexStart;
        /// <summary>v0, v1 of every edge, convex after convex; the indices are local to the convex.</summary>
        public readonly int[] Edges;
        /// <summary>ConvexCount + 1 entries: the first edge of each convex (in edges, not ints).</summary>
        public readonly int[] EdgeStart;
        /// <summary>Per convex: lo x, y, z, hi x, y, z in the shape's local frame.</summary>
        public readonly float[] Bounds;

        public PropShape(int id, float[] vertices, int[] vertexStart, int[] edges, int[] edgeStart, float[] bounds)
        {
            if (vertexStart == null || edgeStart == null || vertexStart.Length != edgeStart.Length || vertexStart.Length < 1) throw new ArgumentException("the convex tables");
            var count = vertexStart.Length - 1;
            if (bounds == null || bounds.Length != count * 6) throw new ArgumentException("the bounds");
            if (vertices == null || vertices.Length != vertexStart[count] * 3) throw new ArgumentException("the vertices");
            if (edges == null || edges.Length != edgeStart[count] * 2) throw new ArgumentException("the edges");
            Id = id; Vertices = vertices; VertexStart = vertexStart; Edges = edges; EdgeStart = edgeStart; Bounds = bounds;
        }

        public int ConvexCount => VertexStart.Length - 1;
        public int Bytes => Vertices.Length * 4 + Edges.Length * 4 + Bounds.Length * 4 + VertexStart.Length * 8;
    }

    /// <summary>
    /// The numeric input of ONE build: the window, the plane, and for every prop fragment taken its shape and a copy of its
    /// transform (shape local to planner coordinates: row-major 3x4, x' = m0 x + m1 y + m2 z + m3 and so on). No engine object.
    /// </summary>
    public sealed class PropObstacleInput
    {
        public double Time;
        public float PlayerX, PlayerZ;
        /// <summary>
        /// The player the window was decided with (at the start of the gathering; PlayerX/PlayerZ are taken at its end). The
        /// capture square is about this place too. NaN: not recorded (records before 2026-11-16).
        /// </summary>
        public float WindowPlayerX = float.NaN, WindowPlayerZ = float.NaN;
        /// <summary>The window's low corner (planner coordinates).</summary>
        public float OriginX, OriginZ;
        public float Cell;
        public int Cells;
        public float PlaneY;
        public float MaxDistance;
        public int Count;
        public PropShape[] Shapes = new PropShape[16];
        public float[] Matrices = new float[16 * 12];
        public int[] Fragments = new int[16];
        public int[] Roots = new int[16];
        public int[] Sides = new int[16];

        public void Clear()
        {
            for (var i = 0; i < Count; i++) Shapes[i] = null;
            Count = 0;
        }

        /// <summary>Adds one fragment; the matrix's 12 numbers are written by the caller at Matrices[index * 12 ..].</summary>
        public int Add(PropShape shape, int fragment, int root, int side)
        {
            if (Count == Shapes.Length)
            {
                var n = Count * 2;
                Array.Resize(ref Shapes, n); Array.Resize(ref Matrices, n * 12); Array.Resize(ref Fragments, n); Array.Resize(ref Roots, n); Array.Resize(ref Sides, n);
            }

            Shapes[Count] = shape; Fragments[Count] = fragment; Roots[Count] = root; Sides[Count] = side;
            return Count++;
        }

        public void SetWindow(PropObstacleSettings settings, float originX, float originZ)
        {
            OriginX = originX; OriginZ = originZ; Cell = settings.cellMeters; Cells = settings.cells; PlaneY = settings.planeHeightMeters; MaxDistance = settings.maxDistanceMeters;
        }
    }

    /// <summary>The rule the window follows the player by: the product (PropObstacleSource) and the replay (PropReplay) both call this.</summary>
    public static class PropObstacleWindow
    {
        /// <summary>
        /// Centres the window on the player again when there is none yet or the player is nearer than the margin (at most 0.4 of
        /// the window) to its edge; its corner on a whole cell, so that cells keep their places as it moves. True: placed anew.
        /// </summary>
        public static bool Follow(float windowMeters, float cellMeters, float marginMeters, ref bool has, ref float originX, ref float originZ, float playerX, float playerZ)
        {
            float margin = Math.Min(marginMeters, windowMeters * 0.4f);
            if (has && !(playerX < originX + margin || playerZ < originZ + margin || playerX > originX + windowMeters - margin || playerZ > originZ + windowMeters - margin)) return false;
            originX = (float)Math.Floor((playerX - windowMeters * 0.5f) / cellMeters) * cellMeters;
            originZ = (float)Math.Floor((playerZ - windowMeters * 0.5f) / cellMeters) * cellMeters;
            has = true;
            return true;
        }
    }

    /// <summary>What one build did (counts and its own clocks).</summary>
    public struct PropObstacleStats
    {
        public int Items;
        public int Convexes;
        /// <summary>Convexes left out by their box: outside the window.</summary>
        public int OutOfWindow;
        /// <summary>Convexes left out by their box: wholly below the plane (a low piece has no section).</summary>
        public int BelowPlane;
        /// <summary>Convexes left out by their box: wholly above the plane.</summary>
        public int AbovePlane;
        /// <summary>Fragments none of whose convexes reaches the plane, all of them below it.</summary>
        public int ItemsBelowPlane;
        public int Sections;
        /// <summary>Convexes whose box crosses the plane and whose section has fewer than three points.</summary>
        public int ThinSections;
        public int OccupiedCells;
        /// <summary>Cells a section covers that the static map blocks already: not written.</summary>
        public int StaticSkippedCells;
        public double SectionMilliseconds;
        public double RasterMilliseconds;
        public double DistanceMilliseconds;

        public double TotalMilliseconds => SectionMilliseconds + RasterMilliseconds + DistanceMilliseconds;

        public string Describe() => FormattableString.Invariant($"items {Items}, convexes {Convexes} (out of the window {OutOfWindow}, below the plane {BelowPlane}, above {AbovePlane}; fragments wholly below {ItemsBelowPlane}), sections {Sections} (thin {ThinSections}), cells occupied {OccupiedCells} (in the static map already {StaticSkippedCells}); ms: sections {SectionMilliseconds:F3}, raster {RasterMilliseconds:F3}, distance {DistanceMilliseconds:F3}");
    }

    /// <summary>
    /// One finished, read-only version of the map: per cell the distance (m) from the cell's centre to the occupied cells,
    /// 0 inside. A reader keeps a version for as long as it likes between Retain and Release; a version with a reader, or the
    /// current one, is never written again (PropObstacleMapPool). Outside the window the map says nothing: no obstacle of its own.
    /// </summary>
    public sealed class PropObstacleMap
    {
        public int Version;
        /// <summary>The time (the planner's clock) the input's poses were taken at.</summary>
        public double InputTime;
        public float OriginX, OriginZ, Cell;
        public int Cells;
        public float PlaneY;
        public float MaxDistance;
        public int OccupiedCells;
        /// <summary>The occupied cells' box (m); empty (min > max) with none.</summary>
        public float MinX, MaxX, MinZ, MaxZ;
        public PropObstacleStats Stats;
        internal float[] Distance = Array.Empty<float>();
        private int users;

        public int Users => Volatile.Read(ref users);
        public void Retain() => Interlocked.Increment(ref users);

        public void Release()
        {
            if (Interlocked.Decrement(ref users) < 0) throw new InvalidOperationException("a prop obstacle map released more often than retained");
        }

        public bool Covers(float x, float z)
        {
            var size = Cells * Cell;
            return x >= OriginX && z >= OriginZ && x < OriginX + size && z < OriginZ + size;
        }

        /// <summary>Whether a box (inflated) comes near the occupied cells at all.</summary>
        public bool BoxNear(float minX, float maxX, float minZ, float maxZ, float inflate)
            => OccupiedCells > 0 && maxX + inflate >= MinX && minX - inflate <= MaxX && maxZ + inflate >= MinZ && minZ - inflate <= MaxZ;

        /// <summary>The distance (m) to the occupied cells at a point, between the four nearest cell centres; infinite outside the window or with nothing occupied.</summary>
        public float DistanceAt(float x, float z)
        {
            if (OccupiedCells == 0) return float.PositiveInfinity;
            var fx = (x - OriginX) / Cell - 0.5f;
            var fz = (z - OriginZ) / Cell - 0.5f;
            if (fx < -0.5f || fz < -0.5f || fx > Cells - 0.5f || fz > Cells - 0.5f) return float.PositiveInfinity;
            var x0 = (int)Math.Floor(fx); var z0 = (int)Math.Floor(fz);
            var tx = fx - x0; var tz = fz - z0;
            var x1 = x0 + 1; var z1 = z0 + 1;
            if (x0 < 0) x0 = 0; if (z0 < 0) z0 = 0; if (x1 >= Cells) x1 = Cells - 1; if (z1 >= Cells) z1 = Cells - 1;
            var d = Distance;
            var a = d[z0 * Cells + x0]; var b = d[z0 * Cells + x1]; var c = d[z1 * Cells + x0]; var e = d[z1 * Cells + x1];
            return (a + (b - a) * tx) * (1f - tz) + (c + (e - c) * tx) * tz;
        }

        /// <summary>Whether a circle at a point overlaps the occupied cells (the radius holds everything the caller wants kept: once).</summary>
        public bool Overlaps(float x, float z, float radius) => OccupiedCells > 0 && DistanceAt(x, z) < radius;

        /// <summary>Whether the cell holding a point is occupied; false outside.</summary>
        public bool OccupiedAt(float x, float z)
        {
            if (OccupiedCells == 0 || !Covers(x, z)) return false;
            var cx = (int)((x - OriginX) / Cell); var cz = (int)((z - OriginZ) / Cell);
            return Distance[cz * Cells + cx] <= 0f;
        }

        /// <summary>The cell's distance by index (viewer, tests).</summary>
        public float CellDistance(int cx, int cz) => Distance[cz * Cells + cx];
    }

    /// <summary>
    /// The versions' buffers: a build takes one no reader holds and that is not the current one; nothing a reader holds is written.
    /// Thread-safe (the builder's worker takes, Main publishes and readers retain/release).
    /// </summary>
    public sealed class PropObstacleMapPool
    {
        private readonly object gate = new object();
        private readonly List<PropObstacleMap> all = new List<PropObstacleMap>();
        private PropObstacleMap current;
        private int versions;

        public int Made { get { lock (gate) return all.Count; } }

        /// <summary>The current version, retained for the caller (null before the first); Release it when done.</summary>
        public PropObstacleMap RetainCurrent()
        {
            lock (gate)
            {
                current?.Retain();
                return current;
            }
        }

        /// <summary>The current version without retaining (a look within one Main turn).</summary>
        public PropObstacleMap Current { get { lock (gate) return current; } }

        /// <summary>A buffer to build into: not the current one, held by no reader.</summary>
        public PropObstacleMap Take(int cells)
        {
            lock (gate)
            {
                PropObstacleMap free = null;
                foreach (var map in all)
                {
                    if (map == current || map.Users != 0 || map.Version < 0) continue;
                    free = map;
                    break;
                }

                if (free == null) { free = new PropObstacleMap(); all.Add(free); }
                free.Version = -1;   // being written: not to be taken again until published or returned
                if (free.Distance.Length != cells * cells) free.Distance = new float[cells * cells];
                return free;
            }
        }

        /// <summary>Makes a built buffer the current version.</summary>
        public void Publish(PropObstacleMap built)
        {
            lock (gate)
            {
                built.Version = ++versions;
                current = built;
            }
        }

        /// <summary>Gives back a buffer taken and not published (a build given up).</summary>
        public void Return(PropObstacleMap taken)
        {
            lock (gate) taken.Version = 0;
        }

        /// <summary>Forgets the current version (the world's end, a new window with nothing built yet keeps the old one instead).</summary>
        public void Clear()
        {
            lock (gate) current = null;
        }
    }

    /// <summary>The sections of one build, kept for a viewer or a test: polygons in planner x, z, counter-clockwise.</summary>
    public sealed class PropSectionList
    {
        public readonly List<float> Points = new List<float>();
        /// <summary>The first point of each polygon (in points, not floats), then the total.</summary>
        public readonly List<int> Start = new List<int> { 0 };
        /// <summary>The input's item each polygon came from.</summary>
        public readonly List<int> Item = new List<int>();

        public int Count => Item.Count;
        public void Clear() { Points.Clear(); Start.Clear(); Start.Add(0); Item.Clear(); }
    }

    /// <summary>
    /// Builds a map version from a numeric input: every convex's section by the plane (edge crossings, then their convex hull
    /// in x, z), the cells the sections cover, and the distance to them. One builder is one thread's (its scratch is its own).
    /// The same code builds in the product's worker and in the replay of saved inputs.
    /// </summary>
    public sealed class PropObstacleBuilder
    {
        private float[] tx = new float[64], ty = new float[64], tz = new float[64];
        private float[] points = new float[128];
        private float[] hull = new float[130];
        private int[] order = new int[64];
        private float[] sectionScratch = new float[64];
        private byte[] occupied = Array.Empty<byte>();
        private float[] squared = Array.Empty<float>();
        private float[] lineIn = Array.Empty<float>(), lineOut = Array.Empty<float>(), parabolaZ = Array.Empty<float>();
        private int[] parabolaV = Array.Empty<int>();
        private readonly Stopwatch watch = new Stopwatch();

        /// <summary>
        /// The static map's blocked cells of a window (1 = the cell's centre is inside a static obstacle): made once a window, on a worker.
        /// A section's cell there is not written -- the static map holds it already; what sticks out of the static obstacle stays.
        /// </summary>
        public static void StaticMask(WalkableMap map, float originX, float originZ, float cell, int cells, byte[] mask)
        {
            if (mask.Length != cells * cells) throw new ArgumentException("the mask's size");
            for (var z = 0; z < cells; z++)
            {
                var wz = originZ + (z + 0.5f) * cell;
                for (var x = 0; x < cells; x++) mask[z * cells + x] = map != null && !map.IsWalkableAt(originX + (x + 0.5f) * cell, wz) ? (byte)1 : (byte)0;
            }
        }

        /// <summary>
        /// The section of one convex by the plane y = planeY after the transform: the hull of its edges' crossings, in x, z,
        /// counter-clockwise, written to `into` (x, z pairs); returns the number of points (0: the plane misses it).
        /// </summary>
        public int Slice(PropShape shape, int convex, float[] matrices, int matrixOffset, float planeY, ref float[] into)
        {
            var v0 = shape.VertexStart[convex]; var vn = shape.VertexStart[convex + 1] - v0;
            if (vn > tx.Length) { var n = Math.Max(vn, tx.Length * 2); tx = new float[n]; ty = new float[n]; tz = new float[n]; }
            var m = matrices; var o = matrixOffset;
            var vertices = shape.Vertices;
            for (var i = 0; i < vn; i++)
            {
                var p = (v0 + i) * 3;
                var x = vertices[p]; var y = vertices[p + 1]; var z = vertices[p + 2];
                tx[i] = m[o] * x + m[o + 1] * y + m[o + 2] * z + m[o + 3];
                ty[i] = m[o + 4] * x + m[o + 5] * y + m[o + 6] * z + m[o + 7] - planeY;
                tz[i] = m[o + 8] * x + m[o + 9] * y + m[o + 10] * z + m[o + 11];
            }

            var e0 = shape.EdgeStart[convex]; var en = shape.EdgeStart[convex + 1] - e0;
            if ((en + vn) * 2 > points.Length) points = new float[(en + vn) * 2 + 32];
            var count = 0;
            for (var i = 0; i < vn; i++)
            {
                if (ty[i] != 0f) continue;   // a vertex on the plane is a point of the section
                points[count * 2] = tx[i]; points[count * 2 + 1] = tz[i]; count++;
            }

            var edges = shape.Edges;
            for (var i = 0; i < en; i++)
            {
                var a = edges[(e0 + i) * 2]; var b = edges[(e0 + i) * 2 + 1];
                var ya = ty[a]; var yb = ty[b];
                if (!((ya < 0f && yb > 0f) || (ya > 0f && yb < 0f))) continue;
                var t = ya / (ya - yb);
                points[count * 2] = tx[a] + (tx[b] - tx[a]) * t; points[count * 2 + 1] = tz[a] + (tz[b] - tz[a]) * t; count++;
            }

            if (count == 0) return 0;
            return Hull(points, count, ref into);
        }

        /// <summary>Andrew's monotone chain: the convex hull of x, z points, counter-clockwise (seen with +z up, +x right), no repeated point.</summary>
        private int Hull(float[] source, int count, ref float[] into)
        {
            if (count > order.Length) order = new int[Math.Max(count, order.Length * 2)];
            for (var i = 0; i < count; i++) order[i] = i;
            // insertion sort by x then z (sections are small)
            for (var i = 1; i < count; i++)
            {
                var k = order[i]; var kx = source[k * 2]; var kz = source[k * 2 + 1];
                var j = i - 1;
                while (j >= 0 && (source[order[j] * 2] > kx || (source[order[j] * 2] == kx && source[order[j] * 2 + 1] > kz))) { order[j + 1] = order[j]; j--; }
                order[j + 1] = k;
            }

            // drop repeated points (sorted: they are neighbours)
            var unique = 0;
            for (var s = 0; s < count; s++)
            {
                var i = order[s];
                if (unique > 0 && source[order[unique - 1] * 2] == source[i * 2] && source[order[unique - 1] * 2 + 1] == source[i * 2 + 1]) continue;
                order[unique++] = i;
            }

            count = unique;
            if ((count + 2) * 2 > hull.Length) hull = new float[(count + 2) * 2 + 32];
            var h = 0;
            if (count < 3)
            {
                for (var s = 0; s < count; s++) { hull[h * 2] = source[order[s] * 2]; hull[h * 2 + 1] = source[order[s] * 2 + 1]; h++; }
            }
            else
            {
                for (var s = 0; s < count; s++)   // the lower chain
                {
                    var i = order[s];
                    var px = source[i * 2]; var pz = source[i * 2 + 1];
                    while (h >= 2 && Cross(hull[(h - 2) * 2], hull[(h - 2) * 2 + 1], hull[(h - 1) * 2], hull[(h - 1) * 2 + 1], px, pz) <= 0f) h--;
                    hull[h * 2] = px; hull[h * 2 + 1] = pz; h++;
                }

                var floor = h + 1;
                for (var s = count - 2; s >= 0; s--)   // the upper chain
                {
                    var i = order[s];
                    var px = source[i * 2]; var pz = source[i * 2 + 1];
                    while (h >= floor && Cross(hull[(h - 2) * 2], hull[(h - 2) * 2 + 1], hull[(h - 1) * 2], hull[(h - 1) * 2 + 1], px, pz) <= 0f) h--;
                    hull[h * 2] = px; hull[h * 2 + 1] = pz; h++;
                }

                h--;   // the last point is the first again
            }

            if (h * 2 > into.Length) into = new float[h * 2 + 32];
            Array.Copy(hull, into, h * 2);
            return h;
        }

        private static float Cross(float ax, float az, float bx, float bz, float cx, float cz) => (bx - ax) * (cz - az) - (bz - az) * (cx - ax);

        /// <summary>The y range and the x, z box of a local box after the transform (its eight corners).</summary>
        public static void TransformBox(float[] bounds, int offset, float[] m, int o, out float minX, out float maxX, out float minY, out float maxY, out float minZ, out float maxZ)
        {
            minX = minY = minZ = float.MaxValue; maxX = maxY = maxZ = float.MinValue;
            for (var corner = 0; corner < 8; corner++)
            {
                var x = bounds[offset + ((corner & 1) != 0 ? 3 : 0)];
                var y = bounds[offset + ((corner & 2) != 0 ? 4 : 1)];
                var z = bounds[offset + ((corner & 4) != 0 ? 5 : 2)];
                var wx = m[o] * x + m[o + 1] * y + m[o + 2] * z + m[o + 3];
                var wy = m[o + 4] * x + m[o + 5] * y + m[o + 6] * z + m[o + 7];
                var wz = m[o + 8] * x + m[o + 9] * y + m[o + 10] * z + m[o + 11];
                if (wx < minX) minX = wx; if (wx > maxX) maxX = wx;
                if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
                if (wz < minZ) minZ = wz; if (wz > maxZ) maxZ = wz;
            }
        }

        /// <summary>
        /// Builds `into` from the input: cleared and made anew. `staticMask` (null: none) marks the cells the static map blocks.
        /// `sections` (null: not kept) receives the polygons.
        /// </summary>
        public void Build(PropObstacleInput input, byte[] staticMask, PropObstacleMap into, PropSectionList sections = null)
        {
            var cells = input.Cells; var cell = input.Cell;
            var total = cells * cells;
            if (into.Distance.Length != total) into.Distance = new float[total];
            if (occupied.Length != total) { occupied = new byte[total]; squared = new float[total]; }
            else Array.Clear(occupied, 0, total);
            if (lineIn.Length < cells) { lineIn = new float[cells]; lineOut = new float[cells]; parabolaZ = new float[cells + 1]; parabolaV = new int[cells]; }
            sections?.Clear();
            var stats = new PropObstacleStats { Items = input.Count };
            var size = cells * cell;
            var windowMaxX = input.OriginX + size; var windowMaxZ = input.OriginZ + size;
            var section = sectionScratch;
            double sectionTicks = 0, rasterTicks = 0;
            var occupiedCount = 0;
            var boxMinX = int.MaxValue; var boxMaxX = int.MinValue; var boxMinZ = int.MaxValue; var boxMaxZ = int.MinValue;

            for (var item = 0; item < input.Count; item++)
            {
                var shape = input.Shapes[item];
                var o = item * 12;
                var reached = false; var allBelow = true;
                for (var convex = 0; convex < shape.ConvexCount; convex++)
                {
                    stats.Convexes++;
                    watch.Restart();
                    TransformBox(shape.Bounds, convex * 6, input.Matrices, o, out var minX, out var maxX, out var minY, out var maxY, out var minZ, out var maxZ);
                    if (maxY < input.PlaneY) { stats.BelowPlane++; sectionTicks += watch.Elapsed.TotalMilliseconds; continue; }
                    allBelow = false;
                    if (minY > input.PlaneY) { stats.AbovePlane++; sectionTicks += watch.Elapsed.TotalMilliseconds; continue; }
                    if (maxX < input.OriginX || minX > windowMaxX || maxZ < input.OriginZ || minZ > windowMaxZ) { stats.OutOfWindow++; sectionTicks += watch.Elapsed.TotalMilliseconds; continue; }
                    var n = Slice(shape, convex, input.Matrices, o, input.PlaneY, ref section);
                    sectionScratch = section;
                    sectionTicks += watch.Elapsed.TotalMilliseconds;
                    if (n == 0) continue;
                    reached = true;
                    if (n < 3) stats.ThinSections++;
                    stats.Sections++;
                    if (sections != null)
                    {
                        for (var i = 0; i < n * 2; i++) sections.Points.Add(section[i]);
                        sections.Start.Add(sections.Points.Count / 2);
                        sections.Item.Add(item);
                    }

                    watch.Restart();
                    Raster(section, n, input, staticMask, ref stats, ref occupiedCount, ref boxMinX, ref boxMaxX, ref boxMinZ, ref boxMaxZ);
                    rasterTicks += watch.Elapsed.TotalMilliseconds;
                }

                if (!reached && allBelow && shape.ConvexCount > 0) stats.ItemsBelowPlane++;
            }

            stats.SectionMilliseconds = sectionTicks;
            stats.RasterMilliseconds = rasterTicks;
            stats.OccupiedCells = occupiedCount;

            watch.Restart();
            var distance = into.Distance;
            var cap = input.MaxDistance;
            if (occupiedCount == 0)
            {
                for (var i = 0; i < total; i++) distance[i] = cap;
            }
            else
            {
                DistanceTransform(cells);
                var half = 0.5f * cell;
                for (var i = 0; i < total; i++)
                {
                    if (occupied[i] != 0) { distance[i] = 0f; continue; }
                    // between cell centres, less the half cell the occupied cell reaches towards this one
                    var d = (float)Math.Sqrt(squared[i]) * cell - half;
                    distance[i] = d < 0f ? 0f : d > cap ? cap : d;
                }
            }

            stats.DistanceMilliseconds = watch.Elapsed.TotalMilliseconds;
            into.InputTime = input.Time;
            into.OriginX = input.OriginX; into.OriginZ = input.OriginZ; into.Cell = cell; into.Cells = cells; into.PlaneY = input.PlaneY; into.MaxDistance = cap;
            into.OccupiedCells = occupiedCount;
            if (occupiedCount > 0)
            {
                into.MinX = input.OriginX + boxMinX * cell; into.MaxX = input.OriginX + (boxMaxX + 1) * cell;
                into.MinZ = input.OriginZ + boxMinZ * cell; into.MaxZ = input.OriginZ + (boxMaxZ + 1) * cell;
            }
            else { into.MinX = into.MinZ = float.MaxValue; into.MaxX = into.MaxZ = float.MinValue; }
            into.Stats = stats;
        }

        /// <summary>
        /// Marks the cells a section covers: a cell is taken when its centre is inside the polygon or nearer than half a cell to
        /// it (so a section thinner than a cell is not lost). Several sections over one cell are one occupied cell.
        /// </summary>
        private void Raster(float[] polygon, int n, PropObstacleInput input, byte[] staticMask, ref PropObstacleStats stats, ref int occupiedCount, ref int boxMinX, ref int boxMaxX, ref int boxMinZ, ref int boxMaxZ)
        {
            var cell = input.Cell; var cells = input.Cells;
            var minX = float.MaxValue; var maxX = float.MinValue; var minZ = float.MaxValue; var maxZ = float.MinValue;
            for (var i = 0; i < n; i++)
            {
                var x = polygon[i * 2]; var z = polygon[i * 2 + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            var half = 0.5f * cell;
            var x0 = (int)Math.Floor((minX - half - input.OriginX) / cell); var x1 = (int)Math.Floor((maxX + half - input.OriginX) / cell);
            var z0 = (int)Math.Floor((minZ - half - input.OriginZ) / cell); var z1 = (int)Math.Floor((maxZ + half - input.OriginZ) / cell);
            if (x0 < 0) x0 = 0; if (z0 < 0) z0 = 0; if (x1 >= cells) x1 = cells - 1; if (z1 >= cells) z1 = cells - 1;
            for (var cz = z0; cz <= z1; cz++)
            {
                var pz = input.OriginZ + (cz + 0.5f) * cell;
                for (var cx = x0; cx <= x1; cx++)
                {
                    var index = cz * cells + cx;
                    if (occupied[index] != 0) continue;
                    var px = input.OriginX + (cx + 0.5f) * cell;
                    if (PolygonDistance(polygon, n, px, pz) > half) continue;
                    if (staticMask != null && staticMask[index] != 0) { stats.StaticSkippedCells++; continue; }
                    occupied[index] = 1;
                    occupiedCount++;
                    if (cx < boxMinX) boxMinX = cx; if (cx > boxMaxX) boxMaxX = cx; if (cz < boxMinZ) boxMinZ = cz; if (cz > boxMaxZ) boxMaxZ = cz;
                }
            }
        }

        /// <summary>The distance from a point to a convex counter-clockwise polygon (0 or less inside); a segment or a point for fewer than three.</summary>
        private static float PolygonDistance(float[] polygon, int n, float px, float pz)
        {
            if (n == 1) { var dx = px - polygon[0]; var dz = pz - polygon[1]; return (float)Math.Sqrt(dx * dx + dz * dz); }
            var inside = n >= 3;
            var best = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var j = i + 1 == n ? 0 : i + 1;
                if (n == 2 && i == 1) break;
                var ax = polygon[i * 2]; var az = polygon[i * 2 + 1]; var bx = polygon[j * 2]; var bz = polygon[j * 2 + 1];
                var ex = bx - ax; var ez = bz - az;
                var wx = px - ax; var wz = pz - az;
                if (inside && ex * wz - ez * wx < 0f) inside = false;   // to the right of a counter-clockwise edge: outside
                var length = ex * ex + ez * ez;
                var t = length > 0f ? (wx * ex + wz * ez) / length : 0f;
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                var qx = wx - ex * t; var qz = wz - ez * t;
                var d = qx * qx + qz * qz;
                if (d < best) best = d;
            }

            return inside ? 0f : (float)Math.Sqrt(best);
        }

        /// <summary>The exact squared distance (in cells) from every cell centre to the nearest occupied cell centre (Felzenszwalb and Huttenlocher's two passes).</summary>
        private void DistanceTransform(int cells)
        {
            const float Far = 1e12f;
            for (var x = 0; x < cells; x++)
            {
                for (var z = 0; z < cells; z++) lineIn[z] = occupied[z * cells + x] != 0 ? 0f : Far;
                Line(cells);
                for (var z = 0; z < cells; z++) squared[z * cells + x] = lineOut[z];
            }

            for (var z = 0; z < cells; z++)
            {
                Array.Copy(squared, z * cells, lineIn, 0, cells);
                Line(cells);
                Array.Copy(lineOut, 0, squared, z * cells, cells);
            }
        }

        private void Line(int n)
        {
            var v = parabolaV; var zs = parabolaZ; var f = lineIn; var d = lineOut;
            var k = 0;
            v[0] = 0; zs[0] = float.NegativeInfinity; zs[1] = float.PositiveInfinity;
            for (var q = 1; q < n; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = ((f[q] + q * q) - (f[p] + p * p)) / (2f * q - 2f * p);
                    if (s > zs[k]) break;
                    k--;
                }

                k++;
                v[k] = q; zs[k] = s; zs[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < n; q++)
            {
                while (zs[k + 1] < q) k++;
                var p = v[k];
                d[q] = (q - p) * (q - p) + f[p];
            }
        }
    }
}
