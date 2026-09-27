using System;
using UnityEngine;

namespace Zantetsu.Core.Animation
{
    public sealed partial class PoseTable
    {
        private Vector3[] _rootPositions;
        private Quaternion[] _rootRotations;
        public bool HasRootTrack => _rootPositions != null && _rootPositions.Length == SampleCount;
        public PoseTable WithYawRate(float rate)
        {
            if (!HasRootTrack) throw new InvalidOperationException("This table has no root track");
            if (rate == 0) return this;
            var result = (PoseTable)MemberwiseClone();
            result._rootPositions = new Vector3[SampleCount];
            result._rootRotations = new Quaternion[SampleCount];
            result._rootPositions[0] = _rootPositions[0];
            result._rootRotations[0] = _rootRotations[0];
            for (int i = 1; i < SampleCount; i++)
            {
                float mid = .5f * (_sampleTimes[i] + _sampleTimes[i - 1]);
                Vector3 delta = _rootPositions[i] - _rootPositions[i - 1]; delta.y = 0;
                Vector3 p = result._rootPositions[i - 1] + Quaternion.Euler(0, rate * mid, 0) * delta;
                p.y = _rootPositions[i].y; result._rootPositions[i] = p;
                result._rootRotations[i] = Quaternion.Euler(0, rate * _sampleTimes[i], 0) * _rootRotations[i];
            }
            return result;
        }
        public bool TryEvaluateRoot(double time, out Pose pose)
        {
            pose = default;
            if (!HasRootTrack || !double.IsFinite(time) || time < 0 || time > DurationSeconds) return false;
            int a = Math.Min((int)Math.Floor(time * SampleRate), RegularSampleCount - 1);
            int b = Math.Min(a + 1, SampleCount - 1);
            float w = a == b ? 0 : Mathf.Clamp01(((float)time - _sampleTimes[a]) / (_sampleTimes[b] - _sampleTimes[a]));
            pose = new Pose(Vector3.LerpUnclamped(_rootPositions[a], _rootPositions[b], w), Quaternion.Lerp(_rootRotations[a], _rootRotations[b], w));
            return true;
        }
    }
}
