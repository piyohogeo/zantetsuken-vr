using System;
using System.Collections.Generic;
using Unity.Collections;

namespace Zantetsu.MeshCut.Tests.ProbeBaseline
{
    public struct F3
    {
        public float X, Y, Z;
        public F3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public struct ContactPairB
    {
        public int E, F;
        public byte Kind;   // 0 crossing, 1 touch, 2 near
    }

    /// <summary>
    /// The cap of the Burst cut: the same split cap over reflex ear clipping with z-order hashing that
    /// the C# core (Caps/SplitCap.cs, Caps/ReflexEarClipCap.cs) and the C++ reference (cap.cpp) run,
    /// written against native containers so Burst compiles it. Per-cycle scratch is Allocator.Temp;
    /// that allocation is part of the cap stage, as it is on the C# side.
    ///
    /// Every predicate is the same expression in the same precision as the other two runtimes.
    /// </summary>
    public static class BurstCapping
    {
        /// <summary>Default z-order hashing threshold (ReflexEarClipCap.HashThreshold); Context.HashThreshold overrides it.</summary>
        public const int HashThreshold = 80;

        // Projected nodes: U/V per node id, positions for auxiliary vertices, epsilon.
        public struct Context
        {
            public NativeArray<float> U, V;
            public NativeArray<F3> Pos;
            public float Eps;
            /// <summary>Loops with at least this many nodes use the z-order hashed ear test.</summary>
            public int HashThreshold;
        }

        public struct CapOut
        {
            public NativeList<int> Triangles;     // cycle-local; >= cycle length are auxiliary
            public NativeList<F3> Aux;
            public int Degenerate, Reflex, Artifacts, Stalled, Crossings;
            public int Failure;                   // 0 ok, 1 stalled with all diagonals forbidden, 2 disk relation
        }

        // ------------------------------------------------------------------ geometry

        public static double Cross(in Context c, int o, int a, int b)
        {
            double ox = c.U[o], oy = c.V[o];
            return ((double)c.U[a] - ox) * ((double)c.V[b] - oy) - ((double)c.V[a] - oy) * ((double)c.U[b] - ox);
        }

        public static double TriangleArea2(in Context c, int a, int b, int d)
        {
            double ax = c.U[a], ay = c.V[a];
            return ((double)c.U[b] - ax) * ((double)c.V[d] - ay) - ((double)c.V[b] - ay) * ((double)c.U[d] - ax);
        }

        public static bool PointInTriangle(in Context c, int a, int b, int d, int p, int sign)
        {
            double d0 = TriangleArea2(c, a, b, p) * sign;
            double d1 = TriangleArea2(c, b, d, p) * sign;
            double d2 = TriangleArea2(c, d, a, p) * sign;
            return d0 > 0 && d1 > 0 && d2 > 0;
        }

        private static double SignedArea2(in Context c, NativeArray<int> nodes, int n)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                int a = nodes[i], b = nodes[(i + 1) % n];
                sum += (double)c.U[a] * c.V[b] - (double)c.U[b] * c.V[a];
            }
            return sum;
        }

        private static bool OnSegment(in Context c, int s0, int s1, int p)
        {
            float x = c.U[p], y = c.V[p];
            return x >= Math.Min(c.U[s0], c.U[s1]) && x <= Math.Max(c.U[s0], c.U[s1]) &&
                   y >= Math.Min(c.V[s0], c.V[s1]) && y <= Math.Max(c.V[s0], c.V[s1]);
        }

        private static double Dist2(in Context c, int a, int b)
        {
            double du = (double)c.U[a] - c.U[b], dv = (double)c.V[a] - c.V[b];
            return du * du + dv * dv;
        }

        private static double PointSegmentDistance2(in Context c, int p, int s0, int s1)
        {
            double sx = c.U[s0], sy = c.V[s0];
            double vx = (double)c.U[s1] - sx, vy = (double)c.V[s1] - sy;
            double wx = (double)c.U[p] - sx, wy = (double)c.V[p] - sy;
            double len2 = vx * vx + vy * vy;
            double t = len2 > 0 ? Math.Max(0.0, Math.Min(1.0, (wx * vx + wy * vy) / len2)) : 0.0;
            double dx = sx + t * vx - c.U[p], dy = sy + t * vy - c.V[p];
            return dx * dx + dy * dy;
        }

        private static double PointSegment(in Context c, int p, int s0, int s1, out double t)
        {
            double sx = c.U[s0], sy = c.V[s0];
            double vx = (double)c.U[s1] - sx, vy = (double)c.V[s1] - sy;
            double wx = (double)c.U[p] - sx, wy = (double)c.V[p] - sy;
            double len2 = vx * vx + vy * vy;
            t = len2 > 0 ? Math.Max(0.0, Math.Min(1.0, (wx * vx + wy * vy) / len2)) : 0.0;
            double dx = sx + t * vx - c.U[p], dy = sy + t * vy - c.V[p];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool SegmentsCollinear(in Context c, int a0, int a1, int b0, int b1, float eps)
        {
            double lenA = Math.Sqrt(Dist2(c, a0, a1)), lenB = Math.Sqrt(Dist2(c, b0, b1));
            if (lenA == 0 || lenB == 0) return true;
            return Math.Abs(Cross(c, b0, b1, a0)) <= eps * lenB && Math.Abs(Cross(c, b0, b1, a1)) <= eps * lenB &&
                   Math.Abs(Cross(c, a0, a1, b0)) <= eps * lenA && Math.Abs(Cross(c, a0, a1, b1)) <= eps * lenA;
        }

        private static long DiagonalKey(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        private struct KeyOrder : IComparer<int>
        {
            public NativeArray<float> Key;
            public int Compare(int a, int b) { int c = Key[a].CompareTo(Key[b]); return c != 0 ? c : a.CompareTo(b); }
        }

        // ------------------------------------------------------------------ contact sweep

        /// <summary>One sweep classifying the cycle and collecting the pairs (LoopGeometry.FindContacts).</summary>
        public static void FindContacts(in Context c, NativeArray<int> nodes, int n, NativeList<ContactPairB> into,
                                        out int crossings, out int contacts)
        {
            crossings = 0; contacts = 0;
            if (n < 4) return;
            float eps = c.Eps;
            double eps2 = (double)eps * eps;

            var minX = new NativeArray<float>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var maxX = new NativeArray<float>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var minY = new NativeArray<float>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var maxY = new NativeArray<float>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var order = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < n; i++)
            {
                int a = nodes[i], b = nodes[(i + 1) % n];
                float ua = c.U[a], ub = c.U[b], va = c.V[a], vb = c.V[b];
                minX[i] = Math.Min(ua, ub); maxX[i] = Math.Max(ua, ub);
                minY[i] = Math.Min(va, vb); maxY[i] = Math.Max(va, vb);
                order[i] = i;
            }
            order.Sort(new KeyOrder { Key = minX });

            var active = new NativeList<int>(64, Allocator.Temp);
            for (int oi = 0; oi < n; oi++)
            {
                int e = order[oi];
                float lo = minX[e];
                for (int k = active.Length - 1; k >= 0; k--)
                    if (maxX[active[k]] < lo - eps) active.RemoveAtSwapBack(k);
                float eMinY = minY[e] - eps, eMaxY = maxY[e] + eps;
                for (int k = 0; k < active.Length; k++)
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
                        into.Add(new ContactPairB { E = e, F = f, Kind = 0 });
                        continue;
                    }
                    bool touch = (d1 == 0 && OnSegment(c, b0, b1, a0)) || (d2 == 0 && OnSegment(c, b0, b1, a1)) ||
                                 (d3 == 0 && OnSegment(c, a0, a1, b0)) || (d4 == 0 && OnSegment(c, a0, a1, b1));
                    if (touch)
                    {
                        contacts++;
                        into.Add(new ContactPairB { E = e, F = f, Kind = 1 });
                        continue;
                    }
                    if (eps > 0)
                    {
                        double s2 = Math.Min(Math.Min(PointSegmentDistance2(c, a0, b0, b1), PointSegmentDistance2(c, a1, b0, b1)),
                                             Math.Min(PointSegmentDistance2(c, b0, a0, a1), PointSegmentDistance2(c, b1, a0, a1)));
                        if (s2 < eps2)
                        {
                            contacts++;
                            into.Add(new ContactPairB { E = e, F = f, Kind = 2 });
                        }
                    }
                }
                active.Add(e);
            }
            active.Dispose(); minX.Dispose(); maxX.Dispose(); minY.Dispose(); maxY.Dispose(); order.Dispose();
        }

        // ------------------------------------------------------------------ reflex ear clipping

        private static uint ZOrder(float u, float v, float minU, float minV, float invSize)
        {
            uint x = (uint)Math.Max(0f, Math.Min(32767f, (u - minU) * invSize));
            uint y = (uint)Math.Max(0f, Math.Min(32767f, (v - minV) * invSize));
            x = (x | (x << 8)) & 0x00FF00FF; x = (x | (x << 4)) & 0x0F0F0F0F;
            x = (x | (x << 2)) & 0x33333333; x = (x | (x << 1)) & 0x55555555;
            y = (y | (y << 8)) & 0x00FF00FF; y = (y | (y << 4)) & 0x0F0F0F0F;
            y = (y | (y << 2)) & 0x33333333; y = (y | (y << 1)) & 0x55555555;
            return x | (y << 1);
        }

        private struct ZOrderKey : IComparer<int>
        {
            public NativeArray<uint> Key;
            public int Compare(int a, int b) => Key[a].CompareTo(Key[b]);
        }

        /// <summary>
        /// Ear clips the cycle (nodes[0..n) as ids into the context) into `result.Triangles` (cycle-local
        /// indices). `forbidden` holds diagonal keys over context ids that must not be used; pass an
        /// uncreated set when unconstrained.
        /// </summary>
        public static void ReflexEarClip(in Context c, NativeArray<int> nodes, int n, NativeHashSet<long> forbidden,
                                         ref CapOut result)
        {
            int sign = SignedArea2(c, nodes, n) >= 0 ? 1 : -1;
            bool hasForbidden = forbidden.IsCreated;

            var prev = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var next = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < n; i++) { prev[i] = (i + n - 1) % n; next[i] = (i + 1) % n; }

            var reflexList = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var reflexAt = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int reflexCount = 0;
            for (int i = 0; i < n; i++) reflexAt[i] = -1;
            for (int i = 0; i < n; i++)
                if (TriangleArea2(c, nodes[prev[i]], nodes[i], nodes[next[i]]) * sign < 0)
                {
                    reflexAt[i] = reflexCount;
                    reflexList[reflexCount++] = i;
                }
            result.Reflex += reflexCount;

            bool hashed = n >= c.HashThreshold;
            NativeArray<uint> zorder = default;
            NativeArray<int> prevZ = default, nextZ = default;
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
                zorder = new NativeArray<uint>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var byZ = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < n; i++) { zorder[i] = ZOrder(c.U[nodes[i]], c.V[nodes[i]], boxMinU, boxMinV, invSize); byZ[i] = i; }
                byZ.Sort(new ZOrderKey { Key = zorder });
                prevZ = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                nextZ = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int k = 0; k < n; k++)
                {
                    prevZ[byZ[k]] = k > 0 ? byZ[k - 1] : -1;
                    nextZ[byZ[k]] = k + 1 < n ? byZ[k + 1] : -1;
                }
                byZ.Dispose();
            }

            int remaining = n, current = 0, sinceProgress = 0;
            while (remaining > 3)
            {
                if (IsEar(c, nodes, prev, next, reflexList, reflexAt, reflexCount, current, sign, hashed, zorder, prevZ, nextZ,
                          boxMinU, boxMinV, invSize, forbidden, hasForbidden))
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
                    if (least == double.MaxValue) { result.Failure = 1; break; }
                    int pp = prev[pick], pq = next[pick];
                    Emit(c, nodes, pp, pick, pq, ref result);
                    if (least > 0) result.Artifacts++;
                    result.Stalled++;
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
            if (result.Failure == 0) Emit(c, nodes, prev[current], current, next[current], ref result);

            prev.Dispose(); next.Dispose(); reflexList.Dispose(); reflexAt.Dispose();
            if (hashed) { zorder.Dispose(); prevZ.Dispose(); nextZ.Dispose(); }
        }

        private static void Emit(in Context c, NativeArray<int> nodes, int a, int b, int d, ref CapOut result)
        {
            result.Triangles.Add(a); result.Triangles.Add(b); result.Triangles.Add(d);
            if (TriangleArea2(c, nodes[a], nodes[b], nodes[d]) == 0.0) result.Degenerate++;
        }

        private static void RemoveReflex(NativeArray<int> reflexList, NativeArray<int> reflexAt, ref int reflexCount, int i)
        {
            int at = reflexAt[i];
            if (at < 0) return;
            int last = reflexList[--reflexCount];
            reflexList[at] = last;
            reflexAt[last] = at;
            reflexAt[i] = -1;
        }

        private static void Refresh(in Context c, NativeArray<int> nodes, NativeArray<int> prev, NativeArray<int> next,
                                    NativeArray<int> reflexList, NativeArray<int> reflexAt, ref int reflexCount, int i, int sign)
        {
            bool reflex = TriangleArea2(c, nodes[prev[i]], nodes[i], nodes[next[i]]) * sign < 0;
            if (reflex && reflexAt[i] < 0) { reflexAt[i] = reflexCount; reflexList[reflexCount++] = i; }
            else if (!reflex && reflexAt[i] >= 0) RemoveReflex(reflexList, reflexAt, ref reflexCount, i);
        }

        private static void UnlinkZ(NativeArray<int> prevZ, NativeArray<int> nextZ, int i)
        {
            if (prevZ[i] >= 0) nextZ[prevZ[i]] = nextZ[i];
            if (nextZ[i] >= 0) prevZ[nextZ[i]] = prevZ[i];
        }

        private static bool IsEar(in Context c, NativeArray<int> nodes, NativeArray<int> prev, NativeArray<int> next,
                                  NativeArray<int> reflexList, NativeArray<int> reflexAt, int reflexCount, int i, int sign,
                                  bool hashed, NativeArray<uint> zorder, NativeArray<int> prevZ, NativeArray<int> nextZ,
                                  float boxMinU, float boxMinV, float invSize, NativeHashSet<long> forbidden, bool hasForbidden)
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

        private static bool Inside(in Context c, NativeArray<int> nodes, NativeArray<int> reflexAt, int j, int p, int i, int q,
                                   int sign, float minU, float maxU, float minV, float maxV)
        {
            if (j == p || j == q || reflexAt[j] < 0) return false;
            float ju = c.U[nodes[j]], jv = c.V[nodes[j]];
            if (ju < minU || ju > maxU || jv < minV || jv > maxV) return false;
            return PointInTriangle(c, nodes[p], nodes[i], nodes[q], nodes[j], sign);
        }

        // ------------------------------------------------------------------ split cap

        private struct Ev
        {
            public int Segment;
            public double T;
            public int Node;
            public int Order;
        }

        private struct EvOrder : IComparer<Ev>
        {
            public int Compare(Ev a, Ev b)
            {
                int s = a.Segment.CompareTo(b.Segment);
                if (s != 0) return s;
                int t = a.T.CompareTo(b.T);
                return t != 0 ? t : a.Order.CompareTo(b.Order);
            }
        }

        /// <summary>
        /// Caps one boundary cycle. `nodes` are context ids in cap traversal order. The cycle is
        /// classified here (contact sweep); a simple one is ear clipped directly, otherwise it is split
        /// at its crossings and contacts exactly as SplitCap.cs does. Auxiliary vertices come back in
        /// `result.Aux`, with cycle-local ids from n upward.
        /// </summary>
        public static void SplitCap(in Context context, NativeArray<int> nodes, int n, ref CapOut result)
        {
            var pairs = new NativeList<ContactPairB>(16, Allocator.Temp);
            FindContacts(context, nodes, n, pairs, out int crossings, out int contacts);
            result.Crossings += crossings;
            if (crossings == 0 && contacts == 0)
            {
                var none = default(NativeHashSet<long>);
                ReflexEarClip(context, nodes, n, none, ref result);
                pairs.Dispose();
                return;
            }

            float eps = context.Eps;
            var events = new NativeList<Ev>(16, Allocator.Temp);
            var seen = new NativeHashSet<long>(16, Allocator.Temp);
            var auxPos = new NativeList<F3>(8, Allocator.Temp);
            var auxU = new NativeList<float>(8, Allocator.Temp);
            var auxV = new NativeList<float>(8, Allocator.Temp);
            int evOrder = 0;

            for (int pi = 0; pi < pairs.Length; pi++)
            {
                int e = pairs[pi].E, f = pairs[pi].F;
                int a0 = nodes[e], a1 = nodes[(e + 1) % n];
                int b0 = nodes[f], b1 = nodes[(f + 1) % n];
                double d1 = Cross(context, b0, b1, a0), d2 = Cross(context, b0, b1, a1);
                double d3 = Cross(context, a0, a1, b0), d4 = Cross(context, a0, a1, b1);
                bool proper = ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
                if (proper)
                {
                    double ta = d1 / (d1 - d2);
                    double tb = d3 / (d3 - d4);
                    double lenA = Math.Sqrt(Dist2(context, a0, a1)), lenB = Math.Sqrt(Dist2(context, b0, b1));
                    bool nearA0 = ta * lenA <= eps, nearA1 = (1 - ta) * lenA <= eps;
                    bool nearB0 = tb * lenB <= eps, nearB1 = (1 - tb) * lenB <= eps;
                    if (nearA0 || nearA1 || nearB0 || nearB1)
                    {
                        if (nearA0) AddEvent(events, seen, ref evOrder, f, tb, e);
                        if (nearA1) AddEvent(events, seen, ref evOrder, f, tb, (e + 1) % n);
                        if (nearB0) AddEvent(events, seen, ref evOrder, e, ta, f);
                        if (nearB1) AddEvent(events, seen, ref evOrder, e, ta, (f + 1) % n);
                        continue;
                    }
                    F3 pa = context.Pos[a0], pb = context.Pos[a1];
                    float fta = (float)ta;
                    var p = new F3(pa.X + fta * (pb.X - pa.X), pa.Y + fta * (pb.Y - pa.Y), pa.Z + fta * (pb.Z - pa.Z));
                    int aux = n + auxPos.Length;
                    auxPos.Add(p);
                    auxU.Add((float)(context.U[a0] + ta * ((double)context.U[a1] - context.U[a0])));
                    auxV.Add((float)(context.V[a0] + ta * ((double)context.V[a1] - context.V[a0])));
                    AddEvent(events, seen, ref evOrder, e, ta, aux);
                    AddEvent(events, seen, ref evOrder, f, tb, aux);
                    continue;
                }
                if (SegmentsCollinear(context, a0, a1, b0, b1, eps)) continue;
                Contact(context, nodes, n, events, seen, ref evOrder, e, f, (f + 1) % n, eps);
                Contact(context, nodes, n, events, seen, ref evOrder, f, e, (e + 1) % n, eps);
            }
            pairs.Dispose();

            // Sort events by (segment, t, arrival); then drop t ~ 1 reports of a node that the next
            // segment also reports (a node coinciding with a cycle vertex).
            var evArray = events.AsArray();
            evArray.Sort(new EvOrder());
            var keep = new NativeArray<bool>(events.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < events.Length; i++) keep[i] = true;
            for (int i = 0; i < events.Length; i++)
            {
                int seg = events[i].Segment;
                double len = Math.Sqrt(Dist2(context, nodes[seg], nodes[(seg + 1) % n]));
                if ((1 - events[i].T) * len > eps) continue;
                int nextSeg = (seg + 1) % n;
                for (int j = 0; j < events.Length; j++)
                    if (events[j].Segment == nextSeg && events[j].Node == events[i].Node) { keep[i] = false; break; }
            }

            // ---- refined cycle: chain per segment, without endpoints and immediate repeats.
            var refined = new NativeList<int>(n + 2 * auxPos.Length + 4, Allocator.Temp);
            var chainStart = new NativeArray<int>(n + 1, Allocator.Temp);     // refined positions of chain nodes, per segment
            var chainPosList = new NativeList<int>(events.Length, Allocator.Temp);
            var originalPos = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int evCursor = 0;
            for (int i = 0; i < n; i++)
            {
                originalPos[i] = refined.Length;
                refined.Add(i);
                chainStart[i] = chainPosList.Length;
                int nextNode = (i + 1) % n;
                int chainCount = 0;
                while (evCursor < events.Length && events[evCursor].Segment == i)
                {
                    Ev x = events[evCursor++];
                    if (!keep[evCursor - 1]) continue;
                    if (x.Node == i || x.Node == nextNode) continue;
                    if (chainCount > 0 && refined[chainPosList[chainPosList.Length - 1]] == x.Node) continue;
                    chainPosList.Add(refined.Length);
                    refined.Add(x.Node);
                    chainCount++;
                }
            }
            chainStart[n] = chainPosList.Length;
            int m = refined.Length;
            var finalId = new NativeArray<int>(m, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < m; i++) finalId[i] = refined[i];
            var lastVisit = new NativeHashMap<int, int>(m, Allocator.Temp);
            for (int pos = 0; pos < m; pos++) lastVisit[refined[pos]] = pos;

            // ---- split at repeated visits with duplication of interleaved ones.
            var subStart = new NativeList<int>(8, Allocator.Temp);   // offsets into subNodes, with terminal entry
            var subNodes = new NativeList<int>(m, Allocator.Temp);   // refined positions
            var path = new NativeList<int>(m, Allocator.Temp);
            var at = new NativeHashMap<int, int>(m, Allocator.Temp);
            var renameNext = new NativeHashSet<int>(8, Allocator.Temp);
            for (int pos = 0; pos < m; pos++)
            {
                int r = refined[pos];
                if (renameNext.Remove(r))
                {
                    finalId[pos] = Duplicate(context, nodes, n, auxPos, auxU, auxV, r);
                    path.Add(pos);
                    continue;
                }
                if (at.TryGetValue(r, out int k))
                {
                    subStart.Add(subNodes.Length);
                    for (int idx = k; idx < path.Length; idx++) subNodes.Add(path[idx]);
                    for (int idx = k + 1; idx < path.Length; idx++)
                    {
                        int p = path[idx];
                        int q = refined[p];
                        at.Remove(q);
                        if (finalId[p] != q) continue;
                        if (lastVisit[q] <= pos) continue;
                        if (q < n && p == originalPos[q]) renameNext.Add(q);
                        else finalId[p] = Duplicate(context, nodes, n, auxPos, auxU, auxV, q);
                    }
                    path.ResizeUninitialized(k + 1);
                }
                else
                {
                    at[r] = path.Length;
                    path.Add(pos);
                }
            }
            subStart.Add(subNodes.Length);
            for (int idx = 0; idx < path.Length; idx++) subNodes.Add(path[idx]);
            subStart.Add(subNodes.Length);

            // ---- slivers and forbidden diagonals.
            var forbidden = new NativeHashSet<long>(n + chainPosList.Length + 4, Allocator.Temp);
            for (int i = 0; i < n; i++) forbidden.Add(DiagonalKey(i, (i + 1) % n));
            for (int i = 0; i < n; i++)
            {
                int prev = i, nextNode = (i + 1) % n;
                for (int cp = chainStart[i]; cp < chainStart[i + 1]; cp++)
                {
                    int x = finalId[chainPosList[cp]];
                    result.Triangles.Add(x); result.Triangles.Add(prev); result.Triangles.Add(nextNode);
                    result.Degenerate++;
                    if (prev != i) forbidden.Add(DiagonalKey(nextNode, prev));
                    prev = x;
                }
            }

            // ---- extended context for the pieces: ids 0..n-1 are the cycle's nodes, n.. auxiliary.
            int total = n + auxPos.Length;
            var lu = new NativeArray<float>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var lv = new NativeArray<float>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var lp = new NativeArray<F3>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < n; i++) { lu[i] = context.U[nodes[i]]; lv[i] = context.V[nodes[i]]; lp[i] = context.Pos[nodes[i]]; }
            for (int i = 0; i < auxPos.Length; i++) { lu[n + i] = auxU[i]; lv[n + i] = auxV[i]; lp[n + i] = auxPos[i]; }
            var local = new Context { U = lu, V = lv, Pos = lp, Eps = context.Eps };

            for (int sc = 0; sc + 1 < subStart.Length; sc++)
            {
                int start = subStart[sc], end = subStart[sc + 1];
                int count = end - start;
                if (count < 3) continue;
                var piece = new NativeArray<int>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < count; i++) piece[i] = finalId[subNodes[start + i]];
                var part = new CapOut
                {
                    Triangles = new NativeList<int>(count * 3, Allocator.Temp),
                    Aux = default
                };
                ReflexEarClip(local, piece, count, forbidden, ref part);
                if (part.Failure != 0) { result.Failure = part.Failure; }
                else
                {
                    for (int i = 0; i < part.Triangles.Length; i++) result.Triangles.Add(piece[part.Triangles[i]]);
                    result.Degenerate += part.Degenerate;
                    result.Reflex += part.Reflex;
                    result.Artifacts += part.Artifacts;
                    result.Stalled += part.Stalled;
                }
                part.Triangles.Dispose();
                piece.Dispose();
                if (result.Failure != 0) break;
            }
            for (int i = 0; i < auxPos.Length; i++) result.Aux.Add(auxPos[i]);

            if (result.Failure == 0)
            {
                int expected = n + 2 * auxPos.Length - 2;
                if (result.Triangles.Length / 3 != expected) result.Failure = 2;
            }

            lu.Dispose(); lv.Dispose(); lp.Dispose();
            forbidden.Dispose(); subStart.Dispose(); subNodes.Dispose(); path.Dispose(); at.Dispose(); renameNext.Dispose();
            lastVisit.Dispose(); finalId.Dispose(); originalPos.Dispose(); chainPosList.Dispose(); chainStart.Dispose();
            refined.Dispose(); keep.Dispose(); events.Dispose(); seen.Dispose(); auxPos.Dispose(); auxU.Dispose(); auxV.Dispose();
        }

        private static void AddEvent(NativeList<Ev> events, NativeHashSet<long> seen, ref int order, int segment, double t, int node)
        {
            long key = ((long)segment << 32) | (uint)node;
            if (!seen.Add(key)) return;
            events.Add(new Ev { Segment = segment, T = t, Node = node, Order = order++ });
        }

        private static void Contact(in Context c, NativeArray<int> nodes, int n, NativeList<Ev> events, NativeHashSet<long> seen,
                                    ref int order, int other, int p0, int p1, float eps)
        {
            int o0 = nodes[other], o1 = nodes[(other + 1) % n];
            for (int which = 0; which < 2; which++)
            {
                int p = which == 0 ? p0 : p1;
                if (p == (other + n - 1) % n || p == (other + 2) % n) continue;
                double d = PointSegment(c, nodes[p], o0, o1, out double t);
                if (d <= Math.Max(eps, 0f)) AddEvent(events, seen, ref order, other, t, p);
            }
        }

        private static int Duplicate(in Context c, NativeArray<int> nodes, int n, NativeList<F3> auxPos,
                                     NativeList<float> auxU, NativeList<float> auxV, int q)
        {
            int id = n + auxPos.Length;
            auxPos.Add(q < n ? c.Pos[nodes[q]] : auxPos[q - n]);
            auxU.Add(q < n ? c.U[nodes[q]] : auxU[q - n]);
            auxV.Add(q < n ? c.V[nodes[q]] : auxV[q - n]);
            return id;
        }
    }
}
