namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable per-store capability for opening an artifact path without
    /// following reparse points. Each store owns one opener instance; there is
    /// no process-global capability state and no way to rewrite a capability
    /// after the store has been constructed.
    /// </summary>
    internal interface ICaptureArtifactNoFollowOpener
    {
        bool IsSupported { get; }

        CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath);
    }
}
