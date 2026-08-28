namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that a capture run <see cref="TraceLogger"/> was
    /// successfully sealed. Produced only by
    /// <see cref="TraceLogger.SealAndDrainRunForFreeze"/> and never
    /// reconstructible by callers: there is no public constructor, no setters,
    /// and no owned resources.
    /// </summary>
    /// <remarks>
    /// The receipt fixes the seal-time values once and never recalculates them.
    /// It holds only references to the issuing logger and the target recorder
    /// (for identity verification) and primitive values; it does not own or
    /// dispose the logger or recorder and requires no disposal itself.
    /// </remarks>
    internal sealed class TraceRunSealReceipt
    {
        private readonly TraceLogger _issuedBy;
        private readonly TraceFlightRecorder _issuedTo;
        private readonly long _testRunId;
        private readonly int _finalDrainedCount;
        private readonly int _capturedPostRollCount;
        private readonly int _traceCaptureOverflowCount;
        private readonly int _sealedTraceEnqueueFailureCount;

        internal TraceRunSealReceipt(
            TraceLogger issuedBy,
            TraceFlightRecorder issuedTo,
            long testRunId,
            int finalDrainedCount,
            int capturedPostRollCount,
            int traceCaptureOverflowCount,
            int sealedTraceEnqueueFailureCount)
        {
            _issuedBy = issuedBy;
            _issuedTo = issuedTo;
            _testRunId = testRunId;
            _finalDrainedCount = finalDrainedCount;
            _capturedPostRollCount = capturedPostRollCount;
            _traceCaptureOverflowCount = traceCaptureOverflowCount;
            _sealedTraceEnqueueFailureCount = sealedTraceEnqueueFailureCount;
        }

        /// <summary>The logger that issued this receipt.</summary>
        internal TraceLogger IssuedBy => _issuedBy;

        /// <summary>The recorder this receipt was issued to.</summary>
        internal TraceFlightRecorder IssuedTo => _issuedTo;

        /// <summary>The bound test run ID at seal time.</summary>
        internal long TestRunId => _testRunId;

        /// <summary>Total number of events drained by the final seal drain.</summary>
        internal int FinalDrainedCount => _finalDrainedCount;

        /// <summary>Cumulative post-roll events captured at seal completion.</summary>
        internal int CapturedPostRollCount => _capturedPostRollCount;

        /// <summary>Cumulative overflow counted at seal completion.</summary>
        internal int TraceCaptureOverflowCount => _traceCaptureOverflowCount;

        /// <summary>Sealed enqueue failure count fixed at seal time.</summary>
        internal int SealedTraceEnqueueFailureCount => _sealedTraceEnqueueFailureCount;
    }
}
