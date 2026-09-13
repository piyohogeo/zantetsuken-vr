using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON serializer for
    /// <see cref="CaptureRunInitializationMarker"/>. The canonical form is
    /// byte-for-byte deterministic: no BOM, no trailing newline, no extra
    /// whitespace, fixed PascalCase property order, invariant shortest decimal
    /// integers, and literal ASCII strings without escape representations.
    /// </summary>
    /// <remarks>
    /// This codec performs no file I/O, no root hash
    /// computation, no clock, no random, and no Unity static API access.
    /// </remarks>
    internal static class CaptureRunInitializationMarkerCodec
    {
        internal const int MaximumCanonicalByteCount = 4 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CaptureRunInitializationMarker marker)
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
            sb.Append(",\"RunRootSha256\":");
            AppendLiteral(sb, marker.RunRootSha256);
            sb.Append('}');

            if (sb.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Returns the lowercase 64-character SHA-256 of the marker's canonical
        /// bytes. Nothing is cached on the marker; the hash is recomputed on
        /// every call.
        /// </summary>
        internal static string ComputeContentSha256(CaptureRunInitializationMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            byte[] canonical = SerializeCanonical(marker);
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(canonical));
            }
        }

        internal static CaptureRunInitializationMarker DeserializeCanonical(byte[] utf8Json, int maxMarkerBytes)
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

        internal static CaptureRunInitializationMarker DeserializeCanonical(Stream input, int maxMarkerBytes)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            CaptureRunMarkerDecoderSupport.ValidateMaxMarkerBytes(maxMarkerBytes);

            byte[] document = CaptureRunMarkerDecoderSupport.ReadBoundedStream(input, maxMarkerBytes);
            return Decode(document);
        }

        private static CaptureRunInitializationMarker Decode(byte[] document)
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
            reader.Expect("\"RunRootSha256\"");
            reader.Expect((byte)':');
            string runRootSha256 = reader.ReadString(64);

            reader.Expect((byte)'}');
            reader.ExpectEnd();

            CaptureRunInitializationMarker marker;
            try
            {
                marker = new CaptureRunInitializationMarker(testRunId, runInitializationId, runRootSha256);
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
            // Values are validated literal ASCII (lowercase hex and the fixed
            // role name), so no JSON escaping is generated.
            sb.Append('"');
            sb.Append(value);
            sb.Append('"');
        }

        internal static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0xF];
            }

            return new string(chars);
        }
    }
}
