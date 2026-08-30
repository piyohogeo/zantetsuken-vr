namespace Zantetsu.Observability
{
    /// <summary>
    /// Reasons a capture frame request may be dropped from scheduling.
    /// Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFrameDropReason : int
    {
        /// <summary>No drop. Default sentinel.</summary>
        None = 0,

        /// <summary>The capture frame request queue was full.</summary>
        RequestQueueFull = 1,

        /// <summary>The GPU readback completed with an error.</summary>
        ReadbackFailed = 2,

        /// <summary>The encoded PNG queue was full.</summary>
        EncodedPngQueueFull = 3,

        /// <summary>The capture frame record registry was full.</summary>
        FrameRecordRegistryFull = 4,

        /// <summary>
        /// A draft could not be admitted before an ID was issued because the
        /// draft registry was full. Never used for a dropped draft that already
        /// has a positive capture frame ID.
        /// </summary>
        FrameDraftRegistryFull = 5,

        /// <summary>Encoding a pending draft to PNG failed.</summary>
        PngEncodeFailed = 6,

        /// <summary>
        /// The encoded PNG could not be admitted to durable staging because its
        /// capacity was full.
        /// </summary>
        PngStagingStoreFull = 7,

        /// <summary>An explicit or shutdown cancellation.</summary>
        CaptureCancelled = 8,

        /// <summary>
        /// A pending draft remaining after the freeze deadline was forcibly
        /// dropped. Never enqueued to the normal logger queue; only the future
        /// freeze terminal builder uses this reason.
        /// </summary>
        FreezeDrainTimeout = 9,
        /// <summary>Format-independent capture surface or readback failure.</summary>
        CaptureInputFailed = 10,

        /// <summary>Format-independent backend media processing failure.</summary>
        MediaProcessingFailed = 11,

        /// <summary>Format-independent backend processing backpressure.</summary>
        MediaProcessingBackpressured = 12,

        /// <summary>Format-independent artifact staging capacity failure.</summary>
        ArtifactStagingFull = 13,

        /// <summary>Format-independent artifact durable write failure.</summary>
        ArtifactWriteFailed = 14
    }
}
