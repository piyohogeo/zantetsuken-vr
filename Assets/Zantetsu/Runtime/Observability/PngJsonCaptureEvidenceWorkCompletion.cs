using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completion bundle published by one accepted evidence work
    /// item. The worker token identifies the worker slot for input release and
    /// acknowledgement; the frame and artifact completions carry the
    /// backend-issued token stamped into the submission.
    /// </summary>
    internal readonly struct PngJsonCaptureEvidenceWorkCompletion
    {
        internal CaptureFrameWorkToken WorkToken { get; }

        internal CaptureFrameCompletion FrameCompletion { get; }

        internal CaptureArtifactCompletion ImageArtifact { get; }

        internal CaptureArtifactCompletion MetadataArtifact { get; }

        internal PngJsonCaptureEvidenceWorkCompletion(
            in CaptureFrameWorkToken workToken,
            in CaptureFrameCompletion frameCompletion,
            CaptureArtifactCompletion imageArtifact,
            CaptureArtifactCompletion metadataArtifact)
        {
            if (!workToken.IsValid)
            {
                throw new ArgumentException("Work token must be valid.", nameof(workToken));
            }

            WorkToken = workToken;
            FrameCompletion = frameCompletion;
            ImageArtifact = imageArtifact;
            MetadataArtifact = metadataArtifact;
        }
    }
}
