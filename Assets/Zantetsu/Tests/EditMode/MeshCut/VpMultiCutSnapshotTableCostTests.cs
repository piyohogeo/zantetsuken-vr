using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The validation's working tables (2026-09-29): what they keep. The pass or fail is decided by the entry counts
    /// (one per root, at most one per fragment an operation was cut from, refilled to the same size by the same build);
    /// the retained managed memory around each build is written out as a diagnostic only -- it is a difference of what is
    /// retained across a forced collection, not the bytes the build allocated, and the tables expand again whenever the
    /// history grows past what they have met.
    /// </summary>
    public class VpMultiCutSnapshotTableCostTests
    {
        private static readonly VpClipBoundary[] k_none = System.Array.Empty<VpClipBoundary>();

        private static float4 Plane(int k) => new float4(0f, 1f, 0f, -0.05f * (k % 10 + 1));

        private static LogicalFragmentId Cut(LogicalCutLedger ledger, LogicalFragmentId source, int k)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return positive;
        }

        private static int Count(VpMultiCutSnapshot s, string field)
        {
            FieldInfo f = typeof(VpMultiCutSnapshot).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(f, Is.Not.Null, field);
            return ((System.Collections.ICollection)f.GetValue(s)).Count;
        }

        [Test]
        public void TheValidationTables_AllocateOnGrowthOnly_AndHoldOneEntryPerFragmentReached()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(16));
            var registrations = new List<VpMultiCutRegistration>();
            int k = 0;
            void Grow(int roots, int depth, int noise)
            {
                for (int r = 0; r < roots; r++)
                {
                    LogicalFragmentId root = ledger.AddFragment();
                    LogicalFragmentId at = root;
                    for (int d = 0; d < depth; d++)
                    {
                        for (int n = 0; n < noise; n++) Cut(ledger, ledger.AddFragment(), k++);
                        at = Cut(ledger, at, k++);
                    }

                    registrations.Add(new VpMultiCutRegistration(root, new Bounds(Vector3.zero, Vector3.one * 2f), Matrix4x4.identity, Matrix4x4.identity, k_none, 0.001f));
                }
            }

            var snapshot = new VpMultiCutSnapshot(new VpMultiCutCapacities(256, 2048, 256, 2048, 64));
            // Retained managed memory around the build (a full collection before and after): what the build keeps, which
            // for an unchanged history is the tables' room already there. (The per-thread allocation counter reads 0
            // under this runtime, so it is not used.)
            long Build(string what, out int roots, out int lineages)
            {
                long before = System.GC.GetTotalMemory(true);
                Assert.That(snapshot.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built), what);
                long bytes = System.GC.GetTotalMemory(true) - before;
                roots = Count(snapshot, "_registrationOfRoot");
                lineages = Count(snapshot, "_lineageOf");
                TestContext.Out.WriteLine(what + ": operations " + ledger.OperationCount + ", registrations " + registrations.Count + ", retained "
                    + bytes + " B, tables hold roots " + roots + " lineages " + lineages);
                return bytes;
            }

            Grow(8, 10, 2);
            long first = Build("first build (8 roots, 10 deep, 2 unrelated cuts per level)", out int roots1, out int lineages1);
            long again = Build("the same build again", out int roots2, out int lineages2);
            Assert.That(roots1, Is.EqualTo(8));
            Assert.That(lineages1, Is.EqualTo(lineages2), "the tables are cleared and refilled to the same size");
            Assert.That(lineages1, Is.LessThanOrEqualTo(ledger.OperationCount), "at most one entry per fragment an operation was cut from");
            TestContext.Out.WriteLine("retained change on the repeated build: " + again + " B (diagnostic: a difference of retained memory, not the bytes allocated)");

            Grow(8, 10, 20);
            long grown = Build("after the history grew (16 roots, 20 unrelated cuts per level)", out int roots3, out int lineages3);
            long grownAgain = Build("the grown history again", out _, out _);
            Assert.That(roots3, Is.EqualTo(16));
            Assert.That(lineages3, Is.GreaterThan(lineages1), "the lineage memo grows with the fragments the operations reach, unrelated ones included");
            TestContext.Out.WriteLine("retained change when the history grew: " + grown + " B; on the next build over it: " + grownAgain + " B (diagnostic)");
            Assert.That(lineages3, Is.LessThanOrEqualTo(ledger.OperationCount), "still at most one entry per fragment an operation was cut from");
        }
    }
}
