using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>Projected boundary nodes of one cut: (u, v) per node in the plane basis, node positions, the near-contact epsilon.</summary>
    public unsafe struct CapContext
    {
        public float* U, V;
        public float3* Pos;
        /// <summary>Distance below which two non-adjacent cycle edges count as touching (a few float ulps of the input extent).</summary>
        public float Eps;
        /// <summary>Loops with at least this many nodes use the z-order hashed ear test.</summary>
        public int HashThreshold;
    }

    /// <summary>
    /// Cap triangulation of one boundary cycle, cycle-local: ids below the cycle length name cycle positions, ids from
    /// the cycle length upward name auxiliary vertices appended by the split (crossing points and duplicated visits).
    /// </summary>
    public unsafe struct CapOut
    {
        /// <summary>Arena-allocated by the cap: 3 cycle-local ids per triangle.</summary>
        public int* triangles;
        public int triangleCount, triangleCap;
        /// <summary>Arena-allocated by the cap: auxiliary vertex positions and plane coordinates.</summary>
        public float3* auxPos;
        public float* auxU, auxV;
        public int auxCount;
        public int degenerate, reflex, artifacts, stalled, crossings, contacts;
        /// <summary>How the cycle was capped: 0 ear clipped as a simple cycle, 1 split at proper crossings, 2 combinatorial ear clipping of a touching / retraced cycle.</summary>
        public int method;
        /// <summary>0 ok, 1 a split piece stalled with every remaining diagonal forbidden, 2 the split's triangle count is not the disk's (both internal errors, never repaired), 3 capacity.</summary>
        public int failure;

        public void AddTriangle(int a, int b, int c)
        {
            if (triangleCount + 3 > triangleCap) { failure = 3; return; }
            triangles[triangleCount++] = a; triangles[triangleCount++] = b; triangles[triangleCount++] = c;
        }
    }

    /// <summary>
    /// The cap of the display cut (DESIGN 6.4): the probe's adopted split cap over reflex ear clipping with z-order
    /// hashing, ported from the probe's BurstCapping onto the caller's scratch arena. A simple cycle is ear clipped
    /// directly; a cycle whose only relations are proper crossings is split at them into simple pieces which are ear
    /// clipped and glued back to the cycle with zero-area slivers; a cycle that touches itself (distinct nodes at one
    /// position, edges along one line) is ear clipped combinatorially without auxiliary vertices. Each construction
    /// closes the cycle as a disk by itself; nothing is checked after the fact. Same predicates, same precision, same
    /// tie-breaking as the probe where the probe's construction is used.
    /// </summary>
    public static unsafe class MeshCutCap
    {
        public const int DefaultHashThreshold = 80;

        public struct ContactPair
        {
            public int E, F;
            public byte Kind;   // 0 crossing, 1 touch, 2 near
        }

        // ------------------------------------------------------------------ geometry

        public static double Cross(in CapContext c, int o, int a, int b)
        {
            double ox = c.U[o], oy = c.V[o];
            return ((double)c.U[a] - ox) * ((double)c.V[b] - oy) - ((double)c.V[a] - oy) * ((double)c.U[b] - ox);
        }

        public static double TriangleArea2(in CapContext c, int a, int b, int d)
        {
            double ax = c.U[a], ay = c.V[a];
            return ((double)c.U[b] - ax) * ((double)c.V[d] - ay) - ((double)c.V[b] - ay) * ((double)c.U[d] - ax);
        }

        static bool PointInTriangle(in CapContext c, int a, int b, int d, int p, int sign)
        {
            double d0 = TriangleArea2(c, a, b, p) * sign;
            double d1 = TriangleArea2(c, b, d, p) * sign;
            double d2 = TriangleArea2(c, d, a, p) * sign;
            return d0 > 0 && d1 > 0 && d2 > 0;
        }

        public static double SignedArea2(in CapContext c, int* nodes, int n)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                int a = nodes[i], b = nodes[(i + 1) % n];
                sum += (double)c.U[a] * c.V[b] - (double)c.U[b] * c.V[a];
            }
            return sum;
        }

        static bool OnSegment(in CapContext c, int s0, int s1, int p)
        {
            float x = c.U[p], y = c.V[p];
            return x >= Math.Min(c.U[s0], c.U[s1]) && x <= Math.Max(c.U[s0], c.U[s1]) &&
                   y >= Math.Min(c.V[s0], c.V[s1]) && y <= Math.Max(c.V[s0], c.V[s1]);
        }

        static double Dist2(in CapContext c, int a, int b)
        {
            double du = (double)c.U[a] - c.U[b], dv = (double)c.V[a] - c.V[b];
            return du * du + dv * dv;
        }

        static double PointSegmentDistance2(in CapContext c, int p, int s0, int s1)
        {
            double sx = c.U[s0], sy = c.V[s0];
            double vx = (double)c.U[s1] - sx, vy = (double)c.V[s1] - sy;
            double wx = (double)c.U[p] - sx, wy = (double)c.V[p] - sy;
            double len2 = vx * vx + vy * vy;
            double t = len2 > 0 ? Math.Max(0.0, Math.Min(1.0, (wx * vx + wy * vy) / len2)) : 0.0;
            double dx = sx + t * vx - c.U[p], dy = sy + t * vy - c.V[p];
            return dx * dx + dy * dy;
        }

        static long DiagonalKey(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        struct KeyOrder : IComparer<int>
        {
            public float* Key;
            public int Compare(int a, int b) { int c = Key[a].CompareTo(Key[b]); return c != 0 ? c : a.CompareTo(b); }
        }

        // ------------------------------------------------------------------ contact sweep

        /// <summary>
        /// One sweep classifying the cycle and collecting every crossing, touching and near pair of non-adjacent edges.
        /// Ties in the sweep order are broken by segment index so auxiliary vertices are numbered identically on every run.
        /// </summary>
        public static void FindContacts(in CapContext c, int* nodes, int n, ContactPair* into, int intoCap, out int pairCount,
                                        out int crossings, out int contacts, ref ScratchArena arena)
        {
            crossings = 0; contacts = 0; pairCount = 0;
            if (n < 4) return;
            float eps = c.Eps;
            double eps2 = (double)eps * eps;

            float* minX = arena.Take<float>(n), maxX = arena.Take<float>(n), minY = arena.Take<float>(n), maxY = arena.Take<float>(n);
            int* order = arena.Take<int>(n);
            int* active = arena.Take<int>(n);
            if (arena.overflow != 0) return;
            for (int i = 0; i < n; i++)
            {
                int a = nodes[i], b = nodes[(i + 1) % n];
                float ua = c.U[a], ub = c.U[b], va = c.V[a], vb = c.V[b];
                minX[i] = Math.Min(ua, ub); maxX[i] = Math.Max(ua, ub);
                minY[i] = Math.Min(va, vb); maxY[i] = Math.Max(va, vb);
                order[i] = i;
            }
            NativeSortExtension.Sort(order, n, new KeyOrder { Key = minX });

            int activeCount = 0;
            for (int oi = 0; oi < n; oi++)
            {
                int e = order[oi];
                float lo = minX[e];
                for (int k = activeCount - 1; k >= 0; k--)
                    if (maxX[active[k]] < lo - eps) { active[k] = active[activeCount - 1]; activeCount--; }
                float eMinY = minY[e] - eps, eMaxY = maxY[e] + eps;
                for (int k = 0; k < activeCount; k++)
                {
                    int f = active[k];
                    if (maxY[f] < eMinY || minY[f] > eMaxY) continue;
                    int d = Math.Abs(e - f);
                    if (d == 1 || d == n - 1) continue;

                    int a0 = nodes[e], a1 = nodes[(e + 1) % n];
                    int b0 = nodes[f], b1 = nodes[(f + 1) % n];
                    double d1 = Cross(c, b0, b1, a0), d2 = Cross(c, b0, b1, a1);
                    double d3 = Cross(c, a0, a1, b0), d4 = Cross(c, a0, a1, b1);
                    if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                    {
                        crossings++;
                        if (pairCount < intoCap) into[pairCount] = new ContactPair { E = e, F = f, Kind = 0 };
                        else arena.overflow = 1;
                        pairCount++;
                        continue;
                    }
                    bool touch = (d1 == 0 && OnSegment(c, b0, b1, a0)) || (d2 == 0 && OnSegment(c, b0, b1, a1)) ||
                                 (d3 == 0 && OnSegment(c, a0, a1, b0)) || (d4 == 0 && OnSegment(c, a0, a1, b1));
                    if (touch)
                    {
                        contacts++;
                        if (pairCount < intoCap) into[pairCount] = new ContactPair { E = e, F = f, Kind = 1 };
                        else arena.overflow = 1;
                        pairCount++;
                        continue;
                    }
                    if (eps > 0)
                    {
                        double s2 = Math.Min(Math.Min(PointSegmentDistance2(c, a0, b0, b1), PointSegmentDistance2(c, a1, b0, b1)),
                                             Math.Min(PointSegmentDistance2(c, b0, a0, a1), PointSegmentDistance2(c, b1, a0, a1)));
                        if (s2 < eps2)
                        {
                            contacts++;
                            if (pairCount < intoCap) into[pairCount] = new ContactPair { E = e, F = f, Kind = 2 };
                            else arena.overflow = 1;
                            pairCount++;
                        }
                    }
                }
                active[activeCount++] = e;
            }
        }

        // ------------------------------------------------------------------ reflex ear clipping

        static uint ZOrder(float u, float v, float minU, float minV, float invSize)
        {
            uint x = (uint)Math.Max(0f, Math.Min(32767f, (u - minU) * invSize));
            uint y = (uint)Math.Max(0f, Math.Min(32767f, (v - minV) * invSize));
            x = (x | (x << 8)) & 0x00FF00FF; x = (x | (x << 4)) & 0x0F0F0F0F;
            x = (x | (x << 2)) & 0x33333333; x = (x | (x << 1)) & 0x55555555;
            y = (y | (y << 8)) & 0x00FF00FF; y = (y | (y << 4)) & 0x0F0F0F0F;
            y = (y | (y << 2)) & 0x33333333; y = (y | (y << 1)) & 0x55555555;
            return x | (y << 1);
        }

        struct ZOrderKey : IComparer<int>
        {
            public uint* Key;
            public int Compare(int a, int b) { int c = Key[a].CompareTo(Key[b]); return c != 0 ? c : a.CompareTo(b); }
        }

        /// <summary>
        /// Ear clips the cycle (nodes[0..n) as ids into the context) into result.triangles (cycle-local indices).
        /// `forbidden` holds diagonal keys over context ids that must not be used; pass an uncreated set when unconstrained.
        /// </summary>
        public static void ReflexEarClip(in CapContext c, int* nodes, int n, in LongSet forbidden, ref CapOut result, ref ScratchArena arena)
        {
            int sign = SignedArea2(c, nodes, n) >= 0 ? 1 : -1;
            bool hasForbidden = forbidden.IsCreated;

            int* prev = arena.Take<int>(n), next = arena.Take<int>(n);
            int* reflexList = arena.Take<int>(n), reflexAt = arena.Take<int>(n);
            if (arena.overflow != 0) return;
            for (int i = 0; i < n; i++) { prev[i] = (i + n - 1) % n; next[i] = (i + 1) % n; }
            int reflexCount = 0;
            for (int i = 0; i < n; i++) reflexAt[i] = -1;
            for (int i = 0; i < n; i++)
                if (TriangleArea2(c, nodes[prev[i]], nodes[i], nodes[next[i]]) * sign < 0)
                {
                    reflexAt[i] = reflexCount;
                    reflexList[reflexCount++] = i;
                }
            result.reflex += reflexCount;

            bool hashed = n >= c.HashThreshold;
            uint* zorder = null;
            int* prevZ = null, nextZ = null;
            float boxMinU = 0f, boxMinV = 0f, invSize = 0f;
            if (hashed)
            {
                float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
                boxMinU = float.PositiveInfinity; boxMinV = float.PositiveInfinity;
                for (int i = 0; i < n; i++)
                {
                    float u = c.U[nodes[i]], v = c.V[nodes[i]];
                    if (u < boxMinU) boxMinU = u; if (u > maxU) maxU = u;
                    if (v < boxMinV) boxMinV = v; if (v > maxV) maxV = v;
                }
                float size = Math.Max(maxU - boxMinU, maxV - boxMinV);
                invSize = size > 0f ? 32767f / size : 0f;
                zorder = arena.Take<uint>(n);
                int* byZ = arena.Take<int>(n);
                prevZ = arena.Take<int>(n);
                nextZ = arena.Take<int>(n);
                if (arena.overflow != 0) return;
                for (int i = 0; i < n; i++) { zorder[i] = ZOrder(c.U[nodes[i]], c.V[nodes[i]], boxMinU, boxMinV, invSize); byZ[i] = i; }
                NativeSortExtension.Sort(byZ, n, new ZOrderKey { Key = zorder });
                for (int k = 0; k < n; k++)
                {
                    prevZ[byZ[k]] = k > 0 ? byZ[k - 1] : -1;
                    nextZ[byZ[k]] = k + 1 < n ? byZ[k + 1] : -1;
                }
            }

            int remaining = n, current = 0, sinceProgress = 0;
            while (remaining > 3)
            {
                if (IsEar(c, nodes, prev, next, reflexList, reflexAt, reflexCount, current, sign, hashed, zorder, prevZ, nextZ,
                          boxMinU, boxMinV, invSize, in forbidden, hasForbidden))
                {
                    int p = prev[current], q = next[current];
                    Emit(c, nodes, p, current, q, ref result);
                    next[p] = q; prev[q] = p;
                    RemoveReflex(reflexList, reflexAt, ref reflexCount, current);
                    if (hashed) UnlinkZ(prevZ, nextZ, current);
                    remaining--;
                    Refresh(c, nodes, prev, next, reflexList, reflexAt, ref reflexCount, p, sign);
                    Refresh(c, nodes, prev, next, reflexList, reflexAt, ref reflexCount, q, sign);
                    current = q;
                    sinceProgress = 0;
                    continue;
                }
                current = next[current];
                if (++sinceProgress > remaining)
                {
                    // No ear in a full pass: the polygon is degenerate somewhere (collinear folds, spikes from coplanar
                    // sheets). Clip the least-area candidate and go on; the disk is kept either way.
                    int pick = current;
                    double least = double.MaxValue;
                    int v = current;
                    for (int step = 0; step < remaining; step++)
                    {
                        double a = Math.Abs(TriangleArea2(c, nodes[prev[v]], nodes[v], nodes[next[v]]));
                        if (hasForbidden && forbidden.Contains(DiagonalKey(nodes[prev[v]], nodes[next[v]]))) a = double.MaxValue;
                        if (a < least) { least = a; pick = v; }
                        v = next[v];
                    }
                    if (least == double.MaxValue) { result.failure = 1; break; }
                    int pp = prev[pick], pq = next[pick];
                    Emit(c, nodes, pp, pick, pq, ref result);
                    if (least > 0) result.artifacts++;
                    result.stalled++;
                    next[pp] = pq; prev[pq] = pp;
                    RemoveReflex(reflexList, reflexAt, ref reflexCount, pick);
                    if (hashed) UnlinkZ(prevZ, nextZ, pick);
                    remaining--;
                    Refresh(c, nodes, prev, next, reflexList, reflexAt, ref reflexCount, pp, sign);
                    Refresh(c, nodes, prev, next, reflexList, reflexAt, ref reflexCount, pq, sign);
                    current = pq;
                    sinceProgress = 0;
                }
            }
            if (result.failure == 0) Emit(c, nodes, prev[current], current, next[current], ref result);
        }

        static void Emit(in CapContext c, int* nodes, int a, int b, int d, ref CapOut result)
        {
            result.AddTriangle(a, b, d);
            if (TriangleArea2(c, nodes[a], nodes[b], nodes[d]) == 0.0) result.degenerate++;
        }

        static void RemoveReflex(int* reflexList, int* reflexAt, ref int reflexCount, int i)
        {
            int at = reflexAt[i];
            if (at < 0) return;
            int last = reflexList[--reflexCount];
            reflexList[at] = last;
            reflexAt[last] = at;
            reflexAt[i] = -1;
        }

        static void Refresh(in CapContext c, int* nodes, int* prev, int* next, int* reflexList, int* reflexAt, ref int reflexCount, int i, int sign)
        {
            bool reflex = TriangleArea2(c, nodes[prev[i]], nodes[i], nodes[next[i]]) * sign < 0;
            if (reflex && reflexAt[i] < 0) { reflexAt[i] = reflexCount; reflexList[reflexCount++] = i; }
            else if (!reflex && reflexAt[i] >= 0) RemoveReflex(reflexList, reflexAt, ref reflexCount, i);
        }

        static void UnlinkZ(int* prevZ, int* nextZ, int i)
        {
            if (prevZ[i] >= 0) nextZ[prevZ[i]] = nextZ[i];
            if (nextZ[i] >= 0) prevZ[nextZ[i]] = prevZ[i];
        }

        static bool IsEar(in CapContext c, int* nodes, int* prev, int* next, int* reflexList, int* reflexAt, int reflexCount, int i, int sign,
                          bool hashed, uint* zorder, int* prevZ, int* nextZ, float boxMinU, float boxMinV, float invSize,
                          in LongSet forbidden, bool hasForbidden)
        {
            if (reflexAt[i] >= 0) return false;
            int p = prev[i], q = next[i];
            if (hasForbidden && forbidden.Contains(DiagonalKey(nodes[p], nodes[q]))) return false;
            double area2 = TriangleArea2(c, nodes[p], nodes[i], nodes[q]) * sign;
            if (area2 < 0) return false;
            if (area2 == 0) return true;

            if (!hashed)
            {
                for (int k = 0; k < reflexCount; k++)
                {
                    int j = reflexList[k];
                    if (j == p || j == i || j == q) continue;
                    if (PointInTriangle(c, nodes[p], nodes[i], nodes[q], nodes[j], sign)) return false;
                }
                return true;
            }

            float pu = c.U[nodes[p]], pv = c.V[nodes[p]];
            float iu = c.U[nodes[i]], iv = c.V[nodes[i]];
            float qu = c.U[nodes[q]], qv = c.V[nodes[q]];
            float minU = Math.Min(pu, Math.Min(iu, qu)), maxU = Math.Max(pu, Math.Max(iu, qu));
            float minV = Math.Min(pv, Math.Min(iv, qv)), maxV = Math.Max(pv, Math.Max(iv, qv));
            uint minZ = ZOrder(minU, minV, boxMinU, boxMinV, invSize);
            uint maxZ = ZOrder(maxU, maxV, boxMinU, boxMinV, invSize);

            int lo = prevZ[i], hi = nextZ[i];
            while (lo >= 0 && zorder[lo] >= minZ)
            {
                if (Inside(c, nodes, reflexAt, lo, p, i, q, sign, minU, maxU, minV, maxV)) return false;
                lo = prevZ[lo];
            }
            while (hi >= 0 && zorder[hi] <= maxZ)
            {
                if (Inside(c, nodes, reflexAt, hi, p, i, q, sign, minU, maxU, minV, maxV)) return false;
                hi = nextZ[hi];
            }
            return true;
        }

        static bool Inside(in CapContext c, int* nodes, int* reflexAt, int j, int p, int i, int q, int sign, float minU, float maxU, float minV, float maxV)
        {
            if (j == p || j == q || reflexAt[j] < 0) return false;
            float ju = c.U[nodes[j]], jv = c.V[nodes[j]];
            if (ju < minU || ju > maxU || jv < minV || jv > maxV) return false;
            return PointInTriangle(c, nodes[p], nodes[i], nodes[q], nodes[j], sign);
        }

        // ------------------------------------------------------------------ split cap

        struct Ev
        {
            public int Segment;
            public double T;
            public int Node;
            public int Order;
        }

        struct EvOrder : IComparer<Ev>
        {
            public int Compare(Ev a, Ev b)
            {
                int s = a.Segment.CompareTo(b.Segment);
                if (s != 0) return s;
                int t = a.T.CompareTo(b.T);
                return t != 0 ? t : a.Order.CompareTo(b.Order);
            }
        }

        struct Events
        {
            public Ev* items;
            public int count, cap;
            public LongSet seen;
            public int order;

            public void Add(int segment, double t, int node, ref ScratchArena arena)
            {
                long key = ((long)segment << 32) | (uint)node;
                if (!seen.Add(key)) return;
                if (count >= cap) { arena.overflow = 1; return; }
                items[count++] = new Ev { Segment = segment, T = t, Node = node, Order = order++ };
            }
        }

        /// <summary>
        /// Caps one boundary cycle. `nodes` are context ids in cap traversal order (the surface contour reversed). The
        /// construction is chosen from the cycle's own edge relations before any triangle exists, never by checking a
        /// generated cap:
        ///  - no crossing and no contact: the cycle is ear clipped as it is;
        ///  - proper crossings only, each strictly inside both of its segments (farther than eps from every endpoint):
        ///    the cycle is split at the crossings into simple pieces around one auxiliary vertex per crossing, the pieces
        ///    are ear clipped and glued back to the cycle edges with zero-area slivers (the probe's split cap);
        ///  - any touch, near contact or crossing at an endpoint (several distinct nodes at one position, an edge running
        ///    along another: the retraced contours of doubled regions and of re-cut caps): the port matching of the split
        ///    is not defined on such a cycle, so it is triangulated combinatorially by the same ear clipper without any
        ///    auxiliary vertex. Every diagonal joins two distinct nodes of the cycle, so its n - 2 triangles close the
        ///    cycle as a disk by construction whatever the projected shape (each cycle edge once, each diagonal twice in
        ///    opposite directions, one fan around every node). A triangle whose projected winding opposes the cycle's
        ///    receives render vertices of its own sign from the caller; the logical topology stays one vertex per node.
        /// The split's disk relation (n + 2 aux - 2 triangles) is its invariant, reported as failure 2 and never repaired.
        /// The arena is the per-cycle scratch; its overflow flag ends the run as CapacityScratch.
        /// </summary>
        public static void SplitCap(in CapContext context, int* nodes, int n, ref CapOut result, ref ScratchArena arena)
        {
            // Pairs scale with the arena so a larger scratch on the next attempt admits more contacts.
            long maxPairs = (long)n * (n - 3) / 2;
            int pairCap = (int)math.min(math.max(16, (long)arena.Remaining / 3 / sizeof(ContactPair)), math.max(16, maxPairs));
            ContactPair* pairs = arena.Take<ContactPair>(pairCap);
            if (arena.overflow != 0) return;
            FindContacts(context, nodes, n, pairs, pairCap, out int pairCount, out int crossings, out int contacts, ref arena);
            if (arena.overflow != 0) return;
            result.crossings += crossings;
            result.contacts += contacts;
            result.auxCount = 0;
            float eps = context.Eps;

            // ---- the decision: split only when every relation is a proper crossing strictly inside both segments.
            bool split = contacts == 0 && crossings > 0;
            for (int pi = 0; pi < pairCount && split; pi++)
            {
                int e = pairs[pi].E, f = pairs[pi].F;
                int a0 = nodes[e], a1 = nodes[(e + 1) % n];
                int b0 = nodes[f], b1 = nodes[(f + 1) % n];
                double d1 = Cross(context, b0, b1, a0), d2 = Cross(context, b0, b1, a1);
                double d3 = Cross(context, a0, a1, b0), d4 = Cross(context, a0, a1, b1);
                double ta = d1 / (d1 - d2), tb = d3 / (d3 - d4);
                double lenA = Math.Sqrt(Dist2(context, a0, a1)), lenB = Math.Sqrt(Dist2(context, b0, b1));
                // a crossing within eps of an endpoint is that endpoint touching the other segment, not a split point
                if (ta * lenA <= eps || (1 - ta) * lenA <= eps || tb * lenB <= eps || (1 - tb) * lenB <= eps) split = false;
            }
            if (!split)
            {
                result.method = crossings == 0 && contacts == 0 ? 0 : 2;
                result.triangleCap = 3 * (n - 2);
                result.triangles = arena.Take<int>(result.triangleCap);
                if (arena.overflow != 0) return;
                var none = default(LongSet);
                ReflexEarClip(context, nodes, n, in none, ref result, ref arena);
                return;
            }
            result.method = 1;

            int eventCap = 2 * pairCount + 4;
            var events = new Events { items = arena.Take<Ev>(eventCap), count = 0, cap = eventCap, seen = LongSet.Create(ref arena, eventCap), order = 0 };
            // Auxiliary vertices: one per crossing plus at most one duplicate per refined position (interleaved revisits).
            int auxLocalCap = pairCount + n + eventCap;
            float3* auxPos = arena.Take<float3>(auxLocalCap);
            float* auxU = arena.Take<float>(auxLocalCap);
            float* auxV = arena.Take<float>(auxLocalCap);
            int auxCount = 0;
            result.triangleCap = 3 * (n + 2 * auxLocalCap);
            result.triangles = arena.Take<int>(result.triangleCap);
            if (arena.overflow != 0) return;

            for (int pi = 0; pi < pairCount; pi++)
            {
                int e = pairs[pi].E, f = pairs[pi].F;
                int a0 = nodes[e], a1 = nodes[(e + 1) % n];
                int b0 = nodes[f], b1 = nodes[(f + 1) % n];
                double d1 = Cross(context, b0, b1, a0), d2 = Cross(context, b0, b1, a1);
                double d3 = Cross(context, a0, a1, b0), d4 = Cross(context, a0, a1, b1);
                double ta = d1 / (d1 - d2);
                double tb = d3 / (d3 - d4);
                float3 pa = context.Pos[a0], pb = context.Pos[a1];
                float fta = (float)ta;
                var p = new float3(pa.x + fta * (pb.x - pa.x), pa.y + fta * (pb.y - pa.y), pa.z + fta * (pb.z - pa.z));
                int aux = n + auxCount;
                if (auxCount >= auxLocalCap) { arena.overflow = 1; return; }
                auxPos[auxCount] = p;
                auxU[auxCount] = (float)(context.U[a0] + ta * ((double)context.U[a1] - context.U[a0]));
                auxV[auxCount] = (float)(context.V[a0] + ta * ((double)context.V[a1] - context.V[a0]));
                auxCount++;
                events.Add(e, ta, aux, ref arena);
                events.Add(f, tb, aux, ref arena);
            }
            if (arena.overflow != 0) return;
            NativeSortExtension.Sort(events.items, events.count, new EvOrder());

            // ---- refined cycle: each segment followed by its crossings in parameter order.
            int refinedCap = n + events.count + 4;
            int* refined = arena.Take<int>(refinedCap);
            int refinedCount = 0;
            int* chainStart = arena.Take<int>(n + 1);
            int* chainPosList = arena.Take<int>(events.count + 1);
            int chainPosCount = 0;
            if (arena.overflow != 0) return;
            int evCursor = 0;
            for (int i = 0; i < n; i++)
            {
                refined[refinedCount++] = i;
                chainStart[i] = chainPosCount;
                int chainCount = 0;
                while (evCursor < events.count && events.items[evCursor].Segment == i)
                {
                    Ev x = events.items[evCursor++];
                    if (chainCount > 0 && refined[chainPosList[chainPosCount - 1]] == x.Node) continue;
                    chainPosList[chainPosCount++] = refinedCount;
                    refined[refinedCount++] = x.Node;
                    chainCount++;
                }
            }
            chainStart[n] = chainPosCount;
            int m = refinedCount;
            // Ids in play: 0..n-1 cycle nodes (each visited once), n.. auxiliary (each crossing visited twice, plus duplicates).
            int idCap = n + auxLocalCap;
            int* finalId = arena.Take<int>(m);
            int* lastVisit = arena.Take<int>(idCap);
            int* at = arena.Take<int>(idCap);          // id -> index in the open path, -1 when not open
            if (arena.overflow != 0) return;
            for (int i = 0; i < m; i++) finalId[i] = refined[i];
            for (int i = 0; i < idCap; i++) { lastVisit[i] = -1; at[i] = -1; }
            for (int pos = 0; pos < m; pos++) lastVisit[refined[pos]] = pos;

            // ---- split at the second visit of each crossing; an inner crossing still open afterwards is duplicated
            //      for the piece being closed so that its later visit keeps the original id (interleaved crossings).
            int* subStart = arena.Take<int>(m + 2);
            int subCount = 0;
            int* subNodes = arena.Take<int>(2 * m + 2);
            int subNodeCount = 0;
            int* path = arena.Take<int>(m + 1);
            int pathCount = 0;
            if (arena.overflow != 0) return;
            for (int pos = 0; pos < m; pos++)
            {
                int r = refined[pos];
                int k = at[r];
                if (k >= 0)
                {
                    subStart[subCount++] = subNodeCount;
                    for (int idx = k; idx < pathCount; idx++) subNodes[subNodeCount++] = path[idx];
                    for (int idx = k + 1; idx < pathCount; idx++)
                    {
                        int p = path[idx];
                        int q = refined[p];
                        at[q] = -1;
                        if (finalId[p] != q) continue;
                        if (lastVisit[q] <= pos) continue;
                        finalId[p] = Duplicate(context, nodes, n, auxPos, auxU, auxV, ref auxCount, auxLocalCap, q, ref arena);
                    }
                    pathCount = k + 1;
                }
                else
                {
                    at[r] = pathCount;
                    path[pathCount++] = pos;
                }
                if (arena.overflow != 0) return;
            }
            subStart[subCount++] = subNodeCount;
            for (int idx = 0; idx < pathCount; idx++) subNodes[subNodeCount++] = path[idx];
            subStart[subCount] = subNodeCount;

            // ---- slivers glue each refined chain back to its cycle edge; the pieces may not reuse those edges.
            var forbidden = LongSet.Create(ref arena, n + chainPosCount + 4);
            if (arena.overflow != 0) return;
            for (int i = 0; i < n; i++) forbidden.Add(DiagonalKey(i, (i + 1) % n));
            for (int i = 0; i < n; i++)
            {
                int prev = i, nextNode = (i + 1) % n;
                for (int cp = chainStart[i]; cp < chainStart[i + 1]; cp++)
                {
                    int x = finalId[chainPosList[cp]];
                    result.AddTriangle(x, prev, nextNode);
                    result.degenerate++;
                    if (prev != i) forbidden.Add(DiagonalKey(nextNode, prev));
                    prev = x;
                }
            }

            // ---- extended context for the pieces: ids 0..n-1 are the cycle's nodes, n.. auxiliary.
            int total = n + auxCount;
            float* lu = arena.Take<float>(total);
            float* lv = arena.Take<float>(total);
            float3* lp = arena.Take<float3>(total);
            if (arena.overflow != 0) return;
            for (int i = 0; i < n; i++) { lu[i] = context.U[nodes[i]]; lv[i] = context.V[nodes[i]]; lp[i] = context.Pos[nodes[i]]; }
            for (int i = 0; i < auxCount; i++) { lu[n + i] = auxU[i]; lv[n + i] = auxV[i]; lp[n + i] = auxPos[i]; }
            var local = new CapContext { U = lu, V = lv, Pos = lp, Eps = context.Eps, HashThreshold = context.HashThreshold };

            for (int sc = 0; sc + 1 <= subCount; sc++)
            {
                int start = subStart[sc], end = subStart[sc + 1];
                int count = end - start;
                if (count < 3) continue;
                int mark = arena.used;
                int* piece = arena.Take<int>(count);
                if (arena.overflow != 0) return;
                for (int i = 0; i < count; i++) piece[i] = finalId[subNodes[start + i]];
                var part = new CapOut
                {
                    triangles = arena.Take<int>(count * 3), triangleCap = count * 3, triangleCount = 0,
                };
                if (arena.overflow != 0) return;
                ReflexEarClip(local, piece, count, in forbidden, ref part, ref arena);
                if (arena.overflow != 0) return;
                if (part.failure != 0) { result.failure = part.failure; return; }
                for (int i = 0; i + 2 < part.triangleCount; i += 3)
                    result.AddTriangle(piece[part.triangles[i]], piece[part.triangles[i + 1]], piece[part.triangles[i + 2]]);
                result.degenerate += part.degenerate;
                result.reflex += part.reflex;
                result.artifacts += part.artifacts;
                result.stalled += part.stalled;
                if (result.failure != 0) return;
                arena.used = mark;   // the piece's scratch is not needed once its triangles are copied out
            }
            result.auxPos = auxPos; result.auxU = auxU; result.auxV = auxV; result.auxCount = auxCount;

            // The split's invariant: the pieces and slivers form one disk over n + 2 aux vertices.
            if (result.triangleCount / 3 != n + 2 * auxCount - 2) result.failure = 2;
        }

        static int Duplicate(in CapContext c, int* nodes, int n, float3* auxPos, float* auxU, float* auxV, ref int auxCount, int auxCap, int q, ref ScratchArena arena)
        {
            if (auxCount >= auxCap) { arena.overflow = 1; return 0; }
            int id = n + auxCount;
            auxPos[auxCount] = q < n ? c.Pos[nodes[q]] : auxPos[q - n];
            auxU[auxCount] = q < n ? c.U[nodes[q]] : auxU[q - n];
            auxV[auxCount] = q < n ? c.V[nodes[q]] : auxV[q - n];
            auxCount++;
            return id;
        }
    }
}
