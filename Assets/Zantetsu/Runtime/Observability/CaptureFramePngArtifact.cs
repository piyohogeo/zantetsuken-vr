using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable binding between a <see cref="CaptureFrameRecord"/> and the
    /// receipt of the PNG saved for that frame, after verifying that the saved
    /// request is fully identical to the record's request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type represents the association that was verified at save time
    /// between a frame record and a PNG receipt. It does not guarantee that the
    /// PNG file still exists: if the file is later moved or deleted, the held
    /// receipt values are unchanged.
    /// </para>
    /// <para>
    /// The artifact holds no PNG bytes and owns no file, stream, or native
    /// buffer. It does not own, delete, rename, or dispose the frame record, the
    /// receipt, or the PNG file, does not implement
    /// <see cref="IDisposable"/>, and performs no filesystem access, hash
    /// recomputation, or path normalization after construction.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifact
    {
        public CaptureFramePngArtifact(
            CaptureFrameRecord frameRecord,
            in CaptureFrameRequest savedRequest,
            CaptureFramePngSaveReceipt pngReceipt)
        {
            if (frameRecord == null)
            {
                throw new ArgumentNullException(nameof(frameRecord));
            }

            if (pngReceipt == null)
            {
                throw new ArgumentNullException(nameof(pngReceipt));
            }

            if (!savedRequest.IsValid)
            {
                throw new ArgumentException("Saved request must be valid.", nameof(savedRequest));
            }

            if (!frameRecord.Request.IdenticalTo(savedRequest))
            {
                throw new ArgumentException("Saved request must be fully identical to the frame record request.", nameof(savedRequest));
            }

            FrameRecord = frameRecord;
            PngReceipt = pngReceipt;
        }

        public CaptureFrameRecord FrameRecord { get; }

        public CaptureFramePngSaveReceipt PngReceipt { get; }

        public long CaptureFrameId => FrameRecord.CaptureFrameId;

        public string DestinationPath => PngReceipt.DestinationPath;

        public int PngByteCount => PngReceipt.ByteCount;

        public string PngContentSha256 => PngReceipt.ContentSha256;
    }
}
