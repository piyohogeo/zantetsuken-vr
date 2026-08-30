using System;
using System.Runtime.ExceptionServices;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Applies immutable encode completions on the main thread. This is the
    /// only Phase 1 component that records the encoded trace and releases the
    /// raw dispatcher payload after encoding.
    /// </summary>
    /// <remarks>
    /// Each token is accepted exactly once. Duplicate, stale, foreign, or
    /// already-acknowledged completions are rejected before any side effect.
    /// The encoded trace is recorded before dispatcher release, preserving the
    /// legacy router order. Registry and Draft transitions remain outside this
    /// coordinator and on the main thread.
    /// </remarks>
    internal sealed class PngJsonCaptureFrameEncodeCompletionCoordinator
    {
        private readonly IPngJsonCaptureFrameEncodeService _service;
        private readonly CaptureFrameTraceObserver _observer;
        private readonly long[] _lastAppliedGenerations;

        internal PngJsonCaptureFrameEncodeCompletionCoordinator(
            IPngJsonCaptureFrameEncodeService service,
            CaptureFrameTraceObserver observer)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            if (service.Capacity <= 0)
            {
                throw new ArgumentException("Encode service capacity must be positive.", nameof(service));
            }

            _service = service;
            _observer = observer;
            _lastAppliedGenerations = new long[service.Capacity];
        }

        internal PngJsonCaptureFrameEncodeApplyResult Apply(in PngJsonCaptureFrameEncodeCompletion completion)
        {
            if (!completion.IsValid)
            {
                throw new ArgumentException("Encode completion must be valid.", nameof(completion));
            }

            CaptureFrameWorkToken token = completion.WorkToken;
            _service.ValidateCollected(token);

            if (token.OwnerToken != _service.OwnerToken ||
                token.SlotIndex < 0 || token.SlotIndex >= _lastAppliedGenerations.Length ||
                token.Generation <= _lastAppliedGenerations[token.SlotIndex])
            {
                throw new InvalidOperationException("Encode completion is duplicate, stale, or belongs to another service.");
            }

            // Exactly-once linearization point. A failure below is
            // non-transactional and must never permit a second application.
            _lastAppliedGenerations[token.SlotIndex] = token.Generation;

            NativeArray<byte> png = default;
            bool transferred = false;
            try
            {
                switch (completion.Status)
                {
                    case PngJsonCaptureFrameEncodeCompletionStatus.Succeeded:
                        png = _service.GetEncodedPng(token);
                        try
                        {
                            _observer.RecordEncoded(
                                completion.FrameRequest.TraceContext,
                                completion.ElapsedMilliseconds,
                                completion.EncodedByteCount);
                        }
                        catch
                        {
                            _service.DisposeEncodedPng(token);
                            png = default;

                            // Preserve legacy behavior: a dispatcher Release
                            // failure replaces the preceding trace failure.
                            _service.ReleaseInput(token);
                            throw;
                        }

                        try
                        {
                            _service.ReleaseInput(token);
                        }
                        catch
                        {
                            _service.DisposeEncodedPng(token);
                            png = default;

                            throw;
                        }

                        png = _service.TakeEncodedPng(token);
                        PngJsonCaptureFrameEncodeApplyResult result = new PngJsonCaptureFrameEncodeApplyResult(
                            completion.FrameRequest,
                            png);
                        transferred = true;
                        png = default;
                        return result;

                    case PngJsonCaptureFrameEncodeCompletionStatus.Failed:
                        _service.ReleaseInput(token);
                        completion.Failure.Throw();
                        throw new InvalidOperationException("Unreachable encode failure continuation.");

                    case PngJsonCaptureFrameEncodeCompletionStatus.Cancelled:
                        _service.ReleaseInput(token);
                        throw new OperationCanceledException("Capture frame encoding was cancelled.");

                    default:
                        throw new InvalidOperationException("Encode completion has an undefined status.");
                }
            }
            finally
            {
                if (!transferred && png.IsCreated)
                {
                    png.Dispose();
                }

                _service.Acknowledge(token);
            }
        }
    }
}
