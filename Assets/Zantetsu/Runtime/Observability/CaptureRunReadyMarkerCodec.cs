using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON serializer for <see cref="CaptureRunReadyMarker"/>.
    /// The canonical form is byte-for-byte deterministic: no BOM, no trailing
    /// newline, no extra whitespace, fixed PascalCase property order, invariant
    /// shortest decimal integers, and literal ASCII strings without escape
    /// representations.
    /// </summary>
    /// <remarks>
    /// This codec performs no file I/O, no hash computation, no clock, no
    /// random, and no Unity static API access.
    /// </remarks>
    internal static class CaptureRunReadyMarkerCodec
    {
        internal const int MaximumCanonicalByteCount = 4 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CaptureRunReadyMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"SchemaVersion\":");
            AppendLong(sb, marker.SchemaVersion);
            sb.Append(",\"TestRunId\":");
            AppendLong(sb, marker.TestRunId);
            sb.Append(",\"RunInitializationId\":");
            AppendLiteral(sb, marker.RunInitializationId);
            sb.Append(",\"StagingInitSha256\":");
            AppendLiteral(sb, marker.StagingInitSha256);
            sb.Append(",\"FinalInitSha256\":");
            AppendLiteral(sb, marker.FinalInitSha256);
            sb.Append('}');

            if (sb.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        internal static CaptureRunReadyMarker DeserializeCanonical(byte[] utf8Json, int maxMarkerBytes)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException(nameof(utf8Json));
            }

            CaptureRunMarkerDecoderSupport.ValidateMaxMarkerBytes(maxMarkerBytes);

            if (utf8Json.Length > maxMarkerBytes)
            {
                throw new InvalidDataException("Marker bytes exceed the caller limit.");
            }

            return Decode(utf8Json);
        }

        internal static CaptureRunReadyMarker DeserializeCanonical(Stream input, int maxMarkerBytes)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            CaptureRunMarkerDecoderSupport.ValidateMaxMarkerBytes(maxMarkerBytes);

            byte[] document = CaptureRunMarkerDecoderSupport.ReadBoundedStream(input, maxMarkerBytes);
            return Decode(document);
        }

        private static CaptureRunReadyMarker Decode(byte[] document)
        {
            CaptureRunMarkerDecoderSupport.ValidateDocument(document);

            CaptureRunMarkerDecoderSupport.Reader reader = new CaptureRunMarkerDecoderSupport.Reader(document);

            reader.Expect((byte)'{');
            reader.Expect("\"SchemaVersion\"");
            reader.Expect((byte)':');
            long schemaVersion = reader.ReadInteger();
            if (schemaVersion != 1)
            {
                throw new InvalidDataException("SchemaVersion must be 1.");
            }

            reader.Expect((byte)',');
            reader.Expect("\"TestRunId\"");
            reader.Expect((byte)':');
            long testRunId = reader.ReadInteger();

            reader.Expect((byte)',');
            reader.Expect("\"RunInitializationId\"");
            reader.Expect((byte)':');
            string runInitializationId = reader.ReadString(32);

            reader.Expect((byte)',');
            reader.Expect("\"StagingInitSha256\"");
            reader.Expect((byte)':');
            string stagingInitSha256 = reader.ReadString(64);

            reader.Expect((byte)',');
            reader.Expect("\"FinalInitSha256\"");
            reader.Expect((byte)':');
            string finalInitSha256 = reader.ReadString(64);

            reader.Expect((byte)'}');
            reader.ExpectEnd();

            CaptureRunReadyMarker marker;
            try
            {
                marker = new CaptureRunReadyMarker(testRunId, runInitializationId, stagingInitSha256, finalInitSha256);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Marker values are invalid.", ex);
            }

            byte[] canonical;
            try
            {
                canonical = SerializeCanonical(marker);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Decoded marker cannot be represented within the canonical size limit.", ex);
            }

            if (!CaptureRunMarkerDecoderSupport.BytesEqual(canonical, document))
            {
                throw new InvalidDataException("Canonical JSON is not in canonical form.");
            }

            return marker;
        }

        private static void AppendLong(StringBuilder sb, long value)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendLiteral(StringBuilder sb, string value)
        {
            // Values are validated literal ASCII (lowercase hex), so no JSON
            // escaping is generated.
            sb.Append('"');
            sb.Append(value);
            sb.Append('"');
        }
    }
}
