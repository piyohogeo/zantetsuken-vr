using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON serializer for <see cref="CapturePublicationPlan"/>.
    /// The canonical form is byte-for-byte deterministic: no BOM, no trailing
    /// newline, no extra whitespace, PascalCase property order per the Schema
    /// v1 contract, invariant shortest decimal integers, and literal ASCII
    /// strings without escape representations.
    /// </summary>
    /// <remarks>
    /// This codec performs no file I/O, hash recomputation, clock, random, or
    /// Unity static API access, re-sorts no entry, and mutates no input. The
    /// 16 MiB output limit is monitored while entries are appended so an
    /// oversized plan fails before the whole document is materialized.
    /// </remarks>
    internal static class CapturePublicationPlanCodec
    {
        internal const int MaximumCanonicalByteCount = 16 * 1024 * 1024;

        private const int SchemaMaximumEntryCount = 100000;
        private const int SchemaMaximumPathByteCount = 512;
        private const int RunInitializationIdByteCount = 32;
        private const int Sha256HexByteCount = 64;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CapturePublicationPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"SchemaVersion\":");
            AppendLong(sb, plan.SchemaVersion);
            sb.Append(",\"TestRunId\":");
            AppendLong(sb, plan.TestRunId);
            sb.Append(",\"RunInitializationId\":");
            AppendLiteral(sb, plan.RunInitializationId);
            sb.Append(",\"RunManifestContentSha256\":");
            AppendLiteral(sb, plan.RunManifestContentSha256);
            sb.Append(",\"EntryCount\":");
            AppendLong(sb, plan.EntryCount);
            sb.Append(",\"Entries\":[");

            for (int i = 0; i < plan.EntryCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendEntry(sb, plan.GetEntry(i));

                // Every serialized value is validated literal ASCII, so the
                // UTF-16 length equals the UTF-8 byte length. Monitor the limit
                // while writing instead of only after the document is built.
                if (sb.Length > MaximumCanonicalByteCount)
                {
                    throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
                }
            }

            sb.Append("]}");

            if (sb.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Decodes canonical Plan bytes into a plan, enforcing the caller
        /// limits before any content is inspected and re-validating every value
        /// through the existing constructors.
        /// </summary>
        internal static CapturePublicationPlan DeserializeCanonical(
            byte[] utf8Json,
            int maxPlanBytes,
            int maxEntryCount,
            int maxPathBytes)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException(nameof(utf8Json));
            }

            ValidateLimits(maxPlanBytes, maxEntryCount, maxPathBytes);

            if (utf8Json.Length > maxPlanBytes)
            {
                throw new InvalidDataException("Plan bytes exceed the caller limit.");
            }

            return Decode(utf8Json, maxEntryCount, maxPathBytes);
        }

        /// <summary>
        /// Decodes canonical Plan bytes from a stream, enforcing the caller
        /// limits before reading and never taking ownership of the stream.
        /// Seekable streams are length-checked before reading and then probed
        /// one extra byte to catch growth; non-seekable streams are read into
        /// a <c>maxPlanBytes</c> document buffer with a separate one-byte
        /// overflow probe that never joins the document.
        /// </summary>
        internal static CapturePublicationPlan DeserializeCanonical(
            Stream input,
            int maxPlanBytes,
            int maxEntryCount,
            int maxPathBytes)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            ValidateLimits(maxPlanBytes, maxEntryCount, maxPathBytes);

            byte[] document = ReadBoundedStream(input, maxPlanBytes);
            return Decode(document, maxEntryCount, maxPathBytes);
        }

        private static void ValidateLimits(int maxPlanBytes, int maxEntryCount, int maxPathBytes)
        {
            if (maxPlanBytes < 1 || maxPlanBytes > MaximumCanonicalByteCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlanBytes), maxPlanBytes, "maxPlanBytes must be between 1 and 16 MiB.");
            }

            if (maxEntryCount < 0 || maxEntryCount > SchemaMaximumEntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntryCount), maxEntryCount, "maxEntryCount must be between 0 and 100000.");
            }

            if (maxPathBytes < 1 || maxPathBytes > SchemaMaximumPathByteCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPathBytes), maxPathBytes, "maxPathBytes must be between 1 and 512.");
            }
        }

        private static byte[] ReadBoundedStream(Stream input, int maxPlanBytes)
        {
            if (input.CanSeek)
            {
                long remaining = input.Length - input.Position;
                if (remaining > maxPlanBytes)
                {
                    throw new InvalidDataException("Plan bytes exceed the caller limit.");
                }

                if (remaining < 0)
                {
                    throw new InvalidDataException("Stream position is beyond its length.");
                }

                byte[] document = new byte[(int)remaining];
                int total = 0;
                while (total < document.Length)
                {
                    int read = input.Read(document, total, document.Length - total);
                    if (read == 0)
                    {
                        throw new InvalidDataException("Stream ended before its reported length.");
                    }

                    total += read;
                }

                // The stream may have grown after Length was first observed;
                // probe one byte to ensure no trailing data was missed.
                int probe = input.ReadByte();
                if (probe != -1)
                {
                    throw new InvalidDataException("Plan bytes exceed the caller limit.");
                }

                return document;
            }

            // Non-seekable: fill a document buffer of at most maxPlanBytes,
            // then probe one extra byte. The probe byte never joins the
            // document, structurally guaranteeing the overflow byte is kept
            // separate from the accepted document.
            byte[] buffer = new byte[maxPlanBytes];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = input.Read(buffer, count, buffer.Length - count);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            int extra = input.ReadByte();
            if (extra != -1)
            {
                throw new InvalidDataException("Plan bytes exceed the caller limit.");
            }

            byte[] result = new byte[count];
            Array.Copy(buffer, 0, result, 0, count);
            return result;
        }

        private static CapturePublicationPlan Decode(byte[] document, int maxEntryCount, int maxPathBytes)
        {
            if (document.Length == 0)
            {
                throw new InvalidDataException("Canonical JSON must not be empty.");
            }

            if (document.Length >= 3 && document[0] == 0xEF && document[1] == 0xBB && document[2] == 0xBF)
            {
                throw new InvalidDataException("Canonical JSON must not have a UTF-8 BOM.");
            }

            CanonicalReader reader = new CanonicalReader(document);

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
            string runInitializationId = reader.ReadString(RunInitializationIdByteCount);

            reader.Expect((byte)',');
            reader.Expect("\"RunManifestContentSha256\"");
            reader.Expect((byte)':');
            string runManifestContentSha256 = reader.ReadString(Sha256HexByteCount);

            reader.Expect((byte)',');
            reader.Expect("\"EntryCount\"");
            reader.Expect((byte)':');
            long entryCount = reader.ReadInteger();
            if (entryCount > maxEntryCount || entryCount > SchemaMaximumEntryCount)
            {
                throw new InvalidDataException("Entry count exceeds the limit.");
            }

            int count = (int)entryCount;

            reader.Expect((byte)',');
            reader.Expect("\"Entries\"");
            reader.Expect((byte)':');
            reader.Expect((byte)'[');

            CapturePublicationPlanEntry[] entries = new CapturePublicationPlanEntry[count];
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    reader.Expect((byte)',');
                }

                entries[i] = DecodeEntry(reader, maxPathBytes);
            }

            reader.Expect((byte)']');
            reader.Expect((byte)'}');
            reader.ExpectEnd();

            CapturePublicationPlan plan;
            try
            {
                plan = new CapturePublicationPlan(testRunId, runInitializationId, runManifestContentSha256, entries);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Plan values are invalid.", ex);
            }

            byte[] canonical;
            try
            {
                canonical = SerializeCanonical(plan);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Decoded plan cannot be represented within the canonical size limit.", ex);
            }

            if (!BytesEqual(canonical, document))
            {
                throw new InvalidDataException("Canonical JSON is not in canonical form.");
            }

            return plan;
        }

        private static CapturePublicationPlanEntry DecodeEntry(CanonicalReader reader, int maxPathBytes)
        {
            reader.Expect((byte)'{');
            reader.Expect("\"CaptureFrameId\"");
            reader.Expect((byte)':');
            long captureFrameId = reader.ReadInteger();

            reader.Expect((byte)',');
            reader.Expect("\"PngStagingRelativePath\"");
            reader.Expect((byte)':');
            string pngStaging = reader.ReadString(maxPathBytes);

            reader.Expect((byte)',');
            reader.Expect("\"SidecarStagingRelativePath\"");
            reader.Expect((byte)':');
            string sidecarStaging = reader.ReadString(maxPathBytes);

            reader.Expect((byte)',');
            reader.Expect("\"PngFinalRelativePath\"");
            reader.Expect((byte)':');
            string pngFinal = reader.ReadString(maxPathBytes);

            reader.Expect((byte)',');
            reader.Expect("\"SidecarFinalRelativePath\"");
            reader.Expect((byte)':');
            string sidecarFinal = reader.ReadString(maxPathBytes);

            reader.Expect((byte)',');
            reader.Expect("\"PngByteLength\"");
            reader.Expect((byte)':');
            long pngByteLength = reader.ReadInteger();

            reader.Expect((byte)',');
            reader.Expect("\"SidecarByteLength\"");
            reader.Expect((byte)':');
            long sidecarByteLength = reader.ReadInteger();

            reader.Expect((byte)',');
            reader.Expect("\"PngContentSha256\"");
            reader.Expect((byte)':');
            string pngContentSha256 = reader.ReadString(Sha256HexByteCount);

            reader.Expect((byte)',');
            reader.Expect("\"SidecarContentSha256\"");
            reader.Expect((byte)':');
            string sidecarContentSha256 = reader.ReadString(Sha256HexByteCount);

            reader.Expect((byte)'}');

            try
            {
                return new CapturePublicationPlanEntry(
                    captureFrameId,
                    pngStaging,
                    sidecarStaging,
                    pngFinal,
                    sidecarFinal,
                    pngByteLength,
                    sidecarByteLength,
                    pngContentSha256,
                    sidecarContentSha256);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Plan entry values are invalid.", ex);
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void AppendEntry(StringBuilder sb, CapturePublicationPlanEntry entry)
        {
            sb.Append("{\"CaptureFrameId\":");
            AppendLong(sb, entry.CaptureFrameId);
            sb.Append(",\"PngStagingRelativePath\":");
            AppendLiteral(sb, entry.PngStagingRelativePath);
            sb.Append(",\"SidecarStagingRelativePath\":");
            AppendLiteral(sb, entry.SidecarStagingRelativePath);
            sb.Append(",\"PngFinalRelativePath\":");
            AppendLiteral(sb, entry.PngFinalRelativePath);
            sb.Append(",\"SidecarFinalRelativePath\":");
            AppendLiteral(sb, entry.SidecarFinalRelativePath);
            sb.Append(",\"PngByteLength\":");
            AppendLong(sb, entry.PngByteLength);
            sb.Append(",\"SidecarByteLength\":");
            AppendLong(sb, entry.SidecarByteLength);
            sb.Append(",\"PngContentSha256\":");
            AppendLiteral(sb, entry.PngContentSha256);
            sb.Append(",\"SidecarContentSha256\":");
            AppendLiteral(sb, entry.SidecarContentSha256);
            sb.Append('}');
        }

        private static void AppendLong(StringBuilder sb, long value)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendLiteral(StringBuilder sb, string value)
        {
            // Values are validated literal ASCII (fixed paths, lowercase hex
            // hashes and initialization ID), so no JSON escaping is generated.
            sb.Append('"');
            sb.Append(value);
            sb.Append('"');
        }

        /// <summary>
        /// Strict byte-level parser over the canonical document. The schema is
        /// fixed, so property names, order, token kinds, and delimiters are
        /// matched exactly; any deviation fails closed.
        /// </summary>
        private sealed class CanonicalReader
        {
            private readonly byte[] _bytes;
            private int _position;

            internal CanonicalReader(byte[] bytes)
            {
                _bytes = bytes;
                _position = 0;
            }

            internal void Expect(byte expected)
            {
                if (_position >= _bytes.Length || _bytes[_position] != expected)
                {
                    throw new InvalidDataException("Unexpected byte; expected '" + (char)expected + "'.");
                }

                _position++;
            }

            internal void Expect(string literal)
            {
                for (int i = 0; i < literal.Length; i++)
                {
                    Expect((byte)literal[i]);
                }
            }

            internal long ReadInteger()
            {
                if (_position >= _bytes.Length)
                {
                    throw new InvalidDataException("Expected a digit.");
                }

                byte first = _bytes[_position];
                if (first < (byte)'0' || first > (byte)'9')
                {
                    throw new InvalidDataException("Expected a digit.");
                }

                if (first == (byte)'0')
                {
                    _position++;
                    if (_position < _bytes.Length && _bytes[_position] >= (byte)'0' && _bytes[_position] <= (byte)'9')
                    {
                        throw new InvalidDataException("Leading zeros are not canonical.");
                    }

                    return 0;
                }

                long value = 0;
                while (_position < _bytes.Length)
                {
                    byte b = _bytes[_position];
                    if (b < (byte)'0' || b > (byte)'9')
                    {
                        break;
                    }

                    int digit = b - '0';
                    if (value > (long.MaxValue - digit) / 10)
                    {
                        throw new InvalidDataException("Integer overflows Int64.");
                    }

                    value = value * 10 + digit;
                    _position++;
                }

                return value;
            }

            internal string ReadString(int maximumByteCount)
            {
                Expect((byte)'"');

                int start = _position;
                while (true)
                {
                    if (_position >= _bytes.Length)
                    {
                        throw new InvalidDataException("Unterminated string.");
                    }

                    byte b = _bytes[_position];
                    if (b == (byte)'"')
                    {
                        break;
                    }

                    // Enforce the limit while scanning so no char[] or string
                    // is allocated for an oversized value.
                    if (_position - start >= maximumByteCount)
                    {
                        throw new InvalidDataException("String exceeds the maximum allowed byte count.");
                    }

                    if (b < 0x20 || b > 0x7E)
                    {
                        throw new InvalidDataException("Strings must contain only literal printable ASCII.");
                    }

                    _position++;
                }

                int length = _position - start;
                string value = BuildString(_bytes, start, length);
                _position++; // closing quote
                return value;
            }

            internal void ExpectEnd()
            {
                if (_position != _bytes.Length)
                {
                    throw new InvalidDataException("Trailing data after the document.");
                }
            }

            private static string BuildString(byte[] bytes, int start, int length)
            {
                char[] chars = new char[length];
                for (int i = 0; i < length; i++)
                {
                    chars[i] = (char)bytes[start + i];
                }

                return new string(chars);
            }
        }
    }
}
