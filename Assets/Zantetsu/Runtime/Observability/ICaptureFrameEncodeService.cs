using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity submission/completion boundary for PNG encoding.
    /// Implementations never mutate Draft, Registry, or Trace state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accepted is the sole input-ownership transfer point. Backpressured,
    /// NotAccepting, and exceptions leave the submission caller-owned.
    /// </para>
    /// <para>
    /// Phase 1 uses a synchronous main-thread implementation. The lifecycle
    /// methods reserve the future shutdown boundary without creating a thread:
    /// stop acceptance, cancel queued work, collect completions, then join.
    /// </para>
    /// </remarks>
    internal interface ICaptureFrameEncodeService : IDisposable
    {
        int Capacity { get; }

        Guid OwnerToken { get; }

        CaptureFrameEncodeSubmitStatus TrySubmit(
            CaptureFrameEncodeSubmission submission,
            out CaptureFrameWorkToken workToken);

        bool TryCollect(out CaptureFrameEncodeCompletion completion);

        NativeArray<byte> GetEncodedPng(in CaptureFrameWorkToken workToken);

        NativeArray<byte> TakeEncodedPng(in CaptureFrameWorkToken workToken);

        void DisposeEncodedPng(in CaptureFrameWorkToken workToken);

        void ReleaseInput(in CaptureFrameWorkToken workToken);

        void BeginDrain();

        int CancelQueued();

        bool TryJoin();

        void ValidateCollected(in CaptureFrameWorkToken workToken);

        void Acknowledge(in CaptureFrameWorkToken workToken);
    }
}
