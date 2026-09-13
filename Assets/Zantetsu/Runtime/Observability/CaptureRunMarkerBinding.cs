namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, fully-initialized Capture Run marker binding: the staging and
    /// final <c>run.init</c> markers and the single <c>run.ready</c> marker that
    /// both Run roots share. Built from the Run's own scalars, it can only ever
    /// hold a mutually consistent marker set; it never represents a one-sided,
    /// tmp-only, or partially recovered Run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction makes the two initialization markers, hashes each of them
    /// exactly once, and makes the one ready marker those hashes describe.
    /// Input validation is delegated to the first initialization marker
    /// constructor; none of it is reimplemented here and no exception is
    /// transformed or wrapped. No retry, correction, or fallback value is
    /// produced on failure.
    /// </para>
    /// <para>
    /// The same ready marker is returned for both roots. It is immutable, so
    /// the two roots still receive identical <c>run.ready</c> content; only the
    /// duplicate object is gone, and its identity is not part of any contract.
    /// </para>
    /// <para>
    /// Init content hashes are computed only through the existing
    /// <see cref="CaptureRunInitializationMarkerCodec.ComputeContentSha256"/>;
    /// no hash computation or canonical serialization is reimplemented here.
    /// Root hashes are treated as opaque, already-verified values and are never
    /// recomputed from an absolute root path.
    /// </para>
    /// <para>
    /// This type performs no marker decode or serialize, no stream, file, or
    /// directory access, no root path derivation or normalization, no root
    /// hash computation, no initialization ID generation, no OS locking, no
    /// root creation/removal/repair, no atomic write/flush/rename, no tmp
    /// marker evaluation, no recovery or collision classification, and no
    /// logger, registry, or draft access. It is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunMarkerBinding
    {
        private readonly CaptureRunInitializationMarker _stagingInitialization;
        private readonly CaptureRunInitializationMarker _finalInitialization;
        private readonly CaptureRunReadyMarker _ready;

        internal CaptureRunMarkerBinding(
            long testRunId,
            string runInitializationId,
            string stagingRunRootSha256,
            string finalRunRootSha256)
        {
            CaptureRunInitializationMarker stagingInitialization = new CaptureRunInitializationMarker(
                testRunId,
                runInitializationId,
                CaptureRunRootRole.Staging,
                stagingRunRootSha256,
                finalRunRootSha256);

            CaptureRunInitializationMarker finalInitialization = new CaptureRunInitializationMarker(
                testRunId,
                runInitializationId,
                CaptureRunRootRole.Final,
                stagingRunRootSha256,
                finalRunRootSha256);

            string stagingInitSha256 = CaptureRunInitializationMarkerCodec.ComputeContentSha256(stagingInitialization);
            string finalInitSha256 = CaptureRunInitializationMarkerCodec.ComputeContentSha256(finalInitialization);

            CaptureRunReadyMarker ready = new CaptureRunReadyMarker(
                testRunId,
                runInitializationId,
                stagingInitSha256,
                finalInitSha256);

            _stagingInitialization = stagingInitialization;
            _finalInitialization = finalInitialization;
            _ready = ready;
        }

        internal CaptureRunInitializationMarker StagingInitialization => _stagingInitialization;

        internal CaptureRunInitializationMarker FinalInitialization => _finalInitialization;

        internal CaptureRunReadyMarker StagingReady => _ready;

        internal CaptureRunReadyMarker FinalReady => _ready;

        internal long TestRunId => _stagingInitialization.TestRunId;

        internal string RunInitializationId => _stagingInitialization.RunInitializationId;

        internal string StagingRunRootSha256 => _stagingInitialization.StagingRunRootSha256;

        internal string FinalRunRootSha256 => _stagingInitialization.FinalRunRootSha256;
    }
}
