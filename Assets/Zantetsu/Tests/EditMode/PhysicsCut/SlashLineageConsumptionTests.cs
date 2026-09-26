using System;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// What a Slash has consumed (DESIGN 19.1.9, T-036), against a ledger's own lineage: the fragment itself and every
    /// descendant of it are consumed for that Slash, whatever became of the hit; an unrelated fragment -- another root,
    /// or a sibling -- is not; another Slash has a set of its own; and an ended Slash keeps nothing.
    /// </summary>
    public class SlashLineageConsumptionTests
    {
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, 0f);

        private static void Cut(LogicalCutLedger ledger, LogicalFragmentId source, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            ledger.PrepareAnchorDistribution(cut, 0.01f, out _);
            Assert.That(ledger.Publish(cut, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
        }

        [Test]
        public void AConsumedFragment_AndEveryDescendant_AreNotPassedOnAgain_ByTheSameSlash()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            LogicalFragmentId box = ledger.AddFragment();
            var consumption = new SlashLineageConsumption(ledger, 4);

            Assert.That(consumption.TryConsume(1, box), Is.True, "the first real hit is passed on");
            Assert.That(consumption.TryConsume(1, box), Is.False, "and the same fragment again is not");

            // Its cut is published: both children are descendants of what Slash 1 consumed.
            Cut(ledger, box, out LogicalFragmentId upper, out LogicalFragmentId lower);
            Assert.That(consumption.IsConsumed(1, upper), Is.True);
            Assert.That(consumption.TryConsume(1, lower), Is.False, "a child of its own cut is not hit by the same Slash");

            // Two steps down, through a cut of another Slash: still a descendant.
            Assert.That(consumption.TryConsume(2, upper), Is.True, "another Slash hits the child");
            Cut(ledger, upper, out LogicalFragmentId grandchild, out _);
            Assert.That(consumption.IsConsumed(1, grandchild), Is.True, "Slash 1 reaches it through two origins");
            Assert.That(consumption.IsConsumed(2, grandchild), Is.True, "and Slash 2 through one");
            Assert.That(consumption.ConsumedCount(1), Is.EqualTo(1), "nothing but the direct hit is kept");
        }

        [Test]
        public void UnrelatedFragments_AreConsumedOneByOne_EvenInOneObject()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            LogicalFragmentId box = ledger.AddFragment();
            LogicalFragmentId other = ledger.AddFragment();
            Cut(ledger, box, out LogicalFragmentId upper, out LogicalFragmentId lower);
            var consumption = new SlashLineageConsumption(ledger, 4);

            // Siblings share an ancestor that nobody consumed, so each is its own hit.
            Assert.That(consumption.TryConsume(3, upper), Is.True);
            Assert.That(consumption.IsConsumed(3, lower), Is.False, "a sibling is not a descendant");
            Assert.That(consumption.TryConsume(3, lower), Is.True, "so it is hit in its turn");
            Assert.That(consumption.TryConsume(3, other), Is.True, "and another root as well");
            Assert.That(consumption.ConsumedCount(3), Is.EqualTo(3));
        }

        [Test]
        public void AnEndedSlash_KeepsNothing_AndASlotIsFoundForEachLiveOne()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            LogicalFragmentId box = ledger.AddFragment();
            var consumption = new SlashLineageConsumption(ledger, 2);
            Assert.That(consumption.TryConsume(1, box), Is.True);
            Assert.That(consumption.TryConsume(2, box), Is.True);
            Assert.That(consumption.LiveSets, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(
                () => consumption.TryConsume(3, box), "more live Slashes than slots is an inconsistency, not an input");

            consumption.KeepOnly(new long[] { 2 });
            Assert.That(consumption.LiveSets, Is.EqualTo(1), "Slash 1 ended and gave its set back");
            Assert.That(consumption.IsConsumed(1, box), Is.False);
            Assert.That(consumption.IsConsumed(2, box), Is.True, "the live one keeps its own");
            Assert.That(consumption.TryConsume(3, box), Is.True, "and the freed slot serves the next Slash");
        }
    }
}
