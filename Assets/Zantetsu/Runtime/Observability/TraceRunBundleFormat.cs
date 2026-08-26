namespace Zantetsu.Observability
{
    /// <summary>
    /// Layout constants for the version 1 trace run bundle directory.
    /// </summary>
    public static class TraceRunBundleFormat
    {
        public const int CurrentVersion = 1;

        public const string IndexFileName = "bundle.index";

        public const string ManifestFileName = "manifest.json";

        public const string TraceFileName = "trace.bin";

        internal const string IndexHeaderLine = "ZANTETSU_TRACE_BUNDLE 1";

        internal const int MaximumIndexByteCount = 512;
    }
}
