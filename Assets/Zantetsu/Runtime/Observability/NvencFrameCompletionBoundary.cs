using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Phase 0.11 NVENC Frame Completion boundary. The single Output Worker
    /// publishes exactly one NVENC-specific Frame Completion for each Accepted
    /// Work after its terminal outcome, and the Main Thread collects those
    /// completions, returning the Work Slot and the Frame Completion credit
    /// exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Submit-to-Output credit is returned at publish time; the Work Slot
    /// and the Frame Completion credit stay active until the Main Thread
    /// collects. The fixed SPSC completion queue is reused unchanged, so
    /// enqueue and dequeue are non-waiting, single-attempt, and allocation
    /// free. Because each Accepted Work already reserved a Frame Completion
    /// credit, a full queue during a normal publish is an invariant violation
    /// and poisons without dropping or overwriting any record.
    /// </para>
    /// <para>
    /// The publish order is fixed inside one resource-resolution gate: verify
    /// the exact correlation and active state, return the Submit-to-Output
    /// credit, enqueue the completion exactly once, and commit the published
    /// state. A failed credit return, a failed enqueue, an unknown
    /// correlation, or a sink result whose work token does not match the
    /// record all poison without guessing a completion; the Submit-to-Output
    /// credit becoming inactive is exactly what makes a second publish of the
    /// same record poison, so double enqueue is impossible without a new
    /// nonce, registry, or history set.
    /// </para>
    /// <para>
    /// The collect side dequeues and then, inside the same resource-resolution
    /// gate, re-verifies the record against the exact Work Slot and Frame
    /// Completion credit pools, returns both in a fixed order, and only then
    /// hands the terminal completion to the caller. Both are pre-verified
    /// active so a one-sided partial return cannot occur on the normal path;
    /// a return failure or a corrupted record poisons without returning a
    /// success completion. After poison no publish, collect, or resource
    /// return progresses.
    /// </para>
    /// </remarks>
    internal sealed class NvencFrameCompletionBoundary
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencSubmitToOutputCreditPool _submitToOutputCredits;
        private readonly NvencFrameCompletionCreditPool _frameCompletionCredits;
        private readonly NvencFixedSpscQueue<NvencFrameCompletionRecord> _queue;

        internal NvencFrameCompletionBoundary(
            NvencCaptureProcessState processState,
            NvencCaptureWorkSlotPool workSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _workSlots = workSlots ?? throw new ArgumentNullException(nameof(workSlots));
            _submitToOutputCredits = submitToOutputCredits ?? throw new ArgumentNullException(nameof(submitToOutputCredits));
            _frameCompletionCredits = frameCompletionCredits ?? throw new ArgumentNullException(nameof(frameCompletionCredits));
            _queue = new NvencFixedSpscQueue<NvencFrameCompletionRecord>();
        }

        internal bool TryPublishSubmitted(
            in NvencSubmitToOutputRecord record,
            in NvencRunChunkSinkResult sinkResult,
            out NvencFrameCompletionRecord completion)
        {
            completion = default;

            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (sinkResult.IsAppended)
            {
                if (!sinkResult.WorkToken.IdenticalTo(record.WorkToken))
                {
                    PoisonAndThrow("Frame Completion sink result work token does not match the record.");
                }

                return PublishCore(
                    record,
                    NvencSubmitToOutputRecordKind.Submitted,
                    CaptureFrameCompletionStatus.Succeeded,
                    NvencFrameCompletionReason.None,
                    out completion);
            }

            if (sinkResult.IsControlledFailure)
            {
                if (!sinkResult.WorkToken.IdenticalTo(record.WorkToken))
                {
                    PoisonAndThrow("Frame Completion sink result work token does not match the record.");
                }

                return PublishCore(
                    record,
                    NvencSubmitToOutputRecordKind.Submitted,
                    CaptureFrameCompletionStatus.Failed,
                    NvencFrameCompletionReason.RunChunkControlledFailure,
                    out completion);
            }

            PoisonAndThrow("Frame Completion sink result has no terminal evidence.");
            return false;
        }

        internal bool TryPublishRunAbandoned(
            in NvencSubmitToOutputRecord record,
            out NvencFrameCompletionRecord completion)
        {
            return PublishCore(
                record,
                NvencSubmitToOutputRecordKind.Submitted,
                CaptureFrameCompletionStatus.Cancelled,
                NvencFrameCompletionReason.CancelledAfterRunAbandoned,
                out completion);
        }

        internal bool TryPublishFailedBeforeSubmit(
            in NvencSubmitToOutputRecord record,
            out NvencFrameCompletionRecord completion)
        {
            completion = default;

            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (!MapFailedBeforeSubmit(
                record.Reason,
                out CaptureFrameCompletionStatus status,
                out NvencFrameCompletionReason reason))
            {
                PoisonAndThrow("Frame Completion failed-before-submit reason is undefined.");
            }

            return PublishCore(
                record,
                NvencSubmitToOutputRecordKind.FailedBeforeSubmit,
                status,
                reason,
                out completion);
        }

        internal bool TryCollect(out NvencFrameCompletionRecord completion)
        {
            completion = default;

            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                if (!_queue.TryDequeue(out NvencFrameCompletionRecord record))
                {
                    return false;
                }

                if (!record.IsValid ||
                    !_workSlots.IsActive(record.WorkSlot) ||
                    !_frameCompletionCredits.IsActive(record.FrameCompletionCredit))
                {
                    PoisonAndThrow("Frame Completion record correlation is broken.");
                }

                // Both were pre-verified active; return in a fixed order so the
                // normal path cannot release only one of the two.
                if (!_workSlots.TryReturn(record.WorkSlot))
                {
                    PoisonAndThrow("Work Slot return failed during Frame Completion collect.");
                }

                if (!_frameCompletionCredits.TryReturn(record.FrameCompletionCredit))
                {
                    PoisonAndThrow("Frame Completion credit return failed during collect.");
                }

                completion = record;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private bool PublishCore(
            in NvencSubmitToOutputRecord record,
            NvencSubmitToOutputRecordKind requiredKind,
            CaptureFrameCompletionStatus status,
            NvencFrameCompletionReason reason,
            out NvencFrameCompletionRecord completion)
        {
            completion = default;

            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (!_processState.TryBeginResourceResolution())
            {
                return false;
            }

            try
            {
                // 1. Exact correlation and active state, before any side effect.
                if (record.Kind != requiredKind || !record.IsValid ||
                    !_workSlots.IsActive(record.WorkSlot) ||
                    !_submitToOutputCredits.IsActive(record.SubmitToOutputCredit) ||
                    !_frameCompletionCredits.IsActive(record.FrameCompletionCredit))
                {
                    PoisonAndThrow("Frame Completion publish correlation is broken.");
                }

                // 2. Return the Submit-to-Output credit exactly once.
                if (!_submitToOutputCredits.TryReturn(record.SubmitToOutputCredit))
                {
                    PoisonAndThrow("Submit-to-Output credit return failed during Frame Completion publish.");
                }

                // 3. Enqueue the completion exactly once.
                NvencFrameCompletionRecord published;
                switch (status)
                {
                    case CaptureFrameCompletionStatus.Succeeded:
                        published = NvencFrameCompletionRecord.CreateSucceeded(
                            record.WorkToken, record.WorkSlot, record.FrameCompletionCredit);
                        break;

                    case CaptureFrameCompletionStatus.Failed:
                        published = NvencFrameCompletionRecord.CreateFailed(
                            record.WorkToken, record.WorkSlot, record.FrameCompletionCredit, reason);
                        break;

                    case CaptureFrameCompletionStatus.Cancelled:
                        published = NvencFrameCompletionRecord.CreateCancelled(
                            record.WorkToken, record.WorkSlot, record.FrameCompletionCredit, reason);
                        break;

                    default:
                        PoisonAndThrow("Frame Completion status is not terminal.");
                        return false;
                }

                if (!_queue.TryEnqueue(published))
                {
                    PoisonAndThrow("Frame Completion queue enqueue failed (capacity invariant violated).");
                }

                // 4. The published state is committed: the Submit-to-Output
                // credit is now returned, so a second publish of this record
                // fails its active-state check and poisons instead of
                // double-enqueueing.
                completion = published;
                return true;
            }
            finally
            {
                _processState.EndResourceResolution();
            }
        }

        private static bool MapFailedBeforeSubmit(
            NvencFailedBeforeSubmitReason reason,
            out CaptureFrameCompletionStatus status,
            out NvencFrameCompletionReason completionReason)
        {
            switch (reason)
            {
                case NvencFailedBeforeSubmitReason.GpuConversionFailed:
                    status = CaptureFrameCompletionStatus.Failed;
                    completionReason = NvencFrameCompletionReason.GpuConversionFailed;
                    return true;

                case NvencFailedBeforeSubmitReason.NvencSubmitFailed:
                    status = CaptureFrameCompletionStatus.Failed;
                    completionReason = NvencFrameCompletionReason.NvencSubmitFailed;
                    return true;

                case NvencFailedBeforeSubmitReason.CancelledBeforeSubmit:
                    status = CaptureFrameCompletionStatus.Cancelled;
                    completionReason = NvencFrameCompletionReason.CancelledBeforeSubmit;
                    return true;

                case NvencFailedBeforeSubmitReason.CancelledAfterRunAbandoned:
                    status = CaptureFrameCompletionStatus.Cancelled;
                    completionReason = NvencFrameCompletionReason.CancelledAfterRunAbandoned;
                    return true;

                case NvencFailedBeforeSubmitReason.DrainedBeforeSubmit:
                    status = CaptureFrameCompletionStatus.Cancelled;
                    completionReason = NvencFrameCompletionReason.DrainedBeforeSubmit;
                    return true;

                default:
                    status = CaptureFrameCompletionStatus.None;
                    completionReason = NvencFrameCompletionReason.None;
                    return false;
            }
        }

        private void PoisonAndThrow(string message)
        {
            _processState.TryPoison();
            throw new InvalidOperationException(message);
        }
    }
}
