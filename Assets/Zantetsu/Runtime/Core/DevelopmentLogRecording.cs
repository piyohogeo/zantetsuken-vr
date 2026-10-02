using System;
using System.Diagnostics;
using System.Globalization;

namespace Zantetsu.Core
{
    /// <summary>
    /// One explicitly ended record of a development-logger writer (DESIGN 21.17), for a diagnosis whose saved file is checked
    /// after the run (Tools/DevelopmentLog/check_writer_jsonl.py). Every record starts with its recording (which of the
    /// session's records of this writer), its seq (1..N, one per record tried), the frame and the seconds the caller gives;
    /// the attempts, acceptances and refusals by reason are counted here. <see cref="End"/> tries the summary as the last
    /// record -- its counts of the records before it (seq 1..N-1), the summary itself seq N -- and nothing is recorded after
    /// it; the same counts, over seq 1..N, go to the log as "[label] ended: recording R, attempted N ...", which does not
    /// depend on the logger. The logger is best effort (accepted is not saved): the check decides afterwards.
    /// <para>
    /// The record's own cost on the caller's thread is measured too (Stopwatch around each write_log call: the first call
    /// apart, the largest one, and the calls of one eventFrame summed -- the largest such sum; and the bytes the thread
    /// allocated in them where the runtime counts them, 0 where it does not, which is not "none") and reported with the
    /// summary. Nothing here changes what the caller observes; the caller gives the frame and the clock reading of its event.
    /// </para>
    /// </summary>
    public sealed class DevelopmentLogRecording
    {
        /// <summary>The values every record starts with (recording, seq, eventFrame, seconds: names and values).</summary>
        public const int Prefix = 8;

        private readonly string _writer;
        private readonly string _label;
        private readonly long _start;
        private long _seq, _accepted, _queueFull, _unavailable, _invalid, _disabled;
        private long _writeTicks, _writeMax, _writeAllocated;
        // The first call apart (it carries the logger's and the formatter's first use), and the calls of one eventFrame summed:
        // the largest such sum and the frames that had any.
        private long _writeFirst = -1, _frameSum, _frameSumMax, _frames;
        private int _sumFrame = int.MinValue;

        /// <param name="writer">The logger's writer_id.</param>
        /// <param name="label">The log line's label: "[label] ended: ...".</param>
        /// <param name="recording">Which record of this writer in the session (1..).</param>
        /// <param name="startTimestamp">The Stopwatch reading the records' seconds count from.</param>
        public DevelopmentLogRecording(string writer, string label, int recording, long startTimestamp)
        {
            _writer = writer;
            _label = label;
            Recording = recording;
            _start = startTimestamp;
        }

        public int Recording { get; }

        /// <summary>Records tried: the seq of the last one.</summary>
        public long Attempted => _seq;

        /// <summary>Records the logger accepted (queued -- not yet known to be saved).</summary>
        public long Accepted => _accepted;

        /// <summary>Records the logger refused, all reasons.</summary>
        public long Refused => _queueFull + _unavailable + _invalid + _disabled;

        /// <summary>Whether the record was ended: nothing is recorded after it.</summary>
        public bool Ended { get; private set; }

        /// <summary>Seconds from the record's start to a Stopwatch reading.</summary>
        public double SecondsAt(long timestamp) => (double)(timestamp - _start) / Stopwatch.Frequency;

        /// <summary>
        /// One record: values[0..Prefix) are filled here (recording, seq, eventFrame, seconds of <paramref name="timestamp"/>),
        /// the caller's field names and values follow from values[Prefix]. Nothing once ended.
        /// </summary>
        public void Write(string tag, int eventFrame, long timestamp, object[] values)
        {
            if (Ended)
            {
                return;
            }

            values[0] = "recording";
            values[1] = Recording;
            values[2] = "seq";
            values[3] = ++_seq;
            values[4] = "eventFrame";
            values[5] = eventFrame;
            values[6] = "seconds";
            values[7] = SecondsAt(timestamp);
            long begin = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
#if DEBUG
            switch (DevelopmentLogger.Instance.write_log(_writer, tag, values))
            {
                case DevelopmentLogResult.Accepted: _accepted++; break;
                case DevelopmentLogResult.QueueFull: _queueFull++; break;
                case DevelopmentLogResult.Unavailable: _unavailable++; break;
                case DevelopmentLogResult.InvalidValue: _invalid++; break;
                default: _disabled++; break;
            }
#else
            _disabled++;
#endif
            _writeAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
            long took = Stopwatch.GetTimestamp() - begin;
            _writeTicks += took;
            if (took > _writeMax) _writeMax = took;
            if (_writeFirst < 0) _writeFirst = took;
            if (eventFrame != _sumFrame)
            {
                _sumFrame = eventFrame;
                _frameSum = 0;
                _frames++;
            }

            _frameSum += took;
            if (_frameSum > _frameSumMax) _frameSumMax = _frameSum;
        }

        /// <summary>
        /// Ends the record (once): the summary as the last record -- the caller's fields, then the cost and the counts of the
        /// records before it -- and the same counts, the summary included, to the log. <paramref name="logDetail"/> is added
        /// to the log line. Returns the line (null when already ended).
        /// </summary>
        public string End(int eventFrame, long timestamp, object[] summaryFields, string logDetail = null)
        {
            if (Ended)
            {
                return null;
            }

            long writes = _seq;
            string cost = "write_log " + writes + " calls " + R(Sec(_writeTicks)) + " s (first " + R(Sec(Math.Max(0, _writeFirst))) + " s, max of one " + R(Sec(_writeMax))
                + " s; " + _frames + " frames with records, max of one frame's sum " + R(Sec(_frameSumMax)) + " s), write_log allocated bytes "
                + _writeAllocated + " (0 = not counted by this runtime)";
            object[] own =
            {
                "writeSeconds", Sec(_writeTicks), "writeMaxSeconds", Sec(_writeMax), "writeFirstSeconds", Sec(Math.Max(0, _writeFirst)),
                "writeFrames", _frames, "writeFrameMaxSeconds", Sec(_frameSumMax), "writeAllocatedBytes", _writeAllocated,
                "attemptedBefore", _seq, "acceptedBefore", _accepted, "queueFull", _queueFull, "unavailable", _unavailable,
                "invalidValue", _invalid, "disabled", _disabled,
            };
            int caller = summaryFields != null ? summaryFields.Length : 0;
            var values = new object[Prefix + caller + own.Length];
            if (caller > 0) Array.Copy(summaryFields, 0, values, Prefix, caller);
            Array.Copy(own, 0, values, Prefix + caller, own.Length);
            Write("summary", eventFrame, timestamp, values);
            Ended = true;
            string line = "[" + _label + "] ended: recording " + Recording + ", attempted " + _seq + " (seq 1.." + _seq + ", the summary last), accepted " + _accepted
                + ", refused: queue full " + _queueFull + ", unavailable " + _unavailable + ", invalid value " + _invalid + ", disabled " + _disabled
                + "; cost before the summary: " + cost + (logDetail != null ? "; " + logDetail : "");
            UnityEngine.Debug.Log(line);
            return line;
        }

        private static double Sec(long ticks) => (double)ticks / Stopwatch.Frequency;

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
