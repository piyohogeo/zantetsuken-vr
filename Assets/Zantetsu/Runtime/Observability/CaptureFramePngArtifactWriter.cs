using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Builds a <see cref="CaptureFramePngArtifact"/> from a frame record, a
    /// saved request, and a PNG receipt, then atomically publishes its canonical
    /// JSON sidecar through a <see cref="CaptureFramePngArtifactFileStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This writer performs no PNG existence, length, or hash verification, and
    /// never re-reads or re-normalizes the PNG receipt; those responsibilities
    /// belong to <see cref="CaptureFramePngArtifactVerifier"/>. Sidecar
    /// serialization, hashing, temp-file writing, flush, and rename are
    /// delegated to the file store.
    /// </para>
    /// <para>
    /// The writer owns and disposes nothing: not the file store, frame record,
    /// receipt, artifact, or PNG file.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactWriter
    {
        private readonly CaptureFramePngArtifactFileStore _artifactFileStore;

        public CaptureFramePngArtifactWriter(CaptureFramePngArtifactFileStore artifactFileStore)
        {
            if (artifactFileStore == null)
            {
                throw new ArgumentNullException(nameof(artifactFileStore));
            }

            _artifactFileStore = artifactFileStore;
        }

        public CaptureFramePngArtifactSaveReceipt SaveAtomic(
            string sidecarDestinationPath,
            CaptureFrameRecord frameRecord,
            in CaptureFrameRequest savedRequest,
            CaptureFramePngSaveReceipt pngReceipt,
            out CaptureFramePngArtifact artifact)
        {
            artifact = null;

            if (frameRecord == null)
            {
                throw new ArgumentNullException(nameof(frameRecord));
            }

            if (pngReceipt == null)
            {
                throw new ArgumentNullException(nameof(pngReceipt));
            }

            CaptureFramePngArtifact built = new CaptureFramePngArtifact(frameRecord, savedRequest, pngReceipt);

            CaptureFramePngArtifactSaveReceipt receipt = _artifactFileStore.SaveAtomic(sidecarDestinationPath, built);

            artifact = built;
            return receipt;
        }
    }
}
