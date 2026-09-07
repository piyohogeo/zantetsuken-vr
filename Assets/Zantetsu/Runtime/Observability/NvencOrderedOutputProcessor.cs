using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Threadless Phase 0.11 Ordered Output Processor. It consumes the fixed
    /// SPSC Submit-to-Output Queue strictly in FIFO order, holds at most one
    /// current record, and connects each record to its exact downstream
    /// boundary — Collector, Run Chunk Sink, the two release/recovery
    /// coordinators, and the Frame Completion boundary — until exactly one
    /// terminal Frame Completion is published. It owns no additional queue,
    /// reorder buffer, Access Unit queue, worker, or native resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current record is dequeued only when no current work is held, and
    /// later records are never touched until the current record has published
    /// its terminal Completion. A busy Collector, Sink, release, recovery, or
    /// publish gate holds the current record, its owned lease, its stage, and
    /// the terminal evidence, and the next call resumes without re-running the
    /// Collector, the output source, or the Sink writer. The held stage is a
    /// single fixed slot; there is no record reordering, peek, sort, or
    /// later-event search.
    /// </para>
    /// <para>
    /// A Submitted record during a running run flows Collector → Sink →
    /// Succeeded Completion; a Sink controlled failure records Run Abandoned
    /// and publishes a Failed Completion; a Collector controlled failure
    /// records Run Abandoned and publishes a Failed Completion without ever
    /// contacting the Sink or its writer. A FailedBeforeSubmit record is
    /// released, records Run Abandoned, and publishes its mapped Completion
    /// without touching the Collector, Sink, or output source. A Submitted
    /// record seen after Run Abandoned is recovered through the abandon
    /// recovery coordinator without any chunk append and published as
    /// Cancelled. An unknown ownership, an indeterminate shape, or an exception
    /// poisons the process and never guesses a Completion or a resource return.
    /// </para>
    /// <para>
    /// After a publish the processor clears only its held current; the
    /// Submit-to-Output credit is returned by the Frame Completion boundary,
    /// the Work Slot and Frame Completion credit stay active until the Main
    /// Thread collects, and the Sample and Owned Access Unit were already
    /// resolved by their dedicated boundaries. This type owns no thread and is
    /// not an <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencOrderedOutputProcessor
    {
        private enum NvencOutputProcessorStage
        {
            None,
            SubmittedCollect,
            SubmittedSink,
            SubmittedPublish,
            CollectorFailurePublish,
            FailedBeforeSubmitRelease,
            FailedBeforeSubmitPublish,
            SubmittedRecover,
            RecoverPublish,
        }

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencFixedSpscQueue<NvencSubmitToOutputRecord> _queue;
        private readonly NvencSubmittedOutputCollector _collector;
        private readonly NvencRunChunkSink _sink;
        private readonly NvencFailedBeforeSubmitReleaseCoordinator _releaseCoordinator;
        private readonly NvencSubmittedOutputAbandonRecoveryCoordinator _recoveryCoordinator;
        private readonly NvencFrameCompletionBoundary _completionBoundary;

        private NvencOutputProcessorStage _stage;
        private NvencSubmitToOutputRecord _current;
        private NvencOwnedAccessUnitLease _ownedLease;
        private NvencRunChunkSinkResult _sinkResult;
        private NvencFailedBeforeSubmitReleaseResult _releaseResult;
        private NvencRunAbandonedRecoveryResult _recoveryResult;
        private NvencSubmittedOutputCollectResult _collectorResult;

        internal NvencOrderedOutputProcessor(
            NvencCaptureProcessState processState,
            NvencFixedSpscQueue<NvencSubmitToOutputRecord> queue,
            NvencSubmittedOutputCollector collector,
            NvencRunChunkSink sink,
            NvencFailedBeforeSubmitReleaseCoordinator releaseCoordinator,
            NvencSubmittedOutputAbandonRecoveryCoordinator recoveryCoordinator,
            NvencFrameCompletionBoundary completionBoundary)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _releaseCoordinator = releaseCoordinator ?? throw new ArgumentNullException(nameof(releaseCoordinator));
            _recoveryCoordinator = recoveryCoordinator ?? throw new ArgumentNullException(nameof(recoveryCoordinator));
            _completionBoundary = completionBoundary ?? throw new ArgumentNullException(nameof(completionBoundary));
        }

        internal bool HasCurrentWork => _stage != NvencOutputProcessorStage.None;

        internal bool HasPendingWork => _stage != NvencOutputProcessorStage.None || _queue.Count > 0;

        internal bool IsRunAbandoned => _processState.IsRunAbandoned;

        /// <summary>
        /// The exact Run Chunk Sink this processor appends into, for
        /// exact-reference correlation with the Run chunk context.
        /// </summary>
        internal NvencRunChunkSink Sink => _sink;

        /// <summary>
        /// O(1) correlation predicate: true only when this processor is bound to
        /// the exact process state, consumes from the exact Submit-to-Output
        /// Queue the Submit Worker emits into, and appends into the exact Sink
        /// of the Run chunk context — without exposing the queue.
        /// </summary>
        internal bool IsCorrelatedWith(
            NvencCaptureProcessState processState,
            NvencOrderedSubmitWorkerService submitWorker,
            NvencRunChunkContext runChunkContext)
        {
            return ReferenceEquals(_processState, processState) &&
                submitWorker.IsCorrelatedWith(processState, _queue) &&
                ReferenceEquals(_sink, runChunkContext.Sink);
        }

        /// <summary>
        /// Advances exactly one current record toward its terminal Completion.
        /// Returns false while the queue is empty, a downstream gate is busy,
        /// or the process is poisoned, holding the current record, owned lease,
        /// stage, and terminal evidence unchanged.
        /// </summary>
        internal bool TryProcessNext()
        {
            if (_processState.IsPoisoned)
            {
                return false;
            }

            if (_stage == NvencOutputProcessorStage.None)
            {
                if (!_queue.TryDequeue(out _current))
                {
                    return false;
                }

                _stage = ClassifyInitialStage(_current);
            }

            switch (_stage)
            {
                case NvencOutputProcessorStage.SubmittedCollect:
                    return StepSubmittedCollect();

                case NvencOutputProcessorStage.SubmittedSink:
                    return StepSubmittedSink();

                case NvencOutputProcessorStage.SubmittedPublish:
                    return StepSubmittedPublish();

                case NvencOutputProcessorStage.CollectorFailurePublish:
                    return StepCollectorFailurePublish();

                case NvencOutputProcessorStage.FailedBeforeSubmitRelease:
                    return StepFailedBeforeSubmitRelease();

                case NvencOutputProcessorStage.FailedBeforeSubmitPublish:
                    return StepFailedBeforeSubmitPublish();

                case NvencOutputProcessorStage.SubmittedRecover:
                    return StepSubmittedRecover();

                case NvencOutputProcessorStage.RecoverPublish:
                    return StepRecoverPublish();

                default:
                    PoisonAndThrow("Ordered Output Processor has no stage for the current record.");
                    return false;
            }
        }

        private NvencOutputProcessorStage ClassifyInitialStage(in NvencSubmitToOutputRecord record)
        {
            switch (record.Kind)
            {
                case NvencSubmitToOutputRecordKind.Submitted:
                    return _processState.IsRunAbandoned
                        ? NvencOutputProcessorStage.SubmittedRecover
                        : NvencOutputProcessorStage.SubmittedCollect;

                case NvencSubmitToOutputRecordKind.FailedBeforeSubmit:
                    return NvencOutputProcessorStage.FailedBeforeSubmitRelease;

                default:
                    PoisonAndThrow("Ordered Output Processor received a record with no variant.");
                    return NvencOutputProcessorStage.None;
            }
        }

        private bool StepSubmittedCollect()
        {
            if (!_collector.TryCollect(_current, out NvencSubmittedOutputCollectResult collectorResult))
            {
                return false;
            }

            if (!collectorResult.WorkToken.IdenticalTo(_current.WorkToken))
            {
                PoisonAndThrow("Ordered Output Processor collector work token does not match the current record.");
            }

            if (collectorResult.IsSucceeded)
            {
                _ownedLease = collectorResult.OwnedLease;
                _stage = NvencOutputProcessorStage.SubmittedSink;
                return StepSubmittedSink();
            }

            if (collectorResult.IsControlledFailure)
            {
                // The Sample Slot and the buffer were already safely recovered
                // by the collector; the Sink and its writer are never contacted.
                // Keep the collector evidence so a busy publish reuses the same
                // proof instead of re-running the collector.
                ConfirmRunAbandoned();
                _collectorResult = collectorResult;
                _stage = NvencOutputProcessorStage.CollectorFailurePublish;
                return StepCollectorFailurePublish();
            }

            PoisonAndThrow("Ordered Output Processor collector result has no terminal shape.");
            return false;
        }

        private bool StepSubmittedSink()
        {
            if (!_sink.TryAppend(_current.WorkToken, _ownedLease, out NvencRunChunkSinkResult sinkResult))
            {
                return false;
            }

            if (!sinkResult.WorkToken.IdenticalTo(_current.WorkToken))
            {
                PoisonAndThrow("Ordered Output Processor sink work token does not match the current record.");
            }

            if (sinkResult.IsAppended)
            {
                _sinkResult = sinkResult;
                _stage = NvencOutputProcessorStage.SubmittedPublish;
                return StepSubmittedPublish();
            }

            if (sinkResult.IsControlledFailure)
            {
                ConfirmRunAbandoned();
                _sinkResult = sinkResult;
                _stage = NvencOutputProcessorStage.SubmittedPublish;
                return StepSubmittedPublish();
            }

            PoisonAndThrow("Ordered Output Processor sink result is indeterminate.");
            return false;
        }

        private bool StepSubmittedPublish()
        {
            if (!_completionBoundary.TryPublishSubmitted(_current, _sinkResult, out _))
            {
                return false;
            }

            ClearCurrent();
            return true;
        }

        private bool StepCollectorFailurePublish()
        {
            if (!_completionBoundary.TryPublishCollectorControlledFailure(_current, _collectorResult, out _))
            {
                return false;
            }

            ClearCurrent();
            return true;
        }

        private bool StepFailedBeforeSubmitRelease()
        {
            if (!_releaseCoordinator.TryRelease(_current, out NvencFailedBeforeSubmitReleaseResult releaseResult))
            {
                return false;
            }

            _releaseResult = releaseResult;
            ConfirmRunAbandoned();
            _stage = NvencOutputProcessorStage.FailedBeforeSubmitPublish;
            return StepFailedBeforeSubmitPublish();
        }

        private bool StepFailedBeforeSubmitPublish()
        {
            if (!_completionBoundary.TryPublishFailedBeforeSubmit(_current, _releaseResult, out _))
            {
                return false;
            }

            ClearCurrent();
            return true;
        }

        private bool StepSubmittedRecover()
        {
            if (!_recoveryCoordinator.TryRecover(_current, out NvencRunAbandonedRecoveryResult recoveryResult))
            {
                return false;
            }

            _recoveryResult = recoveryResult;
            _stage = NvencOutputProcessorStage.RecoverPublish;
            return StepRecoverPublish();
        }

        private bool StepRecoverPublish()
        {
            if (!_completionBoundary.TryPublishRunAbandoned(_current, _recoveryResult, out _))
            {
                return false;
            }

            ClearCurrent();
            return true;
        }

        private void ConfirmRunAbandoned()
        {
            _processState.TryBeginRunAbandoned();
        }

        private void ClearCurrent()
        {
            _stage = NvencOutputProcessorStage.None;
            _current = default;
            _ownedLease = default;
            _sinkResult = default;
            _releaseResult = default;
            _recoveryResult = default;
            _collectorResult = default;
        }

        private void PoisonAndThrow(string message)
        {
            _processState.TryPoison();
            throw new InvalidOperationException(message);
        }
    }
}
