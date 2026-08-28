using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameAdmissionRejectedTests
    {
        private static Type GetRejectKindType()
        {
            Type type = typeof(TraceLogger).Assembly.GetType("Zantetsu.Observability.CaptureFrameAdmissionRejectKind");
            Assert.That(type, Is.Not.Null, "CaptureFrameAdmissionRejectKind type not found.");
            return type;
        }

        private static object RejectKind(int value)
        {
            return Enum.ToObject(GetRejectKindType(), value);
        }

        private static CaptureFrameTraceContext MakeContext(long captureFrameId = 0, long testRunId = 99)
        {
            return new CaptureFrameTraceContext(
                12345, 100, 200, 3, captureFrameId, 77, testRunId, 11, 22, 33, 44, 66);
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

        private static void RecordAdmissionRejected(CaptureFrameTraceObserver observer, CaptureFrameTraceContext context, object rejectKind)
        {
            MethodInfo method = typeof(CaptureFrameTraceObserver).GetMethod(
                "RecordAdmissionRejected", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "RecordAdmissionRejected method not found.");
            method.Invoke(observer, new object[] { context, rejectKind });
        }

        private static Exception RecordAdmissionRejectedException(CaptureFrameTraceObserver observer, CaptureFrameTraceContext context, object rejectKind)
        {
            try
            {
                RecordAdmissionRejected(observer, context, rejectKind);
                return null;
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException tie && tie.InnerException != null)
                {
                    return tie.InnerException;
                }

                return ex;
            }
        }

        private static void AssertAdmissionPayload(TraceEvent e, CaptureFrameTraceContext c, int expectedValue0)
        {
            Assert.That(e.EventType, Is.EqualTo(TraceEventType.CaptureFrameAdmissionRejected));
            Assert.That(e.CaptureFrameId, Is.EqualTo(0));
            Assert.That(e.TestRunId, Is.EqualTo(c.TestRunId));
            Assert.That(e.TaskType, Is.EqualTo(TraceTaskType.None));
            Assert.That(e.FromState, Is.EqualTo(0));
            Assert.That(e.ToState, Is.EqualTo(0));
            Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
            Assert.That(e.Value0, Is.EqualTo((double)expectedValue0));
            Assert.That(e.Value1, Is.EqualTo((double)CaptureFrameDropReason.FrameDraftRegistryFull));

            Assert.That(e.Timestamp, Is.EqualTo(c.Timestamp));
            Assert.That(e.FrameId, Is.EqualTo(c.UnityFrameId));
            Assert.That(e.FixedStepId, Is.EqualTo(c.FixedStepId));
            Assert.That(e.ThreadId, Is.EqualTo(c.ThreadId));
            Assert.That(e.OpenXRFrameId, Is.EqualTo(c.OpenXRFrameId));
            Assert.That(e.SlashId, Is.EqualTo(c.SlashId));
            Assert.That(e.FrontEdgeId, Is.EqualTo(c.FrontEdgeId));
            Assert.That(e.ObjectId, Is.EqualTo(c.ObjectId));
            Assert.That(e.ObjectGeneration, Is.EqualTo(c.ObjectGeneration));
            Assert.That(e.TaskId, Is.EqualTo(c.TaskId));

            Assert.That(e.SlashGeneration, Is.EqualTo(0u));
            Assert.That(e.MobId, Is.EqualTo(0L));
            Assert.That(e.PlanGeneration, Is.EqualTo(0u));
        }

        // ---- TraceEventType contracts ----

        [Test]
        public void TraceEventType_UnderlyingTypeIsInt()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(TraceEventType)), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void TraceEventType_ExistingValues0To44_Unchanged()
        {
            string[] expected =
            {
                "None",
                "BladeTrackingLost", "BladeTrackingRestored", "BladeSamplesReset", "EdgeGateEntered", "EdgeGateRejected",
                "SlashPrimed", "SlashLatched", "SlashFrontCreated", "FrontVertexAdded", "FrontEdgeActivated",
                "FrontSampleIgnored", "FrontTopologyRejected", "SlashFinalizedByReversal", "SlashFinalized", "SlashFrontExpired",
                "SlashRecoveryStarted", "SlashRearmed", "FrontHitConfirmed", "CandidateDetected", "TaskScheduled",
                "TaskStarted", "TaskCompleted", "PredictionValidated", "PredictionRejected", "GenerationChanged",
                "MobPlanCreated", "MobPlanExtended", "MobTierChanged", "ReservationCreated", "MobPlanInvalidated",
                "MobReplanned", "MobPredictionUsed", "MobPredictionRejected", "CaptureFrameQueued", "CaptureFrameEncoded",
                "CaptureFrameDropped", "CaptureRingFrozen", "ProjectionCaptureCopied", "CommitStarted", "CommitSucceeded",
                "CommitRejected", "FallbackActivated", "TaskCancelled", "ResultDisposed",
            };

            Assert.That(expected.Length, Is.EqualTo(45));

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(Enum.GetName(typeof(TraceEventType), i), Is.EqualTo(expected[i]), "Value " + i + " name mismatch.");
            }
        }

        [Test]
        public void TraceEventType_CaptureFrameAdmissionRejected_Is45()
        {
            Assert.That((int)TraceEventType.CaptureFrameAdmissionRejected, Is.EqualTo(45));
            Assert.That(Enum.GetName(typeof(TraceEventType), 45), Is.EqualTo("CaptureFrameAdmissionRejected"));
        }

        [Test]
        public void TraceEventType_HasNoAliasesOrGaps_0To45()
        {
            Type type = typeof(TraceEventType);

            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(46));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(46));

            for (int i = 0; i <= 45; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 46), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Payload contracts ----

        [Test]
        public void RecordAdmissionRejected_PendingLimit_FullPayload()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameTraceContext context = MakeContext(captureFrameId: 0, testRunId: 99);

                RecordAdmissionRejected(observer, context, RejectKind(1)); // PendingLimit
                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                AssertAdmissionPayload(logger.GetHistoryEvent(0), context, 1);
            }
        }

        [Test]
        public void RecordAdmissionRejected_RunEntryLimit_FullPayload()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameTraceContext context = MakeContext(captureFrameId: 0, testRunId: 99);

                RecordAdmissionRejected(observer, context, RejectKind(2)); // RunEntryLimit
                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                AssertAdmissionPayload(logger.GetHistoryEvent(0), context, 2);
            }
        }

        [Test]
        public void RecordAdmissionRejected_CorrelationValuesTranscribed()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                    111, 222, 333, 44, 0, 555, 666, 777, 888, 999, 1010, 1212);

                RecordAdmissionRejected(observer, context, RejectKind(1));
                logger.Drain();

                TraceEvent e = logger.GetHistoryEvent(0);
                Assert.That(e.Timestamp, Is.EqualTo(111L));
                Assert.That(e.FrameId, Is.EqualTo(222L));
                Assert.That(e.FixedStepId, Is.EqualTo(333L));
                Assert.That(e.ThreadId, Is.EqualTo(44));
                Assert.That(e.OpenXRFrameId, Is.EqualTo(555L));
                Assert.That(e.TestRunId, Is.EqualTo(666L));
                Assert.That(e.SlashId, Is.EqualTo(777L));
                Assert.That(e.FrontEdgeId, Is.EqualTo(888L));
                Assert.That(e.ObjectId, Is.EqualTo(999L));
                Assert.That(e.ObjectGeneration, Is.EqualTo(1010u));
                Assert.That(e.TaskId, Is.EqualTo(1212L));
            }
        }

        [Test]
        public void RecordAdmissionRejected_CommonFieldsStayZero()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                RecordAdmissionRejected(observer, MakeContext(captureFrameId: 0), RejectKind(1));
                logger.Drain();

                TraceEvent e = logger.GetHistoryEvent(0);
                Assert.That(e.FromState, Is.EqualTo(0));
                Assert.That(e.ToState, Is.EqualTo(0));
                Assert.That(e.SlashGeneration, Is.EqualTo(0u));
                Assert.That(e.MobId, Is.EqualTo(0L));
                Assert.That(e.PlanGeneration, Is.EqualTo(0u));
                Assert.That(e.TaskType, Is.EqualTo(TraceTaskType.None));
                Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
            }
        }

        // ---- Argument validation contracts ----

        [Test]
        public void RecordAdmissionRejected_RejectsInvalidRejectKind()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameTraceContext context = MakeContext(captureFrameId: 0, testRunId: 99);

                foreach (int value in new[] { 0, -1, 3, int.MaxValue })
                {
                    Exception ex = RecordAdmissionRejectedException(observer, context, RejectKind(value));
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "Value " + value + " must be rejected.");
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("rejectKind"));
                }
            }
        }

        [Test]
        public void RecordAdmissionRejected_RejectsNonZeroAndNegativeCaptureFrameId()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                foreach (long captureFrameId in new[] { 5L, -5L })
                {
                    Exception ex = RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: captureFrameId), RejectKind(1));
                    Assert.That(ex, Is.TypeOf<ArgumentException>(), "CaptureFrameId " + captureFrameId + " must be rejected.");
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("context"));
                }
            }
        }

        [Test]
        public void RecordAdmissionRejected_RejectsNonPositiveTestRunId()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                foreach (long testRunId in new[] { 0L, -1L })
                {
                    Exception ex = RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: testRunId), RejectKind(1));
                    Assert.That(ex, Is.TypeOf<ArgumentException>(), "TestRunId " + testRunId + " must be rejected.");
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("context"));
                }
            }
        }

        [Test]
        public void RecordAdmissionRejected_RejectionLeavesLoggerUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 5), RejectKind(1));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: 0), RejectKind(1));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: -1), RejectKind(1));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0), RejectKind(0));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0), RejectKind(3));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0), RejectKind(int.MaxValue));

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));
            }
        }

        [Test]
        public void RecordAdmissionRejected_CaptureRunRejectionDoesNotTouchSealGate()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 5, testRunId: 42), RejectKind(1));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: 0), RejectKind(1));
                RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: 42), RejectKind(0));

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

        [Test]
        public void RecordAdmissionRejected_TestRunIdMismatch_KeepsLoggerException()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Exception ex = RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: 99), RejectKind(1));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
            }
        }

        [Test]
        public void RecordAdmissionRejected_DisposedLogger_KeepsException()
        {
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            logger.Dispose();

            Exception ex = RecordAdmissionRejectedException(observer, MakeContext(captureFrameId: 0, testRunId: 99), RejectKind(1));
            Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
        }

        // ---- Existing contract preservation ----

        [Test]
        public void RecordDropped_FrameDraftRegistryFull_StillRejected()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => observer.RecordDropped(MakeContext(captureFrameId: 55), CaptureFrameDropReason.FrameDraftRegistryFull));
                Assert.That(ex.ParamName, Is.EqualTo("reason"));
            }
        }

        [Test]
        public void RecordAdmissionRejected_GeneratesOnlyAdmissionRejectedNotDropped()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                RecordAdmissionRejected(observer, MakeContext(captureFrameId: 0, testRunId: 99), RejectKind(1));
                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameAdmissionRejected));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.Not.EqualTo(TraceEventType.CaptureFrameDropped));
            }
        }
    }
}
