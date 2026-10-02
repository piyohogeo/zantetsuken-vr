using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The check's capture accounting, whatever saves the pictures (docs/diagnostics/check-capture-2026-10-02.md): one row
    /// per save asked -- its frame, its real time, its shot -- ending in exactly one outcome: written, skipped (the reason
    /// apart from an error), an error, or cancelled at an ending. The rows are made room for up front (the request limit),
    /// so asking allocates nothing; skipped rows are kept, so the time they cover stays in the record. Main thread only.
    /// </summary>
    public sealed class CheckFrameLedger
    {
        public enum Outcome : byte
        {
            Pending = 0,
            Written,
            SkippedReadbacks,   // the readbacks in flight at their bound
            SkippedEncodes,     // the encode side full (its queue or the slots it holds)
            Error,
            Cancelled,          // dropped by a cancel, an error's cleanup or the deadline
        }

        public struct Row
        {
            public int frame;
            public double real;
            public string shot;
            public Outcome outcome;
            public double readbackMs, writeMs;
        }

        private readonly Row[] _rows;

        public CheckFrameLedger(int limit)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
            _rows = new Row[limit];
        }

        public int Limit => _rows.Length;
        public int Requested { get; private set; }
        public int Written { get; private set; }
        public int SkippedReadbacks { get; private set; }
        public int SkippedEncodes { get; private set; }
        public int Errors { get; private set; }
        public int Cancelled { get; private set; }
        public int Pending => Requested - Written - SkippedReadbacks - SkippedEncodes - Errors - Cancelled;
        /// <summary>Every save asked ended in one outcome.</summary>
        public bool AllAccounted => Pending == 0;
        public bool Full => Requested >= _rows.Length;

        public ref readonly Row this[int index] => ref _rows[index];

        /// <summary>A save asked: its row, pending. -1 when the limit is reached (not asked).</summary>
        public int Ask(int frame, double real, string shot)
        {
            if (Full) return -1;
            int i = Requested++;
            _rows[i] = new Row { frame = frame, real = real, shot = shot, outcome = Outcome.Pending };
            return i;
        }

        public void End(int row, Outcome outcome, double readbackMs = 0.0, double writeMs = 0.0)
        {
            if (_rows[row].outcome != Outcome.Pending) throw new InvalidOperationException("row " + row + " already ended " + _rows[row].outcome);
            _rows[row].outcome = outcome;
            _rows[row].readbackMs = readbackMs;
            _rows[row].writeMs = writeMs;
            switch (outcome)
            {
                case Outcome.Written: Written++; break;
                case Outcome.SkippedReadbacks: SkippedReadbacks++; break;
                case Outcome.SkippedEncodes: SkippedEncodes++; break;
                case Outcome.Error: Errors++; break;
                case Outcome.Cancelled: Cancelled++; break;
                default: throw new ArgumentOutOfRangeException(nameof(outcome));
            }
        }

        public string Describe() =>
            "asked " + Requested + " (limit " + Limit + "): written " + Written + ", skipped (intended) for readbacks in flight " + SkippedReadbacks + " and for the encode side " + SkippedEncodes
            + ", errors " + Errors + ", cancelled " + Cancelled + ", pending " + Pending;

        /// <summary>frames.csv: every row asked, written or not, with its real time.</summary>
        public void WriteCsv(string path)
        {
            var b = new StringBuilder(64 * (Requested + 1));
            b.Append("frame,real,shot,outcome,readbackMs,writeMs\n");
            for (int i = 0; i < Requested; i++)
            {
                ref readonly Row r = ref _rows[i];
                b.Append(r.frame.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.real.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.shot).Append(',')
                    .Append(r.outcome.ToString()).Append(',')
                    .Append(r.readbackMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.writeMs.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
            }

            File.WriteAllText(path, b.ToString());
        }
    }
}
