using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The Place pass taken apart on fixed inputs (TL, 2026-10-01): the placement queries (how many, how many ask again for
    /// what an earlier render fragment of the same pass asked), the planes and the clip, the sections (found in the build,
    /// reused, built; the entries compared to find them) and the cap clipping -- each by its counts, and by the whole
    /// pass's time under test switches that stop the pass after the queries, before the caps or before the cap clip (no
    /// item timed on its own). The structural pass apart from the placement-only one, and the placement-only one with
    /// nothing moved, a tenth moved and everything moved between passes. The placements are answered from real
    /// Transforms, one per fragment the snapshot stands as. Written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotPlaceCostTests
    {
        // Where each fragment stands: a Transform each, read for every query (as the world's owners are read).
        private sealed class TransformPlacements : IVpFragmentPlacement
        {
            public readonly Dictionary<LogicalFragmentId, Transform> at = new Dictionary<LogicalFragmentId, Transform>();
            public readonly List<GameObject> made = new List<GameObject>();

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                if (at.TryGetValue(fragment, out Transform t))
                {
                    geometryLocalToWorld = t.localToWorldMatrix;
                    return VpFragmentPlacementKind.Following;
                }

                geometryLocalToWorld = default;
                return VpFragmentPlacementKind.Missing;
            }

            public void Destroy()
            {
                foreach (GameObject g in made) Object.DestroyImmediate(g);
                made.Clear();
                at.Clear();
            }
        }

        private static VpMultiCutSnapshot NewSnapshot() => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)> Inputs()
        {
            var inputs = new List<(string, LogicalCutLedger, List<VpMultiCutRegistration>)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            inputs.Add(("building-like (2,029 registrations, display sets, nothing to clip)", bl, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(br)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep40 = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (20 registrations, partly reflected, caps), display sets", deep40.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep40.registrations)));
            VpMultiCutSnapshotReflectedIndexTests.Input deep80 = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 80);
            inputs.Add(("deep 80 (20 registrations, caps), display sets", deep80.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep80.registrations)));
            return inputs;
        }

        // A Transform for every fragment the structure stands as, where its registration stands.
        private static TransformPlacements PlacementsFor(LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, out List<LogicalFragmentId> order)
        {
            var p = new TransformPlacements();
            order = new List<LogicalFragmentId>();
            var probe = NewSnapshot();
            Assert.That(probe.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            for (int r = 0; r < probe.RenderFragmentCount; r++)
            {
                LogicalFragmentId f = probe.StandsAsForTest(r).fragment;
                if (p.at.ContainsKey(f)) continue;
                Assert.That(probe.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                var g = new GameObject("place cost " + f.value) { hideFlags = HideFlags.HideAndDontSave };
                Matrix4x4 m = registrations[rf.registration].geometryLocalToWorld;
                g.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                p.made.Add(g);
                p.at[f] = g.transform;
                order.Add(f);
            }

            return p;
        }

        [Test]
        public void ThePlacePass_ItsPartsAndCounts_AreWrittenOut()
        {
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in Inputs())
            {
                TransformPlacements placements = PlacementsFor(ledger, registrations, out List<LogicalFragmentId> order);
                try
                {
                    // The repeated asks: render fragments asking for a placement an earlier one of the same pass asked for.
                    var structure = NewSnapshot();
                    Assert.That(structure.TryBuild(ledger, registrations, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    var keys = new HashSet<(LogicalFragmentId, CutOperationId, float)>();
                    var fragments = new HashSet<LogicalFragmentId>();
                    int again = 0, fragmentAgain = 0;
                    for (int r = 0; r < structure.RenderFragmentCount; r++)
                    {
                        var s = structure.StandsAsForTest(r);
                        if (!keys.Add(s)) again++;
                        if (!fragments.Add(s.fragment)) fragmentAgain++;
                    }

                    TestContext.Out.WriteLine(what + ": render fragments " + structure.RenderFragmentCount + ", placements asked for again within the pass " + again + " (the same fragment again, any side " + fragmentAgain + "); Transforms " + order.Count);

                    // The provider alone: the same asks, in the same order, outside the snapshot (a whole loop timed, 9 medians).
                    var provider = new List<double>();
                    for (int k = 0; k < 10; k++)
                    {
                        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        for (int r = 0; r < structure.RenderFragmentCount; r++)
                        {
                            var s = structure.StandsAsForTest(r);
                            placements.TryGetGeometryLocalToWorld(s.fragment, s.operation, s.side, out _);
                        }

                        if (k > 0) provider.Add((System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    }

                    TestContext.Out.WriteLine("  the placement provider alone (a dictionary and a Transform read per ask): " + Median(provider).ToString("F3") + " ms for " + structure.RenderFragmentCount + " asks (median of 9)");

                    // The placement check alone, over the same answers: once a query, and twice (as a query's answer is checked now).
                    var answers = new List<Matrix4x4>();
                    for (int r = 0; r < structure.RenderFragmentCount; r++)
                    {
                        var s = structure.StandsAsForTest(r);
                        placements.TryGetGeometryLocalToWorld(s.fragment, s.operation, s.side, out Matrix4x4 m);
                        answers.Add(m);
                    }

                    var once = new List<double>();
                    var twice = new List<double>();
                    bool all = true;
                    for (int k = 0; k < 10; k++)
                    {
                        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        foreach (Matrix4x4 m in answers) all &= VpMultiCutSnapshot.IsPlacementForTest(m);
                        long mid = System.Diagnostics.Stopwatch.GetTimestamp();
                        foreach (Matrix4x4 m in answers) all &= VpMultiCutSnapshot.IsPlacementForTest(m) && VpMultiCutSnapshot.IsPlacementForTest(m);
                        long end = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (k > 0)
                        {
                            once.Add((mid - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                            twice.Add((end - mid) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                        }
                    }

                    Assert.That(all, Is.True);
                    TestContext.Out.WriteLine("  the placement check alone (finite, affine, determinant and inverse): once a query " + Median(once).ToString("F3") + " ms, twice a query " + Median(twice).ToString("F3") + " ms, for " + answers.Count + " answers (medians of 9)");

                    // The structural pass: fresh, and with the previous snapshot's sections offered.
                    foreach (bool reuse in new[] { false, true })
                    {
                        var times = new List<double>();
                        var counts = new VpPlaceCounts();
                        VpMultiCutSnapshot previous = NewSnapshot();
                        Assert.That(previous.TryBuild(ledger, registrations, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        for (int k = 0; k < 9; k++)
                        {
                            var s = NewSnapshot();
                            Assert.That(s.TryBuild(ledger, registrations, reuse ? previous : null, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            times.Add(s.StructuralPlaceCounts.seconds * 1000);
                            if (k == 8) counts.CopyFrom(s.StructuralPlaceCounts);
                            previous = s;
                        }

                        TestContext.Out.WriteLine("  structural Place" + (reuse ? ", the previous snapshot's sections offered" : ", fresh") + ": " + Median(times).ToString("F3") + " ms (median of 9); " + counts.Describe());
                    }

                    // The placement-only pass: nothing moved, a tenth moved, everything moved between passes; each part by switches.
                    foreach ((string scenario, int every) in new[] { ("nothing moved", 0), ("a tenth moved", 10), ("everything moved", 1) })
                    {
                        var parts = new Dictionary<string, double>();
                        VpPlaceCounts full = null;
                        foreach (string mode in new[] { "queries only", "no caps", "no cap clip", "whole" })
                        {
                            var times = new List<double>();
                            var target = NewSnapshot();
                            target.placeQueriesOnlyForTest = mode == "queries only";
                            target.placeNoCapsForTest = mode == "no caps";
                            target.placeNoCapClipForTest = mode == "no cap clip";
                            for (int k = 0; k < 10; k++)
                            {
                                if (every > 0)
                                {
                                    for (int i = 0; i < order.Count; i += every)
                                    {
                                        Transform t = placements.at[order[i]];
                                        t.position += new Vector3(0.001f, 0f, 0f);
                                    }
                                }

                                var before = new VpPlaceCounts();
                                before.CopyFrom(target.PlacementOnlyPlaceCounts);
                                Assert.That(target.TryBuildPlacementsFrom(structure, ledger, registrations, placements), Is.EqualTo(VpMultiCutBuildOutcome.Built), mode);
                                var d = new VpPlaceCounts();
                                d.AddDifference(target.PlacementOnlyPlaceCounts, before);
                                if (k > 0) times.Add(d.seconds * 1000);   // the first warms
                                if (mode == "whole" && k == 9) full = d;
                            }

                            parts[mode] = Median(times);
                        }

                        TestContext.Out.WriteLine("  placement only, " + scenario + " (medians of 9 passes, ms): whole " + parts["whole"].ToString("F3")
                            + " = queries " + parts["queries only"].ToString("F3")
                            + " + planes and clip " + (parts["no caps"] - parts["queries only"]).ToString("F3")
                            + " + sections " + (parts["no cap clip"] - parts["no caps"]).ToString("F3")
                            + " + cap clip " + (parts["whole"] - parts["no cap clip"]).ToString("F3")
                            + "; one whole pass's counts: " + full.Describe());
                    }
                }
                finally
                {
                    placements.Destroy();
                }
            }
        }
    }
}
