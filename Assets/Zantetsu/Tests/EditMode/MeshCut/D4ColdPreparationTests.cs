using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.PhysicsCut;

namespace Zantetsu.MeshCut.Tests
{
    public class D4ColdPreparationTests
    {
        static readonly float4 Plane = new float4(1, 0, 0, 0);
        internal static object Field(object value, string name) => value.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(value);
        internal static int Capacity(object owner, string name)
        {
            object collection = Field(owner, name);
            var property = collection.GetType().GetProperty("Capacity");
            return property != null ? (int)property.GetValue(collection)
                : (int)collection.GetType().GetMethod("EnsureCapacity").Invoke(collection, new object[] { 0 });
        }
        static object Record(object owner, string field, int index) => ((IList)Field(owner, field))[index];
        static void Canonical(LogicalCutLedger ledger, LogicalFragmentId id)
            => Assert.That(Field(Record(ledger, "_fragments", id.value - 1), "anchors"), Is.Null);
        static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId id)
        {
            Assert.That(ledger.Admit(id, Plane, true, out var operation), Is.EqualTo(LogicalCutAdmission.Admitted));
            return operation;
        }

        [TestCase(0)][TestCase(1)][TestCase(2)]
        public void D4_EmptyInput_PreparedNullLists_PublishRecutAndCachedEpsilon(int input)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(2));
            IReadOnlyList<float3> anchors = input == 0 ? null : input == 1 ? Array.Empty<float3>() : new List<float3>();
            var root = ledger.AddFragment(anchors); Canonical(ledger, root);
            var op = Admit(ledger, root);
            Assert.That(ledger.Publish(op, out _, out _), Is.EqualTo(LogicalCutResultOutcome.AnchorsNotPrepared));
            Assert.That(ledger.PrepareAnchorDistribution(op, 0, out var result), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.Ok));
            Assert.That(result.positiveCount + result.negativeCount, Is.Zero);
            var record = Record(ledger, "_operations", 0);
            Assert.That(Field(record, "preparedPositive"), Is.Null); Assert.That(Field(record, "preparedNegative"), Is.Null);
            long revision = ledger.Revision;
            Assert.That(ledger.PrepareAnchorDistribution(op, float.NaN, out var cached), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(cached, Is.EqualTo(result)); Assert.That(ledger.Revision, Is.EqualTo(revision));
            Assert.That(ledger.Publish(op, out var positive, out var negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Canonical(ledger, positive); Canonical(ledger, negative);
            Assert.That(ledger.IsFixedOwner(positive) || ledger.IsFixedOwner(negative), Is.False);
            var recut = Admit(ledger, positive);
            Assert.That(ledger.PrepareAnchorDistribution(recut, 0, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(recut, out var child, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Canonical(ledger, child);
        }

        [TestCase(-1f)][TestCase(float.NaN)][TestCase(float.PositiveInfinity)][TestCase(float.NegativeInfinity)]
        public void D4_EmptyDistribution_InvalidEpsilonDoesNotMutateOrAllocateLists(float epsilon)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(1)); var root = ledger.AddFragment();
            var op = Admit(ledger, root); long revision = ledger.Revision;
            Assert.That(ledger.PrepareAnchorDistribution(op, epsilon, out var result), Is.EqualTo(AnchorPreparationOutcome.DistributionRefused));
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.InvalidEpsilon));
            Assert.That(ledger.Revision, Is.EqualTo(revision)); Assert.That(ledger.IsCurrentTarget(root), Is.True);
            Assert.That(ledger.TryGetPreparedAnchorDistribution(op, out _), Is.False);
            Assert.That(Field(Record(ledger, "_operations", 0), "preparedPositive"), Is.Null);
            Assert.That(ledger.PrepareAnchorDistribution(op, 0, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
        }

        [TestCase(0)][TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)]
        public void D4_EmptyValidator_MatchesGeneralPlaneChecks(int kind)
        {
            float4 plane = kind == 0 ? float4.zero : kind == 1 ? new float4(float.NaN,0,0,0)
                : kind == 2 ? new float4(1,0,0,float.PositiveInfinity)
                : kind == 3 ? new float4(float.Epsilon,0,0,0) : new float4(float.MaxValue,0,0,0);
            bool general = FixedSupportAnchors.TryDistribute(null, plane, 0, new List<float3>(), new List<float3>(), out var expected);
            Assert.That(FixedSupportAnchors.TryValidatePlane(plane, 0, out var actual), Is.EqualTo(general));
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(general, Is.EqualTo(kind >= 3));
        }

        [TestCase(false)][TestCase(true)]
        public void D4_EmptyPrepared_AbortOrStalePreservesExistingRules(bool stale)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(1)); var root = ledger.AddFragment();
            var op = Admit(ledger, root); ledger.PrepareAnchorDistribution(op, 0, out _);
            if (stale) ledger.NoteOwnershipChanged(root);
            ledger.Abort(op);
            Assert.That(ledger.IsCurrentTarget(root), Is.EqualTo(stale));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
            long revision = ledger.Revision; ledger.Abort(op);
            Assert.That(ledger.PrepareAnchorDistribution(op,0,out _), Is.EqualTo(AnchorPreparationOutcome.OperationNotActive));
            Assert.That(ledger.Revision, Is.EqualTo(revision));
        }

        [Test] public void D4_Nonempty_CopyAndClassificationPreserved_EmptyChildCanonical()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(2));
            var input = new[] { new float3(2,0,0) }; var root = ledger.AddFragment(input); input[0] = new float3(-2,0,0);
            var op = Admit(ledger, root); ledger.PrepareAnchorDistribution(op,0,out var distribution);
            Assert.That(distribution.positiveCount, Is.EqualTo(1)); Assert.That(distribution.negativeCount, Is.Zero);
            ledger.Publish(op, out var positive, out var negative);
            Assert.That(ledger.IsFixedOwner(positive), Is.True); Canonical(ledger, negative);
            var recut = Admit(ledger, negative); ledger.PrepareAnchorDistribution(recut,0,out _);
            Assert.That(Field(Record(ledger,"_operations",1),"preparedPositive"), Is.Null);
        }

        [Test] public void D4_LedgerCapacity_TotalsNotReservations_TwoRootsAndTwoPublicationsNoGrowth()
        {
            var budget = new LogicalCutIncompleteBudget(1); var ledger = new LogicalCutLedger(budget);
            ledger.PrepareCapacity(6,2); ledger.PrepareCapacity(6,2); ledger.PrepareCapacity(1,0);
            Assert.That(Capacity(ledger,"_fragments"), Is.EqualTo(6)); Assert.That(Capacity(ledger,"_operations"), Is.EqualTo(2));
            Assert.That(ledger.FragmentCount + ledger.OperationCount + ledger.Revision + budget.IncompleteCutOperationCount, Is.Zero);
            var a = ledger.AddFragment(); var b = ledger.AddFragment(); Assert.That(a.value, Is.EqualTo(1)); Assert.That(b.value, Is.EqualTo(2));
            var op = Admit(ledger,a); long revision = ledger.Revision;
            ledger.PrepareCapacity(6,2); Assert.That(ledger.Revision, Is.EqualTo(revision));
            Assert.That(ledger.Admit(b,Plane,true,out var skipped), Is.Not.EqualTo(LogicalCutAdmission.Admitted)); Assert.That(skipped.IsSet, Is.False);
            ledger.PrepareAnchorDistribution(op,0,out _); ledger.Publish(op,out _,out _); ledger.CompleteGeometry(op);
            op = Admit(ledger,b); ledger.PrepareAnchorDistribution(op,0,out _); ledger.Publish(op,out _,out _);
            Assert.That(Capacity(ledger,"_fragments"), Is.EqualTo(6)); Assert.That(Capacity(ledger,"_operations"), Is.EqualTo(2));
            Assert.That(ledger.FragmentCount, Is.EqualTo(6)); Assert.That(ledger.OperationCount, Is.EqualTo(2));
        }

        [Test] public void D4_CapacityInvalidArguments_DoNotPartiallyGrow()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(1));
            Assert.Throws<ArgumentOutOfRangeException>(()=>ledger.PrepareCapacity(100,-1));
            Assert.Throws<ArgumentOutOfRangeException>(()=>ledger.PrepareCapacity(-1,100));
            Assert.That(Capacity(ledger,"_fragments") + Capacity(ledger,"_operations"), Is.Zero);
            using var registry = new PhysicsOwnerRegistry();
            Assert.Throws<ArgumentOutOfRangeException>(()=>registry.PrepareCapacity(100,-1));
            Assert.Throws<ArgumentOutOfRangeException>(()=>registry.PrepareCapacity(-1,100));
            Assert.Throws<OverflowException>(()=>registry.PrepareCapacity(100,int.MaxValue));
            Assert.That(Capacity(registry,"_owners"), Is.Zero);
        }

        [Test] public void D4_RegistryCapacity_TwoPairsMeanFourBodies_NoMapsOrIdsCreated()
        {
            using var registry = new PhysicsOwnerRegistry(); registry.PrepareCapacity(6,2);
            string[] names = {"_owners","_fragmentOfBody","_pairsByOperation","_pairsBySource","_pairOfBody"};
            int[] minima = {6,6,2,2,4}; var before = new int[5];
            for(int i=0;i<5;i++) { before[i]=Capacity(registry,names[i]); Assert.That(before[i],Is.GreaterThanOrEqualTo(minima[i])); }
            registry.PrepareCapacity(6,2); registry.PrepareCapacity(0,0);
            for(int i=0;i<5;i++) { Assert.That(Capacity(registry,names[i]),Is.EqualTo(before[i])); Assert.That(((IDictionary)Field(registry,names[i])).Count,Is.Zero); }
            Assert.That(registry.Count + registry.ProvisionalPairCount,Is.Zero);
        }
    }
}
