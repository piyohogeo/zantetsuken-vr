using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Step-driven, threadless Phase 0.11 submit processor. It consumes the
    /// Submission Queue strictly in FIFO order, holds at most one current work,
    /// requests the evidenced source release handoff once, attempts the
    /// encode-picture submit exactly once, and emits exactly one exclusive
    /// <c>Submitted</c> or <c>FailedBeforeSubmit</c> record to the
    /// Submit-to-Output Queue per accepted work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryProcessNext"/> follows a fixed order: dequeue the
    /// Submission Queue head only when no current work is held, confirm the
    /// output capacity before any side effect, fully verify the current record,
    /// build the submit operation, request the source release handoff, call the
    /// submitter exactly once, build the exclusive output record, enqueue it
    /// exactly once, wake the one bound Output Worker, and only then clear the
    /// current work. A not-yet-completed work leaves the current held and never
    /// overtakes a later record. A full output queue or a busy gate leaves the
    /// current held with no side effect.
    /// </para>
    /// <para>
    /// A submitter exception poisons the process and is re-thrown unchanged; no
    /// output record is fabricated and no resource is speculatively released. A
    /// controlled <c>false</c> maps to
    /// <see cref="NvencFailedBeforeSubmitReason.NvencSubmitFailed"/>. The
    /// Main Thread release apply is a separate boundary and is never called
    /// from this processor. After poison this processor performs no further
    /// work. No thread, task, sleep, busy loop, sorting, peek, or queue growth
    /// is used.
    /// </para>
    /// </remarks>
    internal sealed class NvencOrderedSubmitProcessor
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly NvencFixedSpscQueue<NvencSubmissionRecord> _submissionQueue;
        private readonly NvencFixedSpscQueue<NvencSubmitToOutputRecord> _submitToOutputQueue;
        private readonly NvencCaptureWorkSlotPool _workSlots;
        private readonly NvencEncodeSampleSlotPool _sampleSlots;
        private readonly NvencSourceResourceReleaseCoordinator _releaseCoordinator;
        private readonly INvencEncodePictureSubmitter _submitter;

        private NvencSubmissionRecord _current;
        private volatile bool _hasCurrent;

        // The one Output Worker this processor wakes after an enqueue. Bound
        // once at composition, because that worker cannot exist until the
        // Submit Worker that owns this processor does. Volatile because the
        // binding is written by the composing thread and read by the worker.
        private volatile NvencOrderedOutputWorkerService _outputWorker;

        internal NvencOrderedSubmitProcessor(
            NvencCaptureProcessState processState,
            NvencFixedSpscQueue<NvencSubmissionRecord> submissionQueue,
            NvencFixedSpscQueue<NvencSubmitToOutputRecord> submitToOutputQueue,
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencSourceResourceReleaseCoordinator releaseCoordinator,
            INvencEncodePictureSubmitter submitter)
        {
            if (processState == null)
            {
                throw new ArgumentNullException(nameof(processState));
            }

            if (submissionQueue == null)
            {
                throw new ArgumentNullException(nameof(submissionQueue));
            }

            if (submitToOutputQueue == null)
            {
                throw new ArgumentNullException(nameof(submitToOutputQueue));
            }

            if (workSlots == null)
            {
                throw new ArgumentNullException(nameof(workSlots));
            }

            if (sampleSlots == null)
            {
                throw new ArgumentNullException(nameof(sampleSlots));
            }

            if (releaseCoordinator == null)
            {
                throw new ArgumentNullException(nameof(releaseCoordinator));
            }

            if (submitter == null)
            {
                throw new ArgumentNullException(nameof(submitter));
            }

            _processState = processState;
            _submissionQueue = submissionQueue;
            _submitToOutputQueue = submitToOutputQueue;
            _workSlots = workSlots;
            _sampleSlots = sampleSlots;
            _releaseCoordinator = releaseCoordinator;
            _submitter = submitter;
        }

        /// <summary>
        /// Binds the one Output Worker this processor wakes when it has put a
        /// record in the Submit-to-Output Queue. Exactly once, at composition.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two workers are composed in one order only: this processor, the
        /// Submit Worker that owns it, the Output Processor, and then the
        /// Output Worker, which needs the Submit Worker to exist. The wake
        /// therefore cannot be a constructor argument, and is bound afterwards
        /// - once, before the Submit Worker is started. There is no way to
        /// replace it, and no registry or bus behind it: this is one worker
        /// waking one other worker.
        /// </para>
        /// <para>
        /// Without a binding the processor still works exactly as before and
        /// simply wakes nobody, which is what the step-driven fixtures that
        /// have no Output Worker rely on.
        /// </para>
        /// </remarks>
        internal void BindOutputWorkerNotification(NvencOrderedOutputWorkerService outputWorker)
        {
            if (outputWorker == null)
            {
                throw new ArgumentNullException(nameof(outputWorker));
            }

            if (_outputWorker != null)
            {
                throw new InvalidOperationException(
                    "This submit processor already notifies an Output Worker; the binding is made once.");
            }

            _outputWorker = outputWorker;
        }

        internal bool HasCurrentWork => _hasCurrent;

        /// <summary>
        /// O(1) correlation predicate: true only when this processor is bound to
        /// the exact process state and emits into the exact Submit-to-Output
        /// Queue, without exposing the queue.
        /// </summary>
        internal bool IsCorrelatedWith(
            NvencCaptureProcessState processState,
            NvencFixedSpscQueue<NvencSubmitToOutputRecord> queue)
        {
            return ReferenceEquals(_processState, processState) &&
                ReferenceEquals(_submitToOutputQueue, queue);
        }

        /// <summary>
        /// O(1) resource correlation predicate: true only when this processor
        /// and its internal source release coordinator are bound to the exact
        /// Work, Sample, GPU Conversion Sync, Submit-to-Output credit, and
        /// Frame Completion credit pools, without exposing them.
        /// </summary>
        internal bool IsCorrelatedWithResources(
            NvencCaptureWorkSlotPool workSlots,
            NvencEncodeSampleSlotPool sampleSlots,
            NvencGpuConversionSyncPool syncSlots,
            NvencSubmitToOutputCreditPool submitToOutputCredits,
            NvencFrameCompletionCreditPool frameCompletionCredits)
        {
            return ReferenceEquals(_workSlots, workSlots)
                && ReferenceEquals(_sampleSlots, sampleSlots)
                && _releaseCoordinator.IsCorrelatedWithResources(
                    workSlots, sampleSlots, syncSlots, submitToOutputCredits, frameCompletionCredits);
        }

        /// <summary>
        /// Diagnostic-only stop predicate for the Submit Worker's drain
        /// decision: true while the processor holds a current work or the
        /// Submission Queue is non-empty. It exposes no queue contents and no
        /// current record. Not a linearization point; used only to decide when
        /// a requested drain has nothing left to process.
        /// </summary>
        internal bool HasPendingWork => _hasCurrent || _submissionQueue.Count > 0;

        internal bool TryProcessNext()
        {
            // After poison this processor never starts or continues new work.
            if (_processState.IsPoisoned)
            {
                return false;
            }

            // 1. Hold at most one current work; dequeue the queue head only once.
            if (!_hasCurrent)
            {
                if (!_submissionQueue.TryDequeue(out _current))
                {
                    return false;
                }

                _hasCurrent = true;
            }

            // 2. Confirm output capacity before any side effect.
            if (!_submitToOutputQueue.CanEnqueue)
            {
                return false;
            }

            // 3-4. Fully verify the current record against the exact Work and
            // Sample pools and build the submit operation. An accepted record
            // must remain valid here, so a failure is an invariant violation.
            NvencEncodePictureSubmitOperation operation;
            try
            {
                operation = NvencEncodePictureSubmitOperation.Create(_current, _workSlots, _sampleSlots);
            }
            catch (Exception)
            {
                _processState.TryPoison();
                return false;
            }

            // Acquire the short submit-step gate so the source handoff, the
            // submit call, the output record build, and the output enqueue form
            // one critical section serialized with the poison transition. A busy
            // gate leaves the current held with no side effect.
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                // 5. Request the evidenced source release handoff. On false the
                // current is held and later records are never overtaken.
                //
                // A throw is not a submit failure that can be reported: the
                // completion authority could not say whether the source is
                // still being read, so what this process owns is no longer
                // known. Poison first, fabricate no output record, call no
                // submitter, keep the current and everything it holds, and let
                // the very same exception out.
                bool released;
                try
                {
                    released = _releaseCoordinator.TryReleaseSourceResources(_current);
                }
                catch (Exception)
                {
                    _processState.TryPoison();
                    throw;
                }

                if (!released)
                {
                    return false;
                }

                // Run Abandoned: never submit work that has not been submitted
                // yet; emit it as FailedBeforeSubmit(CancelledAfterRunAbandoned)
                // with the same source handoff and enqueue order preserved.
                if (_processState.IsRunAbandoned)
                {
                    NvencSubmitToOutputRecord cancelled = NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                        operation.WorkToken, operation.WorkSlot, operation.SampleSlot,
                        _current.SubmitToOutputCredit, _current.FrameCompletionCredit,
                        NvencFailedBeforeSubmitReason.CancelledAfterRunAbandoned);

                    if (!_submitToOutputQueue.TryEnqueue(cancelled))
                    {
                        _processState.TryPoison();
                        return false;
                    }

                    // The record is in the queue and observable there before
                    // anyone is told about it.
                    NotifyOutputWorker();

                    _hasCurrent = false;
                    _current = default;
                    return true;
                }

                // 6. Attempt the submit exactly once.
                bool submitted;
                try
                {
                    submitted = _submitter.TrySubmit(operation);
                }
                catch (Exception ex)
                {
                    // Poison before propagating the same exception; no output
                    // record is fabricated and nothing is speculatively released.
                    _processState.TryPoison();
                    throw;
                }

                // 7-8. Build the exclusive output record, forwarding the two
                // capacity credits reserved by the admission boundary. The
                // operation itself carries only the work and slot evidence.
                NvencSubmitToOutputRecord output = submitted
                    ? NvencSubmitToOutputRecord.CreateSubmitted(
                        operation.WorkToken, operation.WorkSlot, operation.SampleSlot,
                        _current.SubmitToOutputCredit, _current.FrameCompletionCredit)
                    : NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                        operation.WorkToken, operation.WorkSlot, operation.SampleSlot,
                        _current.SubmitToOutputCredit, _current.FrameCompletionCredit,
                        NvencFailedBeforeSubmitReason.NvencSubmitFailed);

                // 9. Enqueue exactly once; the capacity was verified above.
                if (!_submitToOutputQueue.TryEnqueue(output))
                {
                    _processState.TryPoison();
                    return false;
                }

                // 10. Wake the Output Worker, now that the record is in the
                // queue and not before.
                NotifyOutputWorker();

                // 11. Clear the current only after the successful enqueue.
                _hasCurrent = false;
                _current = default;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Wakes the bound Output Worker once, after a record has actually
        /// reached the Submit-to-Output Queue.
        /// </summary>
        /// <remarks>
        /// A coalescing state-change hint and nothing more: it carries no
        /// count, acknowledgement, receipt, generation, or retry state, and
        /// several of them may collapse into one wake. Nothing here waits,
        /// sleeps, polls, or times anything.
        ///
        /// A failure to deliver it leaves a record already in the queue, so
        /// there is nothing to take back: the process is poisoned and the
        /// failure propagates, becoming the worker's fatal failure, rather than
        /// the queue being unwound on a guess.
        /// </remarks>
        private void NotifyOutputWorker()
        {
            NvencOrderedOutputWorkerService outputWorker = _outputWorker;
            if (outputWorker == null)
            {
                return;
            }

            try
            {
                outputWorker.Notify();
            }
            catch (Exception)
            {
                _processState.TryPoison();
                throw;
            }
        }
    }
}
