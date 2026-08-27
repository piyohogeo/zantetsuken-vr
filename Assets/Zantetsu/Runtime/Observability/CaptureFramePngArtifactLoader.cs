using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Loads a canonical capture frame artifact sidecar and verifies the PNG it
    /// references against the receipt, returning the artifact only when both the
    /// sidecar load and the PNG verification succeed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous I/O. The sidecar is always fully loaded and validated before
    /// the PNG is read, so a corrupt or mismatched sidecar is reported before
    /// any PNG error.
    /// </para>
    /// <para>
    /// A single instance must not be used concurrently: the verifier owns a
    /// reusable read buffer.
    /// </para>
    /// <para>
    /// Never creates, modifies, deletes, renames, or repairs the sidecar or PNG,
    /// never creates directories, never mutates the artifact, manifest, or
    /// receipt, and retains no stream or PNG byte sequence. It does not own or
    /// dispose the file store or verifier, records no trace events, and uses no
    /// Unity static API, Task, async, or LINQ. It does not implement
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactLoader
    {
        private readonly CaptureFramePngArtifactFileStore _artifactFileStore;
        private readonly CaptureFramePngArtifactVerifier _artifactVerifier;

        public CaptureFramePngArtifactLoader(
            CaptureFramePngArtifactFileStore artifactFileStore,
            CaptureFramePngArtifactVerifier artifactVerifier)
        {
            if (artifactFileStore == null)
            {
                throw new ArgumentNullException(nameof(artifactFileStore));
            }

            if (artifactVerifier == null)
            {
                throw new ArgumentNullException(nameof(artifactVerifier));
            }

            _artifactFileStore = artifactFileStore;
            _artifactVerifier = artifactVerifier;
        }

        /// <summary>
        /// Loads and verifies the artifact sidecar at
        /// <paramref name="sidecarPath"/>. Returns the loaded artifact only when
        /// the sidecar deserializes correctly against
        /// <paramref name="runManifest"/> and the referenced PNG matches the
        /// receipt.
        /// </summary>
        public CaptureFramePngArtifact LoadVerified(
            string sidecarPath,
            TraceRunManifest runManifest)
        {
            CaptureFramePngArtifact artifact = _artifactFileStore.Load(sidecarPath, runManifest);
            _artifactVerifier.Verify(artifact);
            return artifact;
        }
    }
}
