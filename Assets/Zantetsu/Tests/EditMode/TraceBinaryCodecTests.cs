using System;
using System.IO;
using NUnit.Framework;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceBinaryCodecTests
    {
        private static readonly byte[] Magic = { (byte)'Z', (byte)'T', (byte)'R', (byte)'C', (byte)'E', (byte)'V', (byte)'T', (byte)'1' };

        private static byte[] Serialize(TraceEvent[] events)
        {
            return Serialize(events, 0, events.Length);
        }

        private static byte[] Serialize(TraceEvent[] events, int sourceIndex, int count)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                TraceBinaryCodec.Write(ms, events, sourceIndex, count);
                return ms.ToArray();
            }
        }

        private static TraceEvent[] Deserialize(byte[] bytes, int maximumEventCount = int.MaxValue)
        {
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                return TraceBinaryCodec.Read(ms, maximumEventCount);
            }
        }

        private static TraceEvent MakeEvent(long timestamp)
        {
            return new TraceEvent { Timestamp = timestamp };
        }

        private static void AssertTraceEventEqual(TraceEvent expected, TraceEvent actual)
        {
            Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp));
            Assert.That(actual.FrameId, Is.EqualTo(expected.FrameId));
            Assert.That(actual.FixedStepId, Is.EqualTo(expected.FixedStepId));
            Assert.That(actual.ThreadId, Is.EqualTo(expected.ThreadId));
            Assert.That(actual.SlashId, Is.EqualTo(expected.SlashId));
            Assert.That(actual.SlashGeneration, Is.EqualTo(expected.SlashGeneration));
            Assert.That(actual.FrontEdgeId, Is.EqualTo(expected.FrontEdgeId));
            Assert.That(actual.ObjectId, Is.EqualTo(expected.ObjectId));
            Assert.That(actual.ObjectGeneration, Is.EqualTo(expected.ObjectGeneration));
            Assert.That(actual.MobId, Is.EqualTo(expected.MobId));
            Assert.That(actual.PlanGeneration, Is.EqualTo(expected.PlanGeneration));
            Assert.That(actual.TaskId, Is.EqualTo(expected.TaskId));
            Assert.That(actual.CaptureFrameId, Is.EqualTo(expected.CaptureFrameId));
            Assert.That(actual.OpenXRFrameId, Is.EqualTo(expected.OpenXRFrameId));
            Assert.That(actual.TestRunId, Is.EqualTo(expected.TestRunId));
            Assert.That(actual.EventType, Is.EqualTo(expected.EventType));
            Assert.That(actual.TaskType, Is.EqualTo(expected.TaskType));
            Assert.That(actual.FromState, Is.EqualTo(expected.FromState));
            Assert.That(actual.ToState, Is.EqualTo(expected.ToState));
            Assert.That(actual.Reason, Is.EqualTo(expected.Reason));
            Assert.That(actual.Value0, Is.EqualTo(expected.Value0));
            Assert.That(actual.Value1, Is.EqualTo(expected.Value1));
        }

        private static byte[] BuildHeader(int eventCount = 0, ushort major = 1, ushort minor = 0, int headerSize = 32, int recordSize = 140, int flags = 0, int reserved = 0)
        {
            byte[] header = new byte[32];
            Array.Copy(Magic, 0, header, 0, 8);
            WriteUInt16LE(header, 8, major);
            WriteUInt16LE(header, 10, minor);
            WriteInt32LE(header, 12, headerSize);
            WriteInt32LE(header, 16, recordSize);
            WriteInt32LE(header, 20, eventCount);
            WriteInt32LE(header, 24, flags);
            WriteInt32LE(header, 28, reserved);
            return header;
        }

        private static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteInt32LE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        [Test]
        public void RoundTrip_EmptyEvents()
        {
            TraceEvent[] result = Deserialize(Serialize(new TraceEvent[0]));

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void RoundTrip_SingleEvent()
        {
            TraceEvent source = MakeEvent(42);
            TraceEvent[] result = Deserialize(Serialize(new[] { source }));

            Assert.That(result.Length, Is.EqualTo(1));
            AssertTraceEventEqual(source, result[0]);
        }

        [Test]
        public void RoundTrip_MultipleEvents()
        {
            TraceEvent[] source = { MakeEvent(1), MakeEvent(2), MakeEvent(3) };
            TraceEvent[] result = Deserialize(Serialize(source));

            Assert.That(result.Length, Is.EqualTo(3));
            for (int i = 0; i < 3; i++)
            {
                AssertTraceEventEqual(source[i], result[i]);
            }
        }

        [Test]
        public void RoundTrip_All22Fields_DistinctValues()
        {
            TraceEvent source = new TraceEvent
            {
                Timestamp = 1,
                FrameId = 2,
                FixedStepId = 3,
                ThreadId = 4,
                SlashId = 5,
                SlashGeneration = 6,
                FrontEdgeId = 7,
                ObjectId = 8,
                ObjectGeneration = 9,
                MobId = 10,
                PlanGeneration = 11,
                TaskId = 12,
                CaptureFrameId = 13,
                OpenXRFrameId = 14,
                TestRunId = 15,
                EventType = TraceEventType.SlashPrimed,
                TaskType = (TraceTaskType)7,
                FromState = 16,
                ToState = 17,
                Reason = (TraceReason)18,
                Value0 = 19.5,
                Value1 = 20.25,
            };

            TraceEvent[] result = Deserialize(Serialize(new[] { source }));

            Assert.That(result.Length, Is.EqualTo(1));
            AssertTraceEventEqual(source, result[0]);
        }

        [Test]
        public void RoundTrip_DoubleBitPatterns()
        {
            double[] values =
            {
                -0.0,
                double.PositiveInfinity,
                double.NegativeInfinity,
                BitConverter.Int64BitsToDouble(0x7FF8000000000001L), // NaN payload
                BitConverter.Int64BitsToDouble(unchecked((long)0xFFF80000ABCD0000)), // NaN sign + payload
            };

            foreach (double value in values)
            {
                TraceEvent source = new TraceEvent { Value0 = value, Value1 = value };
                TraceEvent result = Deserialize(Serialize(new[] { source }))[0];

                Assert.That(BitConverter.DoubleToInt64Bits(result.Value0), Is.EqualTo(BitConverter.DoubleToInt64Bits(value)));
                Assert.That(BitConverter.DoubleToInt64Bits(result.Value1), Is.EqualTo(BitConverter.DoubleToInt64Bits(value)));
            }
        }

        [Test]
        public void RoundTrip_UnknownEnumValues()
        {
            TraceEvent source = new TraceEvent
            {
                EventType = (TraceEventType)9999,
                TaskType = (TraceTaskType)12345,
                Reason = (TraceReason)678,
            };

            TraceEvent result = Deserialize(Serialize(new[] { source }))[0];

            Assert.That((int)result.EventType, Is.EqualTo(9999));
            Assert.That((int)result.TaskType, Is.EqualTo(12345));
            Assert.That((int)result.Reason, Is.EqualTo(678));
        }

        [Test]
        public void Write_PartialRange()
        {
            TraceEvent[] source = { MakeEvent(1), MakeEvent(2), MakeEvent(3), MakeEvent(4) };

            byte[] bytes = Serialize(source, 1, 2);
            TraceEvent[] result = Deserialize(bytes);

            Assert.That(result.Length, Is.EqualTo(2));
            AssertTraceEventEqual(source[1], result[0]);
            AssertTraceEventEqual(source[2], result[1]);
        }

        [Test]
        public void Write_IsDeterministic()
        {
            TraceEvent[] source =
            {
                new TraceEvent { Timestamp = 10, Value0 = -0.0, Value1 = double.NaN },
                new TraceEvent { Timestamp = 20, EventType = (TraceEventType)12345 },
            };

            byte[] first = Serialize(source);
            byte[] second = Serialize(source);

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void Header_IsExactly32Bytes()
        {
            byte[] bytes = Serialize(new TraceEvent[0]);

            Assert.That(bytes.Length, Is.EqualTo(TraceBinaryFormat.HeaderSize));
            Assert.That(bytes.Length, Is.EqualTo(32));
        }

        [Test]
        public void EventRecord_IsExactly140Bytes()
        {
            byte[] one = Serialize(new[] { MakeEvent(1) });
            byte[] two = Serialize(new[] { MakeEvent(1), MakeEvent(2) });

            Assert.That(two.Length - one.Length, Is.EqualTo(TraceBinaryFormat.EventRecordSize));
            Assert.That(two.Length - one.Length, Is.EqualTo(140));
        }

        [Test]
        public void SingleEvent_TotalLength172()
        {
            byte[] bytes = Serialize(new[] { MakeEvent(1) });

            Assert.That(bytes.Length, Is.EqualTo(172));
        }

        [Test]
        public void GoldenBytes_HeaderFields()
        {
            byte[] bytes = Serialize(new TraceEvent[0]);

            byte[] expected = new byte[32];
            Array.Copy(Magic, 0, expected, 0, 8);
            expected[8] = 0x01; expected[9] = 0x00;          // MajorVersion 1
            expected[10] = 0x00; expected[11] = 0x00;         // MinorVersion 0
            expected[12] = 0x20; expected[13] = 0x00; expected[14] = 0x00; expected[15] = 0x00; // HeaderSize 32
            expected[16] = 0x8C; expected[17] = 0x00; expected[18] = 0x00; expected[19] = 0x00; // EventRecordSize 140
            // EventCount 0, Flags 0, Reserved 0 are already zero.

            Assert.That(bytes, Is.EqualTo(expected));
        }

        [Test]
        public void FieldOrder_AndLittleEndian_AtFixedOffsets()
        {
            TraceEvent source = new TraceEvent
            {
                Timestamp = 0x0102030405060708L,
                ThreadId = 0x0A0B0C0D,
                SlashGeneration = 0x000000FF,
                EventType = TraceEventType.SlashLatched, // 7
                Value0 = 1.0,
            };

            byte[] bytes = Serialize(new[] { source });

            Assert.That(bytes.Length, Is.EqualTo(172));

            // Timestamp (record offset 0 -> absolute 32), little-endian.
            byte[] timestamp = { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 };
            Assert.That(Slice(bytes, 32, 8), Is.EqualTo(timestamp));

            // ThreadId (record offset 24 -> absolute 56), little-endian Int32.
            byte[] threadId = { 0x0D, 0x0C, 0x0B, 0x0A };
            Assert.That(Slice(bytes, 56, 4), Is.EqualTo(threadId));

            // SlashGeneration (record offset 36 -> absolute 68), little-endian UInt32.
            byte[] slashGeneration = { 0xFF, 0x00, 0x00, 0x00 };
            Assert.That(Slice(bytes, 68, 4), Is.EqualTo(slashGeneration));

            // EventType (record offset 104 -> absolute 136), little-endian Int32.
            byte[] eventType = { 0x07, 0x00, 0x00, 0x00 };
            Assert.That(Slice(bytes, 136, 4), Is.EqualTo(eventType));

            // Value0 (record offset 124 -> absolute 156), little-endian double 1.0.
            byte[] value0 = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F };
            Assert.That(Slice(bytes, 156, 8), Is.EqualTo(value0));
        }

        [Test]
        public void Write_NullDestination_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => TraceBinaryCodec.Write(null, new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Write_NullEvents_Throws()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                Assert.Throws<ArgumentNullException>(() => TraceBinaryCodec.Write(ms, null, 0, 0));
            }
        }

        [Test]
        public void Write_InvalidRange_Throws()
        {
            TraceEvent[] events = { MakeEvent(1), MakeEvent(2) };

            using (MemoryStream ms = new MemoryStream())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => TraceBinaryCodec.Write(ms, events, -1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => TraceBinaryCodec.Write(ms, events, 0, -1));
                Assert.Throws<ArgumentException>(() => TraceBinaryCodec.Write(ms, events, 1, 2));
                Assert.Throws<ArgumentException>(() => TraceBinaryCodec.Write(ms, events, 2, 1));
            }
        }

        [Test]
        public void Write_NonWritableStream_Throws()
        {
            TraceEvent[] events = { MakeEvent(1) };
            ReadOnlySequentialStream stream = new ReadOnlySequentialStream(new byte[0]);

            Assert.Throws<ArgumentException>(() => TraceBinaryCodec.Write(stream, events, 0, 1));
        }

        [Test]
        public void Read_NullSource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => TraceBinaryCodec.Read(null, 0));
        }

        [Test]
        public void Read_NonReadableStream_Throws()
        {
            WriteOnlyCollectingStream stream = new WriteOnlyCollectingStream();

            Assert.Throws<ArgumentException>(() => TraceBinaryCodec.Read(stream, 0));
        }

        [Test]
        public void Read_NegativeMaximumEventCount_Throws()
        {
            using (MemoryStream ms = new MemoryStream(new byte[0]))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => TraceBinaryCodec.Read(ms, -1));
            }
        }

        [Test]
        public void Read_InvalidMagic_Throws()
        {
            byte[] header = BuildHeader();
            header[0] = (byte)'X';

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_UnsupportedMajorVersion_Throws()
        {
            byte[] header = BuildHeader(major: 2);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_UnsupportedMinorVersion_Throws()
        {
            byte[] header = BuildHeader(minor: 1);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_InvalidHeaderSize_Throws()
        {
            byte[] header = BuildHeader(headerSize: 33);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_InvalidRecordSize_Throws()
        {
            byte[] header = BuildHeader(recordSize: 141);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_NonZeroFlags_Throws()
        {
            byte[] header = BuildHeader(flags: 1);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_NonZeroReserved_Throws()
        {
            byte[] header = BuildHeader(reserved: 1);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_NegativeEventCount_Throws()
        {
            byte[] header = BuildHeader(eventCount: -1);

            Assert.Throws<InvalidDataException>(() => Deserialize(header));
        }

        [Test]
        public void Read_EventCountExceedsMaximum_ThrowsBeforeAllocation()
        {
            byte[] header = BuildHeader(eventCount: 5); // no records follow

            Assert.Throws<InvalidDataException>(() => Deserialize(header, maximumEventCount: 1));
        }

        [Test]
        public void Read_HeaderTruncatedEof_Throws()
        {
            byte[] header = BuildHeader();
            byte[] truncated = new byte[20];
            Array.Copy(header, truncated, 20);

            Assert.Throws<InvalidDataException>(() => Deserialize(truncated));
        }

        [Test]
        public void Read_RecordTruncatedEof_Throws()
        {
            byte[] full = Serialize(new[] { MakeEvent(1) });
            byte[] truncated = new byte[full.Length - 1];
            Array.Copy(full, truncated, truncated.Length);

            Assert.Throws<InvalidDataException>(() => Deserialize(truncated));
        }

        [Test]
        public void Read_TrailingBytes_Throws()
        {
            byte[] full = Serialize(new[] { MakeEvent(1) });
            byte[] withTrailing = new byte[full.Length + 1];
            Array.Copy(full, withTrailing, full.Length);
            withTrailing[full.Length] = 0xAA;

            Assert.Throws<InvalidDataException>(() => Deserialize(withTrailing));
        }

        [Test]
        public void Write_LeavesStreamOpenAndUsable()
        {
            TraceEvent[] events = { MakeEvent(1) };
            using (MemoryStream ms = new MemoryStream())
            {
                TraceBinaryCodec.Write(ms, events, 0, events.Length);

                Assert.That(ms.CanWrite, Is.True);

                // Stream still usable: additional write succeeds.
                ms.WriteByte(0xAA);
                Assert.That(ms.Length, Is.EqualTo(172 + 1));
            }
        }

        [Test]
        public void Read_LeavesStreamOpenAndUsable()
        {
            byte[] bytes = Serialize(new[] { MakeEvent(1) });
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                TraceEvent[] result = TraceBinaryCodec.Read(ms, int.MaxValue);

                Assert.That(result.Length, Is.EqualTo(1));
                Assert.That(ms.CanRead, Is.True);
            }
        }

        [Test]
        public void RoundTrip_NonSeekableSequentialStream()
        {
            TraceEvent[] source =
            {
                new TraceEvent { Timestamp = 11, Value0 = -0.0 },
                new TraceEvent { Timestamp = 22, Value1 = double.PositiveInfinity },
            };

            WriteOnlyCollectingStream writeStream = new WriteOnlyCollectingStream();
            TraceBinaryCodec.Write(writeStream, source, 0, source.Length);
            byte[] bytes = writeStream.ToArray();

            ReadOnlySequentialStream readStream = new ReadOnlySequentialStream(bytes);
            TraceEvent[] result = TraceBinaryCodec.Read(readStream, int.MaxValue);

            Assert.That(result.Length, Is.EqualTo(2));
            AssertTraceEventEqual(source[0], result[0]);
            AssertTraceEventEqual(source[1], result[1]);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            byte[] slice = new byte[count];
            Array.Copy(source, offset, slice, 0, count);
            return slice;
        }

        private sealed class WriteOnlyCollectingStream : Stream
        {
            private readonly MemoryStream _inner = new MemoryStream();

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public byte[] ToArray() => _inner.ToArray();

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        }

        private sealed class ReadOnlySequentialStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public ReadOnlySequentialStream(byte[] data)
            {
                _data = data;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int available = _data.Length - _position;
                if (available <= 0)
                {
                    return 0;
                }

                int toRead = Math.Min(available, count);
                Array.Copy(_data, _position, buffer, offset, toRead);
                _position += toRead;
                return toRead;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
