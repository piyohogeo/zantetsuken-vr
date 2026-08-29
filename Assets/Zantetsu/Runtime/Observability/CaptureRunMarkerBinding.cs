using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, fully-initialized Capture Run marker binding: the two
    /// <c>run.init</c> markers and the two <c>run.ready</c> markers of a Run
    /// cross-checked purely in memory. This type represents only the state
    /// where all four markers are present and mutually consistent; it never
    /// represents a one-sided, tmp-only, or partially recovered Run root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four input markers are held as immutable, non-owning references and
    /// are never copied, disposed, or mutated. <see cref="TestRunId"/>,
    /// <see cref="RunInitializationId"/>, <see cref="StagingRunRootSha256"/>,
    /// and <see cref="FinalRunRootSha256"/> are forwarded from the staging
    /// initialization marker, which is the authority after the initialization
    /// markers are verified to agree.
    /// </para>
    /// <para>
    /// Both <c>run.ready</c> markers must agree on SchemaVersion, TestRunId,
    /// RunInitializationId, StagingInitSha256, and FinalInitSha256. Because the
    /// ready serializer is deterministic, equal values imply byte-for-byte
    /// identical canonical bytes.
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
        private readonly CaptureRunReadyMarker _stagingReady;
        private readonly CaptureRunReadyMarker _finalReady;

        internal CaptureRunMarkerBinding(
            CaptureRunInitializationMarker stagingInitialization,
            CaptureRunInitializationMarker finalInitialization,
            CaptureRunReadyMarker stagingReady,
            CaptureRunReadyMarker finalReady)
        {
            if (stagingInitialization == null)
            {
                throw new ArgumentNullException(nameof(stagingInitialization));
            }

            if (finalInitialization == null)
            {
                throw new ArgumentNullException(nameof(finalInitialization));
            }

            if (stagingReady == null)
            {
                throw new ArgumentNullException(nameof(stagingReady));
            }

            if (finalReady == null)
            {
                throw new ArgumentNullException(nameof(finalReady));
            }

            if (stagingInitialization.RootRole != CaptureRunRootRole.Staging)
            {
                throw new ArgumentException("Staging initialization marker must have RootRole Staging.", nameof(stagingInitialization));
            }

            if (finalInitialization.RootRole != CaptureRunRootRole.Final)
            {
                throw new ArgumentException("Final initialization marker must have RootRole Final.", nameof(finalInitialization));
            }

            if (stagingInitialization.TestRunId != finalInitialization.TestRunId)
            {
                throw new ArgumentException("Initialization markers must have the same TestRunId.", nameof(finalInitialization));
            }

            if (!string.Equals(stagingInitialization.RunInitializationId, finalInitialization.RunInitializationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Initialization markers must have the same RunInitializationId.", nameof(finalInitialization));
            }

            if (!string.Equals(stagingInitialization.StagingRunRootSha256, finalInitialization.StagingRunRootSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Initialization markers must have the same StagingRunRootSha256.", nameof(finalInitialization));
            }

            if (!string.Equals(stagingInitialization.FinalRunRootSha256, finalInitialization.FinalRunRootSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Initialization markers must have the same FinalRunRootSha256.", nameof(finalInitialization));
            }

            if (stagingReady.SchemaVersion != finalReady.SchemaVersion)
            {
                throw new ArgumentException("Ready markers must have the same SchemaVersion.", nameof(finalReady));
            }

            if (stagingReady.TestRunId != finalReady.TestRunId)
            {
                throw new ArgumentException("Ready markers must have the same TestRunId.", nameof(finalReady));
            }

            if (!string.Equals(stagingReady.RunInitializationId, finalReady.RunInitializationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Ready markers must have the same RunInitializationId.", nameof(finalReady));
            }

            if (!string.Equals(stagingReady.StagingInitSha256, finalReady.StagingInitSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Ready markers must have the same StagingInitSha256.", nameof(finalReady));
            }

            if (!string.Equals(stagingReady.FinalInitSha256, finalReady.FinalInitSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Ready markers must have the same FinalInitSha256.", nameof(finalReady));
            }

            if (stagingReady.TestRunId != stagingInitialization.TestRunId)
            {
                throw new ArgumentException("Ready markers must match the initialization TestRunId.", nameof(stagingReady));
            }

            if (!string.Equals(stagingReady.RunInitializationId, stagingInitialization.RunInitializationId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Ready markers must match the initialization RunInitializationId.", nameof(stagingReady));
            }

            string stagingInitSha256 = CaptureRunInitializationMarkerCodec.ComputeContentSha256(stagingInitialization);
            if (!string.Equals(stagingReady.StagingInitSha256, stagingInitSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("StagingInitSha256 must match the staging initialization marker content hash.", nameof(stagingReady));
            }

            string finalInitSha256 = CaptureRunInitializationMarkerCodec.ComputeContentSha256(finalInitialization);
            if (!string.Equals(finalReady.FinalInitSha256, finalInitSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("FinalInitSha256 must match the final initialization marker content hash.", nameof(finalReady));
            }

            _stagingInitialization = stagingInitialization;
            _finalInitialization = finalInitialization;
            _stagingReady = stagingReady;
            _finalReady = finalReady;
        }

        internal CaptureRunInitializationMarker StagingInitialization => _stagingInitialization;

        internal CaptureRunInitializationMarker FinalInitialization => _finalInitialization;

        internal CaptureRunReadyMarker StagingReady => _stagingReady;

        internal CaptureRunReadyMarker FinalReady => _finalReady;

        internal long TestRunId => _stagingInitialization.TestRunId;

        internal string RunInitializationId => _stagingInitialization.RunInitializationId;

        internal string StagingRunRootSha256 => _stagingInitialization.StagingRunRootSha256;

        internal string FinalRunRootSha256 => _stagingInitialization.FinalRunRootSha256;
    }
}
