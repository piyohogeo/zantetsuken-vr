using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public sealed class D6TDisableProbe : MonoBehaviour
    {
        public Action Disabled;
        void OnDisable(){Disabled?.Invoke();}
    }
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        [UnityTest] public IEnumerator D6T_RealOnDisable_ReentryDispose_SameFramePublicationThenFinal()
        {yield return RequestWithCallback(false);}
        [UnityTest] public IEnumerator D6T_RealOnDisable_ShutdownDeferred_NoEarlyFree()
        {yield return RequestWithCallback(true);}
        [UnityTest] public IEnumerator D6T_InvalidEdgeFixture_PublishesThenKernelFailureAborts_NoRevival()
        {yield return RequestWithCallback(false,false);}

        // The mass-only fixture deliberately leaves edge tables empty. A numerical cut needs full authoring.
        static void CompleteRequestBoxEdges(PhysicsOwnerShape shape)
        {
            var bank=shape.BankOf(0);var r=shape.Convex(0);
            var found=new Dictionary<long,int>();var edges=new List<BrepEdge>();
            for(int f=0;f<r.faceCount;f++)
            {
                int start=bank.faceOffsets[r.faceBase+f],end=bank.faceOffsets[r.faceBase+f+1];
                for(int k=start;k<end;k++)
                {
                    int a=bank.faceIndices[r.faceIndexBase+k],b=bank.faceIndices[r.faceIndexBase+(k+1==end?start:k+1)];
                    int lo=math.min(a,b),hi=math.max(a,b);long key=((long)lo<<32)|(uint)hi;
                    if(!found.TryGetValue(key,out int at)){at=edges.Count;found.Add(key,at);edges.Add(new BrepEdge{v0=lo,v1=hi,f0=-1,f1=-1});}
                    var edge=edges[at];if(a==lo)edge.f0=f;else edge.f1=f;edges[at]=edge;
                    bank.faceEdges[r.faceIndexBase+k]=at;
                }
            }
            Assert.That(edges.Count,Is.EqualTo(r.edgeCount));
            for(int i=0;i<edges.Count;i++){Assert.That(edges[i].f0>=0&&edges[i].f1>=0&&edges[i].v0<edges[i].v1,Is.True);bank.edges[r.edgeBase+i]=edges[i];}
        }
        IEnumerator RequestWithCallback(bool shutdown,bool completeEdges=true)
        {
            ColdWorld();var rig=ColdRig();var motion=rig.gameObject.AddComponent<Rigidbody>();motion.useGravity=false;
            motion.mass=12;motion.automaticCenterOfMass=motion.automaticInertiaTensor=false;motion.inertiaTensor=Vector3.one*4;
            var oldHit=rig.gameObject.AddComponent<BoxCollider>();var probe=rig.gameObject.AddComponent<D6TDisableProbe>();
            var source=NewAuthoredShape(1);coldWarm=new VpPhysicsColdPreparation();
            if(completeEdges)CompleteRequestBoxEdges(source);
            Assert.That(coldWorld.TryPrepareCharacterCut(rig,new[]{0,1,2,3},4,source.BankOf(0),new[]{source.Convex(0)},
                new[]{rig.transform},coldWarm,rig.gameObject,motion,out var handle),Is.True);coldHandles.Add(handle);
            var plane=new float4(1,0,0,-.25f);
            Assert.That(handle.TryCut(plane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Unavailable));
            Assert.That(oldHit.gameObject.activeInHierarchy,Is.True);yield return null;
            Assert.That(handle.TryFinishPreparation(),Is.True);
            int callbacks=0,withdrawFrame=-1;
            probe.Disabled=()=>
            {
                callbacks++;withdrawFrame=Time.frameCount;
                Assert.That(coldWorld.Owners.TryGet(handle.Source,out var owner),Is.True);
                Assert.That(owner.IsWithdrawn&&!owner.Shape.IsFreed,Is.True);
                Assert.That(handle.TryCut(plane,float3.zero).Outcome,Is.EqualTo(VpCharacterCutOutcome.Unavailable));
                handle.Dispose();Assert.That(handle.IsDisposed,Is.False);
                if(shutdown){Assert.That(coldWorld.Shutdown(),Is.False);Assert.That(coldWorld.IsReleased,Is.False);}
            };
            int frame=Time.frameCount;var result=handle.TryCut(plane,float3.zero);
            Assert.That(result.Outcome,Is.EqualTo(VpCharacterCutOutcome.Requested));Assert.That(result.Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(callbacks,Is.EqualTo(1));Assert.That(withdrawFrame,Is.EqualTo(frame));
            Assert.That(handle.IsDisposed,Is.True);Assert.That(rig!=null&&!rig.gameObject.activeSelf,Is.True);
            Assert.That(oldHit.gameObject.activeInHierarchy,Is.False);
            if(!shutdown)
            {
                var transaction=coldWorld.Driver.Transactions[0];
                Assert.That(transaction.PublishedFrame,Is.EqualTo(frame));
                for(int i=0;i<120&&transaction.Phase!=ProvisionalCutPhase.HandedOff&&transaction.Phase!=ProvisionalCutPhase.Recovered;i++)yield return null;
                Assert.That(transaction.Phase,Is.EqualTo(completeEdges?ProvisionalCutPhase.HandedOff:ProvisionalCutPhase.Recovered),
                    "cut="+transaction.CutOutcome+", request="+transaction.Cut?.Outcome+", kernel="+transaction.Cut?.KernelStatus+", failure="+transaction.Cut?.Failure);
                if(!completeEdges){Assert.That(transaction.CutOutcome,Is.EqualTo(PhysicsCutOutcomeKind.KernelFailed));Assert.That(rig.gameObject.activeSelf,Is.False);}
            }
            else Assert.That(coldWorld.IsEnding||coldWorld.IsReleased,Is.True);
            probe.Disabled=null;
        }
    }
}
