using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, value-only entry of a Capture Publication Plan: the expected
    /// capture frame ID, its staging and final relative paths, byte lengths,
    /// and content hashes for the PNG and sidecar files. No public constructor
    /// is provided; instances are built internally from fully validated values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Relative paths are not corrected, re-separated, or normalized: the four
    /// paths must exactly equal the schema-derived fixed values
    /// <c>frames/{id}.png.stage</c>, <c>frames/{id}.json.stage</c>,
    /// <c>frames/{id}.png</c>, and <c>frames/{id}.json</c> where <c>{id}</c> is
    /// the leading-zero-free invariant shortest decimal form of
    /// <see cref="CaptureFrameId"/>. The hashes are 64 lowercase ASCII hex
    /// characters.
    /// </para>
    /// <para>
    /// This type holds only value types and validated literal strings, owns and
    /// disposes nothing, and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CapturePublicationPlanEntry
    {
        private readonly long _captureFrameId;
        private readonly string _pngStagingRelativePath;
        private readonly string _sidecarStagingRelativePath;
        private readonly string _pngFinalRelativePath;
        private readonly string _sidecarFinalRelativePath;
        private readonly long _pngByteLength;
        private readonly long _sidecarByteLength;
        private readonly string _pngContentSha256;
        private readonly string _sidecarContentSha256;

        internal CapturePublicationPlanEntry(
            long captureFrameId,
            string pngStagingRelativePath,
            string sidecarStagingRelativePath,
            string pngFinalRelativePath,
            string sidecarFinalRelativePath,
            long pngByteLength,
            long sidecarByteLength,
            string pngContentSha256,
            string sidecarContentSha256)
        {
            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (pngStagingRelativePath == null)
            {
                throw new ArgumentNullException(nameof(pngStagingRelativePath));
            }

            if (sidecarStagingRelativePath == null)
            {
                throw new ArgumentNullException(nameof(sidecarStagingRelativePath));
            }

            if (pngFinalRelativePath == null)
            {
                throw new ArgumentNullException(nameof(pngFinalRelativePath));
            }

            if (sidecarFinalRelativePath == null)
            {
                throw new ArgumentNullException(nameof(sidecarFinalRelativePath));
            }

            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            RequireExactPath(pngStagingRelativePath, "frames/" + id + ".png.stage", nameof(pngStagingRelativePath));
            RequireExactPath(sidecarStagingRelativePath, "frames/" + id + ".json.stage", nameof(sidecarStagingRelativePath));
            RequireExactPath(pngFinalRelativePath, "frames/" + id + ".png", nameof(pngFinalRelativePath));
            RequireExactPath(sidecarFinalRelativePath, "frames/" + id + ".json", nameof(sidecarFinalRelativePath));

            RequirePrintableAsciiPath(pngStagingRelativePath, nameof(pngStagingRelativePath));
            RequirePrintableAsciiPath(sidecarStagingRelativePath, nameof(sidecarStagingRelativePath));
            RequirePrintableAsciiPath(pngFinalRelativePath, nameof(pngFinalRelativePath));
            RequirePrintableAsciiPath(sidecarFinalRelativePath, nameof(sidecarFinalRelativePath));

            if (pngByteLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pngByteLength), pngByteLength, "PNG byte length must be greater than zero.");
            }

            if (sidecarByteLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sidecarByteLength), sidecarByteLength, "Sidecar byte length must be greater than zero.");
            }

            if (pngContentSha256 == null)
            {
                throw new ArgumentNullException(nameof(pngContentSha256));
            }

            if (sidecarContentSha256 == null)
            {
                throw new ArgumentNullException(nameof(sidecarContentSha256));
            }

            if (!IsLowercaseHex64(pngContentSha256))
            {
                throw new ArgumentException("PNG content SHA-256 must be 64 lowercase ASCII hex characters.", nameof(pngContentSha256));
            }

            if (!IsLowercaseHex64(sidecarContentSha256))
            {
                throw new ArgumentException("Sidecar content SHA-256 must be 64 lowercase ASCII hex characters.", nameof(sidecarContentSha256));
            }

            _captureFrameId = captureFrameId;
            _pngStagingRelativePath = pngStagingRelativePath;
            _sidecarStagingRelativePath = sidecarStagingRelativePath;
            _pngFinalRelativePath = pngFinalRelativePath;
            _sidecarFinalRelativePath = sidecarFinalRelativePath;
            _pngByteLength = pngByteLength;
            _sidecarByteLength = sidecarByteLength;
            _pngContentSha256 = pngContentSha256;
            _sidecarContentSha256 = sidecarContentSha256;
        }

        internal long CaptureFrameId => _captureFrameId;

        internal string PngStagingRelativePath => _pngStagingRelativePath;

        internal string SidecarStagingRelativePath => _sidecarStagingRelativePath;

        internal string PngFinalRelativePath => _pngFinalRelativePath;

        internal string SidecarFinalRelativePath => _sidecarFinalRelativePath;

        internal long PngByteLength => _pngByteLength;

        internal long SidecarByteLength => _sidecarByteLength;

        internal string PngContentSha256 => _pngContentSha256;

        internal string SidecarContentSha256 => _sidecarContentSha256;

        private static void RequireExactPath(string actual, string expected, string paramName)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new ArgumentException("Relative path must exactly match the schema-derived value '" + expected + "'.", paramName);
            }
        }

        private static void RequirePrintableAsciiPath(string value, string paramName)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c > 0x7E)
                {
                    throw new ArgumentException("Relative path must contain only printable ASCII characters.", paramName);
                }
            }

            if (Encoding.UTF8.GetByteCount(value) > 512)
            {
                throw new ArgumentException("Relative path must not exceed 512 UTF-8 bytes.", paramName);
            }
        }

        private static bool IsLowercaseHex64(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < 64; i++)
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
