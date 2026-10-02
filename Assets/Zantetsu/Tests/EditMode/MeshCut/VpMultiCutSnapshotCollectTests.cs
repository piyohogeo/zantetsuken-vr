using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The structure build's Collect stage (TL, 2026-10-01): its parts -- the branch walk, the chains collected from the
    /// ancestors, the selections, the cap identities -- with their counts, timed per collection, never per chain step;
    /// and a chain boundary the geometry reflects no longer having its operation read a second time (the reflected set
    /// asked first). The same snapshots, item by item and in order, and the same refusals, the first one and its reason
    /// included, as reading every boundary's operation first: display sets and plain arrays (a list changed in place
    /// still read again), a pending cut, an aborted one, a retired fragment, partly reflected geometry, a candidate
    /// shortage, a chain too deep, a retired fragment past an Ignored boundary. The parts and the reads, old and new,
    /// written out.
    /// </summary>
    public class VpMultiCutSnapshotCollectTests
    {
        private static (VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage, List<string>, VpValidateCounts) Build(LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, bool old, int candidates = 131072, int chainDepth = 256)
        {
            VpClipCandidates.reflectedAfterReadForTest = old;
            try
            {
                var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, candidates, 16384, 262144, chainDepth));
                VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations);
                return (o, s.InvalidInputReason, s.Shortage, o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null, s.ValidateCounts);
            }
            finally
            {
                VpClipCandidates.reflectedAfterReadForTest = false;
            }
        }

        [Test]
        public void ReadingAReflectedBoundarysOperationNoMore_BuildsAndRefusesAsBefore()
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
                var old = Build(ledger, registrations, true, candidates, chainDepth);
                var now = Build(ledger, registrations, false, candidates, chainDepth);
                TestContext.Out.WriteLine(what + ": old " + old.Item1 + "/" + old.Item2 + "/" + old.Item3 + ", new " + now.Item1 + "/" + now.Item2 + "/" + now.Item3
                    + "; operations read old " + old.Item5.operationReads + ", new " + now.Item5.operationReads + "; candidates " + now.Item5.candidatesMade + " (old " + old.Item5.candidatesMade + ")");
                Assert.That((now.Item1, now.Item2, now.Item3), Is.EqualTo((old.Item1, old.Item2, old.Item3)), what + ": the same outcome and reason");
                Assert.That(old.Item1 != VpMultiCutBuildOutcome.Built, Is.EqualTo(refused), what + (refused ? ": refused" : ": built"));
                if (old.Item4 == null) continue;
                Assert.That(now.Item4.Count, Is.EqualTo(old.Item4.Count), what + ": as many items");
                for (int i = 0; i < old.Item4.Count; i++) Assert.That(now.Item4[i], Is.EqualTo(old.Item4[i]), what + ": item " + i);
                Assert.That(now.Item5.candidatesMade, Is.EqualTo(old.Item5.candidatesMade), what + ": the same candidates made");
                Assert.That(now.Item5.operationReads, Is.LessThanOrEqualTo(old.Item5.operationReads), what + ": no more reads");
            }
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        [Test]
        public void TheCollectParts_OldAndNew_AreWrittenOut()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            inputs.Add(("building-like (2,029 registrations, display sets)", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 80);
            inputs.Add(("deep 80 (20 registrations, candidates below them), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            var before = new VpValidateCounts();
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                var parts = new Dictionary<bool, List<double>[]>();
                var last = new Dictionary<bool, VpValidateCounts>();
                try
                {
                    for (int repeat = 0; repeat < 9; repeat++)
                    {
                        foreach (bool old in new[] { true, false })
                        {
                            VpClipCandidates.reflectedAfterReadForTest = old;
                            var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
                            Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            before.CopyFrom(s.ValidateCounts);
                            Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            var d = new VpValidateCounts();
                            d.AddDifference(s.ValidateCounts, before);
                            if (!parts.ContainsKey(old)) parts[old] = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>() };
                            parts[old][0].Add(d.collectSeconds * 1000); parts[old][1].Add(d.collectIntoSeconds * 1000); parts[old][2].Add(d.selectSeconds * 1000); parts[old][3].Add(d.capIdentitySeconds * 1000);
                            last[old] = d;
                        }
                    }
                }
                finally
                {
                    VpClipCandidates.reflectedAfterReadForTest = false;
                }

                foreach (bool old in new[] { true, false })
                {
                    VpValidateCounts c = last[old];
                    double collect = Median(parts[old][0]), chains = Median(parts[old][1]), select = Median(parts[old][2]), cap = Median(parts[old][3]);
                    TestContext.Out.WriteLine(what + (old ? ", old (read first)" : ", new (reflected first)") + ": Collect " + collect.ToString("F3") + " ms = chains " + chains.ToString("F3") + " + selection " + select.ToString("F3") + " + cap identities " + cap.ToString("F3")
                        + " + the branch walk (the rest) " + (collect - chains - select - cap).ToString("F3") + " (medians of 9); branches " + c.branches + ", collections " + c.collectCalls + ", chain boundaries " + c.chainSteps + ", operations read " + c.operationReads + ", candidates " + c.candidatesMade + ", cap identities " + c.capIdentities);
                }
            }
        }
    }
}
