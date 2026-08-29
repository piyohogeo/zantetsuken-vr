using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run initialization plan: correlates a
    /// <see cref="CaptureRunMarkerPathSet"/> with a
    /// <see cref="CaptureRunMarkerBinding"/> for the same Run and forwards the
    /// Run's identity, roots, and root hashes from their authorities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two dependencies are held as immutable, non-owning references only
    /// after every correlation check succeeds. <see cref="TestRunId"/>,
    /// <see cref="StagingRunRoot"/>, and <see cref="FinalRunRoot"/> are
    /// forwarded from <see cref="MarkerPaths"/>.<c>RootLayout</c>.
    /// <see cref="RunInitializationId"/> is forwarded from
    /// <see cref="MarkerBinding"/>. <see cref="StagingRunRootSha256"/> and
    /// <see cref="FinalRunRootSha256"/> are forwarded from
    /// <see cref="MarkerBinding"/>, which is the authority once the two
    /// dependencies are verified to agree on the same Run.
    /// </para>
    /// <para>
    /// Root hash comparison uses <see cref="StringComparison.Ordinal"/>; no
    /// hash is recomputed and no path is re-normalized. The internal invariants
    /// of the path set and of the binding remain their own responsibility; this
    /// type re-implements none of their per-field checks. Its only duty is the
    /// Run correlation between the two dependencies.
    /// </para>
    /// <para>
    /// This type decodes and serializes nothing, holds no byte array, performs
    /// no file, directory, or I/O operation, creates no root or directory,
    /// writes, flushes, or renames no tmp entry, generates no initialization
    /// ID, recomputes no root or init hash, and performs no Dispose or Clear.
    /// It manages no Run lifecycle and no lock ownership. It is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationPlan
    {
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly CaptureRunMarkerBinding _markerBinding;

        internal CaptureRunInitializationPlan(
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunMarkerBinding markerBinding)
        {
            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (markerBinding == null)
            {
                throw new ArgumentNullException(nameof(markerBinding));
            }

            CaptureRunRootLayout rootLayout = markerPaths.RootLayout;
            if (rootLayout == null)
            {
                throw new ArgumentException("Marker paths must hold a root layout.", nameof(markerPaths));
            }

            if (rootLayout.TestRunId != markerBinding.TestRunId)
            {
                throw new ArgumentException("Marker paths and marker binding must share the same TestRunId.", nameof(markerBinding));
            }

            if (!string.Equals(rootLayout.StagingRunRootSha256, markerBinding.StagingRunRootSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Staging root hash must match the marker binding.", nameof(markerBinding));
            }

            if (!string.Equals(rootLayout.FinalRunRootSha256, markerBinding.FinalRunRootSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Final root hash must match the marker binding.", nameof(markerBinding));
            }

            _markerPaths = markerPaths;
            _markerBinding = markerBinding;
        }

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal CaptureRunMarkerBinding MarkerBinding => _markerBinding;

        internal long TestRunId => _markerPaths.RootLayout.TestRunId;

        internal string RunInitializationId => _markerBinding.RunInitializationId;

        internal string StagingRunRoot => _markerPaths.RootLayout.StagingRunRoot;

        internal string FinalRunRoot => _markerPaths.RootLayout.FinalRunRoot;

        internal string StagingRunRootSha256 => _markerBinding.StagingRunRootSha256;

        internal string FinalRunRootSha256 => _markerBinding.FinalRunRootSha256;
    }
}
