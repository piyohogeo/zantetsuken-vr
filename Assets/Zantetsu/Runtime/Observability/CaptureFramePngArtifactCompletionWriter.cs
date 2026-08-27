using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Completes a saved capture frame: publishes its canonical JSON sidecar
    /// through a <see cref="CaptureFramePngArtifactWriter"/> and, only after
    /// successful publication, removes the corresponding
    /// <see cref="CaptureFrameRecord"/> from a
    /// <see cref="CaptureFrameRecordRegistry"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is looked up by <see cref="CaptureFrameRecordRegistry.TryGet"/>
    /// before any file is created. If the sidecar save fails, the record is not
    /// removed and the caller can retry the same request and PNG receipt against
    /// a different destination. If the sidecar is published but the registry
    /// removal then fails (no record, or a different record instance), an
    /// <see cref="InvalidOperationException"/> is thrown and the already
    /// published sidecar is not deleted, overwritten, or rolled back.
    /// </para>
    /// <para>
    /// Main-thread only. Under that contract the registry is assumed not to be
    /// modified externally between the pre-save lookup and the post-save removal.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the registry, the artifact writer, the
    /// record, the request, the PNG receipt, or the artifact. It performs no PNG
    /// existence, length, or hash verification, never reads the PNG, and
    /// re-implements neither request matching, artifact construction, nor
    /// sidecar serialization. It records no trace events and does not generate
    /// file names or create directories.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactCompletionWriter
    {
        private readonly CaptureFrameRecordRegistry _recordRegistry;
        private readonly CaptureFramePngArtifactWriter _artifactWriter;

        public CaptureFramePngArtifactCompletionWriter(
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFramePngArtifactWriter artifactWriter)
        {
            if (recordRegistry == null)
            {
                throw new ArgumentNullException(nameof(recordRegistry));
            }

            if (artifactWriter == null)
            {
                throw new ArgumentNullException(nameof(artifactWriter));
            }

            _recordRegistry = recordRegistry;
            _artifactWriter = artifactWriter;
        }

        /// <summary>
        /// Looks up the record for <paramref name="savedRequest"/>, publishes its
        /// sidecar through the artifact writer, and removes the record from the
        /// registry only after the sidecar is atomically published.
        /// </summary>
        public CaptureFramePngArtifactSaveReceipt SaveAtomic(
            string sidecarDestinationPath,
            in CaptureFrameRequest savedRequest,
            CaptureFramePngSaveReceipt pngReceipt,
            out CaptureFramePngArtifact artifact)
        {
            artifact = null;

            if (pngReceipt == null)
            {
                throw new ArgumentNullException(nameof(pngReceipt));
            }

            if (!savedRequest.IsValid)
            {
                throw new ArgumentException("Saved request must be valid.", nameof(savedRequest));
            }

            if (!_recordRegistry.TryGet(savedRequest, out CaptureFrameRecord record))
            {
                throw new InvalidOperationException("No capture frame record is registered for the saved capture frame ID.");
            }

            CaptureFramePngArtifactSaveReceipt receipt =
                _artifactWriter.SaveAtomic(sidecarDestinationPath, record, savedRequest, pngReceipt, out CaptureFramePngArtifact built);

            if (!_recordRegistry.TryRemove(savedRequest, out CaptureFrameRecord removed))
            {
                throw new InvalidOperationException(
                    "The capture frame record could not be removed from the registry after the sidecar was published; the sidecar may already be published at: " + receipt.DestinationPath);
            }

            if (!ReferenceEquals(removed, record))
            {
                throw new InvalidOperationException(
                    "The registry returned a different record than the one matched before the sidecar was published; the sidecar may already be published at: " + receipt.DestinationPath);
            }

            artifact = built;
            return receipt;
        }
    }
}
