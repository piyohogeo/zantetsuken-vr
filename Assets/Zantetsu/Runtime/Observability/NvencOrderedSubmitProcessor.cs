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
    /// exactly once, and only then clear the current work. A not-yet-completed
    /// work leaves the current held and never overtakes a later record. A full
    /// output queue or a busy gate leaves the current held with no side effect.
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
        private bool _hasCurrent;

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

        internal bool HasCurrentWork => _hasCurrent;

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
                if (!_releaseCoordinator.TryReleaseSourceResources(_current))
                {
                    return false;
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

                // 7-8. Build the exclusive output record.
                NvencSubmitToOutputRecord output = submitted
                    ? NvencSubmitToOutputRecord.CreateSubmitted(operation.WorkToken, operation.WorkSlot, operation.SampleSlot)
                    : NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                        operation.WorkToken, operation.WorkSlot, operation.SampleSlot,
                        NvencFailedBeforeSubmitReason.NvencSubmitFailed);

                // 9. Enqueue exactly once; the capacity was verified above.
                if (!_submitToOutputQueue.TryEnqueue(output))
                {
                    _processState.TryPoison();
                    return false;
                }

                // 10. Clear the current only after the successful enqueue.
                _hasCurrent = false;
                _current = default;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }
    }
}
