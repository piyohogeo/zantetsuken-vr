using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameTimingTests
    {
        private static readonly double DefaultTime = 1.234;
        private static readonly double DefaultPeriod = 1.0 / 90.0;
        private static readonly double DefaultAppGpu = 3.5;
        private static readonly double DefaultCompositorGpu = 1.25;
        private static readonly long DefaultDropped = 7L;

        private static CaptureFrameTiming MakeValid(bool shouldRender = true)
        {
            return new CaptureFrameTiming(DefaultTime, DefaultPeriod, shouldRender, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped);
        }

        private static void AssertNoReferenceFields(Type type)
        {
            Assert.That(type.IsValueType, Is.True);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Reference-type field: " + field.Name);
            }
        }

        [Test]
        public void ValidValues_Preserved()
        {
            CaptureFrameTiming timing = MakeValid();

            Assert.That(timing.PredictedDisplayTimeSeconds, Is.EqualTo(DefaultTime));
            Assert.That(timing.PredictedDisplayPeriodSeconds, Is.EqualTo(DefaultPeriod));
            Assert.That(timing.ShouldRender, Is.True);
            Assert.That(timing.AppGpuTimeMilliseconds, Is.EqualTo(DefaultAppGpu));
            Assert.That(timing.CompositorGpuTimeMilliseconds, Is.EqualTo(DefaultCompositorGpu));
            Assert.That(timing.DroppedFrameCount, Is.EqualTo(DefaultDropped));
        }

        [Test]
        public void ShouldRender_TrueFalse_Preserved()
        {
            Assert.That(MakeValid(true).ShouldRender, Is.True);
            Assert.That(MakeValid(false).ShouldRender, Is.False);
        }

        [Test]
        public void DisplayTime_ZeroBoundary_Accepted()
        {
            CaptureFrameTiming timing = new CaptureFrameTiming(0.0, DefaultPeriod, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped);

            Assert.That(timing.IsValid, Is.True);
            Assert.That(timing.PredictedDisplayTimeSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void DisplayPeriod_Zero_Negative_NaN_Infinity_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, 0.0, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, -0.1, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, double.NaN, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, double.PositiveInfinity, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
        }

        [Test]
        public void DisplayTime_Negative_NaN_Infinity_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(-0.1, DefaultPeriod, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(double.NaN, DefaultPeriod, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(double.PositiveInfinity, DefaultPeriod, true, DefaultAppGpu, DefaultCompositorGpu, DefaultDropped));
        }

        [Test]
        public void GpuTime_ZeroBoundary_Accepted()
        {
            CaptureFrameTiming timing = new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, 0.0, 0.0, 0L);

            Assert.That(timing.IsValid, Is.True);
        }

        [Test]
        public void AppGpuTime_Negative_NaN_Infinity_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, -0.1, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, double.NaN, DefaultCompositorGpu, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, double.PositiveInfinity, DefaultCompositorGpu, DefaultDropped));
        }

        [Test]
        public void CompositorGpuTime_Negative_NaN_Infinity_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, DefaultAppGpu, -0.1, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, DefaultAppGpu, double.NaN, DefaultDropped));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, DefaultAppGpu, double.PositiveInfinity, DefaultDropped));
        }

        [Test]
        public void DroppedFrameCount_ZeroBoundary_Accepted()
        {
            CaptureFrameTiming timing = new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, DefaultAppGpu, DefaultCompositorGpu, 0L);

            Assert.That(timing.IsValid, Is.True);
            Assert.That(timing.DroppedFrameCount, Is.EqualTo(0L));
        }

        [Test]
        public void DroppedFrameCount_Negative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameTiming(DefaultTime, DefaultPeriod, true, DefaultAppGpu, DefaultCompositorGpu, -1L));
        }

        [Test]
        public void ConstructorSuccess_IsValid()
        {
            Assert.That(MakeValid().IsValid, Is.True);
        }

        [Test]
        public void Default_IsInvalid()
        {
            Assert.That(default(CaptureFrameTiming).IsValid, Is.False);
        }

        [Test]
        public void IsValid_ComputedNotStored_NoBackingField()
        {
            foreach (FieldInfo field in typeof(CaptureFrameTiming).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.Name, Does.Not.Contain("IsValid"), "IsValid must be computed from held values, not stored as an independent flag.");
            }
        }

        [Test]
        public void ReadonlyStruct_NoReferenceFields()
        {
            AssertNoReferenceFields(typeof(CaptureFrameTiming));
        }

        [Test]
        public void CultureIndependent()
        {
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                CaptureFrameTiming timing = MakeValid();

                Assert.That(timing.PredictedDisplayTimeSeconds, Is.EqualTo(DefaultTime));
                Assert.That(timing.PredictedDisplayPeriodSeconds, Is.EqualTo(DefaultPeriod));
                Assert.That(timing.IsValid, Is.True);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void HotPath_StructShape_NoStaticState()
        {
            Type type = typeof(CaptureFrameTiming);

            Assert.That(type.IsValueType, Is.True);
            AssertNoReferenceFields(type);

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Length, Is.EqualTo(0));
        }
    }
}
