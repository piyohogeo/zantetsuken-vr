using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure factory that produces a fully consistent set of four Capture Run
    /// markers for a new Run initialization: the staging and final
    /// <c>run.init</c> markers and the two identical <c>run.ready</c> markers,
    /// cross-checked into a single <see cref="CaptureRunMarkerBinding"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input validation is delegated to the first initialization marker
    /// constructor; no validation is reimplemented here and no exception is
    /// transformed or wrapped. The factory performs no retry, correction, or
    /// fallback value generation on failure.
    /// </para>
    /// <para>
    /// Init content hashes are produced only through the existing
    /// <see cref="CaptureRunInitializationMarkerCodec.ComputeContentSha256"/>.
    /// Root hashes are passed through as opaque, already-verified values and are
    /// never recomputed from a path.
    /// </para>
    /// <para>
    /// This factory owns and disposes nothing, holds no static or mutable state,
    /// and does no marker serialize or decode, no stream, file, or directory
    /// access, no root path derivation or normalization, no initialization ID
    /// generation, no OS locking, no atomic write/flush/rename, no recovery or
    /// collision classification, and no logger, registry, or draft access.
    /// </para>
    /// </remarks>
    internal static class CaptureRunMarkerBindingFactory
    {
        internal static CaptureRunMarkerBinding Create(
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

            CaptureRunReadyMarker stagingReady = new CaptureRunReadyMarker(
                testRunId,
                runInitializationId,
                stagingInitSha256,
                finalInitSha256);

            CaptureRunReadyMarker finalReady = new CaptureRunReadyMarker(
                testRunId,
                runInitializationId,
                stagingInitSha256,
                finalInitSha256);

            return new CaptureRunMarkerBinding(stagingInitialization, finalInitialization, stagingReady, finalReady);
        }
    }
}
