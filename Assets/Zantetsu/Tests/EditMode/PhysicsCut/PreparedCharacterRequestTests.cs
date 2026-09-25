using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe partial class PreparedCharacterColdTests
    {
        static float4 CutPlane=>new float4(1,0,0,-.25f);
        VpPreparedCharacterCut Bound(out SkinnedMeshRenderer rig)
        {
            rig=Rig();var bone=Track(new GameObject("D6T bone"));bone.transform.SetParent(rig.transform,false);
            rig.bones=new[]{bone.transform};var body=rig.gameObject.AddComponent<Rigidbody>();
            body.useGravity=false;body.mass=12;body.automaticCenterOfMass=body.automaticInertiaTensor=false;
            body.centerOfMass=Vector3.zero;body.inertiaTensor=Vector3.one*4;
            var h=Bank();var cold=Keep(new VpPhysicsColdPreparation());
            Assert.That(world.TryPrepareCharacterCut(rig,new[]{0,1,2,3},4,h.input.bank,Ranges(h),new[]{bone.transform},
                cold,rig.gameObject,body,out var handle),Is.True);return Keep(handle);
        }
        [Test] public void D6T_Synchronous_Publication_TransfersOnce_KeepsBorrowedRig()
        {
            NewWorld();var handle=Bound(out var rig);var actor=Field<GameObject>(handle,"actor");
            var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();var mesh=shape.MeshOf(0);
            Assert.That(actor.activeSelf,Is.False);Assert.That(actor.GetComponentsInChildren<Collider>(true),Is.Empty);NoPublication();
            var result=handle.TryCut(CutPlane,float3.zero);
            Assert.That(result.Outcome,Is.EqualTo(VpCharacterCutOutcome.Requested));Assert.That(result.Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(result.Source,Is.EqualTo(handle.Source));Assert.That(result.Operation,Is.EqualTo(handle.Operation));
            Assert.That(world.Owners.ProvisionalPairCount,Is.EqualTo(1));Assert.That(world.Storage.VertexCount,Is.EqualTo(4));
            Assert.That(rig.gameObject.activeSelf,Is.False);Assert.That(rig!=null&&mesh!=null&&!shape.IsFreed,Is.True);
            handle.Dispose();Assert.That(handle.TryCut(CutPlane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Unavailable));
            Assert.That(world.Shutdown(),Is.True);Assert.That(shape.IsFreed,Is.True);Assert.That(rig!=null,Is.True);
        }
        [TestCase(false)][TestCase(true)] public void D6T_Refusal_RearmsSameMesh_ThenDifferentPosePublishes(bool empty)
        {
            NewWorld();var handle=Bound(out var rig);var input=Field<VpPreparedPhysicsInput>(handle,"physics");
            var shape=input.ColdPreparationShape();var mesh=shape.MeshOf(0);long bank=(long)shape.BankOf(0).vertices;
            var blocker=new LogicalCutLedger(world.Ledger.Budget);var ops=new List<CutOperationId>();
            while(!world.Ledger.Budget.IsFull){blocker.Admit(blocker.AddFragment(),CutPlane,true,out var op);ops.Add(op);}
            for(int i=0;i<3;i++)
            {
                rig.bones[0].localPosition=new Vector3(i*.1f,0,0);
                var result=handle.TryCut(empty?new float4(1,0,0,1000):CutPlane,float3.zero);
                Assert.That(result.Outcome,Is.EqualTo(empty?VpCharacterCutOutcome.EmptySide:VpCharacterCutOutcome.Full));
                Assert.That(handle.IsReady,Is.True);Assert.That(input.IsPosed,Is.False);Assert.That(rig.gameObject.activeSelf,Is.True);NoPublication();
            }
            foreach(var op in ops)blocker.Abort(op);
            rig.bones[0].localPosition=new Vector3(.3f,0,0);
            Assert.That(handle.TryCut(new float4(1,0,0,-.55f),float3.zero).Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(shape.MeshOf(0),Is.SameAs(mesh));Assert.That((long)shape.BankOf(0).vertices,Is.EqualTo(bank));
            Assert.That(input.ColdCookCalls,Is.EqualTo(1));
        }
        [TestCase("nan")][TestCase("zero")][TestCase("impulse")]
        public void D6T_BadRequest_DoesNotConsumePreparation(string kind)
        {
            NewWorld();var handle=Bound(out _);
            var result=handle.TryCut(kind=="nan"?new float4(float.NaN):kind=="zero"?float4.zero:CutPlane,float3.zero,kind=="impulse"?-1:0);
            Assert.That(result.Outcome,Is.EqualTo(VpCharacterCutOutcome.Failed));Assert.That(handle.IsReady,Is.True);NoPublication();
        }
        [TestCase("bone")][TestCase("skin")][TestCase("display")]
        public void D6T_FailureIsTerminal_DoesNotRetryOrStopOriginal(string kind)
        {
            NewWorld();var handle=Bound(out var rig);var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();
            if(kind=="bone")rig.bones[0].localScale=Vector3.one*2;
            if(kind=="skin")rig.quality=SkinQuality.Bone2;
            if(kind=="display")Assert.That(world.Display.TryBeginFrame(),Is.True);
            Assert.That(handle.TryCut(CutPlane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Failed));
            Assert.That(handle.IsDisposed,Is.True);Assert.That(shape.IsFreed,Is.True);Assert.That(rig.gameObject.activeSelf,Is.True);
            Assert.That(world.Owners.Count+world.Ledger.OperationCount,Is.Zero);
            Assert.That(world.Storage.VertexCount,Is.EqualTo(kind=="display"?4:0),"append is not rolled back");
            Assert.That(handle.TryCut(CutPlane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Unavailable));
        }
        [Test] public void D6T_UploadException_ReleasesOnlyUntransferredResources()
        {
            NewWorld();var handle=Bound(out var rig);var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();
            var slot=Field<VpLogicalCutDisplay.PreparedRoot>(handle,"slot");Field<VpGpuIndexedGeometryBuffers>(world.Display,"_buffers").VertexBuffer.Dispose();
            Assert.Catch<Exception>(()=>handle.TryCut(CutPlane,float3.zero));
            Assert.That(slot.IsConsumed&&handle.IsDisposed&&shape.IsFreed,Is.True);
            Assert.That(world.Storage.VertexCount,Is.EqualTo(4));Assert.That(world.Owners.Count,Is.Zero);Assert.That(rig.gameObject.activeSelf,Is.True);
        }
        [TestCase(false)][TestCase(true)] public void D6T_RegistrationException_RespectsActualShapeOwner(bool afterRegistry)
        {
            NewWorld();var handle=Bound(out var rig);var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();
            object frames=null;
            if(afterRegistry){frames=Field<object>(world.Geometry,"_frameOf");Set(world.Geometry,"_frameOf",null);}
            else Set(world.Display,"_frameSource",(Func<int>)(()=>{Set(handle,"actorBody",null);return Time.frameCount;}));
            try {Assert.Catch<Exception>(()=>handle.TryCut(CutPlane,float3.zero));}
            finally
            {
                if(afterRegistry)Set(world.Geometry,"_frameOf",frames);
                else Set(world.Display,"_frameSource",null);
            }
            Assert.That(handle.IsDisposed,Is.True);Assert.That(shape.IsFreed,Is.EqualTo(!afterRegistry));
            Assert.That(world.Owners.Count,Is.EqualTo(afterRegistry?1:0));Assert.That(rig.gameObject.activeSelf,Is.True);
            Assert.That(world.Shutdown(),Is.True);Assert.That(shape.IsFreed,Is.True);
            Assert.That(rig.gameObject.activeSelf,Is.True,"bridge was not attached before DAG registration completed");
        }
        [TestCase(false)][TestCase(true)] public void D6T_PublicationCallback_ReentryRefused_DisposeDeferred(bool throws)
        {
            NewWorld();var handle=Bound(out var rig);var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();
            ProvisionalPhysicsPublication.publishedHook=()=>
            {
                Assert.That(handle.TryCut(CutPlane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Unavailable));
                handle.Dispose();Assert.That(handle.IsDisposed,Is.False);Assert.That(shape.IsFreed,Is.False);
                if(throws)throw new InvalidOperationException("D6T after publication");
            };
            try
            {
                if(throws)Assert.Throws<InvalidOperationException>(()=>handle.TryCut(CutPlane,float3.zero));
                else Assert.That(handle.TryCut(CutPlane,float3.zero).Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            }
            finally {ProvisionalPhysicsPublication.publishedHook=null;}
            Assert.That(handle.IsDisposed&&handle.Operation.IsSet,Is.True);Assert.That(shape.IsFreed,Is.False);
            Assert.That(world.Driver.Transactions.Count,Is.EqualTo(1));Assert.That(world.Owners.ProvisionalPairCount,Is.EqualTo(1));
            Assert.That(rig.gameObject.activeSelf,Is.False);Assert.That(world.Shutdown(),Is.True);Assert.That(shape.IsFreed,Is.True);
        }
        [Test] public void D6T_PublicationCallback_WorldShutdownDeferredUntilOuterScopeReturns()
        {
            NewWorld();var handle=Bound(out _);var shape=Field<VpPreparedPhysicsInput>(handle,"physics").ColdPreparationShape();
            ProvisionalPhysicsPublication.publishedHook=()=>{Assert.That(world.Shutdown(),Is.False);Assert.That(world.IsReleased||shape.IsFreed,Is.False);};
            try {Assert.That(handle.TryCut(CutPlane,float3.zero).Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));}
            finally {ProvisionalPhysicsPublication.publishedHook=null;}
            Assert.That(world.IsReleased&&shape.IsFreed,Is.True);
        }
        [Test] public void D6T_WorldReentry_SecondHandleRemainsUnconsumed()
        {
            NewWorld();var a=Bound(out _);var b=Bound(out var rigB);
            ProvisionalPhysicsPublication.publishedHook=()=>Assert.That(b.TryCut(CutPlane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Unavailable));
            try {Assert.That(a.TryCut(CutPlane,float3.zero).Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));}
            finally {ProvisionalPhysicsPublication.publishedHook=null;}
            Assert.That(b.IsReady&&rigB.gameObject.activeSelf,Is.True);Assert.That(b.Source.IsSet,Is.False);
        }
    }
}
