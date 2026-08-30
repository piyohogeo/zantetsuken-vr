using System;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Initial Phase 0 evidence backend. Async GPU readback, PNG encoding,
    /// per-frame JSON generation, and backend queues are confined here.
    /// </summary>
    internal sealed class PngJsonCaptureEvidenceBackend : ICaptureEvidenceSession
    {
        private enum SlotState : int { Free = 0, InFlight = 1 }

        private readonly Guid _ownerToken;
        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly ICaptureArtifactStore _artifactStore;
        private readonly SlotState[] _states;
        private readonly long[] _generations;
        private readonly CaptureFrameWorkToken[] _tokens;
        private readonly CaptureFrameEnvelope[] _frames;
        private readonly CaptureSurfaceLease[] _surfaces;
        private readonly CaptureFrameCompletion[] _frameCompletions;
        private readonly CaptureArtifactCompletion[] _artifactCompletions;
        private int _frameHead;
        private int _frameCount;
        private int _artifactHead;
        private int _artifactCount;
        private bool _accepting;
        private bool _disposed;

        internal PngJsonCaptureEvidenceBackend(
            int capacity,
            UnityRenderTextureReadbackDispatcher dispatcher,
            ICaptureArtifactStore artifactStore)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
            if (dispatcher.Capacity < capacity) throw new ArgumentException("Dispatcher capacity must cover backend capacity.", nameof(dispatcher));

            _ownerToken = Guid.NewGuid();
            _states = new SlotState[capacity];
            _generations = new long[capacity];
            _tokens = new CaptureFrameWorkToken[capacity];
            _frames = new CaptureFrameEnvelope[capacity];
            _surfaces = new CaptureSurfaceLease[capacity];
            _frameCompletions = new CaptureFrameCompletion[capacity];
            _artifactCompletions = new CaptureArtifactCompletion[checked(capacity * 2)];
            _accepting = true;
        }

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
            PumpOneCompletedReadback();
            return TryDequeueFrame(out completion);
        }

        public bool TryCollectArtifactCompletion(out CaptureArtifactCompletion completion)
        {
            ThrowIfDisposed();
            if (_artifactCount == 0)
            {
                completion = null;
                return false;
            }

            completion = _artifactCompletions[_artifactHead];
            _artifactCompletions[_artifactHead] = null;
            _artifactHead = (_artifactHead + 1) % _artifactCompletions.Length;
            _artifactCount--;
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
            // pre-readback queue in this backend.
            return 0;
        }

        public bool TryJoin()
        {
            ThrowIfDisposed();
            return !_accepting && _dispatcher.ActiveCount == 0 && !HasInFlight();
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_dispatcher.ActiveCount != 0 || _frameCount != 0 || _artifactCount != 0 || HasInFlight())
            {
                throw new InvalidOperationException("Backend must be drained and all completions collected before disposal.");
            }
            _accepting = false;
            _disposed = true;
        }

        private void PumpOneCompletedReadback()
        {
            if (_frameCount == _frameCompletions.Length || _artifactCompletions.Length - _artifactCount < 2) return;
            if (!_dispatcher.TryCollect(out CaptureFrameReadbackResult result)) return;

            int slot = FindSlot(result.FrameRequest.TraceContext.CaptureFrameId);
            if (slot < 0) throw new InvalidOperationException("Readback completion has no backend slot.");
            CaptureFrameWorkToken token = _tokens[slot];
            CaptureFrameEnvelope frame = _frames[slot];
            CaptureSurfaceLease surface = _surfaces[slot];
            ExceptionDispatchInfo mediaFailure = null;
            NativeArray<byte> png = default;

            try
            {
                if (result.HasError) throw new InvalidOperationException("GPU readback failed.");
                png = CaptureFramePngEncoder.Encode(_dispatcher.GetBuffer(result), frame.PixelLayout);
                CreateArtifacts(frame, token, png);
            }
            catch (Exception ex)
            {
                mediaFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (png.IsCreated) png.Dispose();
                ReleaseReadbackAndSurface(result, surface, token, slot);
            }

            EnqueueFrame(new CaptureFrameCompletion(
                token,
                frame.CaptureFrameId,
                mediaFailure == null ? CaptureFrameCompletionStatus.Succeeded : CaptureFrameCompletionStatus.Failed,
                true,
                mediaFailure == null ? 2 : 0,
                mediaFailure));
        }

        private void CreateArtifacts(CaptureFrameEnvelope frame, in CaptureFrameWorkToken token, NativeArray<byte> png)
        {
            byte[] pngBytes = new byte[png.Length];
            for (int i = 0; i < png.Length; i++) pngBytes[i] = png[i];
            string id = frame.CaptureFrameId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CaptureArtifactDescriptor image = new CaptureArtifactDescriptor(
                "frame/" + id + "/image",
                CaptureArtifactKind.FrameImage,
                "image/png",
                1,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".png",
                pngBytes.LongLength,
                Hash(pngBytes));
            byte[] metadataBytes = PngJsonFrameMetadataCodec.SerializeCanonical(frame, image);
            CaptureArtifactDescriptor metadata = new CaptureArtifactDescriptor(
                "frame/" + id + "/metadata",
                CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json",
                2,
                "frames/" + id + ".json.stage",
                "frames/" + id + ".json",
                metadataBytes.LongLength,
                Hash(metadataBytes));

            StageArtifact(token, frame.CaptureFrameId, image, pngBytes);
            StageArtifact(token, frame.CaptureFrameId, metadata, metadataBytes);
        }

        private void StageArtifact(in CaptureFrameWorkToken token, long frameId, CaptureArtifactDescriptor descriptor, byte[] bytes)
        {
            CaptureArtifactWriteReceipt receipt = null;
            ExceptionDispatchInfo failure = null;
            try
            {
                receipt = _artifactStore.WriteStaging(new CaptureArtifactWriteRequest(descriptor, bytes));
                if (receipt == null || !receipt.IsIssuedFor(_artifactStore, descriptor)) throw new InvalidOperationException("Store returned an invalid receipt.");
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            EnqueueArtifact(new CaptureArtifactCompletion(
                token,
                frameId,
                descriptor,
                failure == null ? CaptureArtifactCompletionStatus.Staged : CaptureArtifactCompletionStatus.Failed,
                receipt,
                failure));
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
            return true;
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

        private static string Hash(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++) { chars[i * 2] = hex[hash[i] >> 4]; chars[i * 2 + 1] = hex[hash[i] & 15]; }
            return new string(chars);
        }
    }
}
