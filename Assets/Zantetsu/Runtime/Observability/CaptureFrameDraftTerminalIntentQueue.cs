using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, multi-producer single-consumer queue of capture frame
    /// draft terminal intents. Producers enqueue concurrently; dequeue and all
    /// lifecycle and mirror-sync operations are main-thread only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The intent buffer is sized to <c>2 * MaxInFlightDraftCount</c> and the
    /// draft mirror to <c>MaxDraftCountPerRun</c>; both arrays are allocated
    /// exactly once. No <see cref="System.Collections.Generic.List{T}"/>,
    /// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>, LINQ,
    /// enumerator, or mid-processing reallocation is used, and the normal
    /// enqueue and dequeue paths perform no managed allocation, logging, or
    /// string generation.
    /// </para>
    /// <para>
    /// Producers never read the main-thread-only draft registry directly.
    /// Draft existence, request identity, and terminal state are mirrored into
    /// a fixed-length array by the main thread through
    /// <see cref="RegisterPendingDraft"/> and <see cref="MarkDraftTerminal"/>.
    /// A private gate constructed once serializes every queue mutation.
    /// </para>
    /// <para>
    /// Only <see cref="TerminalIntentEnqueueStatus.Accepted"/> transfers the
    /// logical ownership of the intent and, for a stage intent, its staging
    /// entry to this queue. A dequeued stage entry is owned by the caller and
    /// is never disposed by this queue; the queue disposes only stage entries
    /// that are still held when it is disposed.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftTerminalIntentQueue : IDisposable
    {
        private struct DraftMirrorEntry
        {
            public bool Occupied;
            public CaptureFrameRequest Request;
            public bool IsTerminal;
            public int AcceptedCount;
            public int OutstandingCount;
        }

        private readonly object _gate = new object();
        private readonly CaptureFrameDraftRegistry _draftRegistry;
        private readonly long _runTestRunId;
        private readonly CaptureFrameDraftTerminalIntent[] _buffer;
        private readonly DraftMirrorEntry[] _mirror;
        private readonly int _capacity;

        private CaptureFrameDraftTerminalIntentQueueState _state;
        private int _count;
        private int _head;
        private int _tail;
        private int _runAcceptedIntentCount;
        private int _runProcessedIntentCount;
        private int _queueOwnedPrivateBufferCount;
        private bool _disposeStarted;
        private bool _disposed;

        internal CaptureFrameDraftTerminalIntentQueue(
            CaptureFrameDraftRegistry draftRegistry,
            CaptureTraceProfile profile)
        {
            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (draftRegistry.Run.CaptureProfileId != profile.CaptureProfileId)
            {
                throw new ArgumentException("Registry run capture profile ID must match the profile.", nameof(profile));
            }

            int capacity;
            try
            {
                capacity = checked(2 * profile.MaxInFlightDraftCount);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), profile.MaxInFlightDraftCount, "Queue capacity overflowed.");
            }

            _draftRegistry = draftRegistry;
            _runTestRunId = draftRegistry.Run.TestRunId;
            _capacity = capacity;
            _buffer = new CaptureFrameDraftTerminalIntent[capacity];
            _mirror = new DraftMirrorEntry[profile.MaxDraftCountPerRun];
            _state = CaptureFrameDraftTerminalIntentQueueState.Accepting;
            _count = 0;
            _head = 0;
            _tail = 0;
            _runAcceptedIntentCount = 0;
            _runProcessedIntentCount = 0;
            _queueOwnedPrivateBufferCount = 0;
            _disposed = false;
        }

        public CaptureFrameDraftTerminalIntentQueueState State
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _state;
                }
            }
        }

        public bool IsCreated => !_disposeStarted;

        /// <summary>
        /// Returns the draft registry this queue mirrors against. Exposed for the
        /// terminal coordinator's dependency identity validation only.
        /// </summary>
        internal CaptureFrameDraftRegistry Registry => _draftRegistry;

        public int Capacity
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _capacity;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _count;
                }
            }
        }

        public int RunAcceptedIntentCount
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _runAcceptedIntentCount;
                }
            }
        }

        public int RunProcessedIntentCount
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _runProcessedIntentCount;
                }
            }
        }

        public int QueueOwnedPrivateBufferCount
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _queueOwnedPrivateBufferCount;
                }
            }
        }

        /// <summary>
        /// Main-thread only. Mirrors a registry-admitted draft into the queue so
        /// producers can match intents without reading the registry.
        /// </summary>
        internal void RegisterPendingDraft(CaptureFrameDraft draft)
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (draft == null)
                {
                    throw new ArgumentNullException(nameof(draft));
                }

                if (!_draftRegistry.TryGet(draft.Request, out CaptureFrameDraft registeredDraft, out CaptureFrameDraftStatus status))
                {
                    throw new InvalidOperationException("The draft is not registered in the registry.");
                }

                if (status != CaptureFrameDraftStatus.Pending)
                {
                    throw new InvalidOperationException("The draft is not pending.");
                }

                if (!ReferenceEquals(registeredDraft, draft))
                {
                    throw new InvalidOperationException("The supplied draft is not the registered draft instance.");
                }

                int index = FindMirrorIndex(draft.CaptureFrameId);
                if (index >= 0)
                {
                    if (_mirror[index].Request.IdenticalTo(draft.Request))
                    {
                        throw new ArgumentException("The draft is already registered in the queue mirror.", nameof(draft));
                    }

                    throw new InvalidOperationException("A different draft with the same capture frame ID is already registered in the queue mirror.");
                }

                int free = FindFreeMirrorSlot();
                if (free < 0)
                {
                    throw new InvalidOperationException("No free mirror slot is available.");
                }

                _mirror[free].Occupied = true;
                _mirror[free].Request = draft.Request;
                _mirror[free].IsTerminal = false;
                _mirror[free].AcceptedCount = 0;
                _mirror[free].OutstandingCount = 0;
            }
        }

        /// <summary>
        /// Main-thread only. Marks the mirror entry for a request as terminal
        /// once the registry has already staged or dropped it.
        /// </summary>
        internal void MarkDraftTerminal(in CaptureFrameRequest request)
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (!request.IsValid)
                {
                    throw new ArgumentException("Request must be valid.", nameof(request));
                }

                if (!_draftRegistry.TryGet(request, out _, out CaptureFrameDraftStatus status))
                {
                    throw new InvalidOperationException("The draft is not registered in the registry.");
                }

                if (status != CaptureFrameDraftStatus.Staged && status != CaptureFrameDraftStatus.Dropped)
                {
                    throw new InvalidOperationException("The draft is not terminal.");
                }

                int index = FindMirrorIndex(request.TraceContext.CaptureFrameId);
                if (index < 0)
                {
                    throw new InvalidOperationException("The draft is not registered in the queue mirror.");
                }

                _mirror[index].IsTerminal = true;
            }
        }

        /// <summary>
        /// Enqueues one terminal intent. Callable concurrently from multiple
        /// producer threads. Only <see cref="TerminalIntentEnqueueStatus.Accepted"/>
        /// mutates the queue and transfers the intent's (and stage entry's)
        /// logical ownership.
        /// </summary>
        internal TerminalIntentEnqueueStatus EnqueueTerminalIntent(
            CaptureFrameDraftTerminalIntent intent)
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                // 1. InvalidIntent: no queue, counter, or ownership change.
                if (!TryValidateIntent(intent, out int mirrorIndex))
                {
                    return TerminalIntentEnqueueStatus.InvalidIntent;
                }

                // 2. RunNotAccepting.
                if (_state == CaptureFrameDraftTerminalIntentQueueState.Closed)
                {
                    return TerminalIntentEnqueueStatus.RunNotAccepting;
                }

                // 3. DraftAlreadyTerminal.
                if (_mirror[mirrorIndex].IsTerminal)
                {
                    return TerminalIntentEnqueueStatus.DraftAlreadyTerminal;
                }

                // 4. IntentLimitExceeded.
                if (_mirror[mirrorIndex].AcceptedCount >= 2 || _mirror[mirrorIndex].OutstandingCount >= 2)
                {
                    return TerminalIntentEnqueueStatus.IntentLimitExceeded;
                }

                // 5. Backpressured.
                if (_count >= _capacity)
                {
                    return TerminalIntentEnqueueStatus.Backpressured;
                }

                // 6. Accepted.
                _buffer[_tail] = intent;
                _tail = (_tail + 1) % _capacity;
                _count++;
                _mirror[mirrorIndex].AcceptedCount++;
                _mirror[mirrorIndex].OutstandingCount++;
                _runAcceptedIntentCount++;
                if (intent.IsStage)
                {
                    _queueOwnedPrivateBufferCount++;
                }

                return TerminalIntentEnqueueStatus.Accepted;
            }
        }

        /// <summary>
        /// Main-thread only. Dequeues one FIFO intent. Ownership of the intent
        /// and, for a stage intent, its entry transfers to the caller.
        /// </summary>
        internal bool TryDequeue(out CaptureFrameDraftTerminalIntent intent)
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (_count == 0)
                {
                    intent = null;
                    return false;
                }

                intent = _buffer[_head];
                _buffer[_head] = null;
                _head = (_head + 1) % _capacity;
                _count--;

                int mirrorIndex = FindMirrorIndex(intent.Request.TraceContext.CaptureFrameId);
                if (mirrorIndex >= 0)
                {
                    _mirror[mirrorIndex].OutstandingCount--;
                }

                _runProcessedIntentCount++;
                if (intent.IsStage)
                {
                    _queueOwnedPrivateBufferCount--;
                }

                return true;
            }
        }

        /// <summary>
        /// Main-thread only. Moves the queue from <c>Accepting</c> to
        /// <c>ProducerDraining</c>.
        /// </summary>
        internal void BeginProducerDrain()
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (_state != CaptureFrameDraftTerminalIntentQueueState.Accepting)
                {
                    throw new InvalidOperationException("The queue is not in the Accepting state.");
                }

                _state = CaptureFrameDraftTerminalIntentQueueState.ProducerDraining;
            }
        }

        /// <summary>
        /// Main-thread only. Moves the queue from <c>ProducerDraining</c> to
        /// <c>Closed</c> after producers have joined.
        /// </summary>
        internal void CloseAfterProducerJoin()
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (_state != CaptureFrameDraftTerminalIntentQueueState.ProducerDraining)
                {
                    throw new InvalidOperationException("The queue is not in the ProducerDraining state.");
                }

                _state = CaptureFrameDraftTerminalIntentQueueState.Closed;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Close acceptance and mark the queue as no longer usable. Once
            // this flag is set every normal API fails closed and only
            // re-Dispose remains available for the retry of a failed cleanup.
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposeStarted = true;
                _state = CaptureFrameDraftTerminalIntentQueueState.Closed;
            }

            Exception failure = null;

            for (int i = 0; i < _buffer.Length; i++)
            {
                CaptureFrameDraftTerminalIntent intent = _buffer[i];
                if (intent == null)
                {
                    continue;
                }

                if (intent.IsStage && intent.StagingEntry != null)
                {
                    try
                    {
                        intent.StagingEntry.Dispose();
                        _buffer[i] = null;
                    }
                    catch (Exception ex)
                    {
                        failure = failure == null
                            ? ex
                            : new AggregateException(failure, ex);
                    }
                }
                else
                {
                    _buffer[i] = null;
                }
            }

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            _disposed = true;
        }

        private bool TryValidateIntent(CaptureFrameDraftTerminalIntent intent, out int mirrorIndex)
        {
            mirrorIndex = -1;

            if (intent == null)
            {
                return false;
            }

            CaptureFrameRequest request = intent.Request;
            if (!request.IsValid)
            {
                return false;
            }

            long testRunId = request.TraceContext.TestRunId;
            if (testRunId <= 0)
            {
                return false;
            }

            long captureFrameId = request.TraceContext.CaptureFrameId;
            if (captureFrameId <= 0)
            {
                return false;
            }

            if (testRunId != _runTestRunId)
            {
                return false;
            }

            int index = FindMirrorIndex(captureFrameId);
            if (index < 0)
            {
                return false;
            }

            if (!_mirror[index].Request.IdenticalTo(request))
            {
                return false;
            }

            if (intent.StagingEntry != null)
            {
                // Stage intent.
                if (intent.DropReason != CaptureFrameDropReason.None)
                {
                    return false;
                }

                if (!intent.StagingEntry.IsCreated)
                {
                    return false;
                }

                if (intent.StagingEntry.TestRunId != testRunId)
                {
                    return false;
                }

                if (intent.StagingEntry.CaptureFrameId != captureFrameId)
                {
                    return false;
                }
            }
            else
            {
                // Drop intent.
                if (intent.DropReason != CaptureFrameDropReason.PngEncodeFailed
                    && intent.DropReason != CaptureFrameDropReason.PngStagingStoreFull
                    && intent.DropReason != CaptureFrameDropReason.CaptureCancelled)
                {
                    return false;
                }
            }

            mirrorIndex = index;
            return true;
        }

        private int FindMirrorIndex(long captureFrameId)
        {
            for (int i = 0; i < _mirror.Length; i++)
            {
                if (_mirror[i].Occupied && _mirror[i].Request.TraceContext.CaptureFrameId == captureFrameId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindFreeMirrorSlot()
        {
            for (int i = 0; i < _mirror.Length; i++)
            {
                if (!_mirror[i].Occupied)
                {
                    return i;
                }
            }

            return -1;
        }

        private void ThrowIfDisposed()
        {
            if (_disposeStarted)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
