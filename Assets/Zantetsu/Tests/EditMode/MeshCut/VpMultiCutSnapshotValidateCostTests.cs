using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The structural validation's refusal of two registrations of one root (TL, 2026-09-30: the coexistence run's
    /// Validate stage reached 185 ms; each registration compared its root with every other's). Measured at 500, 1500 and
    /// 3000 registrations -- the root comparisons and the seconds, written out, not judged -- with the snapshot's content
    /// written as a fingerprint; and the refusals kept: two registrations of one root, a root registered with a root
    /// above it, and an input outside the contract before either, each in the order the checks always had.
    /// </summary>
    public class VpMultiCutSnapshotValidateCostTests
    {
        private static readonly VpClipBoundary[] k_none = System.Array.Empty<VpClipBoundary>();

        private static float4 Plane(int k) => new float4(0f, 1f, 0f, -0.05f * (k % 10 + 1));

        private static LogicalFragmentId Cut(LogicalCutLedger ledger, LogicalFragmentId source, int k, out LogicalFragmentId negative)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return positive;
        }

        private static VpMultiCutRegistration Registration(LogicalFragmentId root, float x) =>
            new VpMultiCutRegistration(root, new Bounds(Vector3.zero, Vector3.one * 2f), Matrix4x4.Translate(new Vector3(x, 0f, 0f)), Matrix4x4.identity, k_none, 0.001f);

        /// <summary>Roots, each cut twice (its positive side again), registered at their own places.</summary>
        private static (LogicalCutLedger, List<VpMultiCutRegistration>, List<LogicalFragmentId>) History(int roots)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(16));
            var registrations = new List<VpMultiCutRegistration>(roots);
            var children = new List<LogicalFragmentId>(roots);
            int k = 0;
            for (int r = 0; r < roots; r++)
            {
                LogicalFragmentId root = ledger.AddFragment();
                LogicalFragmentId child = Cut(ledger, root, k++, out _);
                Cut(ledger, child, k++, out _);
                children.Add(child);
                registrations.Add(Registration(root, 3f * r));
            }

            return (ledger, registrations, children);
        }

        private static VpMultiCutSnapshot NewSnapshot() => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 32768, 16384, 131072, 16));

        private static string Fingerprint(VpMultiCutSnapshot s) =>
            "registrations " + s.RegistrationCount + ", branches " + s.BranchCount + ", candidates " + s.CandidateCount + ", render fragments " + s.RenderFragmentCount
            + ", conditions " + s.ConditionCount + ", caps " + s.CapCount + ", cap vertices " + s.CapVertexCount;

        [Test]
        public void TheRootRefusal_AtFiveHundredToThreeThousandRegistrations_IsMeasured_AndTheSnapshotIsTheSame([Values(500, 1500, 3000)] int roots)
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = History(roots);
            var seconds = new List<double>();
            var builds = new List<double>();
            long comparisons = 0;
            string fingerprint = null;
            var stageMs = new List<string>();
            for (int repeat = 0; repeat < 5; repeat++)
            {
                VpMultiCutSnapshot snapshot = NewSnapshot();   // a new snapshot: its structure is validated, not settled
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Assert.That(snapshot.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                builds.Add(watch.Elapsed.TotalMilliseconds);
                Assert.That(snapshot.StructureValidations, Is.EqualTo(1));
                seconds.Add(snapshot.LastStructureValidateSeconds * 1000.0);
                stageMs.Add("validate " + (snapshot.LastStructureValidateSeconds * 1000).ToString("F1") + " collect " + (snapshot.LastCollectSeconds * 1000).ToString("F1") + " group " + (snapshot.LastGroupSeconds * 1000).ToString("F1") + " place " + (snapshot.LastPlaceSeconds * 1000).ToString("F1"));
                comparisons = snapshot.RootComparisons;
                string now = Fingerprint(snapshot);
                if (fingerprint != null) Assert.That(now, Is.EqualTo(fingerprint), "the same snapshot every time");
                fingerprint = now;
            }

            seconds.Sort();
            builds.Sort();
            TestContext.Out.WriteLine(roots + " registrations, " + ledger.OperationCount + " operations: root comparisons " + comparisons + " a validation; validate ms median " + seconds[2].ToString("F3")
                + " (min " + seconds[0].ToString("F3") + ", max " + seconds[4].ToString("F3") + "); whole build ms median " + builds[2].ToString("F3") + "; snapshot: " + fingerprint);
TestContext.Out.WriteLine("  stages (ms), each build: " + string.Join(" | ", stageMs));
            Assert.That(fingerprint, Does.StartWith("registrations " + roots + ","));
        }

        [Test]
        public void TwoRegistrationsOfOneRoot_AreRefusedAsLineage_WhereverTheyStand()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = History(40);
            registrations.Add(Registration(registrations[7].root, 500f));   // the same root again, last
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
            Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.Lineage));

            registrations.RemoveAt(registrations.Count - 1);
            registrations.Insert(0, Registration(registrations[30].root, 500f));   // the same root again, first
            snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
            Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.Lineage));
        }

        [Test]
        public void ARootRegisteredWithOneAboveIt_IsRefusedAsLineage()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, List<LogicalFragmentId> children) = History(40);
            registrations.Add(Registration(children[12], 500f));   // a fragment below root 12, registered beside it
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
            Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.Lineage));
        }

        [Test]
        public void AnInputOutsideTheContract_IsRefusedBeforeATwiceRegisteredRoot()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = History(40);
            registrations.Insert(0, Registration(registrations[5].root, 500f));   // a root twice, first
            registrations.Add(new VpMultiCutRegistration(ledger.AddFragment(), new Bounds(Vector3.zero, new Vector3(float.NaN, 1f, 1f)), Matrix4x4.identity, Matrix4x4.identity, k_none, 0.001f));   // last: outside the contract
            VpMultiCutSnapshot snapshot = NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
            Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.InputContract), "the contract is judged for every registration before any lineage");
        }
    }
}
