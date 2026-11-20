using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>Why a trim candidate -- one convex, one direction, sticking out by the threshold or more -- was left alone.</summary>
    public enum ConvexTrimRefusal
    {
        None = 0,

        /// <summary>The whole convex lies outside the plane: nothing of it would be kept on the mesh's side.</summary>
        NothingKept,

        /// <summary>The clip gave no closed convex on the mesh's side (a cut polygon of fewer than three points, a walk that did not close, fewer than four vertices).</summary>
        Degenerate,

        /// <summary>What would be kept has less volume than the settings ask for.</summary>
        VolumeShort,

        /// <summary>What would be kept has more vertices than the convex vertex limit; no reduction is made for a trim.</summary>
        VertexLimit,

        /// <summary>The clip ran out of the room the attempt reserved (a capacity bound that did not hold).</summary>
        Capacity,

        Count,
    }

    /// <summary>One convex of a trim attempt: what it was, what became of it, and the candidates left on it.</summary>
    internal unsafe struct ConvexTrimConvex
    {
        /// <summary>The convex as the trims left it, in the workspace: meaningful once <see cref="copied"/>.</summary>
        public BrepBuffer current;
        public byte copied;

        /// <summary>Trims adopted for this convex.</summary>
        public int trims;

        public int verticesBefore, verticesAfter;

        /// <summary>Volumes, in the shape's own units cubed; the one before is worked out only where a clip was tried.</summary>
        public double volumeBefore, volumeAfter;

        /// <summary>The largest of the 26 distances beyond the mesh's support planes, metres (negative: inside all of them).</summary>
        public float overhangBefore, overhangAfter;

        /// <summary>Per direction: the convex's own largest projection, in the shape's frame (the plane's normal there).</summary>
        public fixed float projection[ConvexTrimSearch.Directions];

        /// <summary>Per direction: why the candidate was left alone on the convex as it is now (<see cref="ConvexTrimRefusal"/>).</summary>
        public fixed byte refused[ConvexTrimSearch.Directions];

        /// <summary>The box of the convex as it is now, in the shape's frame: written with <see cref="trims"/> above zero.</summary>
        public float3 lo, hi;
    }

    /// <summary>
    /// The room of one trim attempt, laid out in one block the caller owns: the per-convex states and their working
    /// B-reps, the two halves a clip writes, the clip's scratch. <see cref="Layout"/> is the size formula.
    /// </summary>
    internal unsafe struct ConvexTrimWorkspace
    {
        public int convexCount;
        public ConvexTrimConvex* convexes;
        public ConvexBrepBank* banks;
        public ConvexBrepRange* ranges;
        public BrepBuffer positive, negative;
        public CutScratch scratch;
        public float* signedDistance;
        public sbyte* signClass;
        public int signCapacity;

        /// <summary>Per direction, in the shape's frame: the unit normal and the offset that makes the plane the mesh's support plane (s(x) = dot(n, x) + w; s > 0 is beyond the mesh).</summary>
        public float4* planes;

        /// <summary>Per direction, in the owner's frame: the unit direction.</summary>
        public float3* directions;

        /// <summary>
        /// Lays the workspace out inside <paramref name="basePtr"/> (null only computes <paramref name="bytes"/>). A convex may
        /// take as many trims as there are directions, each adding one face; its vertices never pass the larger of what it has
        /// and <paramref name="vertexLimit"/>, a trim over the limit being refused before it is kept.
        /// </summary>
        public static ConvexTrimWorkspace Layout(byte* basePtr, ConvexBrepRange[] ranges, int vertexLimit, out long bytes)
        {
            long off = 0;
            T* Take<T>(long count) where T : unmanaged
            {
                off = (off + 15) / 16 * 16;
                T* p = basePtr == null ? null : (T*)(basePtr + off);
                off += Math.Max(1L, count) * sizeof(T);
                return p;
            }

            int n = ranges.Length;
            var w = new ConvexTrimWorkspace { convexCount = n };
            w.convexes = Take<ConvexTrimConvex>(n);
            w.banks = Take<ConvexBrepBank>(n);
            w.ranges = Take<ConvexBrepRange>(n);
            w.planes = Take<float4>(ConvexTrimSearch.Directions);
            w.directions = Take<float3>(ConvexTrimSearch.Directions);
            int vMost = 4, eMost = 6, cutMost = 0, hashMost = 16, vOutMost = 0, fOutMost = 0, iOutMost = 0, eOutMost = 0;
            for (int c = 0; c < n; c++)
            {
                ConvexTrimSearch.Bounds(in ranges[c], vertexLimit, out int v, out int f, out int e, out int i);
                ClipCapacity cap = ClipCapacity.Static(v, e, f, i, v);
                BrepBuffer current = default;
                current.v = Take<float3>(v);
                current.faceOff = Take<int>(f + 1);
                current.faceIdx = Take<int>(i);
                current.faceEdge = Take<int>(i);
                current.edges = Take<BrepEdge>(e);
                current.vCap = v; current.fCap = f; current.iCap = i; current.eCap = e;
                if (basePtr != null)
                {
                    w.convexes[c] = default;
                    w.convexes[c].current = current;
                    w.ranges[c] = ranges[c];
                }

                vMost = Math.Max(vMost, v); eMost = Math.Max(eMost, e);
                cutMost = Math.Max(cutMost, cap.cutK); hashMost = Math.Max(hashMost, cap.hashCap);
                vOutMost = Math.Max(vOutMost, cap.vOut); fOutMost = Math.Max(fOutMost, cap.fOut);
                iOutMost = Math.Max(iOutMost, cap.iOut); eOutMost = Math.Max(eOutMost, cap.eOut);
            }

            for (int side = 0; side < 2; side++)
            {
                BrepBuffer half = default;
                half.v = Take<float3>(vOutMost);
                half.faceOff = Take<int>(fOutMost + 1);
                half.faceIdx = Take<int>(iOutMost);
                half.faceEdge = Take<int>(iOutMost);
                half.edges = Take<BrepEdge>(eOutMost);
                half.vCap = vOutMost; half.fCap = fOutMost; half.iCap = iOutMost; half.eCap = eOutMost;
                if (side == 0) w.positive = half; else w.negative = half;
            }

            w.signedDistance = Take<float>(vMost);
            w.signClass = Take<sbyte>(vMost);
            w.signCapacity = vMost;
            off = (off + 15) / 16 * 16;
            w.scratch = CutScratch.Layout(basePtr == null ? null : basePtr + off, vMost, eMost, vMost, cutMost, hashMost, vOutMost, out int scratchBytes);
            off += scratchBytes;
            bytes = off;
            return w;
        }
    }

    /// <summary>What one run of the trim search came to, over all the convexes of the attempt.</summary>
    internal unsafe struct ConvexTrimOutcome
    {
        /// <summary>Trims adopted, convexes that took one or more, and clips tried (adopted or refused).</summary>
        public int trims, trimmedConvexes, clipsTried;

        /// <summary>Candidates there were before any trim: convex and direction pairs sticking out by the threshold or more.</summary>
        public int candidatesBefore;

        /// <summary>Candidates left at the end, by the reason each was left alone.</summary>
        public fixed int left[(int)ConvexTrimRefusal.Count];

        public int verticesBefore, verticesAfter;

        /// <summary>The largest overhang of any convex in any of the 26 directions, metres, before and after.</summary>
        public float overhangBefore, overhangAfter;

        /// <summary>Stopwatch ticks spent in the clips themselves (tried, adopted or not); the rest of the run is the search.</summary>
        public long clipTicks;

        public int LeftInAll
        {
            get
            {
                int all = 0;
                for (int r = 1; r < (int)ConvexTrimRefusal.Count; r++) all += left[r];
                return all;
            }
        }
    }

    /// <summary>
    /// The numbers of the convex trim: the mesh's support planes in 26 directions fixed to the fragment, and the clips of the
    /// convexes that stick out beyond them. Plain unmanaged code over a caller's block; it allocates nothing and touches no
    /// scene object, so a pool worker may run it. The clip is the cut's own (<see cref="ConvexClipKernel"/>).
    /// </summary>
    internal static unsafe class ConvexTrimSearch
    {
        /// <summary>The three axes, the twelve edge diagonals and the eight corner diagonals of the fragment's frame, both ways.</summary>
        public const int Directions = 26;

        /// <summary>The unit direction <paramref name="k"/> (0..25), in the order x outermost, z innermost, each -1, 0, +1, the zero vector left out.</summary>
        public static float3 Direction(int k)
        {
            int at = k >= 13 ? k + 1 : k;   // the zero vector is the 14th of the 27
            var d = new float3((at / 9) - 1, ((at / 3) % 3) - 1, (at % 3) - 1);
            return math.normalize(d);
        }

        /// <summary>The most a convex can come to under the trims: its vertices, faces, edges and face indices.</summary>
        public static void Bounds(in ConvexBrepRange range, int vertexLimit, out int vertices, out int faces, out int edges, out int faceIndices)
        {
            vertices = Math.Max(4, Math.Max(range.vertexCount, vertexLimit));
            faces = Math.Max(4, range.faceCount) + Directions;
            edges = Math.Max(Math.Max(6, range.edgeCount), vertices + faces - 2);
            faceIndices = Math.Max(range.faceIndexCount, 2 * edges);
        }

        /// <summary>
        /// The largest projection of the vertices the indices name on each direction, in the owner's frame
        /// (<paramref name="support"/>: 26 values, metres along the unit direction). False, with nothing usable, where an index
        /// names a vertex beyond the view or a projection is not finite.
        /// </summary>
        public static bool MeshSupport(VpRenderVertex* vertices, int vertexViewLength, uint* indices, int indexCount, in float4x4 geometryToOwner, float* support)
        {
            if (vertices == null || indices == null || indexCount <= 0) return false;
            // dot(d, G p) = dot(G^T d, p) + dot(d, t): the directions are carried into the geometry's frame once
            float3* local = stackalloc float3[Directions];
            float* offset = stackalloc float[Directions];
            float3 c0 = geometryToOwner.c0.xyz, c1 = geometryToOwner.c1.xyz, c2 = geometryToOwner.c2.xyz, t = geometryToOwner.c3.xyz;
            for (int k = 0; k < Directions; k++)
            {
                float3 d = Direction(k);
                local[k] = new float3(math.dot(d, c0), math.dot(d, c1), math.dot(d, c2));
                offset[k] = math.dot(d, t);
                support[k] = float.NegativeInfinity;
            }

            uint limit = (uint)vertexViewLength;
            for (int i = 0; i < indexCount; i++)
            {
                uint id = indices[i];
                if (id >= limit) return false;
                float3 p = vertices[id].position;
                for (int k = 0; k < Directions; k++)
                {
                    float s = math.dot(local[k], p);
                    if (s > support[k]) support[k] = s;
                }
            }

            for (int k = 0; k < Directions; k++)
            {
                support[k] += offset[k];
                if (!math.isfinite(support[k])) return false;
            }

            return true;
        }

        /// <summary>
        /// Writes the 26 planes into the workspace: the mesh's support planes (<paramref name="support"/>, owner's frame) carried
        /// into the shape's frame, which <paramref name="shapeToOwner"/> maps rigidly from. The banks and ranges of the
        /// workspace are the caller's to have written before <see cref="Run"/>.
        /// </summary>
        public static void SetPlanes(ref ConvexTrimWorkspace w, in float4x4 shapeToOwner, float* support)
        {
            float3 c0 = shapeToOwner.c0.xyz, c1 = shapeToOwner.c1.xyz, c2 = shapeToOwner.c2.xyz, t = shapeToOwner.c3.xyz;
            for (int k = 0; k < Directions; k++)
            {
                float3 d = Direction(k);
                w.directions[k] = d;
                var n = new float3(math.dot(d, c0), math.dot(d, c1), math.dot(d, c2));
                w.planes[k] = new float4(n, math.dot(d, t) - support[k]);
            }
        }

        /// <summary>Whether a frame is rigid as far as lengths go: three unit columns, at right angles (1e-4).</summary>
        public static bool IsRigid(in float4x4 m)
        {
            float3 a = m.c0.xyz, b = m.c1.xyz, c = m.c2.xyz;
            const float Slack = 1e-4f;
            return math.all(math.isfinite(a)) && math.all(math.isfinite(b)) && math.all(math.isfinite(c)) && math.all(math.isfinite(m.c3.xyz))
                && math.abs(math.lengthsq(a) - 1f) <= Slack && math.abs(math.lengthsq(b) - 1f) <= Slack && math.abs(math.lengthsq(c) - 1f) <= Slack
                && math.abs(math.dot(a, b)) <= Slack && math.abs(math.dot(a, c)) <= Slack && math.abs(math.dot(b, c)) <= Slack;
        }

        /// <summary>
        /// The trims: while a convex sticks out beyond a plane by <paramref name="minOverhang"/> or more, the candidate that
        /// sticks out furthest is clipped, and what lies on the mesh's side is kept where it is a convex of some volume within
        /// the vertex limit; a candidate that is not is left alone and the next is tried. After every trim the candidates are
        /// looked at again on the convexes as they then are.
        /// </summary>
        public static void Run(ref ConvexTrimWorkspace w, float minOverhang, int vertexLimit, double minKeptVolume, double minKeptVolumeRatio, out ConvexTrimOutcome outcome)
        {
            outcome = default;
            outcome.overhangBefore = float.NegativeInfinity;
            outcome.overhangAfter = float.NegativeInfinity;
            for (int c = 0; c < w.convexCount; c++)
            {
                ref ConvexTrimConvex convex = ref w.convexes[c];
                BrepBuffer view = w.banks[c].View(in w.ranges[c]);
                convex.verticesBefore = view.V;
                convex.verticesAfter = view.V;
                Project(ref convex, in view, w.planes);
                convex.overhangBefore = Overhang(ref convex, w.planes, minOverhang, out int candidates);
                convex.overhangAfter = convex.overhangBefore;
                outcome.candidatesBefore += candidates;
                outcome.verticesBefore += view.V;
                outcome.overhangBefore = math.max(outcome.overhangBefore, convex.overhangBefore);
            }

            while (true)
            {
                int bestConvex = -1, bestDirection = -1;
                float best = minOverhang;
                for (int c = 0; c < w.convexCount; c++)
                {
                    ref ConvexTrimConvex convex = ref w.convexes[c];
                    for (int k = 0; k < Directions; k++)
                    {
                        if (convex.refused[k] != 0) continue;
                        float over = convex.projection[k] + w.planes[k].w;
                        if (over >= best && (bestConvex < 0 || over > best))
                        {
                            best = over; bestConvex = c; bestDirection = k;
                        }
                    }
                }

                if (bestConvex < 0) break;
                outcome.clipsTried++;
                ref ConvexTrimConvex chosen = ref w.convexes[bestConvex];
                long clipBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                ConvexTrimRefusal refusal = Clip(ref w, bestConvex, bestDirection, vertexLimit, minKeptVolume, minKeptVolumeRatio);
                outcome.clipTicks += System.Diagnostics.Stopwatch.GetTimestamp() - clipBegan;
                if (refusal != ConvexTrimRefusal.None)
                {
                    chosen.refused[bestDirection] = (byte)refusal;
                    continue;
                }

                // Adopted: the convex is another one from here. Its projections are taken again, and what was left alone on
                // the one before is looked at again on this one.
                chosen.trims++;
                outcome.trims++;
                chosen.verticesAfter = chosen.current.V;
                Project(ref chosen, in chosen.current, w.planes);
                for (int k = 0; k < Directions; k++) chosen.refused[k] = 0;
                chosen.overhangAfter = Overhang(ref chosen, w.planes, minOverhang, out _);
            }

            for (int c = 0; c < w.convexCount; c++)
            {
                ref ConvexTrimConvex convex = ref w.convexes[c];
                if (convex.trims > 0)
                {
                    outcome.trimmedConvexes++;
                    var lo = new float3(float.MaxValue);
                    var hi = new float3(float.MinValue);
                    for (int v = 0; v < convex.current.V; v++)
                    {
                        lo = math.min(lo, convex.current.v[v]);
                        hi = math.max(hi, convex.current.v[v]);
                    }

                    convex.lo = lo; convex.hi = hi;
                }

                outcome.verticesAfter += convex.verticesAfter;
                outcome.overhangAfter = math.max(outcome.overhangAfter, convex.overhangAfter);
                for (int k = 0; k < Directions; k++)
                {
                    if (convex.projection[k] + w.planes[k].w >= minOverhang)
                    {
                        int reason = convex.refused[k];
                        outcome.left[reason > 0 && reason < (int)ConvexTrimRefusal.Count ? reason : (int)ConvexTrimRefusal.Degenerate]++;
                    }
                }
            }
        }

        private static void Project(ref ConvexTrimConvex convex, in BrepBuffer b, float4* planes)
        {
            for (int k = 0; k < Directions; k++)
            {
                float3 n = planes[k].xyz;
                float most = float.NegativeInfinity;
                for (int v = 0; v < b.V; v++)
                {
                    float s = math.dot(n, b.v[v]);
                    if (s > most) most = s;
                }

                convex.projection[k] = most;
            }
        }

        private static float Overhang(ref ConvexTrimConvex convex, float4* planes, float minOverhang, out int candidates)
        {
            float most = float.NegativeInfinity;
            candidates = 0;
            for (int k = 0; k < Directions; k++)
            {
                float over = convex.projection[k] + planes[k].w;
                if (over > most) most = over;
                if (over >= minOverhang) candidates++;
            }

            return most;
        }

        private static ConvexTrimRefusal Clip(ref ConvexTrimWorkspace w, int c, int k, int vertexLimit, double minKeptVolume, double minKeptVolumeRatio)
        {
            ref ConvexTrimConvex convex = ref w.convexes[c];
            if (convex.copied == 0)
            {
                BrepBuffer from = w.banks[c].View(in w.ranges[c]);
                if (!Copy(in from, ref convex.current)) return ConvexTrimRefusal.Capacity;
                convex.copied = 1;
                var before = default(MassProperties);
                MassPropertiesKernel.ComputeDouble(in convex.current, ref before);
                convex.volumeBefore = before.volume;
                convex.volumeAfter = before.volume;
            }

            BrepBuffer input = convex.current;
            if (input.V > w.signCapacity) return ConvexTrimRefusal.Capacity;
            float4 plane = w.planes[k];
            bool anyKept = false;
            for (int v = 0; v < input.V; v++)
            {
                float s = math.dot(plane.xyz, input.v[v]) + plane.w;
                w.signedDistance[v] = s;
                w.signClass[v] = (sbyte)(s > 0f ? 1 : s < 0f ? -1 : 0);
                if (s < 0f) anyKept = true;
            }

            if (!anyKept) return ConvexTrimRefusal.NothingKept;
            var stats = default(CutStats);
            int status = ConvexClipKernel.Cut(in input, w.signedDistance, w.signClass, in plane, ref w.positive, ref w.negative, ref w.scratch, ref stats);
            switch ((CutStatus)status)
            {
                case CutStatus.Ok:
                    break;
                case CutStatus.CapacityVertex:
                case CutStatus.CapacityFace:
                case CutStatus.CapacityIndex:
                case CutStatus.CapacityEdge:
                case CutStatus.CapacityCut:
                case CutStatus.CapacityHash:
                case CutStatus.CapacityScratch:
                    return ConvexTrimRefusal.Capacity;
                default:
                    return ConvexTrimRefusal.Degenerate;   // not split at float resolution, a cut polygon too small, a walk that failed
            }

            BrepBuffer kept = w.negative;
            if (kept.V < 4 || kept.F < 4) return ConvexTrimRefusal.Degenerate;
            if (kept.V > vertexLimit) return ConvexTrimRefusal.VertexLimit;
            var mass = default(MassProperties);
            MassPropertiesKernel.ComputeDouble(in kept, ref mass);
            if (!(mass.volume > 0.0) || double.IsInfinity(mass.volume)) return ConvexTrimRefusal.Degenerate;
            if (mass.volume < minKeptVolume || mass.volume < minKeptVolumeRatio * convex.volumeBefore) return ConvexTrimRefusal.VolumeShort;
            if (!Copy(in kept, ref convex.current)) return ConvexTrimRefusal.Capacity;
            convex.volumeAfter = mass.volume;
            return ConvexTrimRefusal.None;
        }

        // The counts and the arrays; the capacities of the destination stay its own.
        private static bool Copy(in BrepBuffer from, ref BrepBuffer to)
        {
            if (from.V > to.vCap || from.F > to.fCap || from.I > to.iCap || from.E > to.eCap) return false;
            UnsafeUtility.MemCpy(to.v, from.v, (long)from.V * sizeof(float3));
            UnsafeUtility.MemCpy(to.faceOff, from.faceOff, (long)(from.F + 1) * sizeof(int));
            UnsafeUtility.MemCpy(to.faceIdx, from.faceIdx, (long)from.I * sizeof(int));
            UnsafeUtility.MemCpy(to.faceEdge, from.faceEdge, (long)from.I * sizeof(int));
            UnsafeUtility.MemCpy(to.edges, from.edges, (long)from.E * sizeof(BrepEdge));
            to.V = from.V; to.F = from.F; to.I = from.I; to.E = from.E;
            return true;
        }

        /// <summary>The longest face loop of a B-rep (a range's <c>maxFaceLoop</c>).</summary>
        public static int MaxFaceLoop(in BrepBuffer b)
        {
            int most = 0;
            for (int f = 0; f < b.F; f++) most = Math.Max(most, b.faceOff[f + 1] - b.faceOff[f]);
            return most;
        }
    }
}
