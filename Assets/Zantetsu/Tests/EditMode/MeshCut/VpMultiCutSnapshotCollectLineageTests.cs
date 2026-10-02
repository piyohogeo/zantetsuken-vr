using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The Collect stage's chain walks taking the ancestors' facts the same structure build has read already -- the
    /// validation's, or an earlier branch's -- and reading from the ledger only those it has not (TL, 2026-10-01). Against
    /// every chain walk reading every ancestor from the ledger: the same snapshots, item by item and in order, the same
    /// outcome, first refusal and its reason, and shortage kind -- on the fixed inputs (display sets, arrays and lists; a
    /// pending cut, an aborted one, a retired fragment, partly reflected geometry; a candidate shortage, a chain too deep, a
    /// retired fragment past an Ignored boundary), and on one snapshot used on and on: the ledger changed under it, another
    /// ledger, a build refused in the validation and one refused in the collection, then the first ledger again, each
    /// compared with a fresh snapshot too, so that nothing of an earlier build is read. The validation's and the
    /// collection's visits, ledger reads and kept facts are counted apart, and the whole structure build is timed as well
    /// as its stages, so that a cost moved to another stage would show; those are written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotCollectLineageTests
    {
        private static VpMultiCutSnapshot NewSnapshot(bool old, int candidates = 131072, int chainDepth = 256)
            => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, candidates, 16384, 262144, chainDepth)) { collectLineageOffForTest = old };

        private struct Result
        {
            public VpMultiCutBuildOutcome outcome;
            public VpMultiCutInvalidInput invalid;
            public VpMultiCutShortage shortage;
            public List<string> contents;
            public VpValidateCounts counts;
        }

        private static Result BuildOn(VpMultiCutSnapshot s, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)
        {
            var before = new VpValidateCounts();
            before.CopyFrom(s.ValidateCounts);
            VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations);
            var d = new VpValidateCounts();
            d.AddDifference(s.ValidateCounts, before);
            return new Result
            {
                outcome = o, invalid = s.InvalidInputReason, shortage = s.Shortage,
                contents = o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null,
                counts = d,
            };
        }

        private static void Same(string what, Result now, Result old)
        {
            Assert.That((now.outcome, now.invalid, now.shortage), Is.EqualTo((old.outcome, old.invalid, old.shortage)), what + ": the same outcome, reason and shortage");
            Assert.That(now.contents == null, Is.EqualTo(old.contents == null), what);
            if (old.contents == null) return;
            Assert.That(now.contents.Count, Is.EqualTo(old.contents.Count), what + ": as many items");
            for (int i = 0; i < old.contents.Count; i++) Assert.That(now.contents[i], Is.EqualTo(old.contents[i]), what + ": item " + i);
        }

        private static void Counted(string what, Result now, Result old)
        {
            VpValidateCounts n = now.counts, o = old.counts;
            TestContext.Out.WriteLine(what + ": " + now.outcome + "/" + now.invalid + "/" + now.shortage
                + "; validation ancestors " + n.ancestorSteps + " (reads " + n.ancestorReads + ", kept " + n.ancestorHits + ")"
                + "; collection ancestors " + n.collectVisits + " (reads " + n.collectReads + ", kept " + n.collectHits + "), old " + o.collectVisits + " (reads " + o.collectReads + ", kept " + o.collectHits + ")"
                + "; chain boundaries " + n.chainSteps + " (old " + o.chainSteps + "), candidates " + n.candidatesMade + " (old " + o.candidatesMade + "), operations read " + n.operationReads + " (old " + o.operationReads + ")");
            Assert.That(n.collectVisits, Is.EqualTo(o.collectVisits), what + ": the same ancestors visited");
            Assert.That(n.chainSteps, Is.EqualTo(o.chainSteps), what + ": the same chain boundaries");
            Assert.That(n.candidatesMade, Is.EqualTo(o.candidatesMade), what + ": the same candidates made");
            Assert.That(o.collectHits, Is.Zero, what + ": the old walk keeps nothing");
            Assert.That(o.collectReads, Is.EqualTo(o.collectVisits), what + ": the old walk reads every ancestor");
            Assert.That(n.collectReads + n.collectHits, Is.EqualTo(n.collectVisits), what + ": every visit read or kept");
            Assert.That(n.collectReads, Is.LessThanOrEqualTo(o.collectReads), what + ": no more reads");
            Assert.That(n.ancestorSteps, Is.EqualTo(o.ancestorSteps), what + ": the validation's walk unchanged");
            Assert.That(n.ancestorReads, Is.EqualTo(o.ancestorReads), what + ": the validation's reads hold none of the collection's");
            Assert.That(n.ancestorReads + n.ancestorHits, Is.EqualTo(n.ancestorSteps), what + ": every validation step read or kept");
        }

        [Test]
        public void TheCollectionTakingTheBuildsLineageFacts_BuildsAndRefusesAsBefore()
        {
            var cases = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, int chainDepth, bool refused)>();
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            cases.Add(("deep (pending, aborted, retired, partly reflected), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations), 131072, 256, false));
            cases.Add(("deep, plain arrays", deep.ledger, deep.registrations, 131072, 256, false));
            var lists = new List<VpMultiCutRegistration>();
            foreach (VpMultiCutRegistration g in deep.registrations)
                lists.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, new List<VpClipBoundary>(g.reflected), g.vertexEpsilon));
            cases.Add(("deep, lists", deep.ledger, lists, 131072, 256, false));
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            cases.Add(("building-like, display sets", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br), 131072, 256, false));
            VpMultiCutSnapshotReflectedIndexTests.Input shortInput = VpMultiCutSnapshotReflectedIndexTests.Deep(4, 20);
            cases.Add(("candidates short", shortInput.ledger, shortInput.registrations, 8, 256, true));
            VpMultiCutSnapshotReflectedIndexTests.Input chain = VpMultiCutSnapshotReflectedIndexTests.Deep(2, 30);
            cases.Add(("chain depth short", chain.ledger, chain.registrations, 131072, 16, true));
            VpMultiCutSnapshotReflectedIndexTests.Input retired = VpMultiCutSnapshotReflectedIndexTests.Deep(1, 12);
            for (int i = 0; i < retired.registrations.Count; i++)
            {
                VpMultiCutRegistration g = retired.registrations[i];
                retired.registrations[i] = new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>(), g.vertexEpsilon);
            }

            cases.Add(("retired past an Ignored boundary", retired.ledger, retired.registrations, 131072, 256, true));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, int chainDepth, bool refused) in cases)
            {
                Result old = BuildOn(NewSnapshot(true, candidates, chainDepth), ledger, registrations);
                Result now = BuildOn(NewSnapshot(false, candidates, chainDepth), ledger, registrations);
                Counted(what, now, old);
                Same(what, now, old);
                Assert.That(old.outcome != VpMultiCutBuildOutcome.Built, Is.EqualTo(refused), what + (refused ? ": refused" : ": built"));
            }
        }

        // A small history with every id known: lineages cut depth deep, the deepest cut's two children registered (each
        // geometry reflecting its whole chain, every third all but two boundaries, or none at all), two levels published
        // below each root, and under the first root one cut pending with its sides prepared.
        private sealed class Tree
        {
            public LogicalCutLedger ledger;
            public readonly List<VpMultiCutRegistration> registrations = new List<VpMultiCutRegistration>();
            public readonly List<LogicalFragmentId> leaves = new List<LogicalFragmentId>();
            public CutOperationId pending;
            public int k;
        }

        private static float4 Plane(int k) => new float4(0f, 1f, 0f, -0.05f * (k % 10 + 1));

        private static CutOperationId Cut(LogicalCutLedger ledger, LogicalFragmentId source, int k, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return cut;
        }

        private static Tree MakeTree(int lineages, int depth, bool reflectNothing = false)
        {
            var t = new Tree { ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)) };
            int r = 0;
            for (int l = 0; l < lineages; l++)
            {
                LogicalFragmentId at = t.ledger.AddFragment();
                var chain = new List<VpClipBoundary>();
                LogicalFragmentId positive = default, negative = default;
                CutOperationId last = default;
                for (int d = 0; d < depth; d++)
                {
                    last = Cut(t.ledger, at, t.k++, out positive, out negative);
                    if (d < depth - 1)
                    {
                        chain.Add(new VpClipBoundary(new VpCapFace(t.ledger, last), (d & 1) == 0 ? 1f : -1f));
                        at = (d & 1) == 0 ? positive : negative;
                    }
                }

                foreach ((LogicalFragmentId root, float side) in new[] { (positive, 1f), (negative, -1f) })
                {
                    var reflected = new List<VpClipBoundary>(chain) { new VpClipBoundary(new VpCapFace(t.ledger, last), side) };
                    if (reflectNothing) reflected.Clear();
                    else if (r % 3 == 2 && reflected.Count > 2) reflected.RemoveRange(0, 2);
                    Cut(t.ledger, root, t.k++, out LogicalFragmentId a, out LogicalFragmentId b);
                    Cut(t.ledger, a, t.k++, out LogicalFragmentId c, out LogicalFragmentId e);
                    t.leaves.Add(c);
                    t.leaves.Add(e);
                    if (l == 0 && side > 0f)
                    {
                        Assert.That(t.ledger.Admit(b, Plane(t.k++), true, out t.pending), Is.EqualTo(LogicalCutAdmission.Admitted));
                        Assert.That(t.ledger.PrepareAnchorDistribution(t.pending, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                    }
                    else
                    {
                        t.leaves.Add(b);
                    }

                    t.registrations.Add(new VpMultiCutRegistration(root, new Bounds(Vector3.zero, Vector3.one * 2f), Matrix4x4.Translate(new Vector3(3f * r, 0f, 0f)), Matrix4x4.identity, reflected.ToArray(), 0.001f));
                    r++;
                }
            }

            return t;
        }

        [Test]
        public void OneSnapshotUsedOnAndOn_NeverReadsAnEarlierBuildsFacts()
        {
            const int candidates = 512;
            VpMultiCutSnapshot old = NewSnapshot(true, candidates);
            VpMultiCutSnapshot now = NewSnapshot(false, candidates);
            void Step(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, VpMultiCutBuildOutcome expected)
            {
                Result o = BuildOn(old, ledger, registrations);
                Result n = BuildOn(now, ledger, registrations);
                Result fresh = BuildOn(NewSnapshot(false, candidates), ledger, registrations);
                Counted(what, n, o);
                Same(what + " (old walk)", n, o);
                Same(what + " (a fresh snapshot)", n, fresh);
                Assert.That(n.outcome, Is.EqualTo(expected), what);
            }

            Tree first = MakeTree(3, 12);
            Step("1. the first ledger", first.ledger, first.registrations, VpMultiCutBuildOutcome.Built);

            // 2. The ledger changed under the same snapshot: the pending cut published, cuts below two leaves, one aborted,
            // a leaf retired, and more fragments than the facts held (their arrays grow).
            Assert.That(first.ledger.Publish(first.pending, out LogicalFragmentId p, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Cut(first.ledger, p, first.k++, out _, out _);
            Cut(first.ledger, first.leaves[0], first.k++, out LogicalFragmentId deeper, out _);
            Cut(first.ledger, deeper, first.k++, out _, out _);
            Assert.That(first.ledger.Admit(first.leaves[1], Plane(first.k++), true, out CutOperationId aborted), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(first.ledger.Abort(aborted), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(first.ledger.Retire(first.leaves[2]), Is.True);
            for (int i = 0; i < 400; i++) first.ledger.AddFragment();
            Step("2. the same ledger changed", first.ledger, first.registrations, VpMultiCutBuildOutcome.Built);

            // 3. Another ledger, its ids over the same range with other facts.
            Tree other = MakeTree(2, 20);
            Step("3. another ledger", other.ledger, other.registrations, VpMultiCutBuildOutcome.Built);

            // 4. Refused in the validation (a root registered twice), then in the collection (the candidates short, after the
            // validation and some branches read their facts).
            var twice = new List<VpMultiCutRegistration>(other.registrations) { other.registrations[0] };
            Step("4a. refused in the validation", other.ledger, twice, VpMultiCutBuildOutcome.InvalidInput);
            Tree many = MakeTree(3, 40, reflectNothing: true);
            Step("4b. refused in the collection", many.ledger, many.registrations, VpMultiCutBuildOutcome.CapacityExceeded);
            Assert.That(now.Shortage, Is.EqualTo(VpMultiCutShortage.Candidates));

            // 5. Built again after the refusals, and the first ledger again.
            Step("5a. another ledger after the refusals", other.ledger, other.registrations, VpMultiCutBuildOutcome.Built);
            Step("5b. the first ledger again", first.ledger, first.registrations, VpMultiCutBuildOutcome.Built);
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        [Test]
        public void TheStructureBuildWholeAndItsStages_OldAndNew_AreWrittenOut()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            inputs.Add(("building-like (2,029 registrations, display sets)", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 80);
            inputs.Add(("deep 80 (20 registrations, candidates below them), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                var times = new Dictionary<bool, List<double>[]>();
                var last = new Dictionary<bool, VpValidateCounts>();
                for (int repeat = 0; repeat < 9; repeat++)
                {
                    foreach (bool oldWalk in new[] { true, false })
                    {
                        VpMultiCutSnapshot s = NewSnapshot(oldWalk);
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        Result r = BuildOn(s, ledger, registrations);
                        Assert.That(r.outcome, Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        VpValidateCounts d = r.counts;
                        double validate = d.indexSeconds + d.inputSeconds + d.ancestorSeconds + d.operationsSeconds;
                        if (!times.ContainsKey(oldWalk)) times[oldWalk] = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>() };
                        times[oldWalk][0].Add(d.structureSeconds * 1000); times[oldWalk][1].Add(validate * 1000); times[oldWalk][2].Add(d.collectSeconds * 1000); times[oldWalk][3].Add(d.collectIntoSeconds * 1000);
                        last[oldWalk] = d;
                    }
                }

                foreach (bool oldWalk in new[] { true, false })
                {
                    VpValidateCounts c = last[oldWalk];
                    TestContext.Out.WriteLine(what + (oldWalk ? ", old (every ancestor read)" : ", new (the build's facts)") + " (medians of 9): structure " + Median(times[oldWalk][0]).ToString("F3")
                        + " ms, validation " + Median(times[oldWalk][1]).ToString("F3") + " (index and arrays " + (c.indexSeconds * 1000).ToString("F3") + ", ancestors " + (c.ancestorSeconds * 1000).ToString("F3") + "), Collect " + Median(times[oldWalk][2]).ToString("F3")
                        + " (chains " + Median(times[oldWalk][3]).ToString("F3") + "); validation ancestors " + c.ancestorSteps + " (reads " + c.ancestorReads + ", kept " + c.ancestorHits + "), collection ancestors " + c.collectVisits
                        + " (reads " + c.collectReads + ", kept " + c.collectHits + "), branches " + c.branches + ", chain boundaries " + c.chainSteps + ", operations read " + c.operationReads + ", candidates " + c.candidatesMade);
                }
            }
        }
    }
}
