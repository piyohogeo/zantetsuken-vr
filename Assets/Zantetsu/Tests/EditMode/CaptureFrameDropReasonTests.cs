using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDropReasonTests
    {
        private static CaptureFrameTraceContext MakeContext(long testRunId = 99)
        {
            return new CaptureFrameTraceContext(
                12345, 100, 200, 3, 55, 77, testRunId, 11, 22, 33, 44, 66);
        }

        private static TraceLogger CreateCaptureLogger(int historyCapacity, long testRunId)
        {
            ConstructorInfo ctor = typeof(TraceLogger).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null, "Capture logger constructor not found.");
            return (TraceLogger)ctor.Invoke(new object[] { historyCapacity, testRunId });
        }

        private static int GetCount(TraceLogger logger, string name)
        {
            PropertyInfo prop = typeof(TraceLogger).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, name + " property not found.");
            return (int)prop.GetValue(logger);
        }

        private static NativeArray<int> GetGate(TraceLogger logger)
        {
            FieldInfo field = typeof(TraceLogger).GetField("_sealGate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_sealGate field not found.");
            return (NativeArray<int>)field.GetValue(logger);
        }

        private static void AssertRejected(CaptureFrameTraceObserver observer, CaptureFrameTraceContext context, CaptureFrameDropReason reason)
        {
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => observer.RecordDropped(context, reason));
            Assert.That(ex.ParamName, Is.EqualTo("reason"));
        }

        [Test]
        public void Enum_UnderlyingType_IsInt()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(CaptureFrameDropReason)), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void Enum_ExistingValues_0To4_Unchanged()
        {
            Assert.That((int)CaptureFrameDropReason.None, Is.EqualTo(0));
            Assert.That((int)CaptureFrameDropReason.RequestQueueFull, Is.EqualTo(1));
            Assert.That((int)CaptureFrameDropReason.ReadbackFailed, Is.EqualTo(2));
            Assert.That((int)CaptureFrameDropReason.EncodedPngQueueFull, Is.EqualTo(3));
            Assert.That((int)CaptureFrameDropReason.FrameRecordRegistryFull, Is.EqualTo(4));

            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 0), Is.EqualTo(nameof(CaptureFrameDropReason.None)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 1), Is.EqualTo(nameof(CaptureFrameDropReason.RequestQueueFull)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 2), Is.EqualTo(nameof(CaptureFrameDropReason.ReadbackFailed)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 3), Is.EqualTo(nameof(CaptureFrameDropReason.EncodedPngQueueFull)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 4), Is.EqualTo(nameof(CaptureFrameDropReason.FrameRecordRegistryFull)));
        }

        [Test]
        public void Enum_NewValues_5To9_MatchExactly()
        {
            Assert.That((int)CaptureFrameDropReason.FrameDraftRegistryFull, Is.EqualTo(5));
            Assert.That((int)CaptureFrameDropReason.PngEncodeFailed, Is.EqualTo(6));
            Assert.That((int)CaptureFrameDropReason.PngStagingStoreFull, Is.EqualTo(7));
            Assert.That((int)CaptureFrameDropReason.CaptureCancelled, Is.EqualTo(8));
            Assert.That((int)CaptureFrameDropReason.FreezeDrainTimeout, Is.EqualTo(9));

            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 5), Is.EqualTo(nameof(CaptureFrameDropReason.FrameDraftRegistryFull)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 6), Is.EqualTo(nameof(CaptureFrameDropReason.PngEncodeFailed)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 7), Is.EqualTo(nameof(CaptureFrameDropReason.PngStagingStoreFull)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 8), Is.EqualTo(nameof(CaptureFrameDropReason.CaptureCancelled)));
            Assert.That(Enum.GetName(typeof(CaptureFrameDropReason), 9), Is.EqualTo(nameof(CaptureFrameDropReason.FreezeDrainTimeout)));
        }

        [Test]
        public void Enum_HasNoAliasesOrGaps_0To9()
        {
            Type type = typeof(CaptureFrameDropReason);

            Array names = Enum.GetNames(type);
            Array values = Enum.GetValues(type);

            // Ten members (None + nine reasons), no gaps and no aliases.
            Assert.That(names.Length, Is.EqualTo(10));
            Assert.That(values.Length, Is.EqualTo(10));

            for (int i = 0; i <= 9; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 10), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        [Test]
        public void RecordDropped_AcceptsLegacyReasons1To4_RecordsValue1()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordDropped(MakeContext(), CaptureFrameDropReason.RequestQueueFull);
                observer.RecordDropped(MakeContext(), CaptureFrameDropReason.ReadbackFailed);
                observer.RecordDropped(MakeContext(), CaptureFrameDropReason.EncodedPngQueueFull);
                observer.RecordDropped(MakeContext(), CaptureFrameDropReason.FrameRecordRegistryFull);

                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(4));
                Assert.That(logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                Assert.That(logger.GetHistoryEvent(2).Value1, Is.EqualTo((int)CaptureFrameDropReason.EncodedPngQueueFull));
                Assert.That(logger.GetHistoryEvent(3).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));
            }
        }

        [Test]
        public void RecordDropped_RejectsNewReasons5To9_QueueUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                AssertRejected(observer, MakeContext(), CaptureFrameDropReason.FrameDraftRegistryFull);
                AssertRejected(observer, MakeContext(), CaptureFrameDropReason.PngEncodeFailed);
                AssertRejected(observer, MakeContext(), CaptureFrameDropReason.PngStagingStoreFull);
                AssertRejected(observer, MakeContext(), CaptureFrameDropReason.CaptureCancelled);
                AssertRejected(observer, MakeContext(), CaptureFrameDropReason.FreezeDrainTimeout);

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));
            }
        }

        [Test]
        public void RecordDropped_RejectsNoneNegativeTenMaxValue_QueueUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                AssertRejected(observer, MakeContext(), CaptureFrameDropReason.None);
                AssertRejected(observer, MakeContext(), (CaptureFrameDropReason)(-1));
                AssertRejected(observer, MakeContext(), (CaptureFrameDropReason)10);
                AssertRejected(observer, MakeContext(), (CaptureFrameDropReason)int.MaxValue);

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));
            }
        }

        [Test]
        public void RecordDropped_RejectionsOnCaptureLogger_DoNotTouchSealGate()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                AssertRejected(observer, MakeContext(42), CaptureFrameDropReason.FrameDraftRegistryFull);
                AssertRejected(observer, MakeContext(42), CaptureFrameDropReason.PngEncodeFailed);
                AssertRejected(observer, MakeContext(42), CaptureFrameDropReason.PngStagingStoreFull);
                AssertRejected(observer, MakeContext(42), CaptureFrameDropReason.CaptureCancelled);
                AssertRejected(observer, MakeContext(42), CaptureFrameDropReason.FreezeDrainTimeout);

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));

                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(0));
                Assert.That(GetCount(logger, "SealedTraceEnqueueFailureCount"), Is.EqualTo(0));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(0));

                NativeArray<int> gate = GetGate(logger);
                Assert.That(gate[1], Is.EqualTo(0)); // active writer count
            }
        }
    }
}
