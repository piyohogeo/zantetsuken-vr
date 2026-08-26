using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class BladePoseWindowTests
    {
        private const float Tolerance = 1e-4f;

        private static EvaluatedBladePose PoseAt(long frameId, double timestamp, Vector3 cutSamplePosition)
        {
            return new EvaluatedBladePose(
                frameId,
                timestamp,
                new Pose(Vector3.zero, Quaternion.identity),
                Vector3.right,
                Vector3.up,
                Vector3.forward,
                cutSamplePosition);
        }

        [Test]
        public void Constructor_RejectsCapacityBelowTwo()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladePoseWindow(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladePoseWindow(1));
            Assert.That(new BladePoseWindow(2).Capacity, Is.EqualTo(2));
        }

        [Test]
        public void Append_IncreasesCount()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            Assert.That(window.Count, Is.EqualTo(0));

            Assert.That(window.TryAppend(PoseAt(1, 0.0, Vector3.zero)), Is.True);
            Assert.That(window.Count, Is.EqualTo(1));

            Assert.That(window.TryAppend(PoseAt(2, 0.030, new Vector3(0, 0.1f, 0))), Is.True);
            Assert.That(window.Count, Is.EqualTo(2));
        }

        [Test]
        public void Append_AtCapacity_OverwritesOldest()
        {
            BladePoseWindow window = new BladePoseWindow(2);
            window.TryAppend(PoseAt(1, 0.0, new Vector3(0, 0, 0)));
            window.TryAppend(PoseAt(2, 0.030, new Vector3(0, 0.05f, 0)));
            Assert.That(window.Count, Is.EqualTo(2));

            window.TryAppend(PoseAt(3, 0.060, new Vector3(0, 0.2f, 0)));
            Assert.That(window.Count, Is.EqualTo(2));

            bool ok = window.TryEvaluateLatest(0.030, 0.060, out BladeMotionSample result);
            Assert.That(ok, Is.True);
            Assert.That(result.FromFrameId, Is.EqualTo(2));
            Assert.That(result.ToFrameId, Is.EqualTo(3));
            Assert.That(result.Speed, Is.EqualTo(5f).Within(Tolerance));
        }

        [Test]
        public void Clear_ThenReuse()
        {
            BladePoseWindow window = new BladePoseWindow(2);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            window.TryAppend(PoseAt(2, 0.030, new Vector3(0, 0.1f, 0)));

            window.Clear();
            Assert.That(window.Count, Is.EqualTo(0));

            Assert.That(window.TryAppend(PoseAt(3, 0.0, Vector3.zero)), Is.True);
            Assert.That(window.Count, Is.EqualTo(1));
        }

        [Test]
        public void SameTimestamp_ClearsAndReturnsFalse()
        {
            BladePoseWindow window = new BladePoseWindow(2);
            window.TryAppend(PoseAt(1, 0.5, Vector3.zero));

            Assert.That(window.TryAppend(PoseAt(2, 0.5, new Vector3(0, 0.1f, 0))), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));
        }

        [Test]
        public void TimestampRegression_ClearsAndReturnsFalse()
        {
            BladePoseWindow window = new BladePoseWindow(2);
            window.TryAppend(PoseAt(1, 0.5, Vector3.zero));

            Assert.That(window.TryAppend(PoseAt(2, 0.4, new Vector3(0, 0.1f, 0))), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));
        }

        [Test]
        public void NonFiniteOrInvalidPose_ClearsAndReturnsFalse()
        {
            BladePoseWindow window = new BladePoseWindow(3);

            // NaN timestamp.
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            Assert.That(window.TryAppend(PoseAt(2, double.NaN, new Vector3(0, 0.1f, 0))), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));

            // Infinite position.
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            EvaluatedBladePose infPosition = new EvaluatedBladePose(2, 0.1, new Pose(Vector3.zero, Quaternion.identity), Vector3.right, Vector3.up, Vector3.forward, new Vector3(float.PositiveInfinity, 0, 0));
            Assert.That(window.TryAppend(infPosition), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));

            // Zero-length blade axis.
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            EvaluatedBladePose zeroAxis = new EvaluatedBladePose(2, 0.1, new Pose(Vector3.zero, Quaternion.identity), Vector3.zero, Vector3.up, Vector3.forward, new Vector3(0, 0.1f, 0));
            Assert.That(window.TryAppend(zeroAxis), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));

            // Non-orthogonal axes.
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            EvaluatedBladePose nonOrthogonal = new EvaluatedBladePose(2, 0.1, new Pose(Vector3.zero, Quaternion.identity), Vector3.right, Vector3.right, Vector3.forward, new Vector3(0, 0.1f, 0));
            Assert.That(window.TryAppend(nonOrthogonal), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));
        }

        [Test]
        public void AdjacentVelocityOverflow_ClearsAndReturnsFalse()
        {
            BladePoseWindow window = new BladePoseWindow(2);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));

            Assert.That(window.TryAppend(PoseAt(2, 1e-40, new Vector3(1, 0, 0))), Is.False);
            Assert.That(window.Count, Is.EqualTo(0));
        }

        [Test]
        public void AfterInvalidAppend_NextPoseStartsFresh()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));

            window.TryAppend(PoseAt(2, 0.0, new Vector3(0, 0.1f, 0)));
            Assert.That(window.Count, Is.EqualTo(0));

            Assert.That(window.TryAppend(PoseAt(3, 0.050, new Vector3(0, 0.2f, 0))), Is.True);
            Assert.That(window.Count, Is.EqualTo(1));
            Assert.That(window.TryEvaluateLatest(0.030, 0.060, out _), Is.False);
        }

        [Test]
        public void FrameIdRegressionOrEqual_IsAcceptedWhenTimestampIncreases()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(PoseAt(10, 0.0, Vector3.zero));

            Assert.That(window.TryAppend(PoseAt(5, 0.030, new Vector3(0, 0.1f, 0))), Is.True);
            Assert.That(window.TryAppend(PoseAt(5, 0.060, new Vector3(0, 0.2f, 0))), Is.True);
            Assert.That(window.Count, Is.EqualTo(3));
        }

        [Test]
        public void LessThanTwoPoses_EvaluationFailsWithDefault()
        {
            BladePoseWindow window = new BladePoseWindow(3);

            Assert.That(window.TryEvaluateLatest(0.030, 0.060, out BladeMotionSample empty), Is.False);
            Assert.That(empty.FromFrameId, Is.EqualTo(0));

            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            Assert.That(window.TryEvaluateLatest(0.030, 0.060, out BladeMotionSample single), Is.False);
            Assert.That(single.FromFrameId, Is.EqualTo(0));
        }

        [Test]
        public void WindowBoundaries_AreSelectable()
        {
            BladePoseWindow minWindow = new BladePoseWindow(3);
            minWindow.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            minWindow.TryAppend(PoseAt(2, 0.030, new Vector3(0, 0.2f, 0)));
            Assert.That(minWindow.TryEvaluateLatest(0.030, 0.060, out _), Is.True);

            BladePoseWindow maxWindow = new BladePoseWindow(3);
            maxWindow.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            maxWindow.TryAppend(PoseAt(2, 0.060, new Vector3(0, 0.2f, 0)));
            Assert.That(maxWindow.TryEvaluateLatest(0.030, 0.060, out _), Is.True);
        }

        [Test]
        public void MultipleCandidates_SelectOldest()
        {
            BladePoseWindow window = new BladePoseWindow(4);
            window.TryAppend(PoseAt(1, 0.0, new Vector3(0, 0, 0)));
            window.TryAppend(PoseAt(2, 0.030, new Vector3(0, 0.05f, 0)));
            window.TryAppend(PoseAt(3, 0.060, new Vector3(0, 0.2f, 0)));

            bool ok = window.TryEvaluateLatest(0.030, 0.060, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.FromFrameId, Is.EqualTo(1));
            Assert.That(result.ToFrameId, Is.EqualTo(3));
            Assert.That(result.Speed, Is.EqualTo(0.2f / 0.06f).Within(Tolerance));
        }

        [Test]
        public void CandidateOlderThanMaxWindow_IsNotSelected()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            window.TryAppend(PoseAt(2, 0.070, new Vector3(0, 0.2f, 0)));

            Assert.That(window.TryEvaluateLatest(0.030, 0.060, out _), Is.False);
        }

        [Test]
        public void CandidateNewerThanMinWindow_IsNotSelected()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            window.TryAppend(PoseAt(2, 0.020, new Vector3(0, 0.2f, 0)));

            Assert.That(window.TryEvaluateLatest(0.030, 0.060, out _), Is.False);
        }

        [Test]
        public void SparseSamples_NoEligibleCandidate_Fails()
        {
            BladePoseWindow window = new BladePoseWindow(4);
            window.TryAppend(PoseAt(1, 0.0, new Vector3(0, 0, 0)));
            window.TryAppend(PoseAt(2, 0.100, new Vector3(0, 0.2f, 0)));
            window.TryAppend(PoseAt(3, 0.120, new Vector3(0, 0.3f, 0)));

            Assert.That(window.TryEvaluateLatest(0.030, 0.060, out _), Is.False);
        }

        [Test]
        public void InvalidWindowArguments_FailWithoutHistoryChange()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            window.TryAppend(PoseAt(2, 0.050, new Vector3(0, 0.2f, 0)));
            int countBefore = window.Count;

            Assert.That(window.TryEvaluateLatest(double.NaN, 0.060, out _), Is.False);
            Assert.That(window.TryEvaluateLatest(0.030, double.PositiveInfinity, out _), Is.False);
            Assert.That(window.TryEvaluateLatest(0.0, 0.060, out _), Is.False);
            Assert.That(window.TryEvaluateLatest(0.060, 0.030, out _), Is.False);

            Assert.That(window.Count, Is.EqualTo(countBefore));
        }

        [Test]
        public void Result_MatchesDirectBladeMotionEvaluator()
        {
            EvaluatedBladePose a = PoseAt(1, 0.0, new Vector3(0, 0, 0));
            EvaluatedBladePose b = PoseAt(2, 0.050, new Vector3(0, 0.2f, 0));

            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(a);
            window.TryAppend(b);

            bool ok = window.TryEvaluateLatest(0.030, 0.060, out BladeMotionSample windowResult);
            bool directOk = BladeMotionEvaluator.TryEvaluate(a, b, out BladeMotionSample directResult);

            Assert.That(ok, Is.True);
            Assert.That(directOk, Is.True);
            Assert.That(windowResult.Speed, Is.EqualTo(directResult.Speed).Within(Tolerance));
            Assert.That(windowResult.LateralSpeed, Is.EqualTo(directResult.LateralSpeed).Within(Tolerance));
            Assert.That(windowResult.EdgeLeadScore, Is.EqualTo(directResult.EdgeLeadScore).Within(Tolerance));
            Assert.That(windowResult.DeltaTimeSeconds, Is.EqualTo(directResult.DeltaTimeSeconds));
        }

        [Test]
        public void Failure_ResultsInDefault()
        {
            BladePoseWindow window = new BladePoseWindow(3);
            window.TryAppend(PoseAt(1, 0.0, Vector3.zero));
            window.TryAppend(PoseAt(2, 0.070, new Vector3(0, 0.2f, 0)));

            bool ok = window.TryEvaluateLatest(0.030, 0.060, out BladeMotionSample result);

            Assert.That(ok, Is.False);
            Assert.That(result.FromFrameId, Is.EqualTo(0));
            Assert.That(result.DeltaTimeSeconds, Is.EqualTo(0.0));
            Assert.That(result.Speed, Is.EqualTo(0f));
            Assert.That(result.EdgeLeadScore, Is.EqualTo(0f));
            Assert.That(result.HasLateralMotion, Is.False);
        }
    }
}
