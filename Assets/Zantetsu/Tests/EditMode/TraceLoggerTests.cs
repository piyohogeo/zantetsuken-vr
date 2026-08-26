using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceLoggerTests
    {
        private struct WriteEventsJob : IJobParallelFor
        {
            public NativeQueue<TraceEvent>.ParallelWriter Writer;

            public void Execute(int index)
            {
                Writer.Enqueue(new TraceEvent { Timestamp = index + 1 });
            }
        }

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        [Test]
        public void Constructor_RejectsNonPositiveCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceLogger(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceLogger(-1));
        }

        [Test]
        public void Enqueue_DoesNotAppearInHistoryBeforeDrain()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                logger.Enqueue(Event(1));

                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));
            }
        }

        [Test]
        public void Drain_MovesEventsToHistoryInOrder()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Enqueue(Event(3));

                int drained = logger.Drain();

                Assert.That(drained, Is.EqualTo(3));
                Assert.That(logger.HistoryCount, Is.EqualTo(3));
                Assert.That(logger.TotalWritten, Is.EqualTo(3));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(1).Timestamp, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(2).Timestamp, Is.EqualTo(3));
            }
        }

        [Test]
        public void Drain_EmptyQueue_ReturnsZero()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                Assert.That(logger.Drain(), Is.EqualTo(0));
            }
        }

        [Test]
        public void CapacityOverflow_PreservesRingBufferContract()
        {
            using (TraceLogger logger = new TraceLogger(3))
            {
                for (int i = 1; i <= 5; i++)
                {
                    logger.Enqueue(Event(i));
                }

                int drained = logger.Drain();

                Assert.That(drained, Is.EqualTo(5));
                Assert.That(logger.HistoryCount, Is.EqualTo(3));
                Assert.That(logger.TotalWritten, Is.EqualTo(5));
                Assert.That(logger.OverwrittenCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(3));
                Assert.That(logger.GetHistoryEvent(1).Timestamp, Is.EqualTo(4));
                Assert.That(logger.GetHistoryEvent(2).Timestamp, Is.EqualTo(5));
            }
        }

        [Test]
        public void ClearHistory_ResetsOnlyHistory()
        {
            using (TraceLogger logger = new TraceLogger(3))
            {
                logger.Enqueue(Event(1));
                logger.Drain();

                logger.ClearHistory();

                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));
                Assert.That(logger.OverwrittenCount, Is.EqualTo(0));
                Assert.That(logger.IsCreated, Is.True);

                logger.Enqueue(Event(2));
                Assert.That(logger.Drain(), Is.EqualTo(1));
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void MultipleLoggers_DoNotShareState()
        {
            using (TraceLogger first = new TraceLogger(4))
            using (TraceLogger second = new TraceLogger(4))
            {
                first.Enqueue(Event(1));
                first.Drain();

                Assert.That(first.HistoryCount, Is.EqualTo(1));
                Assert.That(second.HistoryCount, Is.EqualTo(0));
                Assert.That(second.TotalWritten, Is.EqualTo(0));
            }
        }

        [Test]
        public void ParallelWriter_FromJob_WritesAllEventsWithoutLoss()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                WriteEventsJob job = new WriteEventsJob { Writer = logger.JobWriter };
                job.Schedule(8, 1).Complete();

                int drained = logger.Drain();

                Assert.That(drained, Is.EqualTo(8));
                Assert.That(logger.HistoryCount, Is.EqualTo(8));
                Assert.That(logger.TotalWritten, Is.EqualTo(8));
                Assert.That(logger.OverwrittenCount, Is.EqualTo(0));

                bool[] seen = new bool[9];
                for (int i = 0; i < logger.HistoryCount; i++)
                {
                    long timestamp = logger.GetHistoryEvent(i).Timestamp;
                    Assert.That(timestamp, Is.GreaterThanOrEqualTo(1));
                    Assert.That(timestamp, Is.LessThanOrEqualTo(8));
                    seen[(int)timestamp] = true;
                }

                for (int i = 1; i <= 8; i++)
                {
                    Assert.That(seen[i], Is.True, "Missing event with timestamp " + i);
                }
            }
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            TraceLogger logger = new TraceLogger(4);
            logger.Dispose();
            Assert.That(logger.IsCreated, Is.False);
            logger.Dispose();
            Assert.That(logger.IsCreated, Is.False);
        }

        [Test]
        public void DisposedLogger_AllApisThrow()
        {
            TraceLogger logger = new TraceLogger(4);
            logger.Dispose();

            Assert.That(logger.IsCreated, Is.False);
            Assert.Throws<ObjectDisposedException>(() => { var _ = logger.JobWriter; });
            Assert.Throws<ObjectDisposedException>(() => logger.Enqueue(Event(1)));
            Assert.Throws<ObjectDisposedException>(() => logger.Drain());
            Assert.Throws<ObjectDisposedException>(() => logger.GetHistoryEvent(0));
            Assert.Throws<ObjectDisposedException>(() => logger.CopyHistoryTo(new TraceEvent[1], 0));
            Assert.Throws<ObjectDisposedException>(() => logger.ClearHistory());
            Assert.Throws<ObjectDisposedException>(() => { var _ = logger.HistoryCapacity; });
            Assert.Throws<ObjectDisposedException>(() => { var _ = logger.HistoryCount; });
            Assert.Throws<ObjectDisposedException>(() => { var _ = logger.TotalWritten; });
            Assert.Throws<ObjectDisposedException>(() => { var _ = logger.OverwrittenCount; });
        }
    }
}
