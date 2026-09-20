using System;
using NUnit.Framework;
using Zantetsu.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The allocator that lets several cuts hold room in one array at once: spans are disjoint while they are held,
    /// come back whole when given up, and merge with their neighbours so that taking and giving back over and over
    /// leaves the allocator where it started.
    /// </summary>
    public class VpSpanAllocatorTests
    {
        [Test]
        public void ANewAllocator_HasItsWholeCapacityFreeInOnePiece()
        {
            var spans = new VpSpanAllocator(16);

            Assert.That(spans.Capacity, Is.EqualTo(16));
            Assert.That(spans.Used, Is.Zero);
            Assert.That(spans.FreeSpanCount, Is.EqualTo(1));
            Assert.That(spans.LargestFreeSpan, Is.EqualTo(16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpSpanAllocator(-1));
        }

        [Test]
        public void SpansTakenTogether_DoNotOverlap()
        {
            var spans = new VpSpanAllocator(16);

            Assert.That(spans.TryTake(5, out int first), Is.True);
            Assert.That(spans.TryTake(5, out int second), Is.True);
            Assert.That(spans.TryTake(5, out int third), Is.True);

            Assert.That(first, Is.EqualTo(0));
            Assert.That(second, Is.EqualTo(5));
            Assert.That(third, Is.EqualTo(10));
            Assert.That(spans.Used, Is.EqualTo(15));
            Assert.That(spans.LargestFreeSpan, Is.EqualTo(1), "one slot is left over");
            Assert.That(spans.TryTake(2, out _), Is.False, "and a span that does not fit is refused");
            Assert.That(spans.Used, Is.EqualTo(15), "a refusal changes nothing");
        }

        [Test]
        public void AnEmptySpan_TakesNothingAndGivesBackNothing()
        {
            var spans = new VpSpanAllocator(4);

            Assert.That(spans.TryTake(0, out int start), Is.True);
            Assert.That(start, Is.Zero);
            Assert.That(spans.Used, Is.Zero);
            Assert.DoesNotThrow(() => spans.GiveBack(0, 0));
            Assert.That(spans.Used, Is.Zero);
            Assert.That(spans.TryTake(-1, out _), Is.False, "a negative span is not a span");
        }

        /// <summary>
        /// The middle one of three given back, then its neighbours: the free room ends as one piece, which is what
        /// keeps the allocator from wearing away over many cuts.
        /// </summary>
        [Test]
        public void SpansGivenBack_MergeWithTheirNeighbours()
        {
            var spans = new VpSpanAllocator(12);
            spans.TryTake(4, out int a);
            spans.TryTake(4, out int b);
            spans.TryTake(4, out int c);
            Assert.That(spans.FreeSpanCount, Is.Zero, "nothing is free");

            spans.GiveBack(b, 4);
            Assert.That(spans.FreeSpanCount, Is.EqualTo(1), "a hole in the middle");
            Assert.That(spans.LargestFreeSpan, Is.EqualTo(4));

            spans.GiveBack(a, 4);
            Assert.That(spans.FreeSpanCount, Is.EqualTo(1), "and it joins the one before it");
            Assert.That(spans.LargestFreeSpan, Is.EqualTo(8));

            spans.GiveBack(c, 4);
            Assert.That(spans.FreeSpanCount, Is.EqualTo(1), "and the one after");
            Assert.That(spans.Used, Is.Zero);
            Assert.That(spans.LargestFreeSpan, Is.EqualTo(12), "the whole capacity, in one piece again");
        }

        /// <summary>
        /// Taking and giving back over and over, in orders that leave holes: the allocator ends exactly as it began,
        /// so repeated cancels and retries cost no capacity at all.
        /// </summary>
        [Test]
        public void TakingAndGivingBackRepeatedly_LosesNoCapacity()
        {
            var spans = new VpSpanAllocator(64);

            for (int round = 0; round < 50; round++)
            {
                Assert.That(spans.TryTake(7, out int a), Is.True, "round " + round);
                Assert.That(spans.TryTake(11, out int b), Is.True, "round " + round);
                Assert.That(spans.TryTake(3, out int c), Is.True, "round " + round);

                // Given back in an order that would leave holes if they did not merge.
                spans.GiveBack(b, 11);
                spans.GiveBack(a, 7);
                spans.GiveBack(c, 3);

                Assert.That(spans.Used, Is.Zero, "round " + round + ": nothing is held");
                Assert.That(spans.FreeSpanCount, Is.EqualTo(1), "round " + round + ": and the room is in one piece");
                Assert.That(spans.LargestFreeSpan, Is.EqualTo(64), "round " + round + ": all of it");
            }
        }

        /// <summary>
        /// A span committed in part: the used head stays taken and the tail comes back, next to whatever was free
        /// after it.
        /// </summary>
        [Test]
        public void AnUnusedTailGivenBack_JoinsTheFreeRoomAfterIt()
        {
            var spans = new VpSpanAllocator(20);
            spans.TryTake(12, out int start);

            spans.GiveBack(start + 4, 8);

            Assert.That(spans.Used, Is.EqualTo(4), "only the part that was used is still taken");
            Assert.That(spans.FreeSpanCount, Is.EqualTo(1), "and the tail joined the room after it");
            Assert.That(spans.LargestFreeSpan, Is.EqualTo(16));
            Assert.That(spans.TryTake(16, out int next), Is.True, "which can be taken whole");
            Assert.That(next, Is.EqualTo(4));
        }

        [Test]
        public void GivingBackWhatIsNotHeld_Throws()
        {
            var spans = new VpSpanAllocator(8);
            spans.TryTake(4, out int start);

            Assert.Throws<ArgumentOutOfRangeException>(() => spans.GiveBack(-1, 2), "before the start");
            Assert.Throws<ArgumentOutOfRangeException>(() => spans.GiveBack(6, 4), "past the end");
            Assert.Throws<ArgumentOutOfRangeException>(() => spans.GiveBack(0, -2), "a negative span");

            spans.GiveBack(start, 4);
            Assert.Throws<InvalidOperationException>(() => spans.GiveBack(start, 4), "twice over");
            Assert.That(spans.Used, Is.Zero, "and a refusal leaves the allocator as it was");
        }
    }
}
