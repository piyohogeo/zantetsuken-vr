using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Selects capture frames at a fixed cadence (30 or 45 fps) based on the
    /// predicted display time of each <see cref="CaptureFrameTiming"/>, so a
    /// caller can skip frame generation for unselected frames. When a frame is
    /// not selected the factory is never invoked, so no capture frame ID is
    /// consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invalid timing throws <see cref="ArgumentException"/> and leaves the
    /// state unchanged. The predicted display time must be monotonically
    /// non-decreasing: a value below the last observed value throws
    /// <see cref="ArgumentOutOfRangeException"/> and leaves the state
    /// unchanged (no implicit reset). Re-entering the same timestamp is not a
    /// regression but never re-selects a timestamp that was already selected.
    /// </para>
    /// <para>
    /// Every valid input updates the last observed timestamp regardless of
    /// <c>ShouldRender</c>. A frame with <c>ShouldRender == false</c> is never
    /// selected and does not change the last selected timestamp. The first
    /// renderable frame is always selected; thereafter a frame is selected only
    /// when <c>current - lastSelected &gt;= MinimumIntervalSeconds</c>. A long
    /// gap selects only the current frame and never catch-up generates past
    /// frames.
    /// </para>
    /// <para>
    /// A fixed, non-public comparison tolerance absorbs floating-point boundary
    /// error so 90 Hz timestamps select deterministically at 45 and 30 fps
    /// without periodic skips or double selections. The tolerance is also
    /// capped relative to the interval so it never approaches the interval
    /// itself, even at very high frame rates.
    /// </para>
    /// <para>
    /// <see cref="Reset"/> clears the observed and selected timestamps so the
    /// next renderable frame becomes the first selection again.
    /// </para>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. <see cref="TrySelect"/>
    /// performs no managed allocation, LINQ, enumeration, logging, or string
    /// generation, and never references Unity's <c>Time</c>,
    /// <c>Application</c>, <c>XRDisplaySubsystem</c>, or any capture pipeline
    /// type. It does not generate or consume frame IDs and does not retain or
    /// own any capture target. It is not <see cref="IDisposable"/>, a
    /// MonoBehaviour, or a singleton.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameCadenceSelector
    {
        /// <summary>Default capture cadence for phase zero.</summary>
        public const double PhaseZeroTargetFramesPerSecond = 45.0;

        private const double SelectionToleranceSeconds = 1e-6;

        private const double SelectionToleranceFraction = 1e-6;

        private readonly double _targetFramesPerSecond;
        private readonly double _minimumIntervalSeconds;

        private bool _hasObserved;
        private bool _hasSelected;
        private double _lastObservedTimestampSeconds;
        private double _lastSelectedTimestampSeconds;

        public CaptureFrameCadenceSelector(double targetFramesPerSecond = PhaseZeroTargetFramesPerSecond)
        {
            if (double.IsNaN(targetFramesPerSecond) || double.IsInfinity(targetFramesPerSecond) || targetFramesPerSecond <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond), targetFramesPerSecond, "Target frames per second must be finite and greater than zero.");
            }

            double interval = 1.0 / targetFramesPerSecond;
            if (double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond), targetFramesPerSecond, "Minimum interval must be finite and greater than zero.");
            }

            _targetFramesPerSecond = targetFramesPerSecond;
            _minimumIntervalSeconds = interval;
            _hasObserved = false;
            _hasSelected = false;
            _lastObservedTimestampSeconds = 0.0;
            _lastSelectedTimestampSeconds = 0.0;
        }

        public double TargetFramesPerSecond => _targetFramesPerSecond;

        public double MinimumIntervalSeconds => _minimumIntervalSeconds;

        public bool HasObservedTimestamp => _hasObserved;

        public bool HasSelectedTimestamp => _hasSelected;

        public double LastObservedTimestampSeconds => _lastObservedTimestampSeconds;

        public double LastSelectedTimestampSeconds => _lastSelectedTimestampSeconds;

        public bool TrySelect(in CaptureFrameTiming timing)
        {
            if (!timing.IsValid)
            {
                throw new ArgumentException("Timing must be valid.", nameof(timing));
            }

            double current = timing.PredictedDisplayTimeSeconds;

            if (_hasObserved && current < _lastObservedTimestampSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(timing), current, "Predicted display time must be monotonically non-decreasing.");
            }

            _hasObserved = true;
            _lastObservedTimestampSeconds = current;

            if (!timing.ShouldRender)
            {
                return false;
            }

            if (!_hasSelected)
            {
                _hasSelected = true;
                _lastSelectedTimestampSeconds = current;
                return true;
            }

            double elapsed = current - _lastSelectedTimestampSeconds;
            if (elapsed <= 0.0)
            {
                return false;
            }

            double tolerance = SelectionToleranceSeconds;
            double relativeTolerance = _minimumIntervalSeconds * SelectionToleranceFraction;
            if (relativeTolerance < tolerance)
            {
                tolerance = relativeTolerance;
            }

            if (elapsed + tolerance >= _minimumIntervalSeconds)
            {
                _lastSelectedTimestampSeconds = current;
                return true;
            }

            return false;
        }

        public void Reset()
        {
            _hasObserved = false;
            _hasSelected = false;
            _lastObservedTimestampSeconds = 0.0;
            _lastSelectedTimestampSeconds = 0.0;
        }
    }
}
