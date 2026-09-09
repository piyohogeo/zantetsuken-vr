using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Monotonic state of the Phase 0.11 Publication Service: a fixed
    /// single-request-slot, four-phase boundary that commits the publication
    /// plan, publishes the Fresh NVENC chunk, commits the capture index, and
    /// runs CaptureComplete on the same dedicated Worker thread. The Plan phase
    /// advances
    /// <see cref="AcceptingPlanCommit"/> to <see cref="PlanCommitQueued"/> on a
    /// successful plan submission, <see cref="PlanCommitQueued"/> to
    /// <see cref="PlanCommitExecuting"/> when the Worker takes the exact
    /// operation, and <see cref="PlanCommitExecuting"/> to
    /// <see cref="PlanCommitCompleted"/> on a verified normal return. A
    /// Committed plan result is collected into
    /// <see cref="AcceptingArtifactPublication"/> while the Worker re-parks;
    /// a non-Committed plan result is collected into
    /// <see cref="PlanCommitCollected"/> after the Worker physically stops. The
    /// Artifact phase advances <see cref="AcceptingArtifactPublication"/> to
    /// <see cref="ArtifactPublicationQueued"/> on submission,
    /// <see cref="ArtifactPublicationQueued"/> to
    /// <see cref="ArtifactPublicationExecuting"/> on the Worker claim,
    /// <see cref="ArtifactPublicationExecuting"/> to
    /// <see cref="ArtifactPublicationCompleted"/> on a verified normal return.
    /// A Published artifact result is collected into
    /// <see cref="AcceptingCaptureIndexCommit"/> while the Worker re-parks; a
    /// Failed artifact result is collected into
    /// <see cref="ArtifactPublicationCollected"/> after the Worker physically
    /// stops. The Capture Index phase advances
    /// <see cref="AcceptingCaptureIndexCommit"/> to
    /// <see cref="CaptureIndexCommitQueued"/> on submission,
    /// <see cref="CaptureIndexCommitQueued"/> to
    /// <see cref="CaptureIndexCommitExecuting"/> on the Worker claim, and
    /// <see cref="CaptureIndexCommitExecuting"/> to
    /// <see cref="CaptureIndexCommitCompleted"/> on a verified normal return. A
    /// Committed capture index result is collected into
    /// <see cref="AcceptingCaptureComplete"/> while the Worker re-parks; a
    /// Failed one is collected into <see cref="CaptureIndexCommitCollected"/>
    /// after the Worker physically stops.
    /// <see cref="StoppedWithoutRequest"/> is the normal terminal for a
    /// never-submitted Service, and <see cref="Poisoned"/> is the fail-closed
    /// terminal for any fatal failure or preceding external Poison.
    /// </summary>
    /// <remarks>
    /// <see cref="AcceptingCaptureComplete"/> is the parked state that keeps
    /// the same Service and the same Worker alive after a Committed capture
    /// index commit, and it is where the CaptureComplete phase is accepted. It
    /// advances to <see cref="CaptureCompleteQueued"/> on submission,
    /// <see cref="CaptureCompleteQueued"/> to
    /// <see cref="CaptureCompleteExecuting"/> on the Worker claim, and
    /// <see cref="CaptureCompleteExecuting"/> to
    /// <see cref="CaptureCompleteCompleted"/> on a verified normal return.
    /// CaptureComplete is the final phase, so the Worker physically stops after
    /// it whether the result is Completed or Failed, and the result is
    /// collected into <see cref="CaptureCompleteCollected"/> only after that
    /// stop.
    /// </remarks>
    internal enum NvencRunPublicationServiceState
    {
        AcceptingPlanCommit = 0,
        PlanCommitQueued = 1,
        PlanCommitExecuting = 2,
        PlanCommitCompleted = 3,
        PlanCommitCollected = 4,
        Poisoned = 5,
        StoppedWithoutRequest = 6,
        AcceptingArtifactPublication = 7,
        ArtifactPublicationQueued = 8,
        ArtifactPublicationExecuting = 9,
        ArtifactPublicationCompleted = 10,
        ArtifactPublicationCollected = 11,
        AcceptingCaptureIndexCommit = 12,
        CaptureIndexCommitQueued = 13,
        CaptureIndexCommitExecuting = 14,
        CaptureIndexCommitCompleted = 15,
        CaptureIndexCommitCollected = 16,
        AcceptingCaptureComplete = 17,
        CaptureCompleteQueued = 18,
        CaptureCompleteExecuting = 19,
        CaptureCompleteCompleted = 20,
        CaptureCompleteCollected = 21,
    }

    /// <summary>
    /// Phase 0.11 Publication Service: a fixed single-request-slot, four-phase
    /// boundary that separates the publication plan commit, the Fresh NVENC
    /// chunk publication, the capture index commit, and CaptureComplete from
    /// the Main/Render threads. It owns exactly one dedicated Worker thread with a fixed name,
    /// exactly one wake primitive, and one request slot per phase; it holds no
    /// queue, list, dictionary, task, thread pool, timer, periodic poll, busy
    /// spin, or second worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Worker runs the injected Plan Commit Execution Coordinator exactly
    /// once per accepted plan operation. A Committed plan result is published
    /// as <see cref="PlanCommitCompleted"/> and the Worker re-parks on the same
    /// thread instead of stopping, so the same Service instance and the same
    /// Worker thread later run the injected Artifact Publication Execution
    /// Coordinator exactly once. A FailedBeforeRename or CommitOutcomeUnknown
    /// plan result is published and the Worker physically stops; the Artifact
    /// phase is never entered.
    /// </para>
    /// <para>
    /// A Published artifact result keeps that same Worker parked so it can then
    /// run the injected Capture Index Commit Execution Coordinator exactly
    /// once; a Failed artifact result physically stops it and the Capture Index
    /// phase is never entered. A Committed capture index result likewise keeps
    /// the Worker parked, in
    /// <see cref="NvencRunPublicationServiceState.AcceptingCaptureComplete"/>,
    /// for the later CaptureComplete phase; a Failed one stops it. Every parked
    /// state re-parks on a stray notification instead of terminating, so a
    /// notification delivered while one phase executes can never stop the
    /// Worker that the next phase needs.
    /// </para>
    /// <para>
    /// A coordinator/publisher exception, a null/foreign/corrupt result, or a
    /// Poison that linearized during execution is fatal: the exact first
    /// exception is retained, the process is poisoned, no normal terminal is
    /// published, and the Worker stops without retrying, cleaning up, guessing
    /// file state, or re-running the commit or publish. Submission never waits,
    /// linearizes the Poison check, the accepting check, the operation
    /// validity, and the exact process-state correlation on the shared
    /// process-state gate, and notifies the Worker only after the slot is
    /// claimed; a foreign process, an invalid operation, or a second
    /// submission is rejected with no side effect and never contacts a
    /// coordinator. This type owns only its own thread and signal and never
    /// touches a Session Lease, the Run's local registry slot, or any evidence
    /// disposition.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationService : IDisposable
    {
        internal const string WorkerThreadName = "Zantetsu.NvencPublicationWorker";

        private readonly NvencCaptureProcessState _processState;
        private readonly NvencRunPublicationPlanCommitExecutionCoordinator _planCommitCoordinator;
        private readonly NvencRunArtifactPublicationExecutionCoordinator _artifactPublicationCoordinator;
        private readonly NvencRunCaptureIndexCommitExecutionCoordinator _captureIndexCommitCoordinator;
        private readonly NvencRunCaptureCompleteExecutionCoordinator _captureCompleteCoordinator;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private const int StateRunning = 0;
        private const int StateDisposed = 1;

        private Thread _workerThread;
        private int _lifecycleState = StateRunning;
        private int _state = (int)NvencRunPublicationServiceState.AcceptingPlanCommit;

        private NvencRunPublicationPlanCommitOperation _planCommitOperation;
        private NvencRunPublicationPlanCommitExecutionResult _planCommitResult;

        private NvencRunArtifactPublicationOperation _artifactPublicationOperation;
        private NvencRunArtifactPublicationAttemptResult _artifactPublicationResult;

        private NvencRunCaptureIndexCommitOperation _captureIndexCommitOperation;
        private NvencRunCaptureIndexCommitAttemptResult _captureIndexCommitResult;

        private NvencRunCaptureCompleteOperation _captureCompleteOperation;
        private NvencRunCaptureCompleteAttemptResult _captureCompleteResult;

        private volatile Exception _fatalFailure;
        private Action _settled;

        internal NvencRunPublicationService(
            NvencCaptureProcessState processState,
            NvencRunPublicationPlanCommitExecutionCoordinator planCommitCoordinator,
            NvencRunArtifactPublicationExecutionCoordinator artifactPublicationCoordinator,
            NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCommitCoordinator,
            NvencRunCaptureCompleteExecutionCoordinator captureCompleteCoordinator)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _planCommitCoordinator = planCommitCoordinator ?? throw new ArgumentNullException(nameof(planCommitCoordinator));
            _artifactPublicationCoordinator = artifactPublicationCoordinator ?? throw new ArgumentNullException(nameof(artifactPublicationCoordinator));
            _captureIndexCommitCoordinator = captureIndexCommitCoordinator ?? throw new ArgumentNullException(nameof(captureIndexCommitCoordinator));
            _captureCompleteCoordinator = captureCompleteCoordinator ?? throw new ArgumentNullException(nameof(captureCompleteCoordinator));

            Thread thread = new Thread(Run)
            {
                IsBackground = true,
                Name = WorkerThreadName,
            };
            thread.Start();
            Volatile.Write(ref _workerThread, thread);
        }

        internal NvencRunPublicationServiceState State =>
            (NvencRunPublicationServiceState)Volatile.Read(ref _state);

        /// <summary>
        /// Minimal O(1) exact-process-state correlation for the Run Coordinator:
        /// true only when this Service is bound to the exact supplied process
        /// state. ReferenceEquals only; it never exposes either Execution
        /// Coordinator or the publisher.
        /// </summary>
        internal bool IsBoundToProcessState(NvencCaptureProcessState processState)
        {
            return processState != null
                && ReferenceEquals(_processState, processState);
        }

        /// <summary>
        /// Non-waiting, exclusive submission of exactly one valid publication
        /// plan commit operation. A null operation throws
        /// <see cref="ArgumentNullException"/>. A foreign process state, an
        /// invalid operation, or any second submission is rejected with no side
        /// effect and without contacting the coordinator. Acceptance is
        /// linearized with the Poison transition on the shared process-state
        /// gate, and the Worker is notified only after the slot is claimed.
        /// </summary>
        internal bool TrySubmitPlanCommit(NvencRunPublicationPlanCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.AcceptingPlanCommit
                    || !operation.IsValid
                    || !operation.IsBoundToProcessState(_processState))
                {
                    return false;
                }

                _planCommitOperation = operation;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.PlanCommitQueued);
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            Notify();
            return true;
        }

        /// <summary>
        /// Non-waiting, at-most-once collection of the exact plan commit
        /// Execution Result. A Committed result is collected while the Worker
        /// stays parked and the Service advances to
        /// <see cref="AcceptingArtifactPublication"/>; a non-Committed result is
        /// collected only after the Worker has physically stopped and the
        /// Service advances to <see cref="PlanCommitCollected"/>. A fatal or
        /// Poisoned Service never yields a result. Collection is linearized with
        /// the Poison transition on the shared process-state gate, which also
        /// serializes concurrent collectors, so exactly one caller succeeds. On
        /// success the internal operation and result references are cleared
        /// before the next state is published, so the request slot is empty
        /// before the transition becomes observable and a second collection
        /// returns false with a null result.
        /// </summary>
        internal bool TryCollectPlanCommit(out NvencRunPublicationPlanCommitExecutionResult result)
        {
            result = null;

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned
                    || Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.PlanCommitCompleted)
                {
                    return false;
                }

                NvencRunPublicationPlanCommitExecutionResult collected = _planCommitResult;
                if (collected == null)
                {
                    return false;
                }

                // A non-Committed terminal requires the Worker to have
                // physically stopped before the slot is cleared; a Committed
                // result keeps the Worker parked so no stop is required.
                if (collected.Status != NvencRunPublicationPlanCommitStatus.Committed
                    && !IsStopped)
                {
                    return false;
                }

                _planCommitOperation = null;
                _planCommitResult = null;

                int next = collected.Status == NvencRunPublicationPlanCommitStatus.Committed
                    ? (int)NvencRunPublicationServiceState.AcceptingArtifactPublication
                    : (int)NvencRunPublicationServiceState.PlanCommitCollected;
                Volatile.Write(ref _state, next);

                result = collected;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Non-waiting, exclusive submission of exactly one valid artifact
        /// publication operation. A null operation throws
        /// <see cref="ArgumentNullException"/>. A foreign process state, an
        /// invalid operation, or any second submission is rejected with no side
        /// effect and without contacting the publisher. Acceptance is
        /// linearized with the Poison transition on the shared process-state
        /// gate, and the Worker is notified only after the slot is claimed.
        /// </summary>
        internal bool TrySubmitArtifactPublication(NvencRunArtifactPublicationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.AcceptingArtifactPublication
                    || !operation.IsValid
                    || !operation.IsBoundToProcessState(_processState))
                {
                    return false;
                }

                _artifactPublicationOperation = operation;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.ArtifactPublicationQueued);
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            Notify();
            return true;
        }

        /// <summary>
        /// Non-waiting, at-most-once collection of the exact artifact
        /// publication Attempt Result. A Published result is collected while the
        /// Worker stays parked and the Service advances to
        /// <see cref="NvencRunPublicationServiceState.AcceptingCaptureIndexCommit"/>;
        /// a Failed result is collected only after the Worker has physically
        /// stopped and the Service advances to
        /// <see cref="NvencRunPublicationServiceState.ArtifactPublicationCollected"/>.
        /// A fatal or Poisoned Service never yields a result. Collection is
        /// linearized with the Poison transition on the shared process-state
        /// gate, so exactly one caller succeeds. On success the internal
        /// operation and result are cleared before the next state is published.
        /// </summary>
        internal bool TryCollectArtifactPublication(
            out NvencRunArtifactPublicationAttemptResult result)
        {
            result = default;

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned
                    || Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.ArtifactPublicationCompleted)
                {
                    return false;
                }

                NvencRunArtifactPublicationAttemptResult collected = _artifactPublicationResult;
                if (collected.IsNone)
                {
                    return false;
                }

                // A Failed terminal requires the Worker to have physically
                // stopped before the slot is cleared; a Published result keeps
                // the Worker parked for the Capture Index phase so no stop is
                // required.
                if (collected.Status != NvencRunArtifactPublicationStatus.Published
                    && !IsStopped)
                {
                    return false;
                }

                _artifactPublicationOperation = null;
                _artifactPublicationResult = default;

                int next = collected.Status == NvencRunArtifactPublicationStatus.Published
                    ? (int)NvencRunPublicationServiceState.AcceptingCaptureIndexCommit
                    : (int)NvencRunPublicationServiceState.ArtifactPublicationCollected;
                Volatile.Write(ref _state, next);

                result = collected;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Non-waiting, exclusive submission of exactly one valid capture index
        /// commit operation. A null operation throws
        /// <see cref="ArgumentNullException"/>. A foreign process state, an
        /// invalid operation, or any second submission is rejected with no side
        /// effect and without contacting the committer. Acceptance is linearized
        /// with the Poison transition on the shared process-state gate, and the
        /// Worker is notified only after the slot is claimed.
        /// </summary>
        internal bool TrySubmitCaptureIndexCommit(NvencRunCaptureIndexCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.AcceptingCaptureIndexCommit
                    || !operation.IsValid
                    || !operation.IsBoundToProcessState(_processState))
                {
                    return false;
                }

                _captureIndexCommitOperation = operation;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureIndexCommitQueued);
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            Notify();
            return true;
        }

        /// <summary>
        /// Non-waiting, at-most-once collection of the exact capture index
        /// commit Attempt Result. A Committed result is collected while the
        /// Worker stays parked and the Service advances to
        /// <see cref="NvencRunPublicationServiceState.AcceptingCaptureComplete"/>;
        /// a Failed result is collected only after the Worker has physically
        /// stopped and the Service advances to
        /// <see cref="NvencRunPublicationServiceState.CaptureIndexCommitCollected"/>.
        /// A fatal or Poisoned Service never yields a result. Collection is
        /// linearized with the Poison transition on the shared process-state
        /// gate, so exactly one caller succeeds. On success the internal
        /// operation and result are cleared before the next state is published.
        /// </summary>
        internal bool TryCollectCaptureIndexCommit(
            out NvencRunCaptureIndexCommitAttemptResult result)
        {
            result = default;

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned
                    || Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.CaptureIndexCommitCompleted)
                {
                    return false;
                }

                NvencRunCaptureIndexCommitAttemptResult collected = _captureIndexCommitResult;
                if (collected.IsNone)
                {
                    return false;
                }

                if (collected.Status != NvencRunCaptureIndexCommitStatus.Committed
                    && !IsStopped)
                {
                    return false;
                }

                _captureIndexCommitOperation = null;
                _captureIndexCommitResult = default;

                int next = collected.Status == NvencRunCaptureIndexCommitStatus.Committed
                    ? (int)NvencRunPublicationServiceState.AcceptingCaptureComplete
                    : (int)NvencRunPublicationServiceState.CaptureIndexCommitCollected;
                Volatile.Write(ref _state, next);

                result = collected;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Exception-safe exact-issuance re-verification for the Run
        /// Coordinator: true only when the attempt result is valid and was
        /// issued by the exact retained Capture Index Commit Execution
        /// Coordinator for the exact supplied operation. ReferenceEquals only;
        /// it deliberately compares against the supplied operation rather than
        /// the Service slot, which is cleared before the terminal is published.
        /// </summary>
        internal bool IsCaptureIndexCommitAttemptIssued(
            NvencRunCaptureIndexCommitAttemptResult attempt,
            NvencRunCaptureIndexCommitOperation operation)
        {
            try
            {
                return !attempt.IsNone
                    && attempt.IsValid
                    && operation != null
                    && ReferenceEquals(attempt.Committer, _captureIndexCommitCoordinator.Committer)
                    && ReferenceEquals(attempt.Operation, operation);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Non-waiting, exclusive submission of exactly one valid
        /// CaptureComplete operation. A null operation throws
        /// <see cref="ArgumentNullException"/>. A foreign process state, an
        /// invalid operation, or any second submission is rejected with no side
        /// effect and without contacting the completer. Acceptance is
        /// linearized with the Poison transition on the shared process-state
        /// gate, and the Worker is notified only after the slot is claimed.
        /// </summary>
        internal bool TrySubmitCaptureComplete(NvencRunCaptureCompleteOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned
                    || Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.AcceptingCaptureComplete
                    || _captureCompleteOperation != null
                    || !operation.IsValid
                    || !operation.IsBoundToProcessState(_processState))
                {
                    return false;
                }

                _captureCompleteOperation = operation;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureCompleteQueued);
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            Notify();
            return true;
        }

        /// <summary>
        /// Non-waiting, at-most-once collection of the exact CaptureComplete
        /// Attempt Result. CaptureComplete is the final phase, so both a
        /// Completed and a Failed result are collected only after the Worker has
        /// physically stopped, and the Service advances to
        /// <see cref="NvencRunPublicationServiceState.CaptureCompleteCollected"/>.
        /// A fatal or Poisoned Service never yields a result. Collection is
        /// linearized with the Poison transition on the shared process-state
        /// gate, so exactly one caller succeeds. On success the internal
        /// operation and result are cleared before the next state is published.
        /// </summary>
        internal bool TryCollectCaptureComplete(
            out NvencRunCaptureCompleteAttemptResult result)
        {
            result = default;

            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            try
            {
                if (_processState.IsPoisoned
                    || Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.CaptureCompleteCompleted)
                {
                    return false;
                }

                NvencRunCaptureCompleteAttemptResult collected = _captureCompleteResult;
                if (collected.IsNone)
                {
                    return false;
                }

                // The final phase always stops the Worker, so the slot is
                // cleared only once it has physically exited.
                if (!IsStopped)
                {
                    return false;
                }

                _captureCompleteOperation = null;
                _captureCompleteResult = default;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureCompleteCollected);

                result = collected;
                return true;
            }
            finally
            {
                _processState.EndSubmitStep();
            }
        }

        /// <summary>
        /// Exception-safe exact-issuance re-verification for the Run
        /// Coordinator: true only when the attempt result is valid and was
        /// issued by the exact retained CaptureComplete Execution Coordinator
        /// for the exact supplied operation. ReferenceEquals only; it
        /// deliberately compares against the supplied operation rather than the
        /// Service slot, which is cleared before the terminal is published.
        /// </summary>
        internal bool IsCaptureCompleteAttemptIssued(
            NvencRunCaptureCompleteAttemptResult attempt,
            NvencRunCaptureCompleteOperation operation)
        {
            try
            {
                return !attempt.IsNone
                    && attempt.IsValid
                    && operation != null
                    && ReferenceEquals(attempt.Completer, _captureCompleteCoordinator.Completer)
                    && ReferenceEquals(attempt.Operation, operation);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Exception-safe exact-issuance re-verification for the Run
        /// Coordinator: true only when the attempt result is valid and was
        /// issued by the exact retained Artifact Publication Execution
        /// Coordinator for the exact supplied operation. ReferenceEquals only;
        /// it deliberately compares against the supplied operation rather than
        /// the Service slot, which is cleared before the terminal is published.
        /// </summary>
        internal bool IsArtifactPublicationAttemptIssued(
            NvencRunArtifactPublicationAttemptResult attempt,
            NvencRunArtifactPublicationOperation operation)
        {
            try
            {
                return !attempt.IsNone
                    && attempt.IsValid
                    && operation != null
                    && ReferenceEquals(attempt.Publisher, _artifactPublicationCoordinator.Publisher)
                    && ReferenceEquals(attempt.Operation, operation);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Non-waiting, one-time normal stop of a never-submitted Service: the
        /// <see cref="AcceptingPlanCommit"/> state advances to
        /// <see cref="StoppedWithoutRequest"/> without poisoning the process,
        /// linearized with submission on the shared process-state gate. The
        /// Worker is then notified and exits, so an unused Service for an
        /// Incomplete Run releases its Worker and wait handle while the process
        /// stays Draining. A Service that already accepted a submission, is
        /// already stopped, or observes a Poison is not changed and returns
        /// false.
        /// </summary>
        internal bool TryStopWithoutRequest()
        {
            if (!_processState.TryBeginSubmitStep())
            {
                return false;
            }

            bool stopped;
            try
            {
                if (_processState.IsPoisoned)
                {
                    return false;
                }

                stopped = Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencRunPublicationServiceState.StoppedWithoutRequest,
                    (int)NvencRunPublicationServiceState.AcceptingPlanCommit)
                    == (int)NvencRunPublicationServiceState.AcceptingPlanCommit;
            }
            finally
            {
                _processState.EndSubmitStep();
            }

            if (stopped)
            {
                Notify();
            }

            return stopped;
        }

        /// <summary>
        /// Non-throwing diagnostic for the first fatal exception, if any.
        /// Returns false when the Worker stopped from an external Poison
        /// without a fatal failure.
        /// </summary>
        internal bool TryGetFailure(out Exception failure)
        {
            failure = Volatile.Read(ref _fatalFailure);
            return failure != null;
        }

        /// <summary>
        /// Non-waiting physical stop evidence: true only once the Worker thread
        /// has physically exited (or after disposal), false while the Worker is
        /// running.
        /// </summary>
        internal bool IsStopped
        {
            get
            {
                if (Volatile.Read(ref _lifecycleState) == StateDisposed)
                {
                    return true;
                }

                Thread worker = Volatile.Read(ref _workerThread);
                return worker != null && !worker.IsAlive;
            }
        }

        /// <summary>
        /// Coalescing, non-waiting notification that observable state changed.
        /// The caller never blocks; only the Worker waits. Production
        /// correctness never depends on the Worker being notified more than
        /// once.
        /// </summary>
        internal void Notify()
        {
            if (Volatile.Read(ref _lifecycleState) == StateDisposed)
            {
                throw new ObjectDisposedException(nameof(NvencRunPublicationService));
            }

            _signal.Set();
        }

        /// <summary>
        /// Instance-local, best-effort completion notification raised whenever
        /// the Worker physically stops. Production correctness never depends on
        /// subscribers; tests subscribe before submission and use a
        /// ManualResetEventSlim watchdog, never a sleep or short negative wait.
        /// </summary>
        internal event Action Settled
        {
            add
            {
                Action snapshot;
                Action updated;
                do
                {
                    snapshot = Volatile.Read(ref _settled);
                    updated = snapshot + value;
                }
                while (Interlocked.CompareExchange(ref _settled, updated, snapshot) != snapshot);
            }
            remove
            {
                Action snapshot;
                Action updated;
                do
                {
                    snapshot = Volatile.Read(ref _settled);
                    updated = snapshot - value;
                }
                while (Interlocked.CompareExchange(ref _settled, updated, snapshot) != snapshot);
            }
        }

        /// <summary>
        /// Releases the owned wait primitive. Allowed only after the Worker has
        /// physically stopped, rejected with <see cref="InvalidOperationException"/>
        /// while the Worker is running, and never force-stops a running Worker.
        /// Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Volatile.Read(ref _lifecycleState) == StateDisposed)
            {
                return;
            }

            Thread worker = Volatile.Read(ref _workerThread);
            if (worker == null || worker.IsAlive)
            {
                throw new InvalidOperationException(
                    "The Publication Worker thread has not physically stopped; dispose is allowed only after the worker thread has exited.");
            }

            if (Interlocked.CompareExchange(ref _lifecycleState, StateDisposed, StateRunning) == StateRunning)
            {
                _signal.Dispose();
            }
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    // Park until a notification arrives. The auto-reset event
                    // consumes exactly one notification per wait, so a signaled
                    // event never causes a busy spin and an early notification
                    // is simply re-parked.
                    _signal.WaitOne();

                    // Fast-path Poison check: an external Poison never contacts
                    // a coordinator.
                    if (_processState.IsPoisoned)
                    {
                        EnterFailedWithoutResult();
                        return;
                    }

                    int state = Volatile.Read(ref _state);

                    if (IsParkedState(state))
                    {
                        // An early or spurious notification with no queued
                        // request: re-park instead of terminating, so a later
                        // submission still converges. The post-phase parked
                        // states are included, so a stray notification delivered
                        // while one phase executes can never stop the Worker
                        // that the next phase needs.
                        continue;
                    }

                    if (state == (int)NvencRunPublicationServiceState.PlanCommitQueued)
                    {
                        // Run the Plan Commit Execution Coordinator exactly
                        // once. A Committed result re-parks the same thread for
                        // the Artifact phase; any other result stops it.
                        if (!TryExecutePlanCommit())
                        {
                            return;
                        }

                        continue;
                    }

                    if (state == (int)NvencRunPublicationServiceState.ArtifactPublicationQueued)
                    {
                        // Run the Artifact Publication Execution Coordinator
                        // exactly once. A Published result re-parks the same
                        // thread for the Capture Index phase; a Failed result
                        // stops it.
                        if (!TryExecuteArtifactPublication())
                        {
                            return;
                        }

                        continue;
                    }

                    if (state == (int)NvencRunPublicationServiceState.CaptureIndexCommitQueued)
                    {
                        // Run the Capture Index Commit Execution Coordinator
                        // exactly once. A Committed result re-parks the same
                        // thread for the later CaptureComplete phase; a Failed
                        // result stops it.
                        if (!TryExecuteCaptureIndexCommit())
                        {
                            return;
                        }

                        continue;
                    }

                    if (state == (int)NvencRunPublicationServiceState.CaptureCompleteQueued)
                    {
                        // Run the CaptureComplete Execution Coordinator exactly
                        // once. CaptureComplete is the final phase, so the
                        // Worker always stops afterwards.
                        TryExecuteCaptureComplete();
                        return;
                    }

                    // Any other state (Poisoned, StoppedWithoutRequest, a
                    // collected terminal, or a mid-phase spurious read) has no
                    // further work: stop.
                    return;
                }
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
            }
            finally
            {
                RaiseSettled();
            }
        }

        /// <summary>
        /// Claims the Queued plan operation, runs the Plan Commit Execution
        /// Coordinator exactly once outside any gate, and publishes the verified
        /// result as <see cref="PlanCommitCompleted"/>. Returns true for a
        /// Committed result so the Worker re-parks, and false otherwise so the
        /// Worker stops. A Poison that linearizes during execution fails closed
        /// without publishing a normal terminal.
        /// </summary>
        private bool TryExecutePlanCommit()
        {
            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return false;
                }

                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.PlanCommitQueued)
                {
                    return false;
                }

                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.PlanCommitExecuting);
            }
            finally
            {
                _processState.EndSettlement();
            }

            NvencRunPublicationPlanCommitExecutionResult result;
            try
            {
                result = _planCommitCoordinator.Execute(_planCommitOperation);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }

            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return false;
                }

                if (result == null
                    || !result.IsValid
                    || !ReferenceEquals(result.IssuedBy, _planCommitCoordinator)
                    || !ReferenceEquals(result.Attempt.Operation, _planCommitOperation))
                {
                    throw new InvalidOperationException(
                        "The Publication Plan commit Execution Coordinator returned a null, foreign, or corrupt result.");
                }

                _planCommitResult = result;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.PlanCommitCompleted);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }
            finally
            {
                _processState.EndSettlement();
            }

            // Committed keeps the Worker parked for the Artifact phase; any
            // other status stops it.
            return result.Status == NvencRunPublicationPlanCommitStatus.Committed;
        }

        /// <summary>
        /// Claims the Queued artifact operation, runs the Artifact Publication
        /// Execution Coordinator exactly once outside any gate, and publishes
        /// the verified result as <see cref="ArtifactPublicationCompleted"/>.
        /// Returns true for a Published result so the Worker re-parks for the
        /// Capture Index phase, and false otherwise so the Worker stops. A
        /// Poison that linearizes during execution fails closed without
        /// publishing a normal terminal.
        /// </summary>
        private bool TryExecuteArtifactPublication()
        {
            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return false;
                }

                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.ArtifactPublicationQueued)
                {
                    return false;
                }

                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.ArtifactPublicationExecuting);
            }
            finally
            {
                _processState.EndSettlement();
            }

            NvencRunArtifactPublicationAttemptResult result;
            try
            {
                result = _artifactPublicationCoordinator.Execute(_artifactPublicationOperation);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }

            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return false;
                }

                if (result.IsNone
                    || !result.IsValid
                    || !ReferenceEquals(result.Publisher, _artifactPublicationCoordinator.Publisher)
                    || !ReferenceEquals(result.Operation, _artifactPublicationOperation))
                {
                    throw new InvalidOperationException(
                        "The Artifact Publication Execution Coordinator returned a null, foreign, default, or corrupt result.");
                }

                _artifactPublicationResult = result;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.ArtifactPublicationCompleted);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }
            finally
            {
                _processState.EndSettlement();
            }

            // Published keeps the Worker parked for the Capture Index phase;
            // Failed stops it.
            return result.Status == NvencRunArtifactPublicationStatus.Published;
        }

        /// <summary>
        /// Claims the Queued capture index operation, runs the Capture Index
        /// Commit Execution Coordinator exactly once outside any gate, and
        /// publishes the verified result as
        /// <see cref="NvencRunPublicationServiceState.CaptureIndexCommitCompleted"/>.
        /// Returns true for a Committed result so the Worker re-parks for the
        /// later CaptureComplete phase, and false otherwise so the Worker stops.
        /// A Poison that linearizes during execution fails closed without
        /// publishing a normal terminal.
        /// </summary>
        private bool TryExecuteCaptureIndexCommit()
        {
            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return false;
                }

                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.CaptureIndexCommitQueued)
                {
                    return false;
                }

                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureIndexCommitExecuting);
            }
            finally
            {
                _processState.EndSettlement();
            }

            NvencRunCaptureIndexCommitAttemptResult result;
            try
            {
                result = _captureIndexCommitCoordinator.Execute(_captureIndexCommitOperation);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }

            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return false;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return false;
                }

                if (result.IsNone
                    || !result.IsValid
                    || !ReferenceEquals(result.Committer, _captureIndexCommitCoordinator.Committer)
                    || !ReferenceEquals(result.Operation, _captureIndexCommitOperation))
                {
                    throw new InvalidOperationException(
                        "The Capture Index Commit Execution Coordinator returned a null, foreign, default, or corrupt result.");
                }

                _captureIndexCommitResult = result;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureIndexCommitCompleted);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return false;
            }
            finally
            {
                _processState.EndSettlement();
            }

            // Committed keeps the Worker parked for the later CaptureComplete
            // phase; Failed stops it.
            return result.Status == NvencRunCaptureIndexCommitStatus.Committed;
        }

        /// <summary>
        /// Claims the Queued CaptureComplete operation, runs the CaptureComplete
        /// Execution Coordinator exactly once outside any gate, and publishes
        /// the verified result as
        /// <see cref="NvencRunPublicationServiceState.CaptureCompleteCompleted"/>.
        /// CaptureComplete is the final phase, so the Worker always stops
        /// afterwards and this terminal is never a parked state. A Poison that
        /// linearizes during execution fails closed without publishing a normal
        /// terminal.
        /// </summary>
        private void TryExecuteCaptureComplete()
        {
            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return;
                }

                if (Volatile.Read(ref _state) != (int)NvencRunPublicationServiceState.CaptureCompleteQueued)
                {
                    return;
                }

                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureCompleteExecuting);
            }
            finally
            {
                _processState.EndSettlement();
            }

            NvencRunCaptureCompleteAttemptResult result;
            try
            {
                result = _captureCompleteCoordinator.Execute(_captureCompleteOperation);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return;
            }

            if (!_processState.TryBeginSettlement())
            {
                EnterFailedWithoutResult();
                return;
            }

            try
            {
                if (_processState.IsPoisoned)
                {
                    EnterFailedWithoutResult();
                    return;
                }

                if (result.IsNone
                    || !result.IsValid
                    || !ReferenceEquals(result.Completer, _captureCompleteCoordinator.Completer)
                    || !ReferenceEquals(result.Operation, _captureCompleteOperation))
                {
                    throw new InvalidOperationException(
                        "The CaptureComplete Execution Coordinator returned a null, foreign, default, or corrupt result.");
                }

                _captureCompleteResult = result;
                Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.CaptureCompleteCompleted);
            }
            catch (Exception ex)
            {
                RecordFatalFailure(ex);
                _processState.TryPoison();
                return;
            }
            finally
            {
                _processState.EndSettlement();
            }
        }

        /// <summary>
        /// The states in which a notification carries no queued work and the
        /// Worker must re-park rather than terminate: the accepting states and
        /// the post-phase parked terminals whose Worker a later phase still
        /// needs. The CaptureComplete terminal is deliberately absent: it is
        /// the final phase and its Worker always stops.
        /// </summary>
        private static bool IsParkedState(int state)
        {
            return state == (int)NvencRunPublicationServiceState.AcceptingPlanCommit
                || state == (int)NvencRunPublicationServiceState.PlanCommitCompleted
                || state == (int)NvencRunPublicationServiceState.AcceptingArtifactPublication
                || state == (int)NvencRunPublicationServiceState.ArtifactPublicationCompleted
                || state == (int)NvencRunPublicationServiceState.AcceptingCaptureIndexCommit
                || state == (int)NvencRunPublicationServiceState.CaptureIndexCommitCompleted
                || state == (int)NvencRunPublicationServiceState.AcceptingCaptureComplete;
        }

        private void EnterFailedWithoutResult()
        {
            Volatile.Write(ref _state, (int)NvencRunPublicationServiceState.Poisoned);
        }

        private void RecordFatalFailure(Exception failure)
        {
            Interlocked.CompareExchange(ref _fatalFailure, failure, null);
        }

        private void RaiseSettled()
        {
            Action handler = Volatile.Read(ref _settled);
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch
            {
                // An observer failure must never become the Worker's fatal
                // failure; observation is best-effort.
            }
        }
    }
}
