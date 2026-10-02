using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// What the structural validation's walk from each registration root up its ancestors repeats (TL, 2026-10-01), on
    /// the building-like history with each registration's reflected set made as the display makes it (the chain's
    /// boundaries in their order): the walk replayed whole in variants -- the ledger's reads alone; with the reflected
    /// lookup; with the root table; and two candidate replacements (the reflected lookup answered first by the set's own
    /// order, the hash asked only where that does not match; and the ancestors' ledger facts read once per build into
    /// arrays). Timed as whole walks (best of 9), never inside the loop; the steps, the distinct ancestors and their
    /// revisits counted. Written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotAncestorWalkTests
    {
        internal static List<VpMultiCutRegistration> WithDisplaySets(List<VpMultiCutRegistration> registrations)
        {
            var sets = new List<VpMultiCutRegistration>(registrations.Count);
            foreach (VpMultiCutRegistration g in registrations)
                sets.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, VpReflectedSet.Of(g.reflected, null), g.vertexEpsilon));
            return sets;
        }

        private delegate long Walk();

        private static double Best(Walk walk, out long result)
        {
            result = walk();   // warm
            double best = double.MaxValue;
            for (int i = 0; i < 9; i++)
            {
                var w = System.Diagnostics.Stopwatch.StartNew();
                result = walk();
                best = System.Math.Min(best, w.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        [Test]
        public void TheAncestorWalk_ItsParts_AndItsRevisits_AreWrittenOut()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> plain, int deepest) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            List<VpMultiCutRegistration> registrations = WithDisplaySets(plain);
            var rootOf = new Dictionary<LogicalFragmentId, int>();
            for (int g = 0; g < registrations.Count; g++) rootOf[registrations[g].root] = g;
            var sets = new VpReflectedSet[registrations.Count];
            for (int g = 0; g < registrations.Count; g++) sets[g] = (VpReflectedSet)registrations[g].reflected;

            // Steps, distinct ancestors, revisits.
            var visits = new Dictionary<LogicalFragmentId, int>();
            long steps = 0;
            foreach (VpMultiCutRegistration r in registrations)
            {
                LogicalFragmentId at = r.root;
                while (ledger.TryGetOrigin(at, out CutOperationId origin, out _) && ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    steps++;
                    at = cut.source;
                    visits.TryGetValue(at, out int n); visits[at] = n + 1;
                }
            }

            int maxRevisits = 0;
            foreach (int n in visits.Values) maxRevisits = System.Math.Max(maxRevisits, n);
            TestContext.Out.WriteLine("registrations " + registrations.Count + ", deepest " + deepest + "; ancestor steps " + steps + " over " + visits.Count + " distinct ancestors (" + (steps / (double)System.Math.Max(1, visits.Count)).ToString("F1") + " visits each on average, the most " + maxRevisits + ")");

            // A: the ledger's reads alone.
            double a = Best(() =>
            {
                long n = 0;
                foreach (VpMultiCutRegistration r in registrations)
                {
                    LogicalFragmentId at = r.root;
                    while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side) && ledger.TryGetOperation(origin, out LogicalCutOperation cut)) { n++; at = cut.source; }
                }

                return n;
            }, out long na);

            // B: with the reflected lookup (the hash).
            double b = Best(() =>
            {
                long n = 0;
                for (int g = 0; g < registrations.Count; g++)
                {
                    LogicalFragmentId at = registrations[g].root;
                    VpReflectedSet set = sets[g];
                    while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side) && ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                    {
                        if (!set.Contains(new VpClipBoundary(new VpCapFace(ledger, origin), side))) n += 1000000;
                        n++; at = cut.source;
                    }
                }

                return n;
            }, out long nb);

            // C: with the root table too (the whole walk as the validation makes it, the plane checks apart: none here).
            double c = Best(() =>
            {
                long n = 0;
                for (int g = 0; g < registrations.Count; g++)
                {
                    LogicalFragmentId at = registrations[g].root;
                    VpReflectedSet set = sets[g];
                    while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side) && ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                    {
                        if (!set.Contains(new VpClipBoundary(new VpCapFace(ledger, origin), side))) n += 1000000;
                        at = cut.source;
                        if (rootOf.TryGetValue(at, out int above) && above != g) n += 1000;
                        n++;
                    }
                }

                return n;
            }, out long nc);

            // P: C with the reflected lookup answered first by the set's own order (the display appends each cut's
            // boundary: the k-th ancestor up is the k-th from the set's end), the hash asked only where that does not match.
            long positionalMisses = 0;
            double p = Best(() =>
            {
                long n = 0; positionalMisses = 0;
                for (int g = 0; g < registrations.Count; g++)
                {
                    LogicalFragmentId at = registrations[g].root;
                    VpReflectedSet set = sets[g];
                    int k = set.Count - 1;
                    while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side) && ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                    {
                        var boundary = new VpClipBoundary(new VpCapFace(ledger, origin), side);
                        bool reflected = k >= 0 && set[k] == boundary;
                        if (!reflected) { positionalMisses++; reflected = set.Contains(boundary); }
                        k--;
                        if (!reflected) n += 1000000;
                        at = cut.source;
                        if (rootOf.TryGetValue(at, out int above) && above != g) n += 1000;
                        n++;
                    }
                }

                return n;
            }, out long np);

            // L: C with the ancestors' ledger facts read once per build into arrays by fragment (the reflected lookup and
            // the root check still each registration's own).
            int fragments = ledger.FragmentCount + 1;
            double l = Best(() =>
            {
                var originOf = new int[fragments]; var sideOf = new float[fragments]; var sourceOf = new int[fragments]; var known = new byte[fragments]; var rootAt = new int[fragments];
                for (int i = 0; i < fragments; i++) rootAt[i] = -1;
                for (int g = 0; g < registrations.Count; g++) rootAt[registrations[g].root.value] = g;
                long n = 0;
                for (int g = 0; g < registrations.Count; g++)
                {
                    int at = registrations[g].root.value;
                    VpReflectedSet set = sets[g];
                    for (;;)
                    {
                        if (known[at] == 0)
                        {
                            known[at] = 1;
                            if (ledger.TryGetOrigin(new LogicalFragmentId(at), out CutOperationId o, out float s) && ledger.TryGetOperation(o, out LogicalCutOperation cut))
                            { known[at] = 2; originOf[at] = o.value; sideOf[at] = s; sourceOf[at] = cut.source.value; }
                        }

                        if (known[at] != 2) break;
                        if (!set.Contains(new VpClipBoundary(new VpCapFace(ledger, new CutOperationId(originOf[at])), sideOf[at]))) n += 1000000;
                        at = sourceOf[at];
                        if (rootAt[at] >= 0 && rootAt[at] != g) n += 1000;
                        n++;
                    }
                }

                return n;
            }, out long nl);

            TestContext.Out.WriteLine("whole walks, best of 9 (ms): A the ledger's reads alone " + a.ToString("F3") + "; B with the reflected lookup " + b.ToString("F3") + "; C with the root table too " + c.ToString("F3")
                + "; P as C, the lookup by the set's order first " + p.ToString("F3") + " (hash asked " + positionalMisses + " times); L as C, the ledger's facts read once per build into arrays " + l.ToString("F3"));
            TestContext.Out.WriteLine("results (the same walk): A " + na + ", B " + nb + ", C " + nc + ", P " + np + ", L " + nl);
            Assert.That(nb, Is.EqualTo(nc), "no root above another here");
            Assert.That(np, Is.EqualTo(nc), "P answers as C");
            Assert.That(nl, Is.EqualTo(nc), "L answers as C");
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static (VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage, List<string>, VpMultiCutSnapshot) BuildWalk(LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, bool old, int candidates = 131072)
        {
            VpReflectedIndex.byPositionOffForTest = old;
            try
            {
                var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, candidates, 16384, 262144, 256)) { lineageArraysOffForTest = old };
                VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations);
                return (o, s.InvalidInputReason, s.Shortage, o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null, s);
            }
            finally
            {
                VpReflectedIndex.byPositionOffForTest = false;
            }
        }

        /// <summary>
        /// The walk reading each ancestor's ledger facts once per structural validation and asking a display set by its
        /// order first builds the same snapshots, item by item and in order, and refuses alike, the first refusal and its
        /// reason included: display sets, plain arrays, a geometry reflecting all but its first boundaries, a registration
        /// with another root above it, a plane outside a registration's section (the first of two such registrations), and
        /// a candidate shortage.
        /// </summary>
        [Test]
        public void TheNewWalk_BuildsAndRefusesAsTheOldOne()
        {
            var cases = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, bool refused)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            cases.Add(("building-like, display sets", bl, WithDisplaySets(br), 131072, false));
            cases.Add(("building-like, plain arrays", bl, br, 131072, false));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            cases.Add(("deep, display sets (some partly reflected)", deep.ledger, WithDisplaySets(deep.registrations), 131072, false));
            cases.Add(("deep, plain arrays", deep.ledger, deep.registrations, 131072, false));
            VpMultiCutSnapshotReflectedIndexTests.Input lineage = VpMultiCutSnapshotReflectedIndexTests.Deep(3, 12);
            VpMultiCutRegistration first = lineage.registrations[0];
            Assert.That(lineage.ledger.TryGetReplacingOperation(first.root, out CutOperationId below), Is.True);
            Assert.That(lineage.ledger.TryGetOperation(below, out LogicalCutOperation op), Is.True);
            lineage.registrations.Add(new VpMultiCutRegistration(op.positive, first.localBounds, Matrix4x4.Translate(new Vector3(-9f, 0f, 0f)), Matrix4x4.identity, System.Array.Empty<VpClipBoundary>(), 0.001f));
            cases.Add(("another root above", lineage.ledger, WithDisplaySets(lineage.registrations), 131072, true));
            VpMultiCutSnapshotReflectedIndexTests.Input planes = VpMultiCutSnapshotReflectedIndexTests.Deep(3, 12);
            foreach (int at in new[] { 2, 4 })
            {
                VpMultiCutRegistration g = planes.registrations[at];
                // Nothing reflected, and the lineage frame carried 2e38 along the planes' normal: the first unreflected
                // ancestor's plane, in the geometry's frame, is past what the section can take (refused at that registration's walk).
                planes.registrations[at] = new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, Matrix4x4.Translate(new Vector3(0f, 2e38f, 0f)), System.Array.Empty<VpClipBoundary>(), g.vertexEpsilon);
            }

            cases.Add(("a plane outside the section (registrations 2 and 4)", planes.ledger, WithDisplaySets(planes.registrations), 131072, true));
            VpMultiCutSnapshotReflectedIndexTests.Input shortInput = VpMultiCutSnapshotReflectedIndexTests.Deep(4, 20);
            cases.Add(("candidates short", shortInput.ledger, WithDisplaySets(shortInput.registrations), 8, true));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, bool refused) in cases)
            {
                var old = BuildWalk(ledger, registrations, true, candidates);
                var now = BuildWalk(ledger, registrations, false, candidates);
                TestContext.Out.WriteLine(what + ": old " + old.Item1 + "/" + old.Item2 + "/" + old.Item3 + ", new " + now.Item1 + "/" + now.Item2 + "/" + now.Item3
                    + "; new walk: steps " + now.Item5.ValidateCounts.ancestorSteps + ", ledger reads " + now.Item5.ValidateCounts.ancestorReads + " (old " + old.Item5.ValidateCounts.ancestorReads + ")");
                Assert.That((now.Item1, now.Item2, now.Item3), Is.EqualTo((old.Item1, old.Item2, old.Item3)), what + ": the same outcome and reason");
                Assert.That(old.Item1 != VpMultiCutBuildOutcome.Built, Is.EqualTo(refused), what + (refused ? ": refused" : ": built"));
                if (old.Item4 == null) continue;
                Assert.That(now.Item4.Count, Is.EqualTo(old.Item4.Count), what + ": as many items");
                for (int i = 0; i < old.Item4.Count; i++) Assert.That(now.Item4[i], Is.EqualTo(old.Item4[i]), what + ": item " + i);
            }
        }

        /// <summary>The ancestor part of a structural validation, old walk against new, on the building-like history with display sets (medians of 9, the third build of each snapshot).</summary>
        [Test]
        public void TheNewWalk_OnABuildingLikeHistory_ItsCost_IsWrittenOut()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> plain, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            List<VpMultiCutRegistration> registrations = WithDisplaySets(plain);
            var ancestors = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
            var total = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
            var last = new Dictionary<bool, VpValidateCounts>();
            var before = new VpValidateCounts();
            try
            {
                for (int repeat = 0; repeat < 9; repeat++)
                {
                    foreach (bool old in new[] { true, false })
                    {
                        VpReflectedIndex.byPositionOffForTest = old;
                        var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 64)) { lineageArraysOffForTest = old };
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        before.CopyFrom(s.ValidateCounts);
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        var d = new VpValidateCounts();
                        d.AddDifference(s.ValidateCounts, before);
                        ancestors[old].Add(d.ancestorSeconds * 1000);
                        total[old].Add(s.LastStructureValidateSeconds * 1000);
                        last[old] = d;
                    }
                }
            }
            finally
            {
                VpReflectedIndex.byPositionOffForTest = false;
            }

            TestContext.Out.WriteLine("old walk: ancestors " + Median(ancestors[true]).ToString("F3") + " ms (validation " + Median(total[true]).ToString("F3") + "); " + last[true].Describe());
            TestContext.Out.WriteLine("new walk: ancestors " + Median(ancestors[false]).ToString("F3") + " ms (validation " + Median(total[false]).ToString("F3") + "); " + last[false].Describe());
        }
    }
}
