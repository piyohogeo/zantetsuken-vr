namespace Zantetsu.Observability
{
    /// <summary>
    /// The pair of markers one Capture Run writes: the initialization marker
    /// that names the Run root, and the ready marker that pins that
    /// initialization marker's content hash.
    /// </summary>
    internal sealed class CaptureRunMarkerBinding
    {
        private readonly CaptureRunInitializationMarker _initialization;
        private readonly CaptureRunReadyMarker _ready;

        internal CaptureRunMarkerBinding(
            long testRunId,
            string runInitializationId,
            string runRootSha256)
        {
            CaptureRunInitializationMarker initialization = new CaptureRunInitializationMarker(
                testRunId,
                runInitializationId,
                runRootSha256);

            _initialization = initialization;
            _ready = new CaptureRunReadyMarker(
                testRunId,
                runInitializationId,
                CaptureRunInitializationMarkerCodec.ComputeContentSha256(initialization));
        }

        internal CaptureRunInitializationMarker Initialization => _initialization;

        internal CaptureRunReadyMarker Ready => _ready;

        internal long TestRunId => _initialization.TestRunId;

        internal string RunInitializationId => _initialization.RunInitializationId;

        internal string RunRootSha256 => _initialization.RunRootSha256;
    }
}
