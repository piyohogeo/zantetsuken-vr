using System;
using NUnit.Framework;
using Zantetsu.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The ranges an owner records as it writes (DESIGN 5.6): kept in order, joined when they overlap, touch or lie
    /// within the gap of one another, and otherwise kept apart -- two writes far from each other are two ranges, not one
    /// range with everything between them. Past the room for them the two closest are joined.
    /// </summary>
    public class VpChangedRangesTests
    {
        private static int[] Flat(VpChangedRanges ranges)
        {
            var flat = new int[ranges.Count * 2];
            for (int i = 0; i < ranges.Count; i++)
            {
                flat[i * 2] = ranges.StartAt(i);
                flat[(i * 2) + 1] = ranges.EndAt(i);
            }

            return flat;
        }

        [Test]
        public void WritesFarApart_StayApart_InOrder_WhateverOrderTheyWereMadeIn()
        {
            var ranges = new VpChangedRanges(8, 4);
            Assert.That(ranges.IsEmpty, Is.True);
            ranges.Add(100, 103);
            ranges.Add(10, 12);
            ranges.Add(50, 51);
            ranges.Add(7, 7);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 10, 12, 50, 51, 100, 103 }), "three ranges, in order; the empty one records nothing");
            Assert.That(ranges.Elements, Is.EqualTo(6), "what was written and nothing between");
            Assert.That(ranges.IsEmpty, Is.False);
            ranges.Clear();
            Assert.That(new[] { ranges.Count, (int)ranges.Elements }, Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void WritesThatOverlapTouchOrLieWithinTheGap_AreOneRange()
        {
            var ranges = new VpChangedRanges(8, 4);
            ranges.Add(10, 20);
            ranges.Add(15, 25);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 10, 25 }), "overlapping");
            ranges.Add(25, 30);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 10, 30 }), "touching");
            ranges.Add(34, 36);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 10, 36 }), "four elements between: within the gap");
            ranges.Add(41, 42);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 10, 36, 41, 42 }), "five between: apart");
            ranges.Add(2, 5);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 2, 5, 10, 36, 41, 42 }), "five before: apart");

            // One write that reaches several kept ranges joins them all.
            ranges.Add(6, 40);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 2, 42 }), "one range over the three it reached");
            ranges.Add(12, 14);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 2, 42 }), "a write inside a kept range changes nothing");
        }

        [Test]
        public void WithNoGap_OnlyOverlappingAndTouchingWritesJoin()
        {
            var ranges = new VpChangedRanges(8, 0);
            ranges.Add(0, 2);
            ranges.Add(3, 4);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 0, 2, 3, 4 }), "one element between: apart");
            ranges.Add(2, 3);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 0, 4 }), "the one between written: one range");
        }

        [Test]
        public void PastTheRoomForThem_TheTwoClosestAreJoined_AndNothingWrittenIsLost()
        {
            var ranges = new VpChangedRanges(3, 0);
            ranges.Add(0, 1);
            ranges.Add(100, 101);
            ranges.Add(200, 201);
            Assert.That(ranges.Count, Is.EqualTo(3));
            ranges.Add(110, 111);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 0, 1, 100, 111, 200, 201 }), "the fourth joins its nearest neighbour; the far ones stay apart");
            ranges.Add(40, 41);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 0, 41, 100, 111, 200, 201 }), "and again the two with the fewest between them: 39, not 59 or 89");
        }

        [Test]
        public void TheRangesOfAnother_AreRecordedAsWell_AndBadRangesAreRefused()
        {
            var ranges = new VpChangedRanges(8, 2);
            var other = new VpChangedRanges(8, 2);
            ranges.Add(10, 12);
            other.Add(13, 15);
            other.Add(40, 44);
            ranges.AddAll(other);
            Assert.That(Flat(ranges), Is.EqualTo(new[] { 10, 15, 40, 44 }), "joined where they are close, apart where they are not");
            Assert.That(Flat(other), Is.EqualTo(new[] { 13, 15, 40, 44 }), "the other is only read");
            Assert.Throws<ArgumentNullException>(() => ranges.AddAll(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ranges.Add(-1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => ranges.Add(5, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => ranges.StartAt(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpChangedRanges(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpChangedRanges(4, -1));
        }
    }
}
