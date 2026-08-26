using System;
using System.IO;

namespace Zantetsu.Trace
{
    /// <summary>
    /// Reads and writes <see cref="TraceEvent"/> values using the versioned
    /// trace binary format. All values are explicitly encoded little-endian;
    /// no raw struct copy, BOM, or string header is used, so the saved layout
    /// is independent of runtime, CPU, and struct padding.
    /// </summary>
    public static class TraceBinaryCodec
    {
        private static readonly byte[] Magic =
        {
            (byte)'Z', (byte)'T', (byte)'R', (byte)'C',
            (byte)'E', (byte)'V', (byte)'T', (byte)'1',
        };

        /// <summary>
        /// Writes <paramref name="count"/> events starting at
        /// <paramref name="sourceIndex"/> to <paramref name="destination"/>,
        /// beginning at the stream's current position. The stream is neither
        /// closed nor disposed.
        /// </summary>
        public static void Write(Stream destination, TraceEvent[] events, int sourceIndex, int count)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (!destination.CanWrite)
            {
                throw new ArgumentException("The stream is not writable.", nameof(destination));
            }

            if (sourceIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceIndex), sourceIndex, "Source index must not be negative.");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");
            }

            if ((long)sourceIndex + (long)count > (long)events.Length)
            {
                throw new ArgumentException("Source index plus count exceeds the bounds of the events array.", nameof(count));
            }

            byte[] header = new byte[TraceBinaryFormat.HeaderSize];
            WriteHeader(header, count);
            destination.Write(header, 0, header.Length);

            byte[] record = new byte[TraceBinaryFormat.EventRecordSize];
            for (int i = 0; i < count; i++)
            {
                WriteRecord(record, events[sourceIndex + i]);
                destination.Write(record, 0, record.Length);
            }
        }

        /// <summary>
        /// Reads and decodes events from <paramref name="source"/>, beginning at
        /// the stream's current position. The stream is neither closed nor
        /// disposed and need not support seeking or length queries.
        /// </summary>
        public static TraceEvent[] Read(Stream source, int maximumEventCount)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!source.CanRead)
            {
                throw new ArgumentException("The stream is not readable.", nameof(source));
            }

            if (maximumEventCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEventCount), maximumEventCount, "Maximum event count must not be negative.");
            }

            byte[] header = new byte[TraceBinaryFormat.HeaderSize];
            if (!ReadFully(source, header, 0, header.Length))
            {
                throw new InvalidDataException("Unexpected end of stream while reading the header.");
            }

            int eventCount = ReadHeader(header);

            if (eventCount < 0)
            {
                throw new InvalidDataException("Event count must not be negative.");
            }

            if (eventCount > maximumEventCount)
            {
                throw new InvalidDataException("Event count exceeds the maximum allowed.");
            }

            TraceEvent[] events = new TraceEvent[eventCount];
            byte[] record = new byte[TraceBinaryFormat.EventRecordSize];

            for (int i = 0; i < eventCount; i++)
            {
                if (!ReadFully(source, record, 0, record.Length))
                {
                    throw new InvalidDataException("Unexpected end of stream while reading an event record.");
                }

                events[i] = ReadRecord(record);
            }

            if (source.ReadByte() != -1)
            {
                throw new InvalidDataException("Unexpected trailing bytes after the last event record.");
            }

            return events;
        }

        private static void WriteHeader(byte[] buffer, int eventCount)
        {
            for (int i = 0; i < Magic.Length; i++)
            {
                buffer[i] = Magic[i];
            }

            WriteUInt16(buffer, 8, TraceBinaryFormat.MajorVersion);
            WriteUInt16(buffer, 10, TraceBinaryFormat.MinorVersion);
            WriteInt32(buffer, 12, TraceBinaryFormat.HeaderSize);
            WriteInt32(buffer, 16, TraceBinaryFormat.EventRecordSize);
            WriteInt32(buffer, 20, eventCount);
            WriteInt32(buffer, 24, 0); // Flags
            WriteInt32(buffer, 28, 0); // Reserved
        }

        private static int ReadHeader(byte[] buffer)
        {
            for (int i = 0; i < Magic.Length; i++)
            {
                if (buffer[i] != Magic[i])
                {
                    throw new InvalidDataException("Invalid trace magic.");
                }
            }

            ushort major = ReadUInt16(buffer, 8);
            ushort minor = ReadUInt16(buffer, 10);
            int headerSize = ReadInt32(buffer, 12);
            int recordSize = ReadInt32(buffer, 16);
            int flags = ReadInt32(buffer, 24);
            int reserved = ReadInt32(buffer, 28);

            if (major != TraceBinaryFormat.MajorVersion || minor != TraceBinaryFormat.MinorVersion)
            {
                throw new InvalidDataException("Unsupported trace format version.");
            }

            if (headerSize != TraceBinaryFormat.HeaderSize)
            {
                throw new InvalidDataException("Unsupported trace header size.");
            }

            if (recordSize != TraceBinaryFormat.EventRecordSize)
            {
                throw new InvalidDataException("Unsupported trace event record size.");
            }

            if (flags != 0)
            {
                throw new InvalidDataException("Unsupported trace flags.");
            }

            if (reserved != 0)
            {
                throw new InvalidDataException("Unexpected non-zero reserved header field.");
            }

            return ReadInt32(buffer, 20);
        }

        private static void WriteRecord(byte[] buffer, in TraceEvent e)
        {
            WriteInt64(buffer, 0, e.Timestamp);
            WriteInt64(buffer, 8, e.FrameId);
            WriteInt64(buffer, 16, e.FixedStepId);
            WriteInt32(buffer, 24, e.ThreadId);
            WriteInt64(buffer, 28, e.SlashId);
            WriteUInt32(buffer, 36, e.SlashGeneration);
            WriteInt64(buffer, 40, e.FrontEdgeId);
            WriteInt64(buffer, 48, e.ObjectId);
            WriteUInt32(buffer, 56, e.ObjectGeneration);
            WriteInt64(buffer, 60, e.MobId);
            WriteUInt32(buffer, 68, e.PlanGeneration);
            WriteInt64(buffer, 72, e.TaskId);
            WriteInt64(buffer, 80, e.CaptureFrameId);
            WriteInt64(buffer, 88, e.OpenXRFrameId);
            WriteInt64(buffer, 96, e.TestRunId);
            WriteInt32(buffer, 104, (int)e.EventType);
            WriteInt32(buffer, 108, (int)e.TaskType);
            WriteInt32(buffer, 112, e.FromState);
            WriteInt32(buffer, 116, e.ToState);
            WriteInt32(buffer, 120, (int)e.Reason);
            WriteDouble(buffer, 124, e.Value0);
            WriteDouble(buffer, 132, e.Value1);
        }

        private static TraceEvent ReadRecord(byte[] buffer)
        {
            TraceEvent e = default;
            e.Timestamp = ReadInt64(buffer, 0);
            e.FrameId = ReadInt64(buffer, 8);
            e.FixedStepId = ReadInt64(buffer, 16);
            e.ThreadId = ReadInt32(buffer, 24);
            e.SlashId = ReadInt64(buffer, 28);
            e.SlashGeneration = ReadUInt32(buffer, 36);
            e.FrontEdgeId = ReadInt64(buffer, 40);
            e.ObjectId = ReadInt64(buffer, 48);
            e.ObjectGeneration = ReadUInt32(buffer, 56);
            e.MobId = ReadInt64(buffer, 60);
            e.PlanGeneration = ReadUInt32(buffer, 68);
            e.TaskId = ReadInt64(buffer, 72);
            e.CaptureFrameId = ReadInt64(buffer, 80);
            e.OpenXRFrameId = ReadInt64(buffer, 88);
            e.TestRunId = ReadInt64(buffer, 96);
            e.EventType = (TraceEventType)ReadInt32(buffer, 104);
            e.TaskType = (TraceTaskType)ReadInt32(buffer, 108);
            e.FromState = ReadInt32(buffer, 112);
            e.ToState = ReadInt32(buffer, 116);
            e.Reason = (TraceReason)ReadInt32(buffer, 120);
            e.Value0 = ReadDouble(buffer, 124);
            e.Value1 = ReadDouble(buffer, 132);
            return e;
        }

        private static bool ReadFully(Stream source, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = source.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }

        private static void WriteInt64(byte[] buffer, int offset, long value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value >> (i * 8));
            }
        }

        private static long ReadInt64(byte[] buffer, int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (long)buffer[offset + i] << (i * 8);
            }

            return value;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                buffer[offset + i] = (byte)(value >> (i * 8));
            }
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            uint value = 0;
            for (int i = 0; i < 4; i++)
            {
                value |= (uint)buffer[offset + i] << (i * 8);
            }

            return value;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            WriteUInt32(buffer, offset, (uint)value);
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return (int)ReadUInt32(buffer, offset);
        }

        private static void WriteDouble(byte[] buffer, int offset, double value)
        {
            WriteInt64(buffer, offset, BitConverter.DoubleToInt64Bits(value));
        }

        private static double ReadDouble(byte[] buffer, int offset)
        {
            return BitConverter.Int64BitsToDouble(ReadInt64(buffer, offset));
        }
    }
}
