namespace Zantetsu.Observability
{
    internal enum CaptureArtifactVerificationStatus : int
    {
        None = 0,
        Absent = 1,
        MatchesExpected = 2,
        Mismatch = 3,
        Invalid = 4
    }
}
