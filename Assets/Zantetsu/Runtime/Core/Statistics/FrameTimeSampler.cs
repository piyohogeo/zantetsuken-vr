#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using UnityEngine;
using UnityEngine.XR;

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// Reads one frame's times for the statistics (DESIGN 21.18): <see cref="FrameTimingManager"/> first -- the main
    /// thread's frame (the wait for the present included), the render thread's, the GPU's -- and the XR display's app
    /// GPU time as a source of its own, kept apart because it is another definition. FrameTimingManager's results come
    /// a few frames late and the same result can be read twice: a result whose frame start was read already is not
    /// taken again (<see cref="Duplicates"/>), and a frame with no new result gives no value (<see cref="Absent"/>),
    /// never 0 ms. A value that is not a positive finite number is absent too.
    /// <para>
    /// **The lag, observed (Editor and Development builds only).** How many frames late a result arrives is what the
    /// collector's attribution bound (<see cref="PlayStatisticsCollector.AttributionLagFrames"/>) assumes; nothing in a
    /// Release build measures it. Here, under the observation symbol, each Sample keeps the frame number and the Stopwatch
    /// reading it was made at (a ring of 64), and a new result's frameStartTimestamp -- taken to be in the CPU timer's
    /// ticks that <see cref="FrameTimingManager.GetCpuTimerFrequency"/> gives, the same clock as Stopwatch on Windows (QPC),
    /// unverified elsewhere -- is matched to the latest Sample at or before it: the lag is the frames between.
    /// <see cref="MaxLagFrames"/>, <see cref="LagsPastBound"/> and <see cref="LagCounts"/> say what was seen; a result that
    /// matches no Sample in the ring is <see cref="LagUnknown"/>. This is a verification aid for the Development Player
    /// check, not a part of the statistics sent.
    /// </para>
    /// </summary>
    public sealed class FrameTimeSampler
    {
        private readonly FrameTiming[] _one = new FrameTiming[1];
        private ulong _lastFrameStart;
        private bool _any;

        /// <summary>The XR display whose app GPU time is read, or null (no XR: no such value).</summary>
        public XRDisplaySubsystem XrDisplay { get; set; }

        public ulong Reads { get; private set; }
        public ulong Duplicates { get; private set; }
        public ulong Absent { get; private set; }
        public ulong GpuAbsent { get; private set; }

        /// <summary>Whether FrameTimingManager is on in this build (the Player setting; off, every value is absent).</summary>
        public static bool FrameTimingEnabled => FrameTimingManager.IsFeatureEnabled();

#if ZANTETSU_OBSERVE
        private const int Ring = 64;
        private readonly int[] _ringFrame = new int[Ring];
        private readonly long[] _ringTicks = new long[Ring];
        private int _ringNext;
        private int _ringCount;

        /// <summary>The lag histogram by frames (the last bucket: that many or more).</summary>
        public readonly uint[] LagCounts = new uint[17];
        public int MaxLagFrames { get; private set; }
        public ulong LagsPastBound { get; private set; }
        public ulong LagUnknown { get; private set; }
        public ulong LagsMeasured { get; private set; }
#endif

        public FrameTimeSample Sample()
        {
            Reads++;
            var sample = new FrameTimeSample();
#if ZANTETSU_OBSERVE
            int frame = Time.frameCount;
            _ringFrame[_ringNext] = frame;
            _ringTicks[_ringNext] = System.Diagnostics.Stopwatch.GetTimestamp();
            _ringNext = (_ringNext + 1) % Ring;
            if (_ringCount < Ring) _ringCount++;
#endif
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _one) > 0)
            {
                FrameTiming t = _one[0];
                if (_any && t.frameStartTimestamp == _lastFrameStart)
                {
                    Duplicates++;
                }
                else
                {
                    _any = true;
                    _lastFrameStart = t.frameStartTimestamp;
                    sample.HasMain = Positive(t.cpuMainThreadFrameTime, out sample.MainMs);
                    sample.HasRender = Positive(t.cpuRenderThreadFrameTime, out sample.RenderMs);
                    sample.HasGpu = Positive(t.gpuFrameTime, out sample.GpuMs);
                    if (!sample.HasGpu) GpuAbsent++;
#if ZANTETSU_OBSERVE
                    ObserveLag(frame, t.frameStartTimestamp);
#endif
                }
            }
            else
            {
                Absent++;
            }

            XRDisplaySubsystem xr = XrDisplay;
            if (xr != null && xr.TryGetAppGPUTimeLastFrame(out float seconds))
            {
                sample.HasGpuXr = Positive(seconds * 1000.0, out sample.GpuXrMs);
            }

            return sample;
        }

#if ZANTETSU_OBSERVE
        // The result's frame start in Stopwatch ticks, matched to the latest Sample made at or before it.
        private void ObserveLag(int frame, ulong frameStart)
        {
            ulong frequency = FrameTimingManager.GetCpuTimerFrequency();
            if (frequency == 0) { LagUnknown++; return; }
            long ticks = (long)((double)frameStart * System.Diagnostics.Stopwatch.Frequency / frequency);
            int bestFrame = int.MinValue;
            long bestTicks = long.MinValue;
            for (int i = 0; i < _ringCount; i++)
            {
                if (_ringTicks[i] <= ticks && _ringTicks[i] > bestTicks) { bestTicks = _ringTicks[i]; bestFrame = _ringFrame[i]; }
            }

            if (bestFrame == int.MinValue) { LagUnknown++; return; }
            int lag = frame - bestFrame;
            if (lag < 0) { LagUnknown++; return; }
            LagsMeasured++;
            LagCounts[lag < LagCounts.Length ? lag : LagCounts.Length - 1]++;
            if (lag > MaxLagFrames) MaxLagFrames = lag;
            if (lag > PlayStatisticsCollector.AttributionLagFrames) LagsPastBound++;
        }

        /// <summary>One line of what the lag observation saw (Editor/Development only).</summary>
        public string DescribeLag()
        {
            var b = new System.Text.StringBuilder();
            b.Append("frame timing lag: measured ").Append(LagsMeasured).Append(", unknown ").Append(LagUnknown).Append(", max ").Append(MaxLagFrames)
                .Append(" frames, past the bound ").Append(PlayStatisticsCollector.AttributionLagFrames).Append(": ").Append(LagsPastBound).Append("; by frames [");
            for (int i = 0; i < LagCounts.Length; i++) { if (i > 0) b.Append(' '); b.Append(LagCounts[i]); }
            return b.Append("] (the timestamp domain is assumed to be Stopwatch's: Windows QPC; unverified elsewhere)").ToString();
        }
#endif

        private static bool Positive(double ms, out float value)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms <= 0.0)
            {
                value = 0f;
                return false;
            }

            value = (float)ms;
            return true;
        }
    }
}
