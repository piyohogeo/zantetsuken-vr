using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the already-determined freeze terminal inputs into one retryable
    /// path: build the terminal buffer, begin the freeze terminal append, and
    /// append the buffer to the recorder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This coordinator treats the seal receipt, the forced-drop frame ID set,
    /// the freeze terminal checkpoint, the capture-admission-stopped evidence,
    /// the recorder, and the buffer builder as already-determined inputs. It
    /// does not stop producers, drain terminal intents, issue the ownership
    /// snapshot, run the force drop, seal the logger, or sample the checkpoint.
    /// </para>
    /// <para>
    /// It holds only the recorder and the builder and owns, modifies, or disposes
    /// none of its dependencies. It is main-thread only and not thread-safe, and
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameFreezeTerminalCoordinator
    {
        private readonly TraceFlightRecorder _recorder;
        private readonly FreezeTerminalTraceBufferBuilder _bufferBuilder;

        internal CaptureFrameFreezeTerminalCoordinator(
            TraceFlightRecorder recorder,
            FreezeTerminalTraceBufferBuilder bufferBuilder)
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            if (bufferBuilder == null)
            {
                throw new ArgumentNullException(nameof(bufferBuilder));
            }

            if (!recorder.Logger.IsCaptureRun)
            {
                throw new ArgumentException("The recorder's logger must be a capture-run logger.", nameof(recorder));
            }

            if (bufferBuilder.Registry.Run.TestRunId != recorder.Logger.TestRunId)
            {
                throw new ArgumentException("The builder registry run must match the recorder logger's bound run.", nameof(bufferBuilder));
            }

            _recorder = recorder;
            _bufferBuilder = bufferBuilder;
        }

        internal bool IsFrozenFor(long testRunId) => testRunId > 0
            && _recorder.State == TraceFlightRecorderState.Frozen
            && _recorder.Logger.TestRunId == testRunId
            && _bufferBuilder.Registry.Run.TestRunId == testRunId;

        /// <summary>
        /// Existing Run-freeze integration point. It stops evidence admission,
        /// drains every completion, joins the backend, verifies that no
        /// artifact reservation remains, completes the trace transition to
        /// Frozen, and only then issues publication evidence.
        /// </summary>
        internal bool TryCompleteEvidenceRun(
            CaptureEvidenceDraftCoordinator evidence,
            CaptureRunInitializationSession runSession,
            TraceRunSealReceipt sealReceipt,
            ForcedDropFrameIdSet forcedDropFrameIds,
            in FreezeTerminalCheckpoint checkpoint,
            out CaptureEvidenceRunFreezeReceipt receipt)
        {
            receipt = null;
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            if (runSession == null) throw new ArgumentNullException(nameof(runSession));
            if (!runSession.IsCreated) throw new ArgumentException("Run session must hold the OS Run lock.", nameof(runSession));
            if (!ReferenceEquals(evidence.Drafts, _bufferBuilder.Registry))
                throw new ArgumentException("Evidence and freeze terminal must share the draft registry.", nameof(evidence));
            if (runSession.TestRunId != evidence.Drafts.Run.TestRunId
                || runSession.TestRunId != _recorder.Logger.TestRunId)
                throw new ArgumentException("Run session must match the evidence and trace run.", nameof(runSession));

            evidence.BeginDrain();
            evidence.CancelQueued();
            while (evidence.TryApplyNextCompletion()) { }
            if (!evidence.TryJoin()) return false;
            while (evidence.TryApplyNextCompletion()) { }
            if (!evidence.IsFullyDrained)
                throw new InvalidOperationException("Joined evidence backend retained work or artifact reservations.");

            FreezeTerminalTraceBuffer buffer = Complete(
                sealReceipt,
                forcedDropFrameIds,
                checkpoint,
                true);
            if (!IsFrozenFor(runSession.TestRunId))
                throw new InvalidOperationException("Trace recorder did not reach Frozen.");
            receipt = new CaptureEvidenceRunFreezeReceipt(this, evidence, runSession, buffer);
            return true;
        }

        /// <summary>
        /// Completes the freeze terminal sequence and returns the exact buffer
        /// appended to the recorder. Main-thread only.
        /// </summary>
        internal FreezeTerminalTraceBuffer Complete(
            TraceRunSealReceipt sealReceipt,
            ForcedDropFrameIdSet forcedDropFrameIds,
            in FreezeTerminalCheckpoint checkpoint,
            bool captureAdmissionStopped)
        {
            if (sealReceipt == null)
            {
                throw new ArgumentNullException(nameof(sealReceipt));
            }

            if (forcedDropFrameIds == null)
            {
                throw new ArgumentNullException(nameof(forcedDropFrameIds));
            }

            if (!_recorder.Logger.IsOnConstructingThread)
            {
                throw new InvalidOperationException("The freeze terminal completion must run on the thread that constructed the capture logger.");
            }

            if (_recorder.State != TraceFlightRecorderState.CapturingPostRoll
                && _recorder.State != TraceFlightRecorderState.AwaitingFreezeTerminal)
            {
                throw new InvalidOperationException("The recorder must be CapturingPostRoll or AwaitingFreezeTerminal.");
            }

            if (!ReferenceEquals(sealReceipt.IssuedBy, _recorder.Logger))
            {
                throw new ArgumentException("The seal receipt was not issued by the recorder's logger.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(sealReceipt.IssuedTo, _recorder))
            {
                throw new ArgumentException("The seal receipt was not issued to this recorder.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(sealReceipt, _recorder.Logger.IssuedSealReceipt))
            {
                throw new ArgumentException("The seal receipt is not the exact receipt issued by the logger.", nameof(sealReceipt));
            }

            if (sealReceipt.TestRunId <= 0
                || sealReceipt.TestRunId != _recorder.Logger.TestRunId
                || sealReceipt.TestRunId != _bufferBuilder.Registry.Run.TestRunId
                || sealReceipt.TestRunId != forcedDropFrameIds.TestRunId)
            {
                throw new ArgumentException("The seal receipt's test run ID does not match the logger, registry, and set.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(forcedDropFrameIds, _bufferBuilder.Registry.IssuedForcedDropFrameIdSet))
            {
                throw new ArgumentException("The forced-drop set is not the builder registry's canonical set.", nameof(forcedDropFrameIds));
            }

            if (!checkpoint.IsValid)
            {
                throw new ArgumentException("The checkpoint is invalid.", nameof(checkpoint));
            }

            if (checkpoint.TestRunId != sealReceipt.TestRunId
                || checkpoint.TestRunId != forcedDropFrameIds.TestRunId
                || checkpoint.TestRunId != _recorder.Logger.TestRunId)
            {
                throw new ArgumentException("The checkpoint's test run ID does not match the receipt, set, and logger.", nameof(checkpoint));
            }

            if (_recorder.State == TraceFlightRecorderState.CapturingPostRoll)
            {
                if (!captureAdmissionStopped)
                {
                    throw new ArgumentException("Capture admission must be stopped before beginning the freeze terminal append.", nameof(captureAdmissionStopped));
                }

                // Build first so an allocation or registry-validation failure
                // leaves the recorder CapturingPostRoll.
                FreezeTerminalTraceBuffer buffer = _bufferBuilder.Build(forcedDropFrameIds, checkpoint);
                _recorder.BeginFreezeTerminalAppend(sealReceipt, captureAdmissionStopped);
                _recorder.AppendFreezeTerminalEvents(buffer);
                return buffer;
            }

            // AwaitingFreezeTerminal: a retry after a failed append. Never
            // re-begin, re-seal, or re-issue the set.
            FreezeTerminalTraceBuffer retryBuffer = _bufferBuilder.Build(forcedDropFrameIds, checkpoint);
            _recorder.AppendFreezeTerminalEvents(retryBuffer);
            return retryBuffer;
        }
    }
}
