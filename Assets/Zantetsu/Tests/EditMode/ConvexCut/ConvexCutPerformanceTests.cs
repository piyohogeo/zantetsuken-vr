using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.ConvexCut.Tests.ProbeBaseline;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Same-process, same-input comparison of the ported kernels against the probe kernels (Burst, editor JIT): time per
    /// case, managed allocation inside the kernel loop, scratch bytes, and bitwise output equality. Editor timings are
    /// only meaningful as the ratio measured here, never as absolute product numbers.
    /// </summary>
    public class ConvexCutPerformanceTests
    {
        const int L = 128;
        const int k_warmup = 3, k_rounds = 25;

        sealed unsafe class Case : IDisposable
        {
            public string id, group;
            public NativeArray<float3> v; public NativeArray<int> faceOff, faceIdx, faceEdge; public NativeArray<BrepEdge> edges;
            public NativeArray<float> sd; public NativeArray<sbyte> cls; public float4 plane;
            public int V, E, F, I, lmax;
            public BrepBuffer Input => new BrepBuffer
            {
                v = (float3*)v.GetUnsafeReadOnlyPtr(), faceOff = (int*)faceOff.GetUnsafeReadOnlyPtr(), faceIdx = (int*)faceIdx.GetUnsafeReadOnlyPtr(), faceEdge = (int*)faceEdge.GetUnsafeReadOnlyPtr(), edges = (BrepEdge*)edges.GetUnsafeReadOnlyPtr(),
                V = V, F = F, I = I, E = E,
            };
            public void Dispose() { v.Dispose(); faceOff.Dispose(); faceIdx.Dispose(); faceEdge.Dispose(); edges.Dispose(); sd.Dispose(); cls.Dispose(); }
        }

        static Case MakeCase(string group, string id, ConvexPoly poly, double3 n, double w, double eps)
        {
            var data = ConvexBrepData.FromPoly(poly);
            var c = new Case
            {
                id = id, group = group, V = data.V, E = data.E, F = data.F, I = data.I, lmax = data.Lmax,
                v = new NativeArray<float3>(data.v, Allocator.Persistent), faceOff = new NativeArray<int>(data.faceOff, Allocator.Persistent), faceIdx = new NativeArray<int>(data.faceIdx, Allocator.Persistent),
                faceEdge = new NativeArray<int>(data.faceEdge, Allocator.Persistent), edges = new NativeArray<BrepEdge>(data.edges, Allocator.Persistent),
                sd = new NativeArray<float>(data.V, Allocator.Persistent), cls = new NativeArray<sbyte>(data.V, Allocator.Persistent), plane = new float4((float3)n, (float)w),
            };
            int supPos = 0, supNeg = 0; float fe = (float)eps;
            for (int i = 0; i < data.V; i++) { float s = math.dot(c.plane.xyz, data.v[i]) + c.plane.w; c.sd[i] = s; c.cls[i] = (sbyte)(s > 0f ? 1 : s < 0f ? -1 : 0); if (s > fe) supPos++; else if (s < -fe) supNeg++; }
            if (supPos == 0 || supNeg == 0) { c.Dispose(); return null; }
            return c;
        }

        static List<Case> BuildCases(bool reduction)
        {
            var list = new List<Case>();
            void Add(Case c) { if (c != null) list.Add(c); }
            if (!reduction)
            {
                // exact-cut families of the probe §2.2 table, center / offCenter / grazing planes
                var shapes = new List<(string, ConvexPoly)> { ("box-v8", CaseGenerator.Box()), ("prism32-v64", CaseGenerator.Prism(32, 1, 0.6)), ("cone-v64", CaseGenerator.Cone(64)), ("bipyramid-v64", CaseGenerator.Bipyramid(64)), ("bipyramid-v128", CaseGenerator.Bipyramid(128)) };
                foreach (var v in new[] { 8, 16, 32, 64, 96, 128 }) shapes.Add(("sphereHull-v" + v, CaseGenerator.RandomSphereHull(v, v, 1)));
                foreach (var (fam, poly) in shapes)
                {
                    double eps = CaseGenerator.DefaultEpsRel * poly.MaxExtent();
                    foreach (var cls in new[] { "center", "offCenter", "grazing", "throughVertex" })
                        for (int seed = 0; seed < 2; seed++) { CaseGenerator.MakePlane(poly, cls, 300 + seed, eps, out var n, out var w); Add(MakeCase(fam, fam + "-" + cls + "-" + seed, poly, n, w, eps)); }
                }
            }
            else
            {
                // reduction bench corpus of the probe §12.3
                foreach (var (fam, poly) in new[] { ("sphereHull128", CaseGenerator.RandomSphereHull(128, 7, 1)), ("elongated128", CaseGenerator.RandomSphereHull(128, 8, new double3(4, 1, 0.5))), ("bipyramid128", CaseGenerator.Bipyramid(128)) })
                {
                    double eps = CaseGenerator.DefaultEpsRel * poly.MaxExtent();
                    for (int seed = 0; seed < 4; seed++) { CaseGenerator.MakePlane(poly, "grazing", 500 + seed, eps, out var n, out var w); Add(MakeCase("grazing", fam + "-grazing-" + seed, poly, n, w, eps)); }
                    for (int seed = 0; seed < 2; seed++) { CaseGenerator.MakePlane(poly, "nearFace", 600 + seed, eps, out var n, out var w); Add(MakeCase("light", fam + "-nearFace-" + seed, poly, n, w, eps)); }
                }
                {
                    var bp = CaseGenerator.Bipyramid(128); double eps = CaseGenerator.DefaultEpsRel * bp.MaxExtent();
                    for (int seed = 0; seed < 2; seed++) { CaseGenerator.MakePlane(bp, "manyEdge", 700 + seed, eps, out var n, out var w); Add(MakeCase("mid", "bipyramid128-manyEdge-" + seed, bp, n, w, eps)); }
                    for (int seed = 0; seed < 2; seed++) { CaseGenerator.MakePlane(bp, "offCenter", 710 + seed, eps, out var n, out var w); Add(MakeCase("mid", "bipyramid128-offCenter-" + seed, bp, n, w, eps)); }
                    Add(MakeCase("max", "bipyramid128-apex", bp, new double3(0, 1, 0), -0.8, eps));
                    var cone = CaseGenerator.Cone(128); double eps2 = CaseGenerator.DefaultEpsRel * cone.MaxExtent();
                    Add(MakeCase("max", "cone128-belowApex", cone, new double3(0, 1, 0), -0.9, eps2));
                    var nc = CaseGenerator.NearCoplanarHull(128, 128); double eps3 = CaseGenerator.DefaultEpsRel * nc.MaxExtent();
                    for (int seed = 0; seed < 2; seed++) { CaseGenerator.MakePlane(nc, "manyEdge", 720 + seed, eps3, out var n, out var w); Add(MakeCase("mid", "nearCoplanar128-manyEdge-" + seed, nc, n, w, eps3)); }
                }
            }
            return list;
        }

        struct Sample { public double baseline, ported; }


        static double P50(List<double> s) { s.Sort(); return s.Count == 0 ? double.NaN : s[s.Count / 2]; }

        unsafe struct Buffers : IDisposable
        {
            public NativeArray<float3> pv, nv; public NativeArray<int> pfo, nfo, pfi, nfi, pfe, nfe; public NativeArray<BrepEdge> pe, ne;
            public NativeArray<byte> scratch;
            public ClipCapacity cap;
            public int cutBytesPorted, cutBytesBaseline, redBytesPorted, redBytesBaseline;

            public static Buffers Create(List<Case> cases)
            {
                int maxV = 4, maxE = 6, maxF = 4, maxI = 12, maxL = 3;
                foreach (var c in cases) { maxV = math.max(maxV, c.V); maxE = math.max(maxE, c.E); maxF = math.max(maxF, c.F); maxI = math.max(maxI, c.I); maxL = math.max(maxL, c.lmax); }
                var b = new Buffers { cap = ClipCapacity.Static(maxV, maxE, maxF, maxI, maxL) };
                var cap = b.cap;
                b.pv = new NativeArray<float3>(cap.vOut, Allocator.Persistent); b.nv = new NativeArray<float3>(cap.vOut, Allocator.Persistent);
                b.pfo = new NativeArray<int>(cap.fOut + 1, Allocator.Persistent); b.nfo = new NativeArray<int>(cap.fOut + 1, Allocator.Persistent);
                b.pfi = new NativeArray<int>(cap.iOut, Allocator.Persistent); b.nfi = new NativeArray<int>(cap.iOut, Allocator.Persistent);
                b.pfe = new NativeArray<int>(cap.iOut, Allocator.Persistent); b.nfe = new NativeArray<int>(cap.iOut, Allocator.Persistent);
                b.pe = new NativeArray<BrepEdge>(cap.eOut, Allocator.Persistent); b.ne = new NativeArray<BrepEdge>(cap.eOut, Allocator.Persistent);
                CutScratch.Layout(null, maxV, maxE, maxL, cap.cutK, cap.hashCap, cap.vOut, out b.cutBytesPorted);
                BaselineCutScratch.Layout(null, maxV, maxE, maxL, cap.cutK, cap.hashCap, cap.vOut, out b.cutBytesBaseline);
                ReductionScratch.Layout(null, cap.vOut, cap.eOut, cap.fOut, cap.iOut, out b.redBytesPorted);
                BaselineReductionScratch.Layout(null, cap.vOut, cap.eOut, cap.fOut, cap.iOut, out b.redBytesBaseline);
                int cutBytes = (math.max(b.cutBytesPorted, b.cutBytesBaseline) + 63) / 64 * 64;
                b.scratch = new NativeArray<byte>(cutBytes + math.max(b.redBytesPorted, b.redBytesBaseline) + 64, Allocator.Persistent);
                return b;
            }

            public BrepBuffer Pos => new BrepBuffer { v = (float3*)pv.GetUnsafePtr(), faceOff = (int*)pfo.GetUnsafePtr(), faceIdx = (int*)pfi.GetUnsafePtr(), faceEdge = (int*)pfe.GetUnsafePtr(), edges = (BrepEdge*)pe.GetUnsafePtr(), vCap = cap.vOut, fCap = cap.fOut, iCap = cap.iOut, eCap = cap.eOut };
            public BrepBuffer Neg => new BrepBuffer { v = (float3*)nv.GetUnsafePtr(), faceOff = (int*)nfo.GetUnsafePtr(), faceIdx = (int*)nfi.GetUnsafePtr(), faceEdge = (int*)nfe.GetUnsafePtr(), edges = (BrepEdge*)ne.GetUnsafePtr(), vCap = cap.vOut, fCap = cap.fOut, iCap = cap.iOut, eCap = cap.eOut };
            public byte* ScratchPtr => (byte*)scratch.GetUnsafePtr();
            public int CutBlock => (math.max(cutBytesPorted, cutBytesBaseline) + 63) / 64 * 64;

            public void Dispose() { pv.Dispose(); nv.Dispose(); pfo.Dispose(); nfo.Dispose(); pfi.Dispose(); nfi.Dispose(); pfe.Dispose(); nfe.Dispose(); pe.Dispose(); ne.Dispose(); scratch.Dispose(); }
        }

        /// One case through cut (+ reduction of every side above L when `reduce`) + double mass; returns µs and fills the output hash.
        static unsafe double RunPorted(Case c, ref Buffers b, bool reduce, out ulong hash, out int removed, out int r1, out bool failed)
        {
            var input = c.Input; var pos = b.Pos; var neg = b.Neg;
            var sc = CutScratch.Layout(b.ScratchPtr, c.V, c.E, c.lmax, b.cap.cutK, b.cap.hashCap, b.cap.vOut, out _);
            var st = new CutStats(); removed = 0; r1 = 0; failed = false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int cs = ConvexClipKernel.Cut(in input, (float*)c.sd.GetUnsafeReadOnlyPtr(), (sbyte*)c.cls.GetUnsafeReadOnlyPtr(), in c.plane, ref pos, ref neg, ref sc, ref st);
            if (cs != 0) failed = true;
            else if (reduce)
            {
                for (int side = 0; side < 2; side++)
                {
                    ref BrepBuffer bb = ref side == 0 ? ref pos : ref neg;
                    if (bb.V <= L) continue;
                    var rsc = ReductionScratch.Layout(b.ScratchPtr + b.CutBlock, bb.vCap, bb.eCap, bb.fCap, bb.iCap, out _);
                    var rst = new ReductionStats();
                    if (ConvexReductionKernel.Reduce(ref bb, L, ref rsc, ref rst) != 0) failed = true;
                    removed += rst.removedR0 + rst.removedR1; r1 |= rst.r1Invoked;
                }
            }
            var m = new MassProperties(); MassPropertiesKernel.ComputeDouble(in pos, ref m); MassPropertiesKernel.ComputeDouble(in neg, ref m);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            hash = failed ? 0 : Hash(in pos) ^ (Hash(in neg) * 31);
            return (t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        }

        static unsafe double RunBaseline(Case c, ref Buffers b, bool reduce, out ulong hash, out int removed, out int r1, out bool failed)
        {
            var input = c.Input; var pos = b.Pos; var neg = b.Neg;
            var sc = BaselineCutScratch.Layout(b.ScratchPtr, c.V, c.E, c.lmax, b.cap.cutK, b.cap.hashCap, b.cap.vOut, out _);
            var st = new CutStats(); removed = 0; r1 = 0; failed = false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int cs = BaselinePolygonEdgeCutKernel.Cut(in input, (float*)c.sd.GetUnsafeReadOnlyPtr(), (sbyte*)c.cls.GetUnsafeReadOnlyPtr(), in c.plane, (int)CutFaceMode.Walk, ref pos, ref neg, ref sc, ref st);
            if (cs != 0) failed = true;
            else if (reduce)
            {
                for (int side = 0; side < 2; side++)
                {
                    ref BrepBuffer bb = ref side == 0 ? ref pos : ref neg;
                    if (bb.V <= L) continue;
                    var rsc = BaselineReductionScratch.Layout(b.ScratchPtr + b.CutBlock, bb.vCap, bb.eCap, bb.fCap, bb.iCap, out _);
                    var rst = new ReductionStats();
                    if (BaselineReductionKernel.Reduce(ref bb, L, ref rsc, ref rst) != 0) failed = true;
                    removed += rst.removedR0 + rst.removedR1; r1 |= rst.r1Invoked;
                }
            }
            var m = new MassProperties(); BaselineMassPropertiesKernel.ComputeDouble(in pos, ref m); BaselineMassPropertiesKernel.ComputeDouble(in neg, ref m);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            hash = failed ? 0 : Hash(in pos) ^ (Hash(in neg) * 31);
            return (t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        }

        static unsafe ulong Hash(in BrepBuffer b)
        {
            ulong h = 14695981039346656037UL;
            void Mix(byte* p, long n) { for (long i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211UL; } }
            Mix((byte*)b.v, (long)b.V * sizeof(float3)); Mix((byte*)b.faceOff, (long)(b.F + 1) * sizeof(int)); Mix((byte*)b.faceIdx, (long)b.I * sizeof(int)); Mix((byte*)b.faceEdge, (long)b.I * sizeof(int)); Mix((byte*)b.edges, (long)b.E * sizeof(BrepEdge));
            return h;
        }

        static void Compare(bool reduce, string title, StringBuilder md, out double ratioSum, out long allocatedBytes)
        {
            var cases = BuildCases(reduce);
            Assert.That(cases.Count, Is.GreaterThan(reduce ? 15 : 30));
            var buffers = Buffers.Create(cases);
            // everything the measured loop touches is allocated up front so that any managed allocation inside it is the kernels' own
            var samples = new Sample[cases.Count, k_rounds];
            var mismatch = new bool[cases.Count];
            var failed = new bool[cases.Count];
            allocatedBytes = 0; int collections = 0;
            try
            {
                for (int round = 0; round < k_warmup + k_rounds; round++)
                {
                    bool warm = round < k_warmup;
                    if (!warm) GC.Collect();
                    long mem0 = GC.GetTotalMemory(false); int gc0 = GC.CollectionCount(0);
                    for (int k = 0; k < cases.Count; k++)
                    {
                        int ci = (k + round) % cases.Count;
                        var c = cases[ci];
                        double tb, tp; ulong hb, hp; int rb, rp, r1b, r1p; bool fb, fp;
                        // alternate the order per round so that cache / clock effects do not favour one implementation
                        if ((round & 1) == 0) { tb = RunBaseline(c, ref buffers, reduce, out hb, out rb, out r1b, out fb); tp = RunPorted(c, ref buffers, reduce, out hp, out rp, out r1p, out fp); }
                        else { tp = RunPorted(c, ref buffers, reduce, out hp, out rp, out r1p, out fp); tb = RunBaseline(c, ref buffers, reduce, out hb, out rb, out r1b, out fb); }
                        if (warm) continue;
                        if (fb || fp) failed[ci] = true;
                        if (hb != hp || rb != rp || r1b != r1p) mismatch[ci] = true;
                        samples[ci, round - k_warmup] = new Sample { baseline = tb, ported = tp };
                    }
                    if (!warm) { allocatedBytes += Math.Max(0, GC.GetTotalMemory(false) - mem0); collections += GC.CollectionCount(0) - gc0; }
                }
                var hashMismatch = new List<string>(); var failures = new List<string>();
                for (int ci = 0; ci < cases.Count; ci++) { if (mismatch[ci]) hashMismatch.Add(cases[ci].id); if (failed[ci]) failures.Add(cases[ci].id); }
                md.Append("### ").Append(title).Append("\n\n| case | group | V | baseline p50 us | ported p50 us | ratio |\n|---|---|---|---|---|---|\n");
                double sumB = 0, sumP = 0;
                for (int ci = 0; ci < cases.Count; ci++)
                {
                    var bl = new List<double>(); var pl = new List<double>();
                    for (int r = 0; r < k_rounds; r++) { bl.Add(samples[ci, r].baseline); pl.Add(samples[ci, r].ported); }
                    double pb = P50(bl), pp = P50(pl);
                    sumB += pb; sumP += pp;
                    md.Append("| ").Append(cases[ci].id).Append(" | ").Append(cases[ci].group).Append(" | ").Append(cases[ci].V).Append(" | ").Append(pb.ToString("F2", TestCorpus.Ci)).Append(" | ").Append(pp.ToString("F2", TestCorpus.Ci)).Append(" | ").Append((pp / pb).ToString("F3", TestCorpus.Ci)).Append(" |\n");
                }
                ratioSum = sumP / sumB;
                md.Append("\nsum of p50: baseline ").Append(sumB.ToString("F1", TestCorpus.Ci)).Append(" us, ported ").Append(sumP.ToString("F1", TestCorpus.Ci)).Append(" us, ratio ").Append(ratioSum.ToString("F3", TestCorpus.Ci))
                  .Append("; managed heap growth in the measured loops: ").Append(allocatedBytes).Append(" B, collections ").Append(collections).Append("; output hash / removal mismatches: ").Append(hashMismatch.Count).Append("; kernel failures: ").Append(failures.Count).Append('\n');
                md.Append("scratch bytes (max case): cut ported ").Append(buffers.cutBytesPorted).Append(" vs baseline ").Append(buffers.cutBytesBaseline).Append(", reduction ported ").Append(buffers.redBytesPorted).Append(" vs baseline ").Append(buffers.redBytesBaseline).Append("\n\n");
                Assert.That(failures, Is.Empty, "kernel failures: " + string.Join(", ", failures));
                Assert.That(hashMismatch, Is.Empty, "ported outputs differ from the probe baseline: " + string.Join(", ", hashMismatch));
                Assert.That(buffers.cutBytesPorted, Is.LessThanOrEqualTo(buffers.cutBytesBaseline), "cut scratch regression");
                Assert.That(buffers.redBytesPorted, Is.LessThanOrEqualTo(buffers.redBytesBaseline), "reduction scratch regression");
            }
            finally
            {
                buffers.Dispose();
                foreach (var c in cases) c.Dispose();
            }
        }

        [Test, Category("Performance")]
        public void PortedKernels_MatchTheProbeBaseline_SameRun()
        {
            Assert.That(BurstCompiler.IsEnabled, Is.True);
            var md = new StringBuilder("# ConvexCut kernel comparison (editor, Burst JIT, same process)\n\n");
            md.Append("rounds: warmup ").Append(k_warmup).Append(", measured ").Append(k_rounds).Append("; per case: A-Walk cut (both sides) + reduction of sides above L (reduction group only) + double mass; order alternated per round\n\n");
            Compare(false, "exact cut + mass", md, out double cutRatio, out long cutAlloc);
            Compare(true, "cut + reduction (R0 -> R1) + mass", md, out double redRatio, out long redAlloc);
            TestContext.Out.WriteLine(md.ToString());
            try
            {
                string dir = Path.Combine(Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")), "Logs", "ConvexCutBench");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md"), md.ToString());
            }
            catch (Exception e) { TestContext.Out.WriteLine("bench file not written: " + e.Message); }
            Assert.That(cutAlloc, Is.EqualTo(0), "managed allocation inside the cut kernel loop");
            Assert.That(redAlloc, Is.EqualTo(0), "managed allocation inside the reduction kernel loop");
            // editor timings carry noise; a clear regression is what this guards against
            Assert.That(cutRatio, Is.LessThanOrEqualTo(1.25), "ported cut kernel slower than the probe baseline by more than noise");
            Assert.That(redRatio, Is.LessThanOrEqualTo(1.25), "ported reduction slower than the probe baseline by more than noise");
        }
    }
}
