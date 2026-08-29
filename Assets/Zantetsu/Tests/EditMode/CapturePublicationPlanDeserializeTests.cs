using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CapturePublicationPlanDeserializeTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const int MaxPlan = 16 * 1024 * 1024;
        private const int MaxEntries = 100000;
        private const int MaxPath = 512;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetEntryType() => GetTypeFromAssembly("CapturePublicationPlanEntry");

        private static Type GetPlanType() => GetTypeFromAssembly("CapturePublicationPlan");

        private static Type GetCodecType() => GetTypeFromAssembly("CapturePublicationPlanCodec");

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

        // ---- Factories ----

        private static object MakeEntry(long captureFrameId, long pngByteLength = 16, long sidecarByteLength = 32)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            ConstructorInfo ctor = GetEntryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), typeof(string), typeof(string), typeof(string), typeof(long), typeof(long), typeof(string), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[]
            {
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                pngByteLength,
                sidecarByteLength,
                Hash64,
                Hash64
            });
        }

        private static Array MakeEntryArray(params object[] entries)
        {
            Array array = Array.CreateInstance(GetEntryType(), entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                array.SetValue(entries[i], i);
            }

            return array;
        }

        private static object MakePlan(long testRunId = 1, Array entries = null)
        {
            if (entries == null)
            {
                entries = MakeEntryArray();
            }

            ConstructorInfo ctor = GetPlanType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), typeof(string), GetEntryType().MakeArrayType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { testRunId, InitId, Hash64, entries });
        }

        private static object MakePlanWithIds(params long[] ids)
        {
            Array entries = Array.CreateInstance(GetEntryType(), ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                entries.SetValue(MakeEntry(ids[i]), i);
            }

            return MakePlan(entries: entries);
        }

        // ---- Codec helpers ----

        private static byte[] Serialize(object plan)
        {
            MethodInfo method = GetCodecType().GetMethod("SerializeCanonical", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (byte[])method.Invoke(null, new object[] { plan });
        }

        private static object DeserializeBytes(byte[] json, int maxPlanBytes = MaxPlan, int maxEntryCount = MaxEntries, int maxPathBytes = MaxPath)
        {
            MethodInfo method = GetCodecType().GetMethod(
                "DeserializeCanonical", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(byte[]), typeof(int), typeof(int), typeof(int) }, null);
            Assert.That(method, Is.Not.Null, "byte[] DeserializeCanonical not found.");
            return method.Invoke(null, new object[] { json, maxPlanBytes, maxEntryCount, maxPathBytes });
        }

        private static Exception DeserializeBytesException(byte[] json, int maxPlanBytes = MaxPlan, int maxEntryCount = MaxEntries, int maxPathBytes = MaxPath)
        {
            try
            {
                DeserializeBytes(json, maxPlanBytes, maxEntryCount, maxPathBytes);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object DeserializeStream(Stream input, int maxPlanBytes = MaxPlan, int maxEntryCount = MaxEntries, int maxPathBytes = MaxPath)
        {
            MethodInfo method = GetCodecType().GetMethod(
                "DeserializeCanonical", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(Stream), typeof(int), typeof(int), typeof(int) }, null);
            Assert.That(method, Is.Not.Null, "Stream DeserializeCanonical not found.");
            return method.Invoke(null, new object[] { input, maxPlanBytes, maxEntryCount, maxPathBytes });
        }

        private static Exception DeserializeStreamException(Stream input, int maxPlanBytes = MaxPlan, int maxEntryCount = MaxEntries, int maxPathBytes = MaxPath)
        {
            try
            {
                DeserializeStream(input, maxPlanBytes, maxEntryCount, maxPathBytes);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object GetEntry(object plan, int index)
        {
            MethodInfo method = GetPlanType().GetMethod("GetEntry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(plan, new object[] { index });
        }

        private static string Json(byte[] bytes) => Utf8NoBom.GetString(bytes);

        private static byte[] Bytes(string json) => Utf8NoBom.GetBytes(json);

        private static byte[] Mutate(byte[] canonical, Func<string, string> transform) => Bytes(transform(Json(canonical)));

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

        // Replaces the first bytes of the RunInitializationId value with the
        // supplied raw (possibly invalid UTF-8) bytes, keeping the document
        // length unchanged so the rest remains structurally parseable.
        private static byte[] WithInvalidUtf8InInitId(byte[] invalidBytes)
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));
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

        // A seekable stream whose Length hides the trailing byte. The stream
        // appears to hold N bytes when first observed, but the physical buffer
        // holds N + 1 bytes and Read will serve the hidden byte once N bytes
        // have been consumed. This deterministically models a stream that grows
        // after the caller observed Length.
        private sealed class GrowingSeekableStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public GrowingSeekableStream(byte[] data)
            {
                _data = data;
                _position = 0;
            }

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

        // ---- Roundtrip ----

        [Test]
        public void Roundtrip_EmptyPlan()
        {
            object plan = MakePlanWithIds();
            byte[] canonical = Serialize(plan);

            object restored = DeserializeBytes(canonical);

            Assert.That((int)GetProperty(restored, "SchemaVersion"), Is.EqualTo(1));
            Assert.That((long)GetProperty(restored, "TestRunId"), Is.EqualTo(1));
            Assert.That((int)GetProperty(restored, "EntryCount"), Is.EqualTo(0));
            Assert.That(Serialize(restored), Is.EqualTo(canonical));
        }

        [Test]
        public void Roundtrip_MultiEntryPlan()
        {
            object plan = MakePlanWithIds(10, 20, 30);
            byte[] canonical = Serialize(plan);

            object restored = DeserializeBytes(canonical);

            Assert.That((int)GetProperty(restored, "EntryCount"), Is.EqualTo(3));
            Assert.That((long)GetProperty(GetEntry(restored, 0), "CaptureFrameId"), Is.EqualTo(10));
            Assert.That((long)GetProperty(GetEntry(restored, 1), "CaptureFrameId"), Is.EqualTo(20));
            Assert.That((long)GetProperty(GetEntry(restored, 2), "CaptureFrameId"), Is.EqualTo(30));
            Assert.That(Serialize(restored), Is.EqualTo(canonical));
        }

        [Test]
        public void Roundtrip_LongMaxValue()
        {
            object plan = MakePlan(long.MaxValue, MakeEntryArray(MakeEntry(long.MaxValue, long.MaxValue, long.MaxValue)));
            byte[] canonical = Serialize(plan);

            object restored = DeserializeBytes(canonical);

            Assert.That((long)GetProperty(restored, "TestRunId"), Is.EqualTo(long.MaxValue));
            Assert.That((long)GetProperty(GetEntry(restored, 0), "CaptureFrameId"), Is.EqualTo(long.MaxValue));
            Assert.That((long)GetProperty(GetEntry(restored, 0), "PngByteLength"), Is.EqualTo(long.MaxValue));
            Assert.That(Serialize(restored), Is.EqualTo(canonical));
        }

        [Test]
        public void BytesSeekableNonSeekable_Equivalent()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10, 20));

            object fromBytes = DeserializeBytes(canonical);
            object fromSeekable = DeserializeStream(new MemoryStream(canonical));
            object fromNonSeekable = DeserializeStream(new NonSeekableStream(canonical));

            Assert.That(Serialize(fromBytes), Is.EqualTo(canonical));
            Assert.That(Serialize(fromSeekable), Is.EqualTo(canonical));
            Assert.That(Serialize(fromNonSeekable), Is.EqualTo(canonical));
        }

        [Test]
        public void Stream_NotDisposed()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            TrackDisposeStream tracking = new TrackDisposeStream(new MemoryStream(canonical));
            DeserializeStream(tracking);
            Assert.That(tracking.Disposed, Is.False);
        }

        [Test]
        public void Stream_NotDisposedOnFailure()
        {
            TrackDisposeStream tracking = new TrackDisposeStream(new MemoryStream(Bytes("not json")));
            Assert.That(DeserializeStreamException(tracking), Is.TypeOf<InvalidDataException>());
            Assert.That(tracking.Disposed, Is.False);
        }

        // ---- Bounds ----

        [Test]
        public void MaxPlanBytes_ExactAccepted_NextRejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(canonical, canonical.Length, MaxEntries, MaxPath), Is.Null);
            Assert.That(DeserializeBytesException(canonical, canonical.Length - 1, MaxEntries, MaxPath), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void NonSeekable_LimitPlusOne_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Exception ex = DeserializeStreamException(new NonSeekableStream(canonical), canonical.Length - 1, MaxEntries, MaxPath);
            Assert.That(ex, Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Seekable_GrowsAfterLength_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));
            byte[] withExtra = new byte[canonical.Length + 1];
            Array.Copy(canonical, withExtra, canonical.Length);
            withExtra[canonical.Length] = (byte)' ';

            GrowingSeekableStream stream = new GrowingSeekableStream(withExtra);

            Assert.That(DeserializeStreamException(stream, MaxPlan, MaxEntries, MaxPath), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void MaxEntryCount_ExceededBeforeAllocation_Rejected()
        {
            string json =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":100000,\"Entries\":[]}";

            Exception ex = DeserializeBytesException(Bytes(json), MaxPlan, 100, MaxPath);
            Assert.That(ex, Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void MaxPathBytes_Boundary()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));
            // Longest path is "frames/10.json.stage" = 20 bytes.

            Assert.That(DeserializeBytesException(canonical, MaxPlan, MaxEntries, 20), Is.Null);
            Assert.That(DeserializeBytesException(canonical, MaxPlan, MaxEntries, 19), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void LimitValidation_ParamNames()
        {
            Exception nullInput = DeserializeBytesException(null);
            Assert.That(nullInput, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullInput).ParamName, Is.EqualTo("utf8Json"));

            Exception nullStream = DeserializeStreamException(null);
            Assert.That(nullStream, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullStream).ParamName, Is.EqualTo("input"));

            byte[] doc = Bytes("{}");

            foreach (int bad in new[] { 0, -1, 16 * 1024 * 1024 + 1 })
            {
                Exception ex = DeserializeBytesException(doc, bad, MaxEntries, MaxPath);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("maxPlanBytes"));
            }

            foreach (int bad in new[] { -1, 100001 })
            {
                Exception ex = DeserializeBytesException(doc, MaxPlan, bad, MaxPath);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("maxEntryCount"));
            }

            foreach (int bad in new[] { 0, -1, 513 })
            {
                Exception ex = DeserializeBytesException(doc, MaxPlan, MaxEntries, bad);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("maxPathBytes"));
            }
        }

        // ---- Structural rejections ----

        [Test]
        public void Empty_Rejected()
        {
            Assert.That(DeserializeBytesException(new byte[0]), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Bom_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));
            byte[] withBom = new byte[canonical.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Array.Copy(canonical, 0, withBom, 3, canonical.Length);

            Assert.That(DeserializeBytesException(withBom), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Truncated_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(new byte[0]), Is.TypeOf<InvalidDataException>());

            for (int len = 1; len < canonical.Length; len++)
            {
                byte[] truncated = new byte[len];
                Array.Copy(canonical, truncated, len);
                Assert.That(DeserializeBytesException(truncated), Is.TypeOf<InvalidDataException>(), "Expected rejection at length " + len);
            }
        }

        [Test]
        public void TrailingNewlineAndData_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s + "\n")), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s + " ")), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s + "0")), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Whitespace_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace(":", ": "))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace(",", " ,"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void PropertyOrderChanged_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1,\"RunInitializationId\"", "\"RunInitializationId\""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void UnknownProperty_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace(",\"Entries\"", ",\"Foo\":1,\"Entries\""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void MissingProperty_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace(",\"TestRunId\":1", ""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void DuplicateProperty_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace(",\"TestRunId\":1", ",\"TestRunId\":1,\"TestRunId\":1"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void NullValue_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":null"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"Entries\":[", "\"Entries\":null,"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void FloatExponentLeadingZeroSigned_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":1.0"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":1e1"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":01"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":+1"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"TestRunId\":1", "\"TestRunId\":-1"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void EscapedStringAndNonAscii_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            // Escaped quote inside a string.
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"" + InitId + "\"", "\"abc\\\"def\""))), Is.TypeOf<InvalidDataException>());

            // Non-ASCII character inside a string value.
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"" + InitId + "\"", "\"0123456789abcdef0123456789abcde\u00e9\""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void InvalidUtf8_InvalidContinuation_Rejected()
        {
            // 0xC3 expects a continuation byte 0x80-0xBF; 0x28 is not one.
            byte[] doc = WithInvalidUtf8InInitId(new byte[] { 0xC3, 0x28 });

            Assert.That(DeserializeBytesException(doc), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeStreamException(new NonSeekableStream(doc)), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void InvalidUtf8_TruncatedMultibyte_Rejected()
        {
            // 0xE2 0x82 is a truncated three-byte sequence (final continuation
            // byte missing).
            byte[] doc = WithInvalidUtf8InInitId(new byte[] { 0xE2, 0x82 });

            Assert.That(DeserializeBytesException(doc), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void UppercaseHash_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"" + Hash64 + "\"", "\"" + Hash64.ToUpperInvariant() + "\""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void InitIdLongerThan32_Rejected()
        {
            string json =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + new string('0', 33) +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":0,\"Entries\":[]}";

            Assert.That(DeserializeBytesException(Bytes(json)), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void HashLongerThan64_Rejected()
        {
            string json =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + new string('0', 65) +
                "\",\"EntryCount\":0,\"Entries\":[]}";

            Assert.That(DeserializeBytesException(Bytes(json)), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void EntryHashLongerThan64_Rejected()
        {
            string json =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":1,\"Entries\":[" +
                "{\"CaptureFrameId\":10" +
                ",\"PngStagingRelativePath\":\"frames/10.png.stage\"" +
                ",\"SidecarStagingRelativePath\":\"frames/10.json.stage\"" +
                ",\"PngFinalRelativePath\":\"frames/10.png\"" +
                ",\"SidecarFinalRelativePath\":\"frames/10.json\"" +
                ",\"PngByteLength\":16,\"SidecarByteLength\":32" +
                ",\"PngContentSha256\":\"" + new string('0', 65) + "\"" +
                ",\"SidecarContentSha256\":\"" + Hash64 + "\"}]}";

            Assert.That(DeserializeBytesException(Bytes(json)), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void SchemaVersionMismatch_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void EntryCountArrayLengthMismatch_Rejected()
        {
            // Declared 2, array has 1.
            Assert.That(DeserializeBytesException(Bytes(
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":2,\"Entries\":[" + SingleEntryJson(10) + "]}")), Is.TypeOf<InvalidDataException>());

            // Declared 1, array has 2.
            Assert.That(DeserializeBytesException(Bytes(
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":1,\"Entries\":[" + SingleEntryJson(10) + "," + SingleEntryJson(20) + "]}")), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void IdDuplicateAndReverse_Rejected()
        {
            Assert.That(DeserializeBytesException(Bytes(
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":2,\"Entries\":[" + SingleEntryJson(10) + "," + SingleEntryJson(10) + "]}")), Is.TypeOf<InvalidDataException>());

            Assert.That(DeserializeBytesException(Bytes(
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":2,\"Entries\":[" + SingleEntryJson(20) + "," + SingleEntryJson(10) + "]}")), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void FixedPathMismatch_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"frames/10.png.stage\"", "\"frames/11.png.stage\""))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"frames/10.png.stage\"", "\"frames/010.png.stage\""))), Is.TypeOf<InvalidDataException>());
        }

        [Test]
        public void ByteLengthZeroOrNegative_Rejected()
        {
            byte[] canonical = Serialize(MakePlanWithIds(10));

            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"PngByteLength\":16", "\"PngByteLength\":0"))), Is.TypeOf<InvalidDataException>());
            Assert.That(DeserializeBytesException(Mutate(canonical, s => s.Replace("\"SidecarByteLength\":32", "\"SidecarByteLength\":-1"))), Is.TypeOf<InvalidDataException>());
        }

        // ---- Type contracts ----

        [Test]
        public void Codec_NoPublicApiNoFilesystemNoUnityObjectNoMutableStatic()
        {
            Type type = GetCodecType();

            // No public methods; static and sealed.
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly), Is.Empty, "Codec must expose no public API.");

            // No mutable static state: every static field is readonly or const.
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, type.Name + "." + field.Name + " must be readonly or const.");
            }

            // No instance state and no Unity object references.
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Codec_SourceDoesNotTouchFilesystemLoggerTraceUnity()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CapturePublicationPlanCodec.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("TraceLogger"));
            Assert.That(source, Does.Not.Contain("Debug."));
            Assert.That(source, Does.Not.Contain("System.Linq"));
        }

        private static string SingleEntryJson(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return "{\"CaptureFrameId\":" + idStr +
                ",\"PngStagingRelativePath\":\"frames/" + idStr + ".png.stage\"" +
                ",\"SidecarStagingRelativePath\":\"frames/" + idStr + ".json.stage\"" +
                ",\"PngFinalRelativePath\":\"frames/" + idStr + ".png\"" +
                ",\"SidecarFinalRelativePath\":\"frames/" + idStr + ".json\"" +
                ",\"PngByteLength\":16,\"SidecarByteLength\":32" +
                ",\"PngContentSha256\":\"" + Hash64 + "\"" +
                ",\"SidecarContentSha256\":\"" + Hash64 + "\"}";
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CapturePublicationPlanDeserializeTests).Assembly.Location);
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
