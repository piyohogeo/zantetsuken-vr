namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed, append-only terminal status of one PngJson capture-complete
    /// owner release.
    /// </summary>
    /// <remarks>
    /// Values are explicitly fixed and must only ever be appended; existing
    /// values must never be renumbered or removed.
    /// </remarks>
    internal enum PngJsonCapturePublicationCaptureCompleteReleaseStatus : int
    {
        None = 0,
        OwnerReleased = 1,
    }
}
