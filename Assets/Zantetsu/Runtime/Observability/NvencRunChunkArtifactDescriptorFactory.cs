namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless Phase 0.11 Run chunk artifact descriptor factory. It builds a
    /// <see cref="CaptureArtifactDescriptor"/> for the single fixed H.264 IDR
    /// chunk from only the caller-supplied artifact id, confirmed byte length,
    /// and confirmed lowercase hex content hash. Every other value is fixed:
    /// the <see cref="CaptureArtifactKind.FrameSequence"/> kind, the
    /// <c>NvencH264IdrChunk</c> format, version 1, and the fixed
    /// <c>chunks/chunk-0.nvenc-idr-chunk-v1.h264</c> relative path for both
    /// the staging and the final root.
    /// </summary>
    /// <remarks>
    /// This type holds no state, performs no file, stream, hash, thread, or
    /// task operation, recomputes no hash, and is not an
    /// <see cref="System.IDisposable"/>. All validation is delegated to
    /// <see cref="CaptureArtifactDescriptor"/>. The fixed pending path is
    /// exposed for the future pending-to-final publish flow but is never
    /// placed into a descriptor.
    /// </remarks>
    internal static class NvencRunChunkArtifactDescriptorFactory
    {
        internal const string StagingRelativePath = "chunks/chunk-0.nvenc-idr-chunk-v1.h264";

        internal const string FinalRelativePath = "chunks/chunk-0.nvenc-idr-chunk-v1.h264";

        internal const string PendingRelativePath = "chunks/chunk-0.nvenc-idr-chunk-v1.h264.partial";

        internal const string FormatId = "NvencH264IdrChunk";

        internal const int FormatVersion = 1;

        internal static CaptureArtifactDescriptor Create(
            string artifactId,
            long byteLength,
            string contentHash)
        {
            return new CaptureArtifactDescriptor(
                artifactId,
                CaptureArtifactKind.FrameSequence,
                FormatId,
                FormatVersion,
                StagingRelativePath,
                FinalRelativePath,
                byteLength,
                contentHash);
        }
    }
}
