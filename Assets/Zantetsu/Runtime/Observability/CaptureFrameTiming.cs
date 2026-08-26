using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Per-frame display and GPU timing for a capture record. A value type
    /// with no reference-type fields and no Unity static API, OpenXR API,
    /// DateTime, Time, XRDisplaySubsystem, or ProfilerRecorder access.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Units: <see cref="PredictedDisplayTimeSeconds"/> and
    /// <see cref="PredictedDisplayPeriodSeconds"/> are seconds;
    /// <see cref="AppGpuTimeMilliseconds"/> and
    /// <see cref="CompositorGpuTimeMilliseconds"/> are milliseconds;
    /// <see cref="DroppedFrameCount"/> is a cumulative count over the same
    /// counter series, either since run start or as defined by the caller. The
    /// origin of the counter series is the caller's responsibility.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> is computed from the held values rather than
    /// stored as an independent flag. <see cref="default"/> is therefore
    /// invalid because its display period is zero.
    /// </para>
    /// </remarks>
    public readonly struct CaptureFrameTiming
    {
        public double PredictedDisplayTimeSeconds { get; }

        public double PredictedDisplayPeriodSeconds { get; }

        public bool ShouldRender { get; }

        public double AppGpuTimeMilliseconds { get; }

        public double CompositorGpuTimeMilliseconds { get; }

        public long DroppedFrameCount { get; }

        public bool IsValid =>
            double.IsFinite(PredictedDisplayTimeSeconds) && PredictedDisplayTimeSeconds >= 0.0 &&
            double.IsFinite(PredictedDisplayPeriodSeconds) && PredictedDisplayPeriodSeconds > 0.0 &&
            double.IsFinite(AppGpuTimeMilliseconds) && AppGpuTimeMilliseconds >= 0.0 &&
            double.IsFinite(CompositorGpuTimeMilliseconds) && CompositorGpuTimeMilliseconds >= 0.0 &&
            DroppedFrameCount >= 0;

        public CaptureFrameTiming(
            double predictedDisplayTimeSeconds,
            double predictedDisplayPeriodSeconds,
            bool shouldRender,
            double appGpuTimeMilliseconds,
            double compositorGpuTimeMilliseconds,
            long droppedFrameCount)
        {
            if (double.IsNaN(predictedDisplayTimeSeconds) || double.IsInfinity(predictedDisplayTimeSeconds) || predictedDisplayTimeSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(predictedDisplayTimeSeconds), predictedDisplayTimeSeconds, "Predicted display time must be finite and non-negative.");
            }

            if (double.IsNaN(predictedDisplayPeriodSeconds) || double.IsInfinity(predictedDisplayPeriodSeconds) || predictedDisplayPeriodSeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(predictedDisplayPeriodSeconds), predictedDisplayPeriodSeconds, "Predicted display period must be finite and greater than zero.");
            }

            if (double.IsNaN(appGpuTimeMilliseconds) || double.IsInfinity(appGpuTimeMilliseconds) || appGpuTimeMilliseconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(appGpuTimeMilliseconds), appGpuTimeMilliseconds, "App GPU time must be finite and non-negative.");
            }

            if (double.IsNaN(compositorGpuTimeMilliseconds) || double.IsInfinity(compositorGpuTimeMilliseconds) || compositorGpuTimeMilliseconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(compositorGpuTimeMilliseconds), compositorGpuTimeMilliseconds, "Compositor GPU time must be finite and non-negative.");
            }

            if (droppedFrameCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(droppedFrameCount), droppedFrameCount, "Dropped frame count must not be negative.");
            }

            PredictedDisplayTimeSeconds = predictedDisplayTimeSeconds;
            PredictedDisplayPeriodSeconds = predictedDisplayPeriodSeconds;
            ShouldRender = shouldRender;
            AppGpuTimeMilliseconds = appGpuTimeMilliseconds;
            CompositorGpuTimeMilliseconds = compositorGpuTimeMilliseconds;
            DroppedFrameCount = droppedFrameCount;
        }
    }
}
