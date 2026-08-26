using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Routes completed GPU readbacks: successful results are returned to the
    /// caller, while errored results are traced as dropped and released
    /// automatically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only. Does not own, dispose, or drain the dispatcher,
    /// observer, logger, or pool, and does not mutate capture frame IDs or
    /// requests.
    /// </para>
    /// <para>
    /// Release ownership is asymmetric: the caller must release successful
    /// results (after reading their buffer), while the router releases errored
    /// results itself.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameReadbackCompletionRouter
    {
        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly CaptureFrameTraceObserver _observer;

        public CaptureFrameReadbackCompletionRouter(
            UnityRenderTextureReadbackDispatcher dispatcher,
            CaptureFrameTraceObserver observer)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            _dispatcher = dispatcher;
            _observer = observer;
        }

        public int ActiveReadbackCount => _dispatcher.ActiveCount;

        /// <summary>
        /// Collects a completed readback. Successful results are returned as
        /// <see cref="CaptureFrameReadbackCollectStatus.Succeeded"/> and remain
        /// rented until the caller releases them; errored results are traced
        /// with <see cref="CaptureFrameDropReason.ReadbackFailed"/>, released,
        /// and reported as <see cref="CaptureFrameReadbackCollectStatus.Dropped"/>.
        /// </summary>
        public CaptureFrameReadbackCollectStatus TryCollect(out CaptureFrameReadbackResult result)
        {
            if (!_dispatcher.TryCollect(out CaptureFrameReadbackResult collected))
            {
                result = default;
                return CaptureFrameReadbackCollectStatus.None;
            }

            if (!collected.HasError)
            {
                result = collected;
                return CaptureFrameReadbackCollectStatus.Succeeded;
            }

            result = default;

            try
            {
                _observer.RecordDropped(collected.FrameRequest.TraceContext, CaptureFrameDropReason.ReadbackFailed);
            }
            finally
            {
                _dispatcher.Release(collected);
            }

            return CaptureFrameReadbackCollectStatus.Dropped;
        }
    }
}
