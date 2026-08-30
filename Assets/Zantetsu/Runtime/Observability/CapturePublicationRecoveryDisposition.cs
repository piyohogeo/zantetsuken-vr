namespace Zantetsu.Observability
{
    internal enum CapturePublicationRecoveryDisposition : int
    {
        None = 0,
        PublishMissingArtifacts = 1,
        CaptureComplete = 2,
        ArtifactSourceMissing = 3,
        RunRootCollision = 4
    }
}
