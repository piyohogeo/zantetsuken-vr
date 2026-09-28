using System;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    // A crowd slot's baked bone-local meshes (VpBakedConvexMeshes), synthetic boxes only: an input made from them bakes
    // nothing and uses the very same meshes, and the meshes go only when the last holder -- the slot's object, an input,
    // a shape or a piece that inherited one -- lets go, whatever the order.
    public unsafe partial class VpPreparedPhysicsInputTests
    {
        static ConvexBrepRange[] Ranges(OwnerCutHarness h)
        {
            var ranges = new ConvexBrepRange[h.input.convexCount];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = h.input.convexes[i];
            return ranges;
        }

        [Test] public void Baked_InputBakesNothing_AndTakesTheSameMeshes()
        {
            var h = Bank(2); var ranges = Ranges(h);
            var baked = Keep(new VpBakedConvexMeshes(h.input.bank, ranges));
            Assert.That(baked.ColdCookCalls, Is.EqualTo(2));
            var a = Keep(new VpPreparedPhysicsInput(h.input.bank, ranges, baked));
            var b = Keep(new VpPreparedPhysicsInput(h.input.bank, ranges, baked));
            Assert.That(a.ColdCookCalls, Is.Zero); Assert.That(b.ColdCookCalls, Is.Zero); Assert.That(baked.Uses, Is.EqualTo(2));
            var sa = Posed(a, Pose(), Pose(5)); var sb = Posed(b, Pose(8), Pose(9));
            for (int c = 0; c < 2; c++) Assert.That(sa.MeshOf(c), Is.SameAs(sb.MeshOf(c)));
            // Each input's pose is its own: the B-rep copies differ, the meshes stay bone-local.
            Assert.That(sa.BankOf(0).vertices[sa.Convex(0).vertexBase].Equals(sb.BankOf(0).vertices[sb.Convex(0).vertexBase]), Is.False);
            Assert.That(((float3)sa.MeshOf(0).vertices[0]).Equals(h.input.bank.vertices[ranges[0].vertexBase]), Is.True);
        }

        [Test] public void Baked_MeshesOutliveTheSlotObject_WhileAnInputHoldsThem()
        {
            var h = Bank(); var ranges = Ranges(h);
            var baked = new VpBakedConvexMeshes(h.input.bank, ranges);
            var input = new VpPreparedPhysicsInput(h.input.bank, ranges, baked);
            var mesh = input.ColdPreparationShape().MeshOf(0);
            baked.Dispose(); Assert.That(mesh != null, Is.True); Assert.That(baked.IsReleased, Is.False);
            input.Dispose(); Assert.That(mesh == null, Is.True); Assert.That(baked.IsReleased, Is.True);
        }

        [Test] public void Baked_MeshesOutliveEveryInput_WhileTheSlotObjectHoldsThem()
        {
            var h = Bank(); var ranges = Ranges(h);
            var baked = new VpBakedConvexMeshes(h.input.bank, ranges);
            var first = new VpPreparedPhysicsInput(h.input.bank, ranges, baked);
            var mesh = first.ColdPreparationShape().MeshOf(0);
            first.Dispose(); Assert.That(mesh != null, Is.True);
            var second = new VpPreparedPhysicsInput(h.input.bank, ranges, baked);
            Assert.That(second.ColdPreparationShape().MeshOf(0), Is.SameAs(mesh));
            second.Dispose(); Assert.That(mesh != null, Is.True);
            baked.Dispose(); Assert.That(mesh == null, Is.True);
            Assert.Throws<ObjectDisposedException>(() => new VpPreparedPhysicsInput(h.input.bank, ranges, baked));
        }

        [Test] public void Baked_InheritedPieceHoldsTheMeshes_AfterSlotAndInputLetGo()
        {
            var h = Bank(); var ranges = Ranges(h);
            var baked = new VpBakedConvexMeshes(h.input.bank, ranges);
            var input = new VpPreparedPhysicsInput(h.input.bank, ranges, baked);
            var shape = Posed(input, Pose()); var mesh = shape.MeshOf(0);
            var products = Products(shape); var final = Keep(PhysicsOwnerShape.OfSide(shape, products, PhysicsShapeSource.For(products), true));
            var recut = Keep(PhysicsOwnerShape.ProvisionalSide(final, new[] { 0 }));
            baked.Dispose(); input.Dispose(); final.Dispose();
            Assert.That(mesh != null, Is.True, "a published piece still names it");
            recut.Dispose(); Assert.That(mesh == null, Is.True); Assert.That(baked.IsReleased, Is.True);
        }

        [Test] public void Baked_AnotherInputIsRefused_AndNothingIsHeld()
        {
            var h = Bank(); var ranges = Ranges(h);
            var baked = Keep(new VpBakedConvexMeshes(h.input.bank, ranges));
            var other = Bank(); other.input.bank.vertices[other.input.convexes[0].vertexBase] += new float3(0.01f, 0, 0);
            Assert.That(baked.Matches(other.input.bank, Ranges(other)), Is.False);
            Assert.That(baked.Matches(h.input.bank, ranges), Is.True);
            Assert.Throws<ArgumentException>(() => new VpPreparedPhysicsInput(other.input.bank, Ranges(other), baked));
            Assert.That(baked.Uses, Is.Zero);
        }
    }
}
