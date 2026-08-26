using System;
using NUnit.Framework;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceRingBufferTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent
            {
                Timestamp = tag,
                EventType = TraceEventType.None,
            };
        }

        [Test]
        public void Constructor_RejectsNonPositiveCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceRingBuffer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceRingBuffer(-1));
        }

        [Test]
        public void Write_BelowCapacity_PreservesOrder()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(4);
            buffer.Write(Event(1));
            buffer.Write(Event(2));
            buffer.Write(Event(3));

            Assert.That(buffer.Capacity, Is.EqualTo(4));
            Assert.That(buffer.Count, Is.EqualTo(3));
            Assert.That(buffer.TotalWritten, Is.EqualTo(3));
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(0));
            Assert.That(buffer[0].Timestamp, Is.EqualTo(1));
            Assert.That(buffer[1].Timestamp, Is.EqualTo(2));
            Assert.That(buffer[2].Timestamp, Is.EqualTo(3));
        }

        [Test]
        public void Write_AtCapacity_OverwritesOldest()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            buffer.Write(Event(1));
            buffer.Write(Event(2));
            buffer.Write(Event(3));
            buffer.Write(Event(4));

            Assert.That(buffer.Count, Is.EqualTo(3));
            Assert.That(buffer.TotalWritten, Is.EqualTo(4));
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(1));
            Assert.That(buffer[0].Timestamp, Is.EqualTo(2));
            Assert.That(buffer[1].Timestamp, Is.EqualTo(3));
            Assert.That(buffer[2].Timestamp, Is.EqualTo(4));
        }

        [Test]
        public void Write_MultipleWraps_PreservesOldestFirstOrder()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            for (int i = 1; i <= 5; i++)
            {
                buffer.Write(Event(i));
            }

            Assert.That(buffer.Count, Is.EqualTo(3));
            Assert.That(buffer.TotalWritten, Is.EqualTo(5));
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(2));
            Assert.That(buffer[0].Timestamp, Is.EqualTo(3));
            Assert.That(buffer[1].Timestamp, Is.EqualTo(4));
            Assert.That(buffer[2].Timestamp, Is.EqualTo(5));
        }

        [Test]
        public void CopyTo_CopiesOldestFirstWithOffset()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            buffer.Write(Event(1));
            buffer.Write(Event(2));
            buffer.Write(Event(3));
            buffer.Write(Event(4)); // stored oldest-first: 2, 3, 4

            TraceEvent[] destination = new TraceEvent[5];
            buffer.CopyTo(destination, 2);

            Assert.That(destination[2].Timestamp, Is.EqualTo(2));
            Assert.That(destination[3].Timestamp, Is.EqualTo(3));
            Assert.That(destination[4].Timestamp, Is.EqualTo(4));
        }

        [Test]
        public void CopyTo_ThrowsWhenDestinationTooSmall()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            buffer.Write(Event(1));
            buffer.Write(Event(2));
            buffer.Write(Event(3));

            Assert.Throws<ArgumentException>(() => buffer.CopyTo(new TraceEvent[2], 0));
        }

        [Test]
        public void CopyTo_ThrowsOnNegativeOffset()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            buffer.Write(Event(1));

            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CopyTo(new TraceEvent[3], -1));
        }

        [Test]
        public void Indexer_ThrowsOutOfRange()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            buffer.Write(Event(1));

            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = buffer[-1]; });
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = buffer[1]; });
        }

        [Test]
        public void Clear_ResetsContentsAndCounters()
        {
            TraceRingBuffer buffer = new TraceRingBuffer(3);
            buffer.Write(Event(1));
            buffer.Write(Event(2));
            buffer.Write(Event(3));
            buffer.Write(Event(4));

            buffer.Clear();

            Assert.That(buffer.Count, Is.EqualTo(0));
            Assert.That(buffer.TotalWritten, Is.EqualTo(0));
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(0));
            Assert.That(buffer.Capacity, Is.EqualTo(3));

            buffer.Write(Event(10));
            Assert.That(buffer.Count, Is.EqualTo(1));
            Assert.That(buffer.TotalWritten, Is.EqualTo(1));
            Assert.That(buffer[0].Timestamp, Is.EqualTo(10));
        }

        [Test]
        public void Instances_DoNotShareState()
        {
            TraceRingBuffer first = new TraceRingBuffer(2);
            TraceRingBuffer second = new TraceRingBuffer(2);

            first.Write(Event(1));

            Assert.That(first.Count, Is.EqualTo(1));
            Assert.That(second.Count, Is.EqualTo(0));
            Assert.That(second.TotalWritten, Is.EqualTo(0));
        }
    }
}
