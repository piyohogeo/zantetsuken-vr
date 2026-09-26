using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

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

        // Another character's cut holds a geometry reservation of the same storage; this character's first cut does
        // not wait for it. Both are released by the ordinary collection, and the world ends as usual.
        [UnityTest] public IEnumerator D6T_SecondCharacterCutsWhileFirstGeometryReservationIsHeld()
        {
            ColdWorld(destination=>destination!=WorkDestination.GeometryPool?null
                :coldGeometryHold=new CutWorldRootPlayModeTests.HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)){HoldEverything=true});
            Assert.That(coldGeometryHold,Is.Not.Null,"the geometry destination is this case's to hold");
            coldWarm=new VpPhysicsColdPreparation();
            var a=PrepareCharacter(Vector3.zero,out var aHit);var b=PrepareCharacter(new Vector3(3,0,0),out var bHit);
            yield return null;
            Assert.That(a.TryFinishPreparation()&&b.TryFinishPreparation(),Is.True);

            var aResult=a.TryCut(new float4(1,0,0,-.25f),float3.zero);
            Assert.That(aResult.Outcome,Is.EqualTo(VpCharacterCutOutcome.Requested));
            Assert.That(aResult.Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            var aCut=Transaction(aResult.Operation);
            for(int i=0;i<120&&!(coldGeometryHold.HoldingCount>0&&coldWorld.Geometry.StageOf(aCut.Operation)==CutGeometryStage.Running);i++)
                yield return null;
            var aGeometry=coldWorld.Geometry.RequestOf(aCut.Operation);
            Assert.That(aGeometry,Is.Not.Null);
            Assert.That(aGeometry.HoldsReservation,Is.True,"A's geometry request holds its reservation");
            yield return null;
            Assert.That(aGeometry.HoldsReservation,Is.True,"still held on a later frame");

            // B is asked on its own frame, not from A's call or callback.
            int frame=Time.frameCount,vertices=coldWorld.Storage.VertexCount,transfers=coldWorld.Display.VertexTransfers;
            var bResult=b.TryCut(new float4(1,0,0,-.25f),float3.zero); // in B's own local frame (DESIGN 5.2)
            Assert.That(bResult.Outcome,Is.EqualTo(VpCharacterCutOutcome.Requested),"B is not refused for A's reservation");
            Assert.That(bResult.Acceptance,Is.EqualTo(ProvisionalCutAcceptance.Published));
            var bCut=Transaction(bResult.Operation);
            Assert.That(bCut.Source,Is.Not.EqualTo(aCut.Source));
            Assert.That(bCut.PublishedFrame,Is.EqualTo(frame),"B is published in its request frame");
            Assert.That(bHit.gameObject.activeInHierarchy,Is.False,"B's old hit is withdrawn in that frame");
            Assert.That(coldWorld.Display.TryGetShownPlacement(bCut.Source,out _),Is.True,"B is a draw target in that frame");
            Assert.That(coldWorld.Display.VertexTransfers,Is.GreaterThan(transfers));
            Assert.That(aGeometry.HoldsReservation,Is.True,"A's reservation was not touched");
            Assert.That(coldWorld.Geometry.TryGetGeometry(bCut.Source,out var bBase),Is.True);
            Assert.That(bBase.vertexStart,Is.GreaterThanOrEqualTo(vertices),"B's range is new room, published past the high-water");

            // B's geometry is collected and committed while A's work stays held, with its reservation.
            coldGeometryHold.LetNewWorkThrough=true;
            for(int i=0;i<240&&coldWorld.Geometry.StageOf(bCut.Operation)!=CutGeometryStage.Committed;i++)
            {
                yield return null;
                Assert.That(aGeometry.HoldsReservation,Is.True,"A keeps its reservation while B goes on");
                Assert.That(coldGeometryHold.HoldingCount,Is.EqualTo(1),"only A's work is held back");
            }
            Assert.That(coldWorld.Geometry.StageOf(bCut.Operation),Is.EqualTo(CutGeometryStage.Committed),"B's geometry committed while A is held");
            Assert.That(coldWorld.Geometry.StageOf(aCut.Operation),Is.EqualTo(CutGeometryStage.Running),"A has not moved");
            Assert.That(aGeometry.HoldsReservation,Is.True);
            Assert.That(coldWorld.Ledger.TryGetOperation(bCut.Operation,out var bOperation),Is.True);
            var bSides=new[]{bOperation.positive,bOperation.negative};
            var bCommitted=new string[2];
            for(int s=0;s<2;s++)bCommitted[s]=CommittedSide(bSides[s]);

            // Then A is let go: it commits, and B's committed sides are as they were.
            coldGeometryHold.ReleaseEverything();
            for(int i=0;i<240&&coldWorld.Geometry.StageOf(aCut.Operation)!=CutGeometryStage.Committed;i++)yield return null;
            Assert.That(coldWorld.Geometry.StageOf(aCut.Operation),Is.EqualTo(CutGeometryStage.Committed),"A commits after B");
            for(int s=0;s<2;s++)Assert.That(CommittedSide(bSides[s]),Is.EqualTo(bCommitted[s]),"B's committed side "+s+" after A's commit");
            for(int i=0;i<240&&!(Settled(aCut)&&Settled(bCut));i++)yield return null;
            Assert.That(aCut.Phase,Is.EqualTo(ProvisionalCutPhase.HandedOff),"A: cut="+aCut.CutOutcome);
            Assert.That(bCut.Phase,Is.EqualTo(ProvisionalCutPhase.HandedOff),"B: cut="+bCut.CutOutcome);
            Assert.That(aGeometry.HoldsReservation,Is.False);
            Assert.That(aHit.gameObject.activeInHierarchy||bHit.gameObject.activeInHierarchy,Is.False);
            for(int i=0;i<120&&!coldWorld.Shutdown();i++)yield return null;
            Assert.That(coldWorld.IsReleased,Is.True,"the world ends as usual");
        }
        VpPreparedCharacterCut PrepareCharacter(Vector3 at,out BoxCollider oldHit)
        {
            var rig=ColdRig();rig.transform.position=at;var motion=rig.gameObject.AddComponent<Rigidbody>();motion.useGravity=false;
            motion.mass=12;motion.automaticCenterOfMass=motion.automaticInertiaTensor=false;motion.inertiaTensor=Vector3.one*4;
            oldHit=rig.gameObject.AddComponent<BoxCollider>();
            // The fixture keeps one bank in its fields; an earlier one is kept alive and freed with the rest.
            if(_vertices.IsCreated)_disposables.Insert(0,new EarlierBank{arrays=new IDisposable[]{_vertices,_faceOffsets,_faceIndices,_faceEdges,_edges}});
            var source=NewAuthoredShape(1);CompleteRequestBoxEdges(source);
            Assert.That(coldWorld.TryPrepareCharacterCut(rig,new[]{0,1,2,3},4,source.BankOf(0),new[]{source.Convex(0)},
                new[]{rig.transform},coldWarm,rig.gameObject,motion,out var handle),Is.True);
            coldHandles.Add(handle);return handle;
        }
        ProvisionalCutTransaction Transaction(CutOperationId operation)
        {
            foreach(var t in coldWorld.Driver.Transactions)if(t.Operation==operation)return t;
            Assert.Fail("no transaction for the operation");return null;
        }
        sealed class EarlierBank:IDisposable
        {
            public IDisposable[] arrays;
            public void Dispose(){foreach(var array in arrays)array.Dispose();arrays=Array.Empty<IDisposable>();}
        }
        static bool Settled(ProvisionalCutTransaction t)=>t.Phase==ProvisionalCutPhase.HandedOff||t.Phase==ProvisionalCutPhase.Recovered;
        // One committed side as it lies in the storage: its vertices and its published indices.
        string CommittedSide(LogicalFragmentId side)
        {
            Assert.That(coldWorld.Geometry.TryGetGeometry(side,out var geometry),Is.True);
            Assert.That(coldWorld.Storage.TryGetCommittedVertices(geometry.vertexStart,geometry.vertexCount,out var range),Is.True);
            var text=new System.Text.StringBuilder();
            foreach(var v in range)text.Append(v.position.ToString("G9")).Append(v.normal.ToString("G9")).Append(v.uv0.ToString("G9"));
            Assert.That(coldWorld.Storage.TryAcquireIndexReadLease(geometry.indexRange,out var lease,out var view),Is.True);
            try{foreach(var index in view)text.Append(',').Append(index);}
            finally{Assert.That(coldWorld.Storage.TryReleaseIndexReadLease(lease),Is.True);}
            return geometry.vertexStart+":"+geometry.vertexCount+"/"+text;
        }

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
