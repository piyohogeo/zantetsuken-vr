using System;
using System.Security.Cryptography;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Creates an owned <see cref="CaptureFramePngStagingEntry"/> from a caller
    /// request and a caller-owned PNG byte array, transferring PNG ownership
    /// only on success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation and hashing run in a fixed order: request validity, positive
    /// test run ID, positive capture frame ID, a created PNG, a PNG longer than
    /// 8 bytes, the content SHA-256, entry construction, and only then the
    /// caller's byte variable is cleared. If any step before entry construction
    /// throws, the caller's byte array is unchanged and stays caller-owned.
    /// </para>
    /// <para>
    /// The SHA-256 is computed incrementally through a fixed managed chunk
    /// buffer and <see cref="IncrementalHash"/>; no full PNG-sized managed copy
    /// or <see cref="NativeArray{T}.ToArray"/> is used, and the input PNG is
    /// never modified.
    /// </para>
    /// <para>
    /// A single instance is not safe for concurrent or re-entrant use. The
    /// factory holds no PNG bytes and no created entry, and never disposes
    /// them. It is internal, not a MonoBehaviour or ScriptableObject, and
    /// performs no file I/O or trace recording.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFramePngStagingEntryFactory
    {
        private readonly byte[] _copyBuffer;

        internal CaptureFramePngStagingEntryFactory(int copyBufferSize = 65536)
        {
            if (copyBufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(copyBufferSize), copyBufferSize, "Copy buffer size must be greater than zero.");
            }

            _copyBuffer = new byte[copyBufferSize];
        }

        public int CopyBufferSize => _copyBuffer.Length;

        internal CaptureFramePngStagingEntry Create(
            in CaptureFrameRequest request,
            ref NativeArray<byte> pngBytes)
        {
            // 1. Request validity.
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            long testRunId = request.TraceContext.TestRunId;

            // 2. Test run ID must be positive.
            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), testRunId, "Test run ID must be greater than zero.");
            }

            long captureFrameId = request.TraceContext.CaptureFrameId;

            // 3. Capture frame ID must be positive.
            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            // 4. PNG must be created.
            if (!pngBytes.IsCreated)
            {
                throw new ArgumentException("PNG bytes must be created.", nameof(pngBytes));
            }

            // 5. PNG must be longer than 8 bytes.
            if (pngBytes.Length <= 8)
            {
                throw new ArgumentException("PNG bytes must be longer than 8 bytes.", nameof(pngBytes));
            }

            // 6. Compute the content SHA-256 without mutating the input.
            string contentSha256 = ComputeSha256(pngBytes);

            // 7. Construct the entry; it takes ownership only here.
            CaptureFramePngStagingEntry entry = new CaptureFramePngStagingEntry(
                testRunId,
                captureFrameId,
                pngBytes,
                contentSha256);

            // 8. Ownership transferred: clear the caller's variable.
            pngBytes = default;

            return entry;
        }

        private string ComputeSha256(NativeArray<byte> pngBytes)
        {
            using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                int offset = 0;
                while (offset < pngBytes.Length)
                {
                    int chunkLength = pngBytes.Length - offset;
                    if (chunkLength > _copyBuffer.Length)
                    {
                        chunkLength = _copyBuffer.Length;
                    }

                    NativeArray<byte>.Copy(pngBytes, offset, _copyBuffer, 0, chunkLength);
                    hasher.AppendData(_copyBuffer, 0, chunkLength);
                    offset += chunkLength;
                }

                return CaptureFramePngSaveReceipt.ToLowerHex(hasher.GetHashAndReset());
            }
        }
    }
}
