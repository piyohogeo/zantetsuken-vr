using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Main-thread bridge from codec-independent backend completions to Draft
    /// terminal state and Trace. It references no PNG/readback/JSON type.
    /// </summary>
    internal sealed class CaptureEvidenceDraftCoordinator
    {
        private readonly CaptureEvidenceCoordinator _evidence;
        private readonly CaptureFrameDraftRegistry _drafts;
        private readonly CaptureArtifactRegistry _artifacts;
        private readonly CaptureFrameTraceObserver _trace;
        private readonly CaptureFrameWorkToken[] _tokens;
        private readonly CaptureFrameDraft[] _frames;
        private readonly int[] _expectedArtifacts;
        private readonly int[] _receivedArtifacts;
        private readonly long[] _receivedBytes;
        private readonly bool[] _frameCompleted;
        private readonly bool[] _artifactFailed;
        private readonly bool[] _occupied;

        internal CaptureEvidenceDraftCoordinator(
            int capacity,
            CaptureEvidenceCoordinator evidence,
            CaptureFrameDraftRegistry drafts,
            CaptureArtifactRegistry artifacts,
            CaptureFrameTraceObserver trace)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
            _tokens = new CaptureFrameWorkToken[capacity];
            _frames = new CaptureFrameDraft[capacity];
            _expectedArtifacts = new int[capacity];
            _receivedArtifacts = new int[capacity];
            _receivedBytes = new long[capacity];
            _frameCompleted = new bool[capacity];
            _artifactFailed = new bool[capacity];
            _occupied = new bool[capacity];
        }

        internal CaptureSubmitStatus TrySubmit(
            CaptureFrameDraft draft,
            CaptureSurfaceLease surface,
            CaptureColorSpace colorSpace,
            out CaptureFrameWorkToken token)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (surface == null) throw new ArgumentNullException(nameof(surface));
            int slot = FindFree();
            if (slot < 0) { token = default; return CaptureSubmitStatus.Backpressured; }

            int reservedArtifacts = _evidence.MaximumArtifactCountPerSubmission;
            if (!_artifacts.TryReserve(draft.TestRunId, draft.CaptureFrameId, reservedArtifacts))
            {
                token = default;
                return CaptureSubmitStatus.Backpressured;
            }

            CaptureFrameEnvelope envelope = CaptureFrameEnvelope.FromDraft(draft, colorSpace);
            CaptureSubmitStatus status;
            try
            {
                status = _evidence.TrySubmit(envelope, surface, out token);
            }
            catch
            {
                _artifacts.CancelReservation(draft.TestRunId, draft.CaptureFrameId);
                throw;
            }
            if (status != CaptureSubmitStatus.Accepted)
            {
                _artifacts.CancelReservation(draft.TestRunId, draft.CaptureFrameId);
                return status;
            }
            if (token.SlotIndex < 0 || token.SlotIndex >= _tokens.Length) throw new InvalidOperationException("Backend token slot exceeds coordinator capacity.");
            if (_occupied[token.SlotIndex]) throw new InvalidOperationException("Backend reused an occupied work slot.");

            slot = token.SlotIndex;
            _tokens[slot] = token;
            _frames[slot] = draft;
            _occupied[slot] = true;
            return status;
        }

        internal bool TryApplyNextCompletion()
        {
            if (_evidence.TryCollectFrameCompletion(out CaptureFrameCompletion frameCompletion))
            {
                ApplyFrame(frameCompletion);
                return true;
            }

            if (_evidence.TryCollectArtifactCompletion(out CaptureArtifactCompletion artifactCompletion))
            {
                ApplyArtifact(artifactCompletion);
                return true;
            }

            return false;
        }

        internal void BeginDrain() => _evidence.BeginDrain();
        internal int CancelQueued() => _evidence.CancelQueued();
        internal bool TryJoin() => _evidence.TryJoin();

        private void ApplyFrame(in CaptureFrameCompletion completion)
        {
            if (!completion.IsValid) throw new InvalidOperationException("Backend returned an invalid frame completion.");
            int slot = ValidateToken(completion.WorkToken);
            if (_frameCompleted[slot]) throw new InvalidOperationException("Duplicate frame completion.");
            _frameCompleted[slot] = true;
            _expectedArtifacts[slot] = completion.ProducedArtifactCount;
            _artifacts.TrimReservation(completion.WorkToken, completion.ProducedArtifactCount);
            if (completion.Status != CaptureFrameCompletionStatus.Succeeded)
            {
                CompleteDrop(slot, completion.Status == CaptureFrameCompletionStatus.Cancelled
                    ? CaptureFrameDropReason.CaptureCancelled
                    : CaptureFrameDropReason.MediaProcessingFailed);
                return;
            }

            TryCompleteSuccess(slot);
        }

        private void ApplyArtifact(CaptureArtifactCompletion completion)
        {
            if (completion == null) throw new ArgumentNullException(nameof(completion));
            if (!completion.IsValid) throw new InvalidOperationException("Backend returned an invalid artifact completion.");
            int slot = ValidateToken(completion.WorkToken);
            if (!_frameCompleted[slot]) throw new InvalidOperationException("Artifact completion preceded frame completion.");
            if (_receivedArtifacts[slot] >= _expectedArtifacts[slot]) throw new InvalidOperationException("Unexpected or duplicate artifact completion.");

            _receivedArtifacts[slot]++;
            if (completion.Status == CaptureArtifactCompletionStatus.Staged)
            {
                if (!_artifacts.TryRegister(completion.WorkToken, completion.Descriptor, completion.FrameRelation))
                    throw new InvalidOperationException("Reserved artifact registration failed.");
                _receivedBytes[slot] = checked(_receivedBytes[slot] + completion.ByteLength);
            }
            else
            {
                _artifacts.ReleaseFailedArtifact(completion.WorkToken);
                _artifactFailed[slot] = true;
            }

            if (_receivedArtifacts[slot] == _expectedArtifacts[slot])
            {
                if (_artifactFailed[slot]) CompleteDrop(slot, CaptureFrameDropReason.ArtifactWriteFailed);
                else TryCompleteSuccess(slot);
            }
        }

        private void TryCompleteSuccess(int slot)
        {
            if (!_frameCompleted[slot] || _receivedArtifacts[slot] != _expectedArtifacts[slot]) return;
            CaptureFrameDraft draft = _frames[slot];
            _drafts.MarkEvidenceStaged(draft.Request);
            _trace.RecordMediaProcessed(draft.Request.TraceContext, 0.0, _receivedBytes[slot]);
            Clear(slot);
        }

        private void CompleteDrop(int slot, CaptureFrameDropReason reason)
        {
            CaptureFrameDraft draft = _frames[slot];
            _drafts.MarkEvidenceDropped(draft.Request, reason);
            if (!_trace.RecordDraftDropped(_drafts, draft.CaptureFrameId)) throw new InvalidOperationException("Drop trace was not consumable.");
            Clear(slot);
        }

        private int ValidateToken(in CaptureFrameWorkToken token)
        {
            int slot = token.SlotIndex;
            if (!token.IsValid || slot < 0 || slot >= _tokens.Length || !_occupied[slot] || !_tokens[slot].IdenticalTo(token))
            {
                throw new InvalidOperationException("Completion token is stale, duplicate, or foreign.");
            }
            return slot;
        }

        private int FindFree()
        {
            for (int i = 0; i < _occupied.Length; i++) if (!_occupied[i]) return i;
            return -1;
        }

        private void Clear(int slot)
        {
            _tokens[slot] = default;
            _frames[slot] = null;
            _expectedArtifacts[slot] = 0;
            _receivedArtifacts[slot] = 0;
            _receivedBytes[slot] = 0;
            _frameCompleted[slot] = false;
            _artifactFailed[slot] = false;
            _occupied[slot] = false;
        }
    }
}
