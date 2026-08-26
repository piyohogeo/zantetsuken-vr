using System;
using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Fixed-capacity history of evaluated blade poses with deterministic
    /// sample window selection. Main thread only; preallocated storage and no
    /// allocation, LINQ, enumeration, logging, or string formatting on the
    /// append/evaluate/clear paths.
    /// </summary>
    public sealed class BladePoseWindow
    {
        private readonly EvaluatedBladePose[] _poses;
        private int _head;   // Index where the next pose is written.
        private int _count;  // Number of valid entries, in [0, Capacity].

        public BladePoseWindow(int capacity)
        {
            if (capacity < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 2.");
            }

            _poses = new EvaluatedBladePose[capacity];
            _head = 0;
            _count = 0;
        }

        public int Capacity => _poses.Length;

        public int Count => _count;

        /// <summary>
        /// Appends a pose. On any invalid pose or numeric failure the whole
        /// history is cleared and false is returned; the invalid pose itself is
        /// never stored.
        /// </summary>
        public bool TryAppend(in EvaluatedBladePose pose)
        {
            if (!IsValidPose(pose))
            {
                Clear();
                return false;
            }

            if (_count > 0)
            {
                if (!BladeMotionEvaluator.TryEvaluate(PeekLatest(), pose, out _))
                {
                    Clear();
                    return false;
                }
            }

            _poses[_head] = pose;
            _head++;
            if (_head == _poses.Length)
            {
                _head = 0;
            }

            if (_count < _poses.Length)
            {
                _count++;
            }

            return true;
        }

        /// <summary>
        /// Selects a candidate pose whose timestamp delta from the latest pose
        /// falls within [minimum, maximum] and evaluates the motion. Scans
        /// oldest to newest and adopts the first (oldest) eligible candidate.
        /// </summary>
        public bool TryEvaluateLatest(
            double minimumWindowSeconds,
            double maximumWindowSeconds,
            out BladeMotionSample result)
        {
            result = default;

            if (double.IsNaN(minimumWindowSeconds) || double.IsInfinity(minimumWindowSeconds)
                || double.IsNaN(maximumWindowSeconds) || double.IsInfinity(maximumWindowSeconds))
            {
                return false;
            }

            if (!(minimumWindowSeconds > 0.0))
            {
                return false;
            }

            if (!(maximumWindowSeconds >= minimumWindowSeconds))
            {
                return false;
            }

            if (_count < 2)
            {
                return false;
            }

            EvaluatedBladePose latest = PeekLatest();

            for (int i = 0; i < _count - 1; i++)
            {
                EvaluatedBladePose candidate = GetOldestToNewest(i);
                double delta = latest.TimestampSeconds - candidate.TimestampSeconds;

                if (delta >= minimumWindowSeconds && delta <= maximumWindowSeconds)
                {
                    return BladeMotionEvaluator.TryEvaluate(candidate, latest, out result);
                }
            }

            result = default;
            return false;
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_poses, 0, _poses.Length);
        }

        private static bool IsValidPose(in EvaluatedBladePose pose)
        {
            if (double.IsNaN(pose.TimestampSeconds) || double.IsInfinity(pose.TimestampSeconds))
            {
                return false;
            }

            if (!BladePoseValidation.IsFinite(pose.CutSamplePosition))
            {
                return false;
            }

            if (!BladePoseValidation.HasValidBladeAxes(pose))
            {
                return false;
            }

            return true;
        }

        private EvaluatedBladePose PeekLatest()
        {
            int index = _head - 1;
            if (index < 0)
            {
                index = _poses.Length - 1;
            }

            return _poses[index];
        }

        private EvaluatedBladePose GetOldestToNewest(int index)
        {
            int start = (_count == _poses.Length) ? _head : 0;
            int physical = start + index;
            if (physical >= _poses.Length)
            {
                physical -= _poses.Length;
            }

            return _poses[physical];
        }
    }
}
