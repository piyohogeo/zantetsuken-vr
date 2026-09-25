using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe partial class ProvisionalCutDriverTests
    {
        VpPreparedPhysicsInput LeaseInput(World w)
        {
            var ranges = new ConvexBrepRange[w.harness.input.convexCount];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = w.harness.input.convexes[i];
            var input = new VpPreparedPhysicsInput(w.harness.input.bank, ranges);
            Assert.That(input.TryPose(new[] { float4x4.identity }, out _), Is.True);
            return input;
        }
        void RegisterLeaseInput(World w, VpPreparedPhysicsInput input)
        {
            w.registry.Retire(w.source); w.ledger.Retire(w.source);
            w.root = new GameObject("Prepared synthetic source"); _objects.Add(w.root);
            var body = w.root.AddComponent<Rigidbody>(); body.useGravity = false;
            body.automaticCenterOfMass = body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass; body.centerOfMass = Vector3.zero; body.inertiaTensor = Vector3.one * 4;
            w.shape = input.TakeShape(); w.source = w.ledger.AddFragment();
            w.registry.RegisterAuthored(w.source, w.root, body, w.shape, false, Matrix4x4.identity);
        }
        static float4 LeasePlane => new float4(0, 1, 0, 0);

        [Test] public void D6_Lease_ConsumesSameClassificationOnce_ThenTransactionOwnsIt()
        {
            using var w = NewWorld(); using var input = LeaseInput(w);
            Assert.That(w.driver.TryPrepareFreshCut(input, LeasePlane, ParentMass, out var lease), Is.True);
            using (lease)
            {
                var classification = lease.Classification;
                Assert.That(w.driver.AssessFreshCut(lease), Is.EqualTo(ProvisionalCutDriver.FreshCutEligibility.Ready));
                RegisterLeaseInput(w, input);
                Assert.That(w.driver.AssessFreshCut(lease), Is.EqualTo(ProvisionalCutDriver.FreshCutEligibility.Invalid));
                var ask = new ProvisionalCutAsk { source = w.source, plane = LeasePlane };
                Assert.That(w.driver.RequestPreparedCut(ask, lease, out var transaction, out _), Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(transaction.Classification, Is.SameAs(classification)); Assert.That(lease.Consumed, Is.True);
                lease.Dispose(); lease.Dispose(); Assert.That(classification.IsDisposed, Is.False);
                Assert.That(w.driver.RequestPreparedCut(ask, lease, out _, out _), Is.EqualTo(ProvisionalCutAcceptance.InvalidRequest));
                Assert.That(input.TryRearmAfterRefusal(), Is.False);
                Assert.That(w.driver.EndCut(transaction.Operation), Is.True);
                Assert.That(classification.IsDisposed, Is.True);
            }
        }

        [TestCase("driver")][TestCase("frame")][TestCase("rebind")][TestCase("plane")]
        [TestCase("mass")][TestCase("shape")][TestCase("disposed")][TestCase("null")]
        public void D6_Lease_MismatchCannotConsumeOrAdmit(string mismatch)
        {
            int frame = 101;
            using var w = NewWorld(frameSource: () => frame); using var input = LeaseInput(w);
            Assert.That(w.driver.TryPrepareFreshCut(input, LeasePlane, ParentMass, out var lease), Is.True);
            using (lease)
            {
                if (mismatch != "shape") RegisterLeaseInput(w, input);
                var caller = w.driver;
                if (mismatch == "driver") caller = NewWorld(frameSource: () => frame).driver;
                if (mismatch == "frame") frame++;
                if (mismatch == "rebind") w.driver.Bind(w.ledger,w.registry,w.cook,w.frame,null,SupportEpsilon,AnchorEpsilon,VertexLimit,()=>frame);
                if (mismatch == "mass") w.SourceOwner.Body.mass = 24;
                if (mismatch == "disposed") lease.Dispose();
                var ask = new ProvisionalCutAsk { source = w.source, plane = mismatch == "plane" ? new float4(1,0,0,0) : LeasePlane };
                long revision = w.ledger.Revision;
                Assert.That(caller.RequestPreparedCut(ask, mismatch == "null" ? null : lease, out var none, out var admission), Is.EqualTo(ProvisionalCutAcceptance.InvalidRequest));
                Assert.That(none, Is.Null); Assert.That(admission, Is.EqualTo(LogicalCutAdmission.NoOp));
                Assert.That(lease.Consumed, Is.False); Assert.That(w.ledger.Revision, Is.EqualTo(revision));
                Assert.That(w.ledger.OperationCount + w.driver.Transactions.Count + w.registry.ProvisionalPairCount, Is.Zero);
            }
        }

        [TestCase(false)][TestCase(true)]
        public void D6_Lease_EmptyPrecedesFull_RefusalRearmsWithoutMeshRebuild(bool empty)
        {
            using var w = NewWorld(); using var input = LeaseInput(w);
            var blocker = new LogicalCutLedger(w.ledger.Budget);
            for (int i = 0; i < 8; i++) Assert.That(blocker.Admit(blocker.AddFragment(),LeasePlane,true,out _), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(w.ledger.Budget.IsFull, Is.True);
            Assert.That(w.driver.TryPrepareFreshCut(input, empty ? new float4(0,1,0,10000) : LeasePlane, ParentMass, out var lease), Is.True);
            var classification = lease.Classification;
            using (lease)
            {
                Assert.That(w.driver.AssessFreshCut(lease), Is.EqualTo(empty ? ProvisionalCutDriver.FreshCutEligibility.EmptySide : ProvisionalCutDriver.FreshCutEligibility.Full));
                Assert.That(input.TryRearmAfterRefusal(), Is.False);
            }
            Assert.That(classification.IsDisposed, Is.True); Assert.That(input.TryRearmAfterRefusal(), Is.True);
            Assert.That(input.TryPose(new[] { new float4x4(quaternion.identity,new float3(2,0,0)) }, out _), Is.True);
            Assert.That(input.ColdCookCalls, Is.EqualTo(1)); Assert.That(w.ledger.OperationCount, Is.Zero);
        }

        [Test] public void D6_Lease_InputDisposalKeepsReadersButInvalidatesFreshAssessment()
        {
            using var w = NewWorld(); using var input = LeaseInput(w);
            input.TryBorrowPosedShape(out var shape); var mesh = shape.MeshOf(0);
            Assert.That(w.driver.TryPrepareFreshCut(input, LeasePlane, ParentMass, out var lease), Is.True);
            using (lease)
            {
                input.Dispose(); Assert.That(shape.IsFreed, Is.False); Assert.That(mesh != null, Is.True);
                Assert.That(w.driver.AssessFreshCut(lease), Is.EqualTo(ProvisionalCutDriver.FreshCutEligibility.Invalid));
                Assert.That(w.driver.TryPrepareFreshCut(input,LeasePlane,ParentMass,out _), Is.False);
            }
            Assert.That(shape.IsFreed, Is.True); Assert.That(mesh == null, Is.True);
        }

        [Test] public void D6_Lease_PublicationExceptionKeepsConsumedClassificationInTransaction()
        {
            using var w = NewWorld(); using var input = LeaseInput(w);
            Assert.That(w.driver.TryPrepareFreshCut(input,LeasePlane,ParentMass,out var lease), Is.True);
            using (lease)
            {
                var classification=lease.Classification; RegisterLeaseInput(w,input);
                ProvisionalPhysicsPublication.publishedHook=()=>throw new InvalidOperationException("D6 after publication");
                try
                {
                    var ask=new ProvisionalCutAsk{source=w.source,plane=LeasePlane};
                    Assert.Throws<InvalidOperationException>(()=>w.driver.RequestPreparedCut(ask,lease,out _,out _));
                }
                finally { ProvisionalPhysicsPublication.publishedHook=null; }
                Assert.That(lease.Consumed,Is.True);lease.Dispose();Assert.That(classification.IsDisposed,Is.False);
                Assert.That(w.registry.ProvisionalPairCount,Is.EqualTo(1));Assert.That(w.driver.Transactions.Count,Is.EqualTo(1));
                Assert.That(w.driver.EndCut(w.driver.Transactions[0].Operation),Is.True);
                Assert.That(classification.IsDisposed,Is.True);
            }
        }
    }
}
