using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameCadenceSelectorTests
    {
        private static CaptureFrameTiming MakeTiming(double predictedDisplayTimeSeconds, bool shouldRender)
        {
            return new CaptureFrameTiming(predictedDisplayTimeSeconds, 1.0 / 90.0, shouldRender, 0.0, 0.0, 0L);
        }

        [Test]
        public void Constructor_Valid45And30()
        {
            CaptureFrameCadenceSelector s45 = new CaptureFrameCadenceSelector(45.0);
            Assert.That(s45.TargetFramesPerSecond, Is.EqualTo(45.0));
            Assert.That(s45.MinimumIntervalSeconds, Is.EqualTo(1.0 / 45.0));

            CaptureFrameCadenceSelector s30 = new CaptureFrameCadenceSelector(30.0);
            Assert.That(s30.TargetFramesPerSecond, Is.EqualTo(30.0));
            Assert.That(s30.MinimumIntervalSeconds, Is.EqualTo(1.0 / 30.0));
        }

        [Test]
        public void Constructor_InvalidValues_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameCadenceSelector(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameCadenceSelector(double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameCadenceSelector(double.NegativeInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameCadenceSelector(0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameCadenceSelector(-30.0));
        }

        [Test]
        public void DefaultTarget_Is45Fps()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector();

            Assert.That(selector.TargetFramesPerSecond, Is.EqualTo(45.0));
            Assert.That(CaptureFrameCadenceSelector.PhaseZeroTargetFramesPerSecond, Is.EqualTo(45.0));
        }

        [Test]
        public void TrySelect_InvalidTiming_ThrowsAndStateUnchanged()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.Throws<ArgumentException>(() => selector.TrySelect(default));

            Assert.That(selector.HasObservedTimestamp, Is.False);
            Assert.That(selector.HasSelectedTimestamp, Is.False);
        }

        [Test]
        public void TrySelect_FirstRenderableFrame_Selected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, false)), Is.False);
            Assert.That(selector.HasObservedTimestamp, Is.True);
            Assert.That(selector.HasSelectedTimestamp, Is.False);

            Assert.That(selector.TrySelect(MakeTiming(0.01, true)), Is.True);
            Assert.That(selector.HasSelectedTimestamp, Is.True);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.01));
        }

        [Test]
        public void TrySelect_IntervalBelow_Rejected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);

            // 0.01 seconds is below the 45 fps interval (~0.0222 s).
            Assert.That(selector.TrySelect(MakeTiming(0.01, true)), Is.False);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void TrySelect_IntervalExactly_Selected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);
            double interval = 1.0 / 45.0;

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(interval, true)), Is.True);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(interval));
        }

        [Test]
        public void TrySelect_ShouldRenderFalse_ObservedButNotSelected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, false)), Is.False);
            Assert.That(selector.TrySelect(MakeTiming(0.1, false)), Is.False);

            Assert.That(selector.HasObservedTimestamp, Is.True);
            Assert.That(selector.LastObservedTimestampSeconds, Is.EqualTo(0.1));
            Assert.That(selector.HasSelectedTimestamp, Is.False);
        }

        [Test]
        public void TrySelect_NextDueFrame_AfterNonRenderPeriod_Selected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(0.01, false)), Is.False);
            Assert.That(selector.TrySelect(MakeTiming(0.02, false)), Is.False);

            Assert.That(selector.TrySelect(MakeTiming(0.03, true)), Is.True);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.03));
        }

        [Test]
        public void TrySelect_SameTimestamp_NotDoubleSelected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.False);

            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void TrySelect_HighFps_SameTimestamp_NotReSelected()
        {
            // 2,000,000 fps gives an interval of 5e-7 s, below the absolute
            // tolerance, so a same-timestamp re-entry must still be rejected.
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(2000000.0);

            Assert.That(selector.TrySelect(MakeTiming(1.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(1.0, true)), Is.False);

            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(1.0));
        }

        [Test]
        public void TrySelect_HighFps_ElapsedShorterThanInterval_NotSelected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(2000000.0);
            double interval = selector.MinimumIntervalSeconds; // 5e-7 s

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);

            // Half the interval is clearly too soon to select.
            Assert.That(selector.TrySelect(MakeTiming(interval * 0.5, true)), Is.False);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void TrySelect_HighFps_IntervalBoundary_Selected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(2000000.0);
            double interval = selector.MinimumIntervalSeconds; // 5e-7 s

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(interval, true)), Is.True);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(interval));
        }

        [Test]
        public void TrySelect_TimestampRegression_ThrowsAndStateUnchanged()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(0.05, true)), Is.True);

            Assert.Throws<ArgumentOutOfRangeException>(() => selector.TrySelect(MakeTiming(0.04, true)));

            Assert.That(selector.HasObservedTimestamp, Is.True);
            Assert.That(selector.HasSelectedTimestamp, Is.True);
            Assert.That(selector.LastObservedTimestampSeconds, Is.EqualTo(0.05));
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.05));
        }

        [Test]
        public void TrySelect_LargeJump_SelectsOnlyOne()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);

            // A long gap selects only the current frame; no catch-up.
            Assert.That(selector.TrySelect(MakeTiming(10.0, true)), Is.True);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(10.0));

            Assert.That(selector.TrySelect(MakeTiming(10.01, true)), Is.False);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(10.0));
        }

        [Test]
        public void Reset_ThenNextFrameSelected()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);

            Assert.That(selector.TrySelect(MakeTiming(0.0, true)), Is.True);
            Assert.That(selector.TrySelect(MakeTiming(0.05, true)), Is.True);

            selector.Reset();

            Assert.That(selector.HasObservedTimestamp, Is.False);
            Assert.That(selector.HasSelectedTimestamp, Is.False);

            Assert.That(selector.TrySelect(MakeTiming(1.0, true)), Is.True);
            Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(1.0));
        }

        [Test]
        public void NinetyHz_To45Fps_DeterministicSelection()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);
            List<long> selected = new List<long>();

            const int frames = 20;
            for (int k = 0; k < frames; k++)
            {
                if (selector.TrySelect(MakeTiming(k / 90.0, true)))
                {
                    selected.Add(k);
                }
            }

            Assert.That(selected.Count, Is.EqualTo(frames / 2));
            for (int i = 0; i < selected.Count; i++)
            {
                Assert.That(selected[i], Is.EqualTo(i * 2));
            }
        }

        [Test]
        public void NinetyHz_To30Fps_DeterministicSelection()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(30.0);
            List<long> selected = new List<long>();

            const int frames = 30;
            for (int k = 0; k < frames; k++)
            {
                if (selector.TrySelect(MakeTiming(k / 90.0, true)))
                {
                    selected.Add(k);
                }
            }

            Assert.That(selected.Count, Is.EqualTo(frames / 3));
            for (int i = 0; i < selected.Count; i++)
            {
                Assert.That(selected[i], Is.EqualTo(i * 3));
            }
        }

        [Test]
        public void LongIteration_NoPeriodicSkipOrDoubleSelect()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);
            List<long> selected = new List<long>();

            const int frames = 1000;
            for (int k = 0; k < frames; k++)
            {
                if (selector.TrySelect(MakeTiming(k / 90.0, true)))
                {
                    selected.Add(k);
                }
            }

            Assert.That(selected.Count, Is.EqualTo(frames / 2));
            Assert.That(selected[0], Is.EqualTo(0));
            for (int i = 1; i < selected.Count; i++)
            {
                Assert.That(selected[i] - selected[i - 1], Is.EqualTo(2), "Consecutive selections must be exactly two frames apart.");
            }
        }

        [Test]
        public void Selector_DoesNotMutateExternalState()
        {
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(45.0);
            double target = selector.TargetFramesPerSecond;
            double interval = selector.MinimumIntervalSeconds;

            selector.TrySelect(MakeTiming(0.0, true));
            selector.TrySelect(MakeTiming(0.05, true));

            Assert.That(selector.TargetFramesPerSecond, Is.EqualTo(target));
            Assert.That(selector.MinimumIntervalSeconds, Is.EqualTo(interval));
        }

        [Test]
        public void Selector_HoldsOnlyValueTypes()
        {
            foreach (FieldInfo field in typeof(CaptureFrameCadenceSelector).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Field must be a value type: " + field.Name);
            }
        }

        [Test]
        public void Selector_SealedNotIDisposableNotMonoBehaviour()
        {
            Assert.That(typeof(CaptureFrameCadenceSelector).IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameCadenceSelector)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureFrameCadenceSelector)), Is.False);
        }
    }
}
