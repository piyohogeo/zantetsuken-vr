using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// DESIGN 7.2 inscribed reduction: every clip output above L reduces to a valid convex with V ≤ L that stays inside
    /// the clip result (Q ⊆ C), can be cut again by the product clip kernel, and does not depend on scratch contents.
    /// </summary>
    public class ConvexReductionKernelTests
    {
        public const int L = 128;

        public sealed class ExcessCase { public string id, family; public ConvexPoly input; public override string ToString() => id; }

        static List<ExcessCase> s_excess;

        /// Excess corpus (probe Phase 3): every corpus clip side with V > L (double reference clip, float-rounded) plus grazing / throughVertex on V = 128 shapes.
        public static List<ExcessCase> ExcessCorpus()
        {
            if (s_excess != null) return s_excess;
            var list = new List<ExcessCase>();
            var cases = new List<CutCase>(TestCorpus.Corpus);
            cases.AddRange(CaseGenerator.ReductionExtraCases());
            foreach (var c in cases)
            {
                var rr = ReferenceDoubleClip.Cut(c.poly, c.n, c.w, c.eps);
                if (rr.status != ReferenceDoubleClip.Status.Ok) continue;
                foreach (var (sideName, sidePoly) in new[] { ("pos", rr.positive), ("neg", rr.negative) })
                {
                    if (sidePoly.V.Length <= L) continue;
                    var input = sidePoly.RoundedToFloat();
                    // the double reference can emit vertices closer than float resolution; rounded to float they coincide. The float kernel never
                    // produces these (it reclassifies), so such inputs are harness artefacts and are skipped (probe rule).
                    if (!BrepVerifier.Verify(input, new BrepVerifier.Options { relTol = 1e-6, maxVertices = 100000, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution }).Ok) continue;
                    list.Add(new ExcessCase { id = c.id + "/" + sideName, family = c.family, input = input });
                }
            }
            return s_excess = list;
        }

        static BrepVerifier.Options ReducedOptions(ConvexPoly source, double ext, int maxV = L) =>
            new BrepVerifier.Options { relTol = 1e-6, maxVertices = maxV, source = source, extentOverride = ext, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution };

        [Test]
        public void ExcessCorpus_ReducesWithinL_StaysInscribed_AndRecutsWithTheClipKernel()
        {
            var corpus = ExcessCorpus();
            var failures = new StringBuilder();
            int ok = 0, r1 = 0, recuts = 0, maxScratch = 0; double worstVolume = 1, usMax = 0, usSum = 0;
            var byFamily = new Dictionary<string, int>();
            using (var arena = new SentinelArena(16 << 20))
            {
                foreach (var ec in corpus)
                {
                    var input = ec.input;
                    var bdata = ConvexBrepData.FromPoly(input);
                    var br = ReductionHarness.Reduce(arena, bdata, L);
                    usMax = math.max(usMax, br.microseconds); usSum += br.microseconds; maxScratch = math.max(maxScratch, br.scratchBytes);
                    if (br.badGuards.Count > 0) failures.Append(ec.id).Append(": GUARD OVERRUN ").Append(string.Join(",", br.badGuards)).Append('\n');
                    if (br.status != (int)ReductionStatus.Ok)
                    {
                        failures.Append(ec.id).Append(": ").Append((ReductionStatus)br.status).Append(" at V=").Append(br.stats.outputV).Append(" rejected=").Append(br.stats.rejected).Append('\n');
                        continue;
                    }
                    ok++;
                    byFamily[ec.family] = (byFamily.TryGetValue(ec.family, out var n) ? n : 0) + 1;
                    if (br.stats.r1Invoked != 0) r1++;
                    double ext = input.MaxExtent();
                    var bp = br.output.ToPoly();
                    var vr = BrepVerifier.Verify(bp, ReducedOptions(input, ext));
                    if (!vr.Ok) failures.Append(ec.id).Append(": ").Append(vr).Append('\n');
                    double excess = SupportSignature.MaxSupportExcess(bp, input, SupportSignature.Dirs42) / ext;
                    if (excess > 1e-6) failures.Append(ec.id).Append(": support excess ").Append(excess.ToString("G3", TestCorpus.Ci)).Append(" (Q not inside C)\n");
                    worstVolume = math.min(worstVolume, ReferenceMassProperties.Volume(bp) / ReferenceMassProperties.Volume(input));
                    // the adopted (reduced) B-rep is the next cut input: cut it again with the product clip kernel
                    double eps = CaseGenerator.DefaultEpsRel * ext;
                    CaseGenerator.MakePlane(bp, "center", 42, eps, out var n2, out var w2);
                    var rc = ClipHarness.RunClip(arena, br.output, (float3)n2, (float)w2, (float)eps);
                    if (rc.status == (int)CutStatus.Ok)
                    {
                        recuts++;
                        var vp = BrepVerifier.Verify(rc.pos.ToPoly(), ReducedOptions(bp, ext, 100000));
                        var vn = BrepVerifier.Verify(rc.neg.ToPoly(), ReducedOptions(bp, ext, 100000));
                        if (!vp.Ok || !vn.Ok) failures.Append(ec.id).Append(": re-cut pos[").Append(vp).Append("] neg[").Append(vn).Append("]\n");
                        if (rc.badGuards.Count > 0) failures.Append(ec.id).Append(": re-cut GUARD OVERRUN\n");
                    }
                    else if (rc.status != (int)CutStatus.NotSplit) failures.Append(ec.id).Append(": re-cut failed with ").Append((CutStatus)rc.status).Append('\n');
                }
            }
            var md = new StringBuilder();
            md.Append("excess cases=").Append(corpus.Count).Append(" reduced=").Append(ok).Append(" R1 invoked=").Append(r1).Append(" recuts=").Append(recuts)
              .Append(" worst volume ratio=").Append(worstVolume.ToString("G6", TestCorpus.Ci)).Append(" scratch max=").Append(maxScratch).Append(" B editor us max=").Append(usMax.ToString("F0", TestCorpus.Ci)).Append(" mean=").Append((corpus.Count > 0 ? usSum / corpus.Count : 0).ToString("F0", TestCorpus.Ci)).Append('\n');
            foreach (var kv in byFamily) md.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            TestContext.Out.WriteLine(md.ToString());
            Assert.That(failures.Length, Is.EqualTo(0), failures.ToString());
            Assert.That(corpus.Count, Is.GreaterThanOrEqualTo(180), "excess corpus size (probe: 193)");
            Assert.That(ok, Is.EqualTo(corpus.Count));
            Assert.That(r1, Is.GreaterThan(0), "the R1 local cavity path must be exercised");
            Assert.That(worstVolume, Is.GreaterThan(0.9), "worst single-reduction volume ratio (probe: 0.9367)");
        }

        [Test]
        public void ScratchContents_DoNotChangeTheReduction()
        {
            var corpus = ExcessCorpus();
            int compared = 0, r1Seen = 0;
            using (var arena = new SentinelArena(16 << 20))
            {
                for (int k = 0; k < corpus.Count; k += 5)
                {
                    var ec = corpus[k];
                    var bdata = ConvexBrepData.FromPoly(ec.input);
                    var a = ReductionHarness.Reduce(arena, bdata, L, 0xA5);
                    Assert.That(a.status, Is.EqualTo((int)ReductionStatus.Ok), ec.id);
                    if (a.stats.r1Invoked != 0) r1Seen++;
                    // zeroed, all-ones, arbitrary and pseudo-random scratch must all give the same removal sequence and output
                    var runs = new List<ReductionHarness.Run>
                    {
                        ReductionHarness.Reduce(arena, bdata, L, 0x00), ReductionHarness.Reduce(arena, bdata, L, 0xFF), ReductionHarness.Reduce(arena, bdata, L, 0x3C),
                        ReductionHarness.Reduce(arena, bdata, L, 0x00, 2, 0xC0FFEEu), ReductionHarness.Reduce(arena, bdata, L, 0x00, 2, 0xBADF00Du),
                    };
                    foreach (var b in runs)
                    {
                        Assert.That(b.status, Is.EqualTo(a.status), ec.id);
                        Assert.That(b.stats.removedR0, Is.EqualTo(a.stats.removedR0), ec.id + " removedR0");
                        Assert.That(b.stats.removedR1, Is.EqualTo(a.stats.removedR1), ec.id + " removedR1");
                        Assert.That(b.stats.r1Invoked, Is.EqualTo(a.stats.r1Invoked), ec.id + " r1Invoked");
                        Assert.That(b.stats.evaluations, Is.EqualTo(a.stats.evaluations), ec.id + " evaluations");
                        ConvexClipKernelTests.AssertSameBrep(a.output, b.output, ec.id);
                    }
                    compared++;
                }
            }
            TestContext.Out.WriteLine("compared=" + compared + " with R1=" + r1Seen);
            Assert.That(compared, Is.GreaterThan(20));
            Assert.That(r1Seen, Is.GreaterThan(0));
        }

        [Test]
        public void R0Only_StopsAtExhaustion_AndFullChainRescues()
        {
            var corpus = ExcessCorpus();
            int exhausted = 0, rescued = 0;
            using (var arena = new SentinelArena(16 << 20))
            {
                foreach (var ec in corpus)
                {
                    var bdata = ConvexBrepData.FromPoly(ec.input);
                    var r0 = ReductionHarness.Reduce(arena, bdata, L, 0xA5, 0);
                    if (r0.status == (int)ReductionStatus.Ok) continue;
                    Assert.That(r0.status, Is.EqualTo((int)ReductionStatus.ReductionFailed), ec.id);
                    Assert.That(r0.stats.outputV, Is.GreaterThan(L), ec.id + ": R0 exhaustion must stop above L");
                    exhausted++;
                    var full = ReductionHarness.Reduce(arena, bdata, L);
                    Assert.That(full.status, Is.EqualTo((int)ReductionStatus.Ok), ec.id + ": the R0 -> R1 chain must rescue R0 exhaustion on the probe corpus");
                    Assert.That(full.stats.r1Invoked, Is.EqualTo(1), ec.id);
                    rescued++;
                }
            }
            TestContext.Out.WriteLine("R0 exhausted=" + exhausted + " rescued by R1=" + rescued + " (probe: 48)");
            Assert.That(exhausted, Is.GreaterThan(0));
        }

        [Test]
        public void NotNeeded_LeavesTheInputUntouched()
        {
            using (var arena = new SentinelArena(4 << 20))
            {
                var data = ConvexBrepData.FromPoly(CaseGenerator.RandomSphereHull(128, 5, 1));
                var r = ReductionHarness.Reduce(arena, data, L);
                Assert.That(r.status, Is.EqualTo((int)ReductionStatus.NotNeeded));
                Assert.That(r.badGuards, Is.Empty);
                ConvexClipKernelTests.AssertSameBrep(data, r.output, "unchanged");
            }
        }

        [Test]
        public void RepeatedCutsWithReduction_Depth8_ThroughTheKernels()
        {
            var failures = new StringBuilder();
            int gens = 0, reductions = 0; double worstCumulative = 1;
            using (var arena = new SentinelArena(16 << 20))
            {
                foreach (var (name, shape0) in new[] { ("cone128", CaseGenerator.Cone(128)), ("bipyramid128", CaseGenerator.Bipyramid(128)), ("sphereHull128", CaseGenerator.RandomSphereHull(128, 128, 1)), ("prism32", CaseGenerator.Prism(32, 1, 0.6)) })
                {
                    var cur = ConvexBrepData.FromPoly(shape0);
                    double cumulative = 1;
                    for (int gen = 1; gen <= 8; gen++)
                    {
                        var poly = cur.ToPoly();
                        double eps = CaseGenerator.DefaultEpsRel * poly.MaxExtent();
                        CaseGenerator.MakePlane(poly, gen % 2 == 1 ? "grazing" : "center", 900 + gen, eps, out var n, out var w);
                        var rc = ClipHarness.RunClip(arena, cur, (float3)n, (float)w, (float)eps);
                        gens++;
                        if (rc.status == (int)CutStatus.NotSplit) break;
                        if (rc.status != (int)CutStatus.Ok) { failures.Append(name).Append(" gen").Append(gen).Append(": cut ").Append((CutStatus)rc.status).Append('\n'); break; }
                        var side = ReferenceMassProperties.Volume(rc.pos.ToPoly()) >= ReferenceMassProperties.Volume(rc.neg.ToPoly()) ? rc.pos : rc.neg;
                        var sidePoly = side.ToPoly();
                        double ext = sidePoly.MaxExtent();
                        var next = side;
                        if (side.V > L)
                        {
                            var red = ReductionHarness.Reduce(arena, side, L);
                            if (red.status != (int)ReductionStatus.Ok) { failures.Append(name).Append(" gen").Append(gen).Append(": reduction ").Append((ReductionStatus)red.status).Append('\n'); break; }
                            reductions++;
                            var rp = red.output.ToPoly();
                            var vr = BrepVerifier.Verify(rp, ReducedOptions(sidePoly, ext));
                            if (!vr.Ok) failures.Append(name).Append(" gen").Append(gen).Append(": ").Append(vr).Append('\n');
                            cumulative *= ReferenceMassProperties.Volume(rp) / ReferenceMassProperties.Volume(sidePoly);
                            next = red.output;
                        }
                        else
                        {
                            var vr = BrepVerifier.Verify(sidePoly, new BrepVerifier.Options { relTol = 4e-6, maxVertices = L, source = poly, extentOverride = poly.MaxExtent(), distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution });
                            if (!vr.Ok) failures.Append(name).Append(" gen").Append(gen).Append(": ").Append(vr).Append('\n');
                        }
                        // containment independent of face planes (sources carry residue / flat caps after earlier generations)
                        double excess = SupportSignature.MaxSupportExcess(next.ToPoly(), poly, SupportSignature.Dirs42) / poly.MaxExtent();
                        if (excess > 1e-5) failures.Append(name).Append(" gen").Append(gen).Append(": support excess over the previous generation ").Append(excess.ToString("G3", TestCorpus.Ci)).Append('\n');
                        worstCumulative = math.min(worstCumulative, cumulative);
                        cur = next;
                    }
                }
            }
            TestContext.Out.WriteLine("generations=" + gens + " reductions=" + reductions + " worst cumulative volume ratio=" + worstCumulative.ToString("G6", TestCorpus.Ci) + " (probe: 0.9962)");
            Assert.That(failures.Length, Is.EqualTo(0), failures.ToString());
            Assert.That(reductions, Is.GreaterThan(0));
            Assert.That(worstCumulative, Is.GreaterThan(0.99));
        }
    }
}
