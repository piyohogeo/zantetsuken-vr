using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity Phase 0.1 worker encoder. Submission happens on the
    /// constructing thread; a single dedicated worker thread runs the encoder
    /// for accepted work in FIFO order and publishes one completion per work
    /// item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capacity, submission FIFO, completion FIFO, and every per-slot state
    /// array are allocated once in the constructor. The run path performs no
    /// managed queue, LINQ, resizable-array, or extra-worker allocation. Queue
    /// synchronization uses short lock sections and a worker signal; capacity
    /// exhaustion never waits. Main-thread APIs are constructing-thread only.
    /// </para>
    /// <para>
    /// A slot is reused only after its completion has been collected, applied,
    /// and acknowledged. The worker performs no dispatcher release, trace
    /// recording, Draft/Registry mutation, PNG queueing, or file I/O.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonWorkerCaptureFrameEncodeService : IPngJsonCaptureFrameEncodeService
    {
        private enum SlotState : int
        {
            Free = 0,
            Queued = 1,
            Encoding = 2,
            Completed = 3,
            Collected = 4
        }

        private readonly Guid _ownerToken;
        private readonly int _constructingThreadId;
        private readonly IPngJsonCaptureFrameEncoder _encoder;

        private readonly SlotState[] _states;
        private readonly long[] _generations;
        private readonly long[] _sequences;
        private readonly PngJsonCaptureFrameEncodeCompletion[] _completions;
        private readonly CaptureFrameReadbackPayloadLease[] _payloads;
        private readonly NativeArray<byte>[] _pngs;
        private readonly CaptureFrameRequest[] _frameRequests;

        private readonly int[] _submissionQueue;
        private readonly int[] _completionQueue;
        private readonly bool[] _cancelPending;

        private readonly object _gate = new object();
        private readonly AutoResetEvent _workSignal = new AutoResetEvent(false);
        private readonly Thread _worker;

        private int _submissionHead;
        private int _submissionTail;
        private int _submissionCount;
        private int _completionHead;
        private int _completionTail;
        private int _completionCount;

        private long _nextSequence;
        private bool _accepting;
        private bool _workerStopped;
        private bool _disposed;

        public int Capacity => _states.Length;

        public Guid OwnerToken => _ownerToken;

        internal PngJsonWorkerCaptureFrameEncodeService(int capacity)
            : this(capacity, PngJsonCaptureFrameEncoder.Create())
        {
        }

        internal PngJsonWorkerCaptureFrameEncodeService(int capacity, IPngJsonCaptureFrameEncoder encoder)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            if (encoder == null)
            {
                throw new ArgumentNullException(nameof(encoder));
            }

            _ownerToken = Guid.NewGuid();
            _constructingThreadId = Environment.CurrentManagedThreadId;
            _encoder = encoder;

            _states = new SlotState[capacity];
            _generations = new long[capacity];
            _sequences = new long[capacity];
            _completions = new PngJsonCaptureFrameEncodeCompletion[capacity];
            _payloads = new CaptureFrameReadbackPayloadLease[capacity];
            _pngs = new NativeArray<byte>[capacity];
            _frameRequests = new CaptureFrameRequest[capacity];
            _submissionQueue = new int[capacity];
            _completionQueue = new int[capacity];
            _cancelPending = new bool[capacity];

            _nextSequence = 0;
            _accepting = true;
            _workerStopped = false;
            _disposed = false;

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ZantetsuPngEncodeWorker"
            };
            _worker.Start();
        }

        public PngJsonCaptureFrameEncodeSubmitStatus TrySubmit(
            PngJsonCaptureFrameEncodeSubmission submission,
            out CaptureFrameWorkToken workToken)
        {
            EnsureConstructingThread();

            if (submission == null)
            {
                throw new ArgumentNullException(nameof(submission));
            }

            if (!submission.HasPayload)
            {
                throw new ArgumentException("Submission must own a readback payload.", nameof(submission));
            }

            workToken = default;
            if (!_accepting)
            {
                return PngJsonCaptureFrameEncodeSubmitStatus.NotAccepting;
            }

            long captureFrameId = submission.FrameRequest.TraceContext.CaptureFrameId;
            long testRunId = submission.FrameRequest.TraceContext.TestRunId;
            if (captureFrameId <= 0 || testRunId <= 0)
            {
                throw new ArgumentException("Submission request IDs must be positive.", nameof(submission));
            }

            int slot = FindReusableSlot();
            if (slot < 0)
            {
                return PngJsonCaptureFrameEncodeSubmitStatus.Backpressured;
            }

            long generation = checked(_generations[slot] + 1);
            long sequence = checked(_nextSequence + 1);
            CaptureFrameWorkToken acceptedToken = new CaptureFrameWorkToken(
                _ownerToken,
                slot,
                generation,
                testRunId,
                captureFrameId);

            // Linearization point: every rejection path has completed. Only now
            // consume the submission payload and publish the exact slot.
            CaptureFrameReadbackPayloadLease payload = submission.Accept(_ownerToken, acceptedToken);
            _generations[slot] = generation;
            _sequences[slot] = sequence;
            _nextSequence = sequence;
            _payloads[slot] = payload;
            _frameRequests[slot] = submission.FrameRequest;
            _states[slot] = SlotState.Queued;

            lock (_gate)
            {
                _submissionQueue[_submissionTail] = slot;
                _submissionTail = (_submissionTail + 1) % _submissionQueue.Length;
                _submissionCount++;
            }

            _workSignal.Set();

            workToken = acceptedToken;
            return PngJsonCaptureFrameEncodeSubmitStatus.Accepted;
        }

        public bool TryCollect(out PngJsonCaptureFrameEncodeCompletion completion)
        {
            EnsureConstructingThread();

            int slot = -1;
            lock (_gate)
            {
                if (_completionCount > 0)
                {
                    slot = _completionQueue[_completionHead];
                    _completionHead = (_completionHead + 1) % _completionQueue.Length;
                    _completionCount--;
                }
            }

            if (slot < 0)
            {
                completion = default;
                return false;
            }

            completion = _completions[slot];
            _states[slot] = SlotState.Collected;
            return true;
        }

        public void BeginDrain()
        {
            EnsureConstructingThread();
            lock (_gate)
            {
                if (!_accepting)
                {
                    return;
                }

                _accepting = false;
            }

            _workSignal.Set();
        }

        public int CancelQueued()
        {
            EnsureConstructingThread();
            int count = 0;
            lock (_gate)
            {
                for (int i = 0; i < _states.Length; i++)
                {
                    if (_states[i] == SlotState.Queued && !_cancelPending[i])
                    {
                        _cancelPending[i] = true;
                        count++;
                    }
                }
            }

            return count;
        }

        public bool TryJoin()
        {
            EnsureConstructingThread();
            lock (_gate)
            {
                return !_accepting && _submissionCount == 0 && _workerStopped;
            }
        }

        public NativeArray<byte> GetEncodedPng(in CaptureFrameWorkToken workToken)
        {
            int slot = ValidateCollectedSlot(workToken);
            if (_completions[slot].Status != PngJsonCaptureFrameEncodeCompletionStatus.Succeeded ||
                !_pngs[slot].IsCreated)
            {
                throw new InvalidOperationException("Collected work does not own an encoded PNG.");
            }

            return _pngs[slot];
        }

        public NativeArray<byte> TakeEncodedPng(in CaptureFrameWorkToken workToken)
        {
            NativeArray<byte> png = GetEncodedPng(workToken);
            _pngs[workToken.SlotIndex] = default;
            return png;
        }

        public void DisposeEncodedPng(in CaptureFrameWorkToken workToken)
        {
            int slot = ValidateCollectedSlot(workToken);
            if (_pngs[slot].IsCreated)
            {
                NativeArray<byte> png = _pngs[slot];
                _pngs[slot] = default;
                png.Dispose();
            }
        }

        public void ReleaseInput(in CaptureFrameWorkToken workToken)
        {
            int slot = ValidateCollectedSlot(workToken);
            CaptureFrameReadbackPayloadLease payload = _payloads[slot];
            if (payload == null)
            {
                throw new InvalidOperationException("Collected work has no input payload.");
            }

            payload.ReleaseFromCompletion(workToken);
        }

        public void ValidateCollected(in CaptureFrameWorkToken workToken)
        {
            EnsureConstructingThread();
            ValidateCollectedSlot(workToken);
        }

        public void Acknowledge(in CaptureFrameWorkToken workToken)
        {
            ValidateCollected(workToken);
            int slot = workToken.SlotIndex;

            if (_pngs[slot].IsCreated)
            {
                throw new InvalidOperationException("Encoded PNG ownership must be transferred or disposed before acknowledgement.");
            }

            _completions[slot] = default;
            _payloads[slot] = null;
            _frameRequests[slot] = default;
            _sequences[slot] = 0;
            _states[slot] = SlotState.Free;
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _constructingThreadId)
            {
                throw new InvalidOperationException("The worker encode service is constructing-thread only.");
            }

            if (_disposed)
            {
                return;
            }

            bool workerStopped;
            lock (_gate)
            {
                workerStopped = _workerStopped;
            }

            if (!workerStopped)
            {
                throw new InvalidOperationException(
                    "Encode service cannot be disposed while its worker is alive; drain and join first.");
            }

            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != SlotState.Free)
                {
                    throw new InvalidOperationException(
                        "Encode service cannot be disposed while it owns accepted work, payloads, or completions.");
                }
            }

            _workSignal.Dispose();
            _disposed = true;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                _workSignal.WaitOne();

                while (true)
                {
                    int slot = -1;
                    bool cancelled = false;
                    lock (_gate)
                    {
                        if (_submissionCount == 0)
                        {
                            break;
                        }

                        slot = _submissionQueue[_submissionHead];
                        _submissionHead = (_submissionHead + 1) % _submissionQueue.Length;
                        _submissionCount--;
                        cancelled = _cancelPending[slot];
                        _cancelPending[slot] = false;
                        _states[slot] = cancelled ? SlotState.Completed : SlotState.Encoding;
                    }

                    ProcessSlot(slot, cancelled);
                }

                lock (_gate)
                {
                    if (!_accepting && _submissionCount == 0)
                    {
                        _workerStopped = true;
                        break;
                    }
                }
            }
        }

        private void ProcessSlot(int slot, bool cancelled)
        {
            CaptureFrameWorkToken token = BuildToken(slot);
            CaptureFrameReadbackPayloadLease payload = _payloads[slot];
            CaptureFrameRequest frameRequest = _frameRequests[slot];

            if (cancelled)
            {
                // Queued cancellation never contacts the encoder. The payload
                // moves to completion ownership; the main-thread applier
                // releases it exactly once.
                payload.TransferToCompletion(_ownerToken, token);
                _completions[slot] = new PngJsonCaptureFrameEncodeCompletion(
                    token,
                    frameRequest,
                    PngJsonCaptureFrameEncodeCompletionStatus.Cancelled,
                    0,
                    0.0,
                    null);
                EnqueueCompletion(slot);
                return;
            }

            // Atomic validation + ownership transfer: for an accepted payload
            // this cannot throw, and the transition to CompletionOwned happens
            // before the encoder runs so a Failed completion can still release
            // the input. A failure here is an internal invariant violation and
            // propagates as a fatal worker fault rather than a completion that
            // would orphan a still-service-owned payload.
            NativeArray<byte> raw = payload.GetBufferAndTransferToCompletion(_ownerToken, token);

            NativeArray<byte> png = default;
            ExceptionDispatchInfo failure = null;
            double elapsedMilliseconds = 0.0;

            long startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                // Post-accept processing boundary: encode and non-empty-output
                // validation. The encoder boundary does not contract a
                // non-empty result, so an empty output is treated as a failed
                // encode instead of escaping the worker loop and orphaning the
                // accepted item.
                png = _encoder.Encode(raw, frameRequest.PixelLayout);
                if (!png.IsCreated || png.Length == 0)
                {
                    throw new InvalidOperationException("PNG encode produced an empty result.");
                }

                long endTimestamp = Stopwatch.GetTimestamp();
                elapsedMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            if (failure != null && png.IsCreated)
            {
                png.Dispose();
                png = default;
            }

            if (failure == null)
            {
                _completions[slot] = new PngJsonCaptureFrameEncodeCompletion(
                    token,
                    frameRequest,
                    PngJsonCaptureFrameEncodeCompletionStatus.Succeeded,
                    png.Length,
                    elapsedMilliseconds,
                    null);
                _pngs[slot] = png;
                png = default;
            }
            else
            {
                _completions[slot] = new PngJsonCaptureFrameEncodeCompletion(
                    token,
                    frameRequest,
                    PngJsonCaptureFrameEncodeCompletionStatus.Failed,
                    0,
                    0.0,
                    failure);
                _pngs[slot] = default;
            }

            EnqueueCompletion(slot);
        }

        private void EnqueueCompletion(int slot)
        {
            lock (_gate)
            {
                _completionQueue[_completionTail] = slot;
                _completionTail = (_completionTail + 1) % _completionQueue.Length;
                _completionCount++;
            }
        }

        private CaptureFrameWorkToken BuildToken(int slot)
        {
            return new CaptureFrameWorkToken(
                _ownerToken,
                slot,
                _generations[slot],
                _frameRequests[slot].TraceContext.TestRunId,
                _frameRequests[slot].TraceContext.CaptureFrameId);
        }

        private int FindReusableSlot()
        {
            bool anyGenerationRemaining = false;
            for (int i = 0; i < _states.Length; i++)
            {
                if (_generations[i] != long.MaxValue)
                {
                    anyGenerationRemaining = true;
                }

                if (_states[i] != SlotState.Free)
                {
                    continue;
                }

                if (_generations[i] == long.MaxValue)
                {
                    continue;
                }

                return i;
            }

            if (!anyGenerationRemaining)
            {
                throw new OverflowException("All free encode service slot generations are exhausted.");
            }

            return -1;
        }

        private int ValidateCollectedSlot(in CaptureFrameWorkToken workToken)
        {
            EnsureConstructingThread();
            int slot = ValidateOwnedSlot(workToken);
            if (_states[slot] != SlotState.Collected ||
                !_completions[slot].WorkToken.IdenticalTo(workToken))
            {
                throw new InvalidOperationException("Work token is not the currently collected completion.");
            }

            return slot;
        }

        private int ValidateOwnedSlot(in CaptureFrameWorkToken workToken)
        {
            if (!workToken.IsValid || workToken.OwnerToken != _ownerToken ||
                workToken.SlotIndex < 0 || workToken.SlotIndex >= _states.Length ||
                _generations[workToken.SlotIndex] != workToken.Generation)
            {
                throw new InvalidOperationException("Work token is stale or belongs to another encode service.");
            }

            return workToken.SlotIndex;
        }

        private void EnsureConstructingThread()
        {
            if (Environment.CurrentManagedThreadId != _constructingThreadId)
            {
                throw new InvalidOperationException("The worker encode service is constructing-thread only.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PngJsonWorkerCaptureFrameEncodeService));
            }
        }
    }
}
