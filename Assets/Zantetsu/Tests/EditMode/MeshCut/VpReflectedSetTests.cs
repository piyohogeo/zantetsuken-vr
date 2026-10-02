using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// A display's reflected sets held as unchangeable values with their lookups made once (TL, 2026-10-01), and the
    /// snapshot taking those lookups instead of indexing the sets at every structure build: the same snapshots and
    /// refusals as indexing them again and as scanning them; any other collection read again at every build (changed in
    /// place under the same count, the next build sees it); a set never changed by adding a boundary, the rules of
    /// equality kept; nothing held past a build, registrations reordered read afresh; and the cost moved -- indexing
    /// at the build against making the sets -- and the memory they hold, written out.
    /// </summary>
    public class VpReflectedSetTests
    {
        private static List<VpMultiCutRegistration> AsSets(List<VpMultiCutRegistration> registrations)
        {
            var sets = new List<VpMultiCutRegistration>(registrations.Count);
            foreach (VpMultiCutRegistration g in registrations)
                sets.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, VpReflectedSet.Of(g.reflected, null), g.vertexEpsilon));
            return sets;
        }

        private static (VpMultiCutBuildOutcome outcome, VpMultiCutInvalidInput invalid, VpMultiCutShortage shortage, List<string> contents, VpMultiCutSnapshot snapshot) Build(
            LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, bool scan, bool indexAgain, int candidates = 131072, int chainDepth = 256)
        {
            var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, candidates, 16384, 262144, chainDepth)) { reflectedByScanForTest = scan, reflectedIndexAgainForTest = indexAgain };
            VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations);
            return (o, s.InvalidInputReason, s.Shortage, o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null, s);
        }

        [Test]
        public void TheSetsLookups_BuildTheSameSnapshots_AndRefuseAlike_AsIndexingAgainAndScanning()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, int chainDepth, bool refused)>();
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep", deep.ledger, deep.registrations, 131072, 256, false));
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            inputs.Add(("building-like", bl, br, 131072, 256, false));
            VpMultiCutSnapshotReflectedIndexTests.Input shortInput = VpMultiCutSnapshotReflectedIndexTests.Deep(4, 20);
            inputs.Add(("candidates short", shortInput.ledger, shortInput.registrations, 8, 256, true));
            VpMultiCutSnapshotReflectedIndexTests.Input chain = VpMultiCutSnapshotReflectedIndexTests.Deep(2, 30);
            inputs.Add(("chain depth short", chain.ledger, chain.registrations, 131072, 16, true));
            VpMultiCutSnapshotReflectedIndexTests.Input lineage = VpMultiCutSnapshotReflectedIndexTests.Deep(3, 12);
            VpMultiCutRegistration first = lineage.registrations[0];
            Assert.That(lineage.ledger.TryGetReplacingOperation(first.root, out CutOperationId below), Is.True);
            Assert.That(lineage.ledger.TryGetOperation(below, out LogicalCutOperation op), Is.True);
            lineage.registrations.Add(new VpMultiCutRegistration(op.positive, first.localBounds, Matrix4x4.Translate(new Vector3(-9f, 0f, 0f)), Matrix4x4.identity, System.Array.Empty<VpClipBoundary>(), 0.001f));
            inputs.Add(("lineage", lineage.ledger, lineage.registrations, 131072, 256, true));

            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int candidates, int chainDepth, bool refused) in inputs)
            {
                List<VpMultiCutRegistration> sets = AsSets(registrations);
                var scan = Build(ledger, registrations, true, false, candidates, chainDepth);
                var again = Build(ledger, sets, false, true, candidates, chainDepth);
                var reuse = Build(ledger, sets, false, false, candidates, chainDepth);
                TestContext.Out.WriteLine(what + ": scan " + scan.outcome + "/" + scan.invalid + "/" + scan.shortage + ", indexed again " + again.outcome + ", the sets' lookups " + reuse.outcome
                    + " (indexes built " + reuse.snapshot.ReflectedIndexesBuilt + ", reused " + reuse.snapshot.ReflectedIndexesReused + "; indexed again built " + again.snapshot.ReflectedIndexesBuilt + ")");
                foreach (var other in new[] { again, reuse })
                {
                    Assert.That((other.outcome, other.invalid, other.shortage), Is.EqualTo((scan.outcome, scan.invalid, scan.shortage)), what + ": the same outcome and reason");
                    if (scan.contents == null) continue;
                    Assert.That(other.contents.Count, Is.EqualTo(scan.contents.Count), what + ": as many items");
                    for (int i = 0; i < scan.contents.Count; i++) Assert.That(other.contents[i], Is.EqualTo(scan.contents[i]), what + ": item " + i);
                }

                Assert.That(scan.outcome != VpMultiCutBuildOutcome.Built, Is.EqualTo(refused), what + (refused ? ": refused" : ": built"));
                if (!refused) Assert.That(reuse.snapshot.ReflectedIndexesBuilt, Is.EqualTo(0), what + ": no set indexed again");
                Assert.That(reuse.snapshot.HoldsReflectedForTest || again.snapshot.HoldsReflectedForTest || scan.snapshot.HoldsReflectedForTest, Is.False, what + ": nothing held past the build");
            }
        }

        [Test]
        public void AListChangedInPlace_UnderTheSameCount_IsReadAgainByTheNextBuild()
        {
            VpMultiCutSnapshotReflectedIndexTests.Input input = VpMultiCutSnapshotReflectedIndexTests.Deep(2, 10, fullyReflected: true);
            var lists = new List<List<VpClipBoundary>>();
            var registrations = new List<VpMultiCutRegistration>();
            foreach (VpMultiCutRegistration g in input.registrations)
            {
                var list = new List<VpClipBoundary>(g.reflected);
                lists.Add(list);
                registrations.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, list, g.vertexEpsilon));
            }

            var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
            Assert.That(s.TryBuild(input.ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int before = s.CandidateCount;
            // The same list, the same count: its first two boundaries no longer reflected (replaced by boundaries of no cut here).
            var elsewhere = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            for (int i = 0; i < 2; i++) lists[0][i] = new VpClipBoundary(new VpCapFace(elsewhere, lists[0][i].face.operation), lists[0][i].side);
            Assert.That(s.TryBuild(input.ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            var fresh = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
            Assert.That(fresh.TryBuild(input.ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            TestContext.Out.WriteLine("candidates before " + before + ", after the list changed in place " + s.CandidateCount + " (a fresh snapshot " + fresh.CandidateCount + "); indexes built " + s.ReflectedIndexesBuilt + ", reused " + s.ReflectedIndexesReused);
            Assert.That(s.CandidateCount, Is.GreaterThan(before), "the boundaries no longer reflected are candidates at the next build");
            Assert.That(VpMultiCutSnapshotReflectedIndexTests.Contents(s, input.ledger), Is.EqualTo(VpMultiCutSnapshotReflectedIndexTests.Contents(fresh, input.ledger)), "the same as a fresh snapshot of the changed list");
            Assert.That(s.ReflectedIndexesReused, Is.EqualTo(0), "a list is never taken by a lookup of its own");
        }

        [Test]
        public void ASet_IsNeverChangedByAddingABoundary_AndKeepsTheRulesOfEquality()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            var other = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            LogicalFragmentId root = ledger.AddFragment();
            Assert.That(ledger.Admit(root, new float4(0f, 1f, 0f, 0f), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            var a = new VpClipBoundary(new VpCapFace(ledger, cut), 1f);
            var counts = new VpReflectedSet.Counts();
            VpReflectedSet empty = VpReflectedSet.Of(System.Array.Empty<VpClipBoundary>(), counts);
            VpReflectedSet one = empty.With(a, counts);
            VpReflectedSet two = one.With(a, counts);   // a repeat kept
            VpReflectedSet zero = empty.With(new VpClipBoundary(new VpCapFace(ledger, cut), -0f), counts);
            Assert.That(empty.Count, Is.EqualTo(0), "the set added to is not changed");
            Assert.That(one.Count, Is.EqualTo(1));
            Assert.That(two.Count, Is.EqualTo(2), "repeats kept, in order");
            Assert.That(new List<VpClipBoundary>(two), Is.EqualTo(new List<VpClipBoundary> { a, a }), "the order kept");
            Assert.That(empty.Contains(a), Is.False);
            Assert.That(one.Contains(a), Is.True);
            Assert.That(one.Contains(new VpClipBoundary(new VpCapFace(ledger, cut), -1f)), Is.False, "the other side");
            Assert.That(one.Contains(new VpClipBoundary(new VpCapFace(other, cut), 1f)), Is.False, "the same number from another ledger");
            Assert.That(zero.Contains(new VpClipBoundary(new VpCapFace(ledger, cut), 0f)), Is.True, "+0 equals -0");
            Assert.That(one.With(new VpClipBoundary(new VpCapFace(ledger, cut), float.NaN), counts).Contains(new VpClipBoundary(new VpCapFace(ledger, cut), float.NaN)), Is.False, "a NaN side is never in it");
            Assert.That(counts.made, Is.EqualTo(5), "each set made once");
            Assert.That(typeof(VpReflectedSet).IsPublic, Is.False, "the type is the display's own");
        }

        private static System.WeakReference MakeBuildAndForget(LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, VpMultiCutSnapshot s)
        {
            var sets = AsSets(registrations);
            Assert.That(s.TryBuild(ledger, sets), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            return new System.WeakReference(sets[0].reflected);
        }

        [Test]
        public void NothingIsHeldPastABuild_AndReorderedRegistrationsAreReadAfresh()
        {
            VpMultiCutSnapshotReflectedIndexTests.Input input = VpMultiCutSnapshotReflectedIndexTests.Deep(4, 12);
            var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
            System.WeakReference set = MakeBuildAndForget(input.ledger, input.registrations, s);
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
            Assert.That(set.IsAlive, Is.False, "a registration's set is not kept by the snapshot after its build");
            Assert.That(s.HoldsReflectedForTest, Is.False);

            List<VpMultiCutRegistration> sets = AsSets(input.registrations);
            var reversed = new List<VpMultiCutRegistration>(sets);
            reversed.Reverse();
            Assert.That(s.TryBuild(input.ledger, sets), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(s.TryBuild(input.ledger, reversed), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            var fresh = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
            Assert.That(fresh.TryBuild(input.ledger, reversed), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(VpMultiCutSnapshotReflectedIndexTests.Contents(s, input.ledger), Is.EqualTo(VpMultiCutSnapshotReflectedIndexTests.Contents(fresh, input.ledger)), "the reordered registrations read afresh");
            // A registration left out (retired) and a refused build: nothing held either way.
            sets.RemoveAt(1);
            Assert.That(s.TryBuild(input.ledger, sets), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(s.HoldsReflectedForTest, Is.False);
            var small = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 4, 16384, 262144, 256));
            Assert.That(small.TryBuild(input.ledger, sets), Is.Not.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(small.HoldsReflectedForTest, Is.False, "nothing held after a refused build");
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        [Test]
        public void TheCostMoved_AndTheMemoryHeld_OnABuildingLikeHistory_AreWrittenOut()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            // The sets made as the display makes them: each leaf's from its parent's by one boundary (a commit), the
            // registration's first from nothing (a registration). Made once, timed, and their memory taken.
            var counts = new VpReflectedSet.Counts();
            long memoryBefore = System.GC.GetTotalMemory(true);
            var made = new List<VpReflectedSet>(registrations.Count);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (VpMultiCutRegistration g in registrations) made.Add(VpReflectedSet.Of(g.reflected, counts));
            double makeMs = watch.Elapsed.TotalMilliseconds;
            long memoryAfter = System.GC.GetTotalMemory(true);
            long entries = 0;
            foreach (VpReflectedSet set in made) entries += set.Count;
            // One more boundary each, as a commit makes a child's set.
            var more = new VpReflectedSet.Counts();
            watch.Restart();
            var dummy = new VpClipBoundary(new VpCapFace(ledger, new CutOperationId(1)), 1f);
            foreach (VpReflectedSet set in made) set.With(dummy, more);
            double withMs = watch.Elapsed.TotalMilliseconds;

            var withSets = new List<VpMultiCutRegistration>(registrations.Count);
            for (int i = 0; i < registrations.Count; i++)
            {
                VpMultiCutRegistration g = registrations[i];
                withSets.Add(new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, made[i], g.vertexEpsilon));
            }

            var index = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
            var total = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
            var before = new VpValidateCounts();
            for (int repeat = 0; repeat < 7; repeat++)
            {
                foreach (bool again in new[] { true, false })
                {
                    var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 64)) { reflectedIndexAgainForTest = again };
                    Assert.That(s.TryBuild(ledger, withSets), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    Assert.That(s.TryBuild(ledger, withSets), Is.EqualTo(VpMultiCutBuildOutcome.Built));   // the second build: its own index tables already grown
                    before.CopyFrom(s.ValidateCounts);
                    Assert.That(s.TryBuild(ledger, withSets), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    var d = new VpValidateCounts();
                    d.AddDifference(s.ValidateCounts, before);
                    index[again].Add(d.indexSeconds * 1000);
                    total[again].Add(s.LastStructureValidateSeconds * 1000);
                }
            }

            TestContext.Out.WriteLine("registrations " + registrations.Count + ", boundaries in their sets " + entries);
            TestContext.Out.WriteLine("making the sets (a registration each): " + makeMs.ToString("F3") + " ms (" + counts.made + " sets, " + counts.entries + " boundaries, the sets' own timing " + (counts.seconds * 1000).ToString("F3") + " ms); one more boundary each (a commit each): " + withMs.ToString("F3") + " ms; the memory they hold about " + (memoryAfter - memoryBefore) + " bytes (" + ((memoryAfter - memoryBefore) / (double)System.Math.Max(1, entries)).ToString("F1") + " a boundary)");
            TestContext.Out.WriteLine("a structure build's index part, medians of 7 (the third build of a snapshot): indexed again " + Median(index[true]).ToString("F3") + " ms (validation " + Median(total[true]).ToString("F3") + "), the sets' lookups " + Median(index[false]).ToString("F3") + " ms (validation " + Median(total[false]).ToString("F3") + ")");
        }
    }
}
