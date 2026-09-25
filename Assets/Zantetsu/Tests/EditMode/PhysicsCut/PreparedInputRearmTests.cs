using System;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe partial class VpPreparedPhysicsInputTests
    {
        [Test] public void D6_Rearm_ReusesMeshesAndBank_WritesNewPoseAndBounds()
        {
            var input=Input(Bank(2));Assert.That(input.TryRearmAfterRefusal(),Is.False);
            var cold=input.ColdPreparationShape();var meshes=cold.Meshes.ToArray();
            var vertices=meshes.Select(m=>m.vertices).ToArray();var pointer=(long)cold.BankOf(0).vertices;
            for(int attempt=0;attempt<3;attempt++)
            {
                var frames=new[]{Pose(2+attempt),Pose(6+attempt)};var shape=Posed(input,frames);
                Assert.That((long)shape.BankOf(0).vertices,Is.EqualTo(pointer));
                for(int c=0;c<2;c++)
                {
                    Assert.That(shape.MeshOf(c),Is.SameAs(meshes[c]));CollectionAssert.AreEqual(vertices[c],meshes[c].vertices);
                    var r=shape.Convex(c);float3 lo=new float3(float.PositiveInfinity),hi=-lo;
                    for(int v=0;v<r.vertexCount;v++)
                    {
                        var expected=math.transform(frames[c],(float3)vertices[c][v]);
                        Near(shape.BankOf(c).vertices[r.vertexBase+v],expected);lo=math.min(lo,expected);hi=math.max(hi,expected);
                    }
                    shape.ConvexBounds(c,out var actualLo,out var actualHi);Near(lo,actualLo);Near(hi,actualHi);
                }
                Assert.That(input.TryPose(frames,out _),Is.False);
                Assert.That(input.TryRearmAfterRefusal(),Is.True);Assert.That(input.TryRearmAfterRefusal(),Is.False);
                Assert.Throws<InvalidOperationException>(()=>input.TakeShape());
                Assert.Throws<InvalidOperationException>(()=>input.ColdPreparationShape(),"rearmed is not original cold input");
            }
            Assert.That(input.ColdCookCalls,Is.EqualTo(2));
        }
        [TestCase("work")][TestCase("view")][TestCase("mesh-hold")]
        public void D6_Rearm_RefusesReaders(string kind)
        {
            var input=Input(Bank());PhysicsShapeSource hold=kind=="mesh-hold"?input.AcquireColdMeshHold():null;
            var shape=Posed(input,Pose());PhysicsOwnerShape view=null;
            if(kind=="work")shape.AcquireForWork();
            if(kind=="view")view=PhysicsOwnerShape.ProvisionalSide(shape,new[]{0});
            try { Assert.That(input.TryRearmAfterRefusal(),Is.False); }
            finally { if(kind=="work")shape.ReleaseFromWork();view?.Dispose();hold?.Release(); }
            Assert.That(input.TryRearmAfterRefusal(),Is.True);
        }
        [TestCase("failed")][TestCase("transferred")][TestCase("disposed")]
        public void D6_Rearm_RejectsTerminalState(string state)
        {
            var input=Input(Bank());
            if(state=="failed")Assert.That(input.TryPose(null,out _),Is.False);
            else Posed(input,Pose());
            if(state=="transferred")Keep(input.TakeShape());
            if(state=="disposed")input.Dispose();
            Assert.That(input.TryRearmAfterRefusal(),Is.False);
        }
    }
}
