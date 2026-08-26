using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameTraceObserverTests
    {
        private static CaptureFrameTraceContext MakeContext()
        {
            return new CaptureFrameTraceContext(
                12345, 100, 200, 3, 55, 77, 99, 11, 22, 33, 44, 66);
        }

        private static CaptureFrameIdSequence MakeSequenceAt(long lastIssued)
        {
            ConstructorInfo ctor = typeof(CaptureFrameIdSequence).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameIdSequence)ctor.Invoke(new object[] { lastIssued });
        }

        private static void AssertCommonFields(TraceEvent e, TraceEventType eventType)
        {
            CaptureFrameTraceContext c = MakeContext();
            Assert.That(e.EventType, Is.EqualTo(eventType));
            Assert.That(e.Timestamp, Is.EqualTo(c.Timestamp));
            Assert.That(e.FrameId, Is.EqualTo(c.UnityFrameId));
            Assert.That(e.FixedStepId, Is.EqualTo(c.FixedStepId));
            Assert.That(e.ThreadId, Is.EqualTo(c.ThreadId));
            Assert.That(e.CaptureFrameId, Is.EqualTo(c.CaptureFrameId));
            Assert.That(e.OpenXRFrameId, Is.EqualTo(c.OpenXRFrameId));
            Assert.That(e.TestRunId, Is.EqualTo(c.TestRunId));
            Assert.That(e.SlashId, Is.EqualTo(c.SlashId));
            Assert.That(e.FrontEdgeId, Is.EqualTo(c.FrontEdgeId));
            Assert.That(e.ObjectId, Is.EqualTo(c.ObjectId));
            Assert.That(e.ObjectGeneration, Is.EqualTo(c.ObjectGeneration));
            Assert.That(e.TaskId, Is.EqualTo(c.TaskId));
            Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
        }

        [Test]
        public void Sequence_IssuesIncreasingIds()
        {
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();

            Assert.That(sequence.Next(), Is.EqualTo(1));
            Assert.That(sequence.Next(), Is.EqualTo(2));
            Assert.That(sequence.Next(), Is.EqualTo(3));
            Assert.That(sequence.LastIssued, Is.EqualTo(3));
        }

        [Test]
        public void Sequence_InstancesAreIndependent()
        {
            CaptureFrameIdSequence first = new CaptureFrameIdSequence();
            CaptureFrameIdSequence second = new CaptureFrameIdSequence();

            Assert.That(first.Next(), Is.EqualTo(1));
            Assert.That(first.Next(), Is.EqualTo(2));
            Assert.That(second.Next(), Is.EqualTo(1));
        }

        [Test]
        public void Sequence_MaxValue_Overflows()
        {
            CaptureFrameIdSequence sequence = MakeSequenceAt(long.MaxValue - 1);

            Assert.That(sequence.Next(), Is.EqualTo(long.MaxValue));
            Assert.That(sequence.LastIssued, Is.EqualTo(long.MaxValue));
            Assert.Throws<OverflowException>(() => sequence.Next());
        }

        [Test]
        public void Context_IsValueTypeWithoutReferenceFields()
        {
            Type type = typeof(CaptureFrameTraceContext);

            Assert.That(type.IsValueType, Is.True);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Reference-type field: " + field.Name);
            }
        }

        [Test]
        public void Observer_AllEventTypesAndCommonFields()
        {
            CaptureFrameTraceContext context = MakeContext();
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordQueued(context);
                observer.RecordEncoded(context, 1.5, 100);
                observer.RecordDropped(context, 7);
                observer.RecordRingFrozen(context);

                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(4));
                AssertCommonFields(logger.GetHistoryEvent(0), TraceEventType.CaptureFrameQueued);
                AssertCommonFields(logger.GetHistoryEvent(1), TraceEventType.CaptureFrameEncoded);
                AssertCommonFields(logger.GetHistoryEvent(2), TraceEventType.CaptureFrameDropped);
                AssertCommonFields(logger.GetHistoryEvent(3), TraceEventType.CaptureRingFrozen);
            }
        }

        [Test]
        public void Observer_EncodedValues()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordEncoded(MakeContext(), 12.5, 3456);
                logger.Drain();

                TraceEvent e = logger.GetHistoryEvent(0);
                Assert.That(e.Value0, Is.EqualTo(12.5));
                Assert.That(e.Value1, Is.EqualTo(3456));
            }
        }

        [Test]
        public void Observer_DroppedReasonCode()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordDropped(MakeContext(), 9);
                logger.Drain();

                TraceEvent e = logger.GetHistoryEvent(0);
                Assert.That(e.Value0, Is.EqualTo(0));
                Assert.That(e.Value1, Is.EqualTo(9));
            }
        }

        [Test]
        public void Observer_QueuedAndRingFrozen_HaveZeroValues()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordQueued(MakeContext());
                observer.RecordRingFrozen(MakeContext());
                logger.Drain();

                Assert.That(logger.GetHistoryEvent(0).Value0, Is.EqualTo(0));
                Assert.That(logger.GetHistoryEvent(0).Value1, Is.EqualTo(0));
                Assert.That(logger.GetHistoryEvent(1).Value0, Is.EqualTo(0));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo(0));
            }
        }

        [Test]
        public void Observer_OneEventPerCall()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordQueued(MakeContext());
                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));

                observer.RecordEncoded(MakeContext(), 1, 1);
                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Observer_DoesNotAutoDrain()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordQueued(MakeContext());

                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Observer_InvalidEncodedArgs_NoEnqueue()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordEncoded(MakeContext(), -1, 10));
                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordEncoded(MakeContext(), double.NaN, 10));
                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordEncoded(MakeContext(), double.PositiveInfinity, 10));
                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordEncoded(MakeContext(), 1, -1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Observer_InvalidDroppedReasonCode_NoEnqueue()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordDropped(MakeContext(), 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordDropped(MakeContext(), -5));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Observer_DisposedLogger_Throws()
        {
            TraceLogger logger = new TraceLogger(4);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            logger.Dispose();

            Assert.Throws<ObjectDisposedException>(() => observer.RecordQueued(MakeContext()));
            Assert.Throws<ObjectDisposedException>(() => observer.RecordRingFrozen(MakeContext()));
        }

        [Test]
        public void Observer_DoesNotDisposeLogger()
        {
            TraceLogger logger = new TraceLogger(4);
            try
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordQueued(MakeContext());
                logger.Drain();

                Assert.That(logger.IsCreated, Is.True);
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
            finally
            {
                logger.Dispose();
            }
        }
    }
}
