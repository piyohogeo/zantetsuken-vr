using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity Phase 1 encoder. Submission, encoding, and completion
    /// publication all execute synchronously on the constructing thread.
    /// </summary>
    /// <remarks>
    /// No thread, Task, Job, raw-buffer copy, Registry access, Draft access, or
    /// Trace access is performed. A slot is not reusable until its completion
    /// has been collected and acknowledged.
    /// </remarks>
    internal sealed class SynchronousCaptureFrameEncodeService : ICaptureFrameEncodeService
    {
        private enum SlotState : int
        {
            Free = 0,
            Completed = 1,
            Collected = 2
        }

        private readonly Guid _ownerToken;
        private readonly int _constructingThreadId;
        private readonly SlotState[] _states;
        private readonly long[] _generations;
        private readonly long[] _sequences;
        private readonly CaptureFrameEncodeCompletion[] _completions;
        private readonly CaptureFrameReadbackPayloadLease[] _payloads;
        private readonly NativeArray<byte>[] _pngs;
        private long _nextSequence;
        private bool _accepting;
        private bool _disposed;

        public int Capacity => _states.Length;

        public Guid OwnerToken => _ownerToken;

        internal SynchronousCaptureFrameEncodeService(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _ownerToken = Guid.NewGuid();
            _constructingThreadId = Environment.CurrentManagedThreadId;
            _states = new SlotState[capacity];
            _generations = new long[capacity];
            _sequences = new long[capacity];
            _completions = new CaptureFrameEncodeCompletion[capacity];
            _payloads = new CaptureFrameReadbackPayloadLease[capacity];
            _pngs = new NativeArray<byte>[capacity];
            _nextSequence = 0;
            _accepting = true;
            _disposed = false;
        }

        public CaptureFrameEncodeSubmitStatus TrySubmit(
            CaptureFrameEncodeSubmission submission,
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
                return CaptureFrameEncodeSubmitStatus.NotAccepting;
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
                return CaptureFrameEncodeSubmitStatus.Backpressured;
            }

            long generation = checked(_generations[slot] + 1);
            long sequence = checked(_nextSequence + 1);
            CaptureFrameWorkToken acceptedToken = new CaptureFrameWorkToken(
                _ownerToken,
                slot,
                generation,
                testRunId,
                captureFrameId);

            // Linearization point: everything that may reject without taking
            // ownership is complete. Only now consume the submission payload.
            CaptureFrameReadbackPayloadLease payload = submission.Accept(_ownerToken, acceptedToken);
            _generations[slot] = generation;
            _sequences[slot] = sequence;
            _nextSequence = sequence;
            _payloads[slot] = payload;
            NativeArray<byte> png = default;
            ExceptionDispatchInfo failure = null;
            double elapsedMilliseconds = 0.0;
            long startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                NativeArray<byte> raw = payload.GetBufferForService(_ownerToken, acceptedToken);
                png = CaptureFramePngEncoder.Encode(raw, submission.FrameRequest.PixelLayout);
                long endTimestamp = Stopwatch.GetTimestamp();
                elapsedMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            payload.TransferToCompletion(_ownerToken, acceptedToken);

            try
            {
                _completions[slot] = new CaptureFrameEncodeCompletion(
                    acceptedToken,
                    submission.FrameRequest,
                    failure == null
                        ? CaptureFrameEncodeCompletionStatus.Succeeded
                        : CaptureFrameEncodeCompletionStatus.Failed,
                    png.IsCreated ? png.Length : 0,
                    elapsedMilliseconds,
                    failure);
                _pngs[slot] = png;
                _states[slot] = SlotState.Completed;
                workToken = acceptedToken;
                png = default;
                return CaptureFrameEncodeSubmitStatus.Accepted;
            }
            catch
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }

                throw;
            }
        }

        public bool TryCollect(out CaptureFrameEncodeCompletion completion)
        {
            EnsureConstructingThread();
            int selected = -1;
            long selectedSequence = long.MaxValue;
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] == SlotState.Completed && _sequences[i] < selectedSequence)
                {
                    selected = i;
                    selectedSequence = _sequences[i];
                }
            }

            if (selected < 0)
            {
                completion = default;
                return false;
            }

            completion = _completions[selected];
            _states[selected] = SlotState.Collected;
            return true;
        }

        public void BeginDrain()
        {
            EnsureConstructingThread();
            _accepting = false;
        }

        public int CancelQueued()
        {
            EnsureConstructingThread();
            // Phase 1 completes work inside TrySubmit; it has no queued state.
            return 0;
        }

        public bool TryJoin()
        {
            EnsureConstructingThread();
            // Phase 1 owns no worker thread and TrySubmit never returns while
            // encoding is running. Uncollected completions do not prevent join.
            return true;
        }

        public NativeArray<byte> GetEncodedPng(in CaptureFrameWorkToken workToken)
        {
            int slot = ValidateCollectedSlot(workToken);
            if (_completions[slot].Status != CaptureFrameEncodeCompletionStatus.Succeeded ||
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
            _sequences[slot] = 0;
            _states[slot] = SlotState.Free;
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _constructingThreadId)
            {
                throw new InvalidOperationException("The synchronous encode service is main-thread only.");
            }

            if (_disposed)
            {
                return;
            }

            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != SlotState.Free)
                {
                    throw new InvalidOperationException("Encode service cannot be disposed while it owns accepted work or a completion.");
                }
            }

            _accepting = false;
            _disposed = true;
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
                throw new InvalidOperationException("The synchronous encode service is main-thread only.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SynchronousCaptureFrameEncodeService));
            }
        }
    }
}
