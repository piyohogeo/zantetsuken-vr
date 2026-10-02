using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The registrations' contract checked with each matrix's 16 elements read by field instead of through the indexer
    /// (TL, 2026-10-01: on a building-like history the indexer's reads were most of the contract, and the contract the
    /// largest part of the validation, structural and placement-only alike). The answer is the same at every element
    /// (NaN, both infinities, finite extremes); the snapshots are the same item by item and in order; the refusals are the
    /// same, the first one included. The parts before and after are written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotFinitenessTests
    {
        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static Matrix4x4 With(Matrix4x4 m, int index, float value)
        {
            m[index] = value;
            return m;
        }

        [Test]
        public void TheValidationsParts_OnABuildingLikeHistory_BeforeAndAfter_AreWrittenOut()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int deepest) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            TestContext.Out.WriteLine("registrations " + registrations.Count + ", ledger operations " + ledger.OperationCount + ", the deepest registration " + deepest + " cuts down");
            var before = new VpValidateCounts();
            string[] names = { "structural total", "index", "input", "contract", "ancestors", "operations", "placement input", "placement contract", "placement build" };
            try
            {
                var parts = new Dictionary<bool, Dictionary<string, List<double>>>();
                var counts = new Dictionary<bool, string>();
                foreach (bool old in new[] { true, false })
                {
                    parts[old] = new Dictionary<string, List<double>>();
                    foreach (string n in names) parts[old][n] = new List<double>();
                }

                for (int repeat = 0; repeat < 7; repeat++)
                {
                    foreach (bool old in new[] { true, false })
                    {
                        VpMultiCutSnapshot.finiteByIndexerForTest = old;
                        var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 64));
                        var d = new VpValidateCounts();
                        before.CopyFrom(s.ValidateCounts);
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        d.AddDifference(s.ValidateCounts, before);
                        var pl = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 64));
                        before.CopyFrom(pl.ValidateCounts);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        Assert.That(pl.TryBuildPlacementsFrom(s, ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        double placementBuild = watch.Elapsed.TotalMilliseconds;
                        d.AddDifference(pl.ValidateCounts, before);
                        Dictionary<string, List<double>> p = parts[old];
                        p["structural total"].Add(s.LastStructureValidateSeconds * 1000);
                        p["index"].Add(d.indexSeconds * 1000);
                        p["input"].Add(d.inputSeconds * 1000);
                        p["contract"].Add(d.contractSeconds * 1000);
                        p["ancestors"].Add(d.ancestorSeconds * 1000);
                        p["operations"].Add(d.operationsSeconds * 1000);
                        p["placement input"].Add(d.placementInputSeconds * 1000);
                        p["placement contract"].Add(d.placementContractSeconds * 1000);
                        p["placement build"].Add(placementBuild);
                        counts[old] = d.Describe();
                    }
                }

                foreach (bool old in new[] { true, false })
                {
                    var line = new System.Text.StringBuilder(old ? "before (the indexer), medians of 7, ms: " : "after (by field), medians of 7, ms: ");
                    foreach (string n in names) line.Append(n).Append(' ').Append(Median(parts[old][n]).ToString("F3")).Append("; ");
                    TestContext.Out.WriteLine(line.ToString());
                    TestContext.Out.WriteLine("  counts of one structural and one placement-only validation: " + counts[old]);
                }
            }
            finally
            {
                VpMultiCutSnapshot.finiteByIndexerForTest = false;
            }
        }

        [Test]
        public void TheFinitenessByField_AnswersAsTheIndexerDid_AtEveryElement()
        {
            var values = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, float.Epsilon, -0f, 0f, 1f };
            int checkedCount = 0;
            try
            {
                for (int position = 0; position < 16; position++)
                {
                    foreach (float value in values)
                    {
                        Matrix4x4 m = With(Matrix4x4.TRS(new Vector3(0.25f * position, 2f, -3f), Quaternion.Euler(10f * position, 5f, 0f), Vector3.one), position, value);
                        VpMultiCutSnapshot.finiteByIndexerForTest = true;
                        bool indexer = VpMultiCutSnapshot.IsFiniteMatrix(m);
                        VpMultiCutSnapshot.finiteByIndexerForTest = false;
                        bool byField = VpMultiCutSnapshot.IsFiniteMatrix(m);
                        Assert.That(byField, Is.EqualTo(indexer), "element " + position + " = " + value);
                        Assert.That(byField, Is.EqualTo(!(float.IsNaN(value) || float.IsInfinity(value))), "element " + position + " = " + value);
                        checkedCount++;
                    }
                }
            }
            finally
            {
                VpMultiCutSnapshot.finiteByIndexerForTest = false;
            }

            TestContext.Out.WriteLine("matrices checked " + checkedCount);
        }

        [Test]
        public void TheSnapshots_AndTheRefusals_AreTheSameBeforeAndAfter()
        {
            try
            {
                var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
                (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
                inputs.Add(("building-like", bl, br));
                VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
                inputs.Add(("deep", deep.ledger, deep.registrations));
                foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
                {
                    var contents = new Dictionary<bool, List<string>>();
                    foreach (bool old in new[] { true, false })
                    {
                        VpMultiCutSnapshot.finiteByIndexerForTest = old;
                        var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
                        Assert.That(s.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built), what);
                        contents[old] = VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger);
                    }

                    Assert.That(contents[false].Count, Is.EqualTo(contents[true].Count), what + ": as many items");
                    for (int i = 0; i < contents[true].Count; i++) Assert.That(contents[false][i], Is.EqualTo(contents[true][i]), what + ": item " + i);
                    TestContext.Out.WriteLine(what + ": " + contents[true][0] + " -- the same, item by item");
                }

                var cases = new List<(string what, Matrix4x4 placement, Matrix4x4 lineage)>
                {
                    ("NaN in the placement", With(Matrix4x4.identity, 7, float.NaN), Matrix4x4.identity),
                    ("+Inf in the lineage frame", Matrix4x4.identity, With(Matrix4x4.identity, 13, float.PositiveInfinity)),
                    ("-Inf in the placement", With(Matrix4x4.identity, 0, float.NegativeInfinity), Matrix4x4.identity),
                    ("a projective last row", With(Matrix4x4.identity, 3, 0.5f), Matrix4x4.identity),
                    ("a singular placement", Matrix4x4.Scale(new Vector3(1f, 0f, 1f)), Matrix4x4.identity),
                };
                foreach ((string what, Matrix4x4 placement, Matrix4x4 lineage) in cases)
                {
                    (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(8, 4);
                    foreach (int at in new[] { 3, 5 })
                    {
                        VpMultiCutRegistration g = registrations[at];
                        registrations[at] = new VpMultiCutRegistration(g.root, g.localBounds, placement, lineage, g.reflected, g.vertexEpsilon);
                    }

                    var outcome = new Dictionary<bool, (VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage)>();
                    foreach (bool old in new[] { true, false })
                    {
                        VpMultiCutSnapshot.finiteByIndexerForTest = old;
                        var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 64));
                        VpMultiCutBuildOutcome o = s.TryBuild(ledger, registrations);
                        outcome[old] = (o, s.InvalidInputReason, s.Shortage);
                    }

                    TestContext.Out.WriteLine(what + " (registrations 3 and 5): before " + outcome[true] + ", after " + outcome[false]);
                    Assert.That(outcome[false], Is.EqualTo(outcome[true]), what);
                    Assert.That(outcome[true].Item1, Is.Not.EqualTo(VpMultiCutBuildOutcome.Built), what + ": refused");
                }
            }
            finally
            {
                VpMultiCutSnapshot.finiteByIndexerForTest = false;
            }
        }
    }
}
