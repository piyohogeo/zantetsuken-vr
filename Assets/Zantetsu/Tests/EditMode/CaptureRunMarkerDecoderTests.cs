using System;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunMarkerDecoderTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private const int MaxMarker = 4 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private const string InitStagingJson =
            "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"0123456789abcdef0123456789abcdef\"," +
            "\"RootRole\":\"Staging\",\"StagingRunRootSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"," +
            "\"FinalRunRootSha256\":\"fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210\"}";

        private const string InitFinalJson =
            "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"0123456789abcdef0123456789abcdef\"," +
            "\"RootRole\":\"Final\",\"StagingRunRootSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"," +
            "\"FinalRunRootSha256\":\"fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210\"}";

        private const string ReadyJson =
            "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"0123456789abcdef0123456789abcdef\"," +
            "\"StagingInitSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"," +
            "\"FinalInitSha256\":\"fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210\"}";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetInitCodecType() => GetTypeFromAssembly("CaptureRunInitializationMarkerCodec");

        private static Type GetReadyCodecType() => GetTypeFromAssembly("CaptureRunReadyMarkerCodec");

        private static Type GetRoleType() => GetTypeFromAssembly("CaptureRunRootRole");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        private static byte[] JsonBytes(string json) => Utf8NoBom.GetBytes(json);

        private static string Json(byte[] bytes) => Utf8NoBom.GetString(bytes);

        private static byte[] Mutate(byte[] canonical, Func<string, string> transform) => JsonBytes(transform(Json(canonical)));

        // ---- Codec invocation ----

        private static byte[] SerializeInit(object marker)
        {
            MethodInfo m = GetInitCodecType().GetMethod("SerializeCanonical", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(m, Is.Not.Null);
            return (byte[])m.Invoke(null, new object[] { marker });
        }

        private static byte[] SerializeReady(object marker)
        {
            MethodInfo m = GetReadyCodecType().GetMethod("SerializeCanonical", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(m, Is.Not.Null);
            return (byte[])m.Invoke(null, new object[] { marker });
        }

        private static object DeserializeInitBytes(byte[] json, int maxMarkerBytes = MaxMarker)
        {
            MethodInfo m = GetInitCodecType().GetMethod("DeserializeCanonical", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(byte[]), typeof(int) }, null);
            Assert.That(m, Is.Not.Null, "byte[] DeserializeCanonical not found (init).");
            return m.Invoke(null, new object[] { json, maxMarkerBytes });
        }

        private static object DeserializeReadyBytes(byte[] json, int maxMarkerBytes = MaxMarker)
        {
            MethodInfo m = GetReadyCodecType().GetMethod("DeserializeCanonical", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(byte[]), typeof(int) }, null);
            Assert.That(m, Is.Not.Null, "byte[] DeserializeCanonical not found (ready).");
            return m.Invoke(null, new object[] { json, maxMarkerBytes });
        }

        private static object DeserializeInitStream(Stream input, int maxMarkerBytes = MaxMarker)
        {
            MethodInfo m = GetInitCodecType().GetMethod("DeserializeCanonical", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Stream), typeof(int) }, null);
            Assert.That(m, Is.Not.Null, "Stream DeserializeCanonical not found (init).");
            return m.Invoke(null, new object[] { input, maxMarkerBytes });
        }

        private static object DeserializeReadyStream(Stream input, int maxMarkerBytes = MaxMarker)
        {
            MethodInfo m = GetReadyCodecType().GetMethod("DeserializeCanonical", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Stream), typeof(int) }, null);
            Assert.That(m, Is.Not.Null, "Stream DeserializeCanonical not found (ready).");
            return m.Invoke(null, new object[] { input, maxMarkerBytes });
        }

        private static Exception DeserializeInitBytesException(byte[] json, int maxMarkerBytes = MaxMarker)
        {
            try
            {
                DeserializeInitBytes(json, maxMarkerBytes);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static Exception DeserializeReadyBytesException(byte[] json, int maxMarkerBytes = MaxMarker)
        {
            try
            {
                DeserializeReadyBytes(json, maxMarkerBytes);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static Exception DeserializeInitStreamException(Stream input, int maxMarkerBytes = MaxMarker)
        {
            try
            {
                DeserializeInitStream(input, maxMarkerBytes);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static Exception DeserializeReadyStreamException(Stream input, int maxMarkerBytes = MaxMarker)
        {
            try
            {
                DeserializeReadyStream(input, maxMarkerBytes);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static byte[] WithInvalidUtf8InInitId(byte[] invalidBytes)
        {
            byte[] canonical = JsonBytes(InitStagingJson);
            byte[] marker = Utf8NoBom.GetBytes("\"RunInitializationId\":\"");
            int index = IndexOfBytes(canonical, marker);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "RunInitializationId marker not found.");

            int valueStart = index + marker.Length;
            byte[] result = (byte[])canonical.Clone();
            for (int i = 0; i < invalidBytes.Length; i++)
            {
                result[valueStart + i] = invalidBytes[i];
            }

            return result;
        }

        // ---- Stream stubs ----

        private sealed class TrackDisposeStream : Stream
        {
            private readonly Stream _inner;
            public bool Disposed { get; private set; }

            public TrackDisposeStream(Stream inner) { _inner = inner; }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private sealed class NonSeekableStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public NonSeekableStream(byte[] data) { _data = data; _position = 0; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int available = _data.Length - _position;
                int n = Math.Min(count, available);
                if (n <= 0)
                {
                    return 0;
                }

                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // A seekable stream whose Length hides the trailing byte, modelling a
        // stream that grows after the caller observed Length.
        private sealed class GrowingSeekableStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public GrowingSeekableStream(byte[] data) { _data = data; _position = 0; }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _data.Length - 1;

            public override long Position
            {
                get => _position;
                set => _position = checked((int)value);
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int available = _data.Length - _position;
                int n = Math.Min(count, available);
                if (n <= 0)
                {
                    return 0;
                }

                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target;
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        target = offset;
                        break;
                    case SeekOrigin.Current:
                        target = _position + offset;
                        break;
                    case SeekOrigin.End:
                        target = Length + offset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(origin));
                }

                _position = checked((int)target);
                return _position;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ---- Round-trip ----

        [Test]
        public void Roundtrip_Bytes()
        {
            byte[] init = JsonBytes(InitStagingJson);
            Assert.That(SerializeInit(DeserializeInitBytes(init)), Is.EqualTo(init));

            byte[] final = JsonBytes(InitFinalJson);
            Assert.That(SerializeInit(DeserializeInitBytes(final)), Is.EqualTo(final));

            byte[] ready = JsonBytes(ReadyJson);
            Assert.That(SerializeReady(DeserializeReadyBytes(ready)), Is.EqualTo(ready));
        }

        [Test]
        public void Roundtrip_Stream_SeekableAndNonSeekable()
        {
            byte[] init = JsonBytes(InitStagingJson);
            Assert.That(SerializeInit(DeserializeInitStream(new MemoryStream(init))), Is.EqualTo(init));
            Assert.That(SerializeInit(DeserializeInitStream(new NonSeekableStream(init))), Is.EqualTo(init));

            byte[] ready = JsonBytes(ReadyJson);
            Assert.That(SerializeReady(DeserializeReadyStream(new MemoryStream(ready))), Is.EqualTo(ready));
            Assert.That(SerializeReady(DeserializeReadyStream(new NonSeekableStream(ready))), Is.EqualTo(ready));
        }

        [Test]
        public void Roundtrip_Seekable_NonZeroPosition()
        {
            byte[] init = JsonBytes(InitStagingJson);
            byte[] prefixed = new byte[init.Length + 5];
            prefixed[0] = (byte)'x';
            prefixed[1] = (byte)'y';
            prefixed[2] = (byte)'z';
            prefixed[3] = (byte)'!';
            prefixed[4] = (byte)'?';
            Array.Copy(init, 0, prefixed, 5, init.Length);

            MemoryStream ms = new MemoryStream(prefixed);
            ms.Position = 5;

            Assert.That(SerializeInit(DeserializeInitStream(ms)), Is.EqualTo(init));
        }

        [Test]
        public void Stream_NotDisposed_Success()
        {
            byte[] init = JsonBytes(InitStagingJson);

            TrackDisposeStream tracking = new TrackDisposeStream(new MemoryStream(init));
            DeserializeInitStream(tracking);
            Assert.That(tracking.Disposed, Is.False);
        }

        [Test]
        public void Stream_NotDisposed_Failure()
        {
            TrackDisposeStream tracking = new TrackDisposeStream(new MemoryStream(JsonBytes("not json")));
            Assert.That(DeserializeInitStreamException(tracking), Is.TypeOf<InvalidDataException>());
            Assert.That(tracking.Disposed, Is.False);
        }

        // ---- Limits ----

        [Test]
        public void NullInput_ParamName()
        {
            Exception initBytesNull = DeserializeInitBytesException(null);
            Assert.That(initBytesNull, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)initBytesNull).ParamName, Is.EqualTo("utf8Json"));

            Exception readyBytesNull = DeserializeReadyBytesException(null);
            Assert.That(readyBytesNull, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)readyBytesNull).ParamName, Is.EqualTo("utf8Json"));

            Exception initStreamNull = DeserializeInitStreamException(null);
            Assert.That(initStreamNull, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)initStreamNull).ParamName, Is.EqualTo("input"));

            Exception readyStreamNull = DeserializeReadyStreamException(null);
            Assert.That(readyStreamNull, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)readyStreamNull).ParamName, Is.EqualTo("input"));
        }

        [Test]
        public void MaxMarkerBytes_Invalid_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);
            foreach (int bad in new[] { 0, -1, 4097 })
            {
                Exception ex = DeserializeInitBytesException(init, bad);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("maxMarkerBytes"));
            }

            Exception readyEx = DeserializeReadyBytesException(JsonBytes(ReadyJson), 0);
            Assert.That(readyEx, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)readyEx).ParamName, Is.EqualTo("maxMarkerBytes"));
        }

        [Test]
        public void MaxMarkerBytes_ExactAccepted_NextRejected()
        {
            byte[] init = JsonBytes(InitStagingJson);
            Assert.That(DeserializeInitBytesException(init, init.Length), Is.Null);
            Assert.That(DeserializeInitBytesException(init, init.Length - 1), Is.TypeOf<InvalidDataException>());

            byte[] ready = JsonBytes(ReadyJson);
            Assert.That(DeserializeReadyBytesException(ready, ready.Length), Is.Null);
            Assert.That(DeserializeReadyBytesException(ready, ready.Length - 1), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Seekable_GrowsAfterLength_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);
            byte[] withExtra = new byte[init.Length + 1];
            Array.Copy(init, withExtra, init.Length);
            withExtra[init.Length] = (byte)' ';

            Assert.That(DeserializeInitStreamException(new GrowingSeekableStream(withExtra)), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void NonSeekable_LimitPlusOne_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            Assert.That(DeserializeInitStreamException(new NonSeekableStream(init), init.Length - 1), Is.TypeOf<InvalidDataException>());
        }

        // ---- Structural rejections ----

        [Test]
        public void Bom_Empty_Truncated_Rejected()
        {
            Assert.That(DeserializeInitBytesException(new byte[0]), Is.TypeOf<InvalidDataException>());

            byte[] init = JsonBytes(InitStagingJson);

            byte[] withBom = new byte[init.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Array.Copy(init, 0, withBom, 3, init.Length);
            Assert.That(DeserializeInitBytesException(withBom), Is.TypeOf<InvalidDataException>());

            for (int len = 1; len < init.Length; len++)
            {
                byte[] truncated = new byte[len];
                Array.Copy(init, truncated, len);
                Assert.That(DeserializeInitBytesException(truncated), Is.TypeOf<InvalidDataException>(), "Expected rejection at length " + len);
            }
        }

        [Test]
        public void TrailingData_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            Assert.That(DeserializeInitBytesException(Mutate(init, s => s + "\n")), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s + " ")), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s + "0")), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Whitespace_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace(":", ": "))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace(",", " ,"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void PropertyOrder_Unknown_Missing_Duplicate_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"SchemaVersion\":1,\"TestRunId\":1", "\"TestRunId\":1,\"SchemaVersion\":1"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace(",\"RootRole\"", ",\"Foo\":\"x\",\"RootRole\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace(",\"TestRunId\":1", ""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace(",\"TestRunId\":1", ",\"TestRunId\":1,\"TestRunId\":1"))), Is.TypeOf<InvalidDataException>());

            byte[] ready = JsonBytes(ReadyJson);
            Assert.That(DeserializeReadyBytesException(Mutate(ready, s => s.Replace(",\"StagingInitSha256\"", ",\"Foo\":\"x\",\"StagingInitSha256\""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void SchemaVersionMismatch_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"))), Is.TypeOf<InvalidDataException>());

            byte[] ready = JsonBytes(ReadyJson);
            Assert.That(DeserializeReadyBytesException(Mutate(ready, s => s.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void IntegerNonCanonical_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":1.0"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":1e1"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":-1"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":+1"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":01"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Escape_NonAscii_InvalidUtf8_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            // Escaped quote inside a string.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + InitId + "\"", "\"abc\\\"def\""))), Is.TypeOf<InvalidDataException>());

            // Valid UTF-8 multi-byte character (still non-literal ASCII).
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + InitId + "\"", "\"0123456789abcdef0123456789abcde\u00e9\""))), Is.TypeOf<InvalidDataException>());

            // Invalid continuation byte.
            Assert.That(DeserializeInitBytesException(WithInvalidUtf8InInitId(new byte[] { 0xC3, 0x28 })), Is.TypeOf<InvalidDataException>());

            // Truncated multi-byte sequence.
            Assert.That(DeserializeInitBytesException(WithInvalidUtf8InInitId(new byte[] { 0xE2, 0x82 })), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void IdAndHash_LengthUpperNonHex_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            // RunInitializationId length / uppercase / non-hex.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + InitId + "\"", "\"" + new string('0', 31) + "\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + InitId + "\"", "\"" + new string('0', 33) + "\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + InitId + "\"", "\"" + new string('A', 32) + "\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + InitId + "\"", "\"" + new string('g', 32) + "\""))), Is.TypeOf<InvalidDataException>());

            // StagingRunRootSha256 length / uppercase / non-hex.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + StagingHash + "\"", "\"" + new string('0', 63) + "\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + StagingHash + "\"", "\"" + new string('0', 65) + "\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + StagingHash + "\"", "\"" + new string('A', 64) + "\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"" + StagingHash + "\"", "\"" + new string('g', 64) + "\""))), Is.TypeOf<InvalidDataException>());
        }

        // ---- RootRole specifics ----

        [Test]
        public void Init_RootRole_StagingAndFinal()
        {
            byte[] staging = JsonBytes(InitStagingJson);
            object stagingMarker = DeserializeInitBytes(staging);
            Assert.That(SerializeInit(stagingMarker), Is.EqualTo(staging));
            Assert.That(GetProperty(stagingMarker, "RootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Staging")));

            byte[] final = JsonBytes(InitFinalJson);
            object finalMarker = DeserializeInitBytes(final);
            Assert.That(SerializeInit(finalMarker), Is.EqualTo(final));
            Assert.That(GetProperty(finalMarker, "RootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Final")));
        }

        [Test]
        public void Init_RootRole_Invalid_Rejected()
        {
            byte[] init = JsonBytes(InitStagingJson);

            // Numeric role.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"RootRole\":\"Staging\"", "\"RootRole\":1"))), Is.TypeOf<InvalidDataException>());

            // Unknown role.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"RootRole\":\"Staging\"", "\"RootRole\":\"Unknown\""))), Is.TypeOf<InvalidDataException>());

            // Wrong case.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"RootRole\":\"Staging\"", "\"RootRole\":\"staging\""))), Is.TypeOf<InvalidDataException>());

            // Longer than the 7-byte scan limit.
            Assert.That(DeserializeInitBytesException(Mutate(init, s => s.Replace("\"RootRole\":\"Staging\"", "\"RootRole\":\"StagingX\""))), Is.TypeOf<InvalidDataException>());
        }

        // ---- Exception contract ----

        [Test]
        public void ContentErrors_AreInvalidDataException()
        {
            byte[] init = JsonBytes(InitStagingJson);

            Exception bom = DeserializeInitBytesException(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' });
            Assert.That(bom, Is.TypeOf<InvalidDataException>());

            Exception unknown = DeserializeInitBytesException(Mutate(init, s => s.Replace(",\"RootRole\"", ",\"Foo\":\"x\",\"RootRole\"")));
            Assert.That(unknown, Is.TypeOf<InvalidDataException>());

            Exception role = DeserializeInitBytesException(Mutate(init, s => s.Replace("\"RootRole\":\"Staging\"", "\"RootRole\":\"Unknown\"")));
            Assert.That(role, Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void ConstructorArgumentException_KeptAsInnerException()
        {
            // A 32-character uppercase ID passes the string scan but fails the
            // marker constructor's lowercase hex validation.
            string bad = InitStagingJson.Replace("\"" + InitId + "\"", "\"" + new string('A', 32) + "\"");

            Exception ex = DeserializeInitBytesException(JsonBytes(bad));
            Assert.That(ex, Is.TypeOf<InvalidDataException>());
            Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void DoesNotMutateInputBytes()
        {
            byte[] init = JsonBytes(InitStagingJson);
            byte[] copy = (byte[])init.Clone();

            DeserializeInitBytes(init);

            Assert.That(init, Is.EqualTo(copy));
        }

        // ---- Decoder responsibilities ----

        [Test]
        public void Decoder_DoesNotBindMarkersOrComputeHashesOrTouchFilesystem()
        {
            string initCodec = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationMarkerCodec.cs"));
            Assert.That(initCodec, Does.Not.Contain("CaptureRunReadyMarker"));
            Assert.That(initCodec, Does.Not.Contain("File."));
            Assert.That(initCodec, Does.Not.Contain("Directory."));
            Assert.That(initCodec, Does.Not.Contain("FileStream"));

            string readyCodec = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunReadyMarkerCodec.cs"));
            Assert.That(readyCodec, Does.Not.Contain("CaptureRunInitializationMarker"));
            Assert.That(readyCodec, Does.Not.Contain("SHA256"));
            Assert.That(readyCodec, Does.Not.Contain("File."));
            Assert.That(readyCodec, Does.Not.Contain("Directory."));
            Assert.That(readyCodec, Does.Not.Contain("FileStream"));

            string support = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerDecoderSupport.cs"));
            Assert.That(support, Does.Not.Contain("CaptureRunInitializationMarker"));
            Assert.That(support, Does.Not.Contain("CaptureRunReadyMarker"));
            Assert.That(support, Does.Not.Contain("SHA256"));
            Assert.That(support, Does.Not.Contain("File."));
            Assert.That(support, Does.Not.Contain("Directory."));
            Assert.That(support, Does.Not.Contain("FileStream"));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerDecoderTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }
    }
}
