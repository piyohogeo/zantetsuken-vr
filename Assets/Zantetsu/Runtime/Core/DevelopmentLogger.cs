#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
#endif

namespace Zantetsu.Core
{
    /// <summary>The result of admission, not a receipt for persistence.</summary>
    public enum DevelopmentLogResult
    {
        Accepted,
        QueueFull,
        Unavailable,
        InvalidValue,
        Disabled,
    }

    /// <summary>Optional managed diagnostics. Never a gameplay or Trace authority.</summary>
    public sealed class DevelopmentLogger
    {
        public const int DefaultQueueCapacity = 65536;
        public static DevelopmentLogger Instance { get; } = new DevelopmentLogger();
        private DevelopmentLogger() { }

        /// <summary>
        /// Consumes and disposes iterators on the caller, then queues one complete record.
        /// QueueFull rejects the new record without waiting for capacity or doing file I/O.
        /// Accepted means queued; a later I/O failure or shutdown can still lose the record.
        /// Guard call sites with #if DEBUG to omit argument evaluation in non-debug builds.
        /// </summary>
        public DevelopmentLogResult write_log(string writer_id, string tag, object value)
        {
#if DEBUG
            long utcTicks = DateTime.UtcNow.Ticks;
            int frame = Volatile.Read(ref frameCount);
            Session destination;
            lock (gate) { destination = session; }
            if (destination == null) return DevelopmentLogResult.Unavailable;
            DevelopmentLogResult availability = destination.CheckAvailability();
            if (availability != DevelopmentLogResult.Accepted) return availability;

            string record;
            try { record = FormatRecord(utcTicks, frame, writer_id, tag, value); }
            catch (Exception) { return DevelopmentLogResult.InvalidValue; }

            // Recheck after enumeration: another producer may fill the queue, or this
            // session may stop/restart. Never forward an old record to a new session.
            return destination.TryEnqueue(record);
#else
            return DevelopmentLogResult.Disabled;
#endif
        }

#if DEBUG
        private readonly object gate = new object();
        private Session session;
        private int frameCount;
        internal void SetFrame(int frame) => Volatile.Write(ref frameCount, frame);

        // The factory allows deterministic slow/failing storage tests. It is invoked only
        // by the worker, which owns creation, all writes, flushes and disposal.
        internal void StartSession(string directory, string commit, int queueCapacity = DefaultQueueCapacity,
            Func<StreamWriter> writerFactory = null)
        {
            StopSession();
            SetFrame(0);
            try
            {
                string name = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture)
                    + "_" + commit + "_" + Guid.NewGuid().ToString("N") + ".jsonl";
                var next = new Session(Path.Combine(directory, name), queueCapacity, writerFactory);
                next.Start();
                lock (gate) { session = next; }
            }
            catch (Exception) { /* A missing diagnostic must not prevent startup. */ }
        }

        internal void StopSession()
        {
            Session old;
            lock (gate) { old = session; session = null; }
            if (old == null) return;
            old.RequestStop();
            // Standard lifecycle notification only, never a prerequisite at the common
            // Player exit-request boundary. A stalled filesystem must not cause an unbounded join.
            old.Join(1000);
        }

        private sealed class Session
        {
            private readonly object sync = new object();
            private readonly Queue<string> records;
            private readonly int capacity;
            private readonly Func<StreamWriter> writerFactory;
            private readonly Thread worker;
            private bool accepting = true;
            internal readonly string FilePath;

            internal Session(string path, int capacity, Func<StreamWriter> writerFactory)
            {
                if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
                FilePath = path;
                this.capacity = capacity;
                records = new Queue<string>(capacity);
                this.writerFactory = writerFactory ?? OpenWriter;
                worker = new Thread(Run) { IsBackground = true, Name = "Development Logger" };
            }

            internal void Start() => worker.Start();
            internal bool Join(int milliseconds) => worker.Join(milliseconds);

            internal DevelopmentLogResult CheckAvailability()
            {
                lock (sync) { return AvailabilityLocked(); }
            }

            private DevelopmentLogResult AvailabilityLocked() => !accepting ? DevelopmentLogResult.Unavailable
                : records.Count == capacity ? DevelopmentLogResult.QueueFull : DevelopmentLogResult.Accepted;

            internal DevelopmentLogResult TryEnqueue(string record)
            {
                lock (sync)
                {
                    DevelopmentLogResult result = AvailabilityLocked();
                    if (result != DevelopmentLogResult.Accepted) return result;
                    records.Enqueue(record);
                    Monitor.Pulse(sync);
                    return DevelopmentLogResult.Accepted;
                }
            }

            internal void RequestStop()
            {
                lock (sync) { accepting = false; Monitor.Pulse(sync); }
            }

            private StreamWriter OpenWriter()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                try { return new StreamWriter(stream, new UTF8Encoding(false)); }
                catch { stream.Dispose(); throw; }
            }

            private void Run()
            {
                StreamWriter output = null;
                try
                {
                    output = writerFactory();
                    output.NewLine = "\n";
                    output.AutoFlush = true;
                    while (true)
                    {
                        string record;
                        lock (sync)
                        {
                            while (records.Count == 0 && accepting) Monitor.Wait(sync);
                            if (records.Count == 0) break;
                            record = records.Dequeue();
                        }
                        // Never hold the producer lock across any file I/O.
                        output.WriteLine(record);
                    }
                }
                catch (Exception) { /* I/O failure disables this session; no retry or fallback. */ }
                finally
                {
                    lock (sync) { accepting = false; records.Clear(); }
                    try { output?.Dispose(); }
                    catch (Exception) { }
                }
            }
        }

        internal static string FormatRecord(long utcTicks, int frame, string writerId, string tag, object value)
        {
            var text = new StringBuilder(256);
            // Decimal arithmetic preserves every 100 ns tick; never convert through float/double.
            decimal seconds = (utcTicks - 621355968000000000L) / 10000000m;
            text.Append("{\"time\":").Append(seconds.ToString("F7", CultureInfo.InvariantCulture));
            text.Append(",\"frame\":").Append(frame.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"writer_id\":");
            AppendString(text, writerId);
            text.Append(",\"tag\":");
            AppendString(text, tag);
            text.Append(",\"value\":");
            if (value is string)
                AppendScalar(text, value);
            else if (value is IEnumerable sequence)
                AppendIterator(text, sequence.GetEnumerator());
            else if (value is IEnumerator iterator)
                AppendIterator(text, iterator);
            else
                AppendScalar(text, value);
            return text.Append('}').ToString();
        }

        private static void AppendIterator(StringBuilder text, IEnumerator iterator)
        {
            try
            {
                text.Append('[');
                bool first = true;
                while (iterator.MoveNext())
                {
                    if (!first) text.Append(',');
                    AppendScalar(text, iterator.Current);
                    first = false;
                }
                text.Append(']');
            }
            finally { (iterator as IDisposable)?.Dispose(); }
        }

        private static void AppendScalar(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null"); return; }
            if (value is string str) { AppendString(text, str); return; }
            if (value is float single && !float.IsNaN(single) && !float.IsInfinity(single))
            {
                text.Append(single.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number))
            {
                text.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is byte || value is sbyte || value is short || value is ushort || value is int
                || value is uint || value is long || value is ulong || value is decimal)
            {
                text.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            throw new ArgumentException("Unsupported development log value.");
        }

        private static void AppendString(StringBuilder text, string value)
        {
            if (value == null) { text.Append("null"); return; }
            text.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    default:
                        // Escape surrogate code units too, preserving pairs and lone surrogates
                        // without allowing the UTF-8 encoder to replace the supplied string.
                        if (ch < 0x20 || char.IsSurrogate(ch))
                            text.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(ch);
                        break;
                }
            }
            text.Append('"');
        }
#endif
    }
}
