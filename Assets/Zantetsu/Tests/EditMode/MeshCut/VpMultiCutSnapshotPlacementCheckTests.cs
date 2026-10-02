using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// A placement query's answer checked once (TL, 2026-10-01): the checked baseline is the placement, so the second check of
    /// the same matrix is gone. Against checking it twice (the old pass): the same snapshots item by item and in order, the
    /// same outcome, reason and shortage -- structural builds and placement-only builds alike, with the placements Following
    /// (Transforms), Static, one Missing, no provider at all, a valid scale, and a NaN, a non-affine and a singular matrix;
    /// the provider asked as often either way. The same binary's cost, old and new: the placement-only pass with nothing
    /// moved, a tenth moved and everything moved, and the structural build -- the Place pass and the whole build, medians of
    /// 9. Written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotPlacementCheckTests
    {
        private sealed class Answers : IVpFragmentPlacement
        {
            public Func<LogicalFragmentId, (VpFragmentPlacementKind kind, Matrix4x4 m)> answer;
            public int asked;

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                asked++;
                (VpFragmentPlacementKind kind, Matrix4x4 m) = answer(fragment);
                geometryLocalToWorld = m;
                return kind;
            }
        }

        private static VpMultiCutSnapshot NewSnapshot() => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));

        private struct Result
        {
            public VpMultiCutBuildOutcome outcome;
            public VpMultiCutInvalidInput invalid;
            public VpMultiCutShortage shortage;
            public List<string> contents;
            public int asked;
            public long checks;
        }

        private static Result Run(bool old, Func<VpMultiCutSnapshot, Answers, VpMultiCutBuildOutcome> build, Answers answers, LogicalCutLedger ledger)
        {
            VpMultiCutSnapshot.placementCheckTwiceForTest = old;
            try
            {
                var s = NewSnapshot();
                int askedBefore = answers != null ? answers.asked : 0;
                VpMultiCutBuildOutcome o = build(s, answers);
                return new Result
                {
                    outcome = o, invalid = s.InvalidInputReason, shortage = s.Shortage,
                    contents = o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null,
                    asked = answers != null ? answers.asked - askedBefore : 0,
                    checks = s.StructuralPlaceCounts.placementChecks + s.PlacementOnlyPlaceCounts.placementChecks,
                };
            }
            finally
            {
                VpMultiCutSnapshot.placementCheckTwiceForTest = false;
            }
        }

        [Test]
        public void CheckingAnAnswerOnce_BuildsAndRefusesAsCheckingItTwice()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            inputs.Add(("building-like 24 x 40, plain arrays", bl, br));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                // Each fragment's own place: its registration's, moved a little by its id (so that no two coincide).
                Matrix4x4 Place(LogicalFragmentId f) => Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.Euler(0f, f.value % 7, 0f), Vector3.one);
                LogicalFragmentId missing = default;
                var cases = new List<(string name, Answers answers)>
                {
                    ("following", new Answers { answer = f => (VpFragmentPlacementKind.Following, Place(f)) }),
                    ("static", new Answers { answer = f => (VpFragmentPlacementKind.Static, default) }),
                    ("one missing", new Answers { answer = f => { if (!missing.IsSet) missing = f; return f == missing ? (VpFragmentPlacementKind.Missing, default) : (VpFragmentPlacementKind.Following, Place(f)); } }),
                    ("no provider", null),
                    ("a valid scale", new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.identity, new Vector3(2f, 0.5f, 1.5f))) }),
                    ("a NaN", new Answers { answer = f => { Matrix4x4 m = Place(f); m.m03 = float.NaN; return (VpFragmentPlacementKind.Following, m); } }),
                    ("not affine", new Answers { answer = f => { Matrix4x4 m = Place(f); m.m30 = 0.1f; return (VpFragmentPlacementKind.Following, m); } }),
                    ("singular", new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f))) }),
                };

                foreach ((string name, Answers answers) in cases)
                {
                    missing = default;
                    Result oldStructural = Run(true, (s, a) => s.TryBuild(ledger, registrations, a), answers, ledger);
                    missing = default;
                    Result newStructural = Run(false, (s, a) => s.TryBuild(ledger, registrations, a), answers, ledger);
                    Same(what + ", structural, " + name, newStructural, oldStructural);

                    // Placement-only, over a structure settled with places that are valid.
                    var structure = NewSnapshot();
                    Assert.That(structure.TryBuild(ledger, registrations, new Answers { answer = f => (VpFragmentPlacementKind.Following, Place(f)) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    missing = default;
                    Result oldPlacement = Run(true, (s, a) => s.TryBuildPlacementsFrom(structure, ledger, registrations, a), answers, ledger);
                    missing = default;
                    Result newPlacement = Run(false, (s, a) => s.TryBuildPlacementsFrom(structure, ledger, registrations, a), answers, ledger);
                    Same(what + ", placement only, " + name, newPlacement, oldPlacement);
                }
            }
        }

        private static void Same(string what, Result now, Result old)
        {
            TestContext.Out.WriteLine(what + ": " + now.outcome + "/" + now.invalid + "/" + now.shortage + " (old " + old.outcome + "/" + old.invalid + "/" + old.shortage + "); provider asked " + now.asked + " (old " + old.asked + "), placement checks " + now.checks + " (old " + old.checks + ")");
            Assert.That((now.outcome, now.invalid, now.shortage), Is.EqualTo((old.outcome, old.invalid, old.shortage)), what + ": the same outcome, reason and shortage");
            Assert.That(now.asked, Is.EqualTo(old.asked), what + ": the provider asked as often");
            Assert.That(now.checks, Is.LessThanOrEqualTo(old.checks), what + ": no more checks");
            Assert.That(now.contents == null, Is.EqualTo(old.contents == null), what);
            if (old.contents == null) return;
            Assert.That(now.contents.Count, Is.EqualTo(old.contents.Count), what + ": as many items");
            for (int i = 0; i < old.contents.Count; i++) Assert.That(now.contents[i], Is.EqualTo(old.contents[i]), what + ": item " + i);
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static double Ms(long from, long to) => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        [Test]
        public void TheSameBinarysCost_OldAndNew_AreWrittenOut()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            inputs.Add(("building-like (2,029 registrations, display sets)", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                // A Transform for every fragment the structure stands as.
                var at = new Dictionary<LogicalFragmentId, Transform>();
                var order = new List<LogicalFragmentId>();
                var made = new List<GameObject>();
                var probe = NewSnapshot();
                Assert.That(probe.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                for (int r = 0; r < probe.RenderFragmentCount; r++)
                {
                    LogicalFragmentId f = probe.StandsAsForTest(r).fragment;
                    if (at.ContainsKey(f)) continue;
                    Assert.That(probe.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    var g = new GameObject("placement check cost " + f.value) { hideFlags = HideFlags.HideAndDontSave };
                    Matrix4x4 m = registrations[rf.registration].geometryLocalToWorld;
                    g.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                    made.Add(g); at[f] = g.transform; order.Add(f);
                }

                var placements = new Answers { answer = f => at.TryGetValue(f, out Transform t) ? (VpFragmentPlacementKind.Following, t.localToWorldMatrix) : (VpFragmentPlacementKind.Missing, default) };
                try
                {
                    var structure = NewSnapshot();
                    Assert.That(structure.TryBuild(ledger, registrations, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    foreach ((string scenario, int every) in new[] { ("nothing moved", 0), ("a tenth moved", 10), ("everything moved", 1) })
                    {
                        var place = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                        var whole = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                        var checks = new Dictionary<bool, long>();
                        var targets = new Dictionary<bool, VpMultiCutSnapshot> { [true] = NewSnapshot(), [false] = NewSnapshot() };
                        for (int k = 0; k < 20; k++)
                        {
                            bool old = (k & 1) == 0;
                            if (every > 0 && old) for (int i = 0; i < order.Count; i += every) at[order[i]].position += new Vector3(0.001f, 0f, 0f);
                            VpMultiCutSnapshot target = targets[old];
                            var before = new VpPlaceCounts();
                            before.CopyFrom(target.PlacementOnlyPlaceCounts);
                            VpMultiCutSnapshot.placementCheckTwiceForTest = old;
                            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                            VpMultiCutBuildOutcome o = target.TryBuildPlacementsFrom(structure, ledger, registrations, placements);
                            long end = System.Diagnostics.Stopwatch.GetTimestamp();
                            VpMultiCutSnapshot.placementCheckTwiceForTest = false;
                            Assert.That(o, Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            var d = new VpPlaceCounts();
                            d.AddDifference(target.PlacementOnlyPlaceCounts, before);
                            if (k >= 2) { place[old].Add(d.seconds * 1000); whole[old].Add(Ms(begin, end)); }
                            checks[old] = d.placementChecks;
                        }

                        TestContext.Out.WriteLine(what + ", placement only, " + scenario + " (medians of 9): Place old " + Median(place[true]).ToString("F3") + " new " + Median(place[false]).ToString("F3")
                            + " ms; the whole build old " + Median(whole[true]).ToString("F3") + " new " + Median(whole[false]).ToString("F3") + " ms; placement checks a pass old " + checks[true] + " new " + checks[false]);
                    }

                    var sPlace = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                    var sWhole = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                    for (int k = 0; k < 20; k++)
                    {
                        bool old = (k & 1) == 0;
                        var s = NewSnapshot();
                        VpMultiCutSnapshot.placementCheckTwiceForTest = old;
                        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations, placements);
                        long end = System.Diagnostics.Stopwatch.GetTimestamp();
                        VpMultiCutSnapshot.placementCheckTwiceForTest = false;
                        Assert.That(o, Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        if (k >= 2) { sPlace[old].Add(s.StructuralPlaceCounts.seconds * 1000); sWhole[old].Add(Ms(begin, end)); }
                    }

                    TestContext.Out.WriteLine(what + ", structural (medians of 9): Place old " + Median(sPlace[true]).ToString("F3") + " new " + Median(sPlace[false]).ToString("F3")
                        + " ms; the whole build old " + Median(sWhole[true]).ToString("F3") + " new " + Median(sWhole[false]).ToString("F3") + " ms");
                }
                finally
                {
                    VpMultiCutSnapshot.placementCheckTwiceForTest = false;
                    foreach (GameObject g in made) UnityEngine.Object.DestroyImmediate(g);
                }
            }
        }

        // The ordinary pass and the diagnosis's phased one (PlacePhasedDiagnosis), on the same input.
        private static Result RunPhased(bool phased, Func<VpMultiCutSnapshot, Answers, VpMultiCutBuildOutcome> build, Answers answers, LogicalCutLedger ledger)
        {
            VpMultiCutSnapshot.PlacePhasedDiagnosis = phased;
            try
            {
                return Run(false, build, answers, ledger);
            }
            finally
            {
                VpMultiCutSnapshot.PlacePhasedDiagnosis = false;
            }
        }

        /// <summary>
        /// **The diagnosis's phased pass builds and refuses as the ordinary one** (queries, checks and the rest as three
        /// timed blocks): the same snapshots item by item, the same outcome, reason and shortage; a built pass asks the provider
        /// as often (a refused one may ask on past the refused render fragment, which changes nothing).
        /// </summary>
        [Test]
        public void ThePhasedDiagnosis_BuildsAndRefusesAsTheOrdinaryPass()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            inputs.Add(("building-like 24 x 40, plain arrays", bl, br));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                Matrix4x4 Place(LogicalFragmentId f) => Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.Euler(0f, f.value % 7, 0f), Vector3.one);
                int seen = 0;
                var cases = new List<(string name, Answers answers)>
                {
                    ("following", new Answers { answer = f => (VpFragmentPlacementKind.Following, Place(f)) }),
                    ("static", new Answers { answer = f => (VpFragmentPlacementKind.Static, default) }),
                    ("the fourth missing", new Answers { answer = f => ++seen == 4 ? (VpFragmentPlacementKind.Missing, default) : (VpFragmentPlacementKind.Following, Place(f)) }),
                    ("no provider", null),
                    ("a valid scale", new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.identity, new Vector3(2f, 0.5f, 1.5f))) }),
                    ("the third a NaN", new Answers { answer = f => { Matrix4x4 m = Place(f); if (++seen == 3) m.m03 = float.NaN; return (VpFragmentPlacementKind.Following, m); } }),
                    ("the second not affine, the fifth missing", new Answers { answer = f => { ++seen; Matrix4x4 m = Place(f); if (seen == 2) m.m30 = 0.1f; return seen == 5 ? (VpFragmentPlacementKind.Missing, default) : (VpFragmentPlacementKind.Following, m); } }),
                    ("singular", new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f))) }),
                };

                foreach ((string name, Answers answers) in cases)
                {
                    seen = 0;
                    Result ordinary = RunPhased(false, (s, a) => s.TryBuild(ledger, registrations, a), answers, ledger);
                    seen = 0;
                    Result phased = RunPhased(true, (s, a) => s.TryBuild(ledger, registrations, a), answers, ledger);
                    SamePhased(what + ", structural, " + name, phased, ordinary);
                    var structure = NewSnapshot();
                    Assert.That(structure.TryBuild(ledger, registrations, new Answers { answer = f => (VpFragmentPlacementKind.Following, Place(f)) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    seen = 0;
                    ordinary = RunPhased(false, (s, a) => s.TryBuildPlacementsFrom(structure, ledger, registrations, a), answers, ledger);
                    seen = 0;
                    phased = RunPhased(true, (s, a) => s.TryBuildPlacementsFrom(structure, ledger, registrations, a), answers, ledger);
                    SamePhased(what + ", placement only, " + name, phased, ordinary);
                }
            }
        }

        private static void SamePhased(string what, Result phased, Result ordinary)
        {
            TestContext.Out.WriteLine(what + ": " + phased.outcome + "/" + phased.invalid + " (ordinary " + ordinary.outcome + "/" + ordinary.invalid + "); provider asked " + phased.asked + " (ordinary " + ordinary.asked + ")");
            Assert.That((phased.outcome, phased.invalid, phased.shortage), Is.EqualTo((ordinary.outcome, ordinary.invalid, ordinary.shortage)), what + ": the same outcome, reason and shortage");
            if (ordinary.outcome == VpMultiCutBuildOutcome.Built) Assert.That(phased.asked, Is.EqualTo(ordinary.asked), what + ": a built pass asks as often");
            else Assert.That(phased.asked, Is.GreaterThanOrEqualTo(ordinary.asked), what);
            Assert.That(phased.contents == null, Is.EqualTo(ordinary.contents == null), what);
            if (ordinary.contents == null) return;
            Assert.That(phased.contents.Count, Is.EqualTo(ordinary.contents.Count), what + ": as many items");
            for (int i = 0; i < ordinary.contents.Count; i++) Assert.That(phased.contents[i], Is.EqualTo(ordinary.contents[i]), what + ": item " + i);
        }

        /// <summary>The phased pass's blocks on the fixed inputs (Transform placements), and its whole time beside the ordinary pass's: what the diagnosis itself costs. Written out.</summary>
        [Test]
        public void ThePhasedDiagnosis_ItsBlocksAndItsOwnCost_AreWrittenOut()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            inputs.Add(("building-like (2,029 registrations, display sets)", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                var at = new Dictionary<LogicalFragmentId, Transform>();
                var made = new List<GameObject>();
                var probe = NewSnapshot();
                Assert.That(probe.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                for (int r = 0; r < probe.RenderFragmentCount; r++)
                {
                    LogicalFragmentId f = probe.StandsAsForTest(r).fragment;
                    if (at.ContainsKey(f)) continue;
                    Assert.That(probe.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    var g = new GameObject("phased cost " + f.value) { hideFlags = HideFlags.HideAndDontSave };
                    Matrix4x4 m = registrations[rf.registration].geometryLocalToWorld;
                    g.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                    made.Add(g); at[f] = g.transform;
                }

                var placements = new Answers { answer = f => at.TryGetValue(f, out Transform t) ? (VpFragmentPlacementKind.Following, t.localToWorldMatrix) : (VpFragmentPlacementKind.Missing, default) };
                try
                {
                    var structure = NewSnapshot();
                    Assert.That(structure.TryBuild(ledger, registrations, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    var ordinary = new List<double>();
                    var phased = new List<double>();
                    var provider = new List<double>();
                    var check = new List<double>();
                    var rest = new List<double>();
                    var targets = new Dictionary<bool, VpMultiCutSnapshot> { [false] = NewSnapshot(), [true] = NewSnapshot() };
                    for (int k = 0; k < 20; k++)
                    {
                        bool on = (k & 1) == 1;
                        VpMultiCutSnapshot target = targets[on];
                        var before = new VpPlaceCounts();
                        before.CopyFrom(target.PlacementOnlyPlaceCounts);
                        VpMultiCutSnapshot.PlacePhasedDiagnosis = on;
                        Assert.That(target.TryBuildPlacementsFrom(structure, ledger, registrations, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        VpMultiCutSnapshot.PlacePhasedDiagnosis = false;
                        var d = new VpPlaceCounts();
                        d.AddDifference(target.PlacementOnlyPlaceCounts, before);
                        if (k < 2) continue;
                        if (on) { phased.Add(d.seconds * 1000); provider.Add(d.providerSeconds * 1000); check.Add(d.checkSeconds * 1000); rest.Add(d.restSeconds * 1000); }
                        else ordinary.Add(d.seconds * 1000);
                    }

                    TestContext.Out.WriteLine(what + ", placement only, nothing moved (medians of 9, ms): ordinary " + Median(ordinary).ToString("F3") + ", phased " + Median(phased).ToString("F3")
                        + " = queries " + Median(provider).ToString("F3") + " + checks " + Median(check).ToString("F3") + " + the rest " + Median(rest).ToString("F3"));
                }
                finally
                {
                    VpMultiCutSnapshot.PlacePhasedDiagnosis = false;
                    foreach (GameObject g in made) UnityEngine.Object.DestroyImmediate(g);
                }
            }
        }
    }
}
