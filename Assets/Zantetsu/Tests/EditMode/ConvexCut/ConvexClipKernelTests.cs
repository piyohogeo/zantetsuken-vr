using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>Corpus shared by the ConvexCut tests (built once per test session).</summary>
    public static class TestCorpus
    {
        static List<CutCase> s_corpus;
        public static List<CutCase> Corpus => s_corpus ?? (s_corpus = CaseGenerator.Corpus(2));
        public static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        public static bool IsDiagnosticFamily(CutCase c) => c.family == "offsetScaled" || c.family == "tiny";   // probe §14.5 frame-boundary diagnostics: reported, not gating
    }

    /// <summary>
    /// DESIGN 7.6 / 7.2 numerical contract of the exact plane clip: robust support decides Split per convex, the clip
    /// classifies by the exact sign only, rounding-level residue is accepted by the resolution rule, outputs stay inside
    /// the capacity formula and are usable as the next input.
    /// </summary>
    public class ConvexClipKernelTests
    {
        [Test]
        public void Burst_IsEnabled_AndKernelsRunCompiled()
        {
            Assert.That(BurstCompiler.IsEnabled, Is.True, "Burst must be enabled in the editor for the numerical kernel tests to prove Burst execution");
            using (var h = OwnerFixtures.Compound(4, 2, OwnerFixtures.Mixed, 130, 1, 0))
            {
                h.Build();
                var direct = h.Execute();
                Assert.That(direct.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                Assert.That(direct.executedManaged, Is.EqualTo(0), "direct call ran the managed fallback instead of Burst");
                var job = h.ExecuteViaJob();
                Assert.That(job.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                Assert.That(job.executedManaged, Is.EqualTo(0), "job path ran the managed fallback instead of Burst");
            }
        }

        [Test]
        public void Corpus_MatchesReferenceAndVerifies()
        {
            var corpus = TestCorpus.Corpus;
            var failures = new StringBuilder();
            int cases = 0, split = 0, nearZero = 0, exactZero = 0, residueCases = 0, residueFaces = 0, residuePairs = 0, nearPlaneSignOk = 0, diagnosticOnly = 0;
            var maxUsage = new Dictionary<string, double>();
            var maxErr = new Dictionary<string, double>();
            using (var arena = new SentinelArena(8 << 20))
            {
                foreach (var c in corpus)
                {
                    cases++;
                    var data = ConvexBrepData.FromPoly(c.poly);
                    var srcF = data.ToPoly();
                    double3 nF = (double3)(float3)c.n; double wF = (float)c.w; double epsF = (float)c.eps;
                    var rref = ReferenceDoubleClip.Cut(srcF, nF, wF, epsF);
                    bool diagnostic = TestCorpus.IsDiagnosticFamily(c);
                    var run = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps);
                    void Fail(string what) { if (diagnostic) { diagnosticOnly++; return; } failures.Append(c.id).Append(": ").Append(what).Append('\n'); }
                    if (run.badGuards.Count > 0) failures.Append(c.id).Append(": GUARD OVERRUN ").Append(string.Join(",", run.badGuards)).Append('\n');
                    bool refSplit = rref.status == ReferenceDoubleClip.Status.Ok;
                    bool candSplit = run.status == (int)CutStatus.Ok;
                    if (!candSplit && !refSplit)
                    {
                        int refSide = rref.status == ReferenceDoubleClip.Status.NotSplitNegative ? -1 : +1;
                        if (refSide != run.disposition) Fail("inherit side " + run.disposition + " vs reference " + refSide);
                    }
                    if (refSplit != candSplit || (!candSplit && run.status != (int)CutStatus.NotSplit))
                        Fail("status " + (CutStatus)run.status + " vs reference " + rref.status);
                    else if (candSplit)
                    {
                        split++;
                        foreach (var kv in run.usage) maxUsage[kv.Key] = math.max(maxUsage.TryGetValue(kv.Key, out var m) ? m : 0, kv.Value);
                        double ext = srcF.MaxExtent();
                        var pp = run.pos.ToPoly(); var pn = run.neg.ToPoly();
                        var optP = new BrepVerifier.Options { relTol = 4e-6, maxVertices = 100000, source = srcF, extentOverride = ext, checkHalfspace = true, planeN = c.n, planeW = c.w, side = +1, halfspaceTol = 4e-6 * ext, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution };
                        var optN = new BrepVerifier.Options { relTol = 4e-6, maxVertices = 100000, source = srcF, extentOverride = ext, checkHalfspace = true, planeN = c.n, planeW = c.w, side = -1, halfspaceTol = 4e-6 * ext, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution };
                        var vp = BrepVerifier.Verify(pp, optP); var vn = BrepVerifier.Verify(pn, optN);
                        if (vp.degenerateFaces + vn.degenerateFaces + vp.coincidentVertexPairs + vn.coincidentVertexPairs > 0) residueCases++;
                        residueFaces += vp.degenerateFaces + vn.degenerateFaces; residuePairs += vp.coincidentVertexPairs + vn.coincidentVertexPairs;
                        if (!vp.Ok || !vn.Ok) Fail("pos[" + vp + "] neg[" + vn + "]");
                        double se = math.max(SupportSignature.MaxSupportDiff(pp, rref.positive, SupportSignature.Dirs42), SupportSignature.MaxSupportDiff(pn, rref.negative, SupportSignature.Dirs42)) / ext;
                        if (!diagnostic) maxErr[c.family] = math.max(maxErr.TryGetValue(c.family, out var prev) ? prev : 0, se);
                        if (!(se < 1e-5)) Fail("support error vs reference " + se.ToString("G3", TestCorpus.Ci));
                        // topology must match the reference unless a vertex is within float noise of the plane
                        bool isNearZero = run.minAbsD <= 1e-6f * ext;
                        if (isNearZero) nearZero++;
                        if (!isNearZero && (run.pos.V != rref.positive.V.Length || run.pos.F != rref.positive.F.Length || run.neg.V != rref.negative.V.Length || run.neg.F != rref.negative.F.Length || run.pos.E != rref.positive.EdgeCount))
                            Fail("topology pos " + run.pos.V + "/" + run.pos.E + "/" + run.pos.F + " vs reference " + rref.positive.V.Length + "/" + rref.positive.EdgeCount + "/" + rref.positive.F.Length);
                        if (run.stats.onVertices > 0) exactZero++;
                        // every output must carry a complete edge table (next-cut input contract)
                        if (run.pos.V - run.pos.E + run.pos.F != 2 || run.neg.V - run.neg.E + run.neg.F != 2) Fail("output Euler characteristic");
                    }
                    // 7.6 contract by plane class
                    bool expectSplit = c.planeClass == "nearPlaneSplit" || c.planeClass == "exactZero";
                    bool expectNoSplit = c.planeClass == "nearPlaneNoSplit" || c.planeClass == "exactZeroNoSplit" || c.planeClass == "missPositive" || c.planeClass == "missNegative";
                    if (expectSplit && !candSplit) Fail("expected Split, got " + (CutStatus)run.status + " (disposition " + run.disposition + ")");
                    if (expectNoSplit && candSplit) Fail("expected uncut inheritance, got Split");
                    if (c.planeClass == "nearPlaneNoSplit" && run.disposition != -1) Fail("near-plane convex must inherit negative");
                    if (c.id.Contains("exactBase") && run.disposition != +1) Fail("positive-only support must inherit positive");
                    if (c.id.Contains("exactFace") && run.disposition != -1) Fail("negative-only support must inherit negative");
                    if (c.planeClass == "exactZero" && candSplit && run.stats.onVertices == 0) Fail("expected exact d == 0 vertices");
                    if (c.planeClass == "nearPlaneSplit" && candSplit)
                    {
                        // the 0.5 eps vertex clips by sign: present in the positive output, absent from the negative one
                        int k = -1; float bd = float.MaxValue;
                        for (int i = 0; i < data.V; i++) { float d = math.dot((float3)c.n, data.v[i]) + (float)c.w; if (d > 0 && d < bd) { bd = d; k = i; } }
                        bool inPos = false, inNeg = false;
                        foreach (var v in run.pos.v) if (v.Equals(data.v[k])) inPos = true;
                        foreach (var v in run.neg.v) if (v.Equals(data.v[k])) inNeg = true;
                        if (!(inPos && !inNeg && bd <= c.eps)) Fail("vertex at d=" + bd.ToString("G3", TestCorpus.Ci) + " must clip by sign: inPos=" + inPos + " inNeg=" + inNeg);
                        else nearPlaneSignOk++;
                    }
                }
            }
            var md = new StringBuilder();
            md.Append("cases=").Append(cases).Append(" split=").Append(split).Append(" nearZero(topology by support)=").Append(nearZero).Append(" exactZeroVertexCases=").Append(exactZero)
              .Append(" residueCases=").Append(residueCases).Append(" (degenerate faces=").Append(residueFaces).Append(", coincident pairs=").Append(residuePairs).Append(") nearPlaneSignOk=").Append(nearPlaneSignOk)
              .Append(" diagnosticOnlyDeviations=").Append(diagnosticOnly).Append('\n');
            md.Append("max capacity usage (used/reserved): ");
            foreach (var kv in maxUsage) md.Append(kv.Key).Append('=').Append(kv.Value.ToString("F3", TestCorpus.Ci)).Append(' ');
            md.Append("\nmax support error vs double-input reference per family: ");
            foreach (var kv in maxErr) md.Append(kv.Key).Append('=').Append(kv.Value.ToString("G2", TestCorpus.Ci)).Append(' ');
            TestContext.Out.WriteLine(md.ToString());
            Assert.That(failures.Length, Is.EqualTo(0), failures.ToString());
            Assert.That(cases, Is.EqualTo(880), "probe corpus size");
            Assert.That(exactZero, Is.GreaterThan(0), "exact d == 0 vertices must be exercised");
            Assert.That(residueCases, Is.GreaterThan(0), "rounding-level residue (local degeneracy) must be exercised");
            foreach (var key in new[] { "v", "f", "i", "e", "cut" }) Assert.That(maxUsage[key], Is.LessThanOrEqualTo(1.0), "capacity formula exceeded for " + key);
            Assert.That(maxUsage["v"], Is.GreaterThan(0.99), "vertex capacity formula is expected to be tight on the probe corpus");
        }

        [Test]
        public void RepeatedCuts_Depth8_OutputFeedsNextInput()
        {
            var shapes = new List<(string, ConvexPoly)>
            {
                ("box", CaseGenerator.Box()), ("prism32", CaseGenerator.Prism(32, 1, 0.6)), ("bipyramid128", CaseGenerator.Bipyramid(128)),
                ("sphereHull64", CaseGenerator.RandomSphereHull(64, 64, 1)), ("sphereHull128", CaseGenerator.RandomSphereHull(128, 128, 1)), ("cone64", CaseGenerator.Cone(64)),
            };
            var failures = new StringBuilder();
            int gens = 0, maxV = 0;
            using (var arena = new SentinelArena(8 << 20))
            {
                foreach (var (name, shape) in shapes)
                {
                    var cur = ConvexBrepData.FromPoly(shape);
                    for (int gen = 1; gen <= 8; gen++)
                    {
                        var poly = cur.ToPoly();
                        double eps = CaseGenerator.DefaultEpsRel * poly.MaxExtent();
                        CaseGenerator.MakePlane(poly, gen % 2 == 0 ? "offCenter" : "center", 1000 + gen * 7 + name.Length, eps, out var n, out var w);
                        var run = ClipHarness.RunClip(arena, cur, (float3)n, (float)w, (float)eps);
                        var rref = ReferenceDoubleClip.Cut(poly, n, w, eps);
                        gens++;
                        if (run.status == (int)CutStatus.NotSplit && rref.status != ReferenceDoubleClip.Status.Ok && rref.status != ReferenceDoubleClip.Status.Degenerate) break;
                        if (run.status != (int)CutStatus.Ok || rref.status != ReferenceDoubleClip.Status.Ok) { failures.Append(name).Append(" gen").Append(gen).Append(": ").Append((CutStatus)run.status).Append(" ref ").Append(rref.status).Append('\n'); break; }
                        if (run.badGuards.Count > 0) failures.Append(name).Append(" gen").Append(gen).Append(": GUARD OVERRUN\n");
                        var next = ReferenceMassProperties.Volume(run.pos.ToPoly()) >= ReferenceMassProperties.Volume(run.neg.ToPoly()) ? run.pos : run.neg;
                        var refNext = next == run.pos ? rref.positive : rref.negative;
                        var np = next.ToPoly();
                        double ext = poly.MaxExtent();
                        var vr = BrepVerifier.Verify(np, new BrepVerifier.Options { relTol = 4e-6, maxVertices = 100000, source = poly, extentOverride = ext, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution });
                        double sErr = SupportSignature.MaxSupportDiff(np, refNext, SupportSignature.Dirs42) / ext;
                        if (!vr.Ok || !(sErr < 1e-5)) failures.Append(name).Append(" gen").Append(gen).Append(": ").Append(vr).Append(" supportErr=").Append(sErr.ToString("G3", TestCorpus.Ci)).Append('\n');
                        maxV = math.max(maxV, next.V);
                        cur = next;
                    }
                }
            }
            TestContext.Out.WriteLine("repeated cuts: generations=" + gens + " maxV(no reduction)=" + maxV);
            Assert.That(failures.Length, Is.EqualTo(0), failures.ToString());
            Assert.That(gens, Is.GreaterThanOrEqualTo(40));
        }

        [Test]
        public void Capacity_DeficitIsRejectedWithoutOverrun()
        {
            int checkedCases = 0;
            using (var arena = new SentinelArena(8 << 20))
            {
                foreach (var c in TestCorpus.Corpus)
                {
                    if (TestCorpus.IsDiagnosticFamily(c)) continue;
                    var data = ConvexBrepData.FromPoly(c.poly);
                    var full = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps);
                    if (full.status != (int)CutStatus.Ok || full.usage["v"] < 1.0) continue;
                    var deficit = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps, 0xA5, 1);
                    Assert.That(deficit.status, Is.EqualTo((int)CutStatus.CapacityVertex), c.id + ": one vertex short of the formula must fail with CapacityVertex");
                    Assert.That(deficit.badGuards, Is.Empty, c.id + ": capacity failure must not write past the reserved range");
                    checkedCases++;
                    if (checkedCases >= 12) break;
                }
            }
            Assert.That(checkedCases, Is.GreaterThan(0), "no case reaching vertex usage 1.0 found");
        }

        [Test]
        public void ScratchFill_DoesNotChangeTheResult()
        {
            int compared = 0;
            using (var arena = new SentinelArena(8 << 20))
            {
                int k = 0;
                foreach (var c in TestCorpus.Corpus)
                {
                    if (k++ % 9 != 0) continue;
                    var data = ConvexBrepData.FromPoly(c.poly);
                    var a = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps, 0xA5);
                    if (a.status != (int)CutStatus.Ok) continue;
                    foreach (var fill in new byte[] { 0x00, 0xFF, 0x3C })
                    {
                        var b = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps, fill);
                        Assert.That(b.status, Is.EqualTo(a.status), c.id);
                        AssertSameBrep(a.pos, b.pos, c.id + " pos fill " + fill);
                        AssertSameBrep(a.neg, b.neg, c.id + " neg fill " + fill);
                    }
                    compared++;
                }
            }
            Assert.That(compared, Is.GreaterThan(30));
        }

        [Test]
        public void RobustEpsilon_NeverReachesTheClipGeometry()
        {
            // the same Split convex clipped with eps and with 20 eps (still Split) must produce bitwise identical outputs:
            // eps only gates the disposition (7.6), never classification, snapping or offset
            int compared = 0;
            using (var arena = new SentinelArena(8 << 20))
            {
                int k = 0;
                foreach (var c in TestCorpus.Corpus)
                {
                    if (k++ % 7 != 0 || c.planeClass == "nearPlaneSplit") continue;
                    var data = ConvexBrepData.FromPoly(c.poly);
                    var a = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)c.eps);
                    var b = ClipHarness.RunClip(arena, data, (float3)c.n, (float)c.w, (float)(c.eps * 20));
                    if (a.status != (int)CutStatus.Ok || b.status != (int)CutStatus.Ok) continue;
                    AssertSameBrep(a.pos, b.pos, c.id + " pos");
                    AssertSameBrep(a.neg, b.neg, c.id + " neg");
                    compared++;
                }
            }
            Assert.That(compared, Is.GreaterThan(30));
        }

        public static void AssertSameBrep(ConvexBrepData a, ConvexBrepData b, string what)
        {
            Assert.That(b.V, Is.EqualTo(a.V), what + " V");
            Assert.That(b.F, Is.EqualTo(a.F), what + " F");
            Assert.That(b.I, Is.EqualTo(a.I), what + " I");
            Assert.That(b.E, Is.EqualTo(a.E), what + " E");
            for (int i = 0; i < a.V; i++) Assert.That(b.v[i].Equals(a.v[i]), Is.True, what + " vertex " + i);
            for (int i = 0; i <= a.F; i++) Assert.That(b.faceOff[i], Is.EqualTo(a.faceOff[i]), what + " faceOff " + i);
            for (int i = 0; i < a.I; i++) { Assert.That(b.faceIdx[i], Is.EqualTo(a.faceIdx[i]), what + " faceIdx " + i); Assert.That(b.faceEdge[i], Is.EqualTo(a.faceEdge[i]), what + " faceEdge " + i); }
            for (int i = 0; i < a.E; i++) Assert.That(b.edges[i].v0 == a.edges[i].v0 && b.edges[i].v1 == a.edges[i].v1 && b.edges[i].f0 == a.edges[i].f0 && b.edges[i].f1 == a.edges[i].f1, Is.True, what + " edge " + i);
        }
    }
}
