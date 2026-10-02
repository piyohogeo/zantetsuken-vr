using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// A snapshot over the same registrations in a short history and in one with far more unrelated cuts (2026-09-29):
    /// both built, with the same **counts** of branches, candidates and render fragments and, per candidate, the same
    /// side, pending flag and selection state -- not a comparison of the planes or of which render fragment each branch
    /// belongs to (the candidates themselves are compared with the old listing in VpClipCandidatesChainWalkTests) --
    /// and the origin reads the ledger counts for the build grow by at most one per unrelated operation (its source has
    /// no origin), not by the depth times the registrations. (One read of the held admission order per operation
    /// remains too: the validation walks every operation in order, by design.)
    /// </summary>
    public class VpMultiCutSnapshotHistoryTests
    {
        private static readonly VpClipBoundary[] k_none = System.Array.Empty<VpClipBoundary>();

        private static float4 Plane(int k) => new float4(0f, 1f, 0f, -0.05f * (k % 10 + 1));

        private static (LogicalFragmentId positive, LogicalFragmentId negative) Cut(LogicalCutLedger ledger, LogicalFragmentId source, int k)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (positive, negative);
        }

        // `roots` registered lineages, each cut `depth` deep down its positive side, with `noise` unrelated cuts before
        // each of those cuts. The registrations are the roots, drawn whole, reflecting nothing.
        private static (LogicalCutLedger ledger, VpMultiCutRegistration[] registrations) History(int roots, int depth, int noise)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(16));
            var registrations = new VpMultiCutRegistration[roots];
            int k = 0;
            for (int r = 0; r < roots; r++)
            {
                LogicalFragmentId root = ledger.AddFragment();
                LogicalFragmentId at = root;
                for (int d = 0; d < depth; d++)
                {
                    for (int n = 0; n < noise; n++) Cut(ledger, ledger.AddFragment(), k++);
                    at = Cut(ledger, at, k++).positive;
                }

                registrations[r] = new VpMultiCutRegistration(root, new Bounds(Vector3.zero, Vector3.one * 2f), Matrix4x4.identity, Matrix4x4.identity, k_none, 0.001f);
            }

            return (ledger, registrations);
        }

        private static string Shape(VpMultiCutSnapshot s)
        {
            var text = new System.Text.StringBuilder();
            text.Append(s.BranchCount).Append('/').Append(s.CandidateCount).Append('/').Append(s.RenderFragmentCount).Append(':');
            for (int b = 0; b < s.BranchCount; b++)
            {
                Assert.That(s.TryGetBranch(b, out VpMultiCutBranch branch), Is.True);
                text.Append(' ').Append(branch.candidateCount).Append('+').Append(branch.selectedCount);
                for (int c = 0; c < branch.candidateCount; c++)
                {
                    Assert.That(s.TryGetCandidate(branch.candidateStart + c, out VpClipCandidate candidate, out VpClipSelectionState state), Is.True);
                    text.Append(state == VpClipSelectionState.Selected ? 's' : 'i').Append(candidate.boundary.side > 0 ? '+' : '-').Append(candidate.pending ? 'p' : '.');
                }
            }

            return text.ToString();
        }

        [Test]
        public void TheSameRegistrations_BuildTheSameCounts_AndTheReadsGrowByOnePerUnrelatedOperationAtMost()
        {
            (LogicalCutLedger small, VpMultiCutRegistration[] a) = History(4, 12, 1);
            (LogicalCutLedger large, VpMultiCutRegistration[] b) = History(4, 12, 40);
            Assert.That(large.OperationCount, Is.GreaterThan(small.OperationCount * 20));
            var capacities = new VpMultiCutCapacities(64, 512, 64, 512, 64);
            var s1 = new VpMultiCutSnapshot(capacities);
            var s2 = new VpMultiCutSnapshot(capacities);
            long originsSmall = small.OriginLookups, originsLarge = large.OriginLookups;
            long orderSmall = small.AdmissionOrderReads, orderLarge = large.AdmissionOrderReads;
            Assert.That(s1.TryBuild(small, a), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(s2.TryBuild(large, b), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(Shape(s2), Is.EqualTo(Shape(s1)), "the same counts, and per candidate the same side, pending flag and selection state");
            // The validation still walks every operation once, and an unrelated operation's source has no origin: one
            // origin read each. What is gone is the walk of every operation's lineage up to a root against every
            // registration: the reads grow by at most one per operation added, not by depth times registrations.
            long readsSmall = small.OriginLookups - originsSmall, readsLarge = large.OriginLookups - originsLarge;
            Assert.That(readsLarge - readsSmall, Is.LessThanOrEqualTo(large.OperationCount - small.OperationCount),
                "at most one origin read per unrelated operation added (small " + readsSmall + " over " + small.OperationCount + " operations, large " + readsLarge + " over " + large.OperationCount + ")");
            TestContext.Out.WriteLine("origin reads: small " + readsSmall + " (operations " + small.OperationCount + "), large " + readsLarge + " (operations " + large.OperationCount + ")");
            Assert.That(large.AdmissionOrderReads - orderLarge, Is.EqualTo(large.OperationCount + 1), "every operation is still walked once, in order");
            Assert.That(small.AdmissionOrderReads - orderSmall, Is.EqualTo(small.OperationCount + 1));
        }
    }
}
