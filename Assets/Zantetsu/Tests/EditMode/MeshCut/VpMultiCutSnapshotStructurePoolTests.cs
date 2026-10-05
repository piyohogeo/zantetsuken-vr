using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        [Test]
        public void StructurePool_ChangingA_DoesNotReadOrRebuildB()
        {
            using (Scene scene = NewScene())
            {
                var ledger = scene.ledger; var display = scene.display;
                var a = ledger.AddFragment(); var b = ledger.AddFragment();
                Assert.That(display.TryShow(a, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Assert.That(display.TryShow(b, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                var at = b;
                for (int i = 0; i < 5; i++)
                {
                    var cut = Cut(ledger, at, Tilted(i));
                    ledger.CompleteGeometry(cut.cut); at = cut.positive;
                }
                Collect(scene);
                object bStructure = display.StructureForTest(b);
                long families = display.FamiliesRebuiltForTest;
                long admissions = ledger.AdmissionOrderReads;
                var operation = Admit(ledger, a, new float4(1, 0, 0, 0));
                Collect(scene);
                Assert.That(display.StructureForTest(b), Is.SameAs(bStructure));
                Assert.That(display.FamiliesRebuiltForTest - families, Is.EqualTo(1));
                Assert.That(ledger.AdmissionOrderReads - admissions, Is.EqualTo(1), "only A's operation is validated; B's history is not read");
                Assert.That(ledger.Publish(operation, out var plus, out var minus), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Collect(scene);
                Assert.That(display.StructureForTest(b), Is.SameAs(bStructure));
                Assert.That(ledger.Retire(plus), Is.True);
                Collect(scene);
                Assert.That(display.StructureForTest(b), Is.SameAs(bStructure));
                Assert.That(display.TryGetRenderFragment(RenderFragmentOfRoot(display, minus), out var remaining), Is.True);
                Assert.That(remaining.root, Is.EqualTo(minus));
            }
        }

        [Test]
        public void StructurePool_MotionUpdatesPlacementWithoutRebuilding()
        {
            using (Scene scene = NewScene())
            {
                var root = scene.ledger.AddFragment();
                Assert.That(scene.display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
                Collect(scene);
                object structure = scene.display.StructureForTest(root);
                long families = scene.display.FamiliesRebuiltForTest, origins = scene.ledger.OriginLookups;
                var moved = Matrix4x4.TRS(new Vector3(2, 3, 4), Quaternion.Euler(10, 40, 70), Vector3.one);
                scene.display.Placement = new VpTestPlacements().Put(root, moved);
                Collect(scene);
                Assert.That(scene.display.StructureForTest(root), Is.SameAs(structure));
                Assert.That(scene.display.FamiliesRebuiltForTest, Is.EqualTo(families));
                Assert.That(scene.ledger.OriginLookups, Is.EqualTo(origins));
                Assert.That(scene.display.TryGetRenderFragment(0, out var rendered), Is.True);
                Assert.That(rendered.geometryLocalToWorld, Is.EqualTo(moved));
            }
        }
    }

    public class VpMultiCutSnapshotStructurePoolTests
    {
        private static readonly VpMultiCutCapacities Capacity = new VpMultiCutCapacities(128, 1024, 128, 1024, 64);
        private static VpMultiCutRegistration Input(LogicalFragmentId root, Matrix4x4? placement = null,
            VpReflectedSet reflected = null) => new VpMultiCutRegistration(root, new Bounds(Vector3.zero, Vector3.one * 2),
                placement ?? Matrix4x4.identity, Matrix4x4.identity, reflected ?? VpReflectedSet.Empty, 0.001f);
        private static (CutOperationId cut, LogicalFragmentId plus, LogicalFragmentId minus) Cut(LogicalCutLedger ledger, LogicalFragmentId source, int i)
        {
            Assert.That(ledger.Admit(source, new float4(0, 1, 0, -i * 0.03f), true, out var cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.001f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out var plus, out var minus), Is.EqualTo(LogicalCutResultOutcome.Applied));
            ledger.CompleteGeometry(cut);
            return (cut, plus, minus);
        }
        private static void Same(VpMultiCutSnapshot expected, VpMultiCutSnapshot actual, LogicalCutLedger ledger)
        {
            Assert.That(VpMultiCutSnapshotReflectedIndexTests.Contents(actual, ledger),
                Is.EqualTo(VpMultiCutSnapshotReflectedIndexTests.Contents(expected, ledger)));
            for (int i = 0; i < expected.BranchCount; i++)
            {
                expected.TryGetBranch(i, out var a); actual.TryGetBranch(i, out var b); Assert.That(b, Is.EqualTo(a));
            }
            for (int i = 0; i < expected.RenderFragmentCount; i++)
            {
                expected.TryGetRenderFragment(i, out var a); actual.TryGetRenderFragment(i, out var b); Assert.That(b, Is.EqualTo(a));
                Assert.That(actual.StandsAsForTest(i), Is.EqualTo(expected.StandsAsForTest(i)));
            }
        }

        [Test]
        public void SharedBlocks_SurviveFailedCandidateAndReuse_WithoutChangingAdoptedSnapshot()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(32));
            var a = ledger.AddFragment(); var b = ledger.AddFragment();
            var regs = new List<VpMultiCutRegistration> { Input(a), Input(b) };
            var pool = new VpMultiCutSnapshot.StructurePool(2);
            using (var adopted = new VpMultiCutSnapshot(Capacity))
            using (var candidate = new VpMultiCutSnapshot(Capacity))
            using (var oracle = new VpMultiCutSnapshot(Capacity))
            {
                Assert.That(adopted.TryBuildIncremental(pool, null, ledger, regs, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                object oldA = adopted.StructureForTest(a), oldB = adopted.StructureForTest(b);
                var oldBranches = ((VpMultiCutSnapshot.StructurePool.Part)oldA).branches;
                var saved = VpMultiCutSnapshotReflectedIndexTests.Contents(adopted, ledger);
                var first = Cut(ledger, a, 1);
                Assert.That(candidate.TryBuildIncremental(pool, adopted, ledger, regs, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Assert.That(candidate.StructureForTest(a), Is.Not.SameAs(oldA));
                Assert.That(candidate.StructureForTest(b), Is.SameAs(oldB));
                Assert.That(VpMultiCutSnapshotReflectedIndexTests.Contents(adopted, ledger), Is.EqualTo(saved));
                Assert.That(oracle.TryBuild(ledger, regs), Is.EqualTo(VpMultiCutBuildOutcome.Built)); Same(oracle, candidate, ledger);
                var current = VpMultiCutSnapshotReflectedIndexTests.Contents(candidate, ledger);
                // A failed build releases only its own references; the adopted reader remains valid.
                regs[1] = Input(b, Matrix4x4.zero);
                Assert.That(adopted.TryBuildIncremental(pool, candidate, ledger, regs, null), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
                Assert.That(VpMultiCutSnapshotReflectedIndexTests.Contents(candidate, ledger), Is.EqualTo(current));
                regs[1] = Input(b);
                Assert.That(adopted.TryBuildIncremental(pool, candidate, ledger, regs, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Assert.That(adopted.StructureForTest(b), Is.SameAs(oldB)); Same(oracle, adopted, ledger);
                Assert.That(pool.InUseForTest, Is.EqualTo(2), "both snapshots share two retained blocks");
                ledger.NoteOwnershipChanged(first.plus);
                Assert.That(candidate.TryBuildIncremental(pool, adopted, ledger, regs, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Assert.That(candidate.StructureForTest(a), Is.SameAs(oldA), "a slot is reusable after its last reader releases it");
                Assert.That(((VpMultiCutSnapshot.StructurePool.Part)candidate.StructureForTest(a)).branches, Is.SameAs(oldBranches),
                    "the returned slot retains its internal capacity, not only its outer object");
                Same(oracle, candidate, ledger);
            }
            Assert.That(pool.InUseForTest, Is.Zero, "the last reader releases each slot exactly once");
        }

        [Test]
        public void InterleavedFamilies_AncestorReflectionSplitRetirementAndIgnoredSelection_MatchFullBuild()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(32));
            var root = ledger.AddFragment(); var b = ledger.AddFragment();
            var first = Cut(ledger, root, 1);
            var at = first.plus;
            for (int i = 2; i < 12; i++) at = Cut(ledger, at, i).plus;
            var regs = new List<VpMultiCutRegistration> { Input(root), Input(b) };
            var pool = new VpMultiCutSnapshot.StructurePool(2);
            var adopted = new VpMultiCutSnapshot(Capacity); var candidate = new VpMultiCutSnapshot(Capacity);
            using (var oracle = new VpMultiCutSnapshot(Capacity))
            try
            {
                for (int step = 0; step < 5; step++)
                {
                    if (step == 1)
                    {
                        // Geometry Commit of the ancestor splits the registration, even though its ledger
                        // completion already happened. The immutable reflected input changes instead.
                        regs.Clear();
                        regs.Add(Input(first.plus, reflected: VpReflectedSet.Empty.With(new VpClipBoundary(new VpCapFace(ledger, first.cut), 1), null)));
                        regs.Add(Input(b));
                        regs.Add(Input(first.minus, reflected: VpReflectedSet.Empty.With(new VpClipBoundary(new VpCapFace(ledger, first.cut), -1), null)));
                    }
                    if (step == 2) ledger.Retire(first.minus);
                    if (step == 3) regs.RemoveAt(2);
                    if (step == 4) regs.Reverse();
                    Assert.That(candidate.TryBuildIncremental(pool, adopted, ledger, regs, null), Is.EqualTo(VpMultiCutBuildOutcome.Built), "incremental " + step);
                    Assert.That(oracle.TryBuild(ledger, regs), Is.EqualTo(VpMultiCutBuildOutcome.Built), "oracle " + step);
                    Same(oracle, candidate, ledger);
                    if (step != 0) Assert.That(candidate.StructureForTest(b), Is.SameAs(adopted.StructureForTest(b)));
                    (adopted, candidate) = (candidate, adopted);
                }
            }
            finally { adopted.Dispose(); candidate.Dispose(); }
        }
    }
}
