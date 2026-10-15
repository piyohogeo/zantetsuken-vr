using System;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The finite limits of the bent-partition search (DESIGN 7.9.2, the bent partition): how many frames, seeds,
    /// candidates and iterations it may spend, and how many pieces a partition may have. Every value is the caller's;
    /// the profile holds the provisional ones.
    /// </summary>
    [Serializable]
    public struct SpatialSplitBentLimits
    {
        /// <summary>Frames (a height axis with its sign, and an along axis) tried, of the twelve made of the owner's three axes.</summary>
        public int maxFrames;

        /// <summary>Convexes taken as the seed of the positive side in one frame, smallest projection first.</summary>
        public int maxSeeds;

        /// <summary>Closures that read the triangles, over all frames and seeds (one for a seed whose convexes alone close).</summary>
        public int maxCandidates;

        /// <summary>Iterations of one closure: the surface is remade and what it crosses is absorbed, this many times at most.</summary>
        public int maxIterations;

        /// <summary>Pieces of the partition, at most <see cref="MaxPieces"/> (16 since 2026-10-15: eight left no room for the knots a few convexes' ends ask).</summary>
        public int maxPieces;

        /// <summary>The most pieces a partition can have at all: the surface's own room.</summary>
        public const int MaxPieces = 16;

        public const int MaxFrames = 12;

        /// <summary>The provisional values (2026-10-15, from no measurement).</summary>
        public const int DefaultFrames = 12, DefaultSeeds = 2, DefaultCandidates = 8, DefaultIterations = 4, DefaultPieces = 16;

        public SpatialSplitBentLimits(int maxFrames, int maxSeeds, int maxCandidates, int maxIterations, int maxPieces)
        {
            this.maxFrames = maxFrames;
            this.maxSeeds = maxSeeds;
            this.maxCandidates = maxCandidates;
            this.maxIterations = maxIterations;
            this.maxPieces = maxPieces;
        }

        public static SpatialSplitBentLimits Default =>
            new SpatialSplitBentLimits(DefaultFrames, DefaultSeeds, DefaultCandidates, DefaultIterations, DefaultPieces);

        public bool IsValid => maxFrames >= 0 && maxFrames <= MaxFrames && maxSeeds >= 1 && maxCandidates >= 1
            && maxIterations >= 1 && maxPieces >= 1 && maxPieces <= MaxPieces;

        public bool Equals(in SpatialSplitBentLimits other)
        {
            return maxFrames == other.maxFrames && maxSeeds == other.maxSeeds && maxCandidates == other.maxCandidates
                && maxIterations == other.maxIterations && maxPieces == other.maxPieces;
        }
    }

    /// <summary>
    /// A bent partition: in the owner's frame, a height axis n and an along axis u (unit, perpendicular), and a
    /// continuous piecewise-linear height h = f(u) through knots u_0 &lt; ... &lt; u_K, flat beyond the first and the
    /// last; the surface is this polyline carried along the third axis without end. It is the graph of a function,
    /// so it has one height over every point, meets itself nowhere, and splits space into the side above (positive:
    /// h - f(u) &gt; 0) and the side below. The third axis is free, so every question about it is a question in the
    /// (u, h) plane: a convex or a triangle is its projection there, a convex polygon.
    /// </summary>
    public unsafe struct SpatialSplitBentSurface
    {
        public const int MaxKnots = SpatialSplitBentLimits.MaxPieces + 1;

        /// <summary>The height axis n (unit, the owner's frame): the side above is where h = dot(n, x) is greater than the surface's.</summary>
        public float3 height;

        /// <summary>The along axis u (unit, perpendicular to <see cref="height"/>), the knots' coordinate.</summary>
        public float3 along;

        public int knotCount;
        public fixed float knotU[MaxKnots];
        public fixed float knotH[MaxKnots];

        public int Pieces => knotCount - 1;

        public float KnotU(int k)
        {
            return knotU[k];
        }

        public float KnotH(int k)
        {
            return knotH[k];
        }

        /// <summary>A point of the owner's frame in the (u, h) plane.</summary>
        public float2 Project(float3 pointOwner)
        {
            return new float2(math.dot(along, pointOwner), math.dot(height, pointOwner));
        }

        /// <summary>f(u): the height of the surface over u, flat beyond the first and the last knot.</summary>
        public float HeightAt(float u)
        {
            if (knotCount <= 0) return float.NaN;
            if (u <= knotU[0]) return knotH[0];
            int last = knotCount - 1;
            if (u >= knotU[last]) return knotH[last];
            for (int k = 0; k < last; k++)
            {
                float a = knotU[k], b = knotU[k + 1];
                if (u <= b)
                {
                    float t = b > a ? (u - a) / (b - a) : 0f;
                    return knotH[k] + (t * (knotH[k + 1] - knotH[k]));
                }
            }

            return knotH[last];
        }

        /// <summary>The signed value h - f(u) of a point: positive above. Not a distance.</summary>
        public float Above(float2 q)
        {
            return q.y - HeightAt(q.x);
        }

        /// <summary>The distance in the (u, h) plane from a point to the polyline, its two flat ends included: a length.</summary>
        public float Distance(float2 q)
        {
            if (knotCount <= 0) return float.NaN;
            int last = knotCount - 1;
            var first = new float2(knotU[0], knotH[0]);
            var end = new float2(knotU[last], knotH[last]);
            float best = q.x <= first.x ? math.abs(q.y - first.y) : math.distance(q, first);
            best = math.min(best, q.x >= end.x ? math.abs(q.y - end.y) : math.distance(q, end));
            for (int k = 0; k < last; k++)
            {
                best = math.min(best, DistanceToSegment(q, new float2(knotU[k], knotH[k]), new float2(knotU[k + 1], knotH[k + 1])));
            }

            return best;
        }

        /// <summary>The signed distance of a point: its distance to the surface, negative below. The anchors' rule reads this.</summary>
        public float SignedDistance(float2 q)
        {
            float d = Distance(q);
            return Above(q) < 0f ? -d : d;
        }

        internal static float DistanceToSegment(float2 q, float2 a, float2 b)
        {
            float2 e = b - a;
            float l2 = math.lengthsq(e);
            float t = l2 > 0f ? math.clamp(math.dot(q - a, e) / l2, 0f, 1f) : 0f;
            return math.distance(q, a + (t * e));
        }
    }

    /// <summary>
    /// The bent-partition search (DESIGN 7.9.2, the bent partition; TL 2026-10-15): where no plane leaves every convex
    /// and every triangle whole on a side, a <see cref="SpatialSplitBentSurface"/> that does -- a few plane pieces
    /// joined without a gap -- found by a local search and verified for every element over its whole extent, not by
    /// its vertices alone. No convex is cut, no triangle is cut, nothing is drawn or cooked for the surface: it only
    /// decides which side each convex and each triangle belongs to.
    /// <para>
    /// **Verifying one element.** Its vertices are projected to the (u, h) plane, where it is a convex polygon (the
    /// hull of the points). It lies wholly above the surface with the margin when: every vertex has h - f(u) &gt; 0;
    /// at every knot inside the element's u range the polygon's lower chain is above the knot (the difference of two
    /// piecewise-linear functions is positive on an interval when it is positive at every breakpoint of either, so a
    /// fold that would reach into the polygon between its vertices is caught here); and the distance in the plane
    /// between the polygon and the polyline is the margin or more -- the least of every vertex's distance to the
    /// polyline and every knot's distance to the polygon's edges, which is the least distance between two polylines
    /// that do not cross. Below is the mirror. The function's value is never read as a distance.
    /// </para>
    /// <para>
    /// **The search.** The frames are the twelve pairs of the owner's axes (a height axis with either sign, an along
    /// axis), up to a limit. In a frame the knots are placed where the convexes' extents along u begin or end: two
    /// knots inside every interval between consecutive ends, pulled in from the ends by twice the margin, so that a
    /// steep step can happen there; the widest intervals lose theirs first when there are more than the pieces
    /// allow. The heights come from a seed: a convex, smallest projection first, taken as the positive side. The
    /// surface is the positive set's lower envelope in each column less twice the margin (a height above everything
    /// in a column with nothing positive), the knots at the lower of their two columns, and every element it does
    /// not leave wholly below is absorbed into the positive side; that is repeated until nothing moves, within a
    /// limit of iterations. First with the convexes alone, which is cheap and refuses most frames and seeds; then,
    /// counted against the limit of candidates, with the triangles. A surface that holds is taken when both sides
    /// have a convex and a triangle. The first that holds is taken; there is no best surface.
    /// </para>
    /// <para>
    /// Everything is read through the pointers given and nothing is kept, as the plane searches: it runs on a worker
    /// with the index range leased and the shape held by the caller. No allocation.
    /// </para>
    /// </summary>
    public static unsafe class CutSpatialSplitBentSearch
    {
        private const int MaxKnots = SpatialSplitBentSurface.MaxKnots;

        // The scratch, in order: the convexes' sides (the result, ConvexSide bytes), whether each convex is positive
        // while the closure runs, one bit a triangle for the same, the convexes' u extents in the frame, their
        // projected box areas, the seed order, the sorted ends, each column's height, and the room one element's
        // projection and hull take.
        private static void Layout(
            int convexCount, int maxConvexVertices, int triangles,
            out long sidesAt, out long inPositiveAt, out long triangleBitsAt, out long convexUAt, out long convexAxisAt, out long areasAt,
            out long seedOrderAt, out long endsAt, out long takenAt, out long columnsAt, out long pointsAt, out long workAt, out long bytes)
        {
            int vertices = Math.Max(3, maxConvexVertices);
            sidesAt = 0;
            inPositiveAt = sidesAt + Align16(convexCount);
            triangleBitsAt = inPositiveAt + Align16(convexCount);
            convexUAt = triangleBitsAt + Align16((triangles + 7) / 8);
            convexAxisAt = convexUAt + Align16(2L * convexCount * sizeof(float));
            areasAt = convexAxisAt + Align16(2L * convexCount * sizeof(float3));
            seedOrderAt = areasAt + Align16((long)convexCount * sizeof(float));
            endsAt = seedOrderAt + Align16((long)convexCount * sizeof(int));
            takenAt = endsAt + Align16(2L * convexCount * sizeof(float));
            columnsAt = takenAt + Align16(2L * convexCount);
            pointsAt = columnsAt + Align16((long)MaxKnots * sizeof(float));
            workAt = pointsAt + Align16((long)vertices * sizeof(float2));
            bytes = workAt + Align16(((3L * vertices) + 2) * sizeof(int));
        }

        /// <summary>Scratch the search needs, in bytes, for this many convexes, the most vertices one of them has, and this many triangles.</summary>
        public static int ScratchBytes(int convexCount, int maxConvexVertices, int triangles)
        {
            Layout(convexCount, maxConvexVertices, triangles, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out long bytes);
            return checked((int)bytes);
        }

        /// <summary>Where the convexes' sides are in the scratch after <see cref="FindBentSurface"/> found: one <see cref="ConvexSide"/> byte a convex.</summary>
        public static byte* SidesOf(byte* scratch)
        {
            return scratch;
        }

        /// <summary>
        /// The search. What the plane searches counted stays in the record; the surface and the sides are this one's.
        /// <paramref name="limits"/> is the plane searches' (the convex and triangle maxima are read from it);
        /// <paramref name="bent"/> this search's own. <paramref name="scratch"/> is at least <see cref="ScratchBytes"/>
        /// bytes for the input's counts.
        /// </summary>
        public static SpatialSplitSearchOutcome FindBentSurface(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount, in float4x4 geometryToOwner,
            ConvexBrepBank* banks, ConvexBrepRange* convexes, int convexCount, in float4x4 shapeToOwner,
            float margin, in SpatialSplitSearchLimits limits, in SpatialSplitBentLimits bent, byte* scratch, ref SpatialSplitSearchResult result)
        {
            SpatialSplitSearchResult first = result;
            result = default;
            result.directionsTried = first.directionsTried;
            result.planesTested = first.planesTested;
            result.triangleVisits = first.triangleVisits;
            result.meshLed = first.meshLed;   // whether the search went through the mesh-led one stays in the record
            result.meshDirectionsTried = first.meshDirectionsTried;
            result.meshPlanesTested = first.meshPlanesTested;
            result.refusedForTriangleMinimum = first.refusedForTriangleMinimum;
            result.refusedForNoConvexOnASide = first.refusedForNoConvexOnASide;
            result.margin = margin;
            result.bent = true;
            if (vertices == null || indices == null || banks == null || convexes == null || scratch == null
                || vertexViewLength <= 0 || indexCount <= 0 || indexCount % 3 != 0 || convexCount <= 0
                || !(margin > 0f) || !math.isfinite(margin) || !limits.IsValid || !bent.IsValid
                || !IsFinite(geometryToOwner) || !IsFinite(shapeToOwner))
            {
                return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
            }

            int triangles = indexCount / 3;
            if (convexCount < 2 || triangles < 2)
            {
                // Both sides need a convex of their own: no convex is cut here.
                return result.outcome = SpatialSplitSearchOutcome.NotFound;
            }

            if (convexCount > limits.maxConvexes || triangles > limits.maxTriangles)
            {
                return result.outcome = SpatialSplitSearchOutcome.OverTheLimits;
            }

            int maxConvexVertices = 0;
            for (int c = 0; c < convexCount; c++)
            {
                if (convexes[c].vertexCount <= 0)
                {
                    return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
                }

                maxConvexVertices = Math.Max(maxConvexVertices, convexes[c].vertexCount);
            }

            for (int i = 0; i < indexCount; i++)
            {
                if (indices[i] >= (uint)vertexViewLength)
                {
                    return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
                }
            }

            Layout(convexCount, maxConvexVertices, triangles,
                out long sidesAt, out long inPositiveAt, out long triangleBitsAt, out long convexUAt, out long convexAxisAt, out long areasAt,
                out long seedOrderAt, out long endsAt, out long takenAt, out long columnsAt, out long pointsAt, out long workAt, out _);
            byte* sides = scratch + sidesAt;
            byte* inPositive = scratch + inPositiveAt;
            byte* triangleBits = scratch + triangleBitsAt;
            float* convexU = (float*)(scratch + convexUAt);
            float3* convexAxis = (float3*)(scratch + convexAxisAt);
            float* areas = (float*)(scratch + areasAt);
            int* seedOrder = (int*)(scratch + seedOrderAt);
            float* ends = (float*)(scratch + endsAt);
            byte* taken = scratch + takenAt;
            float* columns = (float*)(scratch + columnsAt);
            float2* points = (float2*)(scratch + pointsAt);
            int* work = (int*)(scratch + workAt);

            var state = new State
            {
                vertices = vertices, indices = indices, indexCount = indexCount, triangles = triangles,
                geometryToOwner = geometryToOwner, banks = banks, convexes = convexes, convexCount = convexCount,
                shapeToOwner = shapeToOwner, margin = margin, points = points, work = work,
                inPositive = inPositive, triangleBits = triangleBits, columns = columns,
            };

            // The extents along the owner's three axes, once for every frame (each frame's u and h are an axis with a
            // sign): of everything, and of each convex (its box's three sides, for its extent along u and its area).
            float3 allLow = new float3(float.PositiveInfinity), allHigh = new float3(float.NegativeInfinity);
            for (int c = 0; c < convexCount; c++)
            {
                float3 low = new float3(float.PositiveInfinity), high = new float3(float.NegativeInfinity);
                ConvexBrepRange range = convexes[c];
                float3* source = banks[c].vertices + range.vertexBase;
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 p = math.transform(shapeToOwner, source[v]);
                    if (!math.all(math.isfinite(p)))
                    {
                        return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                    }

                    low = math.min(low, p);
                    high = math.max(high, p);
                }

                convexAxis[2 * c] = low;
                convexAxis[(2 * c) + 1] = high;
                allLow = math.min(allLow, low);
                allHigh = math.max(allHigh, high);
            }

            for (int i = 0; i < indexCount; i++)
            {
                float3 p = math.transform(geometryToOwner, vertices[indices[i]].position);
                if (!math.all(math.isfinite(p)))
                {
                    return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                }

                allLow = math.min(allLow, p);
                allHigh = math.max(allHigh, p);
            }

            result.triangleVisits += triangles;

            // The twelve frames: a height axis with its sign, and an along axis among the other two.
            for (int frame = 0; frame < SpatialSplitBentLimits.MaxFrames && frame < bent.maxFrames; frame++)
            {
                int heightAxis = frame / 4;
                float sign = (frame & 2) == 0 ? 1f : -1f;
                int alongAxis = ((frame & 1) == 0 ? heightAxis + 1 : heightAxis + 2) % 3;
                var surface = default(SpatialSplitBentSurface);
                surface.height = sign * Axis(heightAxis);
                surface.along = Axis(alongAxis);
                state.surface = surface;
                result.bentFramesTried++;

                // 1. The extents in this frame, from the axes'.
                float u0 = allLow[alongAxis], u1 = allHigh[alongAxis];
                float h0 = sign > 0f ? allLow[heightAxis] : -allHigh[heightAxis];
                float h1 = sign > 0f ? allHigh[heightAxis] : -allLow[heightAxis];
                for (int c = 0; c < convexCount; c++)
                {
                    float3 low = convexAxis[2 * c], high = convexAxis[(2 * c) + 1];
                    convexU[2 * c] = low[alongAxis];
                    convexU[(2 * c) + 1] = high[alongAxis];
                    areas[c] = (high[alongAxis] - low[alongAxis]) * (high[heightAxis] - low[heightAxis]);
                }

                if (!(u1 - u0 > 4f * margin))
                {
                    continue;   // nothing to bend along
                }

                // Above everything, by a clear height: the pieces over columns with nothing positive.
                state.cap = h1 + math.max(4f * margin, 0.1f * (h1 - h0));

                // 2. The knots.
                PlaceKnots(ref surface, convexU, convexCount, ends, taken, u0, u1, margin, bent.maxPieces);
                state.surface = surface;

                // 3. The seeds: the convexes by their projected box's area, smallest first.
                for (int c = 0; c < convexCount; c++) seedOrder[c] = c;
                for (int i = 1; i < convexCount; i++)
                {
                    int moving = seedOrder[i];
                    int at = i - 1;
                    while (at >= 0 && areas[seedOrder[at]] > areas[moving])
                    {
                        seedOrder[at + 1] = seedOrder[at];
                        at--;
                    }

                    seedOrder[at + 1] = moving;
                }

                for (int s = 0; s < convexCount && s < bent.maxSeeds; s++)
                {
                    int seed = seedOrder[s];
                    result.bentSeedsTried++;
                    for (int c = 0; c < convexCount; c++) inPositive[c] = 0;
                    inPositive[seed] = 1;

                    // The convexes alone first: cheap, and what it refuses no surface could hold for.
                    SpatialSplitSearchOutcome closed = Close(ref state, bent.maxIterations, false, ref result);
                    if (closed == SpatialSplitSearchOutcome.NotFinite)
                    {
                        return result.outcome = closed;
                    }

                    if (closed != SpatialSplitSearchOutcome.Found)
                    {
                        continue;
                    }

                    if (result.bentCandidates >= bent.maxCandidates)
                    {
                        return result.outcome = SpatialSplitSearchOutcome.NotFound;
                    }

                    result.bentCandidates++;
                    for (int b = 0; b < (triangles + 7) / 8; b++) triangleBits[b] = 0;
                    closed = Close(ref state, bent.maxIterations, true, ref result);
                    if (closed == SpatialSplitSearchOutcome.NotFinite)
                    {
                        return result.outcome = closed;
                    }

                    if (closed != SpatialSplitSearchOutcome.Found)
                    {
                        continue;
                    }

                    // 4. Found: the record, and the sides for the caller.
                    int positiveConvexes = 0;
                    for (int c = 0; c < convexCount; c++)
                    {
                        sides[c] = (byte)(inPositive[c] != 0 ? ConvexSide.Positive : ConvexSide.Negative);
                        if (inPositive[c] != 0) positiveConvexes++;
                    }

                    surface = state.surface;
                    float meanHeight = 0f;
                    for (int k = 0; k < surface.knotCount; k++) meanHeight += surface.KnotH(k);
                    meanHeight /= surface.knotCount;
                    result.bentSurface = surface;
                    result.bentPieces = surface.Pieces;
                    result.planeOwner = new float4(surface.height, -meanHeight);   // the base direction, for the record: the surface is bentSurface
                    result.planeGeometry = default;
                    result.planeShape = default;
                    result.gap = state.leastClearance;
                    result.positiveConvexes = positiveConvexes;
                    result.negativeConvexes = convexCount - positiveConvexes;
                    result.positiveTriangles = state.positiveTriangles;
                    result.negativeTriangles = triangles - state.positiveTriangles;
                    return result.outcome = SpatialSplitSearchOutcome.Found;
                }
            }

            return result.outcome = SpatialSplitSearchOutcome.NotFound;
        }

        private static float3 Axis(int axis)
        {
            return axis == 0 ? new float3(1f, 0f, 0f) : axis == 1 ? new float3(0f, 1f, 0f) : new float3(0f, 0f, 1f);
        }

        // The knots of a frame: the whole extent's ends, and inside every interval between consecutive distinct ends
        // of the convexes' extents two knots pulled in by twice the margin (one in the middle where the interval holds
        // no room for two). Over the pieces allowed, the intervals that are one convex's own span (its two ends and
        // no other end between) lose their knots first, and then the widest: a step is wanted where one element ends
        // and another begins, and a long stretch is one piece.
        private static void PlaceKnots(
            ref SpatialSplitBentSurface surface, float* convexU, int convexCount, float* ends, byte* taken, float u0, float u1, float margin, int maxPieces)
        {
            int n = 2 * convexCount;
            for (int i = 0; i < n; i++)
            {
                ends[i] = convexU[i];
                taken[i] = 0;
            }

            for (int i = 1; i < n; i++)
            {
                float moving = ends[i];
                int at = i - 1;
                while (at >= 0 && ends[at] > moving)
                {
                    ends[at + 1] = ends[at];
                    at--;
                }

                ends[at + 1] = moving;
            }

            float pull = 2f * margin;
            int maxKnots = Math.Min(MaxKnots, maxPieces + 1);

            // The intervals, narrowest first, each with the knots it would place: taken while they fit.
            // The knots are gathered in the surface's own buffers, unsorted, then sorted.
            surface.knotCount = 0;
            surface.knotU[surface.knotCount++] = u0;
            surface.knotU[surface.knotCount++] = u1;
            while (true)
            {
                // The narrowest interval not yet taken and wide enough for a knot, among those that are not one
                // convex's own span while any such is left.
                int best = -1;
                float bestWidth = float.PositiveInfinity;
                bool bestOwn = true;
                for (int i = 0; i + 1 < n; i++)
                {
                    if (taken[i] != 0) continue;
                    float a = ends[i], b = ends[i + 1];
                    float width = b - a;
                    if (!(width > 2f * margin)) continue;
                    if (a <= u0 && b >= u1) continue;   // the whole extent: its ends are knots already
                    bool own = false;
                    for (int c = 0; c < convexCount && !own; c++) own = convexU[2 * c] == a && convexU[(2 * c) + 1] == b;
                    if ((bestOwn && !own) || (own == bestOwn && width < bestWidth))
                    {
                        bestWidth = width;
                        best = i;
                        bestOwn = own;
                    }
                }

                if (best < 0) break;
                float from = ends[best], to = ends[best + 1];
                taken[best] = 1;
                int wanted = to - from >= (2f * pull) + (2f * margin) ? 2 : 1;
                if (surface.knotCount + wanted > maxKnots) break;
                if (wanted == 2)
                {
                    AddKnot(ref surface, from + pull, u0, u1, margin);
                    AddKnot(ref surface, to - pull, u0, u1, margin);
                }
                else
                {
                    AddKnot(ref surface, 0.5f * (from + to), u0, u1, margin);
                }
            }

            for (int i = 1; i < surface.knotCount; i++)
            {
                float moving = surface.knotU[i];
                int at = i - 1;
                while (at >= 0 && surface.knotU[at] > moving)
                {
                    surface.knotU[at + 1] = surface.knotU[at];
                    at--;
                }

                surface.knotU[at + 1] = moving;
            }
        }

        // A knot strictly inside the extent and at least a margin from every knot placed so far.
        private static void AddKnot(ref SpatialSplitBentSurface surface, float u, float u0, float u1, float margin)
        {
            if (!(u > u0 + margin) || !(u < u1 - margin) || surface.knotCount >= MaxKnots) return;
            for (int k = 0; k < surface.knotCount; k++)
            {
                if (math.abs(surface.knotU[k] - u) < margin) return;
            }

            surface.knotU[surface.knotCount++] = u;
        }

        /// <summary>What one frame's closure reads and writes; the pointers are the caller's scratch.</summary>
        private struct State
        {
            internal VpRenderVertex* vertices;
            internal uint* indices;
            internal int indexCount, triangles;
            internal float4x4 geometryToOwner, shapeToOwner;
            internal ConvexBrepBank* banks;
            internal ConvexBrepRange* convexes;
            internal int convexCount;
            internal float margin, cap;
            internal float2* points;
            internal int* work;
            internal byte* inPositive;
            internal byte* triangleBits;
            internal float* columns;
            internal SpatialSplitBentSurface surface;
            internal float leastClearance;
            internal int positiveTriangles;
        }

        // The closure of one seed: the surface remade from the positive set, every element it does not leave wholly
        // below absorbed, until nothing moves; then the positive set verified above. Found when every element lies
        // wholly on its side with the margin and both sides have a convex (and, with the triangles read, a triangle).
        private static SpatialSplitSearchOutcome Close(ref State s, int maxIterations, bool withTriangles, ref SpatialSplitSearchResult result)
        {
            int convexCount = s.convexCount;
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                result.bentIterations++;
                if (!Remake(ref s, withTriangles))
                {
                    return SpatialSplitSearchOutcome.NotFinite;
                }

                // Every element not positive must lie wholly below; one that does not is absorbed.
                int moved = 0;
                int negativeConvexes = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    if (s.inPositive[c] != 0) continue;
                    int side = SideOfConvex(ref s, c, out _);
                    if (side == int.MinValue) return SpatialSplitSearchOutcome.NotFinite;
                    if (side < 0) negativeConvexes++;
                    else { s.inPositive[c] = 1; moved++; }
                }

                result.bentElementsVerified += convexCount;
                if (negativeConvexes == 0)
                {
                    return SpatialSplitSearchOutcome.NotFound;   // every convex went positive: nothing would stand on the other side
                }

                int negativeTriangles = 0;
                if (withTriangles)
                {
                    for (int t = 0; t < s.triangles; t++)
                    {
                        if ((s.triangleBits[t >> 3] & (1 << (t & 7))) != 0) continue;
                        int side = SideOfTriangle(ref s, t, out _);
                        if (side == int.MinValue) return SpatialSplitSearchOutcome.NotFinite;
                        if (side < 0) negativeTriangles++;
                        else { s.triangleBits[t >> 3] |= (byte)(1 << (t & 7)); moved++; }
                    }

                    result.triangleVisits += s.triangles;
                    result.bentElementsVerified += s.triangles;
                    if (negativeTriangles == 0)
                    {
                        return SpatialSplitSearchOutcome.NotFound;
                    }
                }

                if (moved > 0)
                {
                    continue;
                }

                // Nothing moved: the negative side holds. The positive side, by the same test, with the margin.
                float least = float.PositiveInfinity;
                int positiveTriangles = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    int side = SideOfConvex(ref s, c, out float clearance);
                    if (side == int.MinValue) return SpatialSplitSearchOutcome.NotFinite;
                    if ((s.inPositive[c] != 0) != (side > 0) || side == 0) return SpatialSplitSearchOutcome.NotFound;
                    least = math.min(least, clearance);
                }

                result.bentElementsVerified += convexCount;
                if (withTriangles)
                {
                    for (int t = 0; t < s.triangles; t++)
                    {
                        bool positive = (s.triangleBits[t >> 3] & (1 << (t & 7))) != 0;
                        int side = SideOfTriangle(ref s, t, out float clearance);
                        if (side == int.MinValue) return SpatialSplitSearchOutcome.NotFinite;
                        if (positive != (side > 0) || side == 0) return SpatialSplitSearchOutcome.NotFound;
                        if (positive) positiveTriangles++;
                        least = math.min(least, clearance);
                    }

                    result.triangleVisits += s.triangles;
                    result.bentElementsVerified += s.triangles;
                    if (positiveTriangles == 0)
                    {
                        return SpatialSplitSearchOutcome.NotFound;
                    }

                    s.positiveTriangles = positiveTriangles;
                    s.leastClearance = least;
                }

                return SpatialSplitSearchOutcome.Found;
            }

            return SpatialSplitSearchOutcome.NotFound;
        }

        // The surface from the positive set: each column's height is the least of the positive elements' lower chains
        // over it, less twice the margin, or the cap where none reaches; each knot the lower of its two columns' heights.
        // False when a projection is not finite.
        private static bool Remake(ref State s, bool withTriangles)
        {
            SpatialSplitBentSurface surface = s.surface;
            int pieces = surface.Pieces;
            for (int c = 0; c < pieces; c++) s.columns[c] = s.cap;
            for (int c = 0; c < s.convexCount; c++)
            {
                if (s.inPositive[c] == 0) continue;
                if (!ProjectConvex(ref s, c, out int count)) return false;
                LowerIntoColumns(ref s, count);
            }

            if (withTriangles)
            {
                for (int t = 0; t < s.triangles; t++)
                {
                    if ((s.triangleBits[t >> 3] & (1 << (t & 7))) == 0) continue;
                    if (!ProjectTriangle(ref s, t)) return false;
                    LowerIntoColumns(ref s, 3);
                }
            }

            float pull = 2f * s.margin;
            for (int k = 0; k < surface.knotCount; k++)
            {
                float left = k > 0 ? s.columns[k - 1] : s.cap;
                float right = k < pieces ? s.columns[k] : s.cap;
                float h = math.min(left, right);
                surface.knotH[k] = h < s.cap ? h - pull : s.cap;
            }

            s.surface = surface;
            return true;
        }

        // Lowers each column's height to the least height of the projected element (points[0..count)) over the column:
        // the least of its lower chain at the column's ends and of its points inside.
        private static void LowerIntoColumns(ref State s, int count)
        {
            float2* q = s.points;
            int* order = s.work;
            int* lower = s.work + count;
            int lowerCount = LowerChain(q, count, order, lower);
            float uMin = q[lower[0]].x, uMax = q[lower[lowerCount - 1]].x;
            SpatialSplitBentSurface surface = s.surface;
            for (int c = 0; c < surface.Pieces; c++)
            {
                float a = surface.KnotU(c), b = surface.KnotU(c + 1);
                if (uMax < a || uMin > b) continue;
                float least = math.min(ChainAt(q, lower, lowerCount, math.clamp(a, uMin, uMax)), ChainAt(q, lower, lowerCount, math.clamp(b, uMin, uMax)));
                for (int i = 0; i < count; i++)
                {
                    if (q[i].x >= a && q[i].x <= b) least = math.min(least, q[i].y);
                }

                s.columns[c] = math.min(s.columns[c], least);
            }
        }

        private static bool ProjectConvex(ref State s, int c, out int count)
        {
            ConvexBrepRange range = s.convexes[c];
            float3* source = s.banks[c].vertices + range.vertexBase;
            count = range.vertexCount;
            for (int v = 0; v < count; v++)
            {
                float2 q = s.surface.Project(math.transform(s.shapeToOwner, source[v]));
                if (!math.all(math.isfinite(q))) return false;
                s.points[v] = q;
            }

            return true;
        }

        private static bool ProjectTriangle(ref State s, int t)
        {
            for (int k = 0; k < 3; k++)
            {
                float2 q = s.surface.Project(math.transform(s.geometryToOwner, s.vertices[s.indices[(3 * t) + k]].position));
                if (!math.all(math.isfinite(q))) return false;
                s.points[k] = q;
            }

            return true;
        }

        // +1 wholly above with the margin, -1 wholly below, 0 neither; int.MinValue when a projection is not finite.
        private static int SideOfConvex(ref State s, int c, out float clearance)
        {
            clearance = 0f;
            if (!ProjectConvex(ref s, c, out int count)) return int.MinValue;
            return SideOfPolygon(in s.surface, s.points, count, s.margin, s.work, out clearance);
        }

        private static int SideOfTriangle(ref State s, int t, out float clearance)
        {
            clearance = 0f;
            if (!ProjectTriangle(ref s, t)) return int.MinValue;
            return SideOfPolygon(in s.surface, s.points, 3, s.margin, s.work, out clearance);
        }

        /// <summary>
        /// The side of one element from its projected points (finite; <paramref name="work"/> holds 3 * count + 2
        /// ints): +1 wholly above the surface with the margin, -1 wholly below, 0 neither -- a vertex on the wrong
        /// side or on the surface, a fold reaching into the polygon between its vertices, or a distance under the
        /// margin. <paramref name="clearance"/> is the distance found, a length in the (u, h) plane.
        /// </summary>
        public static int SideOfPolygon(in SpatialSplitBentSurface surface, float2* q, int count, float margin, int* work, out float clearance)
        {
            clearance = 0f;
            if (count <= 0) return 0;
            int above = 0, below = 0;
            for (int i = 0; i < count; i++)
            {
                float d = surface.Above(q[i]);
                if (d > 0f) above++;
                else if (d < 0f) below++;
                else return 0;
            }

            if (above > 0 && below > 0) return 0;
            int side = above > 0 ? 1 : -1;

            int* order = work;
            int* lower = work + count;
            int* upper = work + (2 * count) + 1;
            int lowerCount = LowerChain(q, count, order, lower);
            int upperCount = UpperChain(q, count, order, upper);
            float uMin = q[lower[0]].x, uMax = q[lower[lowerCount - 1]].x;

            // Between the vertices: at every knot inside the element's extent, the chain facing the surface is still beyond it.
            for (int k = 0; k < surface.knotCount; k++)
            {
                float u = surface.KnotU(k);
                if (u <= uMin || u >= uMax) continue;
                float h = surface.KnotH(k);
                if (side > 0 ? !(ChainAt(q, lower, lowerCount, u) > h) : !(ChainAt(q, upper, upperCount, u) < h)) return 0;
            }

            // The distance between the polygon and the polyline: every vertex to the polyline, every knot to the polygon's edges.
            float least = float.PositiveInfinity;
            for (int i = 0; i < count; i++) least = math.min(least, surface.Distance(q[i]));
            for (int k = 0; k < surface.knotCount; k++)
            {
                var knot = new float2(surface.KnotU(k), surface.KnotH(k));
                least = math.min(least, DistanceToChain(q, lower, lowerCount, knot));
                least = math.min(least, DistanceToChain(q, upper, upperCount, knot));
            }

            clearance = least;
            return least >= margin ? side : 0;
        }

        // The lower chain of the points' hull, by u then h, left to right (Andrew's monotone chain): indices into q.
        // One point is a chain of one; collinear points keep their ends only. Returns its count (1 or more).
        private static int LowerChain(float2* q, int count, int* order, int* chain)
        {
            Sort(q, count, order);
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                int p = order[i];
                while (n >= 2 && Cross(q[chain[n - 2]], q[chain[n - 1]], q[p]) <= 0f) n--;
                chain[n++] = p;
            }

            return n;
        }

        private static int UpperChain(float2* q, int count, int* order, int* chain)
        {
            Sort(q, count, order);
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                int p = order[i];
                while (n >= 2 && Cross(q[chain[n - 2]], q[chain[n - 1]], q[p]) >= 0f) n--;
                chain[n++] = p;
            }

            return n;
        }

        private static void Sort(float2* q, int count, int* order)
        {
            for (int i = 0; i < count; i++) order[i] = i;
            for (int i = 1; i < count; i++)
            {
                int moving = order[i];
                int at = i - 1;
                while (at >= 0 && (q[order[at]].x > q[moving].x || (q[order[at]].x == q[moving].x && q[order[at]].y > q[moving].y)))
                {
                    order[at + 1] = order[at];
                    at--;
                }

                order[at + 1] = moving;
            }
        }

        private static float Cross(float2 o, float2 a, float2 b)
        {
            return ((a.x - o.x) * (b.y - o.y)) - ((a.y - o.y) * (b.x - o.x));
        }

        // The chain's height at u, within the chain's extent (clamped to it).
        private static float ChainAt(float2* q, int* chain, int chainCount, float u)
        {
            if (chainCount == 1 || u <= q[chain[0]].x) return q[chain[0]].y;
            if (u >= q[chain[chainCount - 1]].x) return q[chain[chainCount - 1]].y;
            for (int i = 0; i + 1 < chainCount; i++)
            {
                float2 a = q[chain[i]], b = q[chain[i + 1]];
                if (u <= b.x)
                {
                    float t = b.x > a.x ? (u - a.x) / (b.x - a.x) : 0f;
                    return a.y + (t * (b.y - a.y));
                }
            }

            return q[chain[chainCount - 1]].y;
        }

        private static float DistanceToChain(float2* q, int* chain, int chainCount, float2 p)
        {
            if (chainCount == 1) return math.distance(p, q[chain[0]]);
            float least = float.PositiveInfinity;
            for (int i = 0; i + 1 < chainCount; i++)
            {
                least = math.min(least, SpatialSplitBentSurface.DistanceToSegment(p, q[chain[i]], q[chain[i + 1]]));
            }

            return least;
        }

        /// <summary>
        /// Shares the triangles out by the surface, as <see cref="CutSpatialSplitSearch.Distribute"/> shares them by
        /// a plane: each triangle's side is the same test the search verified it with, in the owner's frame through
        /// <paramref name="geometryToOwner"/>. The output's layout and every refusal are the plane version's.
        /// <paramref name="scratch"/> is the search's (its element room is used).
        /// </summary>
        public static bool DistributeBent(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount,
            int* submeshOffsets, int* submeshCounts, int submeshCount, in float4x4 geometryToOwner, in SpatialSplitBentSurface surface,
            float margin, int convexCount, int maxConvexVertices, byte* scratch,
            uint* output, int* outSubmeshOffsets, int* outSubmeshCounts, float3* outBoundsMin, float3* outBoundsMax,
            ref CutSpatialSplitSearch.Distribution distribution)
        {
            distribution = default;
            if (vertices == null || indices == null || output == null || submeshOffsets == null || submeshCounts == null
                || outSubmeshOffsets == null || outSubmeshCounts == null || outBoundsMin == null || outBoundsMax == null || scratch == null
                || submeshCount <= 0 || indexCount <= 0 || indexCount % 3 != 0 || surface.knotCount < 2 || !(margin > 0f))
            {
                return false;
            }

            Layout(convexCount, maxConvexVertices, indexCount / 3,
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out long pointsAt, out long workAt, out _);
            float2* points = (float2*)(scratch + pointsAt);
            int* work = (int*)(scratch + workAt);

            int covered = 0;
            for (int s = 0; s < submeshCount; s++)
            {
                if (submeshOffsets[s] != covered || submeshCounts[s] < 0 || submeshCounts[s] % 3 != 0)
                {
                    return false;
                }

                covered += submeshCounts[s];
                outSubmeshCounts[s] = 0;
                outSubmeshCounts[submeshCount + s] = 0;
            }

            if (covered != indexCount)
            {
                return false;
            }

            for (int s = 0; s < submeshCount; s++)
            {
                int end = submeshOffsets[s] + submeshCounts[s];
                for (int i = submeshOffsets[s]; i < end; i += 3)
                {
                    int side = SideOfIndexedTriangle(vertices, vertexViewLength, indices, i, in geometryToOwner, in surface, margin, points, work);
                    if (side > 0) outSubmeshCounts[s] += 3;
                    else if (side < 0) outSubmeshCounts[submeshCount + s] += 3;
                    else return false;
                }
            }

            int positiveTotal = 0;
            int negativeTotal = 0;
            for (int s = 0; s < submeshCount; s++)
            {
                outSubmeshOffsets[s] = positiveTotal;
                positiveTotal += outSubmeshCounts[s];
                outSubmeshOffsets[submeshCount + s] = negativeTotal;
                negativeTotal += outSubmeshCounts[submeshCount + s];
                outBoundsMin[s] = new float3(float.PositiveInfinity);
                outBoundsMax[s] = new float3(float.NegativeInfinity);
                outBoundsMin[submeshCount + s] = new float3(float.PositiveInfinity);
                outBoundsMax[submeshCount + s] = new float3(float.NegativeInfinity);
            }

            if (positiveTotal == 0 || negativeTotal == 0 || positiveTotal + negativeTotal != indexCount)
            {
                return false;
            }

            uint positiveLo = uint.MaxValue, positiveHi = 0u, negativeLo = uint.MaxValue, negativeHi = 0u;
            for (int s = 0; s < submeshCount; s++)
            {
                int positiveAt = outSubmeshOffsets[s];
                int negativeAt = positiveTotal + outSubmeshOffsets[submeshCount + s];
                int end = submeshOffsets[s] + submeshCounts[s];
                for (int i = submeshOffsets[s]; i < end; i += 3)
                {
                    uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                    float3 p0 = vertices[i0].position, p1 = vertices[i1].position, p2 = vertices[i2].position;
                    uint low = math.min(i0, math.min(i1, i2));
                    uint high = math.max(i0, math.max(i1, i2));
                    // The same test as the first pass: the same inputs, the same answer.
                    if (SideOfIndexedTriangle(vertices, vertexViewLength, indices, i, in geometryToOwner, in surface, margin, points, work) > 0)
                    {
                        output[positiveAt] = i0; output[positiveAt + 1] = i1; output[positiveAt + 2] = i2;
                        positiveAt += 3;
                        outBoundsMin[s] = math.min(outBoundsMin[s], math.min(p0, math.min(p1, p2)));
                        outBoundsMax[s] = math.max(outBoundsMax[s], math.max(p0, math.max(p1, p2)));
                        positiveLo = math.min(positiveLo, low);
                        positiveHi = math.max(positiveHi, high);
                    }
                    else
                    {
                        output[negativeAt] = i0; output[negativeAt + 1] = i1; output[negativeAt + 2] = i2;
                        negativeAt += 3;
                        outBoundsMin[submeshCount + s] = math.min(outBoundsMin[submeshCount + s], math.min(p0, math.min(p1, p2)));
                        outBoundsMax[submeshCount + s] = math.max(outBoundsMax[submeshCount + s], math.max(p0, math.max(p1, p2)));
                        negativeLo = math.min(negativeLo, low);
                        negativeHi = math.max(negativeHi, high);
                    }
                }
            }

            distribution.positiveIndexCount = positiveTotal;
            distribution.negativeIndexCount = negativeTotal;
            distribution.positiveReferencedLo = positiveLo;
            distribution.positiveReferencedHi = positiveHi;
            distribution.negativeReferencedLo = negativeLo;
            distribution.negativeReferencedHi = negativeHi;
            return true;
        }

        private static int SideOfIndexedTriangle(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int i, in float4x4 geometryToOwner, in SpatialSplitBentSurface surface,
            float margin, float2* points, int* work)
        {
            for (int k = 0; k < 3; k++)
            {
                uint index = indices[i + k];
                if (index >= (uint)vertexViewLength) return 0;
                float2 q = surface.Project(math.transform(geometryToOwner, vertices[index].position));
                if (!math.all(math.isfinite(q))) return 0;
                points[k] = q;
            }

            return SideOfPolygon(in surface, points, 3, margin, work, out _);
        }

        private static long Align16(long bytes)
        {
            return (bytes + 15) & ~15L;
        }

        private static bool IsFinite(in float4x4 m)
        {
            return math.all(math.isfinite(m.c0)) && math.all(math.isfinite(m.c1))
                && math.all(math.isfinite(m.c2)) && math.all(math.isfinite(m.c3));
        }
    }
}
