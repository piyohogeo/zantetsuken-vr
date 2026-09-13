using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Owner-level numerical contract (DESIGN 7.2 / 7.6 / T-085 / T-086 numerical parts): side inheritance, exact clip,
    /// reduction, mass conservation, capacity reservation, scratch independence, input immutability and re-cut of the
    /// adopted outputs, all in the common local frame.
    /// </summary>
    public class ConvexCutOwnerKernelTests
    {
        static readonly (string name, int count, int split, int seed)[] k_scenarios =
        {
            ("single-v64", 1, 1, 100), ("single-v128", 1, 1, 110), ("c4-10pct", 4, 1, 120), ("c4-50pct", 4, 2, 130), ("c4-100pct", 4, 4, 140),
            ("c16-10pct", 16, 2, 150), ("c16-50pct", 16, 8, 160), ("c16-100pct", 16, 16, 170), ("c8-0pct", 8, 0, 180),
        };

        static OwnerCutHarness Scenario((string name, int count, int split, int seed) s)
        {
            int[] series = s.count == 1 ? (s.name.Contains("128") ? new[] { 128 } : new[] { 64 }) : OwnerFixtures.Mixed;
            return OwnerFixtures.Compound(s.count, s.split, series, s.seed, 1, 0);
        }

        static void AssertPositiveDefinite(in SymmetricMatrix3 m, string what)
        {
            Assert.That(m.IsFinite, Is.True, what + " finite");
            Assert.That(m.xx, Is.GreaterThan(0), what + " xx");
            Assert.That(m.xx * m.yy - m.xy * m.xy, Is.GreaterThan(0), what + " minor2");
            Assert.That(math.determinant(m.ToMatrix()), Is.GreaterThan(0), what + " det");
        }

        static void AssertMassContract(OwnerCutHarness h, in ConvexCutOwnerResult r, string what)
        {
            Assert.That(r.positiveMass, Is.GreaterThan(0), what + " positive mass");
            Assert.That(r.negativeMass, Is.GreaterThan(0), what + " negative mass");
            Assert.That(math.abs(r.positiveMass + r.negativeMass - h.parentMass) / h.parentMass, Is.LessThanOrEqualTo(1e-12), what + " parent mass conserved");
            Assert.That(math.all(math.isfinite(r.positiveCenterOfMass)) && math.all(math.isfinite(r.negativeCenterOfMass)), Is.True, what + " COM finite");
            AssertPositiveDefinite(in r.positiveInertia, what + " positive inertia");
            AssertPositiveDefinite(in r.negativeInertia, what + " negative inertia");
            // the kernel's double integration over the adopted sets must match the managed reference (volume, COM, inertia)
            foreach (var positive in new[] { true, false })
            {
                var set = h.AdoptedSet(positive);
                var acc = new MassProperties();
                double3 mn = new double3(double.MaxValue), mx = new double3(double.MinValue);
                foreach (var p in set) { acc = acc + ReferenceMassProperties.Compute(p); p.Bounds(out var a, out var b); mn = math.min(mn, a); mx = math.max(mx, b); }
                double vol = positive ? r.positiveVolume : r.negativeVolume;
                double3 com = positive ? r.positiveCenterOfMass : r.negativeCenterOfMass;
                var inertia = positive ? r.positiveInertia : r.negativeInertia;
                Assert.That(math.abs(vol - acc.volume) / acc.volume, Is.LessThanOrEqualTo(1e-9), what + (positive ? " positive" : " negative") + " volume vs reference");
                double ext = math.cmax(mx - mn);
                Assert.That(math.distance(com, acc.CenterOfMass), Is.LessThanOrEqualTo(1e-9 * ext), what + " COM vs reference");
                Assert.That(math.all(com >= mn - 1e-9 * ext) && math.all(com <= mx + 1e-9 * ext), Is.True, what + " COM inside the side's bounds");
                double rho = h.parentMass / (r.positiveVolume + r.negativeVolume);
                var refI = acc.InertiaAboutCom() * rho;
                double scale = math.max(math.abs(refI.xx), math.max(math.abs(refI.yy), math.abs(refI.zz)));
                Assert.That(math.abs(inertia.xx - refI.xx) + math.abs(inertia.yy - refI.yy) + math.abs(inertia.zz - refI.zz) + math.abs(inertia.xy - refI.xy) + math.abs(inertia.xz - refI.xz) + math.abs(inertia.yz - refI.yz), Is.LessThanOrEqualTo(1e-9 * scale), what + " inertia vs reference");
            }
        }

        static void AssertOutputsValid(OwnerCutHarness h, in ConvexCutOwnerResult r, string what, int maxVertices = 128)
        {
            int splits = 0, pos = 0, neg = 0;
            for (int c = 0; c < h.data.Count; c++)
            {
                var o = h.Outcome(c);
                Assert.That(o.side, Is.EqualTo(h.SideOf(c)), what + " convex " + c + " side echoed");
                if (!o.IsSplit)
                {
                    if (o.InheritsPositive) pos++; else neg++;
                    Assert.That(o.positive.vertexCount + o.negative.vertexCount, Is.EqualTo(0), what + " inherited convex has no output range");
                    continue;
                }
                splits++; pos++; neg++;
                var src = h.data[c].ToPoly();
                double ext = src.MaxExtent();
                foreach (var (range, side) in new[] { (o.positive, +1), (o.negative, -1) })
                {
                    Assert.That(range.vertexCount, Is.GreaterThanOrEqualTo(4), what + " output V");
                    Assert.That(range.vertexCount, Is.LessThanOrEqualTo(maxVertices), what + " output V <= L");
                    Assert.That(range.vertexBase + range.vertexCount, Is.LessThanOrEqualTo(h.output.vertexCapacity), what + " vertex range inside the arena");
                    Assert.That(range.faceBase + range.faceCount + 1, Is.LessThanOrEqualTo(h.output.faceOffsetCapacity), what + " face range inside the arena");
                    Assert.That(range.faceIndexBase + range.faceIndexCount, Is.LessThanOrEqualTo(h.output.faceIndexCapacity), what + " index range inside the arena");
                    Assert.That(range.edgeBase + range.edgeCount, Is.LessThanOrEqualTo(h.output.edgeCapacity), what + " edge range inside the arena");
                    var data = h.ReadOutput(in range);
                    Assert.That(data.Lmax, Is.EqualTo(range.maxFaceLoop), what + " maxFaceLoop of the output range");
                    Assert.That(data.V - data.E + data.F, Is.EqualTo(2), what + " output Euler");
                    var poly = data.ToPoly();
                    // exact half-space when no reduction happened; a reduced side is only required to stay inside the clip result
                    bool reduced = o.removedVertices > 0;
                    var opt = new BrepVerifier.Options { relTol = 4e-6, maxVertices = maxVertices, source = src, extentOverride = ext, checkHalfspace = true, planeN = (float3)h.planeN, planeW = h.planeW, side = side, halfspaceTol = 4e-6 * ext, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution };
                    var vr = BrepVerifier.Verify(poly, opt);
                    Assert.That(vr.Ok, Is.True, what + " convex " + c + (side > 0 ? " positive" : " negative") + ": " + vr + (reduced ? " (reduced)" : ""));
                }
            }
            Assert.That(r.splitConvexCount, Is.EqualTo(splits), what + " split count");
            Assert.That(r.positiveConvexCount, Is.EqualTo(pos), what + " positive count");
            Assert.That(r.negativeConvexCount, Is.EqualTo(neg), what + " negative count");
            Assert.That(r.usedVertices, Is.LessThanOrEqualTo(r.reservedVertices), what + " used <= reserved vertices");
            Assert.That(r.reservedVertices, Is.LessThanOrEqualTo(h.capacity.vertices), what + " reserved <= capacity vertices");
            Assert.That(r.reservedFaceOffsets, Is.LessThanOrEqualTo(h.capacity.faceOffsets), what);
            Assert.That(r.reservedFaceIndices, Is.LessThanOrEqualTo(h.capacity.faceIndices), what);
            Assert.That(r.reservedEdges, Is.LessThanOrEqualTo(h.capacity.edges), what);
            Assert.That(r.usedScratchBytes, Is.LessThanOrEqualTo(h.capacity.scratchBytes), what + " scratch");
            Assert.That(h.CheckGuards(), Is.Empty, what + " guard overrun");
        }

        [Test]
        public void CompoundScenarios_SideInheritance_Clip_MassConservation()
        {
            var log = new StringBuilder();
            foreach (var s in k_scenarios)
            {
                using (var h = Scenario(s))
                {
                    h.Build();
                    var r = h.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), s.name + " status (failedConvex=" + r.failedConvex + " cut=" + r.cutStatus + " red=" + r.reductionStatus + ")");
                    Assert.That(r.executedManaged, Is.EqualTo(0), s.name + " ran managed");
                    Assert.That(r.splitConvexCount, Is.EqualTo(s.split), s.name + " expected split convexes");
                    AssertOutputsValid(h, in r, s.name);
                    AssertMassContract(h, in r, s.name);
                    log.Append(s.name).Append(": split=").Append(r.splitConvexCount).Append(" pos/neg=").Append(r.positiveConvexCount).Append('/').Append(r.negativeConvexCount)
                       .Append(" mass=").Append(r.positiveMass.ToString("F6", TestCorpus.Ci)).Append('+').Append(r.negativeMass.ToString("F6", TestCorpus.Ci))
                       .Append(" reserved v/f/i/e=").Append(r.reservedVertices).Append('/').Append(r.reservedFaceOffsets).Append('/').Append(r.reservedFaceIndices).Append('/').Append(r.reservedEdges)
                       .Append(" used=").Append(r.usedVertices).Append('/').Append(r.usedFaceOffsets).Append('/').Append(r.usedFaceIndices).Append('/').Append(r.usedEdges).Append(" scratch=").Append(r.usedScratchBytes).Append('\n');
                }
            }
            TestContext.Out.WriteLine(log.ToString());
        }

        [Test]
        public void ZeroSplitCompound_SucceedsWithoutOutput()
        {
            using (var h = Scenario(k_scenarios[8]))
            {
                h.Build();
                Assert.That(h.capacity.vertices, Is.EqualTo(0));
                Assert.That(h.capacity.splitConvexCount, Is.EqualTo(0));
                var r = h.Execute();
                Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                Assert.That(r.splitConvexCount, Is.EqualTo(0));
                Assert.That(r.positiveConvexCount, Is.GreaterThan(0));
                Assert.That(r.negativeConvexCount, Is.GreaterThan(0));
                Assert.That(r.usedVertices, Is.EqualTo(0));
                AssertMassContract(h, in r, "c8-0pct");
            }
        }

        [Test]
        public void JobPath_And_DirectCall_ProduceTheSameOutput()
        {
            using (var h = Scenario(k_scenarios[7]))
            {
                h.Build();
                var direct = h.Execute();
                Assert.That(direct.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                ulong outDirect = h.OutputHash();
                h.ScrambleScratch(0x51DE);
                var job = h.ExecuteViaJob();
                Assert.That(job.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                Assert.That(job.executedManaged, Is.EqualTo(0));
                Assert.That(h.OutputHash(), Is.EqualTo(outDirect), "job path output differs from the direct call");
                Assert.That(BitConverter.DoubleToInt64Bits(job.positiveMass), Is.EqualTo(BitConverter.DoubleToInt64Bits(direct.positiveMass)));
                Assert.That(BitConverter.DoubleToInt64Bits(job.negativeInertia.xy), Is.EqualTo(BitConverter.DoubleToInt64Bits(direct.negativeInertia.xy)));
                Assert.That(h.CheckGuards(), Is.Empty);
            }
        }

        [Test]
        public void Input_IsNeverModified()
        {
            foreach (var h in new[] { Scenario(k_scenarios[7]), OwnerFixtures.ReduceHeavy(0) })
            {
                using (h)
                {
                    h.Build();
                    ulong before = h.InputHash();
                    var r = h.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                    Assert.That(h.InputHash(), Is.EqualTo(before), "direct call modified the input");
                    h.ExecuteViaJob();
                    Assert.That(h.InputHash(), Is.EqualTo(before), "job modified the input");
                }
            }
        }

        [Test]
        public void ScratchReuse_AndFill_DoNotChangeTheResult()
        {
            foreach (var (name, h) in new[] { ("c16-100pct", Scenario(k_scenarios[7])), ("reduce-heavy", OwnerFixtures.ReduceHeavy(0)), ("reduce-light", OwnerFixtures.ReduceLight(0)) })
            {
                using (h)
                {
                    h.Build(0xA5);
                    var first = h.Execute();
                    Assert.That(first.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), name);
                    ulong outHash = h.OutputHash();
                    long mass = BitConverter.DoubleToInt64Bits(first.positiveMass);
                    // reuse without any reset, then with zero / all-ones / random fills
                    for (int round = 0; round < 5; round++)
                    {
                        if (round == 1) h.arena.Scramble("scratch", 0);
                        if (round == 2) h.ScrambleScratch(0xDEADBEEF);
                        if (round == 3) h.ScrambleScratch(0x12345678);
                        if (round == 4) { h.Build(0x00); }
                        var again = h.Execute();
                        Assert.That(again.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), name + " round " + round);
                        Assert.That(h.OutputHash(), Is.EqualTo(outHash), name + " round " + round + ": output changed with scratch contents");
                        Assert.That(BitConverter.DoubleToInt64Bits(again.positiveMass), Is.EqualTo(mass), name + " round " + round + " mass");
                        Assert.That(again.removedVertices, Is.EqualTo(first.removedVertices), name + " round " + round + " removed");
                        Assert.That(again.r1Invocations, Is.EqualTo(first.r1Invocations), name + " round " + round + " R1");
                        Assert.That(h.CheckGuards(), Is.Empty, name);
                    }
                }
            }
        }

        [Test]
        public void CapacityQuery_MatchesTheExecutionPlan_AndDeficitsFailSafely()
        {
            foreach (var (name, make) in new (string, Func<OwnerCutHarness>)[] { ("c16-100pct", () => Scenario(k_scenarios[7])), ("reduce-heavy", () => OwnerFixtures.ReduceHeavy(0)), ("c4-50pct", () => Scenario(k_scenarios[3])) })
            {
                using (var h = make())
                {
                    h.Build();
                    var r = h.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), name);
                    Assert.That(r.reservedVertices, Is.EqualTo(h.capacity.vertices), name + " plan == query (vertices)");
                    Assert.That(r.reservedFaceOffsets, Is.EqualTo(h.capacity.faceOffsets), name + " plan == query (face offsets)");
                    Assert.That(r.reservedFaceIndices, Is.EqualTo(h.capacity.faceIndices), name + " plan == query (face indices)");
                    Assert.That(r.reservedEdges, Is.EqualTo(h.capacity.edges), name + " plan == query (edges)");
                    Assert.That(r.usedScratchBytes, Is.EqualTo(h.capacity.scratchBytes), name + " plan == query (scratch)");
                    Assert.That(h.capacity.OutputConvexCount, Is.EqualTo(2 * r.splitConvexCount), name);
                }
                using (var h = make())
                {
                    h.Build(vertexDeficit: 1);
                    var r = h.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.CapacityOutput), name + " one vertex short");
                    Assert.That(r.failedConvex, Is.GreaterThanOrEqualTo(0), name);
                    Assert.That(h.CheckGuards(), Is.Empty, name + " capacity failure wrote past the reserved range");
                    var rj = h.ExecuteViaJob();
                    Assert.That(rj.status, Is.EqualTo(ConvexCutOwnerStatus.CapacityOutput), name);
                }
                using (var h = make())
                {
                    h.Build(scratchDeficit: 1);
                    var r = h.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.CapacityScratch), name + " one scratch byte short");
                    Assert.That(h.CheckGuards(), Is.Empty, name);
                }
            }
        }

        [Test]
        public void ReductionOwners_LightAndHeavy_StayWithinL_AndInsideTheClip()
        {
            foreach (var (name, h, expectR1) in new[] { ("reduce-light", OwnerFixtures.ReduceLight(0), false), ("reduce-heavy", OwnerFixtures.ReduceHeavy(0), true) })
            {
                using (h)
                {
                    h.Build();
                    var r = h.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), name + " (red=" + (ReductionStatus)r.reductionStatus + ")");
                    Assert.That(r.reducedConvexCount, Is.GreaterThan(0), name + " reduction must run");
                    Assert.That(r.removedVertices, Is.GreaterThan(0), name);
                    if (expectR1) Assert.That(r.r1Invocations, Is.GreaterThan(0), name + " expected the R1 path");
                    var o = h.Outcome(0);
                    Assert.That(o.IsSplit, Is.True);
                    var src = h.data[0].ToPoly();
                    double ext = src.MaxExtent();
                    foreach (var (range, side) in new[] { (o.positive, +1), (o.negative, -1) })
                    {
                        Assert.That(range.vertexCount, Is.LessThanOrEqualTo(128), name + " V <= L");
                        var poly = h.ReadOutputPoly(in range);
                        // Q ⊆ C = P ∩ H: inside the source and inside the adopted half-space (7.2 construction condition)
                        var vr = BrepVerifier.Verify(poly, new BrepVerifier.Options { relTol = 4e-6, maxVertices = 128, source = src, extentOverride = ext, checkHalfspace = true, planeN = (float3)h.planeN, planeW = h.planeW, side = side, halfspaceTol = 4e-6 * ext, distinctRelTol = 0, resolutionRelTol = BrepVerifier.FloatResolution });
                        Assert.That(vr.Ok, Is.True, name + (side > 0 ? " positive: " : " negative: ") + vr);
                    }
                    AssertMassContract(h, in r, name);
                    TestContext.Out.WriteLine(name + ": removed=" + r.removedVertices + " R1=" + r.r1Invocations + " outV=" + o.positive.vertexCount + "/" + o.negative.vertexCount + " scratch=" + r.usedScratchBytes);
                }
            }
        }

        [Test]
        public void EmptySide_IsReported()
        {
            using (var h = new OwnerCutHarness().Add(CaseGenerator.Box()).Add(CaseGenerator.Box().Transformed(1, new double3(3, 0, 0))).Plane(new float3(1, 0, 0), 5f, 1e-5f))
            {
                h.Build();
                Assert.That(h.SideOf(0), Is.EqualTo(ConvexSide.Positive));
                Assert.That(h.SideOf(1), Is.EqualTo(ConvexSide.Positive));
                var r = h.Execute();
                Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.EmptySide));
                Assert.That(r.negativeConvexCount, Is.EqualTo(0));
                Assert.That(r.positiveMass, Is.EqualTo(0));
            }
        }

        [Test]
        public void NearPlaneConvex_InheritsPositiveUncut()
        {
            // a sliver whose vertices all lie within +-0.3 eps of the plane has no robust support: 7.6 sends it uncut to the positive side
            float eps = 1e-5f;
            var sliver = CaseGenerator.Prism(6, 1, 0.3 * eps).Rotated(quaternion.RotateZ(math.PI / 2)).Transformed(1, new double3(0, 3, 0));
            using (var h = new OwnerCutHarness().Add(CaseGenerator.RandomSphereHull(32, 3, 1)).Add(CaseGenerator.Box().Transformed(1, new double3(-4, 0, 0))).Add(sliver).Plane(new float3(1, 0, 0), 0, eps))
            {
                h.Build();
                Assert.That(h.SideOf(0), Is.EqualTo(ConvexSide.Split));
                Assert.That(h.SideOf(1), Is.EqualTo(ConvexSide.Negative));
                Assert.That(h.SideOf(2), Is.EqualTo(ConvexSide.NearPlaneToPositive));
                var r = h.Execute();
                Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok));
                Assert.That(h.Outcome(2).side, Is.EqualTo(ConvexSide.NearPlaneToPositive));
                Assert.That(h.Outcome(2).IsSplit, Is.False);
                Assert.That(h.Outcome(2).InheritsPositive, Is.True);
                Assert.That(r.positiveConvexCount, Is.EqualTo(2));
                Assert.That(r.negativeConvexCount, Is.EqualTo(2));
                AssertMassContract(h, in r, "near-plane");
            }
        }

        [Test]
        public void AdoptedOutputs_AreTheNextInput_Depth3()
        {
            // the positive adopted set of each generation (split outputs + inherited convexes, rounding residue included) becomes a new owner
            var current = Scenario(k_scenarios[3]);
            double parentMass = current.parentMass;
            try
            {
                for (int gen = 1; gen <= 3; gen++)
                {
                    current.parentMass = parentMass;
                    current.Build();
                    var r = current.Execute();
                    Assert.That(r.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), "gen " + gen + " (failedConvex=" + r.failedConvex + " cut=" + (CutStatus)r.cutStatus + ")");
                    Assert.That(r.splitConvexCount, Is.GreaterThan(0), "gen " + gen + " must split something");
                    AssertOutputsValid(current, in r, "gen " + gen);
                    AssertMassContract(current, in r, "gen " + gen);
                    var set = current.AdoptedSet(true);
                    parentMass = r.positiveMass;
                    var next = new OwnerCutHarness();
                    double3 mn = new double3(double.MaxValue), mx = new double3(double.MinValue);
                    ConvexPoly largest = null; double largestVolume = -1;
                    foreach (var p in set) { next.Add(p); p.Bounds(out var a, out var b); mn = math.min(mn, a); mx = math.max(mx, b); double vol = ReferenceMassProperties.Volume(p); if (vol > largestVolume) { largestVolume = vol; largest = p; } }
                    // the next plane passes through the centroid of the largest adopted convex so that every generation splits something
                    double3 c = largest.VertexCentroid();
                    var rng = new Unity.Mathematics.Random((uint)(77 + gen));
                    double3 n = math.normalize(new double3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1));
                    next.Plane((float3)n, (float)(-math.dot(n, c)), (float)(CaseGenerator.DefaultEpsRel * math.cmax(mx - mn)));
                    current.Dispose();
                    current = next;
                }
            }
            finally { current.Dispose(); }
        }

        [Test]
        public void CommonLocalFrame_RecenteredInputKeepsReferenceAccuracy()
        {
            // P1 (DESIGN 7.2 numerical frame): the same compound expressed near the origin vs. at offset/extent ~ 500.
            // The recentered run must agree with the double reference; the offset run's error is reported (probe §11: 1e-4 and failures at 1e3).
            double3 offset = new double3(1e3, 0.37e3, -0.21e3);
            var log = new StringBuilder();
            double worstCentered = 0, worstOffset = 0; int offsetFailures = 0;
            foreach (var (name, place) in new (string, double3)[] { ("centered", 0), ("offset", offset) })
            {
                using (var h = OwnerFixtures.Compound(4, 2, OwnerFixtures.Mixed, 130, 1, place))
                {
                    h.Build();
                    var r = h.Execute();
                    if (r.status != ConvexCutOwnerStatus.Ok) { if (name == "offset") { offsetFailures++; continue; } Assert.Fail("centered run failed: " + r.status); }
                    for (int c = 0; c < h.data.Count; c++)
                    {
                        var o = h.Outcome(c);
                        if (!o.IsSplit) continue;
                        var src = h.polys[c];   // double positions
                        var rr = ReferenceDoubleClip.Cut(src, (double3)h.planeN, h.planeW, h.eps);
                        Assert.That(rr.status, Is.EqualTo(ReferenceDoubleClip.Status.Ok));
                        double ext = src.MaxExtent();
                        double e = math.max(SupportSignature.MaxSupportDiff(h.ReadOutputPoly(in o.positive), rr.positive, SupportSignature.Dirs42), SupportSignature.MaxSupportDiff(h.ReadOutputPoly(in o.negative), rr.negative, SupportSignature.Dirs42)) / ext;
                        if (name == "centered") worstCentered = math.max(worstCentered, e); else worstOffset = math.max(worstOffset, e);
                    }
                }
            }
            log.Append("support error vs double reference (rel. extent): centered=").Append(worstCentered.ToString("G2", TestCorpus.Ci)).Append(" offset(500 extents)=").Append(worstOffset.ToString("G2", TestCorpus.Ci)).Append(" offsetFailures=").Append(offsetFailures);
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(worstCentered, Is.LessThan(2e-6), "recentered (P1) frame must keep float-kernel accuracy");
        }
    }
}
