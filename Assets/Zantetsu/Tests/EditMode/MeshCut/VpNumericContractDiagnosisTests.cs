#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The numeric input-contract diagnosis (DESIGN 5.6, D-191) is compiled for the Editor and for Development Players
    /// only. Here, in the Editor, it is there and refuses each kind of number outside the contract -- a box, a placement,
    /// a lineage frame, a placed extent, a placement a query answers -- in the build that settles the structure and in
    /// the placement-only build alike, and passes the same input once the number is back inside the contract. A
    /// non-Development Player has none of this; that is checked on its compiled assemblies, not by a test.
    /// </summary>
    public class VpNumericContractDiagnosisTests
    {
        private sealed class Answers : IVpFragmentPlacement
        {
            public Func<LogicalFragmentId, Matrix4x4> answer;

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                geometryLocalToWorld = answer(fragment);
                return VpFragmentPlacementKind.Following;
            }
        }

        private static VpMultiCutSnapshot NewSnapshot() => new VpMultiCutSnapshot(new VpMultiCutCapacities(256, 2048, 256, 4096, 64));

        private static VpMultiCutRegistration With(VpMultiCutRegistration r, Bounds? bounds = null, Matrix4x4? placement = null, Matrix4x4? lineage = null) =>
            new VpMultiCutRegistration(r.root, bounds ?? r.localBounds, placement ?? r.geometryLocalToWorld, lineage ?? r.lineageToGeometryLocal, r.reflected, r.vertexEpsilon);

        private static IEnumerable<(string what, Func<VpMultiCutRegistration, VpMultiCutRegistration> change, VpMultiCutInvalidInput reason)> OutsideTheContract()
        {
            yield return ("a box whose centre is not finite", r => With(r, bounds: new Bounds(new Vector3(float.NaN, 0f, 0f), r.localBounds.size)), VpMultiCutInvalidInput.InputContract);
            yield return ("a box of infinite size", r => With(r, bounds: new Bounds(r.localBounds.center, new Vector3(float.PositiveInfinity, 1f, 1f))), VpMultiCutInvalidInput.InputContract);
            yield return ("a box with a negative extent", r => With(r, bounds: new Bounds(Vector3.zero, new Vector3(-1f, 1f, 1f) * 2f)), VpMultiCutInvalidInput.InputContract);
            yield return ("a placement that is not finite", r => { Matrix4x4 m = r.geometryLocalToWorld; m.m13 = float.NaN; return With(r, placement: m); }, VpMultiCutInvalidInput.InputContract);
            yield return ("a placement that is singular", r => With(r, placement: Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f))), VpMultiCutInvalidInput.InputContract);
            yield return ("a placement that is not affine", r => { Matrix4x4 m = r.geometryLocalToWorld; m.m30 = 0.1f; return With(r, placement: m); }, VpMultiCutInvalidInput.InputContract);
            yield return ("a lineage frame that is not rigid", r => With(r, lineage: Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1.01f, 1f, 1f)) * r.lineageToGeometryLocal), VpMultiCutInvalidInput.InputContract);
            yield return ("a placement no section can be computed at", r => With(r, placement: Matrix4x4.Translate(new Vector3(1e38f, 0f, 0f))), VpMultiCutInvalidInput.ConservativeSection);
        }

        [Test]
        public void InTheEditor_ARegistrationOutsideTheContract_IsRefused_ByTheStructureBuildAndByThePlacementOnlyBuild()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 6);
            int target = registrations.Count / 2;
            int refused = 0;
            foreach ((string what, Func<VpMultiCutRegistration, VpMultiCutRegistration> change, VpMultiCutInvalidInput reason) in OutsideTheContract())
            {
                var changed = new List<VpMultiCutRegistration>(registrations);
                changed[target] = change(registrations[target]);

                // The build that settles the structure.
                var structural = NewSnapshot();
                Assert.That(structural.TryBuild(ledger, changed, null), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ": refused by the structure build");
                Assert.That(structural.InvalidInputReason, Is.EqualTo(reason), what + ": the reason");
                Assert.That(structural.IsBuilt, Is.False, what + ": nothing is drawn from it");

                // The placement-only build, from a structure settled over the registrations as they were.
                var structure = NewSnapshot();
                Assert.That(structure.TryBuild(ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                var placed = NewSnapshot();
                Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built), what + ": inside the contract first");
                Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, changed, null), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ": refused by the placement-only build");
                Assert.That(placed.InvalidInputReason, Is.EqualTo(reason), what + ": the reason");
                Assert.That(placed.IsBuilt, Is.False, what + ": the refused build leaves nothing built");
                Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built), what + ": built again once the number is back inside the contract");
                refused++;
            }

            Assert.That(refused, Is.EqualTo(8));
        }

        [Test]
        public void InTheEditor_APlacementAQueryAnswers_ThatIsNotAPlacement_IsRefused_AndPassesWhenItIsOneAgain()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 6);
            Matrix4x4 Place(LogicalFragmentId f) => Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.identity, Vector3.one);
            var structure = NewSnapshot();
            Assert.That(structure.TryBuild(ledger, registrations, new Answers { answer = Place }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            var placed = NewSnapshot();
            var bad = new (string what, Func<LogicalFragmentId, Matrix4x4> answer)[]
            {
                ("an answer that is not finite", f => { Matrix4x4 m = Place(f); if (f.value % 2 == 0) m.m23 = float.NaN; return m; }),
                ("an answer that is singular", f => f.value % 2 == 0 ? Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 1f, 0f)) : Place(f)),
                ("an answer that is not affine", f => { Matrix4x4 m = Place(f); if (f.value % 2 == 0) m.m32 = 0.5f; return m; }),
            };
            foreach ((string what, Func<LogicalFragmentId, Matrix4x4> answer) in bad)
            {
                Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, new Answers { answer = Place }), Is.EqualTo(VpMultiCutBuildOutcome.Built), what + ": a placement first");
                Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, new Answers { answer = answer }), Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), what + ": refused");
                Assert.That(placed.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.InputContract), what + ": the reason");
                Assert.That(placed.IsBuilt, Is.False, what + ": nothing is drawn from it");
            }

            Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, new Answers { answer = Place }), Is.EqualTo(VpMultiCutBuildOutcome.Built), "a placement again: built");
        }

        [Test]
        public void InTheEditor_APlaneThatDoesNotStayWithinAFloatAgainstTheBox_IsRefused_ApartFromThePlanesOwnCheck()
        {
            // The plane is finite, has a normal, and is carried by the mapping and the placement: the cut plane's own
            // check (kept in every configuration) passes it. What refuses it here is the diagnosis: against this box a
            // corner's distance to it does not stay within a float.
            var farOff = new Unity.Mathematics.float4(1f, 0f, 0f, -3.3e38f);
            Bounds box = VpMultiCutSnapshotTests.k_box;
            Assert.That(VpCutPlane.TryGeometryLocalToWorld(farOff, Matrix4x4.identity, out _), Is.True, "the plane itself is carried");
            Assert.That(VpSectionBounds.IsPlaneWithin(farOff, box), Is.False, "the diagnosis's own judgement");
            Assert.That(VpSectionBounds.IsPlaneWithin(new Unity.Mathematics.float4(1f, 0f, 0f, -10f), box), Is.True, "a plane ten off is within");

            LogicalCutLedger ledger = VpMultiCutSnapshotTests.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            VpMultiCutSnapshotTests.Cut(ledger, root, farOff);
            VpMultiCutSnapshot snapshot = VpMultiCutSnapshotTests.NewSnapshot();
            Assert.That(
                snapshot.TryBuild(ledger, root, box, Matrix4x4.identity, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(box)),
                Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
            Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.ConservativeSection), "refused by the diagnosis, not as a plane that is not carried");
            Assert.That(snapshot.IsBuilt, Is.False);
        }

        [Test]
        public void InTheEditor_TheContractsOwnFunctions_AnswerForWhatADisplayTakesIn()
        {
            var box = new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), Vector3.one);
            Assert.That(VpMultiCutSnapshot.IsWithinInputContract(box, placement, Matrix4x4.identity), Is.True);
            Assert.That(VpMultiCutSnapshot.IsWithinSectionBounds(box, placement, VpCapBoundsPolygon.EpsilonFor(box)), Is.True);
            Assert.That(VpMultiCutSnapshot.IsWithinInputContract(new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one), placement, Matrix4x4.identity), Is.False, "a box that is not finite");
            Assert.That(VpMultiCutSnapshot.IsWithinInputContract(box, Matrix4x4.zero, Matrix4x4.identity), Is.False, "a placement that is singular");
            Assert.That(VpMultiCutSnapshot.IsWithinInputContract(box, placement, Matrix4x4.Scale(new Vector3(2f, 1f, 1f))), Is.False, "a lineage frame that is not rigid");
            Assert.That(VpMultiCutSnapshot.IsWithinSectionBounds(box, Matrix4x4.Translate(new Vector3(1e38f, 0f, 0f)), VpCapBoundsPolygon.EpsilonFor(box)), Is.False,
                "a placement no section can be computed at");
        }
    }
}
#endif
