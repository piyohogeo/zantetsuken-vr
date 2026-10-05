using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The cut plane's own check, which is in every configuration (DESIGN 5.6, D-191): a plane of the lineage must be
    /// carried by the mapping and by the placement to a finite plane with a normal, and one that is not is refused as
    /// <see cref="VpMultiCutInvalidInput.PlaneNotCarried"/> with nothing built. This is apart from the numeric
    /// input-contract diagnosis (whether a plane, against the box, stays within a float), which is compiled for the
    /// Editor and for Development Players only: nothing here names that diagnosis, so these cases hold on both paths.
    /// </summary>
    public class VpMultiCutSnapshotPlaneCarryTests
    {
        private static readonly Bounds k_box = VpMultiCutSnapshotTests.k_box;

        // A placement in itself -- finite, affine, invertible, and the placed box far inside a float -- whose inverse is so
        // small along x that a normal along x, carried through it, has a squared length below the least float: no normal
        // is left, and there is no plane to hand back.
        private static readonly Matrix4x4 k_flatAlongX = Matrix4x4.Scale(new Vector3(1e23f, 1f, 1f));

        [Test]
        public void APlaneThePlacementCannotCarry_IsRefusedAsNotCarried_AndNothingIsBuilt()
        {
            Assert.That(VpCutPlane.TryGeometryLocalToWorld(new float4(1f, 0f, 0f, 0f), k_flatAlongX, out _), Is.False, "the layout: the plane's own conversion refuses it");
            Assert.That(VpCutPlane.TryGeometryLocalToWorld(new float4(0f, 1f, 0f, 0f), k_flatAlongX, out _), Is.True, "and carries a plane whose normal the placement does not flatten");

            LogicalCutLedger ledger = VpMultiCutSnapshotTests.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            VpMultiCutSnapshotTests.Cut(ledger, root, new float4(1f, 0f, 0f, 0f));
            VpMultiCutSnapshot snapshot = VpMultiCutSnapshotTests.NewSnapshot();
            Assert.That(
                snapshot.TryBuild(ledger, root, k_box, k_flatAlongX, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(k_box)),
                Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "a plane the placement cannot carry");
            Assert.That(snapshot.InvalidInputReason, Is.EqualTo(VpMultiCutInvalidInput.PlaneNotCarried));
            Assert.That(snapshot.IsBuilt, Is.False);
            Assert.That(snapshot.CapCount, Is.Zero, "nothing readable, no empty cap in its place");

            // The same snapshot, the same placement, a plane it does carry: built, with its caps.
            LogicalCutLedger carried = VpMultiCutSnapshotTests.NewLedger();
            LogicalFragmentId body = carried.AddFragment();
            VpMultiCutSnapshotTests.Cut(carried, body, new float4(0f, 1f, 0f, 0f));
            Assert.That(
                snapshot.TryBuild(carried, body, k_box, k_flatAlongX, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(k_box)),
                Is.EqualTo(VpMultiCutBuildOutcome.Built), "a plane the placement carries");
            Assert.That(snapshot.CapCount, Is.GreaterThan(0));
        }

        [Test]
        public void AnOrdinaryPlaneAtAnOrdinaryPlacement_IsCarried_AndItsCapsStandOnTheWorldPlane()
        {
            LogicalCutLedger ledger = VpMultiCutSnapshotTests.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            VpMultiCutSnapshotTests.Cut(ledger, root, new float4(1f, 0f, 0f, -0.25f));
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(3f, -2f, 5f), Quaternion.Euler(0f, 90f, 0f), Vector3.one);
            Assert.That(VpCutPlane.TryGeometryLocalToWorld(new float4(1f, 0f, 0f, -0.25f), placement, out float4 world), Is.True);
            VpMultiCutSnapshot snapshot = VpMultiCutSnapshotTests.NewSnapshot();
            Assert.That(
                snapshot.TryBuild(ledger, root, k_box, placement, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(k_box)),
                Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(snapshot.CapCount, Is.GreaterThan(0));
            int vertices = 0;
            for (int c = 0; c < snapshot.CapCount; c++)
            {
                Assert.That(snapshot.TryGetCap(c, out VpMultiCutCap cap), Is.True);
                for (int v = 0; snapshot.TryGetCapVertex(c, v, out Vector3 at); v++)
                {
                    float distance = (world.x * at.x) + (world.y * at.y) + (world.z * at.z) + world.w;
                    Assert.That(Mathf.Abs(distance), Is.LessThan(1e-4f), "cap " + c + " vertex " + v + " on the carried plane");
                    Assert.That(!float.IsNaN(at.x) && !float.IsInfinity(at.x) && !float.IsNaN(at.y) && !float.IsInfinity(at.y) && !float.IsNaN(at.z) && !float.IsInfinity(at.z), Is.True);
                    vertices++;
                }
            }

            Assert.That(vertices, Is.GreaterThan(0), "the caps have vertices");
        }
    }
}
