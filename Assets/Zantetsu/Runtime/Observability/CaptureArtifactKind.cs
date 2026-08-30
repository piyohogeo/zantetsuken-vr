namespace Zantetsu.Observability
{
    /// <summary>Format-independent artifact role. Append-only.</summary>
    internal enum CaptureArtifactKind : int
    {
        None = 0,
        FrameImage = 1,
        FrameMetadata = 2,
        RunManifest = 3,
        FrameIndex = 4,
        TraceBundle = 5
    }
}
