namespace Zantetsu.Observability
{
    /// <summary>Opaque success evidence returned by an artifact store.</summary>
    internal interface ICaptureArtifactStorageReceipt
    {
        CaptureArtifactDescriptor Descriptor { get; }
    }
}
