using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Initial Phase 0 evidence backend. Async GPU readback, backend queues,
    /// drain, and join are confined here; PNG encode, canonical JSON, SHA-256,
    /// and durable staging run on the single dedicated evidence worker.
    /// </summary>
    internal sealed class PngJsonCaptureEvidenceBackend : ICaptureEvidenceSession
    {
        private enum SlotState : int { Free = 0, InFlight = 1, WorkerPending = 2, AwaitingCollection = 3 }

        private readonly Guid _ownerToken;
        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly PngJsonCaptureEvidenceWorkerService _worker;
        private readonly SlotState[] _states;
        private readonly long[] _generations;
        private readonly CaptureFrameWorkToken[] _tokens;
        private readonly CaptureFrameEnvelope[] _frames;
        private readonly CaptureSurfaceLease[] _surfaces;
        private readonly int[] _pendingCollections;
        private readonly CaptureFrameCompletion[] _frameCompletions;
        private readonly CaptureArtifactCompletion[] _artifactCompletions;
        private readonly CaptureFrameWorkToken[] _deliveredFrameTokens;
        private readonly int[] _remainingDeliveredArtifacts;
        private int _frameHead;
        private int _frameCount;
        private int _artifactHead;
        private int _artifactCount;
        private int _deliveredFrameCount;
        private bool _accepting;
        private bool _disposed;

        internal PngJsonCaptureEvidenceBackend(
            int capacity,
            UnityRenderTextureReadbackDispatcher dispatcher,
            ICaptureArtifactStore artifactStore)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (artifactStore == null) throw new ArgumentNullException(nameof(artifactStore));
            if (dispatcher.Capacity < capacity) throw new ArgumentException("Dispatcher capacity must cover backend capacity.", nameof(dispatcher));

            _ownerToken = Guid.NewGuid();
            _worker = new PngJsonCaptureEvidenceWorkerService(capacity, PngJsonCaptureFrameEncoder.Create(), artifactStore);
            _states = new SlotState[capacity];
            _generations = new long[capacity];
            _tokens = new CaptureFrameWorkToken[capacity];
            _frames = new CaptureFrameEnvelope[capacity];
            _surfaces = new CaptureSurfaceLease[capacity];
            _pendingCollections = new int[capacity];
            _frameCompletions = new CaptureFrameCompletion[capacity];
            _artifactCompletions = new CaptureArtifactCompletion[checked(capacity * 2)];
            _deliveredFrameTokens = new CaptureFrameWorkToken[capacity];
            _remainingDeliveredArtifacts = new int[capacity];
            _accepting = true;
        }

        public int MaximumArtifactCountPerSubmission => 2;

        public CaptureSubmitStatus TrySubmit(
            CaptureFrameEnvelope frame,
            CaptureSurfaceLease surface,
            out CaptureFrameWorkToken token)
        {
            ThrowIfDisposed();
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            if (!surface.IsCallerOwned) throw new ArgumentException("Surface must be caller-owned.", nameof(surface));
            token = default;
            if (!_accepting) return CaptureSubmitStatus.NotAccepting;

            int slot = FindFreeSlot();
            if (slot < 0) return CaptureSubmitStatus.Backpressured;
            if (_generations[slot] == long.MaxValue) throw new OverflowException("Backend slot generation exhausted.");

            long generation = _generations[slot] + 1;
            CaptureFrameWorkToken issued = new CaptureFrameWorkToken(
                _ownerToken, slot, generation, frame.TestRunId, frame.CaptureFrameId);

            // Start first; false/exception preserves caller ownership. Once the
            // dispatcher accepts, TransferToBackend is deterministic.
            if (!_dispatcher.TryStart(frame.Request, surface.GetSurfaceForCaller()))
            {
                return CaptureSubmitStatus.Backpressured;
            }

            surface.TransferToBackend(_ownerToken, issued);
            _generations[slot] = generation;
            _tokens[slot] = issued;
            _frames[slot] = frame;
            _surfaces[slot] = surface;
            _states[slot] = SlotState.InFlight;
            token = issued;
            return CaptureSubmitStatus.Accepted;
        }

        public bool TryCollectFrameCompletion(out CaptureFrameCompletion completion)
        {
            ThrowIfDisposed();
            if (TryDequeueFrame(out completion)) return true;
            Pump();
            return TryDequeueFrame(out completion);
        }

        public bool TryCollectArtifactCompletion(out CaptureArtifactCompletion completion)
        {
            ThrowIfDisposed();
            Pump();
            if (_artifactCount == 0)
            {
                completion = null;
                return false;
            }

            CaptureArtifactCompletion pending = _artifactCompletions[_artifactHead];
            int deliveredIndex = FindDeliveredFrame(pending.WorkToken);
            if (deliveredIndex < 0)
            {
                completion = null;
                return false;
            }

            completion = pending;
            _artifactCompletions[_artifactHead] = null;
            _artifactHead = (_artifactHead + 1) % _artifactCompletions.Length;
            _artifactCount--;
            _remainingDeliveredArtifacts[deliveredIndex]--;
            if (_remainingDeliveredArtifacts[deliveredIndex] == 0) RemoveDeliveredFrame(deliveredIndex);
            ReleaseCompletion(pending.WorkToken.SlotIndex);
            return true;
        }

        public void BeginDrain()
        {
            ThrowIfDisposed();
            _accepting = false;
        }

        public int CancelQueued()
        {
            ThrowIfDisposed();
            // AsyncGPUReadback requests are already in flight; there is no
            // pre-readback queue in this backend. Accepted work drains.
            return 0;
        }

        public bool TryJoin()
        {
            ThrowIfDisposed();
            if (_accepting || _dispatcher.ActiveCount != 0 || HasInFlight())
            {
                return false;
            }

            // All readbacks are collected and submitted; the worker can stop
            // accepting and drain. The signal is idempotent.
            _worker.BeginDrain();
            return _worker.TryJoin();
        }

        internal bool WaitForCompletion(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            Pump();
            if (_frameCount > 0 || _artifactCount > 0)
            {
                return true;
            }

            if (!_worker.WaitForCompletion(timeoutMilliseconds))
            {
                return false;
            }

            Pump();
            return _frameCount > 0 || _artifactCount > 0;
        }

        internal bool WaitForJoin(int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            if (_accepting || _dispatcher.ActiveCount != 0 || HasInFlight())
            {
                return false;
            }

            _worker.BeginDrain();
            return _worker.WaitForJoin(timeoutMilliseconds);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_dispatcher.ActiveCount != 0 || _frameCount != 0 || _artifactCount != 0 || _deliveredFrameCount != 0 || HasInFlight())
            {
                throw new InvalidOperationException("Backend must be drained and all completions collected before disposal.");
            }

            _worker.Dispose();
            _accepting = false;
            _disposed = true;
        }

        private void Pump()
        {
            PumpCompletedReadbacks();
            PumpWorkerCompletions();
        }

        private void PumpCompletedReadbacks()
        {
            while (true)
            {
                if (!_dispatcher.TryCollect(out CaptureFrameReadbackResult result)) return;

                int slot = FindSlot(result.FrameRequest.TraceContext.CaptureFrameId);
                if (slot < 0) throw new InvalidOperationException("Readback completion has no backend slot.");
                CaptureFrameWorkToken token = _tokens[slot];
                CaptureFrameEnvelope frame = _frames[slot];
                CaptureSurfaceLease surface = _surfaces[slot];

                if (result.HasError)
                {
                    ReleaseReadbackAndSurface(result, surface, token, slot);
                    EnqueueFrame(new CaptureFrameCompletion(
                        token,
                        frame.CaptureFrameId,
                        CaptureFrameCompletionStatus.Failed,
                        true,
                        0,
                        ExceptionDispatchInfo.Capture(new InvalidOperationException("GPU readback failed."))));
                    continue;
                }

                CaptureFrameReadbackPayloadLease payload;
                try
                {
                    payload = new CaptureFrameReadbackPayloadLease(_dispatcher, result);
                }
                catch (Exception payloadFailure)
                {
                    ReleaseReadbackAndSurface(result, surface, token, slot);
                    EnqueueFrame(new CaptureFrameCompletion(
                        token,
                        frame.CaptureFrameId,
                        CaptureFrameCompletionStatus.Failed,
                        true,
                        0,
                        ExceptionDispatchInfo.Capture(payloadFailure)));
                    continue;
                }

                PngJsonCaptureEvidenceSubmission submission = new PngJsonCaptureEvidenceSubmission(frame, payload, token);
                PngJsonCaptureFrameEncodeSubmitStatus status = _worker.TrySubmit(submission, out _);
                if (status == PngJsonCaptureFrameEncodeSubmitStatus.Accepted)
                {
                    // Confirm acceptance into backend state first so the worker
                    // completion stays recoverable even if the surface release
                    // throws below.
                    _states[slot] = SlotState.WorkerPending;

                    // The raw readback is now captured by the worker-owned
                    // payload; the surface is no longer needed. A failure
                    // before the pool side effect leaves the lease
                    // backend-owned and retryable, so keep the reference and
                    // retry it at completion apply.
                    try
                    {
                        surface.ReleaseFromBackend(_ownerToken, token);
                        _surfaces[slot] = null;
                    }
                    catch
                    {
                        // Deferred; _surfaces[slot] stays non-null for retry.
                    }
                }
                else
                {
                    // Backend and worker capacities match, so this is defensive:
                    // recover the raw result and surface on the main thread.
                    try
                    {
                        payload.ReleaseByCaller();
                    }
                    finally
                    {
                        ReleaseSurfaceAndClearSlot(surface, token, slot);
                    }

                    EnqueueFrame(new CaptureFrameCompletion(
                        token,
                        frame.CaptureFrameId,
                        CaptureFrameCompletionStatus.Failed,
                        true,
                        0,
                        ExceptionDispatchInfo.Capture(new InvalidOperationException("The evidence worker did not accept a collected readback."))));
                }
            }
        }

        private void PumpWorkerCompletions()
        {
            while (_worker.TryCollect(out PngJsonCaptureEvidenceWorkCompletion completion))
            {
                ApplyWorkerCompletion(completion);
            }
        }

        private void ApplyWorkerCompletion(in PngJsonCaptureEvidenceWorkCompletion completion)
        {
            CaptureFrameWorkToken workerToken = completion.WorkToken;
            CaptureFrameCompletion frameCompletion = completion.FrameCompletion;
            int slot = frameCompletion.WorkToken.SlotIndex;

            if (slot < 0 || slot >= _states.Length || _states[slot] != SlotState.WorkerPending ||
                !_tokens[slot].IdenticalTo(frameCompletion.WorkToken))
            {
                throw new InvalidOperationException("Worker completion has no matching backend slot.");
            }

            try
            {
                // The raw dispatcher slot is released exactly once here.
                _worker.ReleaseInput(workerToken);
            }
            finally
            {
                _worker.Acknowledge(workerToken);
                // The slot stays non-reusable until the frame completion and
                // its declared artifact completions have all been collected.
                _states[slot] = SlotState.AwaitingCollection;
                _pendingCollections[slot] = 1 + frameCompletion.ProducedArtifactCount;
                _tokens[slot] = default;
                _frames[slot] = null;
            }

            EnqueueFrame(frameCompletion);
            if (completion.ImageArtifact != null) EnqueueArtifact(completion.ImageArtifact);
            if (completion.MetadataArtifact != null) EnqueueArtifact(completion.MetadataArtifact);

            // Secondary: retry a deferred surface release (best effort, after
            // the raw slot is released and the completions are published).
            CaptureSurfaceLease surface = _surfaces[slot];
            if (surface != null)
            {
                try
                {
                    surface.ReleaseFromBackend(_ownerToken, frameCompletion.WorkToken);
                }
                catch
                {
                    // The surface release remains unavailable; the raw payload
                    // recovery above is unaffected.
                }
                finally
                {
                    _surfaces[slot] = null;
                }
            }
        }

        private void EnqueueFrame(in CaptureFrameCompletion completion)
        {
            int tail = (_frameHead + _frameCount) % _frameCompletions.Length;
            _frameCompletions[tail] = completion;
            _frameCount++;
        }

        private void EnqueueArtifact(CaptureArtifactCompletion completion)
        {
            int tail = (_artifactHead + _artifactCount) % _artifactCompletions.Length;
            _artifactCompletions[tail] = completion;
            _artifactCount++;
        }

        private bool TryDequeueFrame(out CaptureFrameCompletion completion)
        {
            if (_frameCount == 0) { completion = default; return false; }
            completion = _frameCompletions[_frameHead];
            _frameCompletions[_frameHead] = default;
            _frameHead = (_frameHead + 1) % _frameCompletions.Length;
            _frameCount--;
            if (completion.ProducedArtifactCount > 0) AddDeliveredFrame(completion.WorkToken, completion.ProducedArtifactCount);
            ReleaseCompletion(completion.WorkToken.SlotIndex);
            return true;
        }

        private void AddDeliveredFrame(in CaptureFrameWorkToken token, int artifactCount)
        {
            if (_deliveredFrameCount == _deliveredFrameTokens.Length) throw new InvalidOperationException("Delivered-frame gate is full.");
            if (FindDeliveredFrame(token) >= 0) throw new InvalidOperationException("Frame completion was delivered twice.");
            _deliveredFrameTokens[_deliveredFrameCount] = token;
            _remainingDeliveredArtifacts[_deliveredFrameCount] = artifactCount;
            _deliveredFrameCount++;
        }

        private int FindDeliveredFrame(in CaptureFrameWorkToken token)
        {
            for (int i = 0; i < _deliveredFrameCount; i++)
                if (_deliveredFrameTokens[i].IdenticalTo(token)) return i;
            return -1;
        }

        private void RemoveDeliveredFrame(int index)
        {
            int last = _deliveredFrameCount - 1;
            if (index != last)
            {
                _deliveredFrameTokens[index] = _deliveredFrameTokens[last];
                _remainingDeliveredArtifacts[index] = _remainingDeliveredArtifacts[last];
            }
            _deliveredFrameTokens[last] = default;
            _remainingDeliveredArtifacts[last] = 0;
            _deliveredFrameCount--;
        }

        private void ReleaseCompletion(int slot)
        {
            if (_states[slot] != SlotState.AwaitingCollection)
            {
                return;
            }

            _pendingCollections[slot]--;
            if (_pendingCollections[slot] == 0)
            {
                _states[slot] = SlotState.Free;
                _surfaces[slot] = null;
            }
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < _states.Length; i++) if (_states[i] == SlotState.Free) return i;
            return -1;
        }

        private bool HasInFlight()
        {
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != SlotState.Free) return true;
            }
            return false;
        }

        private int FindSlot(long frameId)
        {
            for (int i = 0; i < _states.Length; i++)
                if (_states[i] == SlotState.InFlight && _tokens[i].CaptureFrameId == frameId) return i;
            return -1;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PngJsonCaptureEvidenceBackend));
        }

        private void ReleaseReadbackAndSurface(
            in CaptureFrameReadbackResult result,
            CaptureSurfaceLease surface,
            in CaptureFrameWorkToken token,
            int slot)
        {
            ExceptionDispatchInfo releaseFailure = null;
            try
            {
                _dispatcher.Release(result);
            }
            catch (Exception ex)
            {
                releaseFailure = ExceptionDispatchInfo.Capture(ex);
            }

            try
            {
                surface.ReleaseFromBackend(_ownerToken, token);
            }
            finally
            {
                _states[slot] = SlotState.Free;
                _tokens[slot] = default;
                _frames[slot] = null;
                _surfaces[slot] = null;
            }

            releaseFailure?.Throw();
        }

        private void ReleaseSurfaceAndClearSlot(CaptureSurfaceLease surface, in CaptureFrameWorkToken token, int slot)
        {
            try
            {
                surface.ReleaseFromBackend(_ownerToken, token);
            }
            finally
            {
                _states[slot] = SlotState.Free;
                _tokens[slot] = default;
                _frames[slot] = null;
                _surfaces[slot] = null;
            }
        }
    }
}
