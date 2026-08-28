using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Live run correlation for a capture draft, fixed before freeze. This type
    /// is the source of truth for a draft's run identity while the final
    /// <c>TraceRunManifest</c> has not yet been produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The final manifest and its content hash are not retained here because
    /// they are only determined after freeze. <c>CaptureFrameId</c> is also not
    /// retained; frame-level identity is the draft's own responsibility.
    /// </para>
    /// <para>
    /// This type does not own the source <see cref="TraceRunContext"/> or any
    /// draft derived from it, does not manage ID uniqueness, and does not manage
    /// the run lifecycle or admission state. It is an immutable, thread-safe
    /// read-only value.
    /// </para>
    /// </remarks>
    internal sealed class CaptureDraftRunContext
    {
        internal CaptureDraftRunContext(
            TraceRunContext traceRunContext,
            long testCaseId,
            int captureProfileId)
        {
            if (traceRunContext == null)
            {
                throw new ArgumentNullException(nameof(traceRunContext));
            }

            if (testCaseId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testCaseId), testCaseId, "Test case ID must be greater than zero.");
            }

            if (captureProfileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureProfileId), captureProfileId, "Capture profile ID must be greater than zero.");
            }

            TestRunId = traceRunContext.TestRunId;
            TestCaseId = testCaseId;
            BuildId = traceRunContext.BuildId;
            SceneId = traceRunContext.SceneId;
            RandomSeed = traceRunContext.RandomSeed;
            CaptureProfileId = captureProfileId;
        }

        public long TestRunId { get; }

        public long TestCaseId { get; }

        public string BuildId { get; }

        public string SceneId { get; }

        public long RandomSeed { get; }

        public int CaptureProfileId { get; }

        /// <summary>
        /// Compares the four numeric fields by value and the two string fields
        /// with <see cref="StringComparison.Ordinal"/> without reflection, LINQ,
        /// string generation, or hash computation.
        /// </summary>
        internal bool IdenticalTo(CaptureDraftRunContext other)
        {
            if (other == null)
            {
                return false;
            }

            return TestRunId == other.TestRunId
                && TestCaseId == other.TestCaseId
                && RandomSeed == other.RandomSeed
                && CaptureProfileId == other.CaptureProfileId
                && string.Equals(BuildId, other.BuildId, StringComparison.Ordinal)
                && string.Equals(SceneId, other.SceneId, StringComparison.Ordinal);
        }
    }
}
