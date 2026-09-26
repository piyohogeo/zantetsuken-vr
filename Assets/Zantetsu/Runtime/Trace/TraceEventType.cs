namespace Zantetsu.Trace
{
    /// <summary>
    /// Append-only identifiers for the domain trace. Numeric values are fixed:
    /// never reorder or reuse an existing value.
    /// </summary>
    public enum TraceEventType : int
    {
        None = 0,

        BladeTrackingLost = 1,
        BladeTrackingRestored = 2,
        BladeSamplesReset = 3,
        EdgeGateEntered = 4,
        EdgeGateRejected = 5,
        SlashPrimed = 6,
        SlashLatched = 7,
        SlashFrontCreated = 8,
        FrontVertexAdded = 9,
        FrontEdgeActivated = 10,
        FrontSampleIgnored = 11,
        FrontTopologyRejected = 12,
        SlashFinalizedByReversal = 13,
        SlashFinalized = 14,
        SlashFrontExpired = 15,
        SlashRecoveryStarted = 16,
        SlashRearmed = 17,
        FrontHitConfirmed = 18,
        CandidateDetected = 19,
        TaskScheduled = 20,
        TaskStarted = 21,
        TaskCompleted = 22,
        PredictionValidated = 23,
        PredictionRejected = 24,
        GenerationChanged = 25,
        MobPlanCreated = 26,
        MobPlanExtended = 27,
        MobTierChanged = 28,
        ReservationCreated = 29,
        MobPlanInvalidated = 30,
        MobReplanned = 31,
        MobPredictionUsed = 32,
        MobPredictionRejected = 33,
        CaptureFrameQueued = 34,
        CaptureFrameEncoded = 35,
        CaptureFrameDropped = 36,
        CaptureRingFrozen = 37,
        ProjectionCaptureCopied = 38,
        CommitStarted = 39,
        CommitSucceeded = 40,
        CommitRejected = 41,
        FallbackActivated = 42,
        TaskCancelled = 43,
        ResultDisposed = 44,
        CaptureFrameAdmissionRejected = 45,
        TraceIntegritySummary = 46,

        /// <summary>
        /// A SlashWave Segment Sweep met a convex a fragment is made of now (DESIGN 19.1.7, 21.16.6): an observation of
        /// the real hit, recorded with what the cut acceptance said -- including a hit it did not accept, for which no
        /// operation was issued. Not a statement that anything was cut.
        /// </summary>
        SlashHitConfirmed = 47,
    }
}
