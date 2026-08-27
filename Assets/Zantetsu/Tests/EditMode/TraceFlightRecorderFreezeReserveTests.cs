using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceFlightRecorderFreezeReserveTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static ConstructorInfo GetInternalCtor()
        {
            ConstructorInfo ctor = typeof(TraceFlightRecorder).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "Internal constructor not found.");
            return ctor;
        }

        private static TraceFlightRecorder CreateRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            return (TraceFlightRecorder)GetInternalCtor().Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
        }

        private static Exception CtorException(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            try
            {
                GetInternalCtor().Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static string RangeParamName(Exception exception)
        {
            return ((ArgumentOutOfRangeException)exception).ParamName;
        }

        private static Type GetFactoryType()
        {
            Type factoryType = typeof(TraceFlightRecorder).Assembly.GetType("Zantetsu.Observability.CaptureTraceFlightRecorderFactory");
            Assert.That(factoryType, Is.Not.Null, "Factory type not found.");
            return factoryType;
        }

        private static MethodInfo GetFactoryCreateMethod()
        {
            MethodInfo create = GetFactoryType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(create, Is.Not.Null, "Factory Create method not found.");
            return create;
        }

        private static TraceFlightRecorder FactoryCreate(TraceLogger logger, CaptureFrameProfile frameProfile, CaptureTraceProfile traceProfile)
        {
            return (TraceFlightRecorder)GetFactoryCreateMethod().Invoke(null, new object[] { logger, frameProfile, traceProfile });
        }

        private static Exception FactoryException(TraceLogger logger, CaptureFrameProfile frameProfile, CaptureTraceProfile traceProfile)
        {
            try
            {
                GetFactoryCreateMethod().Invoke(null, new object[] { logger, frameProfile, traceProfile });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static CaptureFrameProfile MakeFrameProfile(int profileId)
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(profileId, new CaptureImageRect(0, 0, 2, 2));
        }

        [Test]
        public void PublicConstructor_LegacyReserveZero()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 5);

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(0));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(5));
                Assert.That(recorder.PostRollCapacity, Is.EqualTo(5));
            }
        }

        [Test]
        public void Legacy_ImmediateFreeze_WhenPostRollZero()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void Legacy_AutoFreeze_WhenPostRollFull()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 2; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(2));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void Legacy_ManualFreeze_Works()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                logger.Enqueue(Event(1));
                recorder.Drain();

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(recorder.Freeze(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void InternalConstructor_InvalidValues_ParamName()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                Assert.That(CtorException(null, 0, 0), Is.InstanceOf<ArgumentNullException>());
                Assert.That(RangeParamName(CtorException(logger, -1, 0)), Is.EqualTo("postRollCapacity"));
                Assert.That(RangeParamName(CtorException(logger, 4, -1)), Is.EqualTo("freezeTerminalTraceReserve"));
                Assert.That(RangeParamName(CtorException(logger, 4, 5)), Is.EqualTo("freezeTerminalTraceReserve"));
                Assert.That(RangeParamName(CtorException(logger, int.MaxValue, 0)), Is.EqualTo("postRollCapacity"));
            }
        }

        [Test]
        public void Factory_NullDependencies_Rejected()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameProfile frameProfile = MakeFrameProfile(7);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(7, 4096, 32, 10000);

                Assert.That(FactoryException(null, frameProfile, traceProfile), Is.InstanceOf<ArgumentNullException>());
                Assert.That(FactoryException(logger, null, traceProfile), Is.InstanceOf<ArgumentNullException>());
                Assert.That(FactoryException(logger, frameProfile, null), Is.InstanceOf<ArgumentNullException>());
            }
        }

        [Test]
        public void Factory_ProfileIdMismatch_NoSideEffects()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                logger.Enqueue(Event(1));

                CaptureFrameProfile frameProfile = MakeFrameProfile(7);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(9, 4096, 32, 10000);

                Assert.That(FactoryException(logger, frameProfile, traceProfile), Is.InstanceOf<ArgumentException>());

                // The logger was not drained, disposed, or otherwise touched.
                Assert.That(logger.IsCreated, Is.True);
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.Drain(), Is.EqualTo(1));
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Factory_PhaseZeroCapacities()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameProfile frameProfile = MakeFrameProfile(7);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(7, 4096, 32, 10000);

                TraceFlightRecorder recorder = FactoryCreate(logger, frameProfile, traceProfile);

                Assert.That(recorder.PostRollCapacity, Is.EqualTo(4096));
                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(33));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(4063));
            }
        }

        [Test]
        public void Factory_ReserveEqualsMaxInFlightPlusOne()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameProfile frameProfile = MakeFrameProfile(7);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(7, 64, 5, 100);

                TraceFlightRecorder recorder = FactoryCreate(logger, frameProfile, traceProfile);

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(6));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(58));
            }
        }

        [Test]
        public void ReserveRecorder_StopsAtNormalPostRollCapacity()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2); // NormalPostRollCapacity == 3

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(2));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(3));

                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));

                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(10));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));

                for (int i = 11; i <= 15; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(5));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            }
        }

        [Test]
        public void ReserveRecorder_Freeze_ReturnsFalse_Unchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                logger.Enqueue(Event(1));
                recorder.Drain();

                int capturedBefore = recorder.CapturedCount;
                int postRollBefore = recorder.CapturedPostRollCount;

                Assert.That(recorder.Freeze(), Is.False);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore));
            }
        }

        [Test]
        public void ReserveRecorder_NormalPostRollZero_StaysCapturingPostRoll()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 2, 2); // NormalPostRollCapacity == 0

                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(0));

                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            }
        }

        [Test]
        public void Reset_KeepsCapacityValues()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                logger.Enqueue(Event(1));
                recorder.Drain();

                recorder.Reset();

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(2));
                Assert.That(recorder.PostRollCapacity, Is.EqualTo(5));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Armed));
                Assert.That(recorder.CapturedCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Factory_DoesNotOwnOrDisposeLoggerProfile()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameProfile frameProfile = MakeFrameProfile(7);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(7, 4096, 32, 10000);

                TraceFlightRecorder recorder = FactoryCreate(logger, frameProfile, traceProfile);

                Assert.That(logger.IsCreated, Is.True);
                Assert.That(frameProfile.ProfileId, Is.EqualTo(7));
                Assert.That(traceProfile.CaptureProfileId, Is.EqualTo(7));
                Assert.That(recorder.PostRollCapacity, Is.EqualTo(traceProfile.PostRollCapacity));
            }
        }
    }
}
