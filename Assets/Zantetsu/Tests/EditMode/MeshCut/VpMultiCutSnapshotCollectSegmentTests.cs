using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The registration roots' chains taken in by the collections (TL, 2026-10-01): the cost of a branch's collection split
    /// into building its chain and the rest (the reflected matching, the order check and the candidates), timed per whole
    /// pass over the branches, never per step; then, against every collection walking and matching its whole chain, the
    /// same snapshots item by item and in order, the same outcome, first refusal, reason and shortage -- on the fixed inputs
    /// (display sets, arrays, lists; pending, aborted, retired, partly reflected; a candidate and a chain-depth shortage; a
    /// retired fragment past an Ignored boundary), with the order check's scan forced at several depths, one root given
    /// different reflected sets build after build, and one snapshot used on through a ledger change, another ledger and
    /// refusals. The whole structure build and its stages are timed old and new in this one binary, with the segments' held
    /// and allocated elements; those are written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotCollectSegmentTests
    {
        private static VpMultiCutSnapshot NewSnapshot(bool old, int candidates = 131072, int chainDepth = 256)
            => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, candidates, 16384, 262144, chainDepth)) { collectSegmentsOffForTest = old };

        private struct Result
        {
            public VpMultiCutBuildOutcome outcome;
            public VpMultiCutInvalidInput invalid;
            public VpMultiCutShortage shortage;
            public List<string> contents;
            public VpValidateCounts counts;
            public int fallbacks;
        }

        private static Result BuildOn(VpMultiCutSnapshot s, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)
        {
            var before = new VpValidateCounts();
            before.CopyFrom(s.ValidateCounts);
            int fallbacks = VpClipCandidates.OrderFallbacks;
            VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations);
            var d = new VpValidateCounts();
            d.AddDifference(s.ValidateCounts, before);
            return new Result
            {
                outcome = o, invalid = s.InvalidInputReason, shortage = s.Shortage,
                contents = o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null,
                counts = d, fallbacks = VpClipCandidates.OrderFallbacks - fallbacks,
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
            TestContext.Out.WriteLine(what + ": " + now.outcome + "/" + now.invalid + "/" + now.shortage + " (order scans new " + now.fallbacks + ", old " + old.fallbacks + ")"
                + "; collection: chain boundaries " + n.chainSteps + " (old " + o.chainSteps + "), ancestors visited " + n.collectVisits + " (old " + o.collectVisits + "), reflected lookups " + n.collectLookups + " (old " + o.collectLookups + ")"
                + ", segments taken in " + n.collectSplices + " (" + n.collectSegmentBoundaries + " boundaries), ledger reads " + n.collectReads + " (old " + o.collectReads + "), operations read " + n.operationReads + " (old " + o.operationReads + ")"
                + ", candidates " + n.candidatesMade + " (old " + o.candidatesMade + "); validation steps " + n.ancestorSteps + " (old " + o.ancestorSteps + "), kept for the segments " + n.segmentEntries);
            Assert.That(now.fallbacks, Is.EqualTo(old.fallbacks), what + ": the same order scans");
            Assert.That(n.chainSteps, Is.EqualTo(o.chainSteps), what + ": the same logical chain length");
            Assert.That(n.candidatesMade, Is.EqualTo(o.candidatesMade), what + ": the same candidates made");
            Assert.That(n.collectVisits, Is.LessThanOrEqualTo(o.collectVisits), what + ": no more ancestors visited");
            Assert.That(n.collectLookups, Is.LessThanOrEqualTo(o.collectLookups), what + ": no more reflected lookups");
            Assert.That(o.collectSplices, Is.Zero, what + ": the old path takes no segment");
            Assert.That(n.ancestorSteps, Is.EqualTo(o.ancestorSteps), what + ": the validation's walk unchanged");
            Assert.That(n.ancestorReads, Is.EqualTo(o.ancestorReads), what + ": the validation's reads unchanged");
            Assert.That(n.collectSegmentBoundaries + n.collectVisits - n.collectSplices, Is.GreaterThanOrEqualTo(0), what);
        }

        private static List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, int chainDepth, bool refused)> Cases()
        {
            var cases = new List<(string, LogicalCutLedger, List<VpMultiCutRegistration>, int, int, bool)>();
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
            cases.Add(("chain depth short by the segment alone (depth 30, room 31)", chain.ledger, chain.registrations, 131072, 31, true));
            VpMultiCutSnapshotReflectedIndexTests.Input retired = VpMultiCutSnapshotReflectedIndexTests.Deep(1, 12);
            for (int i = 0; i < retired.registrations.Count; i++)
            {
                VpMultiCutRegistration g = retired.registrations[i];
                retired.registrations[i] = new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>(), g.vertexEpsilon);
            }

            cases.Add(("retired past an Ignored boundary", retired.ledger, retired.registrations, 131072, 256, true));
            return cases;
        }

        [Test]
        public void TakingInTheRootsChain_BuildsAndRefusesAsWalkingIt()
        {
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, int chainDepth, bool refused) in Cases())
            {
                Result old = BuildOn(NewSnapshot(true, candidates, chainDepth), ledger, registrations);
                Result now = BuildOn(NewSnapshot(false, candidates, chainDepth), ledger, registrations);
                Counted(what, now, old);
                Same(what, now, old);
                Assert.That(old.outcome != VpMultiCutBuildOutcome.Built, Is.EqualTo(refused), what + (refused ? ": refused" : ": built"));
            }
        }

        [Test]
        public void TheOrderChecksScan_ForcedAtSeveralDepths_ListsAsWalkingIt()
        {
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(3, 12);
            List<VpMultiCutRegistration> sets = VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations);
            try
            {
                // 0: the chain's top (inside every segment); 5: inside the segments; 12, 13: just below the roots (depth 12); 40: no chain reaches it.
                foreach (int at in new[] { 0, 5, 11, 12, 13, 40 })
                {
                    VpClipCandidates.orderViolationAtForTest = at;
                    Result old = BuildOn(NewSnapshot(true), deep.ledger, sets);
                    Result now = BuildOn(NewSnapshot(false), deep.ledger, sets);
                    Counted("order scan forced at " + at + " from the top", now, old);
                    Same("order scan forced at " + at + " from the top", now, old);
                }
            }
            finally
            {
                VpClipCandidates.orderViolationAtForTest = -1;
            }
        }

        [Test]
        public void OneSnapshotUsedOn_NeverTakesAnEarlierBuildsSegment()
        {
            VpMultiCutSnapshot old = NewSnapshot(true, 512), now = NewSnapshot(false, 512);
            void Step(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, VpMultiCutBuildOutcome expected)
            {
                Result o = BuildOn(old, ledger, registrations);
                Result n = BuildOn(now, ledger, registrations);
                Result fresh = BuildOn(NewSnapshot(false, 512), ledger, registrations);
                Counted(what, n, o);
                Same(what + " (old walk)", n, o);
                Same(what + " (a fresh snapshot)", n, fresh);
                Assert.That(n.outcome, Is.EqualTo(expected), what);
            }

            VpMultiCutSnapshotReflectedIndexTests.Input first = VpMultiCutSnapshotReflectedIndexTests.Deep(3, 12);
            List<VpMultiCutRegistration> sets = VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(first.registrations);
            Step("1. the first ledger", first.ledger, sets, VpMultiCutBuildOutcome.Built);

            // 2. The same roots, other reflected sets: the whole chain reflected, then none of it (the same fragment, other sets).
            VpMultiCutSnapshotReflectedIndexTests.Input whole = VpMultiCutSnapshotReflectedIndexTests.Deep(3, 12, fullyReflected: true);
            var none = new List<VpMultiCutRegistration>();
            var all = new List<VpMultiCutRegistration>();
            for (int i = 0; i < first.registrations.Count; i++)
            {
                VpMultiCutRegistration g = first.registrations[i];
                var chain = new List<VpClipBoundary>();
                LogicalFragmentId at = g.root;
                while (first.ledger.TryGetOrigin(at, out CutOperationId origin, out float side) && first.ledger.TryGetOperation(origin, out LogicalCutOperation op))
                {
                    chain.Add(new VpClipBoundary(new VpCapFace(first.ledger, origin), side));
                    at = op.source;
                }

                all.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, chain.ToArray(), g.vertexEpsilon));
                none.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>(), g.vertexEpsilon));
            }

            Step("2a. the same roots, their whole chains reflected", first.ledger, all, VpMultiCutBuildOutcome.Built);
            Step("2b. the same roots, nothing reflected (the retired leaf now past an Ignored boundary)", first.ledger, none, VpMultiCutBuildOutcome.RetiredInsideAggregate);
            Step("2c. the same roots, the first sets again", first.ledger, sets, VpMultiCutBuildOutcome.Built);

            // 3. Another ledger; 4. refused in the validation, then in the collection; 5. built again, the first ledger again.
            Step("3. another ledger", whole.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(whole.registrations), VpMultiCutBuildOutcome.Built);
            var twice = new List<VpMultiCutRegistration>(sets) { sets[0] };
            Step("4a. refused in the validation", first.ledger, twice, VpMultiCutBuildOutcome.InvalidInput);
            VpMultiCutSnapshotReflectedIndexTests.Input many = VpMultiCutSnapshotReflectedIndexTests.Deep(4, 40, fullyReflected: false);
            var manyNone = new List<VpMultiCutRegistration>();
            foreach (VpMultiCutRegistration g in many.registrations) manyNone.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>(), g.vertexEpsilon));
            Result refusedNow = BuildOn(now, many.ledger, manyNone);
            Result refusedOld = BuildOn(old, many.ledger, manyNone);
            Counted("4b. refused in the collection or the group", refusedNow, refusedOld);
            Same("4b. refused in the collection or the group", refusedNow, refusedOld);
            Assert.That(refusedNow.outcome, Is.Not.EqualTo(VpMultiCutBuildOutcome.Built), "4b refused");
            Step("5a. the first ledger after the refusals", first.ledger, sets, VpMultiCutBuildOutcome.Built);
            Step("5b. another ledger again", whole.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(whole.registrations), VpMultiCutBuildOutcome.Built);
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        // The branches' collections as the structure build makes them, outside it: each branch with its registration's index
        // and the lineage facts warm, timed as one pass over every branch (9 passes, the median).
        private static (double whole, double chainOnly, long visits, long lookups, long boundaries, int branches) Split(LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)
        {
            var s = NewSnapshot(true);
            Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            var branches = new List<VpMultiCutBranch>();
            for (int b = 0; b < s.BranchCount; b++) { Assert.That(s.TryGetBranch(b, out VpMultiCutBranch branch), Is.True); branches.Add(branch); }
            var counts = new VpReflectedIndex.Counts();
            var indexes = new VpReflectedIndex[registrations.Count];
            for (int g = 0; g < registrations.Count; g++) { indexes[g] = new VpReflectedIndex(); indexes[g].Fill(registrations[g].reflected, false, counts); }
            var lineage = new VpLineageFacts();
            lineage.Open(ledger);
            var chain = new VpClipBoundary[ledger.OperationCount + 2];
            var into = new VpClipCandidate[131072];
            double Pass(bool chainOnly)
            {
                VpClipCandidates.chainOnlyForTest = chainOnly;
                try
                {
                    long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                    foreach (VpMultiCutBranch b in branches)
                    {
                        VpClipCandidates.CollectInto(ledger, b.fragment, b.pendingSide, true, indexes[b.registration], chain, into, 0, into.Length, out _, lineage);
                    }

                    return (System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                }
                finally
                {
                    VpClipCandidates.chainOnlyForTest = false;
                }
            }

            Pass(false);
            long v0 = VpClipCandidates.LineageVisits, l0 = VpClipCandidates.ReflectedLookups, c0 = VpClipCandidates.ChainSteps;
            Pass(false);
            long visits = VpClipCandidates.LineageVisits - v0, lookups = VpClipCandidates.ReflectedLookups - l0, boundaries = VpClipCandidates.ChainSteps - c0;
            var whole = new List<double>();
            var only = new List<double>();
            for (int r = 0; r < 9; r++) { whole.Add(Pass(false)); only.Add(Pass(true)); }
            lineage.Close();
            return (Median(whole), Median(only), visits, lookups, boundaries, branches.Count);
        }

        private static List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)> CostInputs()
        {
            var inputs = new List<(string, LogicalCutLedger, List<VpMultiCutRegistration>)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            inputs.Add(("building-like (2,029 registrations, display sets)", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 80);
            inputs.Add(("deep 80 (20 registrations, candidates below them), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            return inputs;
        }

        [Test]
        public void TheBranchCollections_ChainAndTheRest_AreWrittenOut()
        {
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in CostInputs())
            {
                var r = Split(ledger, registrations);
                TestContext.Out.WriteLine(what + " (the old walk, every branch's collection, medians of 9): whole " + r.whole.ToString("F3") + " ms, the chain alone " + r.chainOnly.ToString("F3")
                    + " ms, the rest (reflected matching, order check, candidates) " + (r.whole - r.chainOnly).ToString("F3") + " ms; branches " + r.branches + ", ancestors visited " + r.visits + ", chain boundaries " + r.boundaries + ", reflected lookups " + r.lookups);
            }
        }

        [Test]
        public void TheStructureBuildWholeAndItsStages_OldAndNew_AreWrittenOut()
        {
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in CostInputs())
            {
                var times = new Dictionary<bool, List<double>[]>();
                var last = new Dictionary<bool, VpValidateCounts>();
                var held = new Dictionary<bool, string>();
                for (int repeat = 0; repeat < 9; repeat++)
                {
                    foreach (bool oldWalk in new[] { true, false })
                    {
                        VpMultiCutSnapshot s = NewSnapshot(oldWalk);
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        long growths = s.SegmentGrowths, allocated = s.SegmentAllocatedBytes;
                        Result r = BuildOn(s, ledger, registrations);
                        Assert.That(r.outcome, Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        VpValidateCounts d = r.counts;
                        double validate = d.indexSeconds + d.inputSeconds + d.ancestorSeconds + d.operationsSeconds;
                        if (!times.ContainsKey(oldWalk)) times[oldWalk] = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>() };
                        times[oldWalk][0].Add(d.structureSeconds * 1000); times[oldWalk][1].Add(validate * 1000); times[oldWalk][2].Add(d.ancestorSeconds * 1000);
                        times[oldWalk][3].Add(d.collectSeconds * 1000); times[oldWalk][4].Add(d.collectIntoSeconds * 1000);
                        last[oldWalk] = d;
                        (int boundaries, int unreflected, int regs) cap = s.SegmentCapacity;
                        held[oldWalk] = "segments held: capacity " + cap.boundaries + " boundaries, " + cap.unreflected + " offsets, " + cap.regs + " records (about " + s.SegmentHeldBytes + " B); allocated over the first two builds "
                            + growths + " times, about " + allocated + " B; in this build " + (s.SegmentGrowths - growths) + " times";
                    }
                }

                foreach (bool oldWalk in new[] { true, false })
                {
                    VpValidateCounts c = last[oldWalk];
                    TestContext.Out.WriteLine(what + (oldWalk ? ", old (every chain walked and matched)" : ", new (the roots' chains taken in)") + " (medians of 9): structure " + Median(times[oldWalk][0]).ToString("F3")
                        + " ms, validation " + Median(times[oldWalk][1]).ToString("F3") + " (ancestors " + Median(times[oldWalk][2]).ToString("F3") + "), Collect " + Median(times[oldWalk][3]).ToString("F3") + " (chains " + Median(times[oldWalk][4]).ToString("F3")
                        + "); validation steps " + c.ancestorSteps + ", lookups " + c.ancestorLookups + ", kept for segments " + c.segmentEntries + "; collection: branches " + c.branches + ", chain boundaries " + c.chainSteps + ", ancestors visited " + c.collectVisits
                        + ", reflected lookups " + c.collectLookups + ", segments taken in " + c.collectSplices + " (" + c.collectSegmentBoundaries + " boundaries), ledger reads " + c.collectReads + ", operations read " + c.operationReads + ", candidates " + c.candidatesMade + "; " + held[oldWalk]);
                }
            }
        }
    }
}
