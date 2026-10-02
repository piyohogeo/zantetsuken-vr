using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Sandbox
{
    /// <summary>What writes one picture, on the sink's worker thread. It may honour the token; it must not keep the pixels.</summary>
    public interface ICheckFrameWriter
    {
        void Write(NativeArray<byte> rgba, int width, int height, string path, CancellationToken cancel);
    }

    /// <summary>JPEG of the bytes the render texture holds (8-bit RGBA, rows as Unity reads them back), written to the path.</summary>
    public sealed class CheckJpegFrameWriter : ICheckFrameWriter
    {
        public readonly int quality;

        public CheckJpegFrameWriter(int quality = 85) => this.quality = quality;

        public void Write(NativeArray<byte> rgba, int width, int height, string path, CancellationToken cancel)
        {
            NativeArray<byte> jpeg = ImageConversion.EncodeNativeArrayToJPG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height, 0, quality);
            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    file.Write(jpeg.AsReadOnlySpan());
                }
            }
            finally
            {
                jpeg.Dispose();
            }
        }
    }

    /// <summary>
    /// IMAGE RUNS ONLY: the saving side of the check's pictures (docs/diagnostics/check-capture-2026-10-02.md). Whatever
    /// renders hands it a render texture, the frame and the real time (<see cref="Offer"/>); it reads the texture back into
    /// a fixed pool (UnityRenderTextureReadbackDispatcher over CaptureFrameReadbackBufferPool), and one worker thread of its
    /// own writes each picture (<see cref="ICheckFrameWriter"/>). The game never waits for it: the readbacks in flight and
    /// the writes waiting have bounds, and a save asked past a bound is skipped and counted, its row kept with its time
    /// (<see cref="CheckFrameLedger"/>). The Main thread's part is driven each frame by a hidden driver object that lives
    /// only while some sink does (<see cref="CheckFrameSinkDriver"/>), and allocates nothing.
    /// <para>
    /// The ending: <see cref="BeginFinish"/> stops asking and writes what was taken; <see cref="Cancel"/> drops what waits;
    /// the first error cancels. Each has a deadline, past which the rest is cancelled and that is recorded; the Main thread
    /// never joins or sleeps. When nothing is held any more the rows are written (frames.csv), the pool, the dispatcher and
    /// the retired render textures released, and the sink is <see cref="Settled"/>.
    /// </para>
    /// </summary>
    public sealed class CheckFrameSink : IDisposable
    {
        public enum State
        {
            Running,     // asking allowed (once saving)
            Finishing,   // no more asking; writing what was taken
            Cancelling,  // no more asking; dropping what waits
            Finished,    // all released after a finish
            Cancelled,   // all released after a cancel
            Failed,      // all released after an error
        }

        public const int DefaultReadbacksInFlight = 2, DefaultWritesWaiting = 2;

        private static int s_liveSinks, s_liveWorkers;
        /// <summary>Sinks made and not yet settled with everything released.</summary>
        public static int LiveSinks => Volatile.Read(ref s_liveSinks);
        /// <summary>Worker threads started and not yet ended.</summary>
        public static int LiveWorkers => Volatile.Read(ref s_liveWorkers);
        private static readonly List<CheckFrameSink> s_live = new List<CheckFrameSink>();
        /// <summary>The sinks not yet settled (Main thread).</summary>
        public static IReadOnlyList<CheckFrameSink> Live => s_live;

        public readonly string directory, name;
        public readonly int width, height, maxReadbacksInFlight, maxWritesWaiting;
        public readonly double deadlineSeconds;
        public readonly CheckFrameLedger ledger;

        public State Now { get; private set; } = State.Running;
        public bool Saving { get; private set; }
        public string Why { get; private set; } = "";
        public bool DeadlineExceeded { get; private set; }
        /// <summary>What was still held when the deadline passed (empty if it never did).</summary>
        public string HeldAtDeadline { get; private set; } = "";
        public bool Settled => Now == State.Finished || Now == State.Cancelled || Now == State.Failed;
        public int ReadbacksInFlight { get; private set; }
        public int MostReadbacksInFlight { get; private set; }
        public int MostWritesWaiting { get; private set; }
        public int WritesWaiting => Volatile.Read(ref _waiting);
        public double WriteMsTotal { get; private set; }
        public double WriteMsMost { get; private set; }
        /// <summary>
        /// The Main thread's longest single Offer and Pump (ms) after each one's first call that did work: what the game paid
        /// at most in one call once going. The first such calls are kept apart (<see cref="OfferMsFirst"/>,
        /// <see cref="PumpMsFirst"/>): a first readback and a first release have been seen to cost more (cause not established).
        /// </summary>
        public double OfferMsMost { get; private set; }
        public double PumpMsMost { get; private set; }
        public double OfferMsFirst { get; private set; } = -1.0;
        /// <summary>Offers after the first that took over 1 ms and over 4 ms: how often, not only the most.</summary>
        public int OffersOver1Ms { get; private set; }
        public int OffersOver4Ms { get; private set; }
        public double PumpMsFirst { get; private set; } = -1.0;
        /// <summary>The settling pump's own Main-thread part (ms): the pool, the dispatcher and the textures released.</summary>
        public double SettleMs { get; private set; } = -1.0;
        /// <summary>frames.csv written (by the worker, once nothing was pending), or why not.</summary>
        public string RowsWritten { get; private set; } = "not yet";
        public bool Released { get; private set; }

        private readonly ICheckFrameWriter _writer;
        private CaptureFrameReadbackBufferPool _pool;
        private UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly CaptureFrameReadbackResult[] _held;   // by pool slot: read back, given to the worker, not yet released
        private readonly bool[] _holding;
        private readonly long[] _askedAt;                      // by ledger row: the Stopwatch time the readback started
        private readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();
        private readonly ConcurrentQueue<Done> _done = new ConcurrentQueue<Done>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly Thread _worker;
        private readonly List<RenderTexture> _retired = new List<RenderTexture>();
        private int _waiting, _workerEnded, _stopWorker, _rowsFailed;
        private string _rowsError;
        private long _deadlineAt;
        private bool _workerStopAsked;

        private struct Job
        {
            public int slot, row, frame;
            public NativeArray<byte> pixels;
        }

        private struct Done
        {
            public int slot, row;
            public CheckFrameLedger.Outcome outcome;
            public long ticks;
            public string error;
        }

        public CheckFrameSink(string name, string directory, int width, int height, int limit, ICheckFrameWriter writer,
            int maxReadbacksInFlight = DefaultReadbacksInFlight, int maxWritesWaiting = DefaultWritesWaiting, double deadlineSeconds = 10.0)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (maxReadbacksInFlight < 1 || maxWritesWaiting < 1) throw new ArgumentOutOfRangeException(nameof(maxReadbacksInFlight));
            this.name = name;
            this.directory = directory;
            this.width = width;
            this.height = height;
            this.maxReadbacksInFlight = maxReadbacksInFlight;
            this.maxWritesWaiting = maxWritesWaiting;
            this.deadlineSeconds = deadlineSeconds;
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            ledger = new CheckFrameLedger(limit);
            Directory.CreateDirectory(directory);

            // The slots: the readbacks in flight, the writes waiting and the one being written; each held until written.
            int slots = maxReadbacksInFlight + maxWritesWaiting + 1;
            _pool = new CaptureFrameReadbackBufferPool(slots, width * height * 4);
            _dispatcher = new UnityRenderTextureReadbackDispatcher(_pool);
            _held = new CaptureFrameReadbackResult[slots];
            _holding = new bool[slots];
            _askedAt = new long[limit];

            Interlocked.Increment(ref s_liveSinks);
            s_live.Add(this);
            _worker = new Thread(WorkerLoop) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Check Frame Writer " + name };
            Interlocked.Increment(ref s_liveWorkers);
            _worker.Start();
            CheckFrameSinkDriver.Ensure();
        }

        public void StartSaving()
        {
            if (Now == State.Running) Saving = true;
        }

        /// <summary>
        /// A rendered frame offered for saving (Main thread, after the texture's render): asked when saving and under the
        /// limit -- then read back, or skipped at the readbacks' bound or with no slot free. Never waits.
        /// </summary>
        public void Offer(RenderTexture source, int frame, double real, string shot)
        {
            if (Now != State.Running || !Saving || ledger.Full || source == null) return;
            long begin = Stopwatch.GetTimestamp();
            int row = ledger.Ask(frame, real, shot);
            if (ReadbacksInFlight >= maxReadbacksInFlight)
            {
                ledger.End(row, CheckFrameLedger.Outcome.SkippedReadbacks);
            }
            else
            {
                var request = new CaptureFrameRequest(new CaptureFrameTraceContext(begin, frame, 0, 0, row, 0, 0, 0, 0, 0, 0, 0),
                    CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, width, height), 0, CapturePixelFormat.Rgba32);
                bool started;
                try
                {
                    started = _dispatcher.TryStart(in request, source);
                }
                catch (Exception e)
                {
                    ledger.End(row, CheckFrameLedger.Outcome.Error);
                    Fail("the readback of frame " + frame + " could not start: " + e.GetType().Name + ": " + e.Message);
                    NoteOffer(Ms(Stopwatch.GetTimestamp() - begin));
                    return;
                }

                if (started)
                {
                    _askedAt[row] = begin;
                    ReadbacksInFlight++;
                    if (ReadbacksInFlight > MostReadbacksInFlight) MostReadbacksInFlight = ReadbacksInFlight;
                }
                else
                {
                    ledger.End(row, CheckFrameLedger.Outcome.SkippedEncodes);   // every slot held by writes
                }
            }

            NoteOffer(Ms(Stopwatch.GetTimestamp() - begin));
        }

        private void NoteOffer(double ms)
        {
            if (OfferMsFirst < 0.0)
            {
                OfferMsFirst = ms;
                return;
            }

            OfferMsMost = Math.Max(OfferMsMost, ms);
            if (ms > 1.0) OffersOver1Ms++;
            if (ms > 4.0) OffersOver4Ms++;
        }

        /// <summary>Stop asking; write what was taken; release everything (the deadline cancels the rest).</summary>
        public void BeginFinish(string why)
        {
            if (Now != State.Running) return;
            Now = State.Finishing;
            Why = why;
            Saving = false;
            _deadlineAt = Stopwatch.GetTimestamp() + (long)(deadlineSeconds * Stopwatch.Frequency);
        }

        /// <summary>Stop asking; drop what waits (counted cancelled); tell the write in progress; release everything.</summary>
        public void Cancel(string why)
        {
            if (Settled || Now == State.Cancelling) return;
            if (Now == State.Running) _deadlineAt = Stopwatch.GetTimestamp() + (long)(deadlineSeconds * Stopwatch.Frequency);
            Now = State.Cancelling;
            Why = Why.Length > 0 ? Why + "; cancelled: " + why : "cancelled: " + why;
            Saving = false;
            _cancel.Cancel();
            _signal.Release();
        }

        /// <summary>A texture the caller no longer draws into, released once no readback of it can be in flight.</summary>
        public void RetireSource(RenderTexture texture)
        {
            if (texture == null) return;
            if (Released || ReadbacksInFlight == 0) { Release(texture); return; }   // no readback of it can be in flight
            _retired.Add(texture);
        }

        /// <summary>The Main thread's part, each frame (driven by the sink itself; callable from a test): collect, hand over, release, end.</summary>
        public void Pump()
        {
            if (Released) return;
            try
            {
                PumpOnce();
            }
            catch (Exception e)
            {
                Fail("the Main thread's part threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void PumpOnce()
        {
            long begin = Stopwatch.GetTimestamp();
            bool cancelling = Now == State.Cancelling || _failed;
            bool work = false;

            // The readbacks done: to the worker, or skipped when the writes waiting are at their bound; dropped when cancelling.
            while (_dispatcher.TryCollect(out CaptureFrameReadbackResult result))
            {
                work = true;
                ReadbacksInFlight--;
                int row = (int)result.FrameRequest.TraceContext.CaptureFrameId;
                double readbackMs = Ms(begin - _askedAt[row]);
                if (result.HasError)
                {
                    _dispatcher.Release(in result);
                    ledger.End(row, CheckFrameLedger.Outcome.Error, readbackMs);
                    Fail("the readback of frame " + ledger[row].frame + " failed");
                    cancelling = true;
                    continue;
                }

                if (cancelling)
                {
                    _dispatcher.Release(in result);
                    ledger.End(row, CheckFrameLedger.Outcome.Cancelled, readbackMs);
                    continue;
                }

                if (Volatile.Read(ref _waiting) >= maxWritesWaiting)
                {
                    _dispatcher.Release(in result);
                    ledger.End(row, CheckFrameLedger.Outcome.SkippedEncodes, readbackMs);
                    continue;
                }

                int slot = result.BufferSlotIndex;
                _held[slot] = result;
                _holding[slot] = true;
                _askedAt[row] = Stopwatch.GetTimestamp() - _askedAt[row];   // now the readback's duration, in ticks
                int waiting = Interlocked.Increment(ref _waiting);
                if (waiting > MostWritesWaiting) MostWritesWaiting = waiting;
                _jobs.Enqueue(new Job { slot = slot, row = row, frame = ledger[row].frame, pixels = _dispatcher.GetBuffer(in result) });
                _signal.Release();
            }

            // The writes ended: their slots back, their rows ended.
            while (_done.TryDequeue(out Done d))
            {
                work = true;
                _dispatcher.Release(in _held[d.slot]);
                _holding[d.slot] = false;
                _held[d.slot] = default;
                double writeMs = Ms(d.ticks);
                ledger.End(d.row, d.outcome, Ms(_askedAt[d.row]), writeMs);
                if (d.outcome == CheckFrameLedger.Outcome.Written)
                {
                    WriteMsTotal += writeMs;
                    WriteMsMost = Math.Max(WriteMsMost, writeMs);
                }
                else if (d.outcome == CheckFrameLedger.Outcome.Error)
                {
                    Fail(d.error);
                }
            }

            if (Now == State.Finishing || Now == State.Cancelling) Ending();
            double ms = Ms(Stopwatch.GetTimestamp() - begin);
            if (work && PumpMsFirst < 0.0) PumpMsFirst = ms;
            else PumpMsMost = Math.Max(PumpMsMost, ms);
        }

        private bool _failed;

        private void Fail(string why)
        {
            if (_failed) return;
            _failed = true;
            Why = Why.Length > 0 ? Why + "; error: " + why : "error: " + why;
            if (Now == State.Running) _deadlineAt = Stopwatch.GetTimestamp() + (long)(deadlineSeconds * Stopwatch.Frequency);
            Now = State.Cancelling;
            Saving = false;
            _cancel.Cancel();
            _signal.Release();
        }

        // Finishing or cancelling: the deadline; once nothing is in flight or held, the worker stopped; once it has ended, all released.
        private void Ending()
        {
            if (!DeadlineExceeded && Stopwatch.GetTimestamp() > _deadlineAt)
            {
                DeadlineExceeded = true;
                HeldAtDeadline = Holding();
                Why += "; the deadline (" + deadlineSeconds.ToString("R") + " s) passed with " + HeldAtDeadline;
                if (Now == State.Finishing)
                {
                    Now = State.Cancelling;
                    _cancel.Cancel();
                    _signal.Release();
                }
            }

            bool anyHeld = false;
            for (int i = 0; i < _holding.Length; i++) anyHeld |= _holding[i];
            if (ReadbacksInFlight > 0 || anyHeld) return;
            if (!_workerStopAsked)
            {
                _workerStopAsked = true;
                Volatile.Write(ref _stopWorker, 1);
                _signal.Release();
            }

            if (Volatile.Read(ref _workerEnded) == 0) return;
            long settle = Stopwatch.GetTimestamp();
            Settle();
            SettleMs = Ms(Stopwatch.GetTimestamp() - settle);
        }

        private string Holding()
        {
            int held = 0;
            for (int i = 0; i < _holding.Length; i++) if (_holding[i]) held++;
            return "readbacks in flight " + ReadbacksInFlight + ", slots held by writes " + held + ", writes waiting " + Volatile.Read(ref _waiting) + ", worker " + (Volatile.Read(ref _workerEnded) == 1 ? "ended" : "running");
        }

        private void Settle()
        {
            if (Volatile.Read(ref _rowsFailed) == 1)
            {
                RowsWritten = "frames.csv NOT written: " + _rowsError;
                Why += "; " + RowsWritten;
                _failed = true;
            }
            else
            {
                RowsWritten = "frames.csv written by the worker (" + ledger.Requested + " rows)";
            }

            _dispatcher.Dispose();
            _dispatcher = null;
            _pool.Dispose();
            _pool = null;
            foreach (RenderTexture t in _retired) Release(t);
            _retired.Clear();
            _signal.Dispose();
            _cancel.Dispose();
            Released = true;
            Now = _failed ? State.Failed : Now == State.Finishing ? State.Finished : State.Cancelled;
            s_live.Remove(this);
            Interlocked.Decrement(ref s_liveSinks);
            if (s_live.Count == 0) CheckFrameSinkDriver.Remove();
        }

        private static void Release(RenderTexture t)
        {
            if (t == null) return;
            t.Release();
            UnityEngine.Object.Destroy(t);
        }

        private void WorkerLoop()
        {
            try
            {
                while (true)
                {
                    _signal.Wait();
                    while (_jobs.TryDequeue(out Job job))
                    {
                        Interlocked.Decrement(ref _waiting);
                        var done = new Done { slot = job.slot, row = job.row };
                        if (_cancel.IsCancellationRequested)
                        {
                            done.outcome = CheckFrameLedger.Outcome.Cancelled;
                            _done.Enqueue(done);
                            continue;
                        }

                        long begin = Stopwatch.GetTimestamp();
                        try
                        {
                            _writer.Write(job.pixels, width, height, Path.Combine(directory, "f" + job.frame.ToString("D6") + ".jpg"), _cancel.Token);
                            done.outcome = CheckFrameLedger.Outcome.Written;
                        }
                        catch (OperationCanceledException)
                        {
                            done.outcome = CheckFrameLedger.Outcome.Cancelled;
                        }
                        catch (Exception e)
                        {
                            done.outcome = CheckFrameLedger.Outcome.Error;
                            done.error = "writing frame " + job.frame + ": " + e.GetType().Name + ": " + e.Message;
                        }

                        done.ticks = Stopwatch.GetTimestamp() - begin;
                        _done.Enqueue(done);
                    }

                    if (Volatile.Read(ref _stopWorker) == 1 && _jobs.IsEmpty)
                    {
                        // Asked to stop only when nothing is pending: the rows are final and the Main thread no longer
                        // changes them; written here, off the Main thread.
                        try
                        {
                            ledger.WriteCsv(Path.Combine(directory, "frames.csv"));
                        }
                        catch (Exception e)
                        {
                            _rowsError = e.GetType().Name + ": " + e.Message;
                            Volatile.Write(ref _rowsFailed, 1);
                        }

                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref s_liveWorkers);
                Volatile.Write(ref _workerEnded, 1);
            }
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public string Describe() =>
            name + " " + width + "x" + height + " -> " + directory + ": " + Now + (Why.Length > 0 ? " (" + Why + ")" : "") + "; " + ledger.Describe()
            + "; most readbacks in flight " + MostReadbacksInFlight + " (bound " + maxReadbacksInFlight + "), most writes waiting " + MostWritesWaiting + " (bound " + maxWritesWaiting + ")"
            + "; write ms total " + WriteMsTotal.ToString("F1") + ", most " + WriteMsMost.ToString("F2") + "; Main's most per call: offer " + OfferMsMost.ToString("F3") + " ms (over 1 ms " + OffersOver1Ms + ", over 4 ms " + OffersOver4Ms + "), pump " + PumpMsMost.ToString("F3") + " ms (the first working call: offer " + OfferMsFirst.ToString("F3") + " ms, pump " + PumpMsFirst.ToString("F3") + " ms; the settling pump's own part " + SettleMs.ToString("F3") + " ms)"
            + (DeadlineExceeded ? "; DEADLINE EXCEEDED, held then: " + HeldAtDeadline : "") + "; released " + Released;

        /// <summary>Not a wait: a cancel when still running; the rest is the sink's own, frame by frame.</summary>
        public void Dispose() => Cancel("disposed");

        /// <summary>Each frame (the driver): every live sink pumped.</summary>
        internal static void PumpAll()
        {
            for (int i = s_live.Count - 1; i >= 0; i--)
            {
                if (i < s_live.Count) s_live[i].Pump();
            }
        }
    }
}
