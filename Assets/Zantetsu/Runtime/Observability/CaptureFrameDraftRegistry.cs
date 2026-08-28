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
    }
}
