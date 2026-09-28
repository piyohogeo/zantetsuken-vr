using System.Linq;
using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// A crowd's fixed set of prepared slots (<see cref="MobPlanSlotPool{TSlot}"/>), with fake slots: the free ones in the
    /// slots' own order, nothing taken when none is free (nothing made, nothing waited for), a returning slot prepared
    /// again only once it is let go and one a call, and a slot that fails set aside.
    /// </summary>
    public class MobPlanSlotPoolTests
    {
        private sealed class Slot : IMobPlanSlot
        {
            public string Name;
            public bool IsPrepared { get; set; }
            public string Failure { get; set; }
            public bool IsReturnReady { get; set; }
            public bool ReprepareSucceeds = true;
            public int Reprepares;

            public bool TryReprepare()
            {
                Reprepares++;
                IsReturnReady = false;
                IsPrepared = false;
                return ReprepareSucceeds;
            }

            public override string ToString() => Name;
        }

        private static Slot[] AddPrepared(MobPlanSlotPool<Slot> pool, int count)
        {
            var slots = new Slot[count];
            for (int i = 0; i < count; i++) pool.Add(slots[i] = new Slot { Name = "s" + i });
            for (int i = count - 1; i >= 0; i--)
            {
                slots[i].IsPrepared = true;
                pool.Advance(false);
            }

            return slots;
        }

        [Test]
        public void SlotsAreFreeOnlyOncePrepared_InTheirOwnOrder()
        {
            var pool = new MobPlanSlotPool<Slot>();
            var a = new Slot { Name = "a" };
            var b = new Slot { Name = "b" };
            pool.Add(a);
            pool.Add(b);
            pool.Advance(false);
            Assert.That(pool.FreeCount, Is.Zero, "nothing is free before it is prepared");
            Assert.That(pool.AllPrepared, Is.False);
            b.IsPrepared = true;
            pool.Advance(false);
            a.IsPrepared = true;
            pool.Advance(false);
            Assert.That(pool.AllPrepared, Is.True);
            Assert.That(pool.TryTake(out Slot first), Is.True);
            Assert.That(pool.TryTake(out Slot second), Is.True);
            Assert.That(first, Is.SameAs(a), "the slots' own order, not the order they finished in");
            Assert.That(second, Is.SameAs(b));
        }

        [Test]
        public void WithNoFreeSlot_NothingIsTakenOrMade_AndAReturnedSlotCanBeTakenOnceItIsPreparedAgain()
        {
            var pool = new MobPlanSlotPool<Slot>();
            Slot[] slots = AddPrepared(pool, 2);
            Assert.That(pool.TryTake(out Slot a), Is.True);
            Assert.That(pool.TryTake(out Slot b), Is.True);
            Assert.That(pool.TryTake(out Slot none), Is.False, "no free slot: nothing is taken");
            Assert.That(none, Is.Null);
            Assert.That(pool.Refused, Is.EqualTo(1));
            Assert.That(pool.SlotCount, Is.EqualTo(2), "and nothing is made");

            // a's individual retires; its slot comes back only once it is let go and prepared again.
            pool.Return(a);
            pool.Advance(true);
            Assert.That(a.Reprepares, Is.Zero, "not let go yet: not prepared again");
            Assert.That(pool.TryTake(out _), Is.False, "still nothing free");
            a.IsReturnReady = true;
            pool.Advance(true);
            Assert.That(a.Reprepares, Is.EqualTo(1));
            Assert.That(pool.TryTake(out _), Is.False, "being prepared again is not free yet");
            a.IsPrepared = true;
            pool.Advance(true);
            Assert.That(pool.TryTake(out Slot again), Is.True);
            Assert.That(again, Is.SameAs(a), "the returned slot, prepared again");
            Assert.That(pool.SlotCount, Is.EqualTo(2), "the slots stayed a fixed set");
            Assert.That(slots, Is.EquivalentTo(new[] { a, b }));
        }

        [Test]
        public void ASlotNotLetGo_IsNeverPreparedAgain_AndOnlyOneIsPreparedAgainPerCall()
        {
            var pool = new MobPlanSlotPool<Slot>();
            AddPrepared(pool, 3);
            Assert.That(pool.TryTake(out Slot held), Is.True);
            Assert.That(pool.TryTake(out Slot x), Is.True);
            Assert.That(pool.TryTake(out Slot y), Is.True);
            pool.Return(held);   // a held or pending cut still refers to it: never let go here
            pool.Return(x);
            pool.Return(y);
            x.IsReturnReady = true;
            y.IsReturnReady = true;
            pool.Advance(true);
            Assert.That(x.Reprepares + y.Reprepares, Is.EqualTo(1), "one prepared again per call");
            pool.Advance(true);
            Assert.That(x.Reprepares + y.Reprepares, Is.EqualTo(2));
            for (int i = 0; i < 10; i++) pool.Advance(true);
            Assert.That(held.Reprepares, Is.Zero, "the slot not let go was never prepared again");
            Assert.That(pool.ReturningCount, Is.EqualTo(1), "and still waits");
            Assert.That(pool.Reprepared, Is.EqualTo(2));
        }

        [Test]
        public void ASlotPreparedAgain_JoinsTheEndOfTheFreeOnes_SoTheSlotsBehindAreTakenFirst()
        {
            var pool = new MobPlanSlotPool<Slot>();
            Slot[] s = AddPrepared(pool, 5);
            // Two are live; three wait behind them, in the slots' own order.
            Assert.That(pool.TryTake(out Slot first), Is.True);
            Assert.That(pool.TryTake(out Slot second), Is.True);
            Assert.That(first, Is.SameAs(s[0]));
            Assert.That(second, Is.SameAs(s[1]));

            // The front slots come back again and again; each one prepared again joins the end.
            var taken = new System.Collections.Generic.List<Slot>();
            Slot live = first;
            for (int round = 0; round < 6; round++)
            {
                pool.Return(live);
                live.IsReturnReady = true;
                pool.Advance(true);
                live.IsPrepared = true;
                pool.Advance(true);
                Assert.That(pool.TryTake(out live), Is.True);
                taken.Add(live);
            }

            Assert.That(taken.Take(3), Is.EqualTo(new[] { s[2], s[3], s[4] }), "the slots that waited are taken before the one that just came back");
            Assert.That(taken.Skip(3), Is.EqualTo(new[] { s[0], s[2], s[3] }), "and the returned ones follow in the order they came back");
            Assert.That(pool.FreeCount, Is.EqualTo(3), "s4, s0, s2 wait; s1 and s3 are live");
            Assert.That(pool.SlotCount, Is.EqualTo(5), "no slot is made");
        }

        [Test]
        public void ASlotThatCannotBePreparedAgain_IsSetAside_NotFree()
        {
            var pool = new MobPlanSlotPool<Slot>();
            AddPrepared(pool, 1);
            Assert.That(pool.TryTake(out Slot a), Is.True);
            a.ReprepareSucceeds = false;
            pool.Return(a);
            a.IsReturnReady = true;
            pool.Advance(true);
            Assert.That(pool.BrokenCount, Is.EqualTo(1));
            Assert.That(pool.TryTake(out _), Is.False);
        }
    }
}
