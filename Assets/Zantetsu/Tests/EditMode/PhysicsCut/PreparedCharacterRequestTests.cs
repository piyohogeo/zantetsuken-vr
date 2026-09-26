using System;
using System.Collections.Generic;
using System.Linq;
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

        // Every collider of both Provisional sides, with the shape convex it stands for.
        static IEnumerable<(PhysicsOwnerSide side,PhysicsOwnerShape shape,int convex,MeshCollider collider)> SideColliders(ProvisionalOwnerPair pair)
        {
            foreach(bool positive in new[]{true,false})
            {
                var side=positive?pair.Positive:pair.Negative;var shape=positive?pair.PositiveShape:pair.NegativeShape;
                Assert.That(side.Colliders.Count,Is.EqualTo(shape.ConvexCount));
                for(int i=0;i<side.Colliders.Count;i++)yield return (side,shape,i,side.Colliders[i]);
            }
        }
        static void StandsWhereItsFrameSays(PhysicsOwnerSide side,PhysicsOwnerShape shape,int i,MeshCollider c,MeshColliderCookingOptions cooking)
        {
            var frame=shape.MeshFrameOf(i);
            Assert.That(frame.HasFrame,Is.True,"a prepared convex has its own frame");
            Assert.That(c.gameObject,Is.Not.SameAs(side.ShapeFrame));Assert.That(c.transform.parent,Is.SameAs(side.ShapeFrame.transform),"a dedicated direct child");
            Assert.That(c.transform.localPosition,Is.EqualTo((Vector3)frame.Position));Assert.That(c.transform.localRotation,Is.EqualTo((Quaternion)frame.Rotation));
            Assert.That(c.transform.localScale,Is.EqualTo(Vector3.one));
            Assert.That(c.convex,Is.True);Assert.That(c.cookingOptions,Is.EqualTo(cooking));Assert.That(c.sharedMesh,Is.SameAs(shape.MeshOf(i)));
            Assert.That(c.enabled&&c.gameObject.activeSelf,Is.True);
        }
        static int Copies()=>Resources.FindObjectsOfTypeAll<MeshCollider>().Count(c=>c!=null&&c.name.EndsWith("(Clone)"));

        [Test] public void D6T_ProvisionalColliders_AreCopiesOfTheWorldsTemplate_WhichNeverEntersTheScene()
        {
            NewWorld();var template=world.Driver.ColliderTemplate;
            Assert.That(template,Is.Not.Null);Assert.That(template.sharedMesh,Is.Null);Assert.That(template.convex,Is.True);
            Assert.That(template.cookingOptions,Is.EqualTo(world.Cook.Cooking));Assert.That(template.gameObject.activeInHierarchy,Is.False);
            var holder=template.transform.parent.gameObject;Assert.That(holder.activeSelf,Is.False);Assert.That(Copies(),Is.Zero,"the warming copy went at once");
            var handle=Bound(out _);var result=handle.TryCut(CutPlane,float3.zero);
            Assert.That(result.Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(world.Owners.TryGetProvisional(result.Operation,out var pair),Is.True);
            int copies=0;
            foreach(var (side,shape,i,c) in SideColliders(pair))
            {
                StandsWhereItsFrameSays(side,shape,i,c,world.Cook.Cooking);
                Assert.That(c.name.EndsWith("(Clone)"),Is.True,"a copy of the template");copies++;
            }
            Assert.That(copies,Is.EqualTo(2));
            Assert.That(template.sharedMesh,Is.Null,"the template is left as it was");Assert.That(template.transform.parent.gameObject,Is.SameAs(holder));
            handle.Dispose();Assert.That(world.Shutdown(),Is.True);
            Assert.That(template==null&&holder==null,Is.True,"given back with the world");Assert.That(world.Driver.ColliderTemplate==null,Is.True);
        }

        [TestCase("profile")][TestCase("destroyed")]
        public void D6T_ATemplateNotSetUpForThisBuild_IsNotUsed(string kind)
        {
            NewWorld();var template=world.Driver.ColliderTemplate;
            if(kind=="profile")
            {
                template.cookingOptions=MeshColliderCookingOptions.None;
                Assert.That(template.cookingOptions,Is.Not.EqualTo(world.Cook.Cooking),"a template set up differently");
            }
            else UnityEngine.Object.DestroyImmediate(template.gameObject);
            var handle=Bound(out _);var result=handle.TryCut(CutPlane,float3.zero);
            Assert.That(result.Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(world.Owners.TryGetProvisional(result.Operation,out var pair),Is.True);
            foreach(var (side,shape,i,c) in SideColliders(pair))
            {
                StandsWhereItsFrameSays(side,shape,i,c,world.Cook.Cooking);
                Assert.That(c.name.EndsWith("(Clone)"),Is.False,"made call by call");
            }
            Assert.That(Copies(),Is.Zero);
        }

        [Test] public void D6T_ABuildThatFailsPartWay_LeavesNoCopyBehind_AndTheTemplateAsItWas()
        {
            NewWorld();var template=world.Driver.ColliderTemplate;var handle=Bound(out _);
            int built=0;
            ProvisionalOwnerBuilder.sideBuiltHook=positive=>{built++;if(!positive)throw new InvalidOperationException("injected after both sides' copies");};
            try{Assert.Catch<InvalidOperationException>(()=>handle.TryCut(CutPlane,float3.zero));}
            finally{ProvisionalOwnerBuilder.sideBuiltHook=null;}
            Assert.That(built,Is.EqualTo(2),"both sides were built, copies and all");
            Assert.That(Copies(),Is.Zero,"no copy outlives the failed build");
            Assert.That(template!=null&&template.sharedMesh==null&&template.convex,Is.True,"the template is left as it was");
            Assert.That(world.Owners.ProvisionalPairCount,Is.Zero);
        }

        // Replacing some of a side's colliders destroys each one's own object and nothing next to it.
        [Test] public void D6T_DestroyingOneCopy_LeavesTheOthersWhereTheyWere()
        {
            var root=Track(new GameObject("side"));root.SetActive(false);
            var frame=new GameObject("Shape Frame");frame.transform.SetParent(root.transform,false);
            var holder=Track(new GameObject("holder"));holder.SetActive(false);
            var made=new GameObject("Convex mesh frame");made.transform.SetParent(holder.transform,false);
            var template=made.AddComponent<MeshCollider>();template.cookingOptions=PhysicsCutCook.DefaultCooking;template.convex=true;
            var fa=new PhysicsMeshFrame(quaternion.EulerXYZ(.1f,.2f,.3f),new float3(1,2,3));
            var fb=new PhysicsMeshFrame(quaternion.EulerXYZ(-.4f,0,.2f),new float3(-1,0,2));
            var a=PhysicsOwnerBuilder.CreateMeshCollider(frame,fa,template);
            var b=PhysicsOwnerBuilder.CreateMeshCollider(frame,fb,template);
            var byCalls=PhysicsOwnerBuilder.CreateMeshCollider(frame,fb);
            Assert.That(b.transform.localPosition,Is.EqualTo(byCalls.transform.localPosition),"the same placement as call by call");
            Assert.That(b.transform.localRotation,Is.EqualTo(byCalls.transform.localRotation));
            Assert.That(b.convex&&b.cookingOptions==PhysicsCutCook.DefaultCooking,Is.True);
            Assert.That(frame.transform.childCount,Is.EqualTo(3));
            PhysicsOwnerBuilder.DestroyComponent(a,frame);
            Assert.That(a==null,Is.True,"its own object went with it");
            Assert.That(b!=null&&b.enabled&&b.gameObject.activeSelf,Is.True,"its neighbour stays usable");
            Assert.That(b.transform.parent,Is.SameAs(frame.transform));
            Assert.That(b.transform.localPosition,Is.EqualTo((Vector3)fb.Position));Assert.That(b.transform.localRotation,Is.EqualTo((Quaternion)fb.Rotation));
            Assert.That(frame.transform.childCount,Is.EqualTo(2));
            Assert.That(template.sharedMesh==null&&template.transform.parent==holder.transform,Is.True,"the template is untouched");
        }
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
