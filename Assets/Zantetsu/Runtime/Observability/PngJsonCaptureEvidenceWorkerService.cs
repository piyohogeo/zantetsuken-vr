using System;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity Phase 0.1 evidence worker. A single dedicated worker
    /// thread runs PNG encode, descriptor/hash generation, canonical JSON, and
    /// staging writes in FIFO order for accepted work and publishes one frame
    /// completion plus up to two artifact completions per work item.
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
    /// recording, or Draft/Registry mutation; the main-thread applier releases
    /// the raw dispatcher slot exactly once per accepted work item.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCaptureEvidenceWorkerService : IDisposable
    {
        private enum SlotState : int
        {
            Free = 0,
            Queued = 1,
            Processing = 2,
            Completed = 3,
            Collected = 4
        }

        private readonly Guid _ownerToken;
        private readonly int _constructingThreadId;
        private readonly IPngJsonCaptureFrameEncoder _encoder;
        private readonly ICaptureArtifactStore _artifactStore;

        private readonly SlotState[] _states;
        private readonly long[] _generations;
        private readonly long[] _sequences;
        private readonly CaptureFrameReadbackPayloadLease[] _payloads;
        private readonly CaptureFrameEnvelope[] _envelopes;
        private readonly CaptureFrameWorkToken[] _completionTokens;
        private readonly CaptureFrameCompletion[] _frameCompletions;
        private readonly CaptureArtifactCompletion[] _imageArtifacts;
        private readonly CaptureArtifactCompletion[] _metadataArtifacts;

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

        internal int Capacity => _states.Length;

        internal Guid OwnerToken => _ownerToken;

        internal PngJsonCaptureEvidenceWorkerService(
            int capacity,
            IPngJsonCaptureFrameEncoder encoder,
            ICaptureArtifactStore artifactStore)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            if (encoder == null)
            {
                throw new ArgumentNullException(nameof(encoder));
            }

            if (artifactStore == null)
            {
                throw new ArgumentNullException(nameof(artifactStore));
            }

            _ownerToken = Guid.NewGuid();
            _constructingThreadId = Environment.CurrentManagedThreadId;
            _encoder = encoder;
            _artifactStore = artifactStore;

            _states = new SlotState[capacity];
            _generations = new long[capacity];
            _sequences = new long[capacity];
            _payloads = new CaptureFrameReadbackPayloadLease[capacity];
            _envelopes = new CaptureFrameEnvelope[capacity];
            _completionTokens = new CaptureFrameWorkToken[capacity];
            _frameCompletions = new CaptureFrameCompletion[capacity];
            _imageArtifacts = new CaptureArtifactCompletion[capacity];
            _metadataArtifacts = new CaptureArtifactCompletion[capacity];
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
                Name = "ZantetsuEvidenceWorker"
            };
            _worker.Start();
        }

        internal PngJsonCaptureFrameEncodeSubmitStatus TrySubmit(
            PngJsonCaptureEvidenceSubmission submission,
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
            _envelopes[slot] = submission.Frame;
            _completionTokens[slot] = submission.CompletionToken;
            _frameCompletions[slot] = default;
            _imageArtifacts[slot] = null;
            _metadataArtifacts[slot] = null;
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

        internal bool TryCollect(out PngJsonCaptureEvidenceWorkCompletion completion)
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

            completion = new PngJsonCaptureEvidenceWorkCompletion(
                BuildToken(slot),
                _frameCompletions[slot],
                _imageArtifacts[slot],
                _metadataArtifacts[slot]);
            _states[slot] = SlotState.Collected;
            return true;
        }

        internal void ReleaseInput(in CaptureFrameWorkToken workToken)
        {
            int slot = ValidateCollectedSlot(workToken);
            CaptureFrameReadbackPayloadLease payload = _payloads[slot];
            if (payload == null)
            {
                throw new InvalidOperationException("Collected work has no input payload.");
            }

            payload.ReleaseFromCompletion(workToken);
        }

        internal void Acknowledge(in CaptureFrameWorkToken workToken)
        {
            int slot = ValidateCollectedSlot(workToken);
            _frameCompletions[slot] = default;
            _imageArtifacts[slot] = null;
            _metadataArtifacts[slot] = null;
            _payloads[slot] = null;
            _envelopes[slot] = null;
            _completionTokens[slot] = default;
            _sequences[slot] = 0;
            _states[slot] = SlotState.Free;
        }

        internal void BeginDrain()
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

        internal int CancelQueued()
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

        internal bool TryJoin()
        {
            EnsureConstructingThread();
            lock (_gate)
            {
                return !_accepting && _submissionCount == 0 && _workerStopped;
            }
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _constructingThreadId)
            {
                throw new InvalidOperationException("The evidence worker service is constructing-thread only.");
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
                    "Evidence worker service cannot be disposed while its worker is alive; drain and join first.");
            }

            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != SlotState.Free)
                {
                    throw new InvalidOperationException(
                        "Evidence worker service cannot be disposed while it owns accepted work, payloads, or completions.");
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
                        _states[slot] = cancelled ? SlotState.Completed : SlotState.Processing;
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
            CaptureFrameEnvelope frame = _envelopes[slot];
            CaptureFrameWorkToken completionToken = _completionTokens[slot];

            if (cancelled)
            {
                // Queued cancellation never contacts the encoder or store. The
                // payload moves to completion ownership; the main-thread
                // applier releases it exactly once.
                payload.TransferToCompletion(_ownerToken, token);
                _frameCompletions[slot] = new CaptureFrameCompletion(
                    completionToken,
                    frame.CaptureFrameId,
                    CaptureFrameCompletionStatus.Cancelled,
                    true,
                    0,
                    null);
                _imageArtifacts[slot] = null;
                _metadataArtifacts[slot] = null;
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
            ExceptionDispatchInfo mediaFailure = null;
            byte[] pngBytes = null;
            CaptureArtifactDescriptor image = null;
            byte[] metadataBytes = null;
            CaptureArtifactDescriptor metadata = null;

            // Phase 1: encode plus descriptor and hash construction. Any
            // failure maps the frame to Failed with zero artifacts.
            try
            {
                png = _encoder.Encode(raw, frame.PixelLayout);
                if (!png.IsCreated || png.Length == 0)
                {
                    throw new InvalidOperationException("PNG encode produced an empty result.");
                }

                pngBytes = new byte[png.Length];
                for (int i = 0; i < png.Length; i++)
                {
                    pngBytes[i] = png[i];
                }

                string id = frame.CaptureFrameId.ToString(CultureInfo.InvariantCulture);
                image = new CaptureArtifactDescriptor(
                    "frame/" + id + "/image",
                    CaptureArtifactKind.FrameImage,
                    "image/png",
                    1,
                    "frames/" + id + ".png.stage",
                    "frames/" + id + ".png",
                    pngBytes.LongLength,
                    Hash(pngBytes));
                metadataBytes = PngJsonFrameMetadataCodec.SerializeCanonical(frame, image);
                metadata = new CaptureArtifactDescriptor(
                    "frame/" + id + "/metadata",
                    CaptureArtifactKind.FrameMetadata,
                    "application/vnd.zantetsu.capture-frame+json",
                    2,
                    "frames/" + id + ".json.stage",
                    "frames/" + id + ".json",
                    metadataBytes.LongLength,
                    Hash(metadataBytes));
            }
            catch (Exception ex)
            {
                mediaFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }
            }

            if (mediaFailure != null)
            {
                _frameCompletions[slot] = new CaptureFrameCompletion(
                    completionToken,
                    frame.CaptureFrameId,
                    CaptureFrameCompletionStatus.Failed,
                    true,
                    0,
                    mediaFailure);
                _imageArtifacts[slot] = null;
                _metadataArtifacts[slot] = null;
                EnqueueCompletion(slot);
                return;
            }

            // Phase 2: durable staging. Descriptors already exist, so the frame
            // completion stays Succeeded with two artifacts while each artifact
            // reports Staged or Failed individually. A PNG staging failure does
            // not suppress JSON staging.
            _imageArtifacts[slot] = StageArtifact(completionToken, frame.CaptureFrameId, image, pngBytes);
            _metadataArtifacts[slot] = StageArtifact(completionToken, frame.CaptureFrameId, metadata, metadataBytes);
            _frameCompletions[slot] = new CaptureFrameCompletion(
                completionToken,
                frame.CaptureFrameId,
                CaptureFrameCompletionStatus.Succeeded,
                true,
                2,
                null);
            EnqueueCompletion(slot);
        }

        private CaptureArtifactCompletion StageArtifact(
            in CaptureFrameWorkToken completionToken,
            long frameId,
            CaptureArtifactDescriptor descriptor,
            byte[] bytes)
        {
            CaptureArtifactWriteReceipt receipt = null;
            ExceptionDispatchInfo failure = null;
            try
            {
                CaptureArtifactWriteReceipt candidate = _artifactStore.WriteStaging(
                    new CaptureArtifactWriteRequest(descriptor, bytes));
                if (candidate == null || !candidate.IsIssuedFor(_artifactStore, descriptor))
                {
                    throw new InvalidOperationException("Store returned an invalid receipt.");
                }

                receipt = candidate;
            }
            catch (Exception ex)
            {
                receipt = null;
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            return new CaptureArtifactCompletion(
                completionToken,
                frameId,
                descriptor,
                new CaptureArtifactFrameRelation(new[] { frameId }),
                failure == null ? CaptureArtifactCompletionStatus.Staged : CaptureArtifactCompletionStatus.Failed,
                receipt,
                failure);
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
                _envelopes[slot].TestRunId,
                _envelopes[slot].CaptureFrameId);
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
                throw new OverflowException("All free evidence worker slot generations are exhausted.");
            }

            return -1;
        }

        private int ValidateCollectedSlot(in CaptureFrameWorkToken workToken)
        {
            EnsureConstructingThread();
            int slot = ValidateOwnedSlot(workToken);
            if (_states[slot] != SlotState.Collected ||
                !BuildToken(slot).IdenticalTo(workToken))
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
                throw new InvalidOperationException("Work token is stale or belongs to another evidence worker.");
            }

            return workToken.SlotIndex;
        }

        private void EnsureConstructingThread()
        {
            if (Environment.CurrentManagedThreadId != _constructingThreadId)
            {
                throw new InvalidOperationException("The evidence worker service is constructing-thread only.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PngJsonCaptureEvidenceWorkerService));
            }
        }

        private static string Hash(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = hex[hash[i] >> 4];
                chars[i * 2 + 1] = hex[hash[i] & 15];
            }

            return new string(chars);
        }
    }
}
