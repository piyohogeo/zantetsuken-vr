using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>Test-only single-convex clip driver with sentinel-guarded outputs and scratch (probe Phase2Verify.RunClip).</summary>
    public static unsafe class ClipHarness
    {
        public sealed class ClipRun
        {
            public int status;             // CutStatus; NotSplit when the robust support decided not to split
            public int disposition;        // +1 inherit positive, -1 inherit negative, 0 split (robust support, 7.6)
            public float minAbsD;
            public CutStats stats;
            public ConvexBrepData pos, neg;
            public List<string> badGuards = new List<string>();
            public Dictionary<string, double> usage = new Dictionary<string, double>();
            public ClipCapacity capPos, capNeg;
            public int scratchBytes;
            public double microseconds;
        }

        public static BrepBuffer AllocOut(SentinelArena a, string tag, ClipCapacity c)
        {
            var b = new BrepBuffer
            {
                v = a.AllocPtr<float3>(tag + ".v", c.vOut), faceOff = a.AllocPtr<int>(tag + ".faceOff", c.fOut + 1), faceIdx = a.AllocPtr<int>(tag + ".faceIdx", c.iOut),
                faceEdge = a.AllocPtr<int>(tag + ".faceEdge", c.iOut), edges = a.AllocPtr<BrepEdge>(tag + ".edges", c.eOut),
                vCap = c.vOut, fCap = c.fOut, iCap = c.iOut, eCap = c.eOut,
            };
            return b;
        }

        /// 7.6 scan of one convex: raw signed distance, exact sign class and the robust-support disposition.
        public static int Scan(ConvexBrepData data, float3 n, float w, float eps, float* sd, sbyte* cls, out int vPos, out int vNeg, out int vOn, out float minAbsD)
        {
            vPos = 0; vNeg = 0; vOn = 0; int supPos = 0, supNeg = 0; minAbsD = float.MaxValue;
            for (int i = 0; i < data.V; i++)
            {
                float s = math.dot(n, data.v[i]) + w;
                sd[i] = s;
                sbyte c = (sbyte)(s > 0f ? 1 : s < 0f ? -1 : 0);
                cls[i] = c;
                if (c > 0) vPos++; else if (c < 0) vNeg++; else vOn++;
                if (s > eps) supPos++; else if (s < -eps) supNeg++;
                minAbsD = math.min(minAbsD, math.abs(s));
            }
            return supPos > 0 && supNeg > 0 ? 0 : supNeg > 0 ? -1 : +1;
        }

        /// Runs the product clip kernel on one convex. `fill` pre-fills every reserved region (scratch included) to prove initialisation independence.
        public static ClipRun RunClip(SentinelArena arena, ConvexBrepData data, float3 n, float w, float eps, byte fill = 0xA5, int vertexCapDeficit = 0)
        {
            arena.Reset(fill);
            int V = data.V, E = data.E, F = data.F, I = data.I, lmax = data.Lmax;
            var run = new ClipRun();
            var sd = arena.AllocPtr<float>("sd", V);
            var cls = arena.AllocPtr<sbyte>("cls", V);
            run.disposition = Scan(data, n, w, eps, sd, cls, out int vPos, out int vNeg, out int vOn, out run.minAbsD);
            if (run.disposition != 0) { run.status = (int)CutStatus.NotSplit; return run; }
            var iv = arena.Alloc<float3>("in.v", V);
            var ifo = arena.Alloc<int>("in.faceOff", F + 1);
            var ifi = arena.Alloc<int>("in.faceIdx", I);
            var ife = arena.Alloc<int>("in.faceEdge", I);
            var ie = arena.Alloc<BrepEdge>("in.edges", E);
            var input = data.Upload(iv, ifo, ifi, ife, ie);
            var cp = ClipCapacity.Snapshot(V, E, F, I, lmax, vPos, vOn);
            var cn = ClipCapacity.Snapshot(V, E, F, I, lmax, vNeg, vOn);
            if (vertexCapDeficit > 0) { cp.vOut = math.max(0, cp.vOut - vertexCapDeficit); cn.vOut = math.max(0, cn.vOut - vertexCapDeficit); }
            run.capPos = cp; run.capNeg = cn;
            var pos = AllocOut(arena, "pos", cp);
            var neg = AllocOut(arena, "neg", cn);
            int cutCap = math.max(cp.cutK, cn.cutK);
            int hashCap = math.max(cp.hashCap, cn.hashCap);
            int boundaryCap = math.max(cp.vOut, cn.vOut);
            CutScratch.Layout(null, V, E, lmax, cutCap, hashCap, boundaryCap, out int bytes);
            run.scratchBytes = bytes;
            var block = arena.AllocPtr<byte>("scratch", bytes);
            var sc = CutScratch.Layout(block, V, E, lmax, cutCap, hashCap, boundaryCap, out _);
            var plane = new float4(n, w);
            var stats = new CutStats();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            run.status = ConvexClipKernel.Cut(in input, sd, cls, in plane, ref pos, ref neg, ref sc, ref stats);
            run.microseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            run.stats = stats;
            run.badGuards = arena.CheckGuards();
            if (run.status == (int)CutStatus.Ok)
            {
                run.pos = ConvexBrepData.FromBuffer(in pos);
                run.neg = ConvexBrepData.FromBuffer(in neg);
                run.usage["v"] = math.max((double)pos.V / pos.vCap, (double)neg.V / neg.vCap);
                run.usage["f"] = math.max((double)pos.F / pos.fCap, (double)neg.F / neg.fCap);
                run.usage["i"] = math.max((double)pos.I / pos.iCap, (double)neg.I / neg.iCap);
                run.usage["e"] = math.max((double)pos.E / pos.eCap, (double)neg.E / neg.eCap);
                run.usage["cut"] = (double)stats.cutK / cutCap;
            }
            return run;
        }
    }

    /// <summary>Test-only reduction driver (probe Harness/ReductionHarness) with sentinel-guarded scratch.</summary>
    public static unsafe class ReductionHarness
    {
        public sealed class Run
        {
            public int status;
            public ReductionStats stats;
            public ConvexBrepData output;
            public List<string> badGuards;
            public int scratchBytes;
            public double microseconds;
        }

        public static Run Reduce(SentinelArena arena, ConvexBrepData input, int L, byte fill = 0xA5, int maxMode = 2, uint scrambleSeed = 0)
        {
            arena.Reset(fill);
            var run = new Run();
            int V = input.V, E = input.E, F = input.F, I = input.I;
            // io buffer: capacity = the input itself (a reduced polytope has fewer vertices; faces bounded by F + 2E, indices by I + 6E)
            var cap = new ClipCapacity { vOut = V, fOut = F + 2 * E + 4, iOut = I + 3 * (2 * E + 4), eOut = 3 * V + 2 * E + 8 };
            var v = arena.Alloc<float3>("io.v", cap.vOut);
            var fo = arena.Alloc<int>("io.faceOff", cap.fOut + 1);
            var fi = arena.Alloc<int>("io.faceIdx", cap.iOut);
            var fe = arena.Alloc<int>("io.faceEdge", cap.iOut);
            var ed = arena.Alloc<BrepEdge>("io.edges", cap.eOut);
            var io = input.Upload(v, fo, fi, fe, ed);
            io.vCap = cap.vOut; io.fCap = cap.fOut; io.iCap = cap.iOut; io.eCap = cap.eOut;
            ReductionScratch.Layout(null, V, E, F, I, out int bytes);
            run.scratchBytes = bytes;
            var block = arena.AllocPtr<byte>("reduction.scratch", bytes);
            if (scrambleSeed != 0) arena.Scramble("reduction.scratch", scrambleSeed);
            var s = ReductionScratch.Layout(block, V, E, F, I, out _);
            var stats = new ReductionStats();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            run.status = ConvexReductionKernel.ReduceUpTo(ref io, L, ref s, ref stats, maxMode);
            run.microseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            run.stats = stats;
            run.badGuards = arena.CheckGuards();
            if (run.status == (int)ReductionStatus.Ok || run.status == (int)ReductionStatus.NotNeeded) run.output = ConvexBrepData.FromBuffer(in io);
            return run;
        }
    }

    /// <summary>Minimal Burst job wrapper (test only) proving the owner kernel runs on the job path as well as by direct call.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct OwnerCutJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public ConvexCutOwnerInput input;
        [NativeDisableUnsafePtrRestriction] public ConvexCutOwnerOutput output;
        public NativeArray<ConvexCutOwnerResult> result;

        public void Execute()
        {
            var r = result[0];
            ConvexCutOwnerKernel.Execute(in input, in output, ref r);
            result[0] = r;
        }
    }

    /// <summary>
    /// Test-only owner driver: builds the kernel input from managed polytopes (7.6 scan on the test side, as the Phase 4
    /// caller will do), reserves the output arena and scratch from the kernel's own capacity query inside a sentinel
    /// arena, runs the kernel by direct call or through <see cref="OwnerCutJob"/>, and reads the outputs back.
    /// </summary>
    public sealed unsafe class OwnerCutHarness : IDisposable
    {
        public readonly List<ConvexPoly> polys = new List<ConvexPoly>();
        public readonly List<ConvexBrepData> data = new List<ConvexBrepData>();
        public float3 planeN; public float planeW; public float eps; public double parentMass = 10.0; public int vertexLimit = 128;
        public ConvexCutOwnerInput input;
        public ConvexCutOwnerOutput output;
        public ConvexCutOwnerCapacity capacity;
        public readonly SentinelArena arena;
        public int scratchOffset, scratchBytesReserved;
        NativeArray<ConvexCutOwnerResult> m_jobResult;
        bool m_built;

        public OwnerCutHarness(int arenaBytes = 32 << 20) { arena = new SentinelArena(arenaBytes); }

        public OwnerCutHarness Add(ConvexPoly p) { polys.Add(p); data.Add(ConvexBrepData.FromPoly(p)); return this; }

        public OwnerCutHarness Plane(float3 n, float w, float eps) { planeN = n; planeW = w; this.eps = eps; return this; }

        public ConvexSide SideOf(int c) => (ConvexSide)input.sides[c];

        /// Lays everything out. `extra*` add slack, `deficit*` remove elements from the queried capacities (capacity-boundary tests).
        public void Build(byte fill = 0xA5, int vertexDeficit = 0, int scratchDeficit = 0, int extraScratch = 0)
        {
            arena.Reset(fill);
            int vc = 0, fo = 0, fi = 0, ec = 0;
            foreach (var d in data) { vc += d.V; fo += d.F + 1; fi += d.I; ec += d.E; }
            var bank = new ConvexBrepBank
            {
                vertices = arena.AllocPtr<float3>("in.v", vc), faceOffsets = arena.AllocPtr<int>("in.faceOff", fo), faceIndices = arena.AllocPtr<int>("in.faceIdx", fi),
                faceEdges = arena.AllocPtr<int>("in.faceEdge", fi), edges = arena.AllocPtr<BrepEdge>("in.edges", ec),
            };
            var ranges = arena.AllocPtr<ConvexBrepRange>("in.ranges", data.Count);
            var sides = arena.AllocPtr<byte>("in.sides", data.Count);
            var sd = arena.AllocPtr<float>("in.sd", vc);
            var cls = arena.AllocPtr<sbyte>("in.cls", vc);
            var dbase = arena.AllocPtr<int>("in.distanceBase", data.Count);
            vc = 0; fo = 0; fi = 0; ec = 0;
            for (int c = 0; c < data.Count; c++)
            {
                var d = data[c];
                for (int i = 0; i < d.V; i++) bank.vertices[vc + i] = d.v[i];
                for (int i = 0; i <= d.F; i++) bank.faceOffsets[fo + i] = d.faceOff[i];
                for (int i = 0; i < d.I; i++) { bank.faceIndices[fi + i] = d.faceIdx[i]; bank.faceEdges[fi + i] = d.faceEdge[i]; }
                for (int i = 0; i < d.E; i++) bank.edges[ec + i] = d.edges[i];
                ranges[c] = d.RangeAt(vc, fo, fi, ec);
                dbase[c] = vc;
                int disp = ClipHarness.Scan(d, planeN, planeW, eps, sd + vc, cls + vc, out _, out _, out _, out _);
                // 7.6 table: both supports -> Split; negative only -> Negative; positive only -> Positive; none -> NearPlaneToPositive
                int supPos = 0, supNeg = 0;
                for (int i = 0; i < d.V; i++) { float s = sd[vc + i]; if (s > eps) supPos++; else if (s < -eps) supNeg++; }
                sides[c] = (byte)(disp == 0 ? ConvexSide.Split : supNeg > 0 ? ConvexSide.Negative : supPos > 0 ? ConvexSide.Positive : ConvexSide.NearPlaneToPositive);
                vc += d.V; fo += d.F + 1; fi += d.I; ec += d.E;
            }
            input = new ConvexCutOwnerInput
            {
                bank = bank, convexes = ranges, convexCount = data.Count, sides = sides, signedDistance = sd, signClass = cls, distanceBases = dbase,
                plane = new float4(planeN, planeW), parentMass = parentMass, vertexLimit = vertexLimit,
            };
            capacity = default;
            ConvexCutOwnerKernel.QueryCapacity(in input, ref capacity);
            int vcap = math.max(0, capacity.vertices - vertexDeficit);
            int scratch = math.max(0, capacity.scratchBytes - scratchDeficit + extraScratch);
            var obank = new ConvexBrepBank
            {
                vertices = arena.AllocPtr<float3>("out.v", vcap), faceOffsets = arena.AllocPtr<int>("out.faceOff", capacity.faceOffsets), faceIndices = arena.AllocPtr<int>("out.faceIdx", capacity.faceIndices),
                faceEdges = arena.AllocPtr<int>("out.faceEdge", capacity.faceIndices), edges = arena.AllocPtr<BrepEdge>("out.edges", capacity.edges),
            };
            var outcomes = arena.AllocPtr<ConvexCutOutcome>("out.outcomes", data.Count);
            scratchBytesReserved = scratch;
            byte* scratchPtr = arena.AllocPtr<byte>("scratch", scratch);
            output = new ConvexCutOwnerOutput
            {
                bank = obank, vertexCapacity = vcap, faceOffsetCapacity = capacity.faceOffsets, faceIndexCapacity = capacity.faceIndices, edgeCapacity = capacity.edges,
                outcomes = outcomes, scratch = scratchPtr, scratchBytes = scratch,
            };
            m_built = true;
        }

        public void ScrambleScratch(uint seed) => arena.Scramble("scratch", seed);

        public ConvexCutOwnerResult Execute()
        {
            if (!m_built) throw new InvalidOperationException("Build first");
            var r = new ConvexCutOwnerResult();
            ConvexCutOwnerKernel.Execute(in input, in output, ref r);
            return r;
        }

        public ConvexCutOwnerResult ExecuteViaJob()
        {
            if (!m_built) throw new InvalidOperationException("Build first");
            if (!m_jobResult.IsCreated) m_jobResult = new NativeArray<ConvexCutOwnerResult>(1, Allocator.Persistent);
            m_jobResult[0] = default;
            new OwnerCutJob { input = input, output = output, result = m_jobResult }.Schedule().Complete();
            return m_jobResult[0];
        }

        public List<string> CheckGuards() => arena.CheckGuards();

        public ConvexCutOutcome Outcome(int c) => output.outcomes[c];
        public ConvexBrepData ReadOutput(in ConvexBrepRange r) => ConvexBrepData.FromBank(in output.bank, in r);
        public ConvexPoly ReadOutputPoly(in ConvexBrepRange r) => ReadOutput(in r).ToPoly();

        /// The adopted convex set of one side: inherited inputs (float-rounded, as the kernel sees them) plus split outputs.
        public List<ConvexPoly> AdoptedSet(bool positive)
        {
            var list = new List<ConvexPoly>();
            for (int c = 0; c < data.Count; c++)
            {
                var o = Outcome(c);
                if (o.IsSplit) list.Add(ReadOutputPoly(positive ? o.positive : o.negative));
                else if (o.InheritsPositive == positive) list.Add(data[c].ToPoly());
            }
            return list;
        }

        /// FNV-1a over every input byte the kernel can see (bank, ranges, sides, distances, classes).
        public ulong InputHash()
        {
            ulong h = 14695981039346656037UL;
            void Mix(byte* p, long n) { for (long i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211UL; } }
            int vc = 0, fo = 0, fi = 0, ec = 0;
            foreach (var d in data) { vc += d.V; fo += d.F + 1; fi += d.I; ec += d.E; }
            Mix((byte*)input.bank.vertices, (long)vc * sizeof(float3));
            Mix((byte*)input.bank.faceOffsets, (long)fo * sizeof(int));
            Mix((byte*)input.bank.faceIndices, (long)fi * sizeof(int));
            Mix((byte*)input.bank.faceEdges, (long)fi * sizeof(int));
            Mix((byte*)input.bank.edges, (long)ec * sizeof(BrepEdge));
            Mix((byte*)input.convexes, (long)data.Count * sizeof(ConvexBrepRange));
            Mix(input.sides, data.Count);
            Mix((byte*)input.signedDistance, (long)vc * sizeof(float));
            Mix((byte*)input.signClass, vc);
            Mix((byte*)input.distanceBases, (long)data.Count * sizeof(int));
            return h;
        }

        /// FNV-1a over the whole output arena and the outcomes (bitwise result comparison across scratch fills).
        public ulong OutputHash()
        {
            ulong h = 14695981039346656037UL;
            void Mix(byte* p, long n) { for (long i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211UL; } }
            for (int c = 0; c < data.Count; c++)
            {
                var o = Outcome(c);
                var oc = o; Mix((byte*)&oc, sizeof(ConvexCutOutcome));
                if (!o.IsSplit) continue;
                foreach (var r in new[] { o.positive, o.negative })
                {
                    Mix((byte*)(output.bank.vertices + r.vertexBase), (long)r.vertexCount * sizeof(float3));
                    Mix((byte*)(output.bank.faceOffsets + r.faceBase), (long)(r.faceCount + 1) * sizeof(int));
                    Mix((byte*)(output.bank.faceIndices + r.faceIndexBase), (long)r.faceIndexCount * sizeof(int));
                    Mix((byte*)(output.bank.faceEdges + r.faceIndexBase), (long)r.faceIndexCount * sizeof(int));
                    Mix((byte*)(output.bank.edges + r.edgeBase), (long)r.edgeCount * sizeof(BrepEdge));
                }
            }
            return h;
        }

        public void Dispose()
        {
            if (m_jobResult.IsCreated) m_jobResult.Dispose();
            arena.Dispose();
        }
    }

    /// <summary>Compound owner fixtures (probe Harness/OwnerCorpus): convexes straddling the plane x = 0 plus non-intersecting ones at x = +-3.5.</summary>
    public static class OwnerFixtures
    {
        static ConvexPoly Shape(int V, int seed, double3 scale)
        {
            switch (seed % 4)
            {
                case 0: return CaseGenerator.RandomSphereHull(V, seed, scale);
                case 1: return V >= 6 && V % 2 == 0 ? CaseGenerator.Prism(V / 2, 1, 0.6).Transformed(scale, 0) : CaseGenerator.RandomSphereHull(V, seed, scale);
                case 2: return CaseGenerator.Bipyramid(V).Transformed(scale, 0);
                default: return CaseGenerator.RandomSphereHull(V, seed + 11, scale);
            }
        }

        /// `count` convexes, `splitCount` of them straddle x = 0; the rest sit at x = +-3.5 (both sides present when count - splitCount >= 2).
        public static OwnerCutHarness Compound(int count, int splitCount, int[] vSeries, int seed, double3 scale, double3 offset, int arenaBytes = 32 << 20)
        {
            var h = new OwnerCutHarness(arenaBytes);
            for (int i = 0; i < count; i++)
            {
                int V = vSeries[i % vSeries.Length];
                var p = Shape(V, seed * 31 + i, 1);
                double3 pos;
                if (i < splitCount) pos = new double3(0, i * 2.2, (i % 3) * 2.2);
                else pos = new double3((i % 2 == 0 ? 3.5 : -3.5), (i - splitCount) * 2.2, ((i - splitCount) % 3) * 2.2);
                h.Add(p.Transformed(scale, (pos * scale) + offset));
            }
            double ext = 2.0 * math.cmax(scale);
            h.Plane(new float3(1, 0, 0), (float)(-offset.x), (float)(CaseGenerator.DefaultEpsRel * ext));
            return h;
        }

        public static readonly int[] Mixed = { 64, 128, 32, 96 };

        /// Reduction owners (probe): light = V128 sphere hull grazed by the plane (R0), heavy = V128 bipyramid with its axis along x cut 10% below the apex (R1).
        public static OwnerCutHarness ReduceLight(int seed) => new OwnerCutHarness().Add(CaseGenerator.RandomSphereHull(128, 900 + seed, 1).Transformed(1, new double3(-0.85, 0, 0))).Plane(new float3(1, 0, 0), 0, (float)(CaseGenerator.DefaultEpsRel * 2.0));
        public static OwnerCutHarness ReduceHeavy(int seed) => new OwnerCutHarness().Add(CaseGenerator.Bipyramid(128).Rotated(quaternion.RotateZ(-math.PI / 2)).Transformed(1, new double3(-0.9, 0, 0))).Plane(new float3(1, 0, 0), 0, (float)(CaseGenerator.DefaultEpsRel * 2.0));
    }
}
