using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Internal-only holder that owns an encoded PNG's
    /// <see cref="NativeArray{T}"/> bytes until the final manifest exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry takes exclusive ownership of the PNG bytes at construction and
    /// releases them exactly once on <see cref="Dispose"/>, which is safe to
    /// call more than once. The metadata (<see cref="TestRunId"/>,
    /// <see cref="CaptureFrameId"/>, <see cref="ByteCount"/>, and
    /// <see cref="ContentSha256"/>) is fixed at construction and remains
    /// readable after disposal.
    /// </para>
    /// <para>
    /// <see cref="GetPngBytes"/> returns a non-owning view into the held
    /// allocation; the caller must not modify, dispose, or retain that view
    /// beyond this entry's lifetime.
    /// </para>
    /// <para>
    /// The entry holds no <see cref="CaptureFrameRequest"/>,
    /// <see cref="CaptureFrameDraft"/>, run context, manifest, destination path,
    /// receipt, or logger reference, and performs no file I/O or trace
    /// recording. It is for the main thread only and is not a MonoBehaviour or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFramePngStagingEntry : IDisposable
    {
        private readonly long _testRunId;
        private readonly long _captureFrameId;
        private readonly int _byteCount;
        private readonly string _contentSha256;
        private NativeArray<byte> _pngBytes;
        private bool _disposed;

        internal CaptureFramePngStagingEntry(
            long testRunId,
            long captureFrameId,
            NativeArray<byte> pngBytes,
            string contentSha256)
        {
            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (!pngBytes.IsCreated)
            {
                throw new ArgumentException("PNG bytes must be created.", nameof(pngBytes));
            }

            if (pngBytes.Length <= 8)
            {
                throw new ArgumentException("PNG bytes must be longer than 8 bytes.", nameof(pngBytes));
            }

            if (contentSha256 == null)
            {
                throw new ArgumentNullException(nameof(contentSha256));
            }

            if (!IsLowercaseHex(contentSha256))
            {
                throw new ArgumentException("Content SHA-256 must be 64 lowercase hexadecimal characters.", nameof(contentSha256));
            }

            _testRunId = testRunId;
            _captureFrameId = captureFrameId;
            _byteCount = pngBytes.Length;
            _contentSha256 = contentSha256;
            _pngBytes = pngBytes;
        }

        public long TestRunId => _testRunId;

        public long CaptureFrameId => _captureFrameId;

        public int ByteCount => _byteCount;

        public string ContentSha256 => _contentSha256;

        public bool IsCreated => !_disposed;

        /// <summary>
        /// Returns a non-owning view of the held PNG bytes. The caller must not
        /// modify, dispose, or retain the returned array beyond this entry's
        /// lifetime. Throws <see cref="ObjectDisposedException"/> after
        /// <see cref="Dispose"/>.
        /// </summary>
        internal NativeArray<byte> GetPngBytes()
        {
            ThrowIfDisposed();
            return _pngBytes;
        }

        /// <summary>
        /// Releases the held PNG bytes exactly once. Safe to call repeatedly.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_pngBytes.IsCreated)
            {
                _pngBytes.Dispose();
            }

            _pngBytes = default;
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        private static bool IsLowercaseHex(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
