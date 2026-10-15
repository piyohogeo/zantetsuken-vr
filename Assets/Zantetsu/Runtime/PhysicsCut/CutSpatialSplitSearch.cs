using System;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What a search for a separating plane came to (DESIGN 7.9.2, 7.9.5).</summary>
    public enum SpatialSplitSearchOutcome
    {
        /// <summary>A plane was found and every condition holds for it.</summary>
        Found = 0,

        /// <summary>The finite search found no plane for this input. Not a proof that none exists.</summary>
        NotFound = 1,

        /// <summary>A position, or something made from one, was not finite: the input is not classified at all.</summary>
        NotFinite = 2,

        /// <summary>The input is larger than the search's limits: nothing was searched.</summary>
        OverTheLimits = 3,

        /// <summary>The input does not hold together (counts, pointers, a matrix).</summary>
        InvalidInput = 4,
    }

    /// <summary>
    /// The finite limits of one search. Every value is the caller's; the profile holds the provisional ones
    /// (DESIGN 7.9.2: 探索は有限の試行・作業量で打ち切る).
    /// </summary>
    [Serializable]
    public struct SpatialSplitSearchLimits
    {
        /// <summary>The most convexes an owner may have to be searched at all.</summary>
        public int maxConvexes;

        /// <summary>The most directions taken from differences of two convexes' centres, beside the three axes.</summary>
        public int maxCentreDirections;

        /// <summary>The most gaps tested against the triangles, over all directions.</summary>
        public int maxPlaneTests;

        /// <summary>The most triangles a geometry may have to be searched at all.</summary>
        public int maxTriangles;

        /// <summary>The mesh-led search: the most directions taken from the spread of the triangles, beside the others.</summary>
        public int maxMeshDirections;

        /// <summary>The mesh-led search: into how many steps a direction's extent is divided to find where no triangle lies.</summary>
        public int meshBins;

        /// <summary>The provisional values of the mesh-led search's two limits, where a caller gives none.</summary>
        public const int DefaultMeshDirections = 3, DefaultMeshBins = 4096;

        public SpatialSplitSearchLimits(int maxConvexes, int maxCentreDirections, int maxPlaneTests, int maxTriangles)
            : this(maxConvexes, maxCentreDirections, maxPlaneTests, maxTriangles, DefaultMeshDirections, DefaultMeshBins)
        {
        }

        public SpatialSplitSearchLimits(int maxConvexes, int maxCentreDirections, int maxPlaneTests, int maxTriangles, int maxMeshDirections, int meshBins)
        {
            this.maxConvexes = maxConvexes;
            this.maxCentreDirections = maxCentreDirections;
            this.maxPlaneTests = maxPlaneTests;
            this.maxTriangles = maxTriangles;
            this.maxMeshDirections = maxMeshDirections;
            this.meshBins = meshBins;
        }

        public bool IsValid => maxConvexes >= 2 && maxCentreDirections >= 0 && maxPlaneTests >= 1 && maxTriangles >= 2
            && maxMeshDirections >= 0 && maxMeshDirections <= 3 && meshBins >= 16 && meshBins <= 65536;
    }

    /// <summary>What one search did, beside its outcome: for the record, and for a test that reads it.</summary>
    public struct SpatialSplitSearchResult
    {
        public SpatialSplitSearchOutcome outcome;

        /// <summary>The plane in the owner's frame, unit normal: s(x) = dot(xyz, x) + w. Positive is s above the margin.</summary>
        public float4 planeOwner;

        /// <summary>The same plane in the geometry's own coordinates (not normalised: same s for the same point).</summary>
        public float4 planeGeometry;

        /// <summary>The same plane in the shape's numerical local frame (not normalised: same s for the same point).</summary>
        public float4 planeShape;

        /// <summary>The margin every vertex of every triangle and convex keeps from the plane.</summary>
        public float margin;

        /// <summary>The clear width found between the two sides, along the normal.</summary>
        public float gap;

        public int directionsTried, planesTested;
        public long triangleVisits;
        public int positiveTriangles, negativeTriangles, positiveConvexes, negativeConvexes;

        /// <summary>
        /// The mesh-led search (<see cref="CutSpatialSplitSearch.FindMeshPlane"/>): whether the plane is its own, how
        /// many convexes are not wholly beyond the margin on one side of it (to be cut, or judged by the cut's own
        /// scan), what it tried, and how many planes that held for the triangles it left for the convexes -- because
        /// the smaller side had fewer triangles than the minimum a convex cut asks, or because nothing would be
        /// left of the convexes on one side.
        /// </summary>
        public bool meshLed;
        public int crossingConvexes;
        public int meshDirectionsTried, meshPlanesTested, refusedForTriangleMinimum, refusedForNoConvexOnASide;

        /// <summary>
        /// The bent partition (<see cref="CutSpatialSplitBentSearch.FindBentSurface"/>): whether the search went on
        /// to it, the surface it found (the sides are then the surface's, and the plane fields hold only its height
        /// axis for the record), its pieces, and what it spent: frames and seeds tried, closures that read the
        /// triangles, iterations, and elements verified over their whole extent.
        /// </summary>
        public bool bent;
        public SpatialSplitBentSurface bentSurface;
        public int bentPieces, bentFramesTried, bentSeedsTried, bentCandidates, bentIterations;
        public long bentElementsVerified;
    }

    /// <summary>
    /// The numerical part of the additional split of one owner (DESIGN 7.9.2, first version: no convex is cut): the
    /// search for one plane, fixed in the owner's frame, that crosses neither a convex nor a triangle and leaves both
    /// on each side; and the sharing out of the geometry's triangles between the two sides.
    /// <para>
    /// **The search.** Directions are the owner's three axes and then, up to a limit, the unit differences of two
    /// convexes' centres. Along a direction each convex is an interval; a gap between the sorted intervals wider than
    /// twice the margin is a candidate, narrowed in one pass by every triangle that reaches into it and then verified
    /// in one more: every triangle and every convex must lie wholly beyond the margin on one side, and each side must
    /// have at least one of each. The first plane that holds is taken; there is no best plane, no GJK and no
    /// all-pairs search, and the number of gaps tested is limited. A vertex within the margin, a triangle or convex
    /// that spans the plane and a gap bridged by the mesh alone or by a convex alone all leave the plane refused.
    /// </para>
    /// <para>
    /// **The sharing out.** Triangles keep their corner order, their submesh and their order within it; a triangle
    /// of no area goes with its vertices like any other; none is dropped and none is written twice. The positive
    /// side's indices are written first and then the negative side's, each side's submeshes in the parent's order
    /// (one descriptor a parent submesh, as a cut's sides have, an empty one with an empty box), with the box of
    /// each and the lowest and highest vertex each side refers to. Only the vertices the geometry's own triangles
    /// name are read.
    /// </para>
    /// <para>
    /// Everything is read through the pointers given and nothing is kept: no Unity object, no storage call and no
    /// allocation, so that it runs on a worker (DESIGN 4.3) with the index range leased and the shape held by the
    /// caller. Managed code; nothing here needs a first call on the main thread.
    /// </para>
    /// </summary>
    public static unsafe class CutSpatialSplitSearch
    {
        /// <summary>Scratch a search needs for <paramref name="convexCount"/> convexes, in bytes (16-byte aligned parts).</summary>
        public static int ScratchBytes(int convexCount)
        {
            // lo, hi (float), order (int), centres (float3 as 3 floats)
            return checked(convexCount * ((2 * sizeof(float)) + sizeof(int) + (3 * sizeof(float))));
        }

        /// <summary>Scratch <see cref="FindMeshPlane"/> needs, in bytes: the convexes' intervals and the steps' counts.</summary>
        public static int MeshScratchBytes(int convexCount, in SpatialSplitSearchLimits limits)
        {
            return checked(ScratchBytes(convexCount) + ((Math.Max(16, limits.meshBins) + 2) * sizeof(int)));
        }

        /// <summary>
        /// The mesh-led search (DESIGN 7.9.2, the convex-cut extension): a plane, fixed in the owner's frame, that
        /// leaves every triangle wholly beyond the margin on one side with triangles on both -- whatever the convexes
        /// do. Directions are the owner's three axes, the unit differences of two convexes' centres (up to the same
        /// limit as <see cref="FindPlane"/>) and up to <see cref="SpatialSplitSearchLimits.maxMeshDirections"/>
        /// principal directions of the triangles' centres. Along a direction the triangles' intervals are counted
        /// into a limited number of steps; a run of steps no triangle reaches is a candidate, narrowed and verified
        /// against every triangle as a gap of the convexes is. No island of the mesh is listed and no plane is
        /// looked for beyond these.
        /// <para>
        /// A plane that holds for the triangles is taken when either no convex comes within the margin of it and
        /// both sides have one (nothing is to be cut: the result is as <see cref="FindPlane"/>'s), or a convex does
        /// and the side with fewer triangles has <paramref name="minCutSideTriangles"/> or more
        /// (<see cref="SpatialSplitSearchResult.crossingConvexes"/> is then above zero and the caller cuts them).
        /// Otherwise the next candidate is looked at, within <see cref="SpatialSplitSearchLimits.maxPlaneTests"/>.
        /// </para>
        /// <paramref name="scratch"/> is at least <see cref="MeshScratchBytes"/> bytes.
        /// </summary>
        public static SpatialSplitSearchOutcome FindMeshPlane(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount, in float4x4 geometryToOwner,
            ConvexBrepBank* banks, ConvexBrepRange* convexes, int convexCount, in float4x4 shapeToOwner,
            float margin, int minCutSideTriangles, in SpatialSplitSearchLimits limits, byte* scratch, ref SpatialSplitSearchResult result)
        {
            // What the first search counted stays in the record; the plane and the sides are this search's.
            SpatialSplitSearchResult first = result;
            result = default;
            result.directionsTried = first.directionsTried;
            result.planesTested = first.planesTested;
            result.triangleVisits = first.triangleVisits;
            // The bent partition's record stays too (2026-10-15: it is searched before this one; a record only).
            result.bent = first.bent;
            result.bentFramesTried = first.bentFramesTried;
            result.bentSeedsTried = first.bentSeedsTried;
            result.bentCandidates = first.bentCandidates;
            result.bentIterations = first.bentIterations;
            result.bentElementsVerified = first.bentElementsVerified;
            result.margin = margin;
            result.meshLed = true;
            if (vertices == null || indices == null || banks == null || convexes == null || scratch == null
                || vertexViewLength <= 0 || indexCount <= 0 || indexCount % 3 != 0 || convexCount <= 0
                || !(margin > 0f) || !math.isfinite(margin) || !limits.IsValid || minCutSideTriangles < 1
                || !IsFinite(geometryToOwner) || !IsFinite(shapeToOwner))
            {
                return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
            }

            int triangles = indexCount / 3;
            if (triangles < 2)
            {
                return result.outcome = SpatialSplitSearchOutcome.NotFound;
            }

            if (convexCount > limits.maxConvexes || triangles > limits.maxTriangles)
            {
                return result.outcome = SpatialSplitSearchOutcome.OverTheLimits;
            }

            float* lo = (float*)scratch;
            float* hi = lo + convexCount;
            int* order = (int*)(hi + convexCount);
            float* centres = (float*)(order + convexCount);
            int bins = Math.Max(16, limits.meshBins);
            int* steps = (int*)(scratch + ScratchBytes(convexCount));

            // The convexes' centres in the owner's frame, as FindPlane takes them.
            for (int c = 0; c < convexCount; c++)
            {
                ConvexBrepRange range = convexes[c];
                if (range.vertexCount <= 0)
                {
                    return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
                }

                float3* points = banks[c].vertices + range.vertexBase;
                float3 low = new float3(float.PositiveInfinity);
                float3 high = new float3(float.NegativeInfinity);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 p = math.transform(shapeToOwner, points[v]);
                    if (!math.all(math.isfinite(p)))
                    {
                        return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                    }

                    low = math.min(low, p);
                    high = math.max(high, p);
                }

                float3 centre = 0.5f * (low + high);
                centres[(3 * c) + 0] = centre.x;
                centres[(3 * c) + 1] = centre.y;
                centres[(3 * c) + 2] = centre.z;
            }

            // The triangles' centres in the owner's frame: their mean and their spread, in one pass.
            double3 sum = default;
            double3 squares = default;   // xx, yy, zz
            double3 products = default;  // xy, xz, yz
            for (int i = 0; i < indexCount; i += 3)
            {
                uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                if (i0 >= (uint)vertexViewLength || i1 >= (uint)vertexViewLength || i2 >= (uint)vertexViewLength)
                {
                    return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
                }

                float3 p0 = vertices[i0].position, p1 = vertices[i1].position, p2 = vertices[i2].position;
                if (!math.all(math.isfinite(p0)) || !math.all(math.isfinite(p1)) || !math.all(math.isfinite(p2)))
                {
                    return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                }

                double3 m = math.transform(geometryToOwner, (p0 + p1 + p2) / 3f);
                sum += m;
                squares += m * m;
                products += new double3(m.x * m.y, m.x * m.z, m.y * m.z);
            }

            result.triangleVisits += triangles;
            double3 mean = sum / triangles;
            double3 variance = (squares / triangles) - (mean * mean);
            double3 covariance = (products / triangles) - new double3(mean.x * mean.y, mean.x * mean.z, mean.y * mean.z);

            const int Axes = 3;
            int centreLimit = Math.Min(limits.maxCentreDirections, 64);
            int takenLimit = Axes + centreLimit + 3;
            float3* taken = stackalloc float3[takenLimit];
            int takenCount = 0;
            taken[takenCount++] = new float3(1f, 0f, 0f);
            taken[takenCount++] = new float3(0f, 1f, 0f);
            taken[takenCount++] = new float3(0f, 0f, 1f);
            for (int i = 0; i < convexCount && takenCount < Axes + centreLimit; i++)
            {
                float3 a = new float3(centres[3 * i], centres[(3 * i) + 1], centres[(3 * i) + 2]);
                for (int j = i + 1; j < convexCount && takenCount < Axes + centreLimit; j++)
                {
                    float3 d = new float3(centres[3 * j], centres[(3 * j) + 1], centres[(3 * j) + 2]) - a;
                    float length = math.length(d);
                    if (length > 4f * margin && !IsNearlyParallelToOne(taken, takenCount, d / length))
                    {
                        taken[takenCount++] = d / length;
                    }
                }
            }

            if (limits.maxMeshDirections > 0 && math.all(math.isfinite((float3)variance)) && math.all(math.isfinite((float3)covariance)))
            {
                float3x3 principal = PrincipalDirections(variance, covariance);
                for (int k = 0; k < limits.maxMeshDirections; k++)
                {
                    float3 d = principal[k];
                    if (math.all(math.isfinite(d)) && math.lengthsq(d) > 0.5f && !IsNearlyParallelToOne(taken, takenCount, d))
                    {
                        taken[takenCount++] = d;
                    }
                }
            }

            float3x3 geometryLinear = new float3x3(geometryToOwner.c0.xyz, geometryToOwner.c1.xyz, geometryToOwner.c2.xyz);
            float3x3 shapeLinear = new float3x3(shapeToOwner.c0.xyz, shapeToOwner.c1.xyz, shapeToOwner.c2.xyz);
            for (int d = 0; d < takenCount; d++)
            {
                float3 n = taken[d];
                result.meshDirectionsTried++;
                float3 nShape = math.mul(math.transpose(shapeLinear), n);
                float shapeOffset = math.dot(n, shapeToOwner.c3.xyz);
                float3 nGeometry = math.mul(math.transpose(geometryLinear), n);
                float geometryOffset = math.dot(n, geometryToOwner.c3.xyz);

                // The triangles' whole extent along the direction.
                float lowAll = float.PositiveInfinity;
                float highAll = float.NegativeInfinity;
                for (int i = 0; i < indexCount; i++)
                {
                    float s = math.dot(nGeometry, vertices[indices[i]].position) + geometryOffset;
                    if (!math.isfinite(s))
                    {
                        return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                    }

                    lowAll = math.min(lowAll, s);
                    highAll = math.max(highAll, s);
                }

                result.triangleVisits += triangles;
                float extent = highAll - lowAll;
                if (!(extent > 4f * margin))
                {
                    continue;
                }

                // Steps no narrower than the margin; each triangle counts into every step its interval reaches
                // (a count up where it begins and down after it ends, summed along the direction).
                float step = math.max(margin, extent / bins);
                int used = math.min(bins, (int)math.ceil(extent / step));
                for (int b = 0; b <= used + 1; b++)
                {
                    steps[b] = 0;
                }

                for (int i = 0; i < indexCount; i += 3)
                {
                    float s0 = math.dot(nGeometry, vertices[indices[i]].position) + geometryOffset;
                    float s1 = math.dot(nGeometry, vertices[indices[i + 1]].position) + geometryOffset;
                    float s2 = math.dot(nGeometry, vertices[indices[i + 2]].position) + geometryOffset;
                    int from = math.clamp((int)((math.min(s0, math.min(s1, s2)) - lowAll) / step), 0, used - 1);
                    int to = math.clamp((int)((math.max(s0, math.max(s1, s2)) - lowAll) / step), 0, used - 1);
                    steps[from]++;
                    steps[to + 1]--;
                }

                result.triangleVisits += triangles;

                // The convexes' intervals along the direction, for what a plane found leaves of them.
                for (int c = 0; c < convexCount; c++)
                {
                    ConvexBrepRange range = convexes[c];
                    float3* points = banks[c].vertices + range.vertexBase;
                    float low = float.PositiveInfinity;
                    float high = float.NegativeInfinity;
                    for (int v = 0; v < range.vertexCount; v++)
                    {
                        float s = math.dot(nShape, points[v]) + shapeOffset;
                        if (!math.isfinite(s))
                        {
                            return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                        }

                        low = math.min(low, s);
                        high = math.max(high, s);
                    }

                    lo[c] = low;
                    hi[c] = high;
                }

                int covering = 0;
                int runBegin = -1;
                for (int b = 0; b <= used; b++)
                {
                    covering += b < used ? steps[b] : 1;   // past the last step: a run ends
                    if (covering == 0)
                    {
                        if (runBegin < 0) runBegin = b;
                        continue;
                    }

                    if (runBegin < 0)
                    {
                        continue;
                    }

                    // Steps runBegin .. b-1 hold no triangle. One step more on either side: the gap's own ends lie
                    // inside the steps beside the run, and the narrowing finds them.
                    float gapFrom = lowAll + ((runBegin - 1) * step);
                    float gapTo = lowAll + ((b + 1) * step);
                    runBegin = -1;
                    if (result.meshPlanesTested >= limits.maxPlaneTests)
                    {
                        return result.outcome = SpatialSplitSearchOutcome.NotFound;
                    }

                    result.meshPlanesTested++;
                    SpatialSplitSearchOutcome tested = TestGap(
                        vertices, vertexViewLength, indices, indexCount, nGeometry, geometryOffset, gapFrom, gapTo, margin,
                        ref result, out float offset, out float clear);
                    if (tested == SpatialSplitSearchOutcome.NotFinite || tested == SpatialSplitSearchOutcome.InvalidInput)
                    {
                        return result.outcome = tested;
                    }

                    if (tested != SpatialSplitSearchOutcome.Found)
                    {
                        continue;
                    }

                    int positive = 0;
                    int negative = 0;
                    int crossing = 0;
                    bool reachesPositive = false;
                    bool reachesNegative = false;
                    for (int c = 0; c < convexCount; c++)
                    {
                        if (lo[c] - offset > margin) positive++;
                        else if (hi[c] - offset < -margin) negative++;
                        else crossing++;
                        reachesPositive |= hi[c] - offset > margin;
                        reachesNegative |= lo[c] - offset < -margin;
                    }

                    if (!reachesPositive || !reachesNegative)
                    {
                        // Triangles on a side no convex reaches: nothing of the physics would stand for them.
                        result.refusedForNoConvexOnASide++;
                        continue;
                    }

                    if (crossing > 0 && math.min(result.positiveTriangles, result.negativeTriangles) < minCutSideTriangles)
                    {
                        result.refusedForTriangleMinimum++;
                        continue;
                    }

                    if (crossing == 0 && (positive == 0 || negative == 0))
                    {
                        result.refusedForNoConvexOnASide++;
                        continue;
                    }

                    result.planeOwner = new float4(n, -offset);
                    result.planeGeometry = new float4(nGeometry, geometryOffset - offset);
                    result.planeShape = new float4(nShape, shapeOffset - offset);
                    result.gap = clear;
                    result.positiveConvexes = positive;
                    result.negativeConvexes = negative;
                    result.crossingConvexes = crossing;
                    return result.outcome = SpatialSplitSearchOutcome.Found;
                }
            }

            result.positiveTriangles = 0;
            result.negativeTriangles = 0;
            return result.outcome = SpatialSplitSearchOutcome.NotFound;
        }

        private static bool IsNearlyParallelToOne(float3* taken, int takenCount, float3 direction)
        {
            for (int k = 0; k < takenCount; k++)
            {
                if (math.abs(math.dot(taken[k], direction)) > 0.9995f)
                {
                    return true;
                }
            }

            return false;
        }

        // The unit eigenvectors of a symmetric 3x3 matrix given by its diagonal and its three products (xy, xz, yz),
        // widest spread first: a fixed number of Jacobi sweeps, which is enough for a direction to try.
        private static float3x3 PrincipalDirections(double3 diagonal, double3 off)
        {
            double a00 = diagonal.x, a11 = diagonal.y, a22 = diagonal.z, a01 = off.x, a02 = off.y, a12 = off.z;
            double3 v0 = new double3(1, 0, 0), v1 = new double3(0, 1, 0), v2 = new double3(0, 0, 1);
            for (int sweep = 0; sweep < 8; sweep++)
            {
                Rotate(ref a00, ref a11, ref a01, ref a02, ref a12, ref v0, ref v1);
                Rotate(ref a00, ref a22, ref a02, ref a01, ref a12, ref v0, ref v2);
                Rotate(ref a11, ref a22, ref a12, ref a01, ref a02, ref v1, ref v2);
            }

            // v0, v1, v2 are the columns the rotations were gathered in -- the eigenvectors: taken by their eigenvalues.
            double3 e0 = v0, e1 = v1, e2 = v2;
            if (a00 < a11) { Swap(ref a00, ref a11); Swap(ref e0, ref e1); }
            if (a00 < a22) { Swap(ref a00, ref a22); Swap(ref e0, ref e2); }
            if (a11 < a22) { Swap(ref a11, ref a22); Swap(ref e1, ref e2); }
            return new float3x3((float3)math.normalizesafe(e0), (float3)math.normalizesafe(e1), (float3)math.normalizesafe(e2));
        }

        // One Jacobi rotation in the plane of two axes p and q: app, aqq their diagonal entries, apq the entry to
        // bring to zero, and the two entries that couple p and q with the third axis r.
        private static void Rotate(
            ref double app, ref double aqq, ref double apq, ref double apr, ref double aqr, ref double3 vp, ref double3 vq)
        {
            if (math.abs(apq) < 1e-300)
            {
                return;
            }

            double theta = (aqq - app) / (2.0 * apq);
            double t = (theta >= 0.0 ? 1.0 : -1.0) / (math.abs(theta) + math.sqrt((theta * theta) + 1.0));
            double c = 1.0 / math.sqrt((t * t) + 1.0);
            double s = t * c;
            app -= t * apq;
            aqq += t * apq;
            apq = 0.0;
            double pr = apr, qr = aqr;
            apr = (c * pr) - (s * qr);
            aqr = (s * pr) + (c * qr);
            double3 p = vp, q = vq;
            vp = (c * p) - (s * q);
            vq = (s * p) + (c * q);
        }

        private static void Swap<T>(ref T a, ref T b)
        {
            T t = a;
            a = b;
            b = t;
        }

        /// <summary>
        /// Searches for the plane. <paramref name="scratch"/> is at least <see cref="ScratchBytes"/> bytes.
        /// <paramref name="geometryToOwner"/> and <paramref name="shapeToOwner"/> carry the triangles' and the
        /// convexes' coordinates to the owner's frame, in which the plane is fixed.
        /// </summary>
        public static SpatialSplitSearchOutcome FindPlane(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount, in float4x4 geometryToOwner,
            ConvexBrepBank* banks, ConvexBrepRange* convexes, int convexCount, in float4x4 shapeToOwner,
            float margin, in SpatialSplitSearchLimits limits, byte* scratch, ref SpatialSplitSearchResult result)
        {
            result = default;
            result.margin = margin;
            if (vertices == null || indices == null || banks == null || convexes == null || scratch == null
                || vertexViewLength <= 0 || indexCount <= 0 || indexCount % 3 != 0 || convexCount <= 0
                || !(margin > 0f) || !math.isfinite(margin) || !limits.IsValid
                || !IsFinite(geometryToOwner) || !IsFinite(shapeToOwner))
            {
                return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
            }

            int triangles = indexCount / 3;
            if (convexCount < 2 || triangles < 2)
            {
                // One convex, or one triangle, cannot be on both sides of anything.
                return result.outcome = SpatialSplitSearchOutcome.NotFound;
            }

            if (convexCount > limits.maxConvexes || triangles > limits.maxTriangles)
            {
                return result.outcome = SpatialSplitSearchOutcome.OverTheLimits;
            }

            float* lo = (float*)scratch;
            float* hi = lo + convexCount;
            int* order = (int*)(hi + convexCount);
            float* centres = (float*)(order + convexCount);

            // The convexes' centres in the owner's frame (the middle of each one's box there).
            for (int c = 0; c < convexCount; c++)
            {
                ConvexBrepRange range = convexes[c];
                if (range.vertexCount <= 0)
                {
                    return result.outcome = SpatialSplitSearchOutcome.InvalidInput;
                }

                float3* points = banks[c].vertices + range.vertexBase;
                float3 low = new float3(float.PositiveInfinity);
                float3 high = new float3(float.NegativeInfinity);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    // Asked of each value itself: a minimum or a maximum lets a NaN pass unseen.
                    float3 p = math.transform(shapeToOwner, points[v]);
                    if (!math.all(math.isfinite(p)))
                    {
                        return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                    }

                    low = math.min(low, p);
                    high = math.max(high, p);
                }

                float3 centre = 0.5f * (low + high);
                if (!math.all(math.isfinite(centre)))
                {
                    return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                }

                centres[(3 * c) + 0] = centre.x;
                centres[(3 * c) + 1] = centre.y;
                centres[(3 * c) + 2] = centre.z;
            }

            // The directions: the three axes, then differences of centres not (nearly) parallel to one already taken.
            const int Axes = 3;
            int directionLimit = Axes + limits.maxCentreDirections;
            float3* taken = stackalloc float3[Math.Min(directionLimit, Axes + 64)];
            int takenLimit = Math.Min(directionLimit, Axes + 64);
            int takenCount = 0;
            taken[takenCount++] = new float3(1f, 0f, 0f);
            taken[takenCount++] = new float3(0f, 1f, 0f);
            taken[takenCount++] = new float3(0f, 0f, 1f);
            for (int i = 0; i < convexCount && takenCount < takenLimit; i++)
            {
                float3 a = new float3(centres[3 * i], centres[(3 * i) + 1], centres[(3 * i) + 2]);
                for (int j = i + 1; j < convexCount && takenCount < takenLimit; j++)
                {
                    float3 d = new float3(centres[3 * j], centres[(3 * j) + 1], centres[(3 * j) + 2]) - a;
                    float length = math.length(d);
                    if (!(length > 4f * margin))
                    {
                        continue;
                    }

                    d /= length;
                    bool parallel = false;
                    for (int k = 0; k < takenCount; k++)
                    {
                        if (math.abs(math.dot(taken[k], d)) > 0.9995f)
                        {
                            parallel = true;
                            break;
                        }
                    }

                    if (!parallel)
                    {
                        taken[takenCount++] = d;
                    }
                }
            }

            float3x3 geometryLinear = new float3x3(geometryToOwner.c0.xyz, geometryToOwner.c1.xyz, geometryToOwner.c2.xyz);
            float3x3 shapeLinear = new float3x3(shapeToOwner.c0.xyz, shapeToOwner.c1.xyz, shapeToOwner.c2.xyz);
            for (int d = 0; d < takenCount; d++)
            {
                float3 n = taken[d];
                result.directionsTried++;

                // s(x_owner) = dot(n, x_owner). In the convexes' own coordinates and in the geometry's own.
                float3 nShape = math.mul(math.transpose(shapeLinear), n);
                float shapeOffset = math.dot(n, shapeToOwner.c3.xyz);
                float3 nGeometry = math.mul(math.transpose(geometryLinear), n);
                float geometryOffset = math.dot(n, geometryToOwner.c3.xyz);

                for (int c = 0; c < convexCount; c++)
                {
                    ConvexBrepRange range = convexes[c];
                    float3* points = banks[c].vertices + range.vertexBase;
                    float low = float.PositiveInfinity;
                    float high = float.NegativeInfinity;
                    for (int v = 0; v < range.vertexCount; v++)
                    {
                        float s = math.dot(nShape, points[v]) + shapeOffset;
                        if (!math.isfinite(s))
                        {
                            return result.outcome = SpatialSplitSearchOutcome.NotFinite;
                        }

                        low = math.min(low, s);
                        high = math.max(high, s);
                    }

                    lo[c] = low;
                    hi[c] = high;
                    order[c] = c;
                }

                // By the low end (an insertion sort: the count is limited and small).
                for (int i = 1; i < convexCount; i++)
                {
                    int moving = order[i];
                    int at = i - 1;
                    while (at >= 0 && lo[order[at]] > lo[moving])
                    {
                        order[at + 1] = order[at];
                        at--;
                    }

                    order[at + 1] = moving;
                }

                float reached = hi[order[0]];
                for (int k = 1; k < convexCount; k++)
                {
                    float next = lo[order[k]];
                    if (next - reached > 2f * margin)
                    {
                        if (result.planesTested >= limits.maxPlaneTests)
                        {
                            return result.outcome = SpatialSplitSearchOutcome.NotFound;
                        }

                        result.planesTested++;
                        SpatialSplitSearchOutcome tested = TestGap(
                            vertices, vertexViewLength, indices, indexCount, nGeometry, geometryOffset, reached, next, margin,
                            ref result, out float offset, out float clear);
                        if (tested == SpatialSplitSearchOutcome.NotFinite || tested == SpatialSplitSearchOutcome.InvalidInput)
                        {
                            return result.outcome = tested;
                        }

                        if (tested == SpatialSplitSearchOutcome.Found)
                        {
                            // The convexes by their intervals, with the same margin: none may reach the plane.
                            int positive = 0;
                            int negative = 0;
                            bool whole = true;
                            for (int c = 0; c < convexCount; c++)
                            {
                                if (lo[c] - offset > margin) positive++;
                                else if (hi[c] - offset < -margin) negative++;
                                else whole = false;
                            }

                            if (whole && positive > 0 && negative > 0)
                            {
                                result.planeOwner = new float4(n, -offset);
                                result.planeGeometry = new float4(nGeometry, geometryOffset - offset);
                                result.planeShape = new float4(nShape, shapeOffset - offset);
                                result.gap = clear;
                                result.positiveConvexes = positive;
                                result.negativeConvexes = negative;
                                return result.outcome = SpatialSplitSearchOutcome.Found;
                            }
                        }
                    }

                    reached = math.max(reached, hi[order[k]]);
                }
            }

            return result.outcome = SpatialSplitSearchOutcome.NotFound;
        }

        // One gap of the convexes, (from, to) along the direction: narrowed by every triangle that reaches into it --
        // a triangle whose middle lies below the gap's middle raises the low end to its own high end, another lowers
        // the high end -- and then every triangle is verified against the plane in the middle of what is left.
        private static SpatialSplitSearchOutcome TestGap(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount, float3 n, float nOffset,
            float from, float to, float margin, ref SpatialSplitSearchResult result, out float offset, out float clear)
        {
            offset = 0f;
            clear = 0f;
            float middle = 0.5f * (from + to);
            float a = from;
            float b = to;
            for (int i = 0; i < indexCount; i += 3)
            {
                uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                if (i0 >= (uint)vertexViewLength || i1 >= (uint)vertexViewLength || i2 >= (uint)vertexViewLength)
                {
                    return SpatialSplitSearchOutcome.InvalidInput;
                }

                float s0 = math.dot(n, vertices[i0].position) + nOffset;
                float s1 = math.dot(n, vertices[i1].position) + nOffset;
                float s2 = math.dot(n, vertices[i2].position) + nOffset;
                if (!math.isfinite(s0) || !math.isfinite(s1) || !math.isfinite(s2))
                {
                    // Asked of each value itself: a minimum or a maximum lets a NaN pass unseen.
                    return SpatialSplitSearchOutcome.NotFinite;
                }

                float low = math.min(s0, math.min(s1, s2));
                float high = math.max(s0, math.max(s1, s2));

                if (high <= a || low >= b)
                {
                    continue;
                }

                if (0.5f * (low + high) < middle) a = math.max(a, high);
                else b = math.min(b, low);
                if (!(b - a > 2f * margin))
                {
                    result.triangleVisits += (i / 3) + 1;
                    return SpatialSplitSearchOutcome.NotFound;
                }
            }

            result.triangleVisits += indexCount / 3;
            offset = 0.5f * (a + b);
            clear = b - a;
            int positive = 0;
            int negative = 0;
            for (int i = 0; i < indexCount; i += 3)
            {
                float s0 = math.dot(n, vertices[indices[i]].position) + nOffset - offset;
                float s1 = math.dot(n, vertices[indices[i + 1]].position) + nOffset - offset;
                float s2 = math.dot(n, vertices[indices[i + 2]].position) + nOffset - offset;
                if (s0 > margin && s1 > margin && s2 > margin) positive++;
                else if (s0 < -margin && s1 < -margin && s2 < -margin) negative++;
                else
                {
                    result.triangleVisits += (i / 3) + 1;
                    return SpatialSplitSearchOutcome.NotFound;
                }
            }

            result.triangleVisits += indexCount / 3;
            if (positive == 0 || negative == 0)
            {
                // The mesh is all on one side: the convexes would be split and the geometry not.
                return SpatialSplitSearchOutcome.NotFound;
            }

            result.positiveTriangles = positive;
            result.negativeTriangles = negative;
            return SpatialSplitSearchOutcome.Found;
        }

        /// <summary>What <see cref="Distribute"/> wrote, beside the indices themselves.</summary>
        public struct Distribution
        {
            public int positiveIndexCount, negativeIndexCount;
            public uint positiveReferencedLo, positiveReferencedHi, negativeReferencedLo, negativeReferencedHi;
        }

        /// <summary>
        /// Shares the triangles out by <paramref name="planeGeometry"/> (in the geometry's own coordinates, as
        /// <see cref="SpatialSplitSearchResult.planeGeometry"/>): the positive side's indices into
        /// <paramref name="output"/> first and the negative side's after them, <paramref name="indexCount"/> in all;
        /// one submesh descriptor a parent submesh for each side in <paramref name="outSubmeshes"/> (the positive
        /// side's <paramref name="submeshCount"/>, then the negative side's, offsets relative to the side's own
        /// start) with its box in <paramref name="outBoundsMin"/> / <paramref name="outBoundsMax"/>.
        /// False, with the output not to be used, when a triangle is not wholly beyond the margin on one side, when
        /// the submeshes do not cover the indices once and in order, or when a side comes out empty.
        /// </summary>
        public static bool Distribute(
            VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount,
            int* submeshOffsets, int* submeshCounts, int submeshCount, float4 planeGeometry, float margin,
            uint* output, int* outSubmeshOffsets, int* outSubmeshCounts, float3* outBoundsMin, float3* outBoundsMax,
            ref Distribution distribution)
        {
            distribution = default;
            if (vertices == null || indices == null || output == null || submeshOffsets == null || submeshCounts == null
                || outSubmeshOffsets == null || outSubmeshCounts == null || outBoundsMin == null || outBoundsMax == null
                || submeshCount <= 0 || indexCount <= 0 || indexCount % 3 != 0)
            {
                return false;
            }

            // First the counts: how many indices of each submesh go to each side.
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

            float3 n = planeGeometry.xyz;
            float w = planeGeometry.w;
            for (int s = 0; s < submeshCount; s++)
            {
                int end = submeshOffsets[s] + submeshCounts[s];
                for (int i = submeshOffsets[s]; i < end; i += 3)
                {
                    uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                    if (i0 >= (uint)vertexViewLength || i1 >= (uint)vertexViewLength || i2 >= (uint)vertexViewLength)
                    {
                        return false;
                    }

                    float s0 = math.dot(n, vertices[i0].position) + w;
                    float s1 = math.dot(n, vertices[i1].position) + w;
                    float s2 = math.dot(n, vertices[i2].position) + w;
                    if (s0 > margin && s1 > margin && s2 > margin) outSubmeshCounts[s] += 3;
                    else if (s0 < -margin && s1 < -margin && s2 < -margin) outSubmeshCounts[submeshCount + s] += 3;
                    else return false;
                }
            }

            // Then where each side's submeshes begin: relative to the side's own start for the descriptors, and in
            // the output (positive side first) for the writing.
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
                    if (math.dot(n, p0) + w > 0f)
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

        private static bool IsFinite(in float4x4 m)
        {
            return math.all(math.isfinite(m.c0)) && math.all(math.isfinite(m.c1))
                && math.all(math.isfinite(m.c2)) && math.all(math.isfinite(m.c3));
        }
    }
}
