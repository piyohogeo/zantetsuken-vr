namespace Zantetsu.Observability
{
    /// <summary>
    /// A loaded trace run bundle: the manifest metadata, the frozen event
    /// snapshot, and the content hashes of the manifest and trace files.
    /// </summary>
    public sealed class TraceRunBundle
    {
        internal TraceRunBundle(
            TraceRunManifest manifest,
            TraceCaptureSnapshot snapshot,
            string manifestContentSha256,
            string traceContentSha256)
        {
            Manifest = manifest;
            Snapshot = snapshot;
            ManifestContentSha256 = manifestContentSha256;
            TraceContentSha256 = traceContentSha256;
        }

        public TraceRunManifest Manifest { get; }

        public TraceCaptureSnapshot Snapshot { get; }

        public string ManifestContentSha256 { get; }

        public string TraceContentSha256 { get; }
    }
}
