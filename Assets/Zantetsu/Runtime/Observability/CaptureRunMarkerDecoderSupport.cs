using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Shared bounded decoder support for the Capture Run marker codecs: a
    /// strict byte-level canonical JSON reader and bounded stream reading. This
    /// support type is marker-specific and is not a general JSON parser.
    /// </summary>
    /// <remarks>
    /// This type owns and disposes nothing, mutates no static state, and does
    /// no file I/O: the <see cref="Stream"/> passed in is read at its current
    /// <see cref="Stream.Position"/> and is never closed.
    /// </remarks>
    internal static class CaptureRunMarkerDecoderSupport
    {
        internal const int MaximumMarkerByteCount = 4 * 1024;

        internal static void ValidateMaxMarkerBytes(int maxMarkerBytes)
        {
            if (maxMarkerBytes < 1 || maxMarkerBytes > MaximumMarkerByteCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMarkerBytes), maxMarkerBytes, "maxMarkerBytes must be between 1 and 4096.");
            }
        }

        /// <summary>
        /// Validates the document preamble (non-empty, no UTF-8 BOM) shared by
        /// both marker schemas.
        /// </summary>
        internal static void ValidateDocument(byte[] document)
        {
            if (document.Length == 0)
            {
                throw new InvalidDataException("Canonical JSON must not be empty.");
            }

            if (document.Length >= 3 && document[0] == 0xEF && document[1] == 0xBB && document[2] == 0xBF)
            {
                throw new InvalidDataException("Canonical JSON must not have a UTF-8 BOM.");
            }
        }

        /// <summary>
        /// Reads a bounded document from the current stream position without
        /// taking ownership. Seekable streams are length-checked before
        /// allocation and probed one extra byte to catch growth; non-seekable
        /// streams are read into a <c>maxMarkerBytes</c> buffer with a separate
        /// one-byte overflow probe that never joins the document.
        /// </summary>
        internal static byte[] ReadBoundedStream(Stream input, int maxMarkerBytes)
        {
            if (input.CanSeek)
            {
                long remaining = input.Length - input.Position;
                if (remaining > maxMarkerBytes)
                {
                    throw new InvalidDataException("Marker bytes exceed the caller limit.");
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

                int probe = input.ReadByte();
                if (probe != -1)
                {
                    throw new InvalidDataException("Marker bytes exceed the caller limit.");
                }

                return document;
            }

            // Non-seekable: a document buffer of at most maxMarkerBytes plus a
            // separate one-byte overflow probe. The probe byte never joins the
            // document.
            byte[] buffer = new byte[maxMarkerBytes];
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
                throw new InvalidDataException("Marker bytes exceed the caller limit.");
            }

            byte[] result = new byte[count];
            Array.Copy(buffer, 0, result, 0, count);
            return result;
        }

        internal static bool BytesEqual(byte[] left, byte[] right)
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

        /// <summary>
        /// Strict byte-level parser over the canonical document. The schemas
        /// are fixed, so property names, order, token kinds, and delimiters are
        /// matched exactly; any deviation fails closed.
        /// </summary>
        internal sealed class Reader
        {
            private readonly byte[] _bytes;
            private int _position;

            internal Reader(byte[] bytes)
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
