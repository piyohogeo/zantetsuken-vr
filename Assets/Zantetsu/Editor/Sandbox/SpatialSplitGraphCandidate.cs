using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>What the candidate method came to.</summary>
    internal enum CandidateVerdict
    {
        /// <summary>A limit (time, cut attempts) stopped the search, or nothing was decided yet; nothing is said.</summary>
        Undecided = 0,

        /// <summary>A placement was found: every pair across its sides was certified apart by more than 2m (double, with error bounds).</summary>
        Separable,

        /// <summary>The definite contacts alone put every convex, or every triangle, in one component: no placement exists.</summary>
        NotSeparable,

        /// <summary>This approximation found no placement; nothing is said of whether one exists.</summary>
        NotFound,

        InvalidInput,
    }

    /// <summary>The stages the candidate method may use, cumulatively: A (components and boxes), B (hulls and GJK), C (BVH).</summary>
    internal enum CandidateStages { A = 0, AB = 1, ABC = 2 }

    internal sealed class CandidateSettings
    {
        public CandidateStages stages = CandidateStages.ABC;
        public double secondsLimit = 120.0;
        public int gjkMaxIterations = 64;
        public int maxCutAttempts = 8;
        public long maxLeafTests = 50_000_000;

        /// <summary>Tests only: a hold (busy wait) inside the conclusion, after its entry check of the deadline, so that the
        /// deadline passes while the conclusion is being computed. 0 in use.</summary>
        internal double testHoldSecondsInConclude;
    }

    internal sealed class CandidateResult
    {
        public CandidateVerdict verdict;
        public string reason = "";
        public string stage = "";
        public int convexCount, triangleCount, meshComponents, definiteComponents;
        public bool[] convexPositive, trianglePositive;
        public int positiveConvexes, positiveTriangles, negativeConvexes, negativeTriangles;

        // Stage A: the pairs of nodes (convexes and mesh components), those apart by their boxes, the candidates left.
        public long nodePairs, pairsApartByBoxes, candidatesA;

        // Stage B: candidates tried against the hulls, refuted, kept (the hulls meet, or GJK did not settle); the work.
        public long triedB, refutedB, ambiguousB, gjkIterations, supportQueries, vertexScans;

        // Stage C: candidates tried with the BVHs, refuted, confirmed as contacts, kept ambiguous; the work.
        public long triedC, refutedC, confirmedC, ambiguousC, limitC, bvhBuiltTriangles, bvhNodesVisited, leafTests, leafAmbiguous;
        public int cutAttempts;
        public bool cutLimitHit, timedOut;

        /// <summary>Why a NotFound or Undecided stopped: "time", "cut-limit", "leaf-limit" (a limit; the first that applies),
        /// "ambiguous" (candidates left that no certificate settled: the approximation's limit), "hulls" (stage B ended with
        /// candidates the hulls could not refute), "exhausted" (no candidate left to try and no deal); "" otherwise.</summary>
        public string stop = "";
        public double secondsPrepare, secondsA, secondsB, secondsC, seconds;
        public long peakManagedBytes;
    }

    /// <summary>
    /// The candidate method for the product (TL 2026-10-22): the connection graph of the additional split's elements,
    /// in double with the same error bounds the reference decider uses -- a pair is put apart only on a certificate
    /// (a box gap, a projection gap along an axis, a feature's lower bound), joined only on a certificate (vertices near,
    /// a vertex inside a volume by the face's own error bound, points on features near, a segment crossing a face by
    /// its margins), and left a candidate otherwise. It never cuts, copies or drops an element, treats a convex as a
    /// closed volume and a mesh as its triangles, and returns a placement only when every pair across the sides was
    /// certified apart by more than 2m: sound, never complete. "NotSeparable" is said only from definite contacts.
    /// <para>
    /// **A.** Triangles sharing a vertex index are joined for sure (one mesh component each); the nodes are the convexes
    /// and the mesh components, each with its box; node pairs whose boxes are apart by more than 2m + 4E1 are apart,
    /// the rest candidates. Definite contacts alone putting every convex or every triangle in one component end it as
    /// NotSeparable. If the graph of definite contacts and candidates already deals to two sides, done.
    /// **B.** A candidate is refuted through the hulls of its two vertex sets: GJK (support points by a scan of the
    /// vertices) gives a direction, and the projection gap of all vertices of both along it, over 2m + 4E1, is the
    /// certificate (the GJK distance itself is never used). Hulls that meet, or an unsettled GJK, leave the candidate.
    /// **C.** A BVH over each mesh component's triangles (a node's box holds its triangles whole); a candidate is walked
    /// down to the pairs of leaves whose boxes come within 2m + 4E1, each judged in double: a contact certified ends the
    /// candidate as a definite contact (the two components merge), a pair certified apart is passed, an ambiguous pair
    /// keeps the candidate.
    /// **The cuts.** Candidates are refuted where it helps: a minimum cut (unit capacity a candidate, none for a
    /// contact or an ambiguous pair) between two seed sides that each hold a convex and a triangle; its edges are tried
    /// in the order of their box gaps, largest first; a single edge whose removal separates is the first cut. A
    /// confirmed contact merges the components and the cut is sought again; an ambiguous edge is never cut again.
    /// </para>
    /// </summary>
    internal static class SpatialSplitGraphCandidate
    {
        public const string Version = "graph-candidate 3 (2026-10-22, uncertain faces kept on triangles and convexes, limits apart, the time before every conclusion and the return)";

        // ---- the scene in double -----------------------------------------------------------------------------------------

        private sealed class Face
        {
            public int[] loop;
            public double3 n;          // unit, outward (a convex) or either way (a triangle)
            public double offset;      // n . v0
            public double dirError;    // the bound on the normal's direction error (+inf: unusable for a half-space certificate)
            public double3 lo, hi;
        }

        private sealed class Body
        {
            public double3[] V;
            public Face[] Faces = Array.Empty<Face>();
            public (int a, int b)[] Edges = Array.Empty<(int, int)>();
            public bool Solid;

            /// <summary>A triangle whose normal could not be certified in double: its area may or may not be zero, so no
            /// feature-wise certificate of "apart" is made with it (only the boxes and the axes); "near" certificates stand.</summary>
            public bool Uncertain;
            public double3 lo, hi;
        }

        private struct Bounds
        {
            public double m2, e1, e2, far, near, u;
        }

        private sealed class Node
        {
            public int index;
            public bool isConvex;
            public int convex = -1;
            public List<int> triangles = new List<int>();
            public double3 lo, hi;
            public double3[] hull;        // the vertices (a convex's, or a mesh component's distinct ones)
            public Bvh bvh;
        }

        private sealed class Bvh
        {
            public double3[] lo, hi;
            public int[] left, right, triangle;   // triangle >= 0 at a leaf
            public int root;
        }

        private sealed class UnionFind
        {
            private readonly int[] _p;
            public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
            public int Find(int i) { while (_p[i] != i) { _p[i] = _p[_p[i]]; i = _p[i]; } return i; }
            public bool Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return false; _p[Math.Max(a, b)] = Math.Min(a, b); return true; }
        }

        // ---- the decision -------------------------------------------------------------------------------------------------

        internal static CandidateResult Decide(SpatialSplitSample sample, CandidateSettings settings)
        {
            long begin = Stopwatch.GetTimestamp();
            var r = new CandidateResult();
            long peak = GC.GetTotalMemory(false);
            try
            {
                DecideInto(sample, settings, begin, r, ref peak);
            }
            catch (ArgumentException e)
            {
                r.verdict = CandidateVerdict.InvalidInput;
                r.reason = e.Message;
            }
            catch (OutOfMemoryException)
            {
                r.verdict = CandidateVerdict.Undecided;
                r.reason = "out of memory";
            }

            // The deadline, right before the return, whatever was concluded (TL review 4, 2026-10-22): a soft limit -- no
            // step is interrupted -- but a decision made after the deadline is never returned as a verdict.
            if (r.verdict != CandidateVerdict.InvalidInput && settings.secondsLimit > 0.0 && Since(begin) > settings.secondsLimit)
            {
                r.timedOut = true;
                r.verdict = CandidateVerdict.Undecided;
                r.stop = "time";
                r.reason = "the time limit passed before the return (stage " + r.stage + ")";
                r.convexPositive = null;
                r.trianglePositive = null;
            }

            r.seconds = Since(begin);
            r.peakManagedBytes = Math.Max(peak, GC.GetTotalMemory(false));
            return r;
        }

        private static double Since(long begin) => (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;

        private sealed class State
        {
            public Body[] triangles, convexes;
            public Node[] nodes;
            public UnionFind definite;                 // over the nodes
            public Dictionary<(int, int), byte> candidates = new Dictionary<(int, int), byte>();   // 1 candidate, 2 ambiguous (never cut)
            public Dictionary<(int, int), double> gaps = new Dictionary<(int, int), double>();
            public Bounds b;
            public CandidateSettings settings;
            public CandidateResult r;
            public long deadline;

            // True once the deadline passed; set by the checks inside the stages, read where a decision is made.
            public bool TimeUp()
            {
                if (r.timedOut) return true;
                if (Stopwatch.GetTimestamp() > deadline)
                {
                    r.timedOut = true;
                    r.verdict = CandidateVerdict.Undecided;
                    r.reason = "the time limit passed at stage " + r.stage;
                    r.stop = "time";
                }

                return r.timedOut;
            }
        }

        private static void DecideInto(SpatialSplitSample sample, CandidateSettings settings, long begin, CandidateResult r, ref long peak)
        {
            long t0 = Stopwatch.GetTimestamp();
            var s = new State { settings = settings, r = r, deadline = settings.secondsLimit > 0.0 ? begin + (long)(settings.secondsLimit * Stopwatch.Frequency) : long.MaxValue };
            BuildScene(sample, s);
            int convexCount = s.convexes.Length, triangleCount = s.triangles.Length;
            r.convexCount = convexCount;
            r.triangleCount = triangleCount;

            // Definite contacts among the triangles: a shared vertex index.
            var byTriangle = new UnionFind(triangleCount);
            var firstOf = new Dictionary<uint, int>();
            for (int k = 0; k < triangleCount; k++)
            {
                for (int c = 0; c < 3; c++)
                {
                    uint id = sample.indices[(3 * k) + c];
                    if (firstOf.TryGetValue(id, out int other)) byTriangle.Union(k, other);
                    else firstOf[id] = k;
                }
            }

            // The nodes: convexes, then mesh components.
            var nodes = new List<Node>();
            for (int c = 0; c < convexCount; c++)
            {
                nodes.Add(new Node { index = nodes.Count, isConvex = true, convex = c, lo = s.convexes[c].lo, hi = s.convexes[c].hi, hull = s.convexes[c].V });
            }

            var componentNode = new Dictionary<int, Node>();
            for (int k = 0; k < triangleCount; k++)
            {
                int root = byTriangle.Find(k);
                if (!componentNode.TryGetValue(root, out Node node))
                {
                    node = new Node { index = nodes.Count, lo = new double3(double.PositiveInfinity), hi = new double3(double.NegativeInfinity) };
                    componentNode[root] = node;
                    nodes.Add(node);
                }

                node.triangles.Add(k);
                node.lo = math.min(node.lo, s.triangles[k].lo);
                node.hi = math.max(node.hi, s.triangles[k].hi);
            }

            foreach (Node node in nodes)
            {
                if (node.isConvex) continue;
                var seen = new HashSet<uint>();
                var hull = new List<double3>();
                foreach (int k in node.triangles)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        if (seen.Add(sample.indices[(3 * k) + c])) hull.Add(s.triangles[k].V[c]);
                    }
                }

                node.hull = hull.ToArray();
            }

            s.nodes = nodes.ToArray();
            s.definite = new UnionFind(s.nodes.Length);
            r.meshComponents = s.nodes.Length - convexCount;
            r.secondsPrepare = Since(t0);

            // ---- A: the boxes.
            long tA = Stopwatch.GetTimestamp();
            int n = s.nodes.Length;
            r.nodePairs = (long)n * (n - 1) / 2;
            r.stage = "A";
            for (int i = 0; i < n; i++)
            {
                if ((i & 63) == 0 && s.TimeUp()) return;
                for (int j = i + 1; j < n; j++)
                {
                    double gap = BoxGap(s.nodes[i].lo, s.nodes[i].hi, s.nodes[j].lo, s.nodes[j].hi);
                    if (gap > s.b.far)
                    {
                        r.pairsApartByBoxes++;
                        continue;
                    }

                    s.candidates[(i, j)] = 1;
                    s.gaps[(i, j)] = gap;
                }
            }

            r.candidatesA = s.candidates.Count;
            r.secondsA = Since(tA);
            if (s.TimeUp()) return;
            if (Conclude(s, out bool concluded) && concluded) return;

            // ---- B: the hulls.
            if (settings.stages >= CandidateStages.AB)
            {
                long tB = Stopwatch.GetTimestamp();
                r.stage = "B";
                bool done = Search(s, (key, node1, node2) => RefuteByHulls(s, node1, node2), true);
                r.secondsB = Since(tB);
                if (done) return;
            }

            // ---- C: the BVHs.
            if (settings.stages >= CandidateStages.ABC)
            {
                long tC = Stopwatch.GetTimestamp();
                r.stage = "C";
                foreach ((int, int) key in new List<(int, int)>(s.candidates.Keys)) s.candidates[key] = 1;   // the hulls' ambiguity is not the leaves'

                bool done = Search(s, (key, node1, node2) => RefuteByLeaves(s, node1, node2), false);
                r.secondsC = Since(tC);
                if (done) return;
            }

            if (r.timedOut) return;
            r.verdict = CandidateVerdict.NotFound;
            int ambiguous = Count(s, 2), limited = Count(s, 3);
            r.stop = r.cutLimitHit ? "cut-limit" : limited > 0 ? "leaf-limit" : r.stage == "B" ? "hulls" : ambiguous > 0 ? "ambiguous" : "exhausted";
            r.reason = "no cut of the candidates could be certified at stage " + r.stage + " (" + Count(s, 1) + " candidates, " + ambiguous + " ambiguous, " + limited + " at a limit; stop: " + r.stop + ")";
            _ = peak;
        }

        private static int Count(State s, byte kind)
        {
            int n = 0;
            foreach (byte v in s.candidates.Values) if (v == kind) n++;
            return n;
        }

        // ---- the graph: components, the deal, the cuts -------------------------------------------------------------------

        // The components of the graph of definite contacts and the candidates left; whether they deal to two sides. True
        // with concluded set when the decision is made (Separable, or NotSeparable from the definite contacts alone).
        private static bool Conclude(State s, out bool concluded)
        {
            CandidateResult r = s.r;
            int n = s.nodes.Length;
            concluded = false;

            // The time, right before any conclusion (TL review 3, 2026-10-22): a deadline passed is Undecided, never a verdict.
            if (s.TimeUp())
            {
                concluded = true;
                return true;
            }

            if (s.settings.testHoldSecondsInConclude > 0.0)
            {
                long until = Stopwatch.GetTimestamp() + (long)(s.settings.testHoldSecondsInConclude * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < until) { }
            }

            // NotSeparable from the definite contacts alone.
            var byDefinite = new Dictionary<int, (int c, int t)>();
            for (int i = 0; i < n; i++)
            {
                int root = s.definite.Find(i);
                byDefinite.TryGetValue(root, out (int c, int t) have);
                byDefinite[root] = (have.c + (s.nodes[i].isConvex ? 1 : 0), have.t + (s.nodes[i].isConvex ? 0 : s.nodes[i].triangles.Count));
            }

            r.definiteComponents = byDefinite.Count;
            int allC = 0, allT = 0;
            foreach ((int c, int t) in byDefinite.Values)
            {
                if (c == r.convexCount && r.convexCount > 0) allC++;
                if (t == r.triangleCount && r.triangleCount > 0) allT++;
            }

            if (allC > 0 || allT > 0 || r.convexCount < 2 || r.triangleCount < 2)
            {
                r.verdict = CandidateVerdict.NotSeparable;
                r.reason = allC > 0 ? "every convex is in one component of definite contacts" : allT > 0 ? "every triangle is in one component of definite contacts"
                    : "fewer than two convexes or two triangles";
                concluded = true;
                return true;
            }

            // The conservative graph: definite contacts and every candidate left.
            var graph = new UnionFind(n);
            for (int i = 0; i < n; i++) graph.Union(i, s.definite.Find(i));
            foreach ((int a, int b) in s.candidates.Keys) graph.Union(a, b);
            if (TryDeal(s, i => graph.Find(i), out bool[] positive))
            {
                Place(s, positive);
                concluded = true;
                return true;
            }

            return false;
        }

        // Whether the components (by the key given) deal to two sides each with a convex and a triangle; the first full
        // component alone positive where there are two, else the first full with the rest, else a convex-only and a
        // triangle-only (deterministic in the nodes' order).
        private static bool TryDeal(State s, Func<int, int> component, out bool[] positive)
        {
            int n = s.nodes.Length;
            var order = new List<int>();
            var hasC = new Dictionary<int, bool>();
            var hasT = new Dictionary<int, bool>();
            for (int i = 0; i < n; i++)
            {
                int k = component(i);
                if (!hasC.ContainsKey(k))
                {
                    order.Add(k);
                    hasC[k] = false;
                    hasT[k] = false;
                }

                if (s.nodes[i].isConvex) hasC[k] = true; else hasT[k] = true;
            }

            int firstFull = -1, fulls = 0, firstC = -1, cs = 0, firstT = -1, ts = 0;
            foreach (int k in order)
            {
                if (hasC[k] && hasT[k]) { fulls++; if (firstFull < 0) firstFull = k; }
                else if (hasC[k]) { cs++; if (firstC < 0) firstC = k; }
                else { ts++; if (firstT < 0) firstT = k; }
            }

            var chosen = new HashSet<int>();
            if (fulls >= 2 || (fulls == 1 && cs >= 1 && ts >= 1)) chosen.Add(firstFull);
            else if (fulls == 0 && cs >= 2 && ts >= 2) { chosen.Add(firstC); chosen.Add(firstT); }
            else
            {
                positive = null;
                return false;
            }

            positive = new bool[n];
            for (int i = 0; i < n; i++) positive[i] = chosen.Contains(component(i));
            return true;
        }

        private static void Place(State s, bool[] nodePositive)
        {
            CandidateResult r = s.r;
            r.verdict = CandidateVerdict.Separable;
            r.convexPositive = new bool[r.convexCount];
            r.trianglePositive = new bool[r.triangleCount];
            foreach (Node node in s.nodes)
            {
                bool side = nodePositive[node.index];
                if (node.isConvex)
                {
                    r.convexPositive[node.convex] = side;
                    if (side) r.positiveConvexes++; else r.negativeConvexes++;
                }
                else
                {
                    foreach (int k in node.triangles) r.trianglePositive[k] = side;
                    if (side) r.positiveTriangles += node.triangles.Count; else r.negativeTriangles += node.triangles.Count;
                }
            }
        }

        // Candidates are refuted along minimum cuts between seed sides until the graph deals, or the attempts run out.
        // 'refute' says: 1 apart (refuted), 2 a contact (definite), 0 ambiguous. Returns true when the decision is made.
        private static bool Search(State s, Func<(int, int), Node, Node, int> refute, bool hullStage)
        {
            CandidateResult r = s.r;
            int n = s.nodes.Length;
            var triedHere = new HashSet<(int, int)>();
            while (true)
            {
                if (s.TimeUp()) return true;
                if (r.cutAttempts >= s.settings.maxCutAttempts * (hullStage ? 1 : 2))
                {
                    r.cutLimitHit = true;
                    return false;
                }

                // The definite components, as the graph's nodes; the candidate edges between them.
                var compOf = new int[n];
                var comps = new Dictionary<int, int>();
                for (int i = 0; i < n; i++)
                {
                    int root = s.definite.Find(i);
                    if (!comps.TryGetValue(root, out int k)) { k = comps.Count; comps[root] = k; }
                    compOf[i] = k;
                }

                int m = comps.Count;
                var hasC = new bool[m];
                var hasT = new bool[m];
                for (int i = 0; i < n; i++) { if (s.nodes[i].isConvex) hasC[compOf[i]] = true; else hasT[compOf[i]] = true; }

                // Edges between components: the candidates (capacity 1) and the ambiguous ones (no cut).
                var edges = new Dictionary<(int, int), List<(int, int)>>();
                var blocked = new HashSet<(int, int)>();
                foreach (KeyValuePair<(int, int), byte> kv in s.candidates)
                {
                    int a = compOf[kv.Key.Item1], b = compOf[kv.Key.Item2];
                    if (a == b) continue;
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    if (kv.Value >= 2 || triedHere.Contains(kv.Key)) blocked.Add(key);
                    if (!edges.TryGetValue(key, out List<(int, int)> list)) edges[key] = list = new List<(int, int)>();
                    list.Add(kv.Key);
                }

                // Seed sides, from the soft components (the definite contacts and the edges this stage cannot cut, which
                // a finite cut never crosses): a soft component with both kinds, alone; a convex-only one with each of its
                // three nearest triangle-only ones by the boxes. A definite component stands for its soft one.
                var compLo = new double3[m];
                var compHi = new double3[m];
                for (int k = 0; k < m; k++) { compLo[k] = new double3(double.PositiveInfinity); compHi[k] = new double3(double.NegativeInfinity); }
                for (int i = 0; i < n; i++) { compLo[compOf[i]] = math.min(compLo[compOf[i]], s.nodes[i].lo); compHi[compOf[i]] = math.max(compHi[compOf[i]], s.nodes[i].hi); }
                var soft = new UnionFind(m);
                foreach ((int a, int b) in blocked) soft.Union(a, b);
                var softRep = new Dictionary<int, int>();
                var softHasC = new Dictionary<int, bool>();
                var softHasT = new Dictionary<int, bool>();
                var softLo = new Dictionary<int, double3>();
                var softHi = new Dictionary<int, double3>();
                for (int k = 0; k < m; k++)
                {
                    int root = soft.Find(k);
                    if (!softRep.ContainsKey(root)) { softRep[root] = k; softHasC[root] = false; softHasT[root] = false; softLo[root] = compLo[k]; softHi[root] = compHi[k]; }
                    softHasC[root] |= hasC[k];
                    softHasT[root] |= hasT[k];
                    softLo[root] = math.min(softLo[root], compLo[k]);
                    softHi[root] = math.max(softHi[root], compHi[k]);
                }

                var seeds = new List<int[]>();
                var cs = new List<int>();
                var ts = new List<int>();
                foreach (KeyValuePair<int, int> kv in softRep)
                {
                    if (softHasC[kv.Key] && softHasT[kv.Key]) seeds.Add(new[] { kv.Value });
                    else if (softHasC[kv.Key]) cs.Add(kv.Key);
                    else ts.Add(kv.Key);
                }

                cs.Sort((x, y) => softRep[x].CompareTo(softRep[y]));
                for (int i = 0; i < Math.Min(cs.Count, 3); i++)
                {
                    int c = cs[i];
                    var nearest = new List<int>(ts);
                    nearest.Sort((x, y) =>
                    {
                        int byGap = BoxGap(softLo[c], softHi[c], softLo[x], softHi[x]).CompareTo(BoxGap(softLo[c], softHi[c], softLo[y], softHi[y]));
                        return byGap != 0 ? byGap : softRep[x].CompareTo(softRep[y]);
                    });
                    for (int j = 0; j < Math.Min(nearest.Count, 3); j++) seeds.Add(new[] { softRep[c], softRep[nearest[j]] });
                }

                bool anyCut = false;
                for (int i = 0; i < seeds.Count && !anyCut; i++)
                {
                    for (int j = i + 1; j < seeds.Count && !anyCut; j++)
                    {
                        if (Overlap(seeds[i], seeds[j])) continue;
                        if (r.cutAttempts >= s.settings.maxCutAttempts * (hullStage ? 1 : 2)) { r.cutLimitHit = true; return false; }
                        r.cutAttempts++;
                        List<(int, int)> cut = MinCut(s, m, edges, blocked, seeds[i], seeds[j], out bool[] sideOfSource);
                        if (s.TimeUp()) return true;
                        if (cut == null) continue;   // no finite cut between these seeds
                        anyCut = true;

                        // The cut's candidates, largest box gap first.
                        var toRefute = new List<(int, int)>();
                        foreach ((int, int) ce in cut) toRefute.AddRange(edges[ce]);
                        toRefute.Sort((x, y) => s.gaps[y].CompareTo(s.gaps[x]));
                        bool allRefuted = true;
                        foreach ((int, int) key in toRefute)
                        {
                            if (!s.candidates.ContainsKey(key)) continue;
                            if (s.TimeUp()) return true;
                            int outcome = refute(key, s.nodes[key.Item1], s.nodes[key.Item2]);
                            if (s.TimeUp()) return true;
                            triedHere.Add(key);
                            if (outcome == 1)
                            {
                                s.candidates.Remove(key);
                            }
                            else if (outcome == 2)
                            {
                                s.candidates.Remove(key);
                                s.definite.Union(key.Item1, key.Item2);
                                allRefuted = false;
                                break;   // the components changed: the cut is sought again
                            }
                            else
                            {
                                s.candidates[key] = outcome == 3 ? (byte)3 : (byte)2;   // 3: a limit stopped the judgement
                                allRefuted = false;
                            }
                        }

                        if (allRefuted)
                        {
                            // The cut's sides are a placement: every candidate across them is refuted. Said from the graph
                            // that is left, so that the placement is the deal of its components.
                            if (Conclude(s, out bool concluded) && concluded) return true;
                            var side = new bool[n];
                            for (int k = 0; k < n; k++) side[k] = sideOfSource[compOf[k]];
                            Place(s, side);
                            return true;
                        }
                    }
                }

                if (!anyCut)
                {
                    if (hullStage) return false;   // the hulls cannot confirm a contact: nothing left to try here
                    if (r.cutAttempts >= s.settings.maxCutAttempts * 2) { r.cutLimitHit = true; return false; }
                    r.cutAttempts++;

                    // No cut to refute: settle the candidates themselves, the nearest boxes first. A contact confirmed may put
                    // every convex or every triangle in one definite component (NotSeparable); a refutation may open a cut.
                    var toSettle = new List<(int, int)>();
                    foreach (KeyValuePair<(int, int), byte> kv in s.candidates) if (kv.Value == 1 && !triedHere.Contains(kv.Key)) toSettle.Add(kv.Key);
                    if (toSettle.Count == 0) return false;
                    toSettle.Sort((x, y) => s.gaps[x].CompareTo(s.gaps[y]));
                    bool changed = false;
                    foreach ((int, int) key in toSettle)
                    {
                        if (s.TimeUp()) return true;
                        int outcome = refute(key, s.nodes[key.Item1], s.nodes[key.Item2]);
                        if (s.TimeUp()) return true;
                        triedHere.Add(key);
                        if (outcome == 1)
                        {
                            s.candidates.Remove(key);
                            changed = true;
                        }
                        else if (outcome == 2)
                        {
                            s.candidates.Remove(key);
                            s.definite.Union(key.Item1, key.Item2);
                            changed = true;
                            if (Conclude(s, out bool settled) && settled) return true;
                        }
                        else
                        {
                            s.candidates[key] = outcome == 3 ? (byte)3 : (byte)2;
                        }
                    }

                    if (!changed) return false;
                    continue;
                }

                if (Conclude(s, out bool done) && done) return true;
            }
        }

        private static bool Overlap(int[] a, int[] b)
        {
            foreach (int x in a) foreach (int y in b) if (x == y) return true;
            return false;
        }

        // A minimum s-t cut (Edmonds-Karp, unit capacities, blocked edges infinite) between the seed sets; the cut's edges,
        // or null where none is finite. sideOfSource: which components stay with the source after the cut.
        private static List<(int, int)> MinCut(State s, int m, Dictionary<(int, int), List<(int, int)>> edges, HashSet<(int, int)> blocked, int[] source, int[] sink, out bool[] sideOfSource)
        {
            const int Inf = 1 << 28;
            int nodes = m + 2;
            int src = m, dst = m + 1;
            var cap = new Dictionary<(int, int), int>();
            var adj = new List<int>[nodes];
            for (int i = 0; i < nodes; i++) adj[i] = new List<int>();
            void Add(int a, int b, int c)
            {
                if (!cap.ContainsKey((a, b))) { cap[(a, b)] = 0; adj[a].Add(b); }
                if (!cap.ContainsKey((b, a))) { cap[(b, a)] = 0; adj[b].Add(a); }
                cap[(a, b)] += c;
            }

            foreach (KeyValuePair<(int, int), List<(int, int)>> kv in edges)
            {
                int c = blocked.Contains(kv.Key) ? Inf : 1;
                Add(kv.Key.Item1, kv.Key.Item2, c);
                Add(kv.Key.Item2, kv.Key.Item1, c);
            }

            foreach (int k in source) Add(src, k, Inf);
            foreach (int k in sink) Add(k, dst, Inf);
            int flow = 0;
            while (true)
            {
                if (s.TimeUp()) { sideOfSource = null; return null; }
                var parent = new int[nodes];
                for (int i = 0; i < nodes; i++) parent[i] = -1;
                parent[src] = src;
                var queue = new Queue<int>();
                queue.Enqueue(src);
                while (queue.Count > 0 && parent[dst] < 0)
                {
                    int u = queue.Dequeue();
                    foreach (int v in adj[u])
                    {
                        if (parent[v] < 0 && cap[(u, v)] > 0) { parent[v] = u; queue.Enqueue(v); }
                    }
                }

                if (parent[dst] < 0) break;
                int push = Inf;
                for (int v = dst; v != src; v = parent[v]) push = Math.Min(push, cap[(parent[v], v)]);
                if (push >= Inf) { sideOfSource = null; return null; }
                for (int v = dst; v != src; v = parent[v]) { cap[(parent[v], v)] -= push; cap[(v, parent[v])] += push; }
                flow += push;
                if (flow >= Inf) { sideOfSource = null; return null; }
            }

            // The source side: what is reachable in the residual graph.
            var reach = new bool[nodes];
            var stack = new Stack<int>();
            stack.Push(src);
            reach[src] = true;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (int v in adj[u]) if (!reach[v] && cap[(u, v)] > 0) { reach[v] = true; stack.Push(v); }
            }

            sideOfSource = new bool[m];
            for (int k = 0; k < m; k++) sideOfSource[k] = reach[k];
            var cut = new List<(int, int)>();
            foreach ((int, int) key in edges.Keys)
            {
                if (reach[key.Item1] != reach[key.Item2]) cut.Add(key);
            }

            return cut;
        }

        // ---- B: the hulls ---------------------------------------------------------------------------------------------------

        private static int RefuteByHulls(State s, Node a, Node b)
        {
            CandidateResult r = s.r;
            r.triedB++;
            if (Gjk(s, a.hull, b.hull, out double3 direction, out bool meet))
            {
                if (!meet && ProjectionGap(direction, a.hull, b.hull, s) > s.b.far)
                {
                    r.refutedB++;
                    return 1;
                }
            }

            r.ambiguousB++;
            return 0;
        }

        // The gap between the projections of two vertex sets onto a near-unit axis (any direction gives a lower bound).
        private static double ProjectionGap(double3 axis, double3[] a, double3[] b, State s)
        {
            double aLo = double.PositiveInfinity, aHi = double.NegativeInfinity, bLo = double.PositiveInfinity, bHi = double.NegativeInfinity;
            foreach (double3 v in a) { double x = math.dot(axis, v); aLo = Math.Min(aLo, x); aHi = Math.Max(aHi, x); }
            foreach (double3 v in b) { double x = math.dot(axis, v); bLo = Math.Min(bLo, x); bHi = Math.Max(bHi, x); }
            s.r.vertexScans += a.Length + b.Length;
            return Math.Max(bLo - aHi, aLo - bHi);
        }

        private static double3 Support(double3[] v, double3 d, State s)
        {
            double best = double.NegativeInfinity;
            double3 at = v[0];
            foreach (double3 p in v)
            {
                double x = math.dot(p, d);
                if (x > best) { best = x; at = p; }
            }

            s.r.supportQueries++;
            s.r.vertexScans += v.Length;
            return at;
        }

        // GJK on the Minkowski difference A - B: the direction from B's nearest point to A's (unit), or whether the hulls
        // meet. False when it did not settle within the iterations.
        private static bool Gjk(State s, double3[] a, double3[] b, out double3 direction, out bool meet)
        {
            var simplex = new List<double3>(4);
            direction = default;
            meet = false;
            double3 d = Centroid(a) - Centroid(b);
            if (math.lengthsq(d) <= 0.0) d = new double3(1.0, 0.0, 0.0);
            double3 v = d;
            for (int it = 0; it < s.settings.gjkMaxIterations; it++)
            {
                s.r.gjkIterations++;
                double3 w = Support(a, -v, s) - Support(b, v, s);
                double vv = math.lengthsq(v);
                if (vv <= 0.0)
                {
                    meet = true;
                    return true;
                }

                // No progress: the nearest point of the difference to the origin is v (within rounding).
                if (vv - math.dot(v, w) <= 1e-12 * vv || (simplex.Count > 0 && Contains(simplex, w)))
                {
                    direction = math.normalize(v);
                    return true;
                }

                simplex.Add(w);
                if (!ClosestToOrigin(simplex, out v))
                {
                    meet = true;   // the origin is inside the tetrahedron
                    return true;
                }
            }

            return false;
        }

        private static bool Contains(List<double3> simplex, double3 w)
        {
            foreach (double3 p in simplex) if (math.all(p == w)) return true;
            return false;
        }

        private static double3 Centroid(double3[] v)
        {
            var c = new double3(0.0);
            foreach (double3 p in v) c += p;
            return c / v.Length;
        }

        // The point of the simplex nearest the origin (Ericson), the simplex reduced to the feature that holds it. False
        // where the origin is inside a tetrahedron.
        private static bool ClosestToOrigin(List<double3> simplex, out double3 v)
        {
            var o = new double3(0.0);
            switch (simplex.Count)
            {
                case 1:
                    v = simplex[0];
                    return true;
                case 2:
                {
                    v = ClosestOnSegment(o, simplex[0], simplex[1], out double t);
                    if (t <= 0.0) simplex.RemoveAt(1); else if (t >= 1.0) simplex.RemoveAt(0);
                    return true;
                }
                case 3:
                {
                    v = ClosestOnTriangle(o, simplex[0], simplex[1], simplex[2], out int keep);
                    Reduce(simplex, keep);
                    return true;
                }
                default:
                {
                    // Inside the tetrahedron when the origin is on the inner side of every face; else the nearest face.
                    bool inside = true;
                    double best = double.PositiveInfinity;
                    double3 bestV = default;
                    int bestKeep = 0;
                    int[][] faces = { new[] { 0, 1, 2 }, new[] { 0, 1, 3 }, new[] { 0, 2, 3 }, new[] { 1, 2, 3 } };
                    int[] opposite = { 3, 2, 1, 0 };
                    for (int f = 0; f < 4; f++)
                    {
                        double3 p = simplex[faces[f][0]], q = simplex[faces[f][1]], rr = simplex[faces[f][2]], other = simplex[opposite[f]];
                        double3 nrm = math.cross(q - p, rr - p);
                        double sideO = math.dot(nrm, o - p), sideOther = math.dot(nrm, other - p);
                        if (sideO * sideOther < 0.0 || sideOther == 0.0)
                        {
                            inside = false;
                            double3 cand = ClosestOnTriangle(o, p, q, rr, out int keep);
                            double l = math.lengthsq(cand);
                            if (l < best)
                            {
                                best = l;
                                bestV = cand;
                                bestKeep = KeepMap(keep, faces[f]);
                            }
                        }
                    }

                    if (inside)
                    {
                        v = o;
                        return false;
                    }

                    v = bestV;
                    Reduce(simplex, bestKeep);
                    return true;
                }
            }
        }

        // keep: a bit set of the simplex vertices the nearest feature uses (bit i: vertex i of the three given).
        private static int KeepMap(int keep, int[] face)
        {
            int mapped = 0;
            for (int i = 0; i < 3; i++) if ((keep & (1 << i)) != 0) mapped |= 1 << face[i];
            return mapped;
        }

        private static void Reduce(List<double3> simplex, int keep)
        {
            var kept = new List<double3>();
            for (int i = 0; i < simplex.Count; i++) if ((keep & (1 << i)) != 0) kept.Add(simplex[i]);
            simplex.Clear();
            simplex.AddRange(kept);
        }

        private static double3 ClosestOnSegment(double3 p, double3 a, double3 b, out double t)
        {
            double3 e = b - a;
            double l = math.lengthsq(e);
            t = l > 0.0 ? math.clamp(math.dot(p - a, e) / l, 0.0, 1.0) : 0.0;
            return a + (e * t);
        }

        // Ericson's closest point on a triangle; keep says which of a (1), b (2), c (4) the feature uses.
        private static double3 ClosestOnTriangle(double3 p, double3 a, double3 b, double3 c, out int keep)
        {
            double3 ab = b - a, ac = c - a, ap = p - a;
            double d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
            if (d1 <= 0.0 && d2 <= 0.0) { keep = 1; return a; }
            double3 bp = p - b;
            double d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
            if (d3 >= 0.0 && d4 <= d3) { keep = 2; return b; }
            double vc = (d1 * d4) - (d3 * d2);
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0) { keep = 3; return a + (ab * (d1 / (d1 - d3))); }
            double3 cp = p - c;
            double d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
            if (d6 >= 0.0 && d5 <= d6) { keep = 4; return c; }
            double vb = (d5 * d2) - (d1 * d6);
            if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0) { keep = 5; return a + (ac * (d2 / (d2 - d6))); }
            double va = (d3 * d6) - (d5 * d4);
            if (va <= 0.0 && d4 - d3 >= 0.0 && d5 - d6 >= 0.0) { keep = 6; return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))); }
            double denom = 1.0 / (va + vb + vc);
            keep = 7;
            return a + (ab * (vb * denom)) + (ac * (vc * denom));
        }

        // ---- C: the BVHs and the leaves ---------------------------------------------------------------------------------------

        private static int RefuteByLeaves(State s, Node a, Node b)
        {
            CandidateResult r = s.r;
            r.triedC++;
            int outcome;
            if (a.isConvex && b.isConvex) outcome = s.r.leafTests >= s.settings.maxLeafTests ? 3 : JudgeBodies(s, s.convexes[a.convex], s.convexes[b.convex]);
            else if (a.isConvex) outcome = ConvexAgainstMesh(s, a, b);
            else if (b.isConvex) outcome = ConvexAgainstMesh(s, b, a);
            else outcome = MeshAgainstMesh(s, a, b);
            if (outcome == 1) r.refutedC++; else if (outcome == 2) r.confirmedC++; else if (outcome == 3) r.limitC++; else r.ambiguousC++;
            return outcome;
        }

        private static Bvh BvhOf(State s, Node node)
        {
            if (node.bvh != null) return node.bvh;
            int count = node.triangles.Count;
            var bvh = new Bvh { lo = new double3[2 * count], hi = new double3[2 * count], left = new int[2 * count], right = new int[2 * count], triangle = new int[2 * count] };
            int next = 0;
            int Build(int[] items, int from, int to)
            {
                int id = next++;
                var lo = new double3(double.PositiveInfinity);
                var hi = new double3(double.NegativeInfinity);
                for (int i = from; i < to; i++)
                {
                    lo = math.min(lo, s.triangles[items[i]].lo);
                    hi = math.max(hi, s.triangles[items[i]].hi);
                }

                bvh.lo[id] = lo;
                bvh.hi[id] = hi;
                if (to - from == 1)
                {
                    bvh.triangle[id] = items[from];
                    bvh.left[id] = bvh.right[id] = -1;
                    return id;
                }

                bvh.triangle[id] = -1;
                double3 extent = hi - lo;
                int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
                Array.Sort(items, from, to - from, Comparer<int>.Create((x, y) => Centre(s.triangles[x], axis).CompareTo(Centre(s.triangles[y], axis))));
                int mid = (from + to) / 2;
                bvh.left[id] = Build(items, from, mid);
                bvh.right[id] = Build(items, mid, to);
                return id;
            }

            int[] all = node.triangles.ToArray();
            bvh.root = Build(all, 0, all.Length);
            node.bvh = bvh;
            s.r.bvhBuiltTriangles += count;
            return bvh;
        }

        private static double Centre(Body t, int axis) => 0.5 * (t.lo[axis] + t.hi[axis]);

        // 1 apart, 2 a contact, 0 ambiguous.
        private static int ConvexAgainstMesh(State s, Node convexNode, Node meshNode)
        {
            Body convex = s.convexes[convexNode.convex];
            Bvh bvh = BvhOf(s, meshNode);
            bool ambiguous = false;
            var stack = new Stack<int>();
            stack.Push(bvh.root);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if ((++s.r.bvhNodesVisited & 1023) == 0 && s.TimeUp()) return 3;
                if (BoxGap(convex.lo, convex.hi, bvh.lo[id], bvh.hi[id]) > s.b.far) continue;
                if (bvh.triangle[id] >= 0)
                {
                    if (s.r.leafTests >= s.settings.maxLeafTests) return 3;   // the leaf budget: before the work, its own outcome
                    int outcome = JudgeBodies(s, convex, s.triangles[bvh.triangle[id]]);
                    if (outcome == 2) return 2;
                    if (outcome == 0) ambiguous = true;
                    continue;
                }

                stack.Push(bvh.left[id]);
                stack.Push(bvh.right[id]);
            }

            return ambiguous ? 0 : 1;
        }

        private static int MeshAgainstMesh(State s, Node a, Node b)
        {
            Bvh ba = BvhOf(s, a), bb = BvhOf(s, b);
            bool ambiguous = false;
            var stack = new Stack<(int, int)>();
            stack.Push((ba.root, bb.root));
            while (stack.Count > 0)
            {
                (int i, int j) = stack.Pop();
                if ((++s.r.bvhNodesVisited & 1023) == 0 && s.TimeUp()) return 3;
                if (BoxGap(ba.lo[i], ba.hi[i], bb.lo[j], bb.hi[j]) > s.b.far) continue;
                bool leafI = ba.triangle[i] >= 0, leafJ = bb.triangle[j] >= 0;
                if (leafI && leafJ)
                {
                    if (s.r.leafTests >= s.settings.maxLeafTests) return 3;
                    int outcome = JudgeBodies(s, s.triangles[ba.triangle[i]], s.triangles[bb.triangle[j]]);
                    if (outcome == 2) return 2;
                    if (outcome == 0) ambiguous = true;
                    continue;
                }

                if (leafI || (!leafJ && Volume(bb, j) > Volume(ba, i)))
                {
                    stack.Push((i, bb.left[j]));
                    stack.Push((i, bb.right[j]));
                }
                else
                {
                    stack.Push((ba.left[i], j));
                    stack.Push((ba.right[i], j));
                }
            }

            return ambiguous ? 0 : 1;
        }

        private static double Volume(Bvh b, int id)
        {
            double3 e = b.hi[id] - b.lo[id];
            return e.x * e.y * e.z;
        }

        // ---- the certificates between two bodies (double) ------------------------------------------------------------------

        // 1 apart (certified), 2 a contact or nearer than 2m (certified), 0 ambiguous.
        private static int JudgeBodies(State s, Body p, Body q)
        {
            s.r.leafTests++;
            Bounds b = s.b;
            double slack = 2.0 * b.e1;

            // Apart: the boxes, or an axis.
            if (BoxGap(p.lo, p.hi, q.lo, q.hi) > b.far) return 1;
            foreach (Face f in p.Faces) if (ProjectionGapBodies(f.n, p, q) > b.far) return 1;
            foreach (Face f in q.Faces) if (ProjectionGapBodies(f.n, p, q) > b.far) return 1;

            // Near: two vertices; a vertex inside a volume by its faces' bounds; points on features.
            foreach (double3 v in p.V) foreach (double3 w in q.V) if (math.distance(v, w) < b.near) return 2;
            if (p.Solid && AVertexInsideByBounds(p, q, b)) return 2;
            if (q.Solid && AVertexInsideByBounds(q, p, b)) return 2;
            if (PointsOnFeaturesNear(p, q, b) || PointsOnFeaturesNear(q, p, b)) return 2;
            foreach ((int a1, int b1) in p.Edges)
            {
                foreach ((int a2, int b2) in q.Edges)
                {
                    if (SegmentsD(p.V[a1], p.V[b1], q.V[a2], q.V[b2]) + slack < b.m2) return 2;
                }
            }

            if (ACrossingByMargins(p, q, b) || ACrossingByMargins(q, p, b)) return 2;

            // A triangle without a certified normal may have an interior the features do not bound: nothing more is said.
            if (p.Uncertain || q.Uncertain)
            {
                s.r.leafAmbiguous++;
                return 0;
            }

            // Apart: every feature pair's lower bound (vertex to face, edge to edge), no edge crossing a face, and no
            // vertex possibly inside a volume -- for two bodies that do not meet, the least distance is between a vertex
            // and a face or two edges, so these bound the distance of the bodies from below.
            if (AllFeaturesApart(p, q, b) && AllFeaturesApart(q, p, b) && AllEdgesApart(p, q, b) && NoEdgeCrossesAFace(p, q, b) && NoEdgeCrossesAFace(q, p, b)) return 1;
            s.r.leafAmbiguous++;
            return 0;
        }

        private static double ProjectionGapBodies(double3 axis, Body p, Body q)
        {
            double pLo = double.PositiveInfinity, pHi = double.NegativeInfinity, qLo = double.PositiveInfinity, qHi = double.NegativeInfinity;
            foreach (double3 v in p.V) { double x = math.dot(axis, v); pLo = Math.Min(pLo, x); pHi = Math.Max(pHi, x); }
            foreach (double3 v in q.V) { double x = math.dot(axis, v); qLo = Math.Min(qLo, x); qHi = Math.Max(qHi, x); }
            return Math.Max(qLo - pHi, pLo - qHi);
        }

        // Inside the volume by more than each face's own bound: the normal's direction error times the reach, plus 2E1.
        private static bool AVertexInsideByBounds(Body volume, Body other, Bounds b)
        {
            foreach (double3 v in other.V)
            {
                bool inside = true;
                foreach (Face f in volume.Faces)
                {
                    if (double.IsPositiveInfinity(f.dirError)) { inside = false; break; }
                    double h = math.dot(f.n, v) - f.offset;
                    double bound = (f.dirError * math.distance(v, volume.V[f.loop[0]])) + (2.0 * b.e1);
                    if (!(h < -bound)) { inside = false; break; }
                }

                if (inside) return true;
            }

            return false;
        }

        // A vertex of p to a point on a face of q (its fan triangles, Ericson) within 2m less the slack.
        private static bool PointsOnFeaturesNear(Body p, Body q, Bounds b)
        {
            double slack = 2.0 * b.e1;
            foreach (double3 v in p.V)
            {
                foreach (Face f in q.Faces)
                {
                    double3 v0 = q.V[f.loop[0]];
                    for (int i = 1; i + 1 < f.loop.Length; i++)
                    {
                        double3 c = ClosestOnTriangle(v, v0, q.V[f.loop[i]], q.V[f.loop[i + 1]], out _);
                        if (math.distance(v, c) + slack < b.m2) return true;
                    }
                }

                if (q.Faces.Length == 0)
                {
                    foreach ((int a2, int b2) in q.Edges) if (PointSegmentD(v, q.V[a2], q.V[b2]) + slack < b.m2) return true;
                    if (q.Edges.Length == 0) foreach (double3 w in q.V) if (math.distance(v, w) + slack < b.m2) return true;
                }
            }

            return false;
        }

        // An edge of p crossing a face of q: the ends on opposite sides beyond the face's bounds, the crossing point
        // inside the polygon by the margins that its own error leaves.
        private static bool ACrossingByMargins(Body p, Body q, Bounds b)
        {
            foreach (Face f in q.Faces)
            {
                if (double.IsPositiveInfinity(f.dirError)) continue;
                double3 v0 = q.V[f.loop[0]];
                foreach ((int a, int bb) in p.Edges)
                {
                    double3 pa = p.V[a], pb = p.V[bb];
                    double ha = math.dot(f.n, pa) - f.offset, hb = math.dot(f.n, pb) - f.offset;
                    double ea = (f.dirError * math.distance(pa, v0)) + (2.0 * b.e1), eb = (f.dirError * math.distance(pb, v0)) + (2.0 * b.e1);
                    if (!((ha > ea && hb < -eb) || (ha < -ea && hb > eb))) continue;
                    double t = ha / (ha - hb);
                    double3 x = pa + ((pb - pa) * t);
                    double ex = ((ea + eb) * math.distance(pa, pb) / (Math.Abs(ha) + Math.Abs(hb))) + (2.0 * b.e1);
                    bool inside = true;
                    int positive = 0, negative = 0;
                    for (int i = 0; i < f.loop.Length && inside; i++)
                    {
                        double3 vi = q.V[f.loop[i]], vj = q.V[f.loop[(i + 1) % f.loop.Length]];
                        double side = math.dot(f.n, math.cross(vj - vi, x - vi));
                        double margin = (math.distance(vi, vj) * (ex + (f.dirError * math.distance(x, vi)))) + b.e2;
                        if (side > margin) positive++;
                        else if (side < -margin) negative++;
                        else inside = false;
                    }

                    if (inside && (positive == 0 || negative == 0)) return true;
                }
            }

            return false;
        }

        // Every vertex of p is apart from every face (or segment, point) of q by its lower bound: the plane distance less
        // the normal's direction error times the reach, or the box gap; and no vertex of p is possibly inside a solid q.
        private static bool AllFeaturesApart(Body p, Body q, Bounds b)
        {
            double slack = 2.0 * b.e1;
            foreach (double3 v in p.V)
            {
                if (q.Solid && MaybeInside(v, q, b)) return false;
                foreach (Face f in q.Faces)
                {
                    double plane = double.IsPositiveInfinity(f.dirError) ? 0.0 : Math.Abs(math.dot(f.n, v) - f.offset) - (f.dirError * math.distance(v, q.V[f.loop[0]]));
                    double lower = Math.Max(plane, PointBoxGap(v, f.lo, f.hi));
                    if (!(lower - slack > b.m2)) return false;
                }

                if (q.Faces.Length == 0)
                {
                    foreach ((int a2, int b2) in q.Edges) if (!(PointBoxGap(v, math.min(q.V[a2], q.V[b2]), math.max(q.V[a2], q.V[b2])) - slack > b.m2)) return false;
                    if (q.Edges.Length == 0) foreach (double3 w in q.V) if (!(math.distance(v, w) - slack > b.m2)) return false;
                }
            }

            return true;
        }

        // Not clearly outside any face of the volume: possibly inside.
        private static bool MaybeInside(double3 v, Body volume, Bounds b)
        {
            foreach (Face f in volume.Faces)
            {
                if (double.IsPositiveInfinity(f.dirError)) continue;
                double h = math.dot(f.n, v) - f.offset;
                double bound = (f.dirError * math.distance(v, volume.V[f.loop[0]])) + (2.0 * b.e1);
                if (h > bound) return false;
            }

            return true;
        }

        // Every edge of p has both ends clearly on one side of every face of q (no crossing), or keeps clear of the face's
        // box by more than 2m.
        private static bool NoEdgeCrossesAFace(Body p, Body q, Bounds b)
        {
            double slack = 2.0 * b.e1;
            foreach (Face f in q.Faces)
            {
                double3 v0 = q.V[f.loop[0]];
                foreach ((int a, int bb) in p.Edges)
                {
                    double3 pa = p.V[a], pb = p.V[bb];
                    if (BoxGap(math.min(pa, pb), math.max(pa, pb), f.lo, f.hi) - slack > b.m2) continue;
                    if (double.IsPositiveInfinity(f.dirError)) return false;
                    double ha = math.dot(f.n, pa) - f.offset, hb = math.dot(f.n, pb) - f.offset;
                    double ea = (f.dirError * math.distance(pa, v0)) + slack, eb = (f.dirError * math.distance(pb, v0)) + slack;
                    if (!((ha > ea && hb > eb) || (ha < -ea && hb < -eb))) return false;
                }
            }

            return true;
        }

        private static bool AllEdgesApart(Body p, Body q, Bounds b)
        {
            double slack = 2.0 * b.e1;
            foreach ((int a1, int b1) in p.Edges)
            {
                foreach ((int a2, int b2) in q.Edges)
                {
                    if (!(BoxGap(math.min(p.V[a1], p.V[b1]), math.max(p.V[a1], p.V[b1]), math.min(q.V[a2], q.V[b2]), math.max(q.V[a2], q.V[b2])) - slack > b.m2)) return false;
                }
            }

            return true;
        }

        private static double BoxGap(double3 lo1, double3 hi1, double3 lo2, double3 hi2) => math.length(math.max(math.max(lo2 - hi1, lo1 - hi2), 0.0));

        private static double PointBoxGap(double3 p, double3 lo, double3 hi) => math.length(math.max(math.max(lo - p, p - hi), 0.0));

        private static double PointSegmentD(double3 p, double3 a, double3 b)
        {
            double3 e = b - a, w = p - a;
            double l = math.lengthsq(e);
            if (l <= 0.0) return math.length(w);
            double t = math.clamp(math.dot(w, e) / l, 0.0, 1.0);
            return math.distance(p, a + (e * t));
        }

        private static double SegmentsD(double3 p1, double3 q1, double3 p2, double3 q2)
        {
            double3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = math.dot(d1, d1), e = math.dot(d2, d2), f = math.dot(d2, r);
            double s, t;
            if (a <= 0.0 && e <= 0.0) return math.distance(p1, p2);
            if (a <= 0.0) { s = 0.0; t = math.clamp(f / e, 0.0, 1.0); }
            else
            {
                double c = math.dot(d1, r);
                if (e <= 0.0) { t = 0.0; s = math.clamp(-c / a, 0.0, 1.0); }
                else
                {
                    double bb = math.dot(d1, d2), denom = (a * e) - (bb * bb);
                    s = denom > 0.0 ? math.clamp(((bb * f) - (c * e)) / denom, 0.0, 1.0) : 0.0;
                    t = ((bb * s) + f) / e;
                    if (t < 0.0) { t = 0.0; s = math.clamp(-c / a, 0.0, 1.0); }
                    else if (t > 1.0) { t = 1.0; s = math.clamp((bb - c) / a, 0.0, 1.0); }
                }
            }

            return math.distance(p1 + (d1 * s), p2 + (d2 * t));
        }

        // ---- the scene ----------------------------------------------------------------------------------------------------------

        private static void BuildScene(SpatialSplitSample sample, State s)
        {
            if (sample == null) throw new ArgumentException("no sample");
            if (!math.isfinite(sample.margin) || sample.margin <= 0f) throw new ArgumentException("the margin is " + sample.margin.ToString("R", CultureInfo.InvariantCulture));
            if (!SpatialSplitReferenceDecider.Finite(in sample.geometryToOwner) || !SpatialSplitReferenceDecider.Finite(in sample.shapeToOwner)) throw new ArgumentException("a transform is not finite");
            if (sample.indices.Length % 3 != 0) throw new ArgumentException("the index count is not a multiple of three");
            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != vertexBytes) throw new ArgumentException("the vertex is " + sample.vertexStructBytes + " bytes, this build's " + vertexBytes);
            int named = sample.vertexIds.Length;
            if (sample.vertexBytes.Length != named * vertexBytes) throw new ArgumentException("the vertex bytes are not " + named + " vertices");
            var local = new float3[named];
            int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            for (int i = 0; i < named; i++)
            {
                int at = (i * vertexBytes) + at0;
                local[i] = new float3(BitConverter.ToSingle(sample.vertexBytes, at), BitConverter.ToSingle(sample.vertexBytes, at + 4), BitConverter.ToSingle(sample.vertexBytes, at + 8));
                if (!math.all(math.isfinite(local[i]))) throw new ArgumentException("vertex " + sample.vertexIds[i] + " is not finite");
            }

            var owner = new double3[named];
            for (int i = 0; i < named; i++) owner[i] = SpatialSplitReferenceDecider.OwnerDouble(local[i], in sample.geometryToOwner);
            double bound = 1.0;
            foreach (double3 v in owner) bound = Math.Max(bound, math.cmax(math.abs(v)));
            bound = Math.Max(bound, SpatialSplitReferenceDecider.TermBound(local, in sample.geometryToOwner));
            bound = Math.Max(bound, SpatialSplitReferenceDecider.ConvexTermBound(sample, in sample.shapeToOwner));
            var b = new Bounds { m2 = 2.0 * sample.margin, u = bound };
            b.e1 = bound * Math.Pow(2.0, -44);
            b.e2 = bound * bound * Math.Pow(2.0, -40);
            b.far = b.m2 + (4.0 * b.e1);
            b.near = b.m2 - (4.0 * b.e1);

            int triangleCount = sample.indices.Length / 3;
            s.triangles = new Body[triangleCount];
            for (int k = 0; k < triangleCount; k++)
            {
                var v = new double3[3];
                for (int c = 0; c < 3; c++)
                {
                    uint id = sample.indices[(3 * k) + c];
                    int at = id < (uint)Math.Max(0, sample.vertexViewLength) ? Array.BinarySearch(sample.vertexIds, id) : -1;
                    if (at < 0) throw new ArgumentException("index " + ((3 * k) + c) + " names vertex " + id + ", which the sample does not hold");
                    v[c] = owner[at];
                }

                s.triangles[k] = TriangleBody(v, b);
            }

            s.convexes = new Body[sample.convexes.Length];
            for (int c = 0; c < sample.convexes.Length; c++)
            {
                SpatialSplitSample.Convex convex = sample.convexes[c];
                foreach (float3 p in convex.vertices) if (!math.all(math.isfinite(p))) throw new ArgumentException("a convex vertex is not finite");
                var v = new double3[convex.vertices.Length];
                for (int i = 0; i < v.Length; i++) v[i] = SpatialSplitReferenceDecider.OwnerDouble(convex.vertices[i], in sample.shapeToOwner);
                s.convexes[c] = ConvexBody(v, convex, b, c);
            }

            s.b = b;
        }

        private static Body TriangleBody(double3[] v, Bounds b)
        {
            var body = new Body { V = v, lo = math.min(v[0], math.min(v[1], v[2])), hi = math.max(v[0], math.max(v[1], v[2])) };
            Face f = FaceOf(v, new[] { 0, 1, 2 }, b, null);
            body.Faces = new[] { f };
            body.Edges = new[] { (0, 1), (1, 2), (2, 0) };

            // No normal certifiable in double (no area, or too thin to trust): the triangle is kept as it is -- its vertices and
            // edges carry the "near" certificates, and the body is never put apart by its features (TL review 2026-10-22,
            // P1: "not certifiable" is not "degenerate"; a small coplanar triangle inside another was put apart by its edges).
            body.Uncertain = double.IsPositiveInfinity(f.dirError);
            return body;
        }

        // A face's unit normal from the differences to its first vertex (Newell), with the bound on its direction error:
        // each term's error is within 2^-46 U times the loop's reach; a normal not clearly longer than that is unusable
        // as a half-space (dirError infinite), and a triangle without one has no face.
        private static Face FaceOf(double3[] v, int[] loop, Bounds b, double3? outwardHint)
        {
            double3 v0 = v[loop[0]];
            var n = new double3(0.0);
            double reach = 0.0;
            for (int i = 1; i + 1 < loop.Length; i++)
            {
                double3 e1 = v[loop[i]] - v0, e2 = v[loop[i + 1]] - v0;
                n += math.cross(e1, e2);
                reach += math.length(e1) + math.length(e2);
            }

            double cN = Math.Pow(2.0, -46) * b.u * Math.Max(reach, 1.0);
            double l = math.length(n);
            bool certified = l > 2.0 * cN;

            // Not certifiable (too short against its own error bound): the face is kept with an infinite direction error --
            // its polygon still carries the "near" certificates and its box the lower bounds; its normal is at most an axis
            // (any unit axis gives a valid projection gap) and never a half-space. A zero normal gives no axis at all.
            var f = new Face { loop = loop, n = l > 0.0 ? n / l : new double3(0.0), dirError = certified ? cN / (l - cN) : double.PositiveInfinity };
            if (outwardHint.HasValue && math.dot(f.n, outwardHint.Value) > 0.0) f.n = -f.n;
            f.offset = math.dot(f.n, v0);
            f.lo = new double3(double.PositiveInfinity);
            f.hi = new double3(double.NegativeInfinity);
            foreach (int i in loop) { f.lo = math.min(f.lo, v[i]); f.hi = math.max(f.hi, v[i]); }
            return f;
        }

        private static Body ConvexBody(double3[] v, SpatialSplitSample.Convex convex, Bounds b, int index)
        {
            int faceCount = convex.faceOffsets.Length - 1;
            if (v.Length < 4 || faceCount < 4) throw new ArgumentException("convex " + index + " of " + v.Length + " vertices and " + faceCount + " faces");
            var body = new Body { V = v, Solid = true, lo = new double3(double.PositiveInfinity), hi = new double3(double.NegativeInfinity) };
            foreach (double3 p in v) { body.lo = math.min(body.lo, p); body.hi = math.max(body.hi, p); }
            var centroid = new double3(0.0);
            foreach (double3 p in v) centroid += p;
            centroid /= v.Length;
            var faces = new List<Face>();
            var edges = new HashSet<(int, int)>();
            for (int f = 0; f < faceCount; f++)
            {
                int from = convex.faceOffsets[f], to = convex.faceOffsets[f + 1];
                if (to - from < 3 || from < 0 || to > convex.faceIndices.Length) throw new ArgumentException("convex " + index + ": face " + f + " of " + (to - from) + " vertices");
                var loop = new int[to - from];
                for (int i = 0; i < loop.Length; i++)
                {
                    loop[i] = convex.faceIndices[from + i];
                    if (loop[i] < 0 || loop[i] >= v.Length) throw new ArgumentException("convex " + index + ": face " + f + " names vertex " + loop[i]);
                    int a = loop[i], bb = convex.faceIndices[from + ((i + 1) % loop.Length)];
                    if (a != bb) edges.Add((Math.Min(a, bb), Math.Max(a, bb)));
                }

                // Outward: away from the centroid (the direction is only used where the face's bound allows).
                // A face whose normal cannot be certified in double is kept (TL review 3, 2026-10-22: a box of edge 2^-24 is a
                // valid convex); the body is then "uncertain": the boxes and the hulls still work on it, the leaves say nothing.
                Face face = FaceOf(v, loop, b, centroid - v[loop[0]]);
                if (double.IsPositiveInfinity(face.dirError)) body.Uncertain = true;
                faces.Add(face);
            }

            body.Faces = faces.ToArray();
            body.Edges = new (int, int)[edges.Count];
            int k = 0;
            foreach ((int a, int bb) in edges) body.Edges[k++] = (a, bb);
            Array.Sort(body.Edges);
            return body;
        }

        // ---- the line --------------------------------------------------------------------------------------------------------------

        internal static string Json(CandidateResult r, string identity, string extra)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var line = new StringBuilder(256);
            line.Append('{').Append(identity)
                .Append(",\"candidate\":").Append(SpatialSplitCaptureSink.Json(Version))
                .Append(",\"verdict\":").Append(SpatialSplitCaptureSink.Json(r.verdict.ToString())).Append(",\"reason\":").Append(SpatialSplitCaptureSink.Json(r.reason))
                .Append(",\"stage\":").Append(SpatialSplitCaptureSink.Json(r.stage)).Append(",\"stop\":").Append(SpatialSplitCaptureSink.Json(r.stop))
                .Append(",\"cutLimitHit\":").Append(r.cutLimitHit ? "true" : "false").Append(",\"timedOut\":").Append(r.timedOut ? "true" : "false").Append(",\"limitC\":").Append(r.limitC)
                .Append(",\"convexes\":").Append(r.convexCount).Append(",\"triangles\":").Append(r.triangleCount)
                .Append(",\"meshComponents\":").Append(r.meshComponents).Append(",\"definiteComponents\":").Append(r.definiteComponents)
                .Append(",\"positiveConvexes\":").Append(r.positiveConvexes).Append(",\"positiveTriangles\":").Append(r.positiveTriangles)
                .Append(",\"negativeConvexes\":").Append(r.negativeConvexes).Append(",\"negativeTriangles\":").Append(r.negativeTriangles)
                .Append(",\"convexSides\":").Append(SpatialSplitCaptureSink.Json(Sides(r.convexPositive)))
                .Append(",\"triangleSides\":").Append(SpatialSplitCaptureSink.Json(Sides(r.trianglePositive)))
                .Append(",\"nodePairs\":").Append(r.nodePairs).Append(",\"pairsApartByBoxes\":").Append(r.pairsApartByBoxes).Append(",\"candidatesA\":").Append(r.candidatesA)
                .Append(",\"triedB\":").Append(r.triedB).Append(",\"refutedB\":").Append(r.refutedB).Append(",\"ambiguousB\":").Append(r.ambiguousB)
                .Append(",\"gjkIterations\":").Append(r.gjkIterations).Append(",\"supportQueries\":").Append(r.supportQueries).Append(",\"vertexScans\":").Append(r.vertexScans)
                .Append(",\"triedC\":").Append(r.triedC).Append(",\"refutedC\":").Append(r.refutedC).Append(",\"confirmedC\":").Append(r.confirmedC).Append(",\"ambiguousC\":").Append(r.ambiguousC)
                .Append(",\"bvhBuiltTriangles\":").Append(r.bvhBuiltTriangles).Append(",\"bvhNodesVisited\":").Append(r.bvhNodesVisited)
                .Append(",\"leafTests\":").Append(r.leafTests).Append(",\"leafAmbiguous\":").Append(r.leafAmbiguous).Append(",\"cutAttempts\":").Append(r.cutAttempts)
                .Append(",\"secondsPrepare\":").Append(r.secondsPrepare.ToString("R", inv)).Append(",\"secondsA\":").Append(r.secondsA.ToString("R", inv))
                .Append(",\"secondsB\":").Append(r.secondsB.ToString("R", inv)).Append(",\"secondsC\":").Append(r.secondsC.ToString("R", inv))
                .Append(",\"seconds\":").Append(r.seconds.ToString("R", inv)).Append(",\"peakManagedBytes\":").Append(r.peakManagedBytes);
            if (!string.IsNullOrEmpty(extra)) line.Append(',').Append(extra);
            return line.Append('}').ToString();
        }

        private static string Sides(bool[] positive)
        {
            if (positive == null) return "";
            var text = new StringBuilder(positive.Length);
            foreach (bool p in positive) text.Append(p ? '+' : '-');
            return text.ToString();
        }
    }
}
