using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The reflected boundaries looked up through an index made once per structure build (TL, 2026-10-01), against the
    /// scan of each registration's set from its start that every lookup made before -- on the same fixed input: a deep
    /// history under several registrations (siblings at the same depth, each geometry reflecting its whole chain or all
    /// but a few boundaries), cuts below them published, one pending with its sides prepared, one aborted, a fragment
    /// retired. The snapshot built either way is compared item by item and in order (branches, candidates and their
    /// states, render fragments, conditions, caps, the side and cap identities), and the refusals either way are the
    /// same, first refusal included (a lineage refusal, a candidate shortage, a chain-depth shortage, a retired fragment
    /// past an Ignored boundary). The lookups, the items compared and the times are written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotReflectedIndexTests
    {
        private static float4 Plane(int k) => new float4(0f, 1f, 0f, -0.05f * (k % 10 + 1));

        private static CutOperationId CutOp(LogicalCutLedger ledger, LogicalFragmentId source, int k, out LogicalFragmentId positive, out LogicalFragmentId negative, bool complete = true)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            if (complete) Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return cut;
        }

        internal sealed class Input
        {
            public LogicalCutLedger ledger;
            public readonly List<VpMultiCutRegistration> registrations = new List<VpMultiCutRegistration>();
            public int k;
        }

        /// <summary>
        /// <paramref name="lineages"/> lineages cut <paramref name="depth"/> deep; the two children of the deepest cut are
        /// registered (each geometry reflecting its whole chain, or -- every third one -- all but its first two
        /// boundaries); below each registered root two levels of published cuts, and under the first lineage's roots one
        /// cut pending (sides prepared), one aborted and one fragment retired.
        /// </summary>
        internal static Input Deep(int lineages, int depth, bool fullyReflected = false)
        {
            var input = new Input { ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)) };
            LogicalCutLedger ledger = input.ledger;
            int r = 0;
            for (int l = 0; l < lineages; l++)
            {
                LogicalFragmentId at = ledger.AddFragment();
                var chain = new List<VpClipBoundary>();
                LogicalFragmentId positive = default, negative = default;
                CutOperationId last = default;
                for (int d = 0; d < depth; d++)
                {
                    last = CutOp(ledger, at, input.k++, out positive, out negative);
                    if (d < depth - 1)
                    {
                        chain.Add(new VpClipBoundary(new VpCapFace(ledger, last), (d & 1) == 0 ? 1f : -1f));
                        at = (d & 1) == 0 ? positive : negative;
                    }
                }

                foreach ((LogicalFragmentId root, float side) in new[] { (positive, 1f), (negative, -1f) })
                {
                    var reflected = new List<VpClipBoundary>(chain) { new VpClipBoundary(new VpCapFace(ledger, last), side) };
                    if (!fullyReflected && r % 3 == 2 && reflected.Count > 2) reflected.RemoveRange(0, 2);
                    reflected.Reverse();   // the set's own order is the caller's
                    // Below the root: two levels published.
                    CutOp(ledger, root, input.k++, out LogicalFragmentId a, out LogicalFragmentId b);
                    CutOp(ledger, a, input.k++, out LogicalFragmentId leaf, out _);
                    if (l == 0)
                    {
                        if (side > 0f)
                        {
                            // One cut pending with its sides prepared; on the other root one aborted (its source retired by it) and a leaf retired.
                            Assert.That(ledger.Admit(b, Plane(input.k++), true, out CutOperationId pending), Is.EqualTo(LogicalCutAdmission.Admitted));
                            Assert.That(ledger.PrepareAnchorDistribution(pending, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                        }
                        else
                        {
                            Assert.That(ledger.Admit(b, Plane(input.k++), true, out CutOperationId aborted), Is.EqualTo(LogicalCutAdmission.Admitted));
                            Assert.That(ledger.Abort(aborted), Is.EqualTo(LogicalCutResultOutcome.Applied));
                            Assert.That(ledger.Retire(leaf), Is.True, "a leaf retired");
                        }
                    }

                    input.registrations.Add(new VpMultiCutRegistration(root, new Bounds(Vector3.zero, Vector3.one * 2f), Matrix4x4.Translate(new Vector3(3f * r, 0f, 0f)), Matrix4x4.identity, reflected.ToArray(), 0.001f));
                    r++;
                }
            }

            return input;
        }

        private static VpMultiCutSnapshot NewSnapshot(bool scan, int candidates = 131072, int chainDepth = 256)
        {
            var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, candidates, 16384, 131072, chainDepth)) { reflectedByScanForTest = scan };
            return s;
        }

        // Every field of a value, nested values included; a reference by its type (the ledger by being this ledger).
        private static void Dump(StringBuilder into, object value, LogicalCutLedger ledger, int level = 0)
        {
            if (value == null) { into.Append("null"); return; }
            System.Type t = value.GetType();
            if (value is float f) { into.Append(f.ToString("R")); return; }
            if (value is double d) { into.Append(d.ToString("R")); return; }
            if (t.IsPrimitive || t.IsEnum || value is string) { into.Append(value); return; }
            if (!t.IsValueType) { into.Append(ReferenceEquals(value, ledger) ? "ledger" : t.Name); return; }
            if (level > 6) { into.Append(value); return; }
            into.Append(t.Name).Append('{');
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                into.Append(field.Name).Append('=');
                Dump(into, field.GetValue(value), ledger, level + 1);
                into.Append(';');
            }

            into.Append('}');
        }

        internal static List<string> Contents(VpMultiCutSnapshot s, LogicalCutLedger ledger)
        {
            var lines = new List<string>
            {
                "counts: registrations " + s.RegistrationCount + " branches " + s.BranchCount + " candidates " + s.CandidateCount + " render fragments " + s.RenderFragmentCount
                + " conditions " + s.ConditionCount + " caps " + s.CapCount + " cap vertices " + s.CapVertexCount,
            };
            var line = new StringBuilder();
            void Add(string what, int i, object o) { line.Clear(); line.Append(what).Append(' ').Append(i).Append(": "); Dump(line, o, ledger); lines.Add(line.ToString()); }
            for (int i = 0; i < s.BranchCount; i++) { Assert.That(s.TryGetBranch(i, out VpMultiCutBranch b), Is.True); Add("branch", i, b); }
            for (int i = 0; i < s.CandidateCount; i++)
            {
                Assert.That(s.TryGetCandidate(i, out VpClipCandidate c, out VpClipSelectionState state), Is.True); Add("candidate", i, c); lines.Add("state " + i + ": " + state);
                if (s.TryGetCapIdentity(i, out VpMultiCutSnapshot.VpMultiCutCapIdentity identity)) Add("cap identity", i, identity);
            }

            for (int i = 0; i < s.RenderFragmentCount; i++)
            {
                Assert.That(s.TryGetRenderFragment(i, out VpMultiCutRenderFragment rf), Is.True); Add("render fragment", i, rf);
                if (s.TryGetSideIdentity(i, out VpMultiCutSnapshot.VpMultiCutSideIdentity side)) Add("side identity", i, side);
            }

            for (int i = 0; i < s.ConditionCount; i++) { Assert.That(s.TryGetCondition(i, out VpCapConstraint c), Is.True); Add("condition", i, c); }
            for (int i = 0; i < s.CapCount; i++)
            {
                Assert.That(s.TryGetCap(i, out VpMultiCutCap cap), Is.True); Add("cap", i, cap);
                for (int v = 0; s.TryGetCapVertex(i, v, out Vector3 world); v++) lines.Add("cap " + i + " vertex " + v + ": " + world.x.ToString("R") + "," + world.y.ToString("R") + "," + world.z.ToString("R"));
            }

            return lines;
        }

        private static void AssertSame(List<string> scan, List<string> index, string what)
        {
            Assert.That(index.Count, Is.EqualTo(scan.Count), what + ": as many items");
            for (int i = 0; i < scan.Count; i++) Assert.That(index[i], Is.EqualTo(scan[i]), what + ": item " + i + " in the same order");
        }

        [Test]
        public void TheIndex_BuildsTheSameSnapshotAsTheScan_InTheSameOrder_WithFewerComparisons([Values(10, 40, 80)] int depth)
        {
            Input input = Deep(10, depth);
            var ms = new Dictionary<bool, string>();
            var counts = new Dictionary<bool, (long lookups, long comparisons, long entries)>();
            List<string> scanContents = null, indexContents = null;
            foreach (bool scan in new[] { true, false })
            {
                double validate = double.MaxValue, collect = double.MaxValue, group = double.MaxValue, index = double.MaxValue, build = double.MaxValue;
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    VpMultiCutSnapshot s = NewSnapshot(scan);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Assert.That(s.TryBuild(input.ledger, input.registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built), (scan ? "scan" : "index") + " built");
                    build = System.Math.Min(build, watch.Elapsed.TotalMilliseconds);
                    validate = System.Math.Min(validate, s.LastStructureValidateSeconds * 1000);
                    collect = System.Math.Min(collect, s.LastCollectSeconds * 1000);
                    group = System.Math.Min(group, s.LastGroupSeconds * 1000);
                    index = System.Math.Min(index, s.LastReflectedIndexSeconds * 1000);
                    if (repeat == 0)
                    {
                        counts[scan] = (s.ReflectedLookups, s.ReflectedComparisons, s.ReflectedIndexEntries);
                        if (scan) scanContents = Contents(s, input.ledger); else indexContents = Contents(s, input.ledger);
                    }
                }

                ms[scan] = "build " + build.ToString("F3") + " ms (validate " + validate.ToString("F3") + " of which the index " + index.ToString("F3") + ", collect " + collect.ToString("F3") + ", group " + group.ToString("F3") + ")";
            }

            TestContext.Out.WriteLine("depth " + depth + ", registrations " + input.registrations.Count + ", ledger operations " + input.ledger.OperationCount + "; " + scanContents[0]);
            TestContext.Out.WriteLine("scan : lookups " + counts[true].lookups + ", items compared " + counts[true].comparisons + "; " + ms[true]);
            TestContext.Out.WriteLine("index: lookups " + counts[false].lookups + ", entries made " + counts[false].entries + ", items compared by a scan " + counts[false].comparisons + "; " + ms[false]);
            AssertSame(scanContents, indexContents, "depth " + depth);
            Assert.That(counts[false].lookups, Is.EqualTo(counts[true].lookups), "the same lookups, in the same places");
            Assert.That(counts[false].comparisons, Is.EqualTo(0), "the index scans nothing");
            Assert.That(scanContents.Count, Is.GreaterThan(1));
        }

        private static (VpMultiCutBuildOutcome outcome, VpMultiCutInvalidInput invalid, VpMultiCutShortage shortage) Refusal(Input input, bool scan, int candidates = 131072, int chainDepth = 256)
        {
            VpMultiCutSnapshot s = NewSnapshot(scan, candidates, chainDepth);
            VpMultiCutBuildOutcome outcome = s.TryBuild(input.ledger, input.registrations);
            return (outcome, s.InvalidInputReason, s.Shortage);
        }

        [Test]
        public void TheIndex_RefusesAsTheScanDoes_TheFirstRefusalIncluded()
        {
            // A registered root with one of its descendants registered too: a lineage refusal.
            Input lineage = Deep(3, 12);
            VpMultiCutRegistration first = lineage.registrations[0];
            Assert.That(lineage.ledger.TryGetReplacingOperation(first.root, out CutOperationId below), Is.True);
            Assert.That(lineage.ledger.TryGetOperation(below, out LogicalCutOperation op), Is.True);
            lineage.registrations.Add(new VpMultiCutRegistration(op.positive, first.localBounds, Matrix4x4.Translate(new Vector3(-9f, 0f, 0f)), Matrix4x4.identity, System.Array.Empty<VpClipBoundary>(), 0.001f));

            // A retired fragment under more unreflected boundaries than the selection takes: past an Ignored boundary.
            Input retired = Deep(1, 12);
            for (int i = 0; i < retired.registrations.Count; i++)
            {
                VpMultiCutRegistration g = retired.registrations[i];
                retired.registrations[i] = new VpMultiCutRegistration(g.root, g.localBounds, g.geometryLocalToWorld, g.lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>(), g.vertexEpsilon);
            }

            var cases = new (string what, Input input, int candidates, int chainDepth)[]
            {
                ("lineage", lineage, 131072, 256),
                ("candidates short", Deep(4, 20, fullyReflected: false), 8, 256),
                ("chain depth short", Deep(2, 30), 131072, 16),
                ("retired past an Ignored boundary", retired, 131072, 256),
            };
            foreach ((string what, Input input, int candidates, int chainDepth) in cases)
            {
                var byScan = Refusal(input, true, candidates, chainDepth);
                var byIndex = Refusal(input, false, candidates, chainDepth);
                TestContext.Out.WriteLine(what + ": scan " + byScan + ", index " + byIndex);
                Assert.That(byScan.outcome, Is.Not.EqualTo(VpMultiCutBuildOutcome.Built), what + ": refused");
                Assert.That(byIndex, Is.EqualTo(byScan), what + ": the same refusal, the same reason");
            }
        }

        [Test]
        public void TheIndex_TakesTheSetsItemsByTheirOwnEquality_TheLedgerTheOperationAndTheSide()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            var other = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            LogicalFragmentId root = ledger.AddFragment();
            CutOperationId cut = CutOp(ledger, root, 0, out _, out _);
            var set = new[] { new VpClipBoundary(new VpCapFace(ledger, cut), 1f), new VpClipBoundary(new VpCapFace(ledger, cut), -0f) };
            var counts = new VpReflectedIndex.Counts();
            var index = new VpReflectedIndex();
            foreach (bool scan in new[] { true, false })
            {
                index.Fill(set, scan, counts);
                string how = scan ? "scan" : "index";
                Assert.That(index.Contains(new VpClipBoundary(new VpCapFace(ledger, cut), 1f)), Is.True, how + ": the same boundary");
                Assert.That(index.Contains(new VpClipBoundary(new VpCapFace(ledger, cut), -1f)), Is.False, how + ": the other side");
                Assert.That(index.Contains(new VpClipBoundary(new VpCapFace(other, cut), 1f)), Is.False, how + ": the same number from another ledger");
                Assert.That(index.Contains(new VpClipBoundary(new VpCapFace(ledger, cut), 0f)), Is.True, how + ": +0 equals -0");
                Assert.That(index.Contains(new VpClipBoundary(new VpCapFace(ledger, cut), float.NaN)), Is.False, how + ": a NaN side is never in it");
            }
        }
    }
}
