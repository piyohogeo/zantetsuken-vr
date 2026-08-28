using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, append-only registry for live capture frame drafts. It
    /// reserves admission capacity before a capture frame ID is ever issued,
    /// then commits or cancels the reservation. Only admission, registration,
    /// and lookup are supported here; terminal transitions and trace generation
    /// are separate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry store is sized to <c>MaxDraftCountPerRun</c> and is strictly
    /// append-only; entries are never removed, cleared, or reordered. The
    /// pending slot pool is sized to <c>MaxInFlightDraftCount</c> and is
    /// reusable. Reservation, generation, and entry mapping are tracked in
    /// fixed-length arrays allocated exactly once at construction. No
    /// <c>List</c>, <c>Dictionary</c>, LINQ, enumerator, or mid-processing
    /// array reallocation is used.
    /// </para>
    /// <para>
    /// This type is for the main thread only and is not thread-safe. It owns
    /// and disposes nothing: the run, profile, and every committed draft are
    /// caller-owned. It holds no ID sequence, factory, logger, recorder, queue,
    /// PNG, or render texture lease, and performs no ID issuance, trace
    /// recording, file I/O, Unity static API access, logging, or queue work.
    /// It does not implement <see cref="IDisposable"/> and provides no
    /// <c>Clear</c> or entry removal.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftRegistry
    {
        private enum PendingSlotState
        {
            Free = 0,
            Reserved = 1,
            Occupied = 2,
        }

        private struct Entry
        {
            public CaptureFrameDraft Draft;
            public CaptureFrameDraftStatus Status;
            public CaptureFrameDropReason DropReason;
            public DraftDropTraceEmissionState EmissionState;
        }

        private readonly Guid _ownerId;
        private readonly CaptureDraftRunContext _run;
        private readonly Entry[] _entries;
        private readonly PendingSlotState[] _slotState;
        private readonly long[] _slotGeneration;
        private readonly int[] _slotEntryIndex;
        private int _entryCount;
        private int _pendingCount;
        private int _reservationCount;
        private ForcedDropFrameIdSet _issuedForcedDropFrameIdSet;

        internal CaptureFrameDraftRegistry(
            CaptureDraftRunContext run,
            CaptureTraceProfile profile)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (run.CaptureProfileId != profile.CaptureProfileId)
            {
                throw new ArgumentException("Run capture profile ID must match the profile capture profile ID.", nameof(profile));
            }

            _ownerId = Guid.NewGuid();
            _run = run;
            _entries = new Entry[profile.MaxDraftCountPerRun];
            _slotState = new PendingSlotState[profile.MaxInFlightDraftCount];
            _slotGeneration = new long[profile.MaxInFlightDraftCount];
            _slotEntryIndex = new int[profile.MaxInFlightDraftCount];
            for (int i = 0; i < _slotEntryIndex.Length; i++)
            {
                _slotEntryIndex[i] = -1;
            }
        }

        internal CaptureDraftRunContext Run => _run;

        internal int EntryCapacity => _entries.Length;

        internal int PendingCapacity => _slotState.Length;

        internal int EntryCount => _entryCount;

        internal int PendingCount => _pendingCount;

        internal int ReservationCount => _reservationCount;

        /// <summary>
        /// Returns the forced-drop frame ID set issued by this registry, or
        /// <c>null</c> before any set has been issued.
        /// </summary>
        internal ForcedDropFrameIdSet IssuedForcedDropFrameIdSet => _issuedForcedDropFrameIdSet;

        internal bool TryReserve(
            out CaptureFrameDraftReservation reservation,
            out CaptureFrameAdmissionRejectKind rejectKind)
        {
            if (_entryCount + _reservationCount >= _entries.Length)
            {
                reservation = default;
                rejectKind = CaptureFrameAdmissionRejectKind.RunEntryLimit;
                return false;
            }

            if (_pendingCount + _reservationCount >= _slotState.Length)
            {
                reservation = default;
                rejectKind = CaptureFrameAdmissionRejectKind.PendingLimit;
                return false;
            }

            for (int i = 0; i < _slotState.Length; i++)
            {
                if (_slotState[i] == PendingSlotState.Free)
                {
                    // A slot whose generation reached long.MaxValue cannot be
                    // reused safely: incrementing it would overflow to a
                    // negative value (making the reservation invalid) and
                    // wrapping back to 1 would collide with stale reservations.
                    if (_slotGeneration[i] == long.MaxValue)
                    {
                        continue;
                    }

                    _slotGeneration[i]++;
                    _slotState[i] = PendingSlotState.Reserved;
                    _reservationCount++;
                    reservation = new CaptureFrameDraftReservation(_ownerId, _slotGeneration[i], i);
                    rejectKind = CaptureFrameAdmissionRejectKind.None;
                    return true;
                }
            }

            // The capacity checks above guarantee at least one free slot exists
            // here, so reaching this point means every free slot's generation
            // is exhausted. Do not mutate registry state.
            throw new OverflowException("All pending slot generations have been exhausted.");
        }

        internal void Commit(
            in CaptureFrameDraftReservation reservation,
            CaptureFrameDraft draft)
        {
            int slot = reservation.PendingSlotIndex;
            if (reservation.OwnerId != _ownerId
                || slot < 0
                || slot >= _slotState.Length
                || reservation.Generation != _slotGeneration[slot]
                || _slotState[slot] != PendingSlotState.Reserved)
            {
                throw new InvalidOperationException("Reservation is invalid, stale, or already consumed.");
            }

            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            if (!ReferenceEquals(draft.Run, _run))
            {
                throw new InvalidOperationException("Draft run does not match the registry run.");
            }

            if (!draft.Request.IsValid)
            {
                throw new ArgumentException("Draft request must be valid.", nameof(draft));
            }

            if (draft.TestRunId != _run.TestRunId)
            {
                throw new InvalidOperationException("Draft test run ID does not match the registry run.");
            }

            if (draft.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(draft), draft.CaptureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (_entryCount > 0 && _entries[_entryCount - 1].Draft.CaptureFrameId >= draft.CaptureFrameId)
            {
                throw new InvalidOperationException("Capture frame IDs must be committed in strictly ascending order without duplicates.");
            }

            Entry entry;
            entry.Draft = draft;
            entry.Status = CaptureFrameDraftStatus.Pending;
            entry.DropReason = CaptureFrameDropReason.None;
            entry.EmissionState = DraftDropTraceEmissionState.None;
            _entries[_entryCount] = entry;
            int entryIndex = _entryCount;
            _entryCount++;

            _slotEntryIndex[slot] = entryIndex;
            _slotState[slot] = PendingSlotState.Occupied;
            _pendingCount++;
            _reservationCount--;
        }

        internal void Cancel(in CaptureFrameDraftReservation reservation)
        {
            int slot = reservation.PendingSlotIndex;
            if (reservation.OwnerId != _ownerId
                || slot < 0
                || slot >= _slotState.Length
                || reservation.Generation != _slotGeneration[slot]
                || _slotState[slot] != PendingSlotState.Reserved)
            {
                throw new InvalidOperationException("Reservation is invalid, stale, or already consumed.");
            }

            _slotState[slot] = PendingSlotState.Free;
            _slotEntryIndex[slot] = -1;
            _reservationCount--;
        }

        internal bool TryGet(
            in CaptureFrameRequest request,
            out CaptureFrameDraft draft,
            out CaptureFrameDraftStatus status)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            long testRunId = request.TraceContext.TestRunId;
            long captureFrameId = request.TraceContext.CaptureFrameId;

            for (int i = 0; i < _entryCount; i++)
            {
                CaptureFrameDraft entryDraft = _entries[i].Draft;
                if (entryDraft.CaptureFrameId == captureFrameId && entryDraft.TestRunId == testRunId)
                {
                    if (!entryDraft.HasIdenticalRequest(request))
                    {
                        throw new InvalidOperationException("A matching capture frame ID has a different request.");
                    }

                    draft = entryDraft;
                    status = _entries[i].Status;
                    return true;
                }
            }

            draft = null;
            status = default;
            return false;
        }

        /// <summary>
        /// Registers a caller-owned staging entry into the staging store and,
        /// only after that registration succeeds, moves the matching pending
        /// draft to <see cref="CaptureFrameDraftStatus.Staged"/> and releases
        /// its pending slot in one terminal operation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a main-thread primitive reserved for the future single
        /// terminal coordinator. Validation runs in a fixed order and touches
        /// neither the staging store nor the registry until every check passes.
        /// The linearization point for staging publication and pending slot
        /// release is the registry state update performed only after
        /// <see cref="CaptureFramePngStagingStore.TryRegister"/> returns
        /// <c>true</c>; after that point only fixed-array and primitive field
        /// writes remain, so no rollback point exists.
        /// </para>
        /// <para>
        /// A <c>false</c> result is a temporary staging capacity shortage, not
        /// a drop: the draft stays <see cref="CaptureFrameDraftStatus.Pending"/>,
        /// its pending slot is not released, and no registry counter, drop
        /// reason, emission state, or trace is changed. The staging entry stays
        /// caller-owned. The future terminal coordinator performs the drop
        /// separately via
        /// <see cref="MarkDropped(in CaptureFrameRequest, CaptureFrameDropReason)"/>
        /// with <see cref="CaptureFrameDropReason.PngStagingStoreFull"/> after
        /// it has released the entry.
        /// </para>
        /// <para>
        /// After a successful return the staging store owns the entry; the
        /// caller must not dispose or roll it back, and
        /// <see cref="CaptureFramePngStagingStore.RollbackRegistration"/> must
        /// never be called after staging has been published. This operation
        /// never disposes the staging entry and never touches a logger, a
        /// lease, a readback, or a PNG queue.
        /// </para>
        /// </remarks>
        internal bool TryMarkStaged(
            in CaptureFrameRequest request,
            CaptureFramePngStagingStore stagingStore,
            CaptureFramePngStagingEntry stagingEntry)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (stagingStore == null)
            {
                throw new ArgumentNullException(nameof(stagingStore));
            }

            if (stagingEntry == null)
            {
                throw new ArgumentNullException(nameof(stagingEntry));
            }

            if (!stagingStore.IsCreated)
            {
                throw new ObjectDisposedException(stagingStore.GetType().Name);
            }

            if (!stagingEntry.IsCreated)
            {
                throw new ObjectDisposedException(stagingEntry.GetType().Name);
            }

            if (!ReferenceEquals(stagingStore.Run, _run))
            {
                throw new ArgumentException("Staging store run must match the registry run.", nameof(stagingStore));
            }

            if (stagingEntry.TestRunId != request.TraceContext.TestRunId)
            {
                throw new ArgumentException("Staging entry test run ID must match the request.", nameof(stagingEntry));
            }

            if (stagingEntry.CaptureFrameId != request.TraceContext.CaptureFrameId)
            {
                throw new ArgumentException("Staging entry capture frame ID must match the request.", nameof(stagingEntry));
            }

            int entryIndex = FindEntryIndex(request);
            if (entryIndex < 0)
            {
                throw new InvalidOperationException("The draft is not registered in the registry.");
            }

            if (_entries[entryIndex].Status != CaptureFrameDraftStatus.Pending)
            {
                throw new InvalidOperationException("The draft is not pending.");
            }

            if (_entries[entryIndex].DropReason != CaptureFrameDropReason.None)
            {
                throw new InvalidOperationException("The draft drop reason is not None.");
            }

            if (_entries[entryIndex].EmissionState != DraftDropTraceEmissionState.None)
            {
                throw new InvalidOperationException("The draft drop trace emission state is not None.");
            }

            int slotIndex = FindSingleOccupiedSlot(entryIndex);

            if (_pendingCount <= 0)
            {
                throw new InvalidOperationException("No pending slots remain.");
            }

            if (!stagingStore.TryRegister(stagingEntry))
            {
                return false;
            }

            // Staging publication succeeded; only primitive state writes follow.
            _entries[entryIndex].Status = CaptureFrameDraftStatus.Staged;
            _slotState[slotIndex] = PendingSlotState.Free;
            _slotEntryIndex[slotIndex] = -1;
            _pendingCount--;
            return true;
        }

        /// <summary>
        /// Atomically moves a pending draft to the terminal
        /// <see cref="CaptureFrameDraftStatus.Dropped"/> state with one of the
        /// three normal draft drop reasons, and schedules its one-time drop
        /// trace for later consumption.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only <see cref="CaptureFrameDropReason.PngEncodeFailed"/>,
        /// <see cref="CaptureFrameDropReason.PngStagingStoreFull"/>, and
        /// <see cref="CaptureFrameDropReason.CaptureCancelled"/> are accepted;
        /// the legacy, admission, and freeze terminal reasons are rejected with
        /// <see cref="ArgumentOutOfRangeException"/>.
        /// </para>
        /// <para>
        /// Validation runs in a fixed order and mutates nothing until every
        /// check passes: the request must be valid, the reason must be a normal
        /// drop reason, the request must be fully registered (a matching ID
        /// with a different request fails closed), the entry must still be
        /// <see cref="CaptureFrameDraftStatus.Pending"/>, and exactly one
        /// occupied pending slot must reference the entry. On success the
        /// status, drop reason, and emission state are set exactly once, the
        /// pending slot is freed, its entry index is reset, and the pending
        /// count is decremented. The entry itself and its draft reference stay
        /// in the append-only store and the entry count is unchanged.
        /// </para>
        /// <para>
        /// This operation performs no rollback, PNG destruction, lease return,
        /// or trace generation. A future terminal coordinator must call it only
        /// after it has rolled back every shared resource (PNG encode/queue
        /// work, render target lease, and readback result) for the frame, and
        /// must never call it for a Staged or already-Dropped entry.
        /// </para>
        /// </remarks>
        internal void MarkDropped(
            in CaptureFrameRequest request,
            CaptureFrameDropReason reason)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (reason != CaptureFrameDropReason.PngEncodeFailed
                && reason != CaptureFrameDropReason.PngStagingStoreFull
                && reason != CaptureFrameDropReason.CaptureCancelled)
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Reason must be PngEncodeFailed, PngStagingStoreFull, or CaptureCancelled.");
            }

            int entryIndex = FindEntryIndex(request);
            if (entryIndex < 0)
            {
                throw new InvalidOperationException("The draft is not registered in the registry.");
            }

            if (_entries[entryIndex].Status != CaptureFrameDraftStatus.Pending)
            {
                throw new InvalidOperationException("The draft is not pending.");
            }

            int slotIndex = FindSingleOccupiedSlot(entryIndex);

            // Every validation succeeded: perform the terminal transition once.
            _entries[entryIndex].Status = CaptureFrameDraftStatus.Dropped;
            _entries[entryIndex].DropReason = reason;
            _entries[entryIndex].EmissionState = DraftDropTraceEmissionState.Pending;
            _slotState[slotIndex] = PendingSlotState.Free;
            _slotEntryIndex[slotIndex] = -1;
            _pendingCount--;
        }

        /// <summary>
        /// Consumes the one-time drop trace emission for a dropped draft,
        /// moving its emission state from <c>Pending</c> to <c>Attempted</c>
        /// irreversibly before any trace is enqueued.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only an entry that is <see cref="CaptureFrameDraftStatus.Dropped"/>
        /// with one of the three normal drop reasons and
        /// <see cref="DraftDropTraceEmissionState.Pending"/> is consumed. Any
        /// other state (nonexistent ID, Pending, Staged, the freeze reason, or
        /// an already <c>Attempted</c> emission) returns <c>false</c> with a
        /// default payload and leaves state unchanged. A second call therefore
        /// always fails; there is deliberately no rollback from
        /// <c>Attempted</c> back to <c>Pending</c>.
        /// </para>
        /// </remarks>
        internal bool TryConsumeDropTrace(
            long captureFrameId,
            out CaptureFrameDraftDropTracePayload payload)
        {
            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            payload = default;

            int entryIndex = FindEntryIndexById(captureFrameId);
            if (entryIndex < 0)
            {
                return false;
            }

            Entry entry = _entries[entryIndex];
            if (entry.Status != CaptureFrameDraftStatus.Dropped
                || (entry.DropReason != CaptureFrameDropReason.PngEncodeFailed
                    && entry.DropReason != CaptureFrameDropReason.PngStagingStoreFull
                    && entry.DropReason != CaptureFrameDropReason.CaptureCancelled)
                || entry.EmissionState != DraftDropTraceEmissionState.Pending)
            {
                return false;
            }

            // Build the payload first, then advance the state irreversibly.
            payload = new CaptureFrameDraftDropTracePayload(entry.Draft.TraceContext, entry.DropReason);
            _entries[entryIndex].EmissionState = DraftDropTraceEmissionState.Attempted;
            return true;
        }

        /// <summary>
        /// Main-thread only. After the ownership snapshot is issued, moves every
        /// remaining pending draft to <see cref="CaptureFrameDraftStatus.Dropped"/>
        /// with <see cref="CaptureFrameDropReason.FreezeDrainTimeout"/> in one
        /// all-or-none operation, and issues the immutable
        /// <see cref="ForcedDropFrameIdSet"/> of their capture frame IDs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Dependency and snapshot identity are verified before any registry
        /// state is read or changed. Once a canonical set has been issued, this
        /// returns the same instance without rescanning or re-terminating.
        /// </para>
        /// <para>
        /// The full entry store and pending slot pool are scanned for invariant
        /// violations before any mutation; any violation throws
        /// <see cref="InvalidOperationException"/> leaving the registry, slots,
        /// counters, and traces completely unchanged.
        /// </para>
        /// <para>
        /// Reason 9 never schedules a normal drop trace:
        /// <see cref="MarkDropped"/> continues to accept only reasons 6-8,
        /// <see cref="TryConsumeDropTrace"/> keeps returning <c>false</c> for
        /// reason 9, and reason 9 entries keep
        /// <see cref="DraftDropTraceEmissionState.None"/>. This method never
        /// touches a logger or trace observer; freeze terminal event generation
        /// is the future freeze terminal builder's responsibility.
        /// </para>
        /// </remarks>
        internal ForcedDropFrameIdSet ForceDropPendingForFreeze(
            CaptureFrameDraftTerminalIntentQueue intentQueue,
            TerminalIntentOwnershipSnapshot ownershipSnapshot)
        {
            if (intentQueue == null)
            {
                throw new ArgumentNullException(nameof(intentQueue));
            }

            if (ownershipSnapshot == null)
            {
                throw new ArgumentNullException(nameof(ownershipSnapshot));
            }

            if (!ReferenceEquals(intentQueue.Registry, this))
            {
                throw new ArgumentException("Intent queue registry must match this registry.", nameof(intentQueue));
            }

            if (!ReferenceEquals(ownershipSnapshot.IssuedBy, intentQueue))
            {
                throw new ArgumentException("Ownership snapshot must be issued by the intent queue.", nameof(ownershipSnapshot));
            }

            if (!ReferenceEquals(ownershipSnapshot, intentQueue.IssuedOwnershipSnapshot))
            {
                throw new ArgumentException("Ownership snapshot must be the queue's issued ownership snapshot.", nameof(ownershipSnapshot));
            }

            if (!ownershipSnapshot.IsValid)
            {
                throw new ArgumentException("Ownership snapshot must be valid.", nameof(ownershipSnapshot));
            }

            if (ownershipSnapshot.TestRunId != _run.TestRunId)
            {
                throw new ArgumentException("Ownership snapshot test run ID must match the registry run.", nameof(ownershipSnapshot));
            }

            if (_issuedForcedDropFrameIdSet != null)
            {
                return _issuedForcedDropFrameIdSet;
            }

            ValidateFreezePreconditions(out int pendingCount);

            long[] captureFrameIds = new long[pendingCount];
            int written = 0;
            for (int i = 0; i < _entryCount; i++)
            {
                if (_entries[i].Status == CaptureFrameDraftStatus.Pending)
                {
                    captureFrameIds[written] = _entries[i].Draft.CaptureFrameId;
                    written++;
                }
            }

            ForcedDropFrameIdSet set = new ForcedDropFrameIdSet(this, _run.TestRunId, captureFrameIds);

            // Linearization point: only fixed-array and primitive writes follow.
            for (int i = 0; i < _entryCount; i++)
            {
                if (_entries[i].Status == CaptureFrameDraftStatus.Pending)
                {
                    _entries[i].Status = CaptureFrameDraftStatus.Dropped;
                    _entries[i].DropReason = CaptureFrameDropReason.FreezeDrainTimeout;
                    _entries[i].EmissionState = DraftDropTraceEmissionState.None;
                }
            }

            for (int s = 0; s < _slotState.Length; s++)
            {
                if (_slotState[s] == PendingSlotState.Occupied)
                {
                    _slotState[s] = PendingSlotState.Free;
                    _slotEntryIndex[s] = -1;
                }
            }

            _pendingCount = 0;
            _issuedForcedDropFrameIdSet = set;
            return set;
        }

        private void ValidateFreezePreconditions(out int pendingCount)
        {
            if (_reservationCount != 0)
            {
                throw new InvalidOperationException("Reservation count must be zero before freeze.");
            }

            if (_pendingCount < 0)
            {
                throw new InvalidOperationException("Pending count must not be negative.");
            }

            pendingCount = 0;
            long previousCaptureFrameId = 0;

            for (int i = 0; i < _entryCount; i++)
            {
                Entry entry = _entries[i];
                CaptureFrameDraft draft = entry.Draft;

                if (draft == null)
                {
                    throw new InvalidOperationException("Entry draft must not be null.");
                }

                if (draft.TestRunId != _run.TestRunId)
                {
                    throw new InvalidOperationException("Entry test run ID must match the registry run.");
                }

                long captureFrameId = draft.CaptureFrameId;
                if (captureFrameId <= 0)
                {
                    throw new InvalidOperationException("Entry capture frame ID must be positive.");
                }

                if (i > 0 && captureFrameId <= previousCaptureFrameId)
                {
                    throw new InvalidOperationException("Capture frame IDs must be strictly increasing.");
                }

                previousCaptureFrameId = captureFrameId;

                switch (entry.Status)
                {
                    case CaptureFrameDraftStatus.Pending:
                        if (entry.DropReason != CaptureFrameDropReason.None)
                        {
                            throw new InvalidOperationException("Pending entry must have drop reason None.");
                        }

                        if (entry.EmissionState != DraftDropTraceEmissionState.None)
                        {
                            throw new InvalidOperationException("Pending entry must have emission state None.");
                        }

                        pendingCount++;
                        break;

                    case CaptureFrameDraftStatus.Staged:
                        if (entry.DropReason != CaptureFrameDropReason.None)
                        {
                            throw new InvalidOperationException("Staged entry must have drop reason None.");
                        }

                        if (entry.EmissionState != DraftDropTraceEmissionState.None)
                        {
                            throw new InvalidOperationException("Staged entry must have emission state None.");
                        }

                        break;

                    case CaptureFrameDraftStatus.Dropped:
                        if (entry.DropReason != CaptureFrameDropReason.PngEncodeFailed
                            && entry.DropReason != CaptureFrameDropReason.PngStagingStoreFull
                            && entry.DropReason != CaptureFrameDropReason.CaptureCancelled)
                        {
                            throw new InvalidOperationException("Dropped entry must have a normal drop reason before freeze.");
                        }

                        if (entry.EmissionState != DraftDropTraceEmissionState.Pending
                            && entry.EmissionState != DraftDropTraceEmissionState.Attempted)
                        {
                            throw new InvalidOperationException("Dropped entry must have emission state Pending or Attempted.");
                        }

                        break;

                    default:
                        throw new InvalidOperationException("Entry has an undefined status.");
                }
            }

            if (pendingCount != _pendingCount)
            {
                throw new InvalidOperationException("Scanned pending count does not match PendingCount.");
            }

            if (pendingCount > _slotState.Length)
            {
                throw new InvalidOperationException("Pending count exceeds pending slot capacity.");
            }

            int occupiedCount = 0;
            for (int s = 0; s < _slotState.Length; s++)
            {
                switch (_slotState[s])
                {
                    case PendingSlotState.Free:
                        if (_slotEntryIndex[s] != -1)
                        {
                            throw new InvalidOperationException("Free slot must have entry index -1.");
                        }

                        break;

                    case PendingSlotState.Reserved:
                        throw new InvalidOperationException("A reserved slot remains before freeze.");

                    case PendingSlotState.Occupied:
                    {
                        occupiedCount++;
                        int entryIndex = _slotEntryIndex[s];
                        if (entryIndex < 0 || entryIndex >= _entryCount)
                        {
                            throw new InvalidOperationException("Occupied slot entry index is out of range.");
                        }

                        if (_entries[entryIndex].Status != CaptureFrameDraftStatus.Pending)
                        {
                            throw new InvalidOperationException("Occupied slot does not reference a pending entry.");
                        }

                        break;
                    }

                    default:
                        throw new InvalidOperationException("Slot has an undefined state.");
                }
            }

            if (occupiedCount != _pendingCount)
            {
                throw new InvalidOperationException("Occupied slot count does not match PendingCount.");
            }

            // Each pending entry must be referenced by exactly one occupied slot.
            for (int i = 0; i < _entryCount; i++)
            {
                if (_entries[i].Status == CaptureFrameDraftStatus.Pending)
                {
                    FindSingleOccupiedSlot(i);
                }
            }
        }

        private int FindEntryIndex(in CaptureFrameRequest request)
        {
            long testRunId = request.TraceContext.TestRunId;
            long captureFrameId = request.TraceContext.CaptureFrameId;

            for (int i = 0; i < _entryCount; i++)
            {
                CaptureFrameDraft entryDraft = _entries[i].Draft;
                if (entryDraft.CaptureFrameId == captureFrameId && entryDraft.TestRunId == testRunId)
                {
                    if (!entryDraft.HasIdenticalRequest(request))
                    {
                        throw new InvalidOperationException("A matching capture frame ID has a different request.");
                    }

                    return i;
                }
            }

            return -1;
        }

        private int FindEntryIndexById(long captureFrameId)
        {
            for (int i = 0; i < _entryCount; i++)
            {
                if (_entries[i].Draft.CaptureFrameId == captureFrameId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindSingleOccupiedSlot(int entryIndex)
        {
            int slotIndex = -1;
            for (int i = 0; i < _slotState.Length; i++)
            {
                if (_slotState[i] == PendingSlotState.Occupied && _slotEntryIndex[i] == entryIndex)
                {
                    if (slotIndex >= 0)
                    {
                        throw new InvalidOperationException("Multiple pending slots reference the same entry.");
                    }

                    slotIndex = i;
                }
            }

            if (slotIndex < 0)
            {
                throw new InvalidOperationException("No occupied pending slot references the entry.");
            }

            return slotIndex;
        }
    }
}
